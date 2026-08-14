using System;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: User-Management tool wiring — the seams that give the
    // Hub-state-agnostic UserManagementService its outputs, plus the
    // usermgmt.get_groups script command backing the Architect User.GetGroups node.
    //
    // Sibling of ScriptManager.Scheduling.cs / ScriptManager.CustomCommands.cs:
    // registered from RegisterHubCommands beside the other tool registrars. The
    // welcoming chat tap itself is registered as an observe-only built-in chat
    // provider in RegisterBuiltInChatProviders (ScriptManager.BuiltInChat.cs), and the
    // viewer queue as a SECOND, handling provider one slot after Loyalty.
    //
    // The queue deliberately adds NO queue.* command here: its verbs already exist in
    // the generic Queue.* band (ScriptManager.Queue.cs) because the tool's line IS a
    // named queue. Chat parsing, role gating, cooldown and reply text all live in
    // UserManagementService — this file only routes the reply to the platform the
    // command arrived on.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterUserManagementCommands()
        {
            var svc = UserManagementService.Instance;

            // Greeting goes back out on the platform the first message arrived on —
            // the exact per-platform reply pattern every built-in tool uses.
            svc.SendReply = (msg, text) =>
            {
                SendUserMgmtReply(msg, text);
                return Task.CompletedTask;
            };

            // Auto-shoutout for a personalized welcome row — same SB action the
            // twitch.shoutout node dispatches.
            svc.Shoutout = user =>
            {
                if (!string.IsNullOrWhiteSpace(user))
                    DispatchNamedAction("usermgmt.shoutout", PhxSbActions.Shoutout, new { user });
                return Task.CompletedTask;
            };

            // User.OnFirstMessage script events flow back through the generic-event
            // dispatcher with pre-built vars. The seam is an Action (the service can't
            // await it), so the async dispatch is fired-and-forgotten HERE through
            // AsyncErrorBoundary.SafeRunAsync — faults route to the log, expected
            // shutdown cancellation is swallowed (mirrors ScriptManager.Counters.cs).
            svc.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "UserManagementService", $"RaiseScriptEvent({phoenixEvent})");
            };

            // usermgmt.get_groups(username) — the User.GetGroups node. Writes the four
            // standard-group bools plus one group.<key> bool per EXISTING custom group
            // (a socket for a deleted group resolves to the empty var — falsy). All
            // false while the tool is dormant, so the node stays honest, never stale.
            //
            // Moderator / VIP / Subscriber are the PLATFORM's answer now, not a list the
            // streamer maintains, so this handler takes the AWAITING lookup: a login the
            // role cache has never seen is resolved through a real "Phoenix: Get User"
            // round-trip rather than reported as a non-moderator. A node that answers
            // "not a mod" because nobody happened to have asked yet is the exact failure
            // this pipeline exists to prevent; a cached login still costs one hash lookup
            // and no traffic.
            _engine.RegisterCommand("usermgmt.get_groups", async (args) =>
            {
                string user = StripBareQuotes(
                    _engine.CurrentBoundArgs?.GetOrDefault<string>("Username", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                // Group members are keyed by LOGIN, but the node's documented default is
                // {user.name} — on the chat path that is the lowercased DISPLAY name
                // (Twitch fills ChatMessage.Username with it), which never matches a
                // login-keyed member list for a viewer whose display name is localized.
                // So pair the argument with the triggering chatter's login and let the
                // service resolve login-first-display-fallback, exactly like the
                // twitch.get_user / twitch.check_role handlers re-resolve phx_user_login
                // before consulting the groups. Only while the argument still NAMES that
                // chatter, though: an explicitly named user must never be answered from
                // the ambient chat identity.
                string chatLogin = string.Equals(user.Trim(), _engine.GetExecutionVar("user.name").Trim(),
                                                 StringComparison.OrdinalIgnoreCase)
                    ? _engine.GetExecutionVar("event.user_login").Trim()
                    : "";
                var (mod, vip, sub, regular, customs) = await UserManagementService.Instance
                    .LookupGroupsAsync(chatLogin.Length > 0 ? chatLogin : user, user)
                    .ConfigureAwait(false);
                _engine.SetLocalResultVar(UserGroupKeys.ModeratorKey,  mod     ? "true" : "false");
                _engine.SetLocalResultVar(UserGroupKeys.VipKey,        vip     ? "true" : "false");
                _engine.SetLocalResultVar(UserGroupKeys.SubscriberKey, sub     ? "true" : "false");
                _engine.SetLocalResultVar(UserGroupKeys.RegularKey,    regular ? "true" : "false");
                foreach (var (key, member) in customs)
                    _engine.SetLocalResultVar("group." + key, member ? "true" : "false");
                return null;
            });
        }

        /// <summary>
        /// The viewer-queue built-in chat commands, registered as the "UserQueue"
        /// provider in RegisterBuiltInChatProviders. Everything that decides anything —
        /// the trigger match, the role gate, the per-user cooldown, the mutation and the
        /// reply text — lives in UserManagementService.TryHandleQueueChatAsync; this
        /// wrapper only posts the answer on the platform the command arrived on.
        ///
        /// Returns true when the message WAS a queue command, including the handled-but-
        /// silent cases (role rejection, cooldown), so the caller suppresses the author
        /// on_chat fan-out for it. DEFAULT-OFF IS A TOTAL NO-OP.
        /// </summary>
        public async Task<bool> TryHandleUserQueueChatCommandAsync(ChatMessage msg)
        {
            var result = await UserManagementService.Instance
                .TryHandleQueueChatAsync(msg).ConfigureAwait(false);
            if (!result.Handled) return false;
            if (!string.IsNullOrEmpty(result.Reply)) SendUserMgmtReply(msg, result.Reply);
            return true;
        }

        // Route the greeting on the platform the message arrived on, reusing the exact
        // per-platform chat-send cores — mirrors SendCustomCommandsReply / SendQuotesReply.
        private void SendUserMgmtReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "usermgmt"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "usermgmt"); break;
                default:                    SendTwitchChatCore(reply, "usermgmt"); break;
            }
        }
    }
#pragma warning restore CS1998
}
