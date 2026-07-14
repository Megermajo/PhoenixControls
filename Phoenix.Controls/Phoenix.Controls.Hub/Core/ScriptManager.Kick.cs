using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: kick.* platform commands.
    // The 11 fire-and-forget Kick action proxies (send_chat / reply / timeout /
    // ban / unban / untimeout / set_title / set_category / delete_message /
    // set_reward_cost / set_reward_enabled) plus the kick.get_user data
    // round-trip. Same wrapper model as the Twitch set in
    // ScriptManager.Twitch.cs: every action routes through DispatchNamedAction
    // against a "Phoenix: Kick …" action-pack wrapper (SB's native Kick
    // sub-actions can't be invoked directly over the WS API), and the data
    // lookup reuses FetchActionGlobalsAsync (DoAction → poll GetGlobals →
    // phx_req token) with the phx_kick_* global prefix.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        // Maps the phx_kick_* globals from a "Phoenix: Kick Get User" fetch onto
        // the user.* result slots. Sibling of ApplyUserGlobals (deliberately not
        // a widening — Kick has no vip / created / game / title surface over SB).
        // Absent globals degrade to ""/"false" the same way.
        private void ApplyKickUserGlobals(Dictionary<string, string> g, string fallbackUser)
        {
            string G(string k) => g.TryGetValue(k, out var v) ? v : "";
            string login   = G("phx_kick_login");
            string display = G("phx_kick_display_name");
            _engine.SetLocalResultVar("user.id",            G("phx_kick_id"));
            _engine.SetLocalResultVar("user.login",         login.Length   > 0 ? login   : fallbackUser);
            _engine.SetLocalResultVar("user.display_name",  display.Length > 0 ? display : fallbackUser);
            _engine.SetLocalResultVar("user.profile_image", G("phx_kick_profile_image"));
            _engine.SetLocalResultVar("user.is_mod",        NormBool(G("phx_kick_is_mod")));
            _engine.SetLocalResultVar("user.is_sub",        NormBool(G("phx_kick_is_sub")));
        }

        // User-only Kick proxies (ban / unban / untimeout) — same shape as
        // RegisterUserOnlyTwitchProxy: empty user → log + return (no dispatch);
        // DispatchNamedAction is LOUD on disconnect / missing wrapper action.
        private void RegisterUserOnlyKickProxy(string command, string phxAction)
        {
            _engine.RegisterCommand(command, async (args) =>
            {
                string user = _engine.CurrentBoundArgs?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(user))
                {
                    GlobalLogger.Log($"{command}: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction(command, phxAction, new { user });
                return null;
            });
        }

        /// <summary>
        /// The Kick chat-send core — kick.send_chat and the unified chat.send
        /// dispatcher both route here. Kick chat messages are capped at 500
        /// chars (same as Twitch); anything longer is rejected upstream
        /// silently, so drop with a clear log up-front.
        /// </summary>
        internal void SendKickChatCore(string message, string commandLabel)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (message.Length > 500)
            {
                GlobalLogger.Log($"{commandLabel} → DROPPED: message length {message.Length} exceeds 500-char Kick chat cap.",
                    "Script", LogLevel.CriticalError);
                return;
            }
            DispatchNamedAction(commandLabel, PhxSbActions.KickSendChat, new { message });
        }

        private void RegisterKickCommands()
        {
            // kick.send_chat("message")
            // Kick chat messages are capped at 500 chars (same as Twitch).
            // Anything longer is rejected upstream silently and the script never
            // sees the failure. Reject up-front so the streamer sees a clear log
            // instead of debugging a no-op.
            _engine.RegisterCommand("kick.send_chat", async (args) =>
            {
                string message = _engine.CurrentBoundArgs?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                SendKickChatCore(message, "kick.send_chat");
                return null;
            });

            // kick.reply(messageId, message) — threaded reply to a specific chat
            // line (SB's Reply To Message sub-action). Same 500-char body cap as
            // kick.send_chat — a reply is still a chat message.
            _engine.RegisterCommand("kick.reply", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string messageId = bound?.GetOrDefault<string>("MessageId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string message   = bound?.GetOrDefault<string>("Message", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    GlobalLogger.Log("kick.reply: empty messageId — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (string.IsNullOrEmpty(message))
                {
                    GlobalLogger.Log("kick.reply: empty message — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (message.Length > 500)
                {
                    GlobalLogger.Log($"kick.reply → DROPPED: message length {message.Length} exceeds 500-char Kick chat cap.",
                        "Script", LogLevel.CriticalError);
                    return null;
                }
                DispatchNamedAction("kick.reply", PhxSbActions.KickReply, new { messageId, message });
                return null;
            });

            // kick.timeout(user, seconds)
            // Typed-bind pattern per twitch.timeout: Sec is Optional Default "300".
            // Engine dispatch applies the default and coerces malformed values to 0
            // (rejected below); the positional fallback mirrors that exactly —
            // missing → 300, present-but-unparseable → 0 → rejected.
            _engine.RegisterCommand("kick.timeout", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string user = bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int sec;
                if (bound != null && bound.ContainsKey("Sec")) sec = bound.Get<int>("Sec");
                else
                {
                    string secRaw = ArgOrEmpty(args, 1);
                    sec = string.IsNullOrEmpty(secRaw) ? 300
                        : (int.TryParse(secRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0);
                }
                if (string.IsNullOrWhiteSpace(user))
                {
                    GlobalLogger.Log("kick.timeout: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (sec < 1)
                {
                    GlobalLogger.Log($"kick.timeout: duration {sec}s must be at least 1 second — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("kick.timeout", PhxSbActions.KickTimeout, new
                {
                    user,
                    duration = sec.ToString(CultureInfo.InvariantCulture)
                });
                GlobalLogger.Log($"Kick timeout: {user} for {sec}s", "Script", LogLevel.LogicExecution);
                return null;
            });

            // kick.unban / kick.untimeout — user-only moderation proxies. kick.ban
            // is registered separately (below) because its "Phoenix: Kick Ban"
            // action binds a %reason% — unlike YouTube, whose ban API has no reason
            // field, so youtube.ban stays user-only.
            RegisterUserOnlyKickProxy("kick.unban",     PhxSbActions.KickUnban);
            RegisterUserOnlyKickProxy("kick.untimeout", PhxSbActions.KickUntimeout);

            // kick.ban(user, reason?) — mirrors twitch.ban. Reason is Optional
            // Default "" (→ "no reason" fallback prose); the SB "Phoenix: Kick Ban"
            // action binds %user% + %reason%. Same 500-char reason cap as twitch.ban.
            _engine.RegisterCommand("kick.ban", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string user = bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string reason;
                if (bound != null && bound.ContainsKey("Reason")) reason = bound.Get<string>("Reason");
                else reason = args.Length >= 2 ? args[1] : "no reason";
                if (string.IsNullOrEmpty(reason)) reason = "no reason";  // Optional Default "" → fallback prose.
                if (string.IsNullOrWhiteSpace(user))
                {
                    GlobalLogger.Log("kick.ban: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (reason.Length > 500)
                {
                    GlobalLogger.Log($"kick.ban: reason length {reason.Length} exceeds 500-char cap — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("kick.ban", PhxSbActions.KickBan, new { user, reason });
                GlobalLogger.Log($"Kick ban: {user}: {reason}", "Script", LogLevel.LogicExecution);
                return null;
            });

            // kick.set_title(title) — updates the channel's stream title.
            _engine.RegisterCommand("kick.set_title", async (args) =>
            {
                string title = _engine.CurrentBoundArgs?.GetOrDefault<string>("Title", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(title))
                {
                    GlobalLogger.Log("kick.set_title: empty title — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("kick.set_title", PhxSbActions.KickSetTitle, new { title });
                return null;
            });

            // kick.set_category(category) — updates the channel's category by name;
            // the SB wrapper's Set Channel Category sub-action resolves it.
            _engine.RegisterCommand("kick.set_category", async (args) =>
            {
                string category = _engine.CurrentBoundArgs?.GetOrDefault<string>("Category", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(category))
                {
                    GlobalLogger.Log("kick.set_category: empty category — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("kick.set_category", PhxSbActions.KickSetCategory, new { category });
                return null;
            });

            // kick.delete_message(messageId) — single-message moderation.
            _engine.RegisterCommand("kick.delete_message", async (args) =>
            {
                string messageId = _engine.CurrentBoundArgs?.GetOrDefault<string>("MessageId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    GlobalLogger.Log("kick.delete_message: empty messageId — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("kick.delete_message", PhxSbActions.KickDeleteMessage, new { messageId });
                return null;
            });

            // kick.set_reward_cost(rewardId, cost) — mirrors twitch.update_reward_cost.
            _engine.RegisterCommand("kick.set_reward_cost", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string rewardId = bound?.GetOrDefault<string>("RewardId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int    cost     = (bound != null && bound.ContainsKey("Cost"))
                    ? bound.Get<int>("Cost")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 0);
                if (string.IsNullOrEmpty(rewardId)) return null;
                DispatchNamedAction("kick.set_reward_cost", PhxSbActions.KickSetRewardCost, new
                {
                    rewardId,
                    cost = cost.ToString(CultureInfo.InvariantCulture)
                });
                return null;
            });

            // kick.set_reward_enabled(rewardId, true/false) — mirrors
            // twitch.set_reward_enabled: typed Bool bind; the legacy fallback
            // retains the `Equals("true", ...)` semantic.
            _engine.RegisterCommand("kick.set_reward_enabled", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string rewardId = bound?.GetOrDefault<string>("RewardId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                bool   enabled  = (bound != null && bound.ContainsKey("Enabled"))
                    ? bound.Get<bool>("Enabled")
                    : ArgOrEmpty(args, 1).Equals("true", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(rewardId)) return null;
                DispatchNamedAction("kick.set_reward_enabled", PhxSbActions.KickSetRewardEnabled, new
                {
                    rewardId,
                    enabled = enabled ? "true" : "false"
                });
                return null;
            });

            // kick.get_user(username) — fetches via "Phoenix: Kick Get User" and
            // writes user.id / user.login / user.display_name / user.profile_image
            // / user.is_mod / user.is_sub.
            _engine.RegisterCommand("kick.get_user", async (args) =>
            {
                string user = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Username", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrEmpty(user)) return null;
                var g = await FetchActionGlobalsAsync("kick.get_user", PhxSbActions.KickGetUser,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g != null) ApplyKickUserGlobals(g, user);
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
