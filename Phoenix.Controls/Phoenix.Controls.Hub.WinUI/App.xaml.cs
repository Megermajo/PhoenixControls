using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Dialogs;
using Phoenix.Controls.Hub.WinUI.Panels;
using Phoenix.Controls.Hub.WinUI.Services;
using Phoenix.Controls.Hub.WinUI.Windows;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI;
using Phoenix.Controls.Shared.WinUI.Contracts;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.WinUI;

public partial class App : Application
{
    private SplashWindow? _splash;
    private MainWindow? _main;
    private IHubServices? _services;
    private Mutex? _instanceMutex;
    private UiHangWatchdog? _hangWatchdog;

    public App()
    {
        // Close the WinUI text-input (IME/TSF) nested-message-loop freeze BEFORE
        // any window exists: ImmDisableIME must run before the first top-level
        // window's WM_CREATE, and the App constructor is the earliest UI-thread
        // point. Gated by AppConfig.DisableImeInput (default true). See ImeGuard.
        ImeGuard.DisableImeInputIfConfigured();

        // Relocate WebView2's user-data folder OUT of the install tree before
        // anything can spin up a WebView2 (the in-app doc/changelog viewer, the
        // Visualist preview panels). Must run before the first CoreWebView2 init;
        // the constructor is the earliest safe point.
        ConfigureWebView2UserDataFolder();

        InitializeComponent();

        // Start the on-disk diagnostic log first thing so even a pre-MainWindow
        // boot fault (and the crash safety net below) lands in a file that
        // survives without the SQLite SystemHistory table being readable.
        try { DiagnosticFileLog.Start(); } catch { /* never block startup */ }

        // Global crash safety net — without these the WinUI dispatcher
        // crashes silently when an exception escapes OnLaunched's
        // try/catch, or when a fire-and-forget Task faults without an
        // awaiter. Log everything to GlobalLogger so System Log keeps a
        // record even when the UI can't render the failure (e.g. boot
        // fault before MainWindow is up).
        this.UnhandledException += (_, e) =>
        {
            try
            {
                GlobalLogger.Error("App.UnhandledException",
                    e.Message ?? "(no message)", e.Exception);
            }
            catch { /* logger itself faulted — nothing useful to do */ }
            // Keep the process up; a partial UI beats a silent crash.
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                try { GlobalLogger.Error("AppDomain.UnhandledException", "fatal", ex); }
                catch { }
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                GlobalLogger.Error("TaskScheduler.UnobservedTaskException",
                    "unobserved task fault", e.Exception);
                e.SetObserved();
            }
            catch { }
        };
    }

    /// <summary>
    /// Point every in-process WebView2 at a user-data folder under
    /// <c>%LocalAppData%\PhoenixControls\Hub\WebView2</c> instead of its default
    /// location next to the exe (<c>{installDir}\…Hub.WinUI.exe.WebView2\</c>).
    ///
    /// <para>Why it matters for the auto-updater: the default folder sits INSIDE
    /// the install tree, and the <c>msedgewebview2.exe</c> browser children keep
    /// it open. When a Hub session ends abnormally (a freeze-recovery hard-kill,
    /// or an abrupt <c>Environment.Exit</c>) those children are orphaned and keep
    /// the tree locked, so the next update's <c>Directory.Move(installRoot →
    /// .bak)</c> fails until the PC is rebooted. Moving the folder to LocalAppData
    /// means an orphan can only ever lock scratch state the swap never touches —
    /// and same-folder WebView2 hosts share one browser process, so the orphan is
    /// reused rather than blocking. Mirrors what the standalone Viewer already
    /// does via an explicit environment.</para>
    ///
    /// <para>Set through the <c>WEBVIEW2_USER_DATA_FOLDER</c> environment variable
    /// so it applies to every <c>EnsureCoreWebView2Async()</c> call in the process
    /// without threading an explicit environment through each host. Best-effort:
    /// a failure here just leaves WebView2 on its default folder — never blocks
    /// startup. An operator-supplied value is respected (not overwritten).</para>
    /// </summary>
    private static void ConfigureWebView2UserDataFolder()
    {
        try
        {
            const string EnvVar = "WEBVIEW2_USER_DATA_FOLDER";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)))
                return; // respect an explicit override

            string udf = Phoenix.Controls.Shared.Core.Paths.LocalAppData(
                Path.Combine("Hub", "WebView2"));
            try { Directory.CreateDirectory(udf); } catch { /* WebView2 creates it if we can't */ }
            Environment.SetEnvironmentVariable(EnvVar, udf);
        }
        catch (Exception ex)
        {
            try { GlobalLogger.Error("App", "ConfigureWebView2UserDataFolder", ex); }
            catch { /* logger not up yet — ignore */ }
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // async void: an unhandled exception escaping this method tears down
        // the dispatcher silently — the user sees the splash hang and the
        // process exit with no diagnostic. Wrap the entire body so anything
        // outside the existing per-section try/catch surfaces through
        // GlobalLogger.Error (and, if possible, the splash error gate)
        // instead of crashing the process invisibly.
        try
        {
            await OnLaunchedCore(args).ConfigureAwait(true);
        }
        catch (Exception fatal)
        {
            try { GlobalLogger.Error("App", "OnLaunched fatal", fatal); }
            catch { /* logger may itself be dead during teardown */ }
            try
            {
                if (_splash is not null)
                    _splash.ShowError($"Hub startup failed: {fatal.Message}");
            }
            catch { /* best-effort */ }
        }
    }

    private async Task OnLaunchedCore(LaunchActivatedEventArgs args)
    {
        // Pre-cache this was a single
        // synchronous RunCommonPreUi (AppData migrator + icon-font detect +
        // DB.Initialize + log writer + Localizer.Init + mutex) running before
        // the splash was shown — 200-750ms of invisible startup on cold disk,
        // user sees nothing and may double-click to launch again. Now we do
        // the mutex check first (cheap), show the splash, then run the heavy
        // I/O on a worker while the splash is already painting.

        // UI-hang auto-recovery handoff. When the prior (frozen) instance
        // relaunched us it passes --recovered-relaunch=<oldPid>; wait for that
        // process to fully exit BEFORE the single-instance guard runs — its
        // still-held mutex would otherwise make us foreground the dying window
        // and exit, leaving nothing running. Detected here (pre-guard); the
        // user-facing "recovered from a freeze" notice is logged after the main
        // window is up (below).
        var recovery = Phoenix.Controls.Hub.Core.HangRecoveryLauncher
            .DetectRecoveredRelaunch(Environment.GetCommandLineArgs());
        if (recovery is { } rec)
        {
            GlobalLogger.Log(
                $"Launched as a hang-recovery relaunch (old pid {rec.OldPid}); waiting for it to exit " +
                "before the single-instance guard.",
                "App", LogLevel.System);
            Phoenix.Controls.Hub.Core.HangRecoveryLauncher.WaitForOldInstanceExit(
                rec.OldPid, TimeSpan.FromSeconds(10));
        }

        // Step 1 — single-instance guard (sync, fast). Must happen before
        // splash creation so a duplicate launch doesn't flash a splash and
        // then exit.
        var pre = PillarBootstrap.RunSingleInstanceGuard("Hub");
        if (pre.ShouldExit)
        {
            Exit();
            return;
        }
        _instanceMutex = pre.OwnedMutex;

        // Step 2 — show splash immediately.
        _splash = new SplashWindow();
        _splash.Activate();

        // Step 3 — heavy pre-UI bootstrap on ThreadPool, splash visible.
        // Awaited so HubBootstrapper.BootAsync below sees a ready DB +
        // localizer + log writer.
        //
        // Catastrophic-failure gate. PillarBootstrap.RunHeavyPreUiCore
        // wraps each of its five sub-steps (log writer, AppData migrator,
        // icon-font resolver, DB.Initialize, Localizer.Init) in its own
        // try/catch — the migrator / icon font / localizer faults are
        // recoverable on a fresh install, so swallowing them lets boot
        // continue. But DB.Initialize is a hard dependency: without an open
        // SQLite connection HubBootstrapper builds half-broken services
        // (ScriptManager loads no `Vars`, GlobalLogger writer can't persist,
        // every script that touches the databank faults at runtime) and the
        // user sees a chrome-only MainWindow with no diagnostic.
        //
        // After awaiting RunHeavyPreUiAsync, re-call DB.Instance.Initialize
        // explicitly. The DB singleton is idempotent — on success this is a
        // no-op against the already-open connection (the lock-guarded
        // `_connection.State == Open` early-out at DB.cs:103 returns
        // immediately). On a swallowed prior failure, _connection stays null
        // and the second attempt re-throws here, which escapes the inner
        // try/catch via `throw;` and lands in OnLaunched's outer catch,
        // routing the user to _splash.ShowError instead of a silent
        // chrome-only MainWindow.
        var swStartup = System.Diagnostics.Stopwatch.StartNew();
        var swPhase = System.Diagnostics.Stopwatch.StartNew();
        GlobalLogger.Log("Startup phase: RunHeavyPreUiAsync begin", "App", LogLevel.System);
        try
        {
            await PillarBootstrap.RunHeavyPreUiAsync(AppContext.BaseDirectory)
                .ConfigureAwait(true);

            DB.Instance.Initialize();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("App", "RunHeavyPreUiAsync failed", ex);
            // Re-throw so OnLaunched's outer try/catch surfaces
            // the failure on the splash. Pre-fix the catch swallowed the
            // exception and fell through to MainWindow construction with a
            // null DB connection.
            throw;
        }
        GlobalLogger.Log($"Startup phase: RunHeavyPreUiAsync end (elapsed {swPhase.ElapsedMilliseconds}ms)", "App", LogLevel.System);

        swPhase.Restart();
        GlobalLogger.Log("Startup phase: Splash.RefreshLocalizedStrings begin", "App", LogLevel.System);
        // Re-resolve the splash's localized strings now that
        // Localizer.Init (step 5 of RunHeavyPreUiAsync) has completed.
        // The constructor seeded BootEyebrow from the XAML literal because
        // a Localizer.T call before Init always returns the English
        // fallback; this re-set picks up the DE/FR/ES bundle value when
        // one exists. Best-effort: a failure here just leaves the English
        // seed text in place.
        try { _splash.RefreshLocalizedStrings(); }
        catch (Exception ex) { GlobalLogger.Error("App", "Splash.RefreshLocalizedStrings", ex); }
        GlobalLogger.Log($"Startup phase: Splash.RefreshLocalizedStrings end (elapsed {swPhase.ElapsedMilliseconds}ms)", "App", LogLevel.System);

        // ── Terms of Service consent gate ────────────────────────────────
        // Fail-closed, by construction. This runs BEFORE HubBootstrapper.BootAsync,
        // so while the ToS pop-up is open NOTHING operational has started — no
        // Streamer.bot connection (BootAsync step 5), no on_startup scripts
        // (step 7), no HUD server, no scheduler, and no MainWindow. The app is
        // genuinely non-functional until the user accepts. Declining, closing the
        // pop-up, or any failure to obtain consent exits the process before a
        // single service comes up ("the safe way"). The gate re-appears whenever
        // TermsOfServiceGate.CurrentVersion exceeds the user's stored
        // AcceptedTosVersion — i.e. a fresh install, or the terms were revised
        // and the constant was bumped. EnsureAcceptedAsync loads config itself
        // (config is otherwise first loaded inside BootAsync), checks the version,
        // shows the pop-up when needed, and persists acceptance synchronously.
        bool tosAccepted;
        try
        {
            tosAccepted = await TermsOfServiceGate.EnsureAcceptedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // EnsureAcceptedAsync is already fail-closed internally; this outer
            // guard covers anything truly unexpected escaping it.
            GlobalLogger.Error("App", "Terms of Service gate threw", ex);
            tosAccepted = false;
        }
        if (!tosAccepted)
        {
            // Consent not given — tear down the splash and exit before boot.
            try { _splash?.Close(); } catch { /* best-effort */ }
            _splash = null;
            Exit();
            return;
        }

        // Minimum splash duration so the phoenix-mark rise animation
        // (2.4s) gets a chance to complete on warm-disk machines where
        // BootAsync can finish in ~300ms. Without this the splash closes
        // mid-animation and the mark never fully fades in — pre-fix it
        // looked like a glitch rather than an intentional intro. Timer
        // starts here and is awaited after BootAsync; total wait is
        // max(boot, MinSplashMs).
        const int MinSplashMs = 1500;
        var minSplashTask = Task.Delay(MinSplashMs);

        swPhase.Restart();
        GlobalLogger.Log("Startup phase: DispatcherQueueUiDispatcher ctor begin", "App", LogLevel.System);
        var dispatcher = new DispatcherQueueUiDispatcher(_splash.DispatcherQueue);
        GlobalLogger.Log($"Startup phase: DispatcherQueueUiDispatcher ctor end (elapsed {swPhase.ElapsedMilliseconds}ms)", "App", LogLevel.System);

        swPhase.Restart();
        GlobalLogger.Log("Startup phase: DeferredPillarNavigator ctor begin", "App", LogLevel.System);
        // Navigator is deferred — the real implementation (MainWindow) can't
        // exist until BootAsync completes, but ScriptHostMonitor needs the
        // contract surface from BootAsync onwards. Real navigator is attached
        // post-BootAsync, before any user interaction can route through it.
        var navigator = new DeferredPillarNavigator();
        GlobalLogger.Log($"Startup phase: DeferredPillarNavigator ctor end (elapsed {swPhase.ElapsedMilliseconds}ms)", "App", LogLevel.System);

        swPhase.Restart();
        GlobalLogger.Log($"Startup phase: HubBootstrapper.BootAsync begin (T+{swStartup.ElapsedMilliseconds}ms since OnLaunchedCore startup-stopwatch reset)", "App", LogLevel.System);
        Exception? bootFailure = null;
        try
        {
            _services = await HubBootstrapper.BootAsync(dispatcher, navigator, _splash.Progress)
                .ConfigureAwait(true);
            GlobalLogger.Log($"Startup phase: HubBootstrapper.BootAsync end (elapsed {swPhase.ElapsedMilliseconds}ms)", "App", LogLevel.System);
        }
        catch (Exception ex)
        {
            bootFailure = ex;
            GlobalLogger.Error("App", "HubBootstrapper.BootAsync failed", ex);
            System.Diagnostics.Debug.WriteLine($"HubBootstrapper.BootAsync failed: {ex}");
        }

        // Honour the minimum splash duration regardless of boot outcome
        // — the error overlay benefits from the same intentional intro
        // beat as the success path. Wrapped in try because a stalled
        // dispatcher shouldn't block the boot-failure surface forever.
        try { await minSplashTask.ConfigureAwait(true); }
        catch { /* delay was cancelled / dispatcher torn down */ }

        // Boot-failure gate: if
        // services didn't materialise — either BootAsync threw, or the
        // bootstrapper completed with a null result — keep the splash up
        // in error mode and skip MainWindow activation entirely. Without
        // this gate the user saw a chrome-only window with no panels and
        // no diagnostic.
        if (_services is null)
        {
            string detail = bootFailure?.Message ?? "Hub services were not initialised.";
            _splash.ShowError(detail);
            // Splash stays open until the user clicks Close (which calls
            // Application.Exit). Don't activate MainWindow.
            return;
        }

        // SetPanels BEFORE Activate so the first
        // frame paints with content. Old order activated an empty
        // MainPaneRegion, then injected panels — users saw splash → un-themed
        // window → panels light up. Defer splash close until the first
        // MainWindow Activated fire so the splash mask survives until the
        // window has actually painted its first frame.
        //
        // Wrap the MainWindow construction + SetPanels +
        // Activate + splash-handoff block in its own try so a fault here —
        // e.g. PanelFactory throwing during a panel ctor, or the
        // splash-Activated wiring failing — surfaces through GlobalLogger
        // and the splash error gate instead of falling through to the outer
        // OnLaunched catch (which already handles it, but at a coarser
        // granularity that doesn't distinguish "boot succeeded, UI panel
        // failed" from "boot itself failed"). The same outer try still
        // catches anything escaping this inner block.
        try
        {
            _main = new MainWindow();
            navigator.Attach(_main);
            _main.SetPanels(_services, new PanelFactory());
            // Connect both design-time pillar bus links
            // to the Hub IPC bus now that BootAsync has bound the server. They
            // run for the whole session (stopped only at app close) so OBS
            // transmissions + Architect live-debug keep flowing regardless of
            // which pillar tab is open — Majo: "the BUS MUST run even when not in
            // Visualist".
            _main.StartPillarBusLinks();
            _main.Activate();

            // Keep _splash assigned past this block so the
            // outer OnLaunched catch — and the inline try/catch wrappers
            // around --open, the welcome dialog, and the UpdateChecker
            // sentinel surface below — can still funnel a fault through
            // the splash error gate. Pre-fix _splash was nulled here,
            // which meant any throw in the post-MainWindow steps reached
            // the outer catch with a null reference and no user-visible
            // diagnostic. The Loaded/Activated handler below nulls
            // _splash itself once the close actually happens, so neither
            // path leaks a stale window reference.
            var splash = _splash;
            if (splash is not null)
            {
                // Defer splash close until MainWindow's
                // content visual tree has actually finished its first
                // arrange/measure pass. Activated fires early in the WinUI 3
                // lifecycle — on some hardware the splash dismissed before
                // MainPaneRegion painted any content, producing a sub-second
                // flash of empty chrome. Loaded is the canonical "first frame
                // realised" signal, so the splash mask survives long enough to
                // mask the un-themed gap entirely. Activated stays as a
                // defence-in-depth fallback in case Loaded never fires.
                global::Windows.Foundation.TypedEventHandler<object, Microsoft.UI.Xaml.WindowActivatedEventArgs>? closeOnActivatedFallback = null;
                RoutedEventHandler? closeOnContentLoaded = null;
                int closed = 0;

                void CloseOnce()
                {
                    if (System.Threading.Interlocked.Exchange(ref closed, 1) != 0) return;
                    if (closeOnActivatedFallback is not null) _main.Activated -= closeOnActivatedFallback;
                    if (closeOnContentLoaded is not null && _main.Content is FrameworkElement felDetach)
                        felDetach.Loaded -= closeOnContentLoaded;
                    // Null _splash from inside CloseOnce so the
                    // outer catch only loses its handle once the window has
                    // actually been dismissed — not at the point we wired
                    // the handlers. ReferenceEquals guards a re-entrant
                    // ShowError(...) that may already have swapped _splash
                    // to a different window.
                    if (ReferenceEquals(_splash, splash)) _splash = null;
                    try { splash.Close(); } catch { /* shutdown best-effort */ }
                }

                if (_main.Content is FrameworkElement fel)
                {
                    if (fel.IsLoaded)
                    {
                        CloseOnce();
                    }
                    else
                    {
                        closeOnContentLoaded = (_, _) => CloseOnce();
                        fel.Loaded += closeOnContentLoaded;
                    }
                }

                if (closed == 0)
                {
                    closeOnActivatedFallback = (_, _) => CloseOnce();
                    _main.Activated += closeOnActivatedFallback;
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("App", "MainWindow/SetPanels/Activate failed", ex);
            try { _splash?.ShowError($"Hub UI bring-up failed: {ex.Message}"); }
            catch { /* splash may already be torn down */ }
            // Re-throw so the outer OnLaunched gate keeps the splash in error
            // mode and the process doesn't continue with a half-initialised
            // MainWindow. The outer catch already routes to GlobalLogger +
            // splash; the extra context above identifies the specific phase.
            throw;
        }

        // Surface yesterday's Updater outcome through System Log — the
        // Updater writes %AppData%/PhoenixControls/Hub/last-update-result.json
        // on success/rollback/failure and Hub had been ignoring it. Read &
        // clear here so the entry appears once per launch, not every time.
        try
        {
            // Clear any Hub-exit sentinel left over from a crashed prior run.
            // If the recorded PID is no longer in the process table the
            // previous Hub never made it past Application.Exit; we don't want
            // a stale updating.lock gating a future BeginApply call.
            Phoenix.Controls.Hub.Core.UpdateChecker.ClearStaleSentinel();

            var prev = Phoenix.Controls.Hub.Core.UpdateChecker.ReadAndClearLastUpdateResult();
            if (prev is not null)
            {
                string label = prev.Outcome ?? "(unknown)";
                string trail = string.IsNullOrEmpty(prev.ErrorMessage)
                    ? string.Empty
                    : $" — {prev.ErrorMessage}";
                GlobalLogger.Log(
                    $"Last update: {label}{trail} (timestamp {prev.Timestamp ?? "—"})",
                    "App", LogLevel.System);

                // First launch after a COMPLETED auto-update: pop the bundled
                // changelog so the user sees what changed. The result file is
                // read-once (deleted by the reader above), so this fires exactly
                // once per applied update — no extra "already shown" flag needed.
                if (string.Equals(prev.Outcome, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    string versionTail = string.IsNullOrWhiteSpace(prev.NewSha)
                        ? string.Empty
                        : $" — v{prev.NewSha}";
                    DocViewerWindow.OpenOrFocus(new DocViewRequest(
                        "changelog.html",
                        Title: $"Phoenix Controls — What's new{versionTail}"));
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("App", "ReadAndClearLastUpdateResult surface failed", ex);
        }

        // Start the UI-thread hang watchdog now that the main window is up and
        // the heavy startup phases (which legitimately block the UI thread) are
        // done — so a stall it reports is a real freeze, not boot work. All
        // three pillars run in-process on this dispatcher, so one watchdog on
        // the Hub UI thread covers Architect-editing freezes too. Diagnostic
        // only; never gates startup.
        try
        {
            if (_main is not null)
            {
                _hangWatchdog = new UiHangWatchdog(_main.DispatcherQueue);
                _hangWatchdog.Start();
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("App", "UiHangWatchdog start failed", ex);
        }

        // Surface the auto-recovery notice now the UI is up: this launch was a
        // self-relaunch out of a permanent UI freeze in the previous session.
        // CriticalError so it stands out in the System Log / Errors surfaces.
        if (recovery is { } recNotice)
        {
            try
            {
                GlobalLogger.Error("App",
                    "Hub automatically recovered from a UI-thread freeze in the previous session and restarted. " +
                    (string.IsNullOrEmpty(recNotice.DumpPath)
                        ? "A diagnostic dump path was not provided."
                        : $"Diagnostic dump saved to: {recNotice.DumpPath}"));
            }
            catch { /* best-effort notice */ }
        }

        // CLI deep-link — when launched via Explorer's
        // .phxg / .phx / .phxlayer file association, Hub gets
        // `--open <absolute-path>` (or a positional path) in argv. Route
        // through the navigator so the right pillar tab comes up and the
        // file loads automatically. Done before the welcome dialog so a
        // first-run user double-clicking a sample doesn't have to dismiss
        // the dialog before seeing the file.
        //
        // Note: the installer-registered file
        // associations fire as plain argv positional args (Hub.exe
        // "C:\path\file.phxg"), which TryParseOpenTarget already covers.
        // Packaged-MSIX apps would use AppInstance.GetActivatedEventArgs()
        // (Kind == File) instead, but Phoenix Controls ships unpackaged
        // via Inno Setup so that activation path never fires for us.
        string? openTarget = TryParseOpenTarget(Environment.GetCommandLineArgs());
        if (!string.IsNullOrEmpty(openTarget))
        {
            try
            {
                // .phx dispatches to the read-only viewer window,
                // NOT a pillar. .phx is a generated output (see the project docs
                // "Critical rule: never edit `.phx` directly"); routing it
                // to Architect would let the streamer hand-edit a runtime
                // script that the exporter will overwrite on the next graph
                // save. The new window enforces IsReadOnly at the TextBox
                // level so the file association is safe for unsuspecting
                // double-clicks.
                string ext = Path.GetExtension(openTarget).ToLowerInvariant();
                if (ext == ".phx")
                {
                    OpenReadOnlyPhxViewer(openTarget);
                }
                else
                {
                    PillarKind? kind = PillarFromExtension(openTarget);
                    if (kind is { } k)
                    {
                        _main?.NavigateTo(k, openTarget);
                    }
                    else
                    {
                        GlobalLogger.Log(
                            $"--open '{openTarget}' has unrecognised extension; staying on Hub workspace.",
                            "App", LogLevel.System);
                    }
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("App", $"--open '{openTarget}' route failed", ex);
            }
        }

        // First-run welcome dialog — opens once per fresh
        // install. AppConfig.SeenWelcomeDialog flips the moment the user
        // dismisses (or picks a sample), so a successful boot followed by
        // a crash before the user reached the dialog still gets a second
        // chance on next launch. Errors during the dialog are non-fatal:
        // failure to show the welcome screen must never block Hub startup.
        // Skip when --open routed to a pillar — the user came in to view
        // a specific file, not to be greeted.
        try
        {
            if (string.IsNullOrEmpty(openTarget)
                && !ConfigManager.Current.SeenWelcomeDialog
                && _main?.Content is FrameworkElement root)
            {
                // Activate() schedules the window's first arrange but the
                // visual tree's XamlRoot isn't necessarily live on the same
                // dispatcher tick. Constructing ContentDialog here used to
                // throw "The parameter is incorrect — element does not have
                // a XamlRoot" because root.XamlRoot was null. Defer the
                // dialog show until root.Loaded so XamlRoot is guaranteed
                // populated.
                if (root.IsLoaded && root.XamlRoot is { } xrNow)
                {
                    await RunWelcomeAndRouteAsync(xrNow);
                }
                else
                {
                    RoutedEventHandler? once = null;
                    once = async (_, _) =>
                    {
                        root.Loaded -= once!;
                        if (root.XamlRoot is { } xr) await RunWelcomeAndRouteAsync(xr);
                    };
                    root.Loaded += once;
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("App", "show WelcomeDialog failed", ex);
        }
    }

    // Helper for the first-launch welcome path — shows the dialog, and on a
    // sample pick routes the user into Architect with the copied file as
    // the open target so they don't land on the Hub workspace and have to
    // hunt for the Architect tab themselves. Exception-tolerant: a
    // navigation fault is logged via GlobalLogger and the user is left on
    // the Hub workspace (still better than crashing the boot path).
    private async Task RunWelcomeAndRouteAsync(Microsoft.UI.Xaml.XamlRoot xr)
    {
        var (picked, path) = await ShowWelcomeDialogAsync(xr);
        if (picked && !string.IsNullOrEmpty(path) && _main is not null)
        {
            try { _main.NavigateTo(PillarKind.Architect, openTarget: path); }
            catch (Exception ex) { GlobalLogger.Error("App", $"Welcome: NavigateTo(Architect, '{path}') failed", ex); }
        }
    }

    // Internal so MainWindow's Help-menu wiring can re-open the welcome
    // dialog post-launch (handled by a sibling agent). Returns the picked
    // sample's destination path alongside the SamplePicked flag so callers
    // can route the user into Architect with the freshly-copied file as the
    // open target. Exception-tolerant — a render/theme fault during the
    // dialog is logged and surfaces as (false, null) so the caller treats
    // the dismissal as "no pick" rather than re-throwing into startup.
    internal static async Task<(bool picked, string? path)> ShowWelcomeDialogAsync(Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        try
        {
            var dialog = new WelcomeDialog { XamlRoot = xamlRoot };
            await dialog.ShowAsync();
            return (dialog.SamplePicked, dialog.PickedSamplePath);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("App", "ShowWelcomeDialogAsync failed", ex);
            return (false, null);
        }
    }

    // Pulls the deep-link target out of argv. Accepts either `--open <path>`
    // or a single positional path argument. argv[0] is always the exe; we
    // only scan from index 1. Unknown flags (--foo) are skipped so future
    // additions don't crash older builds — same convention as Architect's
    // pre-collapse CliBootstrap.
    private static string? TryParseOpenTarget(string[] argv)
    {
        if (argv is null || argv.Length < 2) return null;
        for (int i = 1; i < argv.Length; i++)
        {
            string a = argv[i];
            if (string.IsNullOrEmpty(a)) continue;
            if (a == "--open" && i + 1 < argv.Length) return argv[i + 1];
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            // First positional non-flag argument — treat as a path.
            return a;
        }
        return null;
    }

    private static PillarKind? PillarFromExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".phxg"     => PillarKind.Architect,
            // .phx is intentionally NOT in this map any more — it
            // routes to ReadOnlyPhxWindow via the upstream dispatch in
            // OnLaunchedCore. Adding .phx back here would re-introduce the
            // editable-Architect surface the sprint is closing.
            ".phxlayer" => PillarKind.Visualist,
            _ => null,
        };
    }

    /// <summary>
    /// Open the read-only .phx viewer for an Explorer
    /// double-click. Constructs a fresh <see cref="ReadOnlyPhxWindow"/>
    /// and activates it. The sibling-graph "Open in Architect" callback
    /// is bound to <see cref="MainWindow.NavigateTo"/> so clicking the
    /// banner button routes through the existing pillar-launch flow
    /// (the navigator handles tab construction + Architect.OpenAsync).
    /// </summary>
    private void OpenReadOnlyPhxViewer(string phxPath)
    {
        try
        {
            Action<string>? openGraph = null;
            var main = _main;
            if (main is not null)
            {
                openGraph = graphPath =>
                {
                    try { main.NavigateTo(PillarKind.Architect, openTarget: graphPath); }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("App",
                            $"ReadOnlyPhxWindow → NavigateTo(Architect, '{graphPath}') failed", ex);
                    }
                };
            }

            var viewer = new ReadOnlyPhxWindow(phxPath, openGraph);
            WindowFront.Show(viewer);
            GlobalLogger.Log(
                $"Opened read-only viewer for {phxPath}",
                "App", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("App", $"OpenReadOnlyPhxViewer('{phxPath}') failed", ex);
        }
    }
}

public readonly record struct SplashProgress(string Status, double Fraction);
