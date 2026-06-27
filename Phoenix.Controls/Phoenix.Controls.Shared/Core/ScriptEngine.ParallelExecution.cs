// ParallelExecution band carved from ScriptEngine.cs ().
// Owns: parallel and structured-concurrency block handlers —
//   RunParallelBranch (BH-003/BH-004 — isolated ExecutionContext per branch),
//   HandleAsyncTimeoutBlock (M14 — async_timeout with on_late: branch),
//   HandleSequenceBlock (L8 — sequence_begin, per-arm _visitedNodes snapshot),
//   HandleDoNBlock (L9 — do_n with overflow guard and completed: branch).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Core
{
    public partial class ScriptEngine
    {
        /// <summary>
        /// BH-003 + BH-004 — single parallel_begin branch entry. Started via Task.Run so
        /// the AsyncLocal write below lands in this branch's isolated ExecutionContext
        /// rather than the parent's flow (preventing _executionVars bleed across siblings).
        /// On fault, signals the linked CTS so sibling branches abort at their next
        /// cancellation observation point.
        /// </summary>
        private async Task RunParallelBranch(
            string[] lines, int start, int end, int indent,
            Dictionary<string, string> branchVars,
            HashSet<string> resultKeys,
            CancellationTokenSource linkedCts)
        {
            // BH-003: scope _executionVars to this branch's dict. SetLocalResultVar /
            // SetScriptVarAsync writes from commands that run inside this branch will
            // now land in branchVars — matching the explicit `vars` argument that the
            // script-level loop variables (loop.index, loop.item) already use.
            // _branchResultKeysLocal carries the per-branch HashSet so result-writes
            // get tagged for parallel_begin's merge-back propagation.
            //  Snapshot the prior tagging slot (if a nested parallel_begin
            // sits inside another branch, the outer branch's HashSet must be
            // restored on exit). AsyncLocal copy-on-write already isolates the
            // slot across Task.Run boundaries, but the explicit save/restore
            // mirrors ExecuteScriptAsync's pattern and pins the invariant that
            // no branch leaks tagging state past its own scope.
            var savedBranchVars       = _executionVars;
            var savedBranchResultKeys = _branchResultKeysLocal.Value;
            _executionVars = branchVars;
            _branchResultKeysLocal.Value = resultKeys;
            // QC01-07 — Snapshot _visitedNodes per branch. AsyncLocal copy-on-write
            // copies the slot reference, not the underlying HashSet, so without
            // this snapshot all sibling branches mutated the same HashSet
            // concurrently. That stochastically dropped Architect debug-flash
            // markers (.Add could resize buckets while another branch was
            // reading them) and forced TryAddVisited to take _executionVarsLock
            // — funneling every cross-branch debug-trace through a single
            // semaphore. Mirror HandleSequenceBlock's per-arm snapshot pattern:
            // seed from the parent's visited-set so a node already executed
            // upstream of parallel_begin stays de-duped, but isolate growth so
            // arm-internal NODE_EXECs don't cross-contaminate siblings.
            var parentVisited = _visitedNodes;
            _visitedNodes = parentVisited != null
                ? new HashSet<string>(parentVisited, parentVisited.Comparer)
                : null;
            try
            {
                await ExecuteBlock(lines, start, end, indent, branchVars);
            }
            catch
            {
                // BH-004: signal sibling branches to stop. Swallow the CTS Cancel itself
                // so the original exception still propagates up to Task.WhenAll.
                try { linkedCts.Cancel(); } catch (ObjectDisposedException) { }
                throw;
            }
            finally
            {
                //  Restore the prior slot values so a nested branch
                // (parallel_begin inside another branch) doesn't strand the
                // outer branch's _executionVars / _branchResultKeysLocal /
                // _visitedNodes pointing at the inner branch's dict / HashSet.
                _executionVars = savedBranchVars;
                _branchResultKeysLocal.Value = savedBranchResultKeys;
                // R3 (audit 2026-06-03): _visitedNodes was snapshotted + replaced
                // above (line ~57) but, unlike the other two, was never restored
                // here — leaking the branch's visited-set into the parent flow and
                // corrupting debug-flash dedup after parallel_begin. Restore it
                // symmetrically (matches ExecuteScriptAsync's save/restore).
                _visitedNodes = parentVisited;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // SWEEP #7 BLOCK HANDLERS (M14 / L8 / L9)
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// M14 — async_timeout(ms): block. Runs the body with a per-block CTS
        /// linked to the parent execution token, racing a delay-task against
        /// the body-task via Task.WhenAny. If the timeout wins, the optional
        /// on_late: branch is invoked BEFORE the parent CTS gets a chance to
        /// abort the body — which is the exact failure mode the legacy
        /// flag-based timeout_check pattern hit when MS was shorter than the
        /// global ScriptTimeoutSeconds (the parent CTS would cancel the
        /// whole script before the Late branch could ever evaluate).
        /// Parent timeout semantics are preserved — the parent CTS still
        /// cancels the body task; we just guarantee the Late branch runs.
        /// </summary>
        private async Task<int> HandleAsyncTimeoutBlock(string[] lines, int i, int end, int indent, string line, Dictionary<string, string> vars)
        {
            // Parse "async_timeout(MS):" — strip prefix + closing "):"
            int prefixLen = "async_timeout(".Length;
            int innerLen  = line.Length - prefixLen - 2;
            string msArg  = innerLen > 0 ? line.Substring(prefixLen, innerLen) : "0";
            int.TryParse(SubstituteVars(msArg.Trim(), vars), out int ms);
            if (ms <= 0) ms = 0; // 0 → fire Late immediately (degenerate but well-defined)

            int blockEnd = FindBlockEnd(lines, i, indent);

            // Optional on_late: at the same indent as async_timeout
            int lateIdx = blockEnd + 1;
            while (lateIdx <= end && string.IsNullOrWhiteSpace(lines[lateIdx])) lateIdx++;
            int lateBodyEnd = -1;
            bool hasLate = false;
            if (lateIdx <= end &&
                StripInlineComment(lines[lateIdx].Trim()) == "on_late:" &&
                GetIndent(lines[lateIdx]) == indent)
            {
                hasLate = true;
                lateBodyEnd = FindBlockEnd(lines, lateIdx, indent);
            }

            // Per-block CTS linked to the parent token. The parent CTS
            // cancellation still propagates; the local source lets us cancel
            // the body when the timeout wins WITHOUT mutating the parent.
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(_executionCt);
            var       savedCt  = _executionCt;
            _executionCt = localCts.Token;

            // Launch the body — it observes the local token via _executionCt
            // (set above) and via any registered command that captures
            // engine.ExecutionToken at await time.
            Task bodyTask = ExecuteBlock(lines, i + 1, blockEnd, indent + 1, vars);
            Task timeoutTask = Task.Delay(ms, localCts.Token);

            Task winner;
            try
            {
                winner = await Task.WhenAny(bodyTask, timeoutTask).ConfigureAwait(false);
            }
            catch
            {
                _executionCt = savedCt;
                throw;
            }

            if (winner == timeoutTask && !timeoutTask.IsCanceled)
            {
                // Timeout fired first — cancel the body locally (NOT the parent
                // CTS) so the body unwinds, then run on_late before returning.
                // Crucially, _executionCt is left pointing at the local (now
                // cancelled) token until bodyTask is observed, so the body's
                // own ExecuteBlock loop sees the cancellation on its next
                // ThrowIfCancellationRequested check rather than racing on
                // an already-restored parent token.
                try { localCts.Cancel(); } catch { }
                try { await bodyTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                _executionCt = savedCt;

                if (hasLate)
                    await ExecuteBlock(lines, lateIdx + 1, lateBodyEnd, indent + 1, vars);
                return hasLate ? lateBodyEnd + 1 : blockEnd + 1;
            }

            // Body finished within budget — restore the parent token so
            // siblings of this block see the correct cancellation context,
            // then surface any genuine body exception to the caller.
            try { await bodyTask.ConfigureAwait(false); }
            finally
            {
                // QC01-09 — Cancel the delay so the Task.Delay completes promptly
                // and observe its OperationCanceledException so the dangling task
                // doesn't show up as UnobservedTaskException at finalize-time.
                // localCts.Cancel() is safe to call on a token that's already
                // been observed via Task.WhenAny (no-op).
                try { localCts.Cancel(); } catch { }
                try { await timeoutTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected — delay was cancelled */ }
                catch { /* defensive — Task.Delay shouldn't throw anything else */ }
                _executionCt = savedCt;
            }
            // Skip the on_late: branch if present, since the body succeeded.
            return hasLate ? lateBodyEnd + 1 : blockEnd + 1;
        }

        /// <summary>
        /// L8 — sequence_begin block. Each top-level entry inside the block
        /// (at indent+1) is one "arm" — a discrete path through the graph.
        /// Snapshot+restore <see cref="_visitedNodes"/> around each arm so
        /// arm-2 doesn't see arm-1's NODE_EXEC fingerprints and skip nodes
        /// that should fire again. Mirrors the save/restore pattern used
        /// around parallel_begin's per-execution state. Arms run sequentially
        /// (not in parallel — that's parallel_begin).
        /// </summary>
        private async Task<int> HandleSequenceBlock(string[] lines, int i, int end, int indent, Dictionary<string, string> vars)
        {
            int seqEnd = FindBlockEnd(lines, i, indent);

            // Each top-level entry under sequence_begin (indent + 1) is one
            // arm — including comment-only entries (NODE_EXEC markers). Don't
            // skip comments here the way parallel_begin does: a sequence
            // arm can BE a single marker (one node-fire), and skipping it
            // would silently drop the arm.
            int j = i + 1;
            while (j <= seqEnd)
            {
                if (j >= lines.Length) break;
                string rawArm = lines[j];
                if (string.IsNullOrWhiteSpace(rawArm)) { j++; continue; }

                int armIndent = GetIndent(rawArm);
                if (armIndent < indent + 1) break;
                if (armIndent > indent + 1) { j++; continue; }

                int armEnd = FindBlockEnd(lines, j, indent + 1);
                armEnd = Math.Min(armEnd, seqEnd);

                // Snapshot _visitedNodes for THIS arm — entries added during
                // arm execution don't leak across to the next arm. Restored
                // in finally so an arm-internal throw still rebalances state.
                var savedVisited = _visitedNodes != null
                    ? new HashSet<string>(_visitedNodes, _visitedNodes.Comparer)
                    : null;
                try
                {
                    await ExecuteBlock(lines, j, armEnd, indent + 1, vars);
                }
                finally
                {
                    _visitedNodes = savedVisited;
                }

                j = armEnd + 1;
            }
            return seqEnd + 1;
        }

        /// <summary>
        /// L9 — do_n(n): block. Runs the body up to N times across the
        /// execution. Counter is keyed by the line index so each call site
        /// has its own count. Saturates at int.MaxValue rather than
        /// overflowing into negative territory (which would silently freeze
        /// the Completed branch forever because the post-overflow comparison
        /// would always fall into the "still under N" arm). On overflow the
        /// optional completed: branch fires immediately.
        /// </summary>
        private async Task<int> HandleDoNBlock(string[] lines, int i, int end, int indent, string line, Dictionary<string, string> vars)
        {
            int prefixLen = "do_n(".Length;
            int innerLen  = line.Length - prefixLen - 2;
            string nArg   = innerLen > 0 ? line.Substring(prefixLen, innerLen) : "0";
            int.TryParse(SubstituteVars(nArg.Trim(), vars), out int n);

            int blockEnd = FindBlockEnd(lines, i, indent);

            // Optional completed: at the same indent as do_n
            int compIdx = blockEnd + 1;
            while (compIdx <= end && string.IsNullOrWhiteSpace(lines[compIdx])) compIdx++;
            int compEnd = -1;
            bool hasCompleted = false;
            if (compIdx <= end &&
                StripInlineComment(lines[compIdx].Trim()) == "completed:" &&
                GetIndent(lines[compIdx]) == indent)
            {
                hasCompleted = true;
                compEnd = FindBlockEnd(lines, compIdx, indent);
            }

            // Counter key — use the source-line index in hex so each call
            // site has its own slot. ScriptFile is folded in so the same
            // line index in two different scripts doesn't share a counter.
            string key = $"{ScriptFile}:line_{i:x}";
            int cur;
            int next;
            bool overflow;
            lock (_doNCountersLock)
            {
                _doNCounters.TryGetValue(key, out cur);

                // Saturate-at-MaxValue overflow guard: if we're one tick from
                // overflowing, refuse to increment further and short-circuit
                // straight to the completed: branch. This is the heart of L9 —
                // the legacy `counter += 1` path silently rolled to int.MinValue
                // and the comparison `counter <= N` then stayed permanently
                // false-on-the-inner-arm, freezing the Completed path forever.
                overflow = cur > int.MaxValue - 1;
                if (overflow)
                {
                    _doNCounters[key] = int.MaxValue;
                    next = int.MaxValue;
                }
                else
                {
                    next = cur + 1;
                    _doNCounters[key] = next;
                }
            }

            if (overflow)
            {
                if (hasCompleted)
                    await ExecuteBlock(lines, compIdx + 1, compEnd, indent + 1, vars);
                return hasCompleted ? compEnd + 1 : blockEnd + 1;
            }

            if (next <= n)
            {
                await ExecuteBlock(lines, i + 1, blockEnd, indent + 1, vars);
                return hasCompleted ? compEnd + 1 : blockEnd + 1;
            }
            else
            {
                if (hasCompleted)
                    await ExecuteBlock(lines, compIdx + 1, compEnd, indent + 1, vars);
                return hasCompleted ? compEnd + 1 : blockEnd + 1;
            }
        }
    }
}
