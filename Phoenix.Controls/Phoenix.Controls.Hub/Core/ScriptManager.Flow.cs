using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: flow.* command registrations.
    // Lifts the 2 execution-local "fire once / fire N times" gates
    // (do_once, do_n) out of RegisterHubCommands.
    //
    // The legacy exporter emits inline `global._doonce_<id>` /
    // `global._don_counter_<id>` bookkeeping persisted to Vars
    // (so the counter survives engine restarts). These dispatched-command
    // variants keep the counter in execution-local result vars (ephemeral,
    // lost on engine restart) and honour the Reset socket so the streamer
    // can re-arm a "do once" flow without manually clearing the DB.
    //
    // Both return "go" when the body should run, "skip"/"complete"
    // otherwise; the exporter / Done branch gates on
    // result.flow_do_once_<id> / result.flow_do_n_<id>. Reset is truthy
    // per ScriptEngine.ParseTruthy ("true", "1", "yes", "on",
    // "y", "t") on the legacy-fallback path, and case-insensitive Bool
    // on the typed-bind path.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterFlowCommands()
        {
            _engine.RegisterCommand("flow.do_once", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string nodeId = bound?.GetOrDefault<string>("NodeId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                // The legacy fallback used ScriptEngine.ParseTruthy which is a
                // superset of the binder's accepted Bool aliases (e.g. accepts "t").
                // Keep the legacy semantic on the fallback path, the binder's
                // case-insensitive acceptance on the bound path.
                bool reset = (bound != null && bound.ContainsKey("Reset"))
                    ? bound.Get<bool>("Reset")
                    : (args.Length >= 2 && ScriptEngine.ParseTruthy(args[1]));
                if (string.IsNullOrEmpty(nodeId)) return "skip";
                string key    = $"do_once_{nodeId}";
                string state  = _engine.GetExecutionVar(key);
                if (reset)
                {
                    // Clear the ephemeral counter and re-arm. Reset takes priority — even
                    // if the body already ran, we treat the next call as the new "first".
                    _engine.SetLocalResultVar(key, "");
                    state = "";
                }
                if (string.IsNullOrEmpty(state))
                {
                    _engine.SetLocalResultVar(key, "done");
                    _engine.SetLocalResultVar($"result.flow_do_once_{nodeId}", "go");
                    await Task.CompletedTask;
                    return "go";
                }
                _engine.SetLocalResultVar($"result.flow_do_once_{nodeId}", "skip");
                await Task.CompletedTask;
                return "skip";
            });
            _engine.RegisterCommand("flow.do_n", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string nodeId = bound?.GetOrDefault<string>("NodeId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int n = (bound != null && bound.ContainsKey("N"))
                    ? bound.Get<int>("N")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedN) ? parsedN : 0);
                bool reset = (bound != null && bound.ContainsKey("Reset"))
                    ? bound.Get<bool>("Reset")
                    : (args.Length >= 3 && ScriptEngine.ParseTruthy(args[2]));
                if (string.IsNullOrEmpty(nodeId)) return "skip";
                string key    = $"do_n_{nodeId}";
                string raw    = _engine.GetExecutionVar(key);
                int counter   = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 0;
                if (reset)
                {
                    counter = 0;
                    _engine.SetLocalResultVar(key, "0");
                }
                if (counter < n)
                {
                    counter++;
                    _engine.SetLocalResultVar(key, counter.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar($"result.flow_do_n_{nodeId}", "go");
                    await Task.CompletedTask;
                    return "go";
                }
                _engine.SetLocalResultVar($"result.flow_do_n_{nodeId}", "complete");
                await Task.CompletedTask;
                return "complete";
            });
        }
    }
#pragma warning restore CS1998
}
