using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using SysProcess = System.Diagnostics.Process;

namespace Phoenix.Controls.Shared.WinUI;

/// <summary>
/// Cross-pillar pre-UI bootstrap. Each pillar's <c>App.OnLaunched</c> calls
/// <see cref="RunSingleInstanceGuard"/> as the very first line, then awaits
/// <see cref="RunHeavyPreUiAsync"/> off the UI thread once the splash has
/// rendered, so the cross-cutting startup services (AppData migration,
/// icon-font detection, databank init, log writer, localizer,
/// single-instance guard) run before any window is constructed.
///
/// Pre-T15 each WinForms <c>Program.cs</c> ran these in order; the WinUI
/// shells skipped them entirely (TODO.md P0 #4). The migrator + font check
/// + DB init + logger writer are required by every pillar; only the Hub
/// strictly needs Streamer.bot wiring (which lives in
/// <see cref="HubBootstrapper"/> and is Hub-only).
/// </summary>
public static class PillarBootstrap
{
    /// <summary>
    /// Result of the single-instance guard. <see cref="ShouldExit"/> is true
    /// when another instance of this pillar is already running — the App
    /// should foreground that one (best-effort) and exit.
    ///
    /// OWNERSHIP CONTRACT: when
    /// <see cref="OwnedMutex"/> is non-null the caller OWNS that
    /// <see cref="Mutex"/> and MUST dispose it on app exit (the disposing
    /// releases the named OS mutex so the next launch is recognised as the
    /// single-instance owner). The owning <c>App</c> should stash it for the
    /// process lifetime and dispose it from its exit handler
    /// (e.g. <c>Application.Exit</c> / window-closed). This type can't dispose
    /// it for the caller — the mutex must outlive this method for the whole
    /// run. NEEDS CALLER WIRING (App.OnExit): App.xaml.cs is out of scope for
    /// this change, so the disposal site itself is not wired here; this
    /// contract documents the obligation precisely so it can be wired there.
    /// </summary>
    public readonly record struct PreUiResult(bool ShouldExit, Mutex? OwnedMutex);

    // The sync pre-UI form (RunCommonPreUi) was removed
    // because it blocked the UI thread on AppData migration + DB init +
    // localizer load (200-750ms cold) and grep confirmed zero remaining
    // callers. Every pillar now uses the split pair below
    // (RunSingleInstanceGuard first, then RunHeavyPreUiAsync off the UI
    // thread once the splash has rendered).

