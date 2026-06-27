using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    public class Process
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public bool IsActive { get; set; } = true;
        
        // Dynamic caches for this specific process
        public ConcurrentDictionary<string, ConcurrentBag<string>> Caches { get; } = new ConcurrentDictionary<string, ConcurrentBag<string>>();
        
        // Signal sources to wake up the process (e.g. WaitUntil nodes)
        public ConcurrentDictionary<string, TaskCompletionSource<bool>> Signals { get; } = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
    }

    public class ProcessManager
    {
        // BH-018 cousin — same systemic ??= race that BH-018 calls out for ScriptManager /
        // LayerRuntime / LayerRegistry. Concurrent first-touches could double-run the
        // private ctor's side effects. Double-checked locking on _instanceLock; _instance
        // remains settable for symmetry with the rest of the singleton pillar.
        private static ProcessManager? _instance;
        private static readonly object _instanceLock = new();
        public static ProcessManager Instance
        {
            get
            {
                var inst = _instance;
                if (inst != null) return inst;
                lock (_instanceLock)
                {
                    return _instance ??= new ProcessManager();
                }
            }
        }

        private readonly ConcurrentDictionary<string, Process> _activeProcesses = new ConcurrentDictionary<string, Process>();

        public event Action<Process>? OnProcessStarted;
        public event Action<string>? OnProcessTerminated;

        private ProcessManager() { }

        public Process CreateProcess(string id, string title)
        {
            var newProcess = new Process
            {
                Id = id,
                Title = title,
                // P0-5 — UTC for DST-safe time math. No live consumer compares
                // Process.StartedAt today (HttpTranslator's StartedAt is an
                // unrelated internal type), so keeping the field as DateTime is
                // safe; just switching the captured wall-clock to UtcNow avoids
                // ±1h jitter if a future consumer subtracts against UtcNow.
                StartedAt = DateTime.UtcNow
            };

            // BH-038 — atomic check-and-displace. The previous TryGetValue + assignment
            // pair was non-atomic; concurrent callers could each pass the check, and
            // the dead `!ReferenceEquals(existing, newProcess)` guard was always true
            // (newProcess is fresh on this stack). AddOrUpdate composes the update
            // factory, so racing CreateProcess calls now serialize at the slot.
            Process? displaced = null;
            var stored = _activeProcesses.AddOrUpdate(
                id,
                addValueFactory: _ => newProcess,
                updateValueFactory: (_, existing) =>
                {
                    displaced = existing;
                    return newProcess;
                });

            if (displaced != null)
            {
                GlobalLogger.Log(
                    $"ProcessManager: process id '{id}' already running ('{displaced.Title}'). Terminating old to make room for '{title}'.",
                    "ProcessManager", Shared.Models.LogLevel.CriticalError);
                displaced.IsActive = false;
                // Wake every waiter on the displaced process so they don't hang forever.
                foreach (var signal in displaced.Signals.Values) signal.TrySetResult(false);
                SafeEvent.Raise(OnProcessTerminated, id, "ProcessManager", "OnProcessTerminated");
            }

            GlobalLogger.Log($"New Persistent Process Started: {title} ({id})", "ProcessManager", Shared.Models.LogLevel.System);
            SafeEvent.Raise(OnProcessStarted, stored, "ProcessManager", "OnProcessStarted");
            return stored;
        }

        public void TerminateProcess(string id)
        {
            if (_activeProcesses.TryRemove(id, out var process))
            {
                process.IsActive = false;
                // Trigger all waiting signals to prevent memory leaks/hung tasks
                foreach (var signal in process.Signals.Values) signal.TrySetResult(false);
                
                GlobalLogger.Log($"Persistent Process Terminated: {process.Title}", "ProcessManager", Shared.Models.LogLevel.System);
                SafeEvent.Raise(OnProcessTerminated, id, "ProcessManager", "OnProcessTerminated");
            }
        }

        public Process? GetProcess(string id)
        {
            _activeProcesses.TryGetValue(id, out var p);
            return p;
        }

        public IEnumerable<Process> GetAllProcesses() => _activeProcesses.Values;

        // M38 — bounded cache. Without this, a long-running process with an active
        // interceptor on a high-traffic event type leaks memory until process
        // termination. Cap is generous (1000 payloads / type) but firm.
        private const int MaxCacheEntries = 1000;

        // P2 — drain throttle. RouteEventToInterceptors runs on the WebSocket
        // receive thread (WS.ParseBotMessage). The old unbounded drain
        // (`while (bag.Count > MaxCacheEntries / 2 ...)`) could take up to ~500
        // items in one call — and each ConcurrentBag.Count is itself O(n) —
        // stalling the message pump under overflow. Cap the synchronous prune to
        // a small fixed number of TryTakes per call; since this method is invoked
        // for every matching incoming event, the bag keeps getting trimmed on
        // subsequent calls without ever blocking the receive thread for long.
        private const int MaxDrainPerCall = 100;

        // Routing engine: HUB calls this for EVERY incoming event type
        public void RouteEventToInterceptors(string eventType, string payload)
        {
            foreach (var process in _activeProcesses.Values)
            {
                if (process.IsActive && process.Caches.TryGetValue(eventType, out var bag))
                {
                    if (bag.Count >= MaxCacheEntries)
                    {
                        // ConcurrentBag doesn't expose a "drop oldest", so trim items
                        // off the bag. Bounded to MaxDrainPerCall TryTakes so the
                        // receive thread isn't stalled; we also avoid the per-iteration
                        // O(n) bag.Count by counting drained items locally.
                        int drained = 0;
                        while (drained < MaxDrainPerCall && bag.TryTake(out _)) drained++;
                        GlobalLogger.Log(
                            $"ProcessManager: interceptor cache for '{eventType}' on '{process.Id}' hit {MaxCacheEntries} entries — pruned {drained}.",
                            "ProcessManager", Shared.Models.LogLevel.System);
                    }
                    bag.Add(payload);
                }
            }
        }
    }
}
