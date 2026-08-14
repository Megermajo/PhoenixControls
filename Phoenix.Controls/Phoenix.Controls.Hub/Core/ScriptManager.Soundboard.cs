using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Soundboard (chat-triggered clip playback) — the Hub-side wiring of
    // the soundboard tool (sibling of ScriptManager.CustomCommands.cs / .Alerts.cs).
    //
    // Registers NO script command, for the reason ScriptManager.Alerts.cs states about
    // itself: the tool is a no-code shortcut over a capability every graph already has —
    // Visual.Trigger emits visual.trigger_queued with an Args payload, which is exactly
    // what this tool sends — so Architect-first parity holds with zero new engine surface.
    // A hand-built graph doing Visual.Trigger(layer, widget, trigger, Args3=<clip>) lands
    // in the SAME Bus.TriggerVisualQueuedAsync → LayerRuntime → RUN_TRIGGER →
    // Visual.Arg("Args3") path this tool uses; the tool's only privilege is that it
    // resolves the widget set for you via the shared (layer, trigger) fan-out.
    //
    // Two responsibilities, and it is smaller than its siblings because a soundboard has
    // no reply half:
    //
    //   1. Seam injection — the service can't reach the visual fan-out or the generic-event
    //      dispatcher itself, so RegisterSoundboardCommands() wires FireVisual and
    //      RaiseScriptEvent. There is deliberately NO SendSoundboardReply: the tool never
    //      speaks in chat, so a reply seam would be a member no code path can reach.
    //
    //   2. The BUILT-IN chat-command entry (TryHandleSoundboardChatCommandAsync),
    //      registered as the Soundboard provider in RegisterBuiltInChatProviders — the
    //      slot immediately BEFORE CustomCommands, which that file has reserved by name
    //      since the dispatch was written. A thin wrapper: the parse/gate/cooldown/fire
    //      logic lives on SoundboardService (unit-testable without a ScriptManager).
    //      Default-OFF is a total no-op.
    public partial class ScriptManager
    {
        private void RegisterSoundboardCommands()
        {
            var svc = SoundboardService.Instance;

            // ── Seam: the shared (layer, trigger) visual fan-out ─────────────
            // Same path every other tool effect rides (see ScriptManager.Visual.cs). The
            // widget graph receives Args1="PLAY" / Args2=USER / Args3=CLIP; the clip is
            // already validated relative by the service, because a wired Audio.Load.Path
            // must be.
            svc.FireVisual = (layerId, triggerName, eventData) =>
                FireVisualTriggerFanOutAsync(layerId, triggerName, eventData, "SoundboardService");

            // ── Seam: Soundboard.OnPlay script events ────────────────────────
            // The tool's provider returns HandledSuppress, which also skips the author
            // on_chat fan-out — so mapping !airhorn here silently switches off a graph that
            // was handling !airhorn on on_chat. This root is where that graph moves.
            // Fired through the generic-event dispatcher with pre-built vars; the seam is an
            // Action (the service can't await it), so the async dispatch is
            // fire-and-forgotten HERE through AsyncErrorBoundary.SafeRunAsync — faults route
            // to the log, expected shutdown cancellation is swallowed. Verbatim
            // ScriptManager.Ranks.cs / ScriptManager.Counters.cs.
            svc.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "SoundboardService", $"RaiseScriptEvent({phoenixEvent})");
            };
        }

        // ── Built-in chat-command entry (thin wrapper over the testable core) ──
        /// <summary>
        /// The built-in Soundboard provider entry. Delegates the parse/gate/cooldown/fire
        /// to SoundboardService (unit-testable). Returns true when a soundboard row was
        /// recognized, so the caller suppresses the author on_chat fan-out — a sound row
        /// owns its word the same way a counter or quote command owns its own.
        /// Default-OFF is a total no-op — the service returns false the instant the tool
        /// is disabled.
        /// </summary>
        public Task<bool> TryHandleSoundboardChatCommandAsync(ChatMessage msg)
            => SoundboardService.Instance.TryHandleChatCommandAsync(msg);
    }
}
