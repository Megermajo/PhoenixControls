using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Core;
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
    /// On a stall it additionally captures every managed thread's stack via
    /// <see cref="HangStackCapture"/> (ClrMD process snapshot, taken from this
    /// background thread) into <c>logs/ui-hang-stacks-*.txt</c>. The breadcrumb
    /// tracer can only name instrumented scopes — both recorded freezes
    /// (2026-07-01, 2026-07-14) stalled in UNINSTRUMENTED code ("scope already
    /// CLOSED"), which is exactly the gap the stack capture closes: the file
    /// names the blocked frame even inside framework internals. A second
    /// snapshot ~20s into a continuing stall separates a live loop (frames
    /// differ) from a blocked wait (frames identical), and periodic
    /// "STILL unresponsive" lines distinguish a permanent hang from an 8s blip
    /// (both recorded freezes never recovered — pre-fix, the absent recovery
    /// line was the only tell).
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

        // UI-thread identity for the stack capture — recorded once by the first
        // serviced heartbeat (that callback runs on the UI thread), read by the
        // capture worker. 0 until known (a stall before the very first heartbeat
        // still captures; the file just can't mark which thread is the UI one).
        private int _uiManagedThreadId;
        private uint _uiOsThreadId;

        // Stack-capture state. _captureInFlight gates to ONE capture at a time
        // (cross-thread: the watchdog loop launches, the capture worker clears) —
        // if a capture itself ever wedges, further captures are blocked instead
        // of stacking threads. The per-stall budget is two snapshots: one at the
        // trip, one ~20s later, so diffing them separates a live loop from a
        // blocked wait. The session budget is a runaway backstop. Both counters
        // are watchdog-thread-only.
        private int _captureInFlight;
        private int _capturesThisStall;
        private int _capturesThisSession;
        private const int MaxCapturesPerStall = 2;
        private const int MaxCapturesPerSession = 8;
        private static readonly TimeSpan SecondCaptureDelay = TimeSpan.FromSeconds(20);

        // Continuing-stall re-log schedule, in seconds since the stall began:
        // 30s, 60s, then every further 60s. Watchdog-thread-only.
        private double _nextRelogAtSeconds;

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
                // Belt-and-suspenders: nothing a poll iteration does may ever
                // tear down the watchdog thread — an unhandled exception on a
                // background thread kills the WHOLE process on .NET 5+, which
                // would turn a diagnosable freeze into a crash. The iteration
                // body is already defensive; this is the structural guarantee.
                try { PollOnce(); }
                catch { /* skip this tick — the next poll re-evaluates from scratch */ }
                Thread.Sleep(_pollInterval);
            }
        }

        private void PollOnce()
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
                    if (waited < _stallThreshold) return;

                    if (!_reported)
                    {
                        _reported = true;
                        _capturesThisStall = 0;
                        _nextRelogAtSeconds = 30;
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
                        // Disambiguate loop-vs-post-loop. The
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
                        TryLaunchStackCapture(waited.TotalSeconds);
                    }
                    else
                    {
                        // Second snapshot into a continuing stall — identical
                        // frames to the first capture = blocked wait; different
                        // frames = the thread is alive but spinning.
                        if (_capturesThisStall == 1 && waited >= _stallThreshold + SecondCaptureDelay)
                            TryLaunchStackCapture(waited.TotalSeconds);

                        if (waited.TotalSeconds >= _nextRelogAtSeconds)
                        {
                            GlobalLogger.Error("UiHangWatchdog",
                                $"UI thread STILL unresponsive after ~{waited.TotalSeconds:F0}s — the freeze is ongoing, not a blip.");
                            _nextRelogAtSeconds = _nextRelogAtSeconds < 60 ? 60 : _nextRelogAtSeconds + 60;
                        }
                    }
                }
        }

        // Launch one guarded, budgeted stack capture on its own background
        // thread. Never blocks the watchdog loop (the loop keeps re-logging /
        // scheduling the second capture), and a wedged ClrMD walk merely leaks
        // one background thread while _captureInFlight suppresses further
        // attempts — diagnostics must never add a second fault to a frozen app.
        // The LAUNCH itself is guarded too: Thread creation/start can throw
        // (OOM / handle exhaustion) under the very pressure a hang creates, and
        // the gate's only normal reset lives in the worker's finally — so a
        // failed launch must roll back the gate + budgets itself, or captures
        // would be silently disabled for the rest of the session.
        private void TryLaunchStackCapture(double stalledSeconds)
        {
            if (_capturesThisStall >= MaxCapturesPerStall) return;
            if (_capturesThisSession >= MaxCapturesPerSession) return;
            if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) return;
            try
            {
                LaunchStackCaptureWorker(stalledSeconds);
                _capturesThisStall++;
                _capturesThisSession++;
            }
            catch
            {
                // Worker never started → the finally that clears the gate will
                // never run. Release it here so the second-snapshot / next-stall
                // captures self-heal instead of being latched off.
                Interlocked.Exchange(ref _captureInFlight, 0);
            }
        }

        private void LaunchStackCaptureWorker(double stalledSeconds)
        {
            int uiManaged = Volatile.Read(ref _uiManagedThreadId);
            uint uiOs = Volatile.Read(ref _uiOsThreadId);
            var worker = new Thread(() =>
            {
                try
                {
                    string dir = Paths.RoamingAppData("logs");
                    string? path = HangStackCapture.TryCaptureToFile(
                        dir, $"UI thread unresponsive ~{stalledSeconds:F1}s",
                        uiManaged, uiOs, out string? error);
                    if (path is not null)
                        GlobalLogger.Error("UiHangWatchdog",
                            $"All-thread managed stacks captured to '{path}' — the '>>> UI THREAD' section names the blocked frame.");
                    else
                        GlobalLogger.Error("UiHangWatchdog", $"Stack capture failed: {error}");
                }
                catch { /* guarded end-to-end; nothing left to do */ }
                finally { Interlocked.Exchange(ref _captureInFlight, 0); }
            })
            {
                IsBackground = true,
                Name = "UiHangStackCapture",
            };
            worker.Start();
        }

        private void OnHeartbeatServiced()
        {
            // First heartbeat → record which thread the dispatcher lives on, so
            // a later capture can mark it. Plain reads race-free: only this
            // (UI-thread) callback writes, and it writes once.
            if (Volatile.Read(ref _uiOsThreadId) == 0)
            {
                Volatile.Write(ref _uiManagedThreadId, Environment.CurrentManagedThreadId);
                Volatile.Write(ref _uiOsThreadId, GetCurrentThreadId());
            }

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

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
