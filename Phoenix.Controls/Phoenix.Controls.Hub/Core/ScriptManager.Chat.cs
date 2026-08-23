using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: chat.* command registrations.
    // Two awaiters that drain the WS chat ring buffer
    // (chat.wait_for_next blocking, chat.peek_recent non-blocking).
    //
    // The two HUD-overlay broadcasts that used to live here
    // (chat.overlay.push / chat.overlay.clear) lost their bodies in V4 part C:
    // compositor.js never had a handler for their STEP payloads (op
    // "chat_push" / "chat_clear"), so both had shipped inert. An on-overlay
    // chat widget is authored in Visualist and fed by the Overlay Live
    // Channel now — there is no per-widget push command any more. The two NAMES
    // are still registered, as no-op shims in
    // ScriptManager.RetiredCommands.cs, so an unmigrated `.phx` that still
    // calls them stays quiet instead of tripping the engine's CriticalError
    // unknown-command path.
    // Per-node result-var defaults (`global._chat_wait_*`, `global._chat_peek_*`)
    // are honored when the caller passes empty names so two Chat.WaitForNext /
    // Chat.PeekRecent nodes can coexist in one script without clobbering.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterChatCommands()
        {
            // chat.wait_for_next(userFilter, commandFilter, timeoutMs, okVar, userVar, msgVar)
            // Awaits the next chat line that matches the (optional) filters. Per-node
            // result-var names let two Chat.WaitForNext nodes coexist in one script
            // without clobbering each other's output. ChatWaitForNextHandler in the
            // exporter generates the per-node global names.
            _engine.RegisterCommand("chat.wait_for_next", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string userFilter = bound?.GetOrDefault<string>("UserFilter", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string cmdFilter  = bound?.GetOrDefault<string>("CommandFilter", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                int    timeout    = (bound != null && bound.ContainsKey("TimeoutMS"))
                    ? bound.Get<int>("TimeoutMS")
                    : (int.TryParse(ArgOrEmpty(args, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 30000);
                string okVar      = bound?.GetOrDefault<string>("OkVar", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                string userVar    = bound?.GetOrDefault<string>("UserVar", ArgOrEmpty(args, 4)) ?? ArgOrEmpty(args, 4);
                string msgVar     = bound?.GetOrDefault<string>("MsgVar", ArgOrEmpty(args, 5)) ?? ArgOrEmpty(args, 5);
                if (string.IsNullOrWhiteSpace(okVar)) okVar = "global._chat_wait_ok";
                if (string.IsNullOrWhiteSpace(userVar)) userVar = "global._chat_wait_user";
                if (string.IsNullOrWhiteSpace(msgVar)) msgVar = "global._chat_wait_msg";

                var msg = await WS.Instance.WaitForNextChatAsync(
                    string.IsNullOrWhiteSpace(userFilter) ? null : userFilter,
                    string.IsNullOrWhiteSpace(cmdFilter) ? null : cmdFilter,
                    timeout > 0 ? timeout : 30000,
                    _engine.ExecutionToken);

                bool completed = msg != null;
                await _engine.SetScriptVarAsync(okVar, completed ? "true" : "false");
                await _engine.SetScriptVarAsync(userVar, completed ? msg!.Username : "");
                await _engine.SetScriptVarAsync(msgVar, completed ? msg!.Message : "");
                if (!completed)
                    GlobalLogger.Log($"chat.wait_for_next: timeout (user='{userFilter}' cmd='{cmdFilter}')", "ScriptEngine", LogLevel.CriticalError);
                return null;
            });

            // chat.peek_recent(n, usersVar, msgsVar) — non-blocking. Reads from the
            // recent-chat ring buffer without consuming. Writes comma-joined Usernames
            // and Messages lists (oldest-to-newest) into the named globals so Array.*
            // nodes can iterate them. Empty-buffer case writes empty strings.
            _engine.RegisterCommand("chat.peek_recent", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                int    n        = (bound != null && bound.ContainsKey("N"))
                    ? bound.Get<int>("N")
                    : (int.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nn) ? nn : 1);
                string usersVar = bound?.GetOrDefault<string>("UsersVar", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string msgsVar  = bound?.GetOrDefault<string>("MsgsVar", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrWhiteSpace(usersVar)) usersVar = "global._chat_peek_users";
                if (string.IsNullOrWhiteSpace(msgsVar))  msgsVar  = "global._chat_peek_msgs";

                // Shared-chat guest gate: peek_recent hands the ring's content to
                // SCRIPT logic (usernames + full message text into script vars),
                // and the sibling awaiter chat.wait_for_next is gated for exactly
                // that reason — an ungated peek would let a "repeat last chat"
                // script re-broadcast a partner channel's text into every channel
                // of the session (the outbound fan-out cannot be restricted). The
                // ring itself stays unfiltered: it also feeds the Chat panel
                // backlog, which deliberately shows guest lines (tagged).
                bool guestsCanTrigger = ConfigManager.Current?.SharedChatGuestsCanTrigger == true;
                var snapshot = WS.Instance.PeekRecentChat(n)
                    .Where(m => !IsGatedSharedChatGuest(m, guestsCanTrigger))
                    .ToList();
                string users = string.Join(",", snapshot.Select(m => m.Username));
                // Engine substitution doesn't escape commas inside list elements,
                // so per-message commas are preserved as-is. This matches array.make
                // / text.join_list semantics elsewhere in the engine.
                string msgs  = string.Join(",", snapshot.Select(m => m.Message));
                await _engine.SetScriptVarAsync(usersVar, users);
                await _engine.SetScriptVarAsync(msgsVar, msgs);
                return null;
            });

            // chat.message_count() — inline pure-data probe backing the
            // Chat.MessageCount value node. Returns the process-wide monotonic
            // count of inbound (bot-filtered) chat lines the Hub has seen since
            // launch. The exporter emits `chat.message_count()` and the engine
            // round-trips this return value into the node's Count output
            // (mirrors queue.length() / giveaway.default_id()). No result-var
            // base, no flow, no args.
            //
            // Arrived with the Dev merge. Its two former neighbours from that
            // same Dev region — chat.overlay.push / chat.overlay.clear — are
            // deliberately NOT re-registered here: V4 part C retired them into
            // ScriptManager.RetiredCommands.cs (compositor.js never handled
            // their STEP payloads, so both shipped inert). Re-adding the bodies
            // would double-register the names against those shims.
            _engine.RegisterCommand("chat.message_count", async (args) =>
                ChatActivityCounter.Current.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
#pragma warning restore CS1998
}
