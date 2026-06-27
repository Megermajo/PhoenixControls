using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: wait_for_visual + wait_for_event.
    // Latent / async awaiters that suspend script execution until the bus
    // delivers a matching signal. Both surface their outcome through
    // `global._wait_ok` ("true"/"false") so the exporter's Done/Timeout
    // branches can distinguish the two paths without a separate result var.
    //
    // wait_for_visual closes the visual-trigger round-trip end-to-end:
    // Bus registers a pending wait keyed by waitId, LayerRuntime
    // dispatches RUN_TRIGGER carrying that waitId to /hud/<layerId>, the
    // browser's compositor.js echoes VISUAL_COMPLETE back, HUDServer parses
    // it, LayerRuntime resolves the wait. Inactive layers (no live
    // /hud/<id> clients) fast-succeed inside LayerRuntime so a script
    // doesn't deadlock when nobody's looking.
    //
    // wait_for_event hooks the generic Bus event channel and
    // additionally writes `global._wait_payload` so the awaited event's
    // payload is reachable from the script after resume.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterWaitCommands()
        {
            // wait_for_visual(layerId, widgetId, triggerName, timeoutMs?, key=val, key=val, ...)
            // Closes the loop end-to-end: Bus registers a pending wait by waitId,
            // LayerRuntime dispatches RUN_TRIGGER (carrying waitId) to /hud/<layerId>, the browser's
            // compositor.js echoes VISUAL_COMPLETE back, HUDServer parses it, LayerRuntime resolves
            // the wait. Inactive layers (no live /hud/<id> clients) fast-succeed via LayerRuntime.
            // Sweep 16 — EventData via ArgType.KvPairs Variadic; TimeoutMS bound as Int.
            _engine.RegisterCommand("wait_for_visual", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string layerId     = bound?.GetOrDefault<string>("LayerID", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string widgetId    = bound?.GetOrDefault<string>("WidgetID", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string triggerName = bound?.GetOrDefault<string>("TriggerName", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                if (string.IsNullOrEmpty(layerId) || string.IsNullOrEmpty(widgetId) || string.IsNullOrEmpty(triggerName))
                    return null;
                int timeout = (bound != null && bound.ContainsKey("TimeoutMS"))
                    ? bound.Get<int>("TimeoutMS")
                    : (args.Length >= 4 && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 10000);

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
                    for (int i = 4; i < args.Length; i++)
                    {
                        var parts = args[i].Split('=', 2);
                        if (parts.Length == 2) eventData[parts[0].Trim()] = parts[1].Trim();
                    }
                }
                ExpandArgsList(eventData);

                bool completed = await Bus.Instance.TriggerVisualAndWaitAsync(
                    layerId, widgetId, triggerName, eventData, timeout, _engine.ExecutionToken);

                // Surface the wait result so the exporter's Done/Timeout
                // branches can distinguish the two outcomes via _wait_ok.
                await _engine.SetScriptVarAsync("global._wait_ok", completed ? "true" : "false");

                if (!completed)
                    GlobalLogger.Log($"wait_for_visual: timeout waiting for '{layerId}/{widgetId}/{triggerName}'",
                        "ScriptEngine", LogLevel.CriticalError);
                return null;
            });

            _engine.RegisterCommand("wait_for_event", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string eventName = bound?.GetOrDefault<string>("EventName", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int timeout;
                if (bound != null && bound.ContainsKey("TimeoutMS")) timeout = bound.Get<int>("TimeoutMS");
                else
                {
                    string raw = ArgOrEmpty(args, 1);
                    timeout = string.IsNullOrEmpty(raw) ? 10000
                        : (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 10000);
                }
                if (string.IsNullOrEmpty(eventName)) return null;
                var msg = await Bus.Instance.WaitForEventAsync(eventName, timeout, _engine.ExecutionToken);
                bool completed = msg != null;
                await _engine.SetScriptVarAsync("global._wait_ok", completed ? "true" : "false");
                await _engine.SetScriptVarAsync("global._wait_payload", completed ? (msg!.Payload ?? "") : "");
                if (!completed)
                    GlobalLogger.Log($"wait_for_event: timeout waiting for '{eventName}'", "ScriptEngine", LogLevel.CriticalError);
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
