using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>One live, started instance of a process.</summary>
    public sealed class ProcessInstance
    {
        public string InstanceId { get; }
        public string ProcessId  { get; }
        public string Name       { get; }
        public string TemplateContent { get; }
        public IReadOnlyDictionary<string, string> ScopedVars { get; }
        public DateTime StartedAtUtc { get; }
        public CancellationTokenSource ScheduleCts { get; } = new();

        private int _stopped;
        public bool MarkStopped() => Interlocked.Exchange(ref _stopped, 1) == 0;

        public ProcessInstance(string instanceId, string processId, string name,
            string templateContent, IReadOnlyDictionary<string, string> scopedVars, DateTime startedAtUtc)
        {
            InstanceId = instanceId;
            ProcessId = processId;
            Name = name;
            TemplateContent = templateContent;
            ScopedVars = scopedVars;
            StartedAtUtc = startedAtUtc;
        }
    }

    /// <summary>
    /// Owns the set of LIVE process instances. <c>process.start</c> mints an instance,
    /// snapshots its template + start params, registers it with <see cref="ScriptRegistry"/>
    /// (so the WhereEnabled fan-out routes events to it), spins per-instance schedule
    /// timers, and runs <c>on_process_start</c> once. <c>process.stop</c> reverses it.
    /// Instance lifetime is unbounded — each trigger is a fresh, normally-capped run;
    /// the instance just "stays registered until stopped".
    /// </summary>
    public sealed class ProcessInstanceManager
    {
        private static ProcessInstanceManager? _instance;
        private static readonly object _gate = new();
        public static ProcessInstanceManager Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_gate) { return _instance ??= new ProcessInstanceManager(); }
            }
        }

        private readonly ConcurrentDictionary<string, ProcessInstance> _instances =
            new(StringComparer.Ordinal);

        // At-most-one-live-instance-per-process gate (processId → live instanceId).
        // A process.start for a process that already has a live instance is IGNORED
        // and the running instance is returned instead of minting a second one.
        // Majo's decision after the 2026-07-14 self-DDoS: a `wheelspin` process
        // accumulated 3 concurrent instances that each re-ran on every chat message
        // and pinned all 3 chat-script semaphore slots, melting the pipeline. This
        // deliberately overrides the original "unlimited concurrent instances" model.
        // GetOrAdd makes the check-and-claim atomic so two racing starts can't both win.
        private readonly ConcurrentDictionary<string, string> _liveByProcessId =
            new(StringComparer.Ordinal);

        private ProcessInstanceManager() { }

        /// <summary>Starts a new instance of <paramref name="processId"/>. Returns the
        /// minted instance id, or "" when the process has no template (not saved).
        /// <paramref name="scopedVars"/> are already keyed "param.&lt;name&gt;".</summary>
        public string StartInstance(string processId, string name, IReadOnlyDictionary<string, string> scopedVars)
        {
            string? template = ProcessTemplateRegistry.Instance.GetContent(processId);
            if (string.IsNullOrEmpty(template))
            {
                GlobalLogger.Log(
                    $"process.start: no template found for process id '{processId}' (name '{name}'). " +
                    "Save the process in Architect so its template is generated. Nothing started.",
                    "ProcessInstance", LogLevel.CriticalError);
                return "";
            }

            string instanceId = Guid.NewGuid().ToString();

            // Cap: at most one live instance per process. GetOrAdd atomically returns
            // the already-live instance id when one exists, or claims the slot for
            // this new id. A non-matching return means another instance is already
            // live → ignore this start and hand back the live one so the caller's
            // InstanceId output still points at a running instance (process.stop on
            // it works). Prevents the runaway-accumulation self-DDoS.
            string claimed = _liveByProcessId.GetOrAdd(processId, instanceId);
            if (!string.Equals(claimed, instanceId, StringComparison.Ordinal))
            {
                GlobalLogger.Log(
                    $"process.start: '{name}' (id '{processId}') already has a live instance ({claimed}) — " +
                    "ignoring the new start (max 1 concurrent instance per process). " +
                    "Stop the running one first if you need a fresh run.",
                    "ProcessInstance", LogLevel.System);
                return claimed;
            }

            var scoped = scopedVars ?? new Dictionary<string, string>(0);
            var inst = new ProcessInstance(instanceId, processId, name, template, scoped, DateTime.UtcNow);
            _instances[instanceId] = inst;

            // Publish so the WhereEnabled fan-out reaches the instance's live triggers.
            ScriptRegistry.Instance.RegisterProcessInstance(instanceId, template, scoped);

            // Per-instance schedule timers (cancelled on stop).
            StartScheduleTimers(inst);

            // Observable record for the in-process ProcessManager.
            try { ProcessManager.Instance.CreateProcess(instanceId, name); } catch { }

            // on_process_start runs once, detached, with EventType="ProcessStart".
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => ScriptManager.Instance.RunProcessInstanceAsync(
                    instanceId, template, scoped, "ProcessStart"),
                "ProcessInstance", $"on_process_start ({name})");

            GlobalLogger.Log($"Process '{name}' started — instance {instanceId}.",
                "ProcessInstance", LogLevel.System);
            return instanceId;
        }

        /// <summary>Stops a live instance by id (idempotent). Returns true if a live
        /// instance was found and torn down.</summary>
        public bool StopInstance(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            if (!_instances.TryRemove(instanceId, out var inst)) return false;
            if (!inst.MarkStopped()) return false; // a concurrent stop won the race

            // Release the at-most-one-live gate so a future process.start for this
            // process can run again. Keyed remove so a race with a re-start (a new
            // instanceId already re-claimed the slot) can't evict the newer claim.
            _liveByProcessId.TryRemove(
                new KeyValuePair<string, string>(inst.ProcessId, instanceId));

            try { inst.ScheduleCts.Cancel(); } catch { }

            // No new events to this instance.
            ScriptRegistry.Instance.UnregisterProcessInstance(instanceId);

            // on_process_stop runs once, detached, with EventType="ProcessStop".
            // Content + scoped vars come from the instance object (it is already
            // unregistered, so the run path can't re-derive them).
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => ScriptManager.Instance.RunProcessInstanceAsync(
                    instanceId, inst.TemplateContent, inst.ScopedVars, "ProcessStop"),
                "ProcessInstance", $"on_process_stop ({inst.Name})");

            try { ProcessManager.Instance.TerminateProcess(instanceId); } catch { }

            // Dispose the schedule CTS after a beat so any in-flight loop observes the
            // cancel before the token source is torn down.
            try { inst.ScheduleCts.Dispose(); } catch { }

            GlobalLogger.Log($"Process '{inst.Name}' stopped — instance {instanceId}.",
                "ProcessInstance", LogLevel.System);
            return true;
        }

        /// <summary>Stops every live instance (Hub shutdown).</summary>
        public void StopAll()
        {
            foreach (var id in _instances.Keys.ToArray())
                StopInstance(id);
        }

        /// <summary>Snapshot of the live instances (for diagnostics / future UI).</summary>
        public IReadOnlyCollection<ProcessInstance> GetAll() => _instances.Values.ToArray();

        // ── internals ─────────────────────────────────────────────────────────

        private static void StartScheduleTimers(ProcessInstance inst)
        {
            foreach (var entry in SchedulerService.ParseScheduleHeaders(inst.TemplateContent, inst.Name))
            {
                var captured = entry;
                _ = Task.Run(() => SchedulerService.RunScheduleLoopAsync(
                    captured, inst.ScheduleCts.Token, fireCount => FireScheduleAsync(inst, fireCount)));
            }
        }

        private static Task FireScheduleAsync(ProcessInstance inst, int fireCount)
        {
            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.timestamp"] = DateTimeOffset.Now.ToString("o"),
                ["event.count"]     = fireCount.ToString(),
            };
            return ScriptManager.Instance.RunProcessInstanceAsync(
                inst.InstanceId, inst.TemplateContent, inst.ScopedVars, "Schedule", extra);
        }

    }
}
