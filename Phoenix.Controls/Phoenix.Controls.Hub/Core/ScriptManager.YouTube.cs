using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: youtube.* platform commands.
    // The 7 fire-and-forget YouTube action proxies (send_chat / set_title /
    // set_description / timeout / ban / create_poll / end_poll) plus the
    // youtube.get_user data round-trip. Same wrapper model as the Twitch set in
    // ScriptManager.Twitch.cs: every action routes through DispatchNamedAction
    // against a "Phoenix: YT …" action-pack wrapper (SB's native YouTube
    // sub-actions can't be invoked directly over the WS API), and the data
    // lookup reuses FetchActionGlobalsAsync (DoAction → poll GetGlobals →
    // phx_req token) with the phx_yt_* global prefix.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        // Maps the phx_yt_* globals from a "Phoenix: YT Get User" fetch onto the
        // user.* result slots. YouTube's identity shape differs from Twitch's
        // (no separate lowercase login; sponsor ≈ sub; channel owner ≈
        // broadcaster), so this is a sibling of ApplyUserGlobals — deliberately
        // NOT a widening of it. Absent globals degrade to ""/"false" the same way.
        private void ApplyYouTubeUserGlobals(Dictionary<string, string> g, string fallbackUser)
        {
            string G(string k) => g.TryGetValue(k, out var v) ? v : "";
            string display = G("phx_yt_display_name");
            _engine.SetLocalResultVar("user.id",             G("phx_yt_id"));
            _engine.SetLocalResultVar("user.display_name",   display.Length > 0 ? display : fallbackUser);
            _engine.SetLocalResultVar("user.profile_image",  G("phx_yt_profile_image"));
            _engine.SetLocalResultVar("user.is_mod",         NormBool(G("phx_yt_is_mod")));
            _engine.SetLocalResultVar("user.is_sub",         NormBool(G("phx_yt_is_sponsor")));
            _engine.SetLocalResultVar("user.is_broadcaster", NormBool(G("phx_yt_is_owner")));
        }

        private void RegisterYouTubeCommands()
        {
            // youtube.send_chat("message")
            // YouTube live chat caps messages at 200 chars (tighter than Twitch's
            // 500). Anything longer is rejected upstream silently and the script
            // never sees the failure. Reject up-front so the streamer sees a
            // clear log instead of debugging a no-op.
            _engine.RegisterCommand("youtube.send_chat", async (args) =>
            {
                string message = _engine.CurrentBoundArgs?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(message)) return null;
                if (message.Length > 200)
                {
                    GlobalLogger.Log($"youtube.send_chat → DROPPED: message length {message.Length} exceeds 200-char YouTube live-chat cap.",
                        "Script", LogLevel.CriticalError);
                    return null;
                }
                DispatchNamedAction("youtube.send_chat", PhxSbActions.YtSendChat, new { message });
                return null;
            });

            // youtube.set_title(title) — updates the live broadcast's title.
            _engine.RegisterCommand("youtube.set_title", async (args) =>
            {
                string title = _engine.CurrentBoundArgs?.GetOrDefault<string>("Title", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(title))
                {
                    GlobalLogger.Log("youtube.set_title: empty title — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("youtube.set_title", PhxSbActions.YtSetTitle, new { title });
                return null;
            });

            // youtube.set_description(description) — updates the broadcast description.
            _engine.RegisterCommand("youtube.set_description", async (args) =>
            {
                string description = _engine.CurrentBoundArgs?.GetOrDefault<string>("Description", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(description))
                {
                    GlobalLogger.Log("youtube.set_description: empty description — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("youtube.set_description", PhxSbActions.YtSetDescription, new { description });
                return null;
            });

            // youtube.timeout(user, seconds)
            // Typed-bind pattern per twitch.timeout: Sec is Optional Default "300".
            // Engine dispatch applies the default and coerces malformed values to 0
            // (rejected below); the positional fallback mirrors that exactly —
            // missing → 300, present-but-unparseable → 0 → rejected.
            _engine.RegisterCommand("youtube.timeout", async (args) =>
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
                    GlobalLogger.Log("youtube.timeout: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                if (sec < 1)
                {
                    GlobalLogger.Log($"youtube.timeout: duration {sec}s must be at least 1 second — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("youtube.timeout", PhxSbActions.YtTimeout, new
                {
                    user,
                    duration = sec.ToString(CultureInfo.InvariantCulture)
                });
                GlobalLogger.Log($"YouTube timeout: {user} for {sec}s", "Script", LogLevel.LogicExecution);
                return null;
            });

            // youtube.ban(user) — permanent (hidden) on the live chat.
            _engine.RegisterCommand("youtube.ban", async (args) =>
            {
                string user = _engine.CurrentBoundArgs?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(user))
                {
                    GlobalLogger.Log("youtube.ban: empty user — skipping.",
                        "Script", LogLevel.Communication);
                    return null;
                }
                DispatchNamedAction("youtube.ban", PhxSbActions.YtBan, new { user });
                GlobalLogger.Log($"YouTube ban: {user}", "Script", LogLevel.LogicExecution);
                return null;
            });

            // youtube.create_poll(title, choices, durationSec)
            // Mirrors twitch.create_poll: raw comma-separated choices pass through;
            // the SB wrapper action's YouTube Create Poll sub-action splits them.
            // DoAction can't return a value, so no poll id surfaces here either.
            _engine.RegisterCommand("youtube.create_poll", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string title   = bound?.GetOrDefault<string>("Title", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string choices = bound?.GetOrDefault<string>("Choices", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                int    dur     = (bound != null && bound.ContainsKey("DurationSec"))
                    ? bound.Get<int>("DurationSec")
                    : (int.TryParse(ArgOrEmpty(args, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d : 60);
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(choices)) return null;
                DispatchNamedAction("youtube.create_poll", PhxSbActions.YtCreatePoll, new
                {
                    title,
                    choices,
                    duration = dur.ToString(CultureInfo.InvariantCulture)
                });
                GlobalLogger.Log($"YouTube Poll: {title}", "Script", LogLevel.Communication);
                return null;
            });

            // youtube.end_poll() — acts on the active poll; SB's YT End Poll
            // sub-action takes no arguments.
            _engine.RegisterCommand("youtube.end_poll", async (args) =>
            {
                DispatchNamedAction("youtube.end_poll", PhxSbActions.YtEndPoll, new { });
                return null;
            });

            // youtube.get_user(username) — fetches via "Phoenix: YT Get User" and
            // writes user.id / user.display_name / user.profile_image / user.is_mod
            // / user.is_sub (sponsor) / user.is_broadcaster (channel owner).
            _engine.RegisterCommand("youtube.get_user", async (args) =>
            {
                string user = StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Username", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                if (string.IsNullOrEmpty(user)) return null;
                var g = await FetchActionGlobalsAsync("youtube.get_user", PhxSbActions.YtGetUser,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g != null) ApplyYouTubeUserGlobals(g, user);
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
