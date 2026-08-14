using System;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Quotes (databank-backed quote store) — the Hub-side wiring of
    // the quote-store pre-build tool (sibling of Counters / Loyalty / Giveaway).
    // Two responsibilities, mirroring ScriptManager.Counters.cs:
    //
    //   1. Seam injection — QuotesService can't touch the script dispatcher itself
    //      (Hub-side), so RaiseScriptEvent (Quote.OnAdded) is wired at the top of
    //      RegisterQuotesCommands, fired-and-forgotten through
    //      AsyncErrorBoundary.SafeRunAsync so a faulting event handler routes to the
    //      log instead of becoming an unobserved Task.
    //
    //   2. The BUILT-IN chat-command entry (TryHandleQuotesChatCommandAsync),
    //      registered as the Quotes provider in RegisterBuiltInChatProviders (AFTER
    //      Counters). It is a thin wrapper: the parse/gate/format logic lives on
    //      QuotesService (so it is unit-testable without a ScriptManager), and this
    //      wrapper supplies the per-platform reply send. Default-OFF is a total no-op.
    //
    // The five quote.* script commands (add / edit / delete / get / count) were
    // RETIRED in the 2026-08 tool-node cut with the Architect Quote.* wrapper nodes:
    // the Quotes store is the OPEN "Quotes" table, so graphs use the generic db.*
    // family instead. The old names answer through ScriptManager.RetiredCommands
    // shims; the chat commands and the panel are untouched.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterQuotesCommands()
        {
            // ── Seam ─────────────────────────────────────────────────────────
            // Quote.OnAdded script events flow back through the generic-event
            // dispatcher with pre-built vars (event.number / event.text / event.name).
            QuotesService.Instance.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "QuotesService", $"RaiseScriptEvent({phoenixEvent})");
            };
        }

        // ── Built-in chat-command entry (thin wrapper over the testable core) ──
        /// <summary>
        /// The built-in Quotes chat commands (!&lt;add&gt; / !&lt;get&gt; / !&lt;del&gt;).
        /// Delegates the parse/gate/format to QuotesService (unit-testable) and supplies
        /// the per-platform reply send. Returns true when a Quotes command was handled
        /// (so the caller suppresses the author on_chat fan-out). Default-OFF is a total
        /// no-op — QuotesService returns false the instant the tool is disabled.
        /// </summary>
        public Task<bool> TryHandleQuotesChatCommandAsync(ChatMessage msg)
            => QuotesService.Instance.TryHandleChatCommandAsync(
                   msg,
                   reply => { SendQuotesReply(msg, reply); return Task.CompletedTask; });

        // Route the reply on the platform the command arrived on, reusing the exact
        // per-platform chat-send cores chat.send / *.send_chat use — mirrors
        // SendCountersReply, no new send path.
        private void SendQuotesReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "quotes"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "quotes"); break;
                default:                    SendTwitchChatCore(reply, "quotes"); break;
            }
        }
    }
#pragma warning restore CS1998
}
