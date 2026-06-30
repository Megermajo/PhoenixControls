using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Live-process commands + per-instance run path. process.start / process.stop
    // replace the deprecated fire-and-forget process_spawn block + process.terminate
    // (the latter kept as a shim in ScriptManager.System.cs, routed to StopInstance).
    public partial class ScriptManager
    {
        private static readonly IReadOnlyDictionary<string, string> _emptyParams =
            new Dictionary<string, string>(0);

        private void RegisterProcessCommands()
        {
            // process.start("<ProcessId>", "<Name>"[, p=v …]) → returns the minted
            // instance id. The exporter captures it into the Process.Start node's
            // InstanceId output via `global._proc_inst_x = process.start(...)`.
            _engine.RegisterCommand("process.start", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string processId = bound?.GetOrDefault<string>("ProcessId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string name      = bound?.GetOrDefault<string>("Name", "Process") ?? "Process";
                var startParams  = bound?.GetOrDefault<IReadOnlyDictionary<string, string>>("StartParams", _emptyParams) ?? _emptyParams;

                // Re-key params under "param.<name>" so the template body resolves
                // them as {param.<name>} (the exporter's read side).
                var scoped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in startParams) scoped[$"param.{kv.Key}"] = kv.Value;

                string instanceId = ProcessInstanceManager.Instance.StartInstance(processId, name, scoped);
                await Task.CompletedTask;
                return instanceId;
            });

            // process.stop(<InstanceId>) — tears down one live instance.
            _engine.RegisterCommand("process.stop", async (args) =>
            {
                string id = _engine.CurrentBoundArgs?.GetOrDefault<string>("InstanceId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (!string.IsNullOrEmpty(id)) ProcessInstanceManager.Instance.StopInstance(id);
                await Task.CompletedTask;
                return null;
            });
        }

        /// <summary>
        /// Runs a process instance's template ONCE with the given EventType + extra
        /// vars — used for on_process_start / on_process_stop and per-instance schedule
        /// ticks. Event-driven fan-out runs templates through the normal family
        /// dispatchers instead; both ultimately funnel through TimedExecuteAsync. Here
        /// the scoped params are merged explicitly because the stop run happens after
        /// the instance is unregistered (so the registry-based merge can't see them).
        /// </summary>
        public async Task RunProcessInstanceAsync(
            string instanceId, string content,
            IReadOnlyDictionary<string, string> scopedVars,
            string eventType,
            IReadOnlyDictionary<string, string>? extraVars = null)
        {
            await _initTask.ConfigureAwait(false);
            if (string.IsNullOrEmpty(content)) return;

            string fn = $"process::{instanceId}";
            var slot = await AcquireEventSlotAsync(fn).ConfigureAwait(false);
            if (slot == EventSlotResult.TimedOut) return;
            var token = BeginExecutionTracked(fn);
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (scopedVars != null) foreach (var kv in scopedVars) vars[kv.Key] = kv.Value;
                if (extraVars  != null) foreach (var kv in extraVars)  vars[kv.Key] = kv.Value;
                vars["process.instance_id"] = instanceId;

                _engine.EventType  = eventType;
                _engine.ScriptFile = fn;
                await TimedExecuteAsync(fn, content, vars, token.Cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobalLogger.Error("ProcessInstance", $"run failed for {fn} ({eventType})", ex);
            }
            finally
            {
                EndExecutionTracked(token);
                ReleaseEventSlot(slot);
            }
        }

        /// <summary>
        /// When <paramref name="fn"/> is a live-process instance ("process::&lt;id&gt;"),
        /// merges that instance's scoped start params (keyed "param.&lt;name&gt;", so they
        /// never overwrite event vars) plus its id into <paramref name="vars"/>. Called
        /// from TimedExecuteAsync so the event-fan-out path (which builds event-only
        /// vars) still resolves {param.*}. No-op for normal file scripts.
        /// </summary>
        private static void MergeProcessInstanceVars(string fn, Dictionary<string, string> vars)
        {
            const string prefix = "process::";
            if (fn == null || !fn.StartsWith(prefix, StringComparison.Ordinal)) return;
            string instanceId = fn.Substring(prefix.Length);
            var info = ScriptRegistry.Instance.GetProcessInstance(instanceId);
            if (info?.ScopedVars != null)
                foreach (var kv in info.ScopedVars)
                    if (!vars.ContainsKey(kv.Key)) vars[kv.Key] = kv.Value;
            vars["process.instance_id"] = instanceId;
        }
    }
}
