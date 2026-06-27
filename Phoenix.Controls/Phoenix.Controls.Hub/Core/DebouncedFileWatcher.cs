using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Pattern-C consolidation — shared primitives for the
    /// "FileSystemWatcher + 250 ms debounce + content-stability poll" pattern
    /// used by <see cref="LogicWatcher"/>, <see cref="LayerWatcher"/>, and
    /// <see cref="HUDServer"/>'s post-reload settle fence.
    ///
    /// Two concerns live here:
    ///   1. <see cref="PathDebouncer"/> — coalesces a burst of FileSystemWatcher
    ///      events on a given key (file path or a synthetic constant) into a
    ///      single delayed callback. LogicWatcher uses one global key; LayerWatcher
    ///      keys per file path.
    ///   2. <see cref="WaitForSizeStable"/> / <see cref="WaitForSizeStableAsync"/>
    ///      — polls a file's length until two consecutive samples agree, catching
    ///      the editor "write to .tmp → rename → modify timestamp" pattern where
    ///      the watcher fires before the last bytes hit disk.
    ///
    /// LogicWatcher's <c>FileShare.None</c>-based stability check is intentionally
    /// kept private to that class: it asserts an exclusive write lock has been
    /// released, which is a fundamentally different contract from the size-poll
    /// approach (Visualist's atomic save flushes through a temp file + rename, so
    /// there is never an exclusive write lock on the destination to acquire).
    /// </summary>
    internal static class DebouncedFileWatcher
    {
        /// <summary>
        /// Polls <paramref name="path"/>'s length until two consecutive reads
        /// match, or <paramref name="maxAttempts"/> samples have elapsed.
        /// Returns silently in any failure mode (file vanished, IO error) — the
        /// caller's downstream reader will surface the real error.
        ///
        /// Sync sibling of <see cref="WaitForSizeStableAsync"/>; the void return
        /// preserves <see cref="LayerWatcher.WaitForFileStable"/>'s shape.
        /// </summary>
        public static void WaitForSizeStable(string path, int maxAttempts = 10, int delayMs = 50)
        {
            long lastSize = -1;
            for (int i = 0; i < maxAttempts; i++)
            {
                long size;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) return;
                    size = info.Length;
                }
                catch
                {
                    return;
                }

                if (size == lastSize && size >= 0)
                    return; // two consecutive reads agree → bytes have stopped flowing

                lastSize = size;
                Thread.Sleep(delayMs);
            }
        }

        /// <summary>
        /// Async variant — returns <c>true</c> when the file's length stabilised,
        /// <c>false</c> if it kept growing past <paramref name="maxAttempts"/>,
        /// the file was missing, or the cancellation token tripped. The bool
        /// return preserves <see cref="HUDServer.WaitForFileStableAsync"/>'s shape.
        /// </summary>
        public static async Task<bool> WaitForSizeStableAsync(
            string path,
            CancellationToken ct = default,
            int maxAttempts = 10,
            int delayMs = 50)
        {
            long previous = -1;
            for (int i = 0; i < maxAttempts; i++)
            {
                if (ct.IsCancellationRequested) return false;

                long current;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) return false;
                    current = info.Length;
                }
                catch (IOException) { current = -1; }
                catch (UnauthorizedAccessException) { current = -1; }

                if (current >= 0 && current == previous) return true;
                previous = current;

                try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }
            return false;
        }
    }

    /// <summary>
    /// Per-key debounce timer pool. Each <see cref="Schedule"/> call resets the
    /// timer for its key; the supplied action fires once after the configured
    /// quiet window. Disposing the pool cancels every pending timer.
    ///
    /// Used by <see cref="LogicWatcher"/> with a single constant key (one global
    /// "burst of recent edits" debounce) and by <see cref="LayerWatcher"/> with
    /// per-file-path keys (a debounce per .phxlayer file).
    ///
    /// The contract: if <see cref="Dispose"/> has run, <see cref="Schedule"/>
    /// becomes a no-op. Callers should also re-check disposal inside the
    /// action body, because the timer pool can't observe whether the work
    /// payload itself has long-running prerequisites (e.g. WaitForSizeStable
    /// polls) that should abort on shutdown.
    /// </summary>
    internal sealed class PathDebouncer : IDisposable
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, System.Threading.Timer> _timers =
            new(StringComparer.OrdinalIgnoreCase);
        private int _disposed;

        /// <summary>
        /// Reset the timer for <paramref name="key"/>. After
        /// <paramref name="delayMs"/> of quiet, <paramref name="action"/> fires
        /// on a thread-pool thread. Throws nothing — exceptions inside the
        /// action are swallowed; callers should funnel their own errors through
        /// the action body.
        /// </summary>
        public void Schedule(string key, int delayMs, Action action)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (string.IsNullOrEmpty(key)) return;
            if (action == null) return;

            lock (_lock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;

                if (!_timers.TryGetValue(key, out var timer))
                {
                    string capturedKey = key;
                    Action capturedAction = action;
                    timer = new System.Threading.Timer(_ =>
                    {
                        if (Volatile.Read(ref _disposed) != 0) return;
                        // Drop the timer so a later edit creates a fresh one.
                        // We keep the timer alive long enough for Schedule to
                        // re-Change() it; if the action body is the only owner,
                        // GC reaches it after the callback returns.
                        lock (_lock)
                        {
                            if (_timers.TryGetValue(capturedKey, out var t))
                            {
                                _timers.Remove(capturedKey);
                                try { t.Dispose(); } catch { }
                            }
                        }
                        try { capturedAction(); }
                        catch { /* caller funnels its own errors */ }
                    }, null, Timeout.Infinite, Timeout.Infinite);
                    _timers[key] = timer;
                }
                timer.Change(delayMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Cancel and discard the pending timer for <paramref name="key"/> if
        /// any. Safe to call concurrently with <see cref="Schedule"/>.
        /// </summary>
        public void Cancel(string? key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                if (_timers.TryGetValue(key, out var t))
                {
                    _timers.Remove(key);
                    try { t.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Returns true once <see cref="Dispose"/> has run. Mirrors LayerWatcher's
        /// <c>_disposed</c> flag so callers can re-check inside their action body
        /// (e.g. between WaitForSizeStable and the final registry mutation).
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Dispose every pending timer. Subsequent <see cref="Schedule"/> calls
        /// become no-ops. The flag-flip happens inside the same lock used by
        /// Schedule's timer-creation critical section, so a debounce timer can't
        /// be created after Dispose has started tearing down.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                foreach (var t in _timers.Values)
                {
                    try { t.Dispose(); } catch { }
                }
                _timers.Clear();
            }
        }
    }
}
