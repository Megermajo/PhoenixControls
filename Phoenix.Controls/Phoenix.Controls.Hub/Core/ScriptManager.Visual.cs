using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: visual.* command registrations.
    // Lifts the HUD-addressing handlers out of RegisterHubCommands; they lived
    // across three non-contiguous source clusters in the parent partial, and
    // collapsing them here is safe because RegisterCommand stores by name.
    //
    // Every handler here routes through Bus.TriggerVisualQueuedAsync —
    // addressed delivery into the LayerRuntime per-(layerId, widgetId) queue.
    // visual.trigger is an alias kept for the manifesto §3 hand-authored
    // examples; Architect's Visual.Trigger node emits visual.trigger_queued
    // directly.
    //
    // The second pattern this file used to hold — a direct
    // HubHost.HUD.BroadcastAsync of a per-element mutation step
    // (visual.set_text / set_visible / set_property) — is gone as of V4 part C,
    // along with HUDServer.BroadcastAsync itself. compositor.js never had a
    // handler for SET_TEXT / VISUAL_SET_VISIBLE / VISUAL_SET_PROPERTY, so those
    // three commands had shipped inert. Their capability now belongs to
    // overlay.publish (ScriptManager.Overlay.cs) plus a widget-side Var.Live
    // binding, which is the one data path for ambient values. The three NAMES are
    // still registered, as no-op shims in ScriptManager.RetiredCommands.cs, so an
    // unmigrated `.phx` that still calls them stays quiet instead of tripping the
    // engine's CriticalError unknown-command path.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterVisualCommands()
        {
            // visual.trigger(layerId, widgetId, triggerName, key=val, ...) — alias of visual.trigger_queued.
            // The legacy 1-arg "id, data" broadcast form was retired in Phase 2; this alias preserves
            // the spelling so manifesto §3 examples keep parsing while routing to the addressed
            // (layerId, widgetId, triggerName) pipeline. Architect's Visual.Trigger node emits
            // visual.trigger_queued directly; visual.trigger remains for hand-written scripts.
            // EventData via ArgType.KvPairs Variadic; binder produces the dict.
            _engine.RegisterCommand("visual.trigger", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string layerId     = bound?.GetOrDefault<string>("LayerID", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string widgetId    = bound?.GetOrDefault<string>("WidgetID", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string triggerName = bound?.GetOrDefault<string>("TriggerName", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrEmpty(layerId) || string.IsNullOrEmpty(widgetId) || string.IsNullOrEmpty(triggerName))
                    return null;

                IReadOnlyDictionary<string, string> eventDataRO;
                if (bound != null)
                    eventDataRO = bound.GetOrDefault<IReadOnlyDictionary<string, string>>("EventData",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                else
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 3; i < args.Length; i++)
                    {
                        var parts = args[i].Split('=', 2);
                        if (parts.Length == 2) d[parts[0].Trim()] = parts[1].Trim();
                    }
                    eventDataRO = d;
                }
                // Bus.TriggerVisualQueuedAsync takes a Dictionary<string,string>; copy
                // out of the read-only view since the bus signature predates the binder.
                var eventData = new Dictionary<string, string>(eventDataRO, StringComparer.OrdinalIgnoreCase);
                ExpandArgsList(eventData);
                await Bus.Instance.TriggerVisualQueuedAsync(layerId, widgetId, triggerName, eventData);
                return null;
            });

            // visual.trigger_queued(layerId, widgetId, triggerName, key=val, key=val, ...)
            // Fire-and-forget: enqueues into LayerRuntime without blocking script.
            // EventData via ArgType.KvPairs Variadic.
            _engine.RegisterCommand("visual.trigger_queued", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string layerId     = bound?.GetOrDefault<string>("LayerID", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string widgetId    = bound?.GetOrDefault<string>("WidgetID", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string triggerName = bound?.GetOrDefault<string>("TriggerName", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrEmpty(layerId) || string.IsNullOrEmpty(widgetId) || string.IsNullOrEmpty(triggerName))
                    return null;

                Dictionary<string, string> eventData;
                if (bound != null)
                {
                    var ro = bound.GetOrDefault<IReadOnlyDictionary<string, string>>("EventData",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    eventData = new Dictionary<string, string>(ro, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    eventData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 3; i < args.Length; i++)
                    {
                        var parts = args[i].Split('=', 2);
                        if (parts.Length == 2) eventData[parts[0].Trim()] = parts[1].Trim();
                    }
                }
                ExpandArgsList(eventData);
                await Bus.Instance.TriggerVisualQueuedAsync(layerId, widgetId, triggerName, eventData);
                return null;
            });
        }

        // ── Shared (layer, trigger) visual fan-out ──────────────────────────
        // A tool effect carries only (LayerId, TriggerName) — no widget id. The
        // LayerRuntime routes per (LayerId, WidgetId) and the browser matches by
        // widget, so resolve every widget on the layer that owns the trigger and
        // fire each through the SAME fire-and-forget Bus method that
        // visual.trigger_queued uses. Extracted from the Loyalty reward seam
        // (which now delegates here) so every pre-build tool shares one path.
        internal async Task FireVisualTriggerFanOutAsync(
            string layerId, string triggerName,
            Dictionary<string, string>? eventData = null, string source = "VisualFanOut")
        {
            if (string.IsNullOrWhiteSpace(layerId) || string.IsNullOrWhiteSpace(triggerName)) return;
            var layer = LayerRuntime.Instance.Registry.GetLayer(layerId);
            if (layer is null)
            {
                GlobalLogger.Log($"{source} visual: layer '{layerId}' not found — effect skipped.",
                    source, Phoenix.Controls.Shared.Models.LogLevel.VisualEvent);
                return;
            }
            int fired = 0;
            foreach (var w in layer.Widgets)
            {
                if (!WidgetOwnsTrigger(w, triggerName)) continue;
                await Bus.Instance.TriggerVisualQueuedAsync(layerId, w.Id, triggerName, eventData).ConfigureAwait(false);
                fired++;
            }
            if (fired == 0)
                GlobalLogger.Log($"{source} visual: no widget on layer '{layerId}' owns trigger '{triggerName}'.",
                    source, Phoenix.Controls.Shared.Models.LogLevel.VisualEvent);
        }
    }
#pragma warning restore CS1998
}
