using System;
using System.Threading;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// Lightweight UI-thread breadcrumb tracker. Heavy code paths wrap their
    /// work in <c>using (UiActivityTrace.Begin("name"))</c>; the watchdog
    /// (or any other diagnostic consumer) reads <see cref="LastActivity"/> +
    /// <see cref="LastActivityStartedAtUtc"/> to find out what was running
    /// when the UI thread stalled.
    ///
    /// Pre-fix, the rolling log only contained the watchdog's "UI thread
    /// unresponsive for ~Ns" marker with nothing in the preceding seconds —
    /// the user got a freeze report with no signal about WHAT froze.
    /// Wrapping the suspect paths (graph load, wildcard cascade, pin overlay
    /// rebuild, snap-guide rebuild, render tick) with a Begin scope is
    /// enough to surface the last-touched path in the stall log.
    ///
    /// Thread-static: each thread (UI vs. background) has its own current
    /// activity. <see cref="LastActivity"/> is the most-recently-begun
    /// activity name across ALL threads so the diagnostic remains useful
    /// even when a background thread enqueues work that the UI thread is
    /// blocked on.
    /// </summary>
    public static class UiActivityTrace
    {
        // Per-thread current scope. Set on Begin, restored on Dispose.
        [ThreadStatic] private static string? _currentActivity;

        // Process-wide last-touched activity. Updated on every Begin so
        // the watchdog's stall report identifies the most recent UI work
        // even if the scope has already exited. Volatile read/write because
        // the watchdog reads from a background thread while the UI thread
        // writes; no need for a full lock since the cost of a stale read
        // is a one-cycle-old name (still useful as a breadcrumb).
        private static string? _lastActivity;
        private static long _lastActivityStartedAtUtcTicks;

        //  Process-wide count of currently-open Begin scopes.
        // Incremented on Begin, decremented on Dispose. The watchdog reads this
        // at stall time to discriminate the two cases the latched LastActivity
        // alone can't: depth > 0 means a traced scope is STILL OPEN (the named
        // activity is genuinely where the thread is stuck), depth == 0 means the
        // last scope already closed and the stall is in UNINSTRUMENTED code that
        // ran after it (the latched name is stale). Coarse by design — it counts
        // across all threads, but the heavy traced paths are all UI-thread, so a
        // non-zero depth during a UI stall reliably points at an open UI scope.
        private static int _openScopeDepth;

        /// <summary>
        /// Name of the most recently begun activity across all threads,
        /// or null when nothing has been traced. Safe to read from any
        /// thread (volatile through Interlocked.CompareExchange).
        /// </summary>
        public static string? LastActivity => Volatile.Read(ref _lastActivity);

        /// <summary>
        /// UTC start time of <see cref="LastActivity"/>. <c>DateTime.MinValue</c>
        /// when no activity has begun. Watchdog computes "started Ns ago"
        /// from this so the stall log shows whether the activity is still
        /// running or whether the UI thread stalled after it returned.
        /// </summary>
        public static DateTime LastActivityStartedAtUtc =>
            new(Volatile.Read(ref _lastActivityStartedAtUtcTicks), DateTimeKind.Utc);

        /// <summary>
        /// Current per-thread activity scope name. Returns null on threads
        /// where Begin has never been called.
        /// </summary>
        public static string? CurrentActivity => _currentActivity;

        /// <summary>
        ///  Number of Begin scopes currently open across all
        /// threads. The watchdog logs this alongside <see cref="LastActivity"/>:
        /// a value &gt; 0 at stall time means the named scope is still executing
        /// (real culprit); 0 means it already returned and the stall is in
        /// uninstrumented code afterwards (the name is a stale breadcrumb).
        /// </summary>
        public static int OpenScopeDepth => Volatile.Read(ref _openScopeDepth);

        /// <summary>
        /// Begin a new traced UI-thread scope. The returned IDisposable
        /// restores the prior scope name on Dispose. Cheap — three field
        /// writes and an alloc of the scope struct (only on the boxing of
        /// the IDisposable). Safe to call from any thread; the per-thread
        /// state stays correctly nested through async boundaries that
        /// don't change Thread.CurrentThread.
        /// </summary>
        public static IDisposable Begin(string name)
        {
            var prior = _currentActivity;
            _currentActivity = name;
            Volatile.Write(ref _lastActivity, name);
            Volatile.Write(ref _lastActivityStartedAtUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _openScopeDepth);
            return new Scope(prior);
        }

        private sealed class Scope : IDisposable
        {
            private readonly string? _prior;
            private bool _disposed;
            public Scope(string? prior) { _prior = prior; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _currentActivity = _prior;
                Interlocked.Decrement(ref _openScopeDepth);
                // Deliberately leave _lastActivity at whatever it was —
                // the watchdog wants the last-touched name, not "now
                // we're back in the parent scope".
            }
        }
    }
}
