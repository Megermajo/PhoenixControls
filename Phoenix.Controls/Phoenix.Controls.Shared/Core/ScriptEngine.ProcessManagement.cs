// ProcessManagement band carved from ScriptEngine.cs.
// Owns: fire-and-forget process spawn/terminate primitives —
//   _spawnedProcesses ledger + OnProcessSpawned/OnProcessTerminated events,
//   TerminateSpawnedProcess, HandleProcessSpawnBlock, StripQuotesAndSubstitute.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Core
{
    public partial class ScriptEngine
    {
        // ─────────────────────────────────────────────────────────────────
        // PROCESS UNIFICATION — engine-native fire-and-forget spawn block
        //
        // Engine-side ledger of running spawned processes, keyed by instance
        // id. Each entry carries a CTS so process.terminate(id) can cancel
        // the body's await points without touching the parent script. The
        // dictionary is engine-owned (not ProcessManager-owned) because
        // ScriptEngine lives in Shared and can't reference Hub —
        // ScriptManager subscribes to OnProcessSpawned / OnProcessTerminated
        // and mirrors state to ProcessManager + RemoteBridgeServer.
        // ─────────────────────────────────────────────────────────────────
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _spawnedProcesses
            = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        /// <summary>Fired when <c>process_spawn(...)</c> launches a new instance. (instanceId, title).</summary>
        public event Action<string, string>? OnProcessSpawned;

        /// <summary>Fired when a spawned instance ends (clean exit, fault, or terminate).</summary>
        public event Action<string>? OnProcessTerminated;

        /// <summary>
        /// Cancel a spawned process body by instance id. Returns true if a live
        /// instance was found and signalled. Called from ScriptManager's
        /// <c>process.terminate</c> handler.
        /// </summary>
        public bool TerminateSpawnedProcess(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            if (_spawnedProcesses.TryGetValue(instanceId, out var cts))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Handles <c>process_spawn(stableId, instanceId, "Title"):</c>. Captures
        /// the body, snapshots vars, registers the instance under its own CTS,
        /// fires Task.Run to execute the body detached, and returns immediately
        /// so the parent script flows past Done. Body errors are logged; cancellation
        /// is the normal terminate path. The Task.Run lets the spawned body's
        /// AsyncLocal context (`_executionVars`, `_executionCt`) live in its own
        /// flow without bleeding into the parent.
        ///
        /// Returns synchronously (not async Task) so the parent's ExecuteBlock
        /// loop continues immediately — the `_ = Task.Run(...)` discard is the
        /// fire-and-forget contract.
        /// </summary>
        private int HandleProcessSpawnBlock(string[] lines, int i, int end, int indent, string line, Dictionary<string, string> vars)
        {
            // Parse "process_spawn(arg1, arg2, arg3):" — strip prefix + closing "):"
            int prefixLen = "process_spawn(".Length;
            int innerLen  = line.Length - prefixLen - 2;
            string innerArgs = innerLen > 0 ? line.Substring(prefixLen, innerLen) : "";
            var parts = SplitArgs(innerArgs);
            string stableProcessId = parts.Length > 0 ? StripQuotesAndSubstitute(parts[0], vars) : "";
            string instanceId      = parts.Length > 1 ? StripQuotesAndSubstitute(parts[1], vars) : "";
            string title           = parts.Length > 2 ? StripQuotesAndSubstitute(parts[2], vars) : "Process";

            if (string.IsNullOrEmpty(instanceId)) instanceId = Guid.NewGuid().ToString();

            int blockEnd = FindBlockEnd(lines, i, indent);

            // Snapshot vars so the spawned body sees the parent's state at
            // spawn time but its writes don't bleed back into the parent's
            // dict. SetScriptVarAsync inside the spawn still hits the DB
            // (shared singleton) — that's intentional; processes can persist
            // results just like any other script.
            var snapshot = new Dictionary<string, string>(vars, StringComparer.OrdinalIgnoreCase);

            // Surface the instance id to the caller so Process.Spawn's
            // InstanceId output socket resolves to it. The exporter writes
            // the slot name into a per-call-site global before the block.
            // Cache the Replace results — each Replace scans the whole string,
            // and this is a hot process_spawn path.
            string cleanStableId   = stableProcessId.Replace("-", "");
            string cleanInstanceId = instanceId.Replace("-", "");
            string instanceOutKey = $"global._proc_instance_{cleanStableId}_{cleanInstanceId[..Math.Min(6, cleanInstanceId.Length)]}";
            // These two writes mutate the parent's _executionVars dict
            // and previously skipped _executionVarsLock. Inside a parallel_begin
            // arm, a sibling branch reading vars (e.g. via GetExecutionVar /
            // SubstituteVars / SetLocalResultVar) could collide mid-resize and
            // throw InvalidOperationException or read torn values. Mirror the
            // gated-lock pattern from SetLocalResultVar / GetExecutionVar.
            // Acquire UNCONDITIONALLY. The prior
            // `lockTaken = Volatile.Read(_parallelBranchDepth) > 0` gate was a
            // TOCTOU: a nested event.trigger resets depth to 0 mid-flight, so this
            // writer could skip the lock while a sibling branch is mid-access and
            // corrupt the shared Dictionary. Matches the SetScriptVarAsync /
            // SetLocalResultVar / GetExecutionVar fix.
            _executionVarsLock.Wait();
            try
            {
                // Best-effort: tag the parent's vars (no DB write — pure local).
                vars[instanceOutKey] = instanceId;
                // Also stash a "last spawned id" sentinel for hand-written scripts.
                vars["global._proc_last_id"] = instanceId;
                // When process_spawn runs INSIDE a
                // parallel_begin branch these writes land in the branch's vars and
                // must be tagged so parallel_begin's merge-back propagates the
                // instance id (and the _proc_last_id sentinel) to the outer scope —
                // otherwise the Process.Spawn InstanceId output is lost on merge.
                _branchResultKeysLocal.Value?.Add(instanceOutKey);
                _branchResultKeysLocal.Value?.Add("global._proc_last_id");
            }
            finally { _executionVarsLock.Release(); }

            // Independent CTS — NOT linked to the parent's _executionCt.
            // A spawned process must outlive the parent script (that's the
            // whole point of the unification), so linking would defeat the
            // primitive. Termination flows through ProcessManager → engine
            // event → CTS.Cancel via TerminateSpawnedProcess.
            var cts = new CancellationTokenSource();
            // Atomically claim the instance id — if a duplicate id is reused
            // the second spawn collides and is rejected (same posture as
            // ProcessManager.CreateProcess's displaced-displace).
            if (!_spawnedProcesses.TryAdd(instanceId, cts))
            {
                GlobalLogger.Log($"process_spawn: instance id '{instanceId}' is already running — refusing duplicate.",
                    "ScriptEngine", LogLevel.CriticalError);
                cts.Dispose();
                return blockEnd + 1;
            }

            try { OnProcessSpawned?.Invoke(instanceId, title); } catch { /* host wiring failure must not abort spawn */ }
            GlobalLogger.Log($"Spawned process '{title}' ({instanceId}) — body runs detached.", "ScriptEngine", LogLevel.System);

            int bodyStart  = i + 1;
            int bodyEnd    = blockEnd;
            int bodyIndent = indent + 1;
            // Capture for the closure — `lines`, `bodyStart`, etc. are
            // stack-locals; closure captures them by value of reference.
            try
            {
            _ = Task.Run(async () =>
            {
                // AsyncLocal writes inside this Task only land in this Task's
                // ExecutionContext — they don't bleed into the parent script's
                // flow. Same isolation pattern as RunParallelBranch.
                _executionVars   = snapshot;
                _executionCt     = cts.Token;
                _executionDepth  = 0;
                // Allocate a FRESH StrongBox<int> rather than assigning
                // _aggregateLoopIterations = 0 (which mutates the parent-shared box
                // via _execItersLocal.Value.Value=0 and silently zeroes the parent's
                // runaway-loop counter mid-flight). The spawned process gets its own
                // independent budget; the parent's cap is preserved.
                _execItersLocal.Value = new System.Runtime.CompilerServices.StrongBox<int>(0);
                _visitedNodes    = new HashSet<string>(StringComparer.Ordinal);
                _branchResultKeysLocal.Value = null;

                try
                {
                    await ExecuteBlock(lines, bodyStart, bodyEnd, bodyIndent, snapshot).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal terminate path — process.terminate(id) → CTS.Cancel.
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"process '{title}' ({instanceId}) faulted: {ex.Message}",
                        "ScriptEngine", LogLevel.CriticalError);
                }
                finally
                {
                    if (_spawnedProcesses.TryRemove(instanceId, out var removed))
                    {
                        try { removed.Dispose(); } catch { }
                    }
                    try { OnProcessTerminated?.Invoke(instanceId); } catch { }
                    GlobalLogger.Log($"Process '{title}' ({instanceId}) ended.", "ScriptEngine", LogLevel.System);
                }
            });
            }
            catch (Exception ex)
            {
                // Task.Run scheduling failed before the lambda's finally could run —
                // the ledger entry would leak and permanently lock instanceId.
                // Remove + dispose the CTS here so the id is reusable.
                if (_spawnedProcesses.TryRemove(instanceId, out var leakedCts))
                {
                    try { leakedCts.Dispose(); } catch { }
                }
                try { OnProcessTerminated?.Invoke(instanceId); } catch { }
                GlobalLogger.Log($"process_spawn: failed to schedule body for '{title}' ({instanceId}): {ex.Message}",
                    "ScriptEngine", LogLevel.CriticalError);
            }

            // Parent flow continues immediately — fire-and-forget.
            return blockEnd + 1;
        }

        /// <summary>
        /// Convenience: trim quotes off a string-literal arg AND substitute any
        /// {var} references against the vars dict. Used by HandleProcessSpawnBlock
        /// to coerce literal-or-variable arguments into a final value.
        /// </summary>
        private string StripQuotesAndSubstitute(string raw, Dictionary<string, string> vars)
        {
            string trimmed = raw.Trim();
            string substituted = SubstituteVars(trimmed, vars);
            string s = substituted.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s.Substring(1, s.Length - 2);
            return s;
        }
    }
}
