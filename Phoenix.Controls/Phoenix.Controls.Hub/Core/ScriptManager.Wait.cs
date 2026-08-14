using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
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
    // ★ V13/§8.1 — BOTH commands write `global._wait_payload`, and they write
    // the SAME var by design (the exporter resolves BOTH `Async.WaitForEvent`
    // → `Payload` and `Async.WaitForVisual` → `Payload` to `{global._wait_payload}`;
    // see ScriptExporter.ResolveOutputFromNode). wait_for_visual's value is the
    // browser's `Visual.Complete → Payload` string, threaded
    // compositor.js → HUDServer → LayerRuntime.NotifyTriggerComplete →
    // WidgetTriggerQueue.NotifyComplete → Bus.ResolveVisualWait →
    // Bus.TriggerVisualAndWaitWithPayloadAsync.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterWaitCommands() => RegisterWaitCommands(_engine);

        /// <summary>
        /// Engine-parameterised form of the wait-command registration. Production calls
        /// the instance overload above with <c>_engine</c>; the test assembly
        /// (<c>InternalsVisibleTo</c>, same seam as <see cref="ArgOrEmpty"/> /
        /// <see cref="ExpandArgsList"/>) registers the REAL handlers onto its own
        /// <see cref="ScriptEngine"/> so the var-write contract below is verified by
        /// running the shipped closures, not by re-describing them.
        /// </summary>
        internal static void RegisterWaitCommands(ScriptEngine engine)
        {
            // wait_for_visual(layerId, widgetId, triggerName, timeoutMs?, key=val, key=val, ...)
            // Closes the loop end-to-end: Bus registers a pending wait by waitId,
            // LayerRuntime dispatches RUN_TRIGGER (carrying waitId) to /hud/<layerId>, the browser's
            // compositor.js echoes VISUAL_COMPLETE back, HUDServer parses it, LayerRuntime resolves
            // the wait. Inactive layers (no live /hud/<id> clients) fast-succeed via LayerRuntime.
            // EventData via ArgType.KvPairs Variadic; TimeoutMS bound as Int.
            engine.RegisterCommand("wait_for_visual", async (args) => {
                var bound = engine.CurrentBoundArgs;
                string layerId     = bound?.GetOrDefault<string>("LayerID", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string widgetId    = bound?.GetOrDefault<string>("WidgetID", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string triggerName = bound?.GetOrDefault<string>("TriggerName", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                // ★ THE STALENESS RULE APPLIES TO THIS GUARD TOO — a malformed call is an
                // OUTCOME, not the absence of one. Returning here without writing (which is
                // what shipped) leaves BOTH vars holding the PREVIOUS wait's values, and
                // because SetScriptVarAsync persists to the SQLite Vars table and the engine
                // pre-loads every {global.*} reference at script start, "previous" can mean a
                // previous Hub SESSION. See the full rationale at the payload write below —
                // this block is that rule's sixth outcome, not an exception to it.
                //
                // Reachable on ordinary input, not a hostile one: the exporter emits
                // wait_for_visual("", "", "onTrigger", 10000) for an Async.WaitForVisual whose
                // LayerID / WidgetID pins are unwired (byte-exactly pinned by
                // V13DesignTimeTests.H1_WaitForVisual_Unwired_Payload_Emits_No_Extra_Arg), and
                // a wired-but-empty var resolves the same way.
                //
                // _wait_ok reads "false" DELIBERATELY: no wait was ever registered, so nothing
                // completed, and the exporter's Timeout branch is the honest route for a call
                // that could not run. _wait_payload is cleared for the same reason. Write order
                // (ok, then payload) matches every other exit path.
                if (string.IsNullOrEmpty(layerId) || string.IsNullOrEmpty(widgetId) || string.IsNullOrEmpty(triggerName))
                {
                    await engine.SetScriptVarAsync("global._wait_ok", "false");
                    await engine.SetScriptVarAsync("global._wait_payload", "");
                    GlobalLogger.Log(
                        "wait_for_visual: ignored — LayerID, WidgetID and TriggerName must all be non-empty " +
                        $"(got '{layerId}'/'{widgetId}'/'{triggerName}'). _wait_ok=false, _wait_payload cleared.",
                        "ScriptEngine", LogLevel.CriticalError);
                    return null;
                }
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

                // ★ V13/§8.1 — the PAYLOAD-BEARING Bus API. The bool-returning
                // TriggerVisualAndWaitAsync wrapper awaits this same call and then
                // discards result.Payload, so calling it here would leave the whole
                // Visual.Complete → Payload path with no reader: the socket would be
                // wirable in Architect, the value would arrive on the /hud socket, and
                // the script would never see it.
                var (completed, payload) = await Bus.Instance.TriggerVisualAndWaitWithPayloadAsync(
                    layerId, widgetId, triggerName, eventData, timeout, engine.ExecutionToken);

                // Surface the wait result so the exporter's Done/Timeout
                // branches can distinguish the two outcomes via _wait_ok.
                await engine.SetScriptVarAsync("global._wait_ok", completed ? "true" : "false");

                // ★ THE STALENESS RULE — the var is written on EVERY outcome, including the
                // ones that carry nothing. This line covers the five outcomes that reach it;
                // the argument guard at the top of the handler covers the sixth (it returns
                // before this point, so it writes both vars itself).
                //
                // global._wait_payload is PERSISTENT: SetScriptVarAsync writes the SQLite
                // Vars table, and ScriptEngine pre-loads every {global.*} reference in the
                // script text at execution start. wait_for_event writes the same var. So a
                // conditional write here (or no write at all, which is what shipped first)
                // does not leave the var empty — it leaves whatever an EARLIER
                // wait_for_event stored, possibly from a previous Hub session, and the
                // author's graph reads that as this wait's result. Wrong data is worse than
                // no data, so every completion overwrites it:
                //
                //   • real VISUAL_COMPLETE carrying a payload  → that payload
                //   • real VISUAL_COMPLETE with the pin unwired → "" (Bus omits it)
                //   • inactive-layer fast-succeed / queue hard-timeout → ""
                //   • outer timeout (completed == false)            → "" (cleared)
                //   • payload declined for arriving on an Untrusted socket (§8.3) → ""
                //   • malformed call — empty LayerID / WidgetID / TriggerName → "",
                //     written by the guard at the top of this handler (which also writes
                //     _wait_ok = "false"), because it returns before reaching this line
                //
                // The `completed ? … : ""` shape is deliberate even though Bus already
                // guarantees "" on every non-ack path: it states the timeout-clears
                // decision in code instead of borrowing it, and it mirrors wait_for_event
                // line-for-line so the two commands cannot drift.
                //
                // CANCEL is the one path that writes NEITHER var: a cancelled script CT
                // makes TriggerVisualAndWaitWithPayloadAsync throw OperationCanceledException
                // before this point, and the execution is being torn down — there is no
                // subsequent node to mis-read the stale value. That matches _wait_ok's
                // pre-existing behaviour exactly; do not "fix" it with a catch, which would
                // swallow the engine's cancellation contract.
                //
                // Ordering (ok, then payload) matches wait_for_event, so the DB-outage
                // window is identical to the one that command has always had.
                await engine.SetScriptVarAsync("global._wait_payload", completed ? payload : "");

                if (!completed)
                    GlobalLogger.Log($"wait_for_visual: timeout waiting for '{layerId}/{widgetId}/{triggerName}'",
                        "ScriptEngine", LogLevel.CriticalError);
                return null;
            });

            engine.RegisterCommand("wait_for_event", async (args) => {
                var bound = engine.CurrentBoundArgs;
                string eventName = bound?.GetOrDefault<string>("EventName", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int timeout;
                if (bound != null && bound.ContainsKey("TimeoutMS")) timeout = bound.Get<int>("TimeoutMS");
                else
                {
                    string raw = ArgOrEmpty(args, 1);
                    timeout = string.IsNullOrEmpty(raw) ? 10000
                        : (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 10000);
                }
                // ★ KNOWN RESIDUAL, deliberately not changed in this sprint: this guard is the
                // same shape wait_for_visual's malformed-args guard used to have, so an empty
                // EventName still returns with BOTH vars left holding the previous wait's
                // values. It is a real instance of the same staleness class (reported to the
                // campaign owner, not silently accepted); the fix is the four lines
                // wait_for_visual now carries — _wait_ok = "false", _wait_payload = "", one log.
                // It is out of B1's charter, which covered wait_for_visual only, and B1's write
                // block below still mirrors wait_for_visual's line-for-line.
                if (string.IsNullOrEmpty(eventName)) return null;
                var msg = await Bus.Instance.WaitForEventAsync(eventName, timeout, engine.ExecutionToken);
                bool completed = msg != null;
                await engine.SetScriptVarAsync("global._wait_ok", completed ? "true" : "false");
                await engine.SetScriptVarAsync("global._wait_payload", completed ? (msg!.Payload ?? "") : "");
                if (!completed)
                    GlobalLogger.Log($"wait_for_event: timeout waiting for '{eventName}'", "ScriptEngine", LogLevel.CriticalError);
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
