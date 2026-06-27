using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Services
{
    /// <summary>
    /// Detects UI-thread stalls — the "app sometimes freezes during editing"
    /// report — by heartbeating the dispatcher from a background thread. When
    /// the UI thread fails to service a heartbeat within <see cref="_stallThreshold"/>
    /// it logs ONE <see cref="LogLevel.CriticalError"/> ("UI thread unresponsive
    /// for Ns"), and a recovery line if the thread comes back. The marker lands
    /// in the System Log and the on-disk <see cref="DiagnosticFileLog"/> with the
    /// preceding entries showing what was running when it stalled — so the next
    /// freeze is diagnosable instead of invisible.
    ///
    /// Pure diagnostics: it observes and logs, never alters app behaviour.
    /// Modal dialogs / file pickers pump a nested message loop, so the heartbeat
    /// still gets serviced — only a genuinely blocked UI thread (sync wait on
    /// async, lock contention, tight loop) trips it. Since all three pillars run
    /// in-process on Hub's single UI thread, one watchdog covers the whole suite.
    /// </summary>
    public sealed class UiHangWatchdog
    {
        private readonly DispatcherQueue _ui;
        private readonly TimeSpan _stallThreshold;
        private readonly TimeSpan _pollInterval;

        private Thread? _thread;
        private volatile bool _running;

        // Heartbeat state. _pingSentAtTicks is written by the watchdog thread and
        // read by both; _outstanding / _reported flip between the watchdog thread
        // and the UI callback. bool fields are volatile for cross-thread
        // visibility; the long uses Interlocked for atomic 32-bit reads.
        private long _pingSentAtTicks;
        private volatile bool _outstanding;
        private volatile bool _reported;

        public UiHangWatchdog(DispatcherQueue ui, TimeSpan? stallThreshold = null, TimeSpan? pollInterval = null)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _stallThreshold = stallThreshold ?? TimeSpan.FromSeconds(8);
            _pollInterval   = pollInterval   ?? TimeSpan.FromSeconds(1);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "UiHangWatchdog",
            };
            _thread.Start();
        }

        public void Stop() => _running = false;

        private void Loop()
        {
            while (_running)
            {
                if (!_outstanding)
                {
                    // Fire a fresh heartbeat at the UI thread.
                    Interlocked.Exchange(ref _pingSentAtTicks, DateTime.UtcNow.Ticks);
                    _outstanding = true;
                    bool enqueued = false;
                    try { enqueued = _ui.TryEnqueue(OnHeartbeatServiced); }
                    catch { /* dispatcher shutting down */ }
                    if (!enqueued) _outstanding = false; // retry next tick
                }
                else
                {
                    // A heartbeat is in flight and not yet serviced — measure it.
                    long waitedTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref _pingSentAtTicks);
                    var waited = TimeSpan.FromTicks(Math.Max(0, waitedTicks));
                    if (waited >= _stallThreshold && !_reported)
                    {
                        _reported = true;
                        // 0.11.x polish — surface the last-traced UI activity
                        // in the stall message so the rolling log identifies
                        // WHAT was running, not just THAT something stalled.
                        // Pre-fix the watchdog only logged the stall duration;
                        // the surrounding entries were typically silent (heavy
                        // operations weren't instrumented), and Majo got
                        // "UI thread unresponsive" with no signal at all about
                        // the culprit. Wrapping suspect paths in
                        // UiActivityTrace.Begin makes the breadcrumb appear
                        // here.
                        string lastActivity = UiActivityTrace.LastActivity ?? "(none traced)";
                        var lastStart = UiActivityTrace.LastActivityStartedAtUtc;
                        string sinceStart = lastStart == DateTime.MinValue
                            ? "n/a"
                            : $"{(DateTime.UtcNow - lastStart).TotalSeconds:F1}s ago";
                        //  Disambiguate loop-vs-post-loop. The
                        // latched LastActivity is the last path to call Begin —
                        // NOT proof its scope is still running (Dispose leaves
                        // the name latched). OpenScopeDepth > 0 means that scope
                        // is genuinely still open (real culprit); 0 means it
                        // returned and the stall is in uninstrumented code after
                        // it (the name is a stale breadcrumb — look downstream).
                        int openDepth = UiActivityTrace.OpenScopeDepth;
                        string scopeState = openDepth > 0
                            ? $"scope STILL OPEN (depth {openDepth}) — this activity is the blocker"
                            : "scope already CLOSED — stall is in uninstrumented code after it";
                        GlobalLogger.Error("UiHangWatchdog",
                            $"UI thread unresponsive for ~{waited.TotalSeconds:F1}s — the app appears frozen. " +
                            $"Last traced UI activity: '{lastActivity}' (started {sinceStart}; {scopeState}). " +
                            "The entries logged just before this show what was running when it stalled.");
                    }
                }
                Thread.Sleep(_pollInterval);
            }
        }

        private void OnHeartbeatServiced()
        {
            // Reaching here means the dispatcher is alive.
            if (_reported)
            {
                long stalledTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref _pingSentAtTicks);
                var stalled = TimeSpan.FromTicks(Math.Max(0, stalledTicks));
                GlobalLogger.Log(
                    $"UI thread responsive again after ~{stalled.TotalSeconds:F1}s stall.",
                    "UiHangWatchdog", LogLevel.System);
                _reported = false;
            }
            _outstanding = false;
        }
    }
}
