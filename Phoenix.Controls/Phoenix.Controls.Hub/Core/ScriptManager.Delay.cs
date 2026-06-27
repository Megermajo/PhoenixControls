using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: delay-tier commands (delay / delay_seconds /
    // timeout_check). All three are temporal primitives that read either
    // ms or seconds and tie into the engine's CancellationToken / per-
    // handler start-time so timeouts and shutdown can interrupt sleeps.
    //
    // H11 — both delay forms honour _engine.ExecutionToken so a
    // delay(60000) cannot outlast ScriptTimeoutSeconds. The catch-and-
    // rethrow on OperationCanceledException is intentional: it lets the
    // surrounding script-engine machinery treat the cancellation the same
    // way as any other propagated cancel.
    //
    // timeout_check writes global._timeout_ok against the engine's
    // global._script_start_ms stamp (set at the start of each
    // ExecuteScriptAsync call). Async.Timeout drives its OnTime / Late
    // branches off this var.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterDelayCommands()
        {
            // delay(ms)
            // H11 — honor the script's CancellationToken so timeout / shutdown / CancelAllScripts
            // can interrupt sleeps. Without this, a `delay(60000)` outlasted ScriptTimeoutSeconds.
            _engine.RegisterCommand("delay", async (args) => {
                int ms = (_engine.CurrentBoundArgs != null && _engine.CurrentBoundArgs.ContainsKey("MS"))
                    ? _engine.CurrentBoundArgs.Get<int>("MS")
                    : (int.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0);
                if (ms > 0)
                {
                    var ct = _engine.ExecutionToken;
                    try { await Task.Delay(ms, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                }
                return null;
            });

            // delay_seconds(s) — pauses the current handler for s seconds
            // (accepts integer or float). Driven by the Flow.Delay node.
            _engine.RegisterCommand("delay_seconds", async (args) => {
                // Float → cast to double for the * 1000 math; binder's Float is float
                // (single-precision), which is fine for second-resolution delays.
                double s;
                var bound = _engine.CurrentBoundArgs;
                if (bound != null && bound.ContainsKey("Seconds")) s = bound.Get<float>("Seconds");
                else s = double.TryParse(ArgOrEmpty(args, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out var sx) ? sx : 0d;
                if (s > 0)
                {
                    int ms = (int)Math.Min(int.MaxValue, s * 1000d);
                    var ct = _engine.ExecutionToken;
                    try { await Task.Delay(ms, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                }
                return null;
            });

            // timeout_check(ms) — sets global._timeout_ok to "true" if the
            // current script handler has been running for fewer than ms
            // milliseconds, "false" otherwise. ScriptEngine stamps
            // global._script_start_ms at the start of each ExecuteScriptAsync
            // call. Used by Async.Timeout to drive its OnTime/Late branches.
            _engine.RegisterCommand("timeout_check", async (args) => {
                long limitMs = (_engine.CurrentBoundArgs != null && _engine.CurrentBoundArgs.ContainsKey("MS"))
                    ? _engine.CurrentBoundArgs.Get<int>("MS")
                    : (long.TryParse(ArgOrEmpty(args, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0L);
                string startStr = _engine.GetExecutionVar("global._script_start_ms");
                if (!long.TryParse(startStr, out long startMs))
                    startMs = Environment.TickCount64;
                long elapsed = Environment.TickCount64 - startMs;
                bool ok = elapsed < limitMs;
                await _engine.SetScriptVarAsync("global._timeout_ok", ok ? "true" : "false");
                return null;
            });
        }
    }
#pragma warning restore CS1998
}