    /// <summary>
    /// PERF (perf/architect-blockers, BlockerC) — single-instance mutex check
    /// only. Run this as the very first line of App.OnLaunched so a duplicate
    /// launch is rejected before any window is constructed. Cheap (Mutex
    /// construction + a SetForegroundWindow on duplicate); safe to keep
    /// synchronous on the UI thread.
    ///
    /// RESOURCE CONTRACT: on the owner path this
    /// returns <see cref="PreUiResult.OwnedMutex"/> non-null and the CALLER
    /// MUST dispose it on app exit — see the <see cref="PreUiResult"/> docs.
    /// The mutex must be held for the entire process lifetime (disposing it
    /// early would let a second instance start), so it cannot be wrapped in a
    /// <c>using</c> here. The duplicate path and the acquire-failure path both
    /// dispose internally and return a null mutex, so only the owner path
    /// carries the disposal obligation. Until App.OnExit wiring lands (out of
    /// scope for this fix), the named mutex is reclaimed by the OS on process
    /// exit, so this is a managed-handle hygiene leak, not a stuck-lock bug.
    /// </summary>
    public static PreUiResult RunSingleInstanceGuard(string pillarKey)
    {
        if (string.IsNullOrWhiteSpace(pillarKey))
            throw new ArgumentException("pillarKey is required", nameof(pillarKey));

        string mutexName = $"Phoenix.Controls.{pillarKey}.SingleInstance";
        Mutex? mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name: mutexName, out createdNew);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PillarBootstrap", $"mutex '{mutexName}' acquire", ex);
            return new PreUiResult(ShouldExit: false, OwnedMutex: null);
        }

        if (createdNew)
        {
            GlobalLogger.Log($"{pillarKey} pillar starting (single-instance owner).",
                "PillarBootstrap", LogLevel.System);
            return new PreUiResult(ShouldExit: false, OwnedMutex: mutex);
        }

        try { mutex.Dispose(); } catch { /* best effort */ }
        TryForegroundExistingPillar(pillarKey);
        GlobalLogger.Log($"{pillarKey} already running — foregrounded existing instance.",
            "PillarBootstrap", LogLevel.System);
        return new PreUiResult(ShouldExit: true, OwnedMutex: null);
    }

    /// <summary>
    /// PERF (perf/architect-blockers, BlockerC) — heavy I/O bootstrap on a
    /// ThreadPool worker so the splash window has rendered first. Order:
    /// log writer start FIRST so subsequent step failures persist
    /// (), then AppData migrate, icon font resolve,
    /// DB.Initialize, Localizer.Init.
    /// </summary>
    public static Task RunHeavyPreUiAsync(string baseDir)
        => Task.Run(() => RunHeavyPreUiCore(baseDir));

    private static void RunHeavyPreUiCore(string baseDir)
    {
        // Phase instrumentation — the 0.10.x startup regression (~60s vs ~6s
        // pre-0.10) shows a 30s gap between PillarBootstrap's DB-init log and
        // HubBootstrapper's first log line. Stamping each sub-step here lets
        // the next user run pin down which phase swallows the wall clock.
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        GlobalLogger.Log("Startup phase: log-writer-start begin", "PillarBootstrap", LogLevel.System);
        // Step 1 — start the GlobalLogger writer FIRST. The
        // migrator/font/DB steps below all call GlobalLogger.Error on
        // failure; if the writer isn't pumping, those entries sit in the
        // bounded in-memory channel until either the channel evicts them
        // (DropOldest at MAX_QUEUE) or the writer eventually starts and
        // drains — but if the process crashes during one of those steps
        // first-class diagnostics never reach the SQLite Log table.
        // Starting the pump first means every subsequent boot error is
        // persisted. WriteEntryAsync routes through DB.WriteLogDedicatedAsync
        // which lazily opens its own connection on first write, so the
        // writer can run before DB.Instance.Initialize completes — the
        // first write call will block until the DB is up, then proceed.
        _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            () => GlobalLogger.StartLogWriterAsync(),
            "PillarBootstrap", "GlobalLogger.StartLogWriterAsync");
        GlobalLogger.Log($"Startup phase: log-writer-start end (elapsed {sw.ElapsedMilliseconds}ms)", "PillarBootstrap", LogLevel.System);

        sw.Restart();
        GlobalLogger.Log("Startup phase: IconFontResolver begin", "PillarBootstrap", LogLevel.System);
        // Step 3 — icon font detection + one-shot warning.
        try
        {
            IconFontResolver.Init();
            IconFontResolver.ShowMissingFontWarningIfNeeded();
        }
        catch (Exception ex) { GlobalLogger.Error("PillarBootstrap", "IconFontResolver", ex); }
        GlobalLogger.Log($"Startup phase: IconFontResolver end (elapsed {sw.ElapsedMilliseconds}ms)", "PillarBootstrap", LogLevel.System);

        sw.Restart();
        GlobalLogger.Log("Startup phase: DB.Initialize begin", "PillarBootstrap", LogLevel.System);
        // Step 4 — open the SQLite databank.
        try { DB.Instance.Initialize(); }
        catch (Exception ex) { GlobalLogger.Error("PillarBootstrap", "DB.Initialize", ex); }
        GlobalLogger.Log($"Startup phase: DB.Initialize end (elapsed {sw.ElapsedMilliseconds}ms)", "PillarBootstrap", LogLevel.System);

        sw.Restart();
        GlobalLogger.Log("Startup phase: Localizer.Init begin", "PillarBootstrap", LogLevel.System);
        // Step 5 — initialise the UI localizer.
        try { Localizer.Init(baseDir); }
        catch (Exception ex) { GlobalLogger.Error("PillarBootstrap", "Localizer.Init", ex); }
        GlobalLogger.Log($"Startup phase: Localizer.Init end (elapsed {sw.ElapsedMilliseconds}ms)", "PillarBootstrap", LogLevel.System);

        GlobalLogger.Log($"Startup phase: RunHeavyPreUiCore total (elapsed {swTotal.ElapsedMilliseconds}ms)", "PillarBootstrap", LogLevel.System);
    }

    private static void TryForegroundExistingPillar(string pillarKey)
    {
        // Match by exe name. Each pillar's exe is named
        // Phoenix.Controls.<Pillar>.WinUI.exe — Process.MainWindowHandle
        // gives us the HWND to forward focus to.
        string targetName = $"Phoenix.Controls.{pillarKey}.WinUI";
        try
        {
            int self = Environment.ProcessId;
            foreach (var p in SysProcess.GetProcessesByName(targetName))
            {
                try
                {
                    if (p.Id == self) continue;
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    // SetForegroundWindow alone won't un-minimize a
                    // window that's currently iconified in the taskbar — the
                    // user clicks the taskbar entry again and nothing happens.
                    // ShowWindow(SW_RESTORE) lifts the minimize state first so
                    // the SetForegroundWindow call below has a non-minimized
                    // target to bring forward. No-op when the window is
                    // already restored (IsIconic returns false).
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                catch { /* per-process best effort */ }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PillarBootstrap", "TryForegroundExistingPillar", ex);
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
