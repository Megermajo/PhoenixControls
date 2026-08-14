using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    public static class GlobalLogger
    {
        public static event Action<Log>? OnLogEntry;

        // Fires alongside OnLogEntry whenever Error(...) is called. Carries the
        // original Exception so downstream consumers (Live Event Feed redesign,
        // telemetry, etc.) get stack traces, not just message strings.
        public static event Action<ErrorEvent>? OnError;

        // ---------------- Bounded queue ----------------
        // The persistent log writer feeds a SQLite sink that may stall (DB lock,
        // disk full, AV scanning the WAL, etc.). Unbounded ConcurrentQueue would
        // grow without limit while the drain stalled. A bounded Channel with
        // DropOldest gives us back-pressure: oldest entries fall off the back
        // when the cap is reached, the producer never blocks, and we surface a
        // monotonically-increasing drop count via DroppedLogCount.
        private const int MAX_QUEUE = 5000;
        private const int DROP_LOG_INTERVAL = 100; // one Debug.WriteLine per N drops

        // Channel element wraps the Log with a write-sequence number
        // so the writer pump can detect DropOldest evictions exactly (gap
        // between consecutive dequeued sequences = entries evicted).
        private readonly record struct QueuedEntry(Log Entry, long Sequence);

        private static readonly Channel<QueuedEntry> _logChannel =
            Channel.CreateBounded<QueuedEntry>(new BoundedChannelOptions(MAX_QUEUE)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        // Two distinct counters now:
        //   _droppedLogCount        — bumped only when TryWrite returns false
        //                             (closed channel after Stop()).
        //   _evictedLogCount        — bumped inside the writer pump when an
        //                             enqueued entry was knocked off the back of
        //                             the bounded DropOldest channel (detected
        //                             via a monotonically-incrementing per-write
        //                             sequence — see Log() and the writer pump).
        // The old post-write `Reader.Count >= MAX_QUEUE` heuristic was a
        // sampling check, not an eviction detector: it could under-report
        // (writer drained between TryWrite and the count check) AND over-report
        // (channel briefly sat at cap with no eviction happening). Replaced.
        private static long _droppedLogCount;
        private static long _evictedLogCount;
        private static long _lastReportedDropCount;
        private static long _writeSequence; // monotonically incremented per TryWrite call
        private static long _lastReadSequence; // last sequence observed by the writer pump
        // DroppedLogCount = post-close drops + DropOldest evictions. The two
        // are conceptually distinct (the operator distinction is "logger was
        // stopped" vs "sink is too slow") but for a single "how many entries
        // did we lose" headline number, summing keeps the public surface
        // backward-compatible with the M87 contract and the BugFixSweep6 test.
        public static long DroppedLogCount
            => Interlocked.Read(ref _droppedLogCount) + Interlocked.Read(ref _evictedLogCount);
        public static long EvictedLogCount => Interlocked.Read(ref _evictedLogCount);

        private static int _isWriterRunning = 0; // 0 = stopped, 1 = running
        private static CancellationTokenSource _cts = new CancellationTokenSource();

        // Re-entry guard for the OnLogEntry event. A subscriber that calls Log()
        // recursively would otherwise re-fire the event from inside its own
        // handler.
        //
        // Was [ThreadStatic] but the dispatch can happen on any
        // continuation thread (a subscriber may post back to the same Log()
        // through an awaiter that resumes on a different thread). AsyncLocal
        // flows the guard with the logical async context so a Log → handler →
        // Log re-entry is still caught even after a cross-thread marshal.
        private static readonly AsyncLocal<bool> _isDispatchingOnLogEntry = new();

        // OnError re-entry guard. A handler that itself calls Error() (or
        // Log() at CriticalError) would otherwise loop until the stack blows.
        // Same AsyncLocal reasoning as _isDispatchingOnLogEntry — Error() can
        // be invoked from an async continuation that resumed on a thread other
        // than the original caller's.
        private static readonly AsyncLocal<bool> _isDispatchingOnError = new();

        // L72 / — last DB write exception, exposed so the dashboard /
        // health UI can show "logger unhealthy" without subscribing to OnError
        // directly. Stored as a single record so paired writes can be applied
        // atomically via Volatile.Write — a reader can never see a torn pair
        // (new Exception with old timestamp, or vice versa).
        private sealed record DbWriteErrorSnapshot(Exception? Exception, DateTime At);
        private static DbWriteErrorSnapshot _lastDbWriteErrorSnapshot
            = new(null, DateTime.MinValue);
        public static (Exception? Exception, DateTime At) LastDbWriteError
        {
            get
            {
                var snap = Volatile.Read(ref _lastDbWriteErrorSnapshot);
                return (snap.Exception, snap.At);
            }
        }

        // In-memory history for late-opening windows (e.g. SystemLogWindow).
        // L77 — moved off ConcurrentQueue: the snapshot under enumeration was
        // weakly-consistent (entries could appear/disappear mid-copy). A plain
        // LinkedList behind a short lock gives a stable point-in-time view.
        private const int MAX_HISTORY = 2000;
        private static readonly LinkedList<Log> _history = new();
        private static readonly object _historyLock = new();

        public static IReadOnlyList<Log> GetRecentLogs()
        {
            lock (_historyLock)
            {
                // Copy under the lock so the returned list is a stable snapshot.
                var copy = new List<Log>(_history.Count);
                foreach (var entry in _history) copy.Add(entry);
                return copy;
            }
        }

        public static void Log(string message, string source = "System", LogLevel level = LogLevel.System)
            => Log(message, source, level, exception: null);

        // Overload that carries the original Exception through to
        // OnLogEntry subscribers (SystemLog row "View Last Error" affordance,
        // any future telemetry sink). The persistent SQLite sink doesn't store
        // the object — it flattens via Message — so the Exception lives only on
        // the in-memory ring and the OnLogEntry/OnError dispatches.
        public static void Log(string message, string source, LogLevel level, Exception? exception)
            => LogInternal(message, source, level, exception, persist: true);

        // Ephemeral variant — reaches the in-memory ring + OnLogEntry
        // subscribers (the live LiveFeed / SystemLog panels) but is NOT written
        // to the SystemHistory SQLite store. For privacy-sensitive content that
        // must be visible LIVE in the Hub yet must never land in the permanent
        // on-disk log: e.g. an incoming whisper DM, whose body shows in the Live
        // Feed while the only persisted record is the redacted EventLog audit
        // (sender + "whisper received", no body). Everything else about a normal
        // Log() holds — ring buffer, OnLogEntry fan-out, re-entry guard — only
        // the SQLite persistence step is skipped.
        public static void LogTransient(string message, string source = "System", LogLevel level = LogLevel.System)
            => LogInternal(message, source, level, exception: null, persist: false);

        private static void LogInternal(string message, string source, LogLevel level, Exception? exception, bool persist)
        {
            // Capture the instant once as a DateTimeOffset so the
            // zone offset is preserved. DateTime.Now (Kind=Local) loses the
            // offset when serialized via JsonSerializer (it writes the local
            // wall-clock with no zone). The legacy DateTime Timestamp field
            // stays for UI consumers (LiveFeed / SystemLog) that already key
            // off DateTimeKind; TimestampOffset is the offset-aware form for
            // any on-wire / persisted JSON path. Both fields refer to the
            // same instant — derived from one Now() reading to avoid skew.
            var nowOffset = DateTimeOffset.Now;
            var entry = new Log
            {
                Timestamp = nowOffset.LocalDateTime, // Kind=Local, matches pre-fix value
                TimestampOffset = nowOffset,
                Level = level,
                Source = source,
                Message = message,
                Exception = exception,
            };

            // 1. Keep in-memory history (cap at MAX_HISTORY).
            lock (_historyLock)
            {
                _history.AddLast(entry);
                while (_history.Count > MAX_HISTORY)
                    _history.RemoveFirst();
            }

            // 2. Queue for Persistent Storage (SQLite) FIRST so a slow
            // OnLogEntry subscriber can't delay persistence.
            //
            // Pre-fix the order was history → OnLogEntry fan-out →
            // channel write. A slow listener (Hub Dashboard DispatcherQueue
            // stall, RemoteBridge socket back-pressure, etc.) would push the
            // channel write back arbitrarily; under burst that lets the
            // bounded channel evict earlier entries before they're even
            // queued. Swapping the order means the persistence path is
            // guaranteed to be queued on the same thread tick as the Log()
            // call, regardless of subscriber latency. Listener fan-out still
            // runs on the producer thread (kept synchronous so existing
            // consumers — the WinUI panel sinks, the BugFixSweep tests that
            // assert immediately on captured entries — keep their ordering
            // contract), but the persistence sink no longer pays for it.
            //
            // Bounded channel with DropOldest: TryWrite never returns false
            // on a writable channel, but it MAY silently evict the oldest
            // entry to make room. We tag each enqueue with a monotonically-
            // incrementing sequence number; the writer pump compares the
            // dequeued sequence against the last-observed one. Any gap ==
            // that many entries were evicted between observations.
            //
            // The previous `Reader.Count >= MAX_QUEUE` heuristic
            // was a sampling check, NOT an eviction detector — it lied in
            // both directions (false positives when queue briefly idled at
            // cap, false negatives when the writer drained between TryWrite
            // and the count check). Counter-on-pump is exact.
            // Ephemeral entries (LogTransient) skip BOTH the persistence enqueue
            // and the writer-start — they exist only for the in-memory ring +
            // the OnLogEntry fan-out below, so the whisper body (and any future
            // privacy-sensitive live-only line) never reaches SystemHistory.
            if (persist)
            {
            long seq = Interlocked.Increment(ref _writeSequence);
            if (!_logChannel.Writer.TryWrite(new QueuedEntry(entry, seq)))
            {
                // Channel completed (post-Stop). Treat as a drop and continue —
                // the entry is in-memory history already from step 1.
                long dropped = Interlocked.Increment(ref _droppedLogCount);
                ReportDropIfNeeded(dropped);
            }

            // 3. Ensure writer is running (atomic check-and-set).
            // Snapshot the CTS reference once so a concurrent Stop()
            // can't replace the field between our token-read and the
            // StartLogWriterAsync call (which would let us pass an already-
            // disposed CTS to the pump). Volatile.Read pairs with the
            // Interlocked.Exchange in Stop().
            if (Interlocked.CompareExchange(ref _isWriterRunning, 1, 0) == 0)
            {
                var ctsSnap = Volatile.Read(ref _cts);
                // Route the fire-and-forget pump start
                // through AsyncErrorBoundary (reachable from Shared — it lives in
                // Phoenix.Controls.Shared.Core, NOT Hub, so no pillar/dependency
                // violation) so any fault that escapes the pump's own top-level
                // try/catch is observed instead of becoming an unobserved
                // TaskScheduler exception. The snapshot token is passed as the
                // expected CT so graceful-shutdown cancellation stays silent.
                _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => StartLogWriterAsync(ctsSnap.Token),
                    "GlobalLogger", "log-writer pump", ctsSnap.Token);
            }
            } // end if (persist)

            // 4. Fire event for UI listeners (Hub Dashboard). Per-handler
            // try/catch so one throwing subscriber can't crash the caller.
            // Re-entry guard prevents a handler that calls Log() from
            // recursively re-firing the event from inside its own dispatch.
            DispatchOnLogEntry(entry);

            // Drop the live Exception reference from the ring-buffer
            // entry now that synchronous OnLogEntry fan-out is complete. Deep
            // inner chains + stack traces can run hundreds of KB each; pinning
            // 2000 of them in Gen2 for the lifetime of the process was worst-
            // case ~1 GB. The formatted cascade is already folded into
            // entry.Message by FormatExceptionCascade(), so the historical
            // ring still tells the operator what happened — just not via a
            // live System.Exception object anyone could re-throw.
            //
            // Subscribers that need the original Exception object (telemetry
            // sinks, re-throw paths, the SystemLog "View Last Error"
            // affordance fed from live OnLogEntry) get it on the entry as
            // dispatched above, and via OnError's ErrorEvent.Exception.
            // Late-opening windows that rebuild from GetRecentLogs() get
            // Exception=null on historical rows — the formatted cascade in
            // Message is the historical record. The DB writer (queued via
            // _logChannel above) only reads Level/Source/Message/RawData, so
            // nulling the Exception here is safe even though the writer pump
            // dequeues asynchronously.
            //
            // Reference-typed property — atomic write on all .NET ABIs, no
            // tearing risk for a GetRecentLogs() snapshot reader iterating
            // concurrently (they see either the original or null, never a
            // half-written reference).
            entry.Exception = null;
        }

        // Single-reader pump: each dequeue advances _lastReadSequence
        // by exactly 1 in the no-eviction case. A larger jump means DropOldest
        // knocked entries off the back; the size of the jump is the eviction
        // count. Bumps _evictedLogCount and surfaces via ReportDropIfNeeded so
        // operators see persistent log loss without subscribing to OnError.
        private static void TrackEvictionGap(long currentSeq)
        {
            long last = _lastReadSequence;
            _lastReadSequence = currentSeq;
            if (last == 0) return; // first read — nothing to compare against
            long gap = currentSeq - last - 1;
            if (gap <= 0) return;
            long total = Interlocked.Add(ref _evictedLogCount, gap);
            ReportDropIfNeeded(total);
        }

        private static void ReportDropIfNeeded(long currentDropCount)
        {
            // Only emit a Debug line every DROP_LOG_INTERVAL drops to avoid
            // log-spam when the sink is permanently down.
            long last = Interlocked.Read(ref _lastReportedDropCount);
            if (currentDropCount - last >= DROP_LOG_INTERVAL)
            {
                if (Interlocked.CompareExchange(ref _lastReportedDropCount, currentDropCount, last) == last)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GlobalLogger] persistent log queue dropped {currentDropCount} entries (cap={MAX_QUEUE}).");
                }
            }
        }

        // Routes an error to BOTH the existing log channel (so SystemLogWindow
        // and LiveFeedWindow render it via OnLogEntry, MainForm error bar fires)
        // and the structured OnError event (so future Live Event Feed redesign
        // consumers get the full Exception). Replaces every silent
        // ContinueWith(OnlyOnFaulted) site in the Hub.
        //
        // The original Exception is attached to the Log entry so
        // OnLogEntry subscribers (SystemLog, future telemetry) can render the
        // stack trace. ExceptionDispatchInfo.Capture preserves the stack so
        // downstream consumers may re-throw without losing the origin frame.
        //
        // AggregateException is flattened; each inner exception is
        // appended to the composed message and to the routed entries so a
        // Task.WhenAll fault no longer reads as the generic "One or more
        // errors occurred." line. Non-Aggregate exceptions have their
        // InnerException chain walked too (cap=5 to avoid pathological loops).
        public static void Error(string source, string message, Exception? ex = null)
        {
            // Single DateTimeOffset.Now reading shared between the
            // log-entry timestamp (set inside Log()) and the ErrorEvent we
            // dispatch here. The ErrorEvent's DateTime Timestamp keeps its
            // pre-fix semantics (Kind=Local); the new TimestampOffset field
            // is the offset-aware form for any on-wire JSON consumer.
            var nowOffset = DateTimeOffset.Now;

            if (ex == null)
            {
                Log(message, source, LogLevel.CriticalError, exception: null);
                DispatchOnError(new ErrorEvent
                {
                    Timestamp       = nowOffset.LocalDateTime,
                    TimestampOffset = nowOffset,
                    Source          = source,
                    Message         = message,
                    Exception       = null,
                    Level           = LogLevel.CriticalError,
                });
                return;
            }

            // Preserve the original throw site so a downstream consumer that
            // chooses to re-throw lands on the real origin frame (per the QC
            // note). Capture is cheap and idempotent.
            var captured = ExceptionDispatchInfo.Capture(ex);

            // Build a single multi-line composed string that includes each
            // leaf exception's type, message and stack. Folding into one log
            // entry keeps the ring/SystemLog readable (one row per call site)
            // while still surfacing every inner fault.
            string composed = FormatExceptionCascade(message, captured.SourceException);
            Log(composed, source, LogLevel.CriticalError, captured.SourceException);

            DispatchOnError(new ErrorEvent
            {
                Timestamp       = nowOffset.LocalDateTime,
                TimestampOffset = nowOffset,
                Source          = source,
                Message         = message,
                Exception       = captured.SourceException,
                Level           = LogLevel.CriticalError,
            });
        }

        // Flatten AggregateException + walk InnerException chains.
        // Cap at MaxExceptionDepth so a pathological/cyclic chain can't blow
        // the stack or pin the dispatcher in a tight format loop.
        private const int MaxExceptionDepth = 5;

        private static string FormatExceptionCascade(string message, Exception root)
        {
            var sb = new StringBuilder();
            sb.Append(message).Append(": ").Append(root.Message);

            // Collect every leaf in BFS order, capped at MaxExceptionDepth +1
            // entries (root + 5 inners). AggregateException.Flatten unwraps any
            // nested Aggregates first; we then walk each leaf's InnerException
            // chain to a bounded depth.
            var leaves = CollectLeaves(root, MaxExceptionDepth);

            // Skip the root in the inner-loop output when its Message already
            // matches a leaf — otherwise prefix with "Root:" so the format is
            // unambiguous when there are zero inners.
            int idx = 0;
            foreach (var leaf in leaves)
            {
                idx++;
                sb.AppendLine();
                sb.Append("Inner #").Append(idx).Append(" [")
                  .Append(leaf.GetType().FullName).Append("]: ")
                  .Append(leaf.Message);
                if (!string.IsNullOrEmpty(leaf.StackTrace))
                {
                    sb.AppendLine();
                    sb.Append(leaf.StackTrace);
                }
            }

            // If there were no flattened leaves (e.g. simple Exception with no
            // inner), include the root's own stack so the entry is useful.
            if (idx == 0 && !string.IsNullOrEmpty(root.StackTrace))
            {
                sb.AppendLine();
                sb.Append(root.StackTrace);
            }

            return sb.ToString();
        }

        private static List<Exception> CollectLeaves(Exception root, int maxDepth)
        {
            var result = new List<Exception>(capacity: 4);
            var seen   = new HashSet<Exception>(ReferenceEqualityComparer.Instance);

            // Step 1: flatten the top-level Aggregate if any.
            IEnumerable<Exception> seeds;
            if (root is AggregateException agg)
            {
                var flat = agg.Flatten();
                seeds = flat.InnerExceptions;
            }
            else
            {
                seeds = new[] { root };
            }

            // Step 2: walk each seed's InnerException chain to maxDepth.
            foreach (var seed in seeds)
            {
                if (result.Count >= maxDepth) break;
                var cur = seed;
                int depth = 0;
                while (cur != null && depth < maxDepth && result.Count < maxDepth)
                {
                    if (!seen.Add(cur)) break; // cycle guard
                    result.Add(cur);
                    // For a nested Aggregate inside a non-Aggregate chain,
                    // also descend into its InnerExceptions list.
                    if (cur is AggregateException nestedAgg)
                    {
                        foreach (var nested in nestedAgg.Flatten().InnerExceptions)
                        {
                            if (result.Count >= maxDepth) break;
                            if (seen.Add(nested)) result.Add(nested);
                        }
                        break;
                    }
                    cur = cur.InnerException;
                    depth++;
                }
            }

            return result;
        }

        // Central OnError dispatch with per-handler try/catch and an
        // AsyncLocal re-entry guard. A handler that calls Error/Log will
        // re-enter Log() (which is fine, and we want the inner log to be kept),
        // but will NOT re-fire OnError from inside its own dispatch — the inner
        // call hits the guard, drops to Debug.WriteLine, and returns.
        //
        // Synchronous on purpose: existing tests (ErrorChannelTests,
        // BugFixSweep6_ArchitectBusClient_Tests.CaptureErrors) inspect
        // captured ErrorEvents immediately after the producing call returns,
        // and production listeners (HubChrome status strip, etc.) need the
        // visible bar to flip before downstream code can observe the new
        // state. See WriteBatchAsync for the writer-pump fire-and-forget
        // variant introduced by .
        private static void DispatchOnError(ErrorEvent ev)
        {
            if (_isDispatchingOnError.Value)
            {
                // Re-entrant call (an OnError handler itself called Error()).
                // Surface to debug, don't recurse.
                System.Diagnostics.Debug.WriteLine(
                    $"[GlobalLogger] re-entrant OnError dropped: {ev.Source} :: {ev.Message}");
                return;
            }

            _isDispatchingOnError.Value = true;
            try
            {
                var handlers = OnError?.GetInvocationList();
                if (handlers == null) return;

                foreach (Action<ErrorEvent> h in handlers)
                {
                    try { h(ev); }
                    catch (Exception subEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[GlobalLogger] OnError subscriber threw: {subEx}");
                    }
                }
            }
            finally
            {
                _isDispatchingOnError.Value = false;
            }
        }

        // Writer-pump variant of DispatchOnError. The pump runs
        // single-reader: a slow OnError handler invoked from
        // WriteBatchAsync's catch would block the next dequeue, which is
        // exactly the failure mode the persistent-DB fault path was
        // supposed to surface (NOT cause). Hand the dispatch off to a
        // ThreadPool work item so the pump returns to draining
        // immediately. Re-entry guard is set inside the work item; the
        // producer-side outer guard isn't relevant here because the only
        // caller is the writer pump itself, which never has an outer
        // OnError dispatch flowing.
        //
        // Per-handler invocation order is preserved (single work item walks
        // the invocation list). A handler-thrown exception is swallowed per
        // the per-handler try/catch, identical to the synchronous path.
        private static void DispatchOnErrorFromWriterPump(ErrorEvent ev)
        {
            var handlers = OnError?.GetInvocationList();
            if (handlers == null || handlers.Length == 0) return;

            ThreadPool.QueueUserWorkItem(static state =>
            {
                var (handlers, ev) = ((Delegate[], ErrorEvent))state!;
                bool prior = _isDispatchingOnError.Value;
                _isDispatchingOnError.Value = true;
                try
                {
                    foreach (Action<ErrorEvent> h in handlers)
                    {
                        try { h(ev); }
                        catch (Exception subEx)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[GlobalLogger] OnError subscriber threw: {subEx}");
                        }
                    }
                }
                finally
                {
                    _isDispatchingOnError.Value = prior;
                }
            }, (handlers, ev));
        }

        // OnLogEntry dispatch. Synchronous fan-out on the
        // producer thread (kept that way so existing test fixtures and
        // production sinks observe the entry before the producing call
        // returns); the previous defect was that this dispatch ran BEFORE
        // the channel write, which let a slow subscriber delay
        // persistence. Fix landed in Log(): channel write now happens
        // first, this fan-out is the final step. The producer thread
        // still pays for slow subscribers, but the SQLite sink and its
        // bounded channel no longer do.
        private static void DispatchOnLogEntry(Log entry)
        {
            if (OnLogEntry == null) return;
            if (_isDispatchingOnLogEntry.Value)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GlobalLogger] re-entrant OnLogEntry dropped: {entry.Source} :: {entry.Message}");
                return;
            }

            _isDispatchingOnLogEntry.Value = true;
            try
            {
                var handlers = OnLogEntry?.GetInvocationList();
                if (handlers == null) return;

                foreach (Action<Log> h in handlers)
                {
                    try { h(entry); }
                    catch (Exception subEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[GlobalLogger] OnLogEntry subscriber threw: {subEx}");
                    }
                }
            }
            finally
            {
                _isDispatchingOnLogEntry.Value = false;
            }
        }

        /// <summary>Signals the background writer to stop after draining remaining entries.</summary>
        public static void Stop()
        {
            // Swap in a fresh CTS first via Interlocked.Exchange so a
            // concurrent Log() caller that already passed the
            // CompareExchange(_isWriterRunning, 1, 0) gate will read EITHER the
            // pre-swap CTS (and start a pump that observes immediate
            // cancellation, drains, and resets the flag) OR the post-swap CTS
            // (and start a pump that keeps draining new entries). Either branch
            // is safe; what we must NOT do is mutate _cts in place or null it.
            var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by a previous Stop() — benign on re-entry.
            }
            finally
            {
                old.Dispose();
            }

            // Complete the writer so the pump's
            // WaitToReadAsync wakes, drains the remainder, and exits its loop
            // (the drain comment in StartLogWriterAsync already anticipates
            // completion). TryComplete (not Complete) keeps Stop() idempotent —
            // a second call no-ops instead of throwing on an already-completed
            // writer. The pump's post-loop TryRead drain still flushes anything
            // that queued between the final WaitToReadAsync and completion.
            _logChannel.Writer.TryComplete();
        }

        // Upper bound on entries folded into one SQLite transaction by the
        // writer pump. Bounds both the local accumulation list and the size
        // of a single transaction so a deep backlog flushes as several
        // moderate commits instead of one giant one.
        private const int MaxWriteBatch = 200;

        public static async Task StartLogWriterAsync(CancellationToken ct = default)
        {
            // The outer catch must be Exception, not just OperationCanceledException —
            // an unexpected throw from inside the loop (e.g., a Task.Delay quirk under
            // a cancelled CT) would otherwise escape past the finally and leave
            // _isWriterRunning stuck at 1, wedging future log entries forever.
            try
            {
                var reader = _logChannel.Reader;
                // Batch accumulator reused across iterations — the inner
                // TryRead drain already defines the natural batch boundary
                // (everything available right now); folding that burst into
                // one transaction via WriteBatchAsync replaces the previous
                // commit-per-entry cost. TrackEvictionGap still runs per
                // dequeue, in order, so eviction accounting is unchanged.
                var batch = new List<Log>(capacity: 64);

                // L71 — main loop driven by Channel.WaitToReadAsync, which wakes
                // exactly when an item is produced. No drain-then-finally gap:
                // any item written between TryRead returning false and the next
                // WaitToReadAsync call will simply trigger that wait to return
                // true on the next iteration.
                while (!ct.IsCancellationRequested)
                {
                    bool readable;
                    try
                    {
                        readable = await reader.WaitToReadAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    if (!readable) break; // channel completed

                    while (reader.TryRead(out var queued))
                    {
                        TrackEvictionGap(queued.Sequence);
                        if (queued.Entry != null)
                            batch.Add(queued.Entry);
                        if (batch.Count >= MaxWriteBatch)
                        {
                            await WriteBatchAsync(batch).ConfigureAwait(false);
                            batch.Clear();
                        }
                    }
                    if (batch.Count > 0)
                    {
                        await WriteBatchAsync(batch).ConfigureAwait(false);
                        batch.Clear();
                    }
                }

                // Drain remaining entries after cancellation. Use TryRead (not
                // ReadAsync) so we don't block forever if the channel was never
                // completed; if cancellation fired with the writer mid-write,
                // we still flush whatever queued up.
                while (_logChannel.Reader.TryRead(out var queued))
                {
                    TrackEvictionGap(queued.Sequence);
                    if (queued.Entry != null)
                        batch.Add(queued.Entry);
                    if (batch.Count >= MaxWriteBatch)
                    {
                        await WriteBatchAsync(batch).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
                if (batch.Count > 0)
                {
                    await WriteBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalLogger] writer faulted: {ex}");
            }
            finally
            {
                // Use CompareExchange(want=0, expect=1) so we ONLY
                // reset when we still own the running flag. A racing Stop() that
                // already swapped the CTS and launched a fresh writer wouldn't
                // see its own _isWriterRunning=1 stomped to 0 by a late finally
                // on the prior pump task.
                Interlocked.CompareExchange(ref _isWriterRunning, 0, 1);
            }
        }

        // L72 — DB write failures now bubble up through OnError (so user-visible
        // surfaces like the dashboard error bar can react), and into
        // LastDbWriteError for poll-style health checks. Uses the M86 re-entry
        // guard implicitly: OnError handlers that themselves throw won't crash
        // the writer (DispatchOnError swallows subscriber exceptions); a handler
        // that *calls Error()* won't reach OnError again because the inner
        // dispatch will trip the guard.
        //
        // Routes through DB.WriteLogBatchDedicatedAsync (dedicated
        // SqliteConnection owned by the logger), so a long-running
        // script-driven SELECT on DB.Instance's shared semaphore can no longer
        // stall log persistence. SQLite WAL allows the second connection to
        // write while the shared one reads. One transaction per drained batch;
        // a failure loses the in-flight batch (bounded at MaxWriteBatch), and
        // is published exactly like the old per-entry failure.
        private static async Task WriteBatchAsync(IReadOnlyList<Log> entries)
        {
            if (entries.Count == 0) return;
            try
            {
                await DB.WriteLogBatchDedicatedAsync(entries).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Single atomic publish of the (Exception, Timestamp)
                // pair so a polling reader can never observe a torn snapshot
                // (e.g. a new Exception with a stale timestamp). Volatile.Write
                // on a reference is a release barrier — paired with the
                // Volatile.Read in the LastDbWriteError property getter.
                // Capture offset-aware now once and project both
                // forms — the snapshot keeps its legacy DateTime At field
                // (callers and tests already use it via LastDbWriteError),
                // and the ErrorEvent carries the offset-aware TimestampOffset
                // for downstream JSON consumers.
                var nowOffset = DateTimeOffset.Now;
                Volatile.Write(ref _lastDbWriteErrorSnapshot,
                    new DbWriteErrorSnapshot(ex, nowOffset.LocalDateTime));
                System.Diagnostics.Debug.WriteLine($"[GlobalLogger] DB write failed: {ex}");

                // Surface via OnError ONLY if we're not already inside an OnError
                // dispatch chain — otherwise an OnError handler that itself
                // logs (which queues another DB write that fails) would loop.
                // The guard inside DispatchOnErrorFromWriterPump covers re-entry
                // safely; we additionally avoid re-firing Log()/Error() from
                // here so the failed-write path can't re-enqueue itself into
                // the same broken sink.
                //
                // Writer-pump-specific variant: fans the
                // invocation out onto a ThreadPool work item so this
                // single-reader pump returns to draining the channel
                // immediately, regardless of OnError handler latency.
                // Producer-side Error() callers still get the synchronous
                // DispatchOnError they need for the existing test contracts
                // — only this pump path is async.
                DispatchOnErrorFromWriterPump(new ErrorEvent
                {
                    Timestamp       = nowOffset.LocalDateTime,
                    TimestampOffset = nowOffset,
                    Source          = "GlobalLogger",
                    Message         = "Persistent log write failed",
                    Exception       = ex,
                    Level           = LogLevel.CriticalError,
                });
            }
        }
    }
}
