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

        // Stack-capture state. Each capture writes BOTH a native minidump (.dmp —
        // the priority instrument; every recorded freeze is a NATIVE UI-thread
        // wait ClrMD can't name) and the ClrMD managed all-thread text (.txt).
        // _captureInFlight gates to ONE capture at a time (cross-thread: the
        // watchdog loop launches, the capture worker clears) — if a capture itself
        // ever wedges, further captures are blocked instead of stacking threads.
        // The per-stall budget is two snapshots: one at the trip, one ~20s later,
        // so diffing them separates a live loop from a blocked wait. The session
        // budget is a runaway backstop. Both counters are watchdog-thread-only.
        private int _captureInFlight;
        private int _capturesThisStall;
        private int _capturesThisSession;
        private const int MaxCapturesPerStall = 2;
        private const int MaxCapturesPerSession = 8;
        private static readonly TimeSpan SecondCaptureDelay = TimeSpan.FromSeconds(20);

        // Continuing-stall re-log schedule, in seconds since the stall began:
        // 30s, 60s, then every further 60s. Watchdog-thread-only.
        private double _nextRelogAtSeconds;

        // UI-hang auto-recovery. A confirmed PERMANENT freeze — still
        // unresponsive past the AppConfig.HangAutoRecoveryStallSeconds total —
        // triggers a ONE-shot self-relaunch (spawn a fresh Hub, hard-kill this
        // wedged one) via HangRecoveryLauncher, gated on
        // AppConfig.HangAutoRecoveryEnabled and capped per-window there so a
        // deterministic re-freeze can't fork-bomb. The threshold is read LIVE
        // (see GetRelaunchAfter) and clamped just above the trip so it always
        // lands on a confirmed freeze; 12s default — observed freezes never
        // return to usable, so healing fast beats making the user sit through
        // it and close it themselves (~14s). Latched per-stall (reset on a new
        // stall) so a suppressed/failed attempt doesn't retry every poll; the
        // relaunch runs on its own thread so the poll loop keeps re-logging
        // while it captures a final dump and spawns. Watchdog-thread-only.
        private bool _recoveryInitiated;
        private const int DefaultRelaunchAfterSeconds = 12;

        // Window scanned in the Windows System event log for a display-driver
        // reset (TDR) near the freeze — the smoking gun for the GPU-stall class.
        private static readonly TimeSpan TdrScanWindow = TimeSpan.FromMinutes(5);

        // Sub-trip "soft hitch": a shorter stall that won't trigger the full
        // capture but still leaves a breadcrumb, so brief freezes (the kind that
        // recover before the 8s trip and otherwise leave NO trace at all) are
        // visible. Logged once per unresponsive period; latch reset the moment a
        // heartbeat is serviced. Watchdog-thread + UI-callback.
        private volatile bool _softHitchLogged;
        private static readonly TimeSpan SoftHitchThreshold = TimeSpan.FromSeconds(4);

        // Periodic resource-usage breadcrumb (Majo 2026-07-23: "include resource
        // usage logs — especially on hang-logs"). A Debug-level line every 5 min
        // lands in the log ring (and therefore in a freeze report's breadcrumb
        // tail) so the lead-up trend into a freeze is visible, not just the
        // at-capture sample. Runs on the watchdog thread; the ~0.5s CPU/GPU
        // sample window merely delays one poll tick. Suppressed while a stall is
        // being reported (captures own the thread's attention then).
        // Watchdog-thread-only.
        private static readonly TimeSpan ResourceBreadcrumbInterval = TimeSpan.FromMinutes(5);
        private DateTime _nextResourceBreadcrumbUtc = DateTime.UtcNow + TimeSpan.FromMinutes(1);

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
                MaybeLogResourceBreadcrumb();

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

                    // Sub-trip soft hitch — record a brief stall that won't reach
                    // the full-capture threshold, so short freezes (which recover
                    // before the 8s trip and otherwise leave NO trace) are still
                    // visible. Once per unresponsive period.
                    if (!_softHitchLogged && waited >= SoftHitchThreshold && waited < _stallThreshold)
                    {
                        _softHitchLogged = true;
                        GlobalLogger.Log(
                            $"UI thread slow to respond (~{waited.TotalSeconds:F1}s) — a brief hitch below the " +
                            $"{_stallThreshold.TotalSeconds:F0}s freeze-capture threshold. Last traced activity: " +
                            $"'{UiActivityTrace.LastActivity ?? "(none)"}'. Resources: {ResourceUsageProbe.TryFormatInstantLine()}.",
                            "UiHangWatchdog", LogLevel.System);
                    }

                    if (waited < _stallThreshold) return;

                    if (!_reported)
                    {
                        _reported = true;
                        _capturesThisStall = 0;
                        _recoveryInitiated = false;
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
                            $"Resources: {ResourceUsageProbe.TryFormatInstantLine()}. " +
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

                        // Confirmed permanent freeze → one-shot auto-relaunch.
                        if (!_recoveryInitiated && waited >= GetRelaunchAfter())
                            TryInitiateRecovery(waited.TotalSeconds);
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
                // Opt-in deep dump: ONLY the first capture of a stall may write
                // the multi-GB full-memory variant; the ~20s second snapshot and
                // the pre-relaunch capture stay lightweight, so one stall costs
                // at most one big file. Config read live (Settings flip applies
                // without restart, like the sibling hang settings).
                bool fullMemory = _capturesThisStall == 0 && ReadFullMemoryDumpConfig();
                LaunchStackCaptureWorker(stalledSeconds, fullMemory);
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

        private static bool ReadFullMemoryDumpConfig()
        {
            try { return ConfigManager.Current.HangFullMemoryDump; }
            catch { return false; }
        }

        private void LaunchStackCaptureWorker(double stalledSeconds, bool fullMemory)
        {
            int uiManaged = Volatile.Read(ref _uiManagedThreadId);
            uint uiOs = Volatile.Read(ref _uiOsThreadId);
            var worker = new Thread(() =>
            {
                try
                {
                    if (fullMemory) _fullMemoryCaptureInFlight = true;
                    WriteFullFreezeDiagnostics(
                        $"UI thread unresponsive ~{stalledSeconds:F1}s", stalledSeconds, uiManaged, uiOs, fullMemory);
                }
                catch { /* guarded end-to-end; nothing left to do */ }
                finally
                {
                    _fullMemoryCaptureInFlight = false;
                    Interlocked.Exchange(ref _captureInFlight, 0);
                }
            })
            {
                IsBackground = true,
                Name = "UiHangStackCapture",
            };
            worker.Start();
        }

        // True while a capture worker is writing the (multi-GB, tens-of-seconds)
        // full-memory dump. The auto-relaunch consults this to WAIT for the dump
        // instead of hard-killing the process mid-write — the deep dump is the
        // entire point of arming HangFullMemoryDump, and a TerminateProcess runs
        // none of the writer's cleanup. Volatile: capture worker writes,
        // recovery worker reads.
        private volatile bool _fullMemoryCaptureInFlight;
        private static readonly TimeSpan FullDumpGateBudget = TimeSpan.FromSeconds(120);

        // Periodic Debug-level resource breadcrumb — lands in the log ring (and
        // therefore in every freeze report's breadcrumb tail) so the trend INTO
        // a freeze is on record. Skipped while a stall is being reported: the
        // at-capture sample in WriteFullFreezeDiagnostics owns that moment.
        private void MaybeLogResourceBreadcrumb()
        {
            if (_reported) return;
            var now = DateTime.UtcNow;
            if (now < _nextResourceBreadcrumbUtc) return;
            _nextResourceBreadcrumbUtc = now + ResourceBreadcrumbInterval;
            try
            {
                var sample = ResourceUsageProbe.TrySample();
                string architect = PillarDiagnostics.ArchitectStatus is string s
                    ? $" · Architect {s}"
                    : string.Empty;
                GlobalLogger.Log(
                    $"Resource usage — {ResourceUsageProbe.FormatLine(sample)}{architect}",
                    "UiHangWatchdog", LogLevel.Debug);
            }
            catch { /* breadcrumb is best-effort */ }
        }

        // The full freeze-diagnostics bundle written on a confirmed stall (and
        // again just before an auto-relaunch): native minidump, ClrMD all-thread
        // text, and the consolidated FREEZE REPORT that synthesizes a plain-
        // language LIKELY CAUSE from the UI-thread frames + a Windows display-TDR
        // scan + the loaded GPU driver + the last log breadcrumbs. Returns the
        // native dump path for the relaunch handoff. Runs on the caller's
        // background thread; every step is independently guarded so one failure
        // still lets the rest write. The dump + ClrMD snapshot run sequentially
        // (dump first) so two process readers never touch the frozen app at once.
        private string? WriteFullFreezeDiagnostics(string reason, double stalledSeconds, int uiManaged, uint uiOs, bool fullMemoryDump = false)
        {
            string dir = Paths.RoamingAppData("logs");

            // 1. Native minidump FIRST — the priority instrument; its native
            //    UI-thread stack names the blocking module in WinDbg. With
            //    AppConfig.HangFullMemoryDump armed, the stall's first capture
            //    writes the full-memory variant (heap included → DispatcherQueue
            //    / XAML-core state becomes inspectable); NativeMiniDump falls
            //    back to the lightweight dump when disk space can't take it.
            string? dumpPath = NativeMiniDump.TryWriteToFile(dir, fullMemoryDump, out bool wroteFullMemory, out string? dumpError);
            if (dumpPath is not null)
            {
                string kind = wroteFullMemory
                    ? "FULL-MEMORY minidump (heap included — DispatcherQueue/XAML state inspectable)"
                    : fullMemoryDump
                        ? "Native minidump (full-memory was requested but fell back — likely insufficient free disk)"
                        : "Native minidump";
                GlobalLogger.Error("UiHangWatchdog",
                    $"{kind} written to '{dumpPath}' (UI stall ~{stalledSeconds:F1}s) — open in WinDbg/VS.");
            }
            else
                GlobalLogger.Error("UiHangWatchdog", $"Native minidump failed: {dumpError}");

            // 1b. Resource sample (~0.5s window) right after the dump — the
            //     at-capture CPU / RAM / GPU truth for the report and the live
            //     log line (Majo: "include resource usage — especially on
            //     hang-logs"). Background thread; the frozen UI is unaffected.
            ResourceUsageProbe.Snapshot? resources = null;
            try { resources = ResourceUsageProbe.TrySample(); }
            catch { /* section prints unavailable */ }

            // 2. ClrMD structured capture SECOND (one snapshot → text + UI frames).
            string? textPath = null;
            IReadOnlyList<string> uiFrames = Array.Empty<string>();
            try
            {
                var cap = HangStackCapture.Capture(reason, uiManaged, uiOs);
                uiFrames = cap.UiThreadFrames;
                textPath = HangStackCapture.TryWriteText(dir, cap.Text, out string? textError);
                if (textPath is not null)
                    GlobalLogger.Error("UiHangWatchdog",
                        $"All-thread managed stacks captured to '{textPath}'.");
                else
                    GlobalLogger.Error("UiHangWatchdog", $"Stack capture write failed: {textError}");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UiHangWatchdog", "ClrMD stack capture failed", ex);
            }

            // 3. Consolidated freeze report: LIKELY CAUSE + GPU/TDR + breadcrumbs.
            try
            {
                var tdr = GpuTdrProbe.RecentDisplayResets(TdrScanWindow);
                var gpu = GpuTdrProbe.LoadedGraphicsModules();
                var breadcrumbs = GlobalLogger.GetRecentLogs();

                string lastActivity = UiActivityTrace.LastActivity ?? "(none traced)";
                var lastStart = UiActivityTrace.LastActivityStartedAtUtc;
                string age = lastStart == DateTime.MinValue
                    ? "n/a" : $"{(DateTime.UtcNow - lastStart).TotalSeconds:F1}s ago";
                int openDepth = UiActivityTrace.OpenScopeDepth;
                string scopeState = openDepth > 0
                    ? $"scope STILL OPEN (depth {openDepth}) — this activity is the blocker"
                    : "scope already CLOSED — stall is in uninstrumented code after it";

                var ctx = new FreezeReport.Context(
                    stalledSeconds, _stallThreshold.TotalSeconds, lastActivity, age, scopeState, uiManaged, uiOs);

                string? resourceSection = null;
                try { if (resources is not null) resourceSection = ResourceUsageProbe.FormatReportSection(resources); }
                catch { /* section prints unavailable */ }

                // Architect status is published from a Low-priority UI-thread
                // lambda, which cannot run while the UI is wedged — so what we
                // read here can predate the freeze by minutes. Annotate its AGE
                // so the report reader can weigh it (a 2s-old "graph-bind: 183
                // nodes" is a lead; a 20-min-old one is just ambient context).
                string? architectStatus = PillarDiagnostics.ArchitectStatus;
                if (architectStatus is not null)
                {
                    var publishedAt = PillarDiagnostics.ArchitectStatusAtUtc;
                    architectStatus += publishedAt == DateTime.MinValue
                        ? " (age unknown)"
                        : $" (published {(DateTime.UtcNow - publishedAt).TotalSeconds:F0}s before this capture)";
                }

                string cause = FreezeReport.SynthesizeLikelyCause(uiFrames, tdr);
                string report = FreezeReport.Build(ctx, uiFrames, breadcrumbs, gpu, tdr, TdrScanWindow, dumpPath, textPath,
                    resourceUsage: resourceSection,
                    architectStatus: architectStatus);
                string? reportPath = FreezeReport.TryWriteToFile(dir, report, out string? repError);

                // GPU-pressure note on the LIVE line — the "graphics card was
                // quite in use" observation, decided per freeze from data. This
                // stays a NOTE beside the synthesized cause (the dump shows a
                // GetMessage wait, so GPU load is an aggravator hypothesis, not
                // a proven present-block).
                string gpuNote = string.Empty;
                if (resources?.GpuTotalPercent is double gpuTotal && gpuTotal >= 90.0)
                {
                    string proc = resources.GpuProcessPercent is double p
                        ? $", ~{p:F0}% from Phoenix" : string.Empty;
                    gpuNote = $" | GPU under heavy load at capture (~{gpuTotal:F0}% machine-wide engine-sum{proc}).";
                }

                // 4. Prominent in-app System Log entry — the synthesized cause +
                //    where the full report lives, so the freeze explains itself live.
                GlobalLogger.Error("UiHangWatchdog",
                    $"FREEZE likely cause — {cause}{gpuNote}" +
                    (reportPath is not null
                        ? $" | Full report: {reportPath}"
                        : $" | (report write failed: {repError})"));
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UiHangWatchdog", "Freeze report build failed", ex);
            }

            return dumpPath;
        }

        // Effective total-freeze deadline before auto-relaunch, read LIVE from
        // AppConfig (a Settings change applies without a restart) and clamped
        // just above the trip threshold so it always lands on a CONFIRMED freeze,
        // and below an absurd ceiling. Falls back to the default on any fault.
        private TimeSpan GetRelaunchAfter()
        {
            int secs = DefaultRelaunchAfterSeconds;
            try
            {
                int cfg = ConfigManager.Current.HangAutoRecoveryStallSeconds;
                if (cfg > 0) secs = cfg;
            }
            catch { /* fall back to the default */ }
            double min = _stallThreshold.TotalSeconds + 1;   // never before a confirmed trip
            return TimeSpan.FromSeconds(Math.Clamp(secs, min, 600));
        }

        // Confirmed permanent freeze → one-shot self-relaunch. The actual
        // relaunch runs on its own thread so the poll loop keeps re-logging; a
        // final dump+text is captured synchronously first (the scheduled 2nd
        // capture may not have fired by the 25s relaunch point) and serialized
        // against any in-flight normal capture via the _captureInFlight gate so
        // two process snapshots never touch the frozen process at once.
        private void TryInitiateRecovery(double stalledSeconds)
        {
            // Config gate — read live so a Settings flip applies without a
            // restart; fail safe to the default-on behaviour if the read throws.
            bool enabled;
            try { enabled = ConfigManager.Current.HangAutoRecoveryEnabled; }
            catch { enabled = true; }
            if (!enabled) return;

            _recoveryInitiated = true;

            int uiManaged = Volatile.Read(ref _uiManagedThreadId);
            uint uiOs = Volatile.Read(ref _uiOsThreadId);

            var worker = new Thread(() =>
            {
                bool claimed = false;
                try
                {
                    GlobalLogger.Error("UiHangWatchdog",
                        $"UI thread frozen ~{stalledSeconds:F0}s with no recovery — initiating auto-relaunch " +
                        "(AppConfig.HangAutoRecoveryEnabled=true).");

                    string? dumpPath = null;

                    // Take the one-at-a-time capture gate (bounded wait) so the
                    // pre-relaunch snapshot is alone on the process. If a normal
                    // capture is already in flight (it's writing its own dump),
                    // skip ours rather than run a second concurrent snapshot.
                    // EXCEPTION: an in-flight FULL-MEMORY dump (opt-in,
                    // multi-GB, tens of seconds) gets a generous wait — killing
                    // the process mid-write would truncate the one artifact the
                    // user explicitly armed this freeze hunt for. The relaunch
                    // resumes the moment the gate frees.
                    TimeSpan gateBudget = TimeSpan.FromSeconds(3);
                    if (_fullMemoryCaptureInFlight)
                    {
                        gateBudget = FullDumpGateBudget;
                        GlobalLogger.Error("UiHangWatchdog",
                            $"Auto-relaunch is WAITING (up to {FullDumpGateBudget.TotalSeconds:F0}s) for the in-flight " +
                            "full-memory hang dump to finish (AppConfig.HangFullMemoryDump=true) — relaunching " +
                            "immediately would truncate it.");
                    }
                    claimed = TryClaimCaptureGate(gateBudget);
                    if (claimed)
                    {
                        dumpPath = WriteFullFreezeDiagnostics(
                            $"UI thread unresponsive ~{stalledSeconds:F1}s (pre-relaunch)",
                            stalledSeconds, uiManaged, uiOs);
                    }
                    else
                    {
                        GlobalLogger.Error("UiHangWatchdog",
                            "Pre-relaunch capture skipped — a capture is already in flight; relaunching.");
                    }

                    // Loop-guard, spawn a fresh Hub, hard-kill self. Returns only
                    // when suppressed by the restart-loop cap or a spawn failure
                    // (on success this process is already terminated).
                    HangRecoveryLauncher.Relaunch(dumpPath);
                }
                catch (Exception ex)
                {
                    try { GlobalLogger.Error("UiHangWatchdog", "auto-relaunch worker failed", ex); }
                    catch { /* dying process — nothing to do */ }
                }
                finally
                {
                    // Release the gate only on the survive path (suppressed /
                    // failed relaunch); on success the process is already gone.
                    if (claimed) Interlocked.Exchange(ref _captureInFlight, 0);
                }
            })
            {
                IsBackground = true,
                Name = "UiHangRecovery",
            };
            worker.Start();
        }

        // Try to take the one-at-a-time capture gate within <paramref name="budget"/>,
        // so the pre-relaunch capture never runs concurrently with a normal
        // capture worker (two process snapshots at once risk a cross-tool suspend
        // / loader-lock stall on the frozen process). Returns true if claimed.
        private bool TryClaimCaptureGate(TimeSpan budget)
        {
            var deadline = DateTime.UtcNow + budget;
            while (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0)
            {
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(50);
            }
            return true;
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
            // A serviced heartbeat ends any unresponsive period — re-arm the
            // soft-hitch breadcrumb for the next one.
            _softHitchLogged = false;
            _outstanding = false;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
