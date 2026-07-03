using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: visual.* command registrations.
    // Lifts the 5 HUD-broadcasting handlers (set_text / trigger / set_visible /
    // set_property + the trigger_queued alias) out of RegisterHubCommands.
    // They lived across three non-contiguous source clusters (set_text +
    // trigger up top, trigger_queued sandwiched between wait_for_visual and
    // wait_for_event, set_visible + set_property after the moderation block);
    // collapsing them here is safe because RegisterCommand stores by name.
    //
    // Two patterns of broadcast:
    //   * HubHost.HUD.BroadcastAsync — direct HUDServer broadcast for the
    //     low-level set_text / set_visible / set_property steps.
    //   * Bus.TriggerVisualQueuedAsync — addressed routing into the
    //     LayerRuntime per-(layerId, widgetId) queue used by visual.trigger
    //     and visual.trigger_queued. visual.trigger is an alias kept for the
    //     manifesto §3 hand-authored examples; Architect's Visual.Trigger
    //     node emits visual.trigger_queued directly.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterVisualCommands()
        {
            // visual.set_text(id, value)
            _engine.RegisterCommand("visual.set_text", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string id    = bound?.GetOrDefault<string>("Id", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string value = bound?.GetOrDefault<string>("Value", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (!string.IsNullOrEmpty(id) && HubHost.HUD != null)
                    await HubHost.HUD.BroadcastAsync(new { type = "SET_TEXT", id, value });
                return null;
            });

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

            // ── Visual extras ───────────────────────────────────────────────
            _engine.RegisterCommand("visual.set_visible", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string widget = bound?.GetOrDefault<string>("Widget", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                bool   visible = (bound != null && bound.ContainsKey("Visible"))
                    ? bound.Get<bool>("Visible")
                    : (bool.TryParse(ArgOrEmpty(args, 1), out var b) && b);
                if (string.IsNullOrEmpty(widget) || HubHost.HUD == null) return null;
                await HubHost.HUD.BroadcastAsync(new { type = "VISUAL_SET_VISIBLE", widget, visible });
                return null;
            });
            _engine.RegisterCommand("visual.set_property", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string widget = bound?.GetOrDefault<string>("Widget", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string key    = bound?.GetOrDefault<string>("Key", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string value  = bound?.GetOrDefault<string>("Value", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrEmpty(widget) || string.IsNullOrEmpty(key) || HubHost.HUD == null) return null;
                await HubHost.HUD.BroadcastAsync(new { type = "VISUAL_SET_PROPERTY", widget, key, value });
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
