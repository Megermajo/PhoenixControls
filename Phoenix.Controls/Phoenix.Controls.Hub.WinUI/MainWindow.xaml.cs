using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Architect.WinUI.Services;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.WinUI.Controls;
using Phoenix.Controls.Hub.WinUI.Dialogs;
using Phoenix.Controls.Hub.WinUI.Services;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.WinUI.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;
using SysProcess = System.Diagnostics.Process;
using SysProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using HubAsyncBoundary = Phoenix.Controls.Hub.Core.AsyncErrorBoundary;

namespace Phoenix.Controls.Hub.WinUI;

public sealed partial class MainWindow : Window, IPillarNavigator
{
    private const string GitHubUrl = "https://github.com/" + UpdateChecker.DefaultGitHubRepo;
    private UpdateChecker? _updateChecker;

    // AppWindow lives outside the Window's
    // managed lifecycle, so its event handlers survive Window.Closed unless
    // explicitly detached. Caption-reserve + min-size handlers now flow
    // through named methods (OnAppWindowChangedClampMin /
    // OnAppWindowClosing) so every += has a matching -= in the Closed
    // handler — no field-captured lambdas needed for those paths.
    private AppWindow? _appWindow;
    // Defer the startup update check until after the first Loaded fire so
    // the HTTPS round-trip doesn't compete with first-frame paint.
    // Field exists so we can detach on Close even if the
    // window closes before Loaded ever fires.
    private RoutedEventHandler? _onDeferStartupUpdateCheck;
    // One update prompt at a time — the deferred startup check and a manual
    // re-check can both surface ReleaseAvailable in the same session.
    private bool _updatePromptOpen;

    private readonly HubWorkspaceView _hubWorkspace = new();
    private Phoenix.Controls.Architect.WinUI.MainView? _architectView;
    private Phoenix.Controls.Visualist.WinUI.MainView? _visualistView;
    private PillarKind _activePillar = PillarKind.Hub;

    // 4th-tab (Giveaway) pop-out window — single instance. The Giveaway tab
    // opens the page in its own independent window rather than swapping the
    // MainPaneRegion surface; a second click focuses the existing window
    // instead of spawning a duplicate. The matching view is held so its
    // Dispose runs when the window closes (unsubscribing the VM from the
    // GiveawaySource events).
    private Window? _giveawayWindow;
    private Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel.GiveawayView? _giveawayView;

    // Design_Orders §4.7 — under Windows high-contrast themes the custom
    // coal/ember chrome would render unreadable against the HC palette.
    // Fall back to the system title bar by collapsing the chrome and
    // flipping ExtendsContentIntoTitleBar off. Watcher fires on HC toggle.
    private Phoenix.Controls.Hub.WinUI.Services.HighContrastWatcher? _hcWatcher;

    // Held so the lazy-construction of pillar MainViews can pass
    // through the Hub's IHubServices (e.g. Visualist needs IHubServices.Layers
    // for live-presence dots on the LayerRail). Set by SetPanels which fires
    // before the user can click into a pillar tab.
    private IHubServices? _services;

    /// <summary>
    /// IPillarNavigator implementation — Hub-internal cross-pillar navigation.
    /// Activates the requested pillar (constructing its MainView lazily on
    /// first hit) and, when openTarget is supplied, forwards the path to the
    /// pillar's Open method. Used by:
    ///   * ScriptHostMonitor.OpenInArchitectAsync (script row → Edit in Architect)
    ///   * Hub.App.OnLaunched's --open argv parser — Explorer
    ///     double-click on a .phxg / .phxlayer routes through here so the
    ///     right pillar comes up with the file already loaded.
    /// IPillarNavigator keeps the sync-void shape, so the async core is
    /// fire-and-forget through AsyncErrorBoundary, matching the surrounding
    /// "best-effort, log on fault" semantic.
    /// </summary>
    public void NavigateTo(PillarKind kind, string? openTarget = null)
    {
        _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            () => NavigateToCore(kind, openTarget),
            "MainWindow",
            $"NavigateTo({kind}, '{openTarget}') failed");
    }

    // The pillar swap MUST be awaited before openTarget is dispatched.
    // SwitchPillarAsync suspends at the outgoing pillar's dirty prompt and
    // only constructs the target MainView after that await — dispatching
    // without awaiting would observe _architectView/_visualistView still
    // null and silently skip the open, leaving the pillar up but empty.
    // The swap's bool result gates the dispatch: a declined dirty prompt
    // must drop openTarget even when the target MainView already exists
    // from an earlier visit — dispatching into the hidden pillar would
    // clobber its open document behind the user's back.
    private async Task NavigateToCore(PillarKind kind, string? openTarget)
    {
        bool swapped = await SwitchPillarAsync(kind);
        if (string.IsNullOrEmpty(openTarget)) return;
        if (!swapped)
        {
            // Dropping openTarget is the intended outcome of a declined
            // dirty prompt; log so the drop stays visible.
            GlobalLogger.Log(
                $"NavigateTo({kind}): open target '{openTarget}' dropped — pillar swap declined at the dirty prompt.",
                "MainWindow", LogLevel.System);
            return;
        }
        switch (kind)
        {
            case PillarKind.Architect when _architectView is not null:
                // Architect.OpenAsync replaces the deadlock-prone sync Open.
                // Awaited here — the whole core already runs inside the
                // NavigateTo error boundary, so a faulted load is logged
                // with the navigation context.
                await _architectView.OpenAsync(openTarget);
                break;
            case PillarKind.Visualist when _visualistView is not null:
                // This is the receiving end of the .phxlayer
                // drop route. Architect's LogicCanvasView raises
                // LayerFileOpenRequested → Architect.MainView's
                // subscriber calls IPillarNavigator.NavigateTo(Visualist,
                // openTarget: path) → lands here, which swaps the active
                // pillar to Visualist and forwards the path to
                // _visualistView.Open. The "dragdrop.unrouted
                // .phxlayer" log line only fires when Architect runs
                // outside a MainView host (e.g. headless tests).
                _visualistView.Open(openTarget);
                break;
            case PillarKind.Architect:
            case PillarKind.Visualist:
                // Still null after a completed swap — shouldn't happen (the
                // swap constructs the target MainView before returning true),
                // but a defensive drop-with-log beats dispatching into a
                // null view if a future swap path forgets construction.
                GlobalLogger.Log(
                    $"NavigateTo({kind}): open target '{openTarget}' dropped — pillar view unavailable after swap.",
                    "MainWindow", LogLevel.System);
                break;
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        // Wire the cross-pillar documentation-viewer seam. Architect's F1 /
        // Help → Node Reference and Hub's Help → README / Changelog all route
        // through DocViewerHost; the concrete opener is the WebView2-backed
        // DocViewerWindow that lives in this exe. Registered here (before any
        // pillar tab can be shown) so the very first F1 resolves.
        DocViewerHost.Opener = DocViewerWindow.OpenOrFocus;

        Title = "Phoenix Controls";
        // Custom traffic lights: the chrome owns min / max /
        // close so we drop the system caption entirely. ExtendsContentIntoTitleBar
        // stays true so the chrome draws over the full top of the window;
        // ConfigureAppWindow then calls SetBorderAndTitleBar(true, false)
        // on the OverlappedPresenter to hide the system caption buttons.
        // SetTitleBar is deferred to ConfigureAppWindow (after _appWindow +
        // Presenter are resolved) — wiring it here against an as-yet-unsettled
        // AppWindow would race the Presenter's SetBorderAndTitleBar(false)
        // call, leaving the system caption ghosted under the chrome's
        // traffic-light buttons.
        ExtendsContentIntoTitleBar = true;

        // HC accessibility fallback — must run after the initial chrome
        // attachment above so the first toggle (if HC is already on at
        // launch) sees a settled baseline to invert. The watcher's callback
        // can fire on a non-UI thread (AccessibilitySettings.HighContrastChanged
        // dispatches from the platform), so marshal back onto the Window's
        // DispatcherQueue before touching ChromeBar / SetTitleBar.
        _hcWatcher = new Phoenix.Controls.Hub.WinUI.Services.HighContrastWatcher(() =>
        {
            var dq = DispatcherQueue;
            if (dq is null || dq.HasThreadAccess) ApplyHighContrastFallback();
            else dq.TryEnqueue(ApplyHighContrastFallback);
        });
        ApplyHighContrastFallback();

        ChromeBar.PillarTabClicked        += OnPillarTabClicked;
        ChromeBar.GiveawayTabClicked      += OnGiveawayTabClicked;
        ChromeBar.MenuItemInvoked         += OnMenuItemInvoked;
        ChromeBar.MinimizeClicked         += OnMinimizeClicked;
        ChromeBar.MaximizeRestoreClicked  += OnMaximizeRestoreClicked;
        ChromeBar.CloseClicked            += OnCloseClicked;
        ChromeBar.FileCloseRequested      += OnChromeFileCloseRequested;

        // Wire the chrome's menu
        // accelerators onto a window-wide scope so Ctrl+Z, Ctrl+S, Ctrl+O
        // (and the rest of the advertised chords) fire even when the user
        // hasn't opened the Edit menu yet. Pre-fix the accelerators lived
        // only on the lazy-realized MenuFlyoutItems, so the chord registry
        // stayed empty until the user popped the menu once — leaving Ctrl+Z
        // looking like a dead key on every fresh launch. The mirror attaches
        // to Content (the MainWindow root Grid) so accelerators stay active
        // regardless of where focus lives (canvas, chrome, popup textbox).
        if (Content is UIElement scopeRoot)
        {
            ChromeBar.MirrorAcceleratorsTo(scopeRoot);
        }

        // Default workspace = Hub. Architect / Visualist are constructed
        // lazily on first activation so their VMs / canvas state aren't
        // built until the user actually clicks the tab.
        MainPaneRegion.Content = _hubWorkspace;

        // Dispose the Hub workspace's panel VMs at app shutdown so they
        // unsubscribe from HubServices source events before the source
        // backends get torn down. Tab switching does NOT
        // dispose — _hubWorkspace stays alive for the window lifetime
        // and is re-shown on Hub-tab clicks.
        // Also tears down the UpdateChecker subscription + the chrome-bar
        // event hooks; without these the handlers stay attached to the
        // singletons and pin the window through to process exit.
        Closed += OnMainWindowClosed;

        ConfigureAppWindow();
        WireUpdateChecker();
        WireSavePromptOnClose();

        // Register the Hub Window with the activation
        // tracker so StatusDot / ScriptStatusDot pulses can pause when the
        // window is deactivated. Register must run after Content is set
        // (InitializeComponent sets the XAML root) so XamlRoot is reachable.
        WindowActivationTracker.Register(this);

        // Replay any Architect sibling windows that were open at the
        // last clean shutdown. Best-effort, never blocks boot, faults are
        // logged + swallowed per entry.
        WireArchitectSiblingReplay();
    }

    // Coordinated shutdown. Replaces the prior
    // "fire and pray" Closed handler that scheduled ~8 SafeRunAsync calls
    // and returned immediately. Window.Closed is synchronous, so those
    // fire-and-forget tasks raced with the message-pump exit; any one of
    // them stalling (a non-cancellable WS accept-loop, an undisposed
    // Threading.Timer, the LiveCaptionService poll, ScriptManager's stop
    // drain) left Phoenix.Controls.Hub.WinUI.exe alive in Task Manager
    // after the window vanished.
    //
    // New flow:
    //   1. Synchronous handler-detach + dispose (cheap, no thread work).
    //   2. Spawn a background coordinator that awaits the slow tear-downs
    //      in parallel with an aggregate hard cap of ShutdownTimeoutMs.
    //   3. After the coordinator completes (or the timeout fires), end the
    //      process via HubProcessExit.TerminateSelf — a TerminateProcess
    //      hard exit that can't be held alive by a forgotten foreground
    //      thread AND can't wedge in native DLL teardown.
    //
    // Why TerminateProcess and not Environment.Exit(0): Environment.Exit
    // still runs the Windows ExitProcess sequence (ProcessExit handlers,
    // then every DLL's DLL_PROCESS_DETACH under the loader lock). With
    // XAML / WebView2 / Win2D / TSF / AV hooks in-process, a wedge there
    // left a window-less zombie Hub that held the single-instance mutex
    // and the install tree's file locks — and a process stuck inside
    // ExitProcess often can't be force-killed, so the next auto-update
    // failed until a reboot. TerminateSelf flushes what must survive
    // (sibling-replay store, rolling file log), kills our WebView2
    // children, then TerminateProcess-es — nothing left that can hang.
    // App.xaml.cs pins DispatcherShutdownMode.OnExplicitShutdown so the
    // natural last-window-close exit (the same wedge-prone native
    // teardown) can never race this coordinator.
    //
    // Diagnostic logging surfaces *which* tear-down stalled so future
    // sessions can fix the root cause without re-introducing the hang.
    private const int ShutdownTimeoutMs = 4000;

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        // Step 1 — synchronous detaches. These are cheap and must run on
        // the UI thread before the message pump exits.

        // Close the doc/changelog viewer's WebView2 first so its
        // msedgewebview2.exe browser child exits with us instead of being
        // orphaned into the install tree (an orphan there locks the folder the
        // next auto-update renames aside). Cheap and synchronous.
        try { Phoenix.Controls.Hub.WinUI.Dialogs.DocViewerWindow.CloseIfOpen(); }
        catch (Exception ex) { GlobalLogger.Error("MainWindow", "DocViewerWindow.CloseIfOpen", ex); }

        try { _hubWorkspace.Dispose(); } catch (Exception ex) { GlobalLogger.Error("MainWindow", "_hubWorkspace.Dispose", ex); }

        // Tear down the persistent pillar bus links at
        // REAL app close — this is their single Stop site. Both clients run for
        // the whole session (started in StartPillarBusLinks at launch) so the
        // bus keeps transmitting to OBS / Architect debug regardless of which
        // pillar tab is active — Majo: "the BUS MUST run even when not in
        // Visualist". Visualist's ShutdownPillar() also stops VisualistBusClient
        // AND flushes the pillar VM (dirty-doc + autosave-timer teardown) that
        // used to (wrongly) run on every tab-blur. Guarded so a teardown fault
        // can't block the close path.
        try { _visualistView?.ShutdownPillar(); }
        catch (Exception ex) { GlobalLogger.Error("MainWindow", "Visualist ShutdownPillar", ex); }
        try { Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.Stop(); }
        catch (Exception ex) { GlobalLogger.Error("MainWindow", "Architect bus stop", ex); }

        ChromeBar.PillarTabClicked       -= OnPillarTabClicked;
        ChromeBar.GiveawayTabClicked     -= OnGiveawayTabClicked;
        ChromeBar.MenuItemInvoked        -= OnMenuItemInvoked;
        ChromeBar.MinimizeClicked        -= OnMinimizeClicked;
        ChromeBar.MaximizeRestoreClicked -= OnMaximizeRestoreClicked;
        ChromeBar.CloseClicked           -= OnCloseClicked;
        ChromeBar.FileCloseRequested     -= OnChromeFileCloseRequested;

        // Close the Giveaway pop-out window (if open) so its VM unsubscribes
        // from the GiveawaySource events before the backends tear down.
        if (_giveawayWindow is not null)
        {
            try { _giveawayWindow.Close(); } catch { /* shutdown best-effort */ }
            _giveawayWindow = null;
            _giveawayView = null;
        }

        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChangedClampMin;
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow = null;
        }
        if (_onDeferStartupUpdateCheck is not null)
        {
            ChromeBar.Loaded -= _onDeferStartupUpdateCheck;
            _onDeferStartupUpdateCheck = null;
        }
        if (_hcWatcher is not null)
        {
            try { _hcWatcher.Dispose(); } catch { /* shutdown best-effort */ }
            _hcWatcher = null;
        }
        if (_updateChecker is not null)
        {
            _updateChecker.StatusChanged -= OnUpdateStatusChanged;
            try { _updateChecker.Dispose(); } catch { /* shutdown best-effort */ }
            _updateChecker = null;
        }

        // Step 2 — synchronous HUDServer dispose. HUDServer.Dispose
        // closes its HttpListener on the calling thread; cheap and the
        // socket release matters for the next launch (port 18080 stays
        // bound through TIME_WAIT if we hand it to the coordinator).
        try
        {
            if (HubHost.HUD is { } hud)
            {
                hud.Dispose();
                HubHost.HUD = null;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("MainWindow", "HUDServer.Dispose teardown failed", ex);
        }

        // Step 3 — snapshot the async-disposable services on the UI
        // thread (the field reads are not threadsafe). The coordinator
        // task picks them up from local captures.
        var services = _services;
        _services = null;

        // Step 4 — background coordinator. Awaits every slow tear-down
        // in parallel, applies an aggregate 4 s hard cap, and hard-exits
        // via HubProcessExit.TerminateSelf when done. Runs on Task.Run so
        // the rest of Window.Closed can return; the dispatcher keeps
        // pumping meanwhile (DispatcherShutdownMode.OnExplicitShutdown)
        // instead of starting the wedge-prone natural exit.
        _ = Task.Run(async () =>
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            try
            {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(ShutdownTimeoutMs));
            var ct = cts.Token;

            // Each step is wrapped in its own try/catch so a single
            // stalled tear-down doesn't strand the rest.
            async Task<long> RunStepAsync(string name, Func<Task> body)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await body().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    GlobalLogger.Log(
                        $"Shutdown step '{name}' exceeded {ShutdownTimeoutMs}ms cap and was abandoned.",
                        "MainWindow.Shutdown", LogLevel.CriticalError);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("MainWindow.Shutdown", $"step '{name}' failed", ex);
                }
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }

            // Run all the slow tear-downs in parallel. The Bus +
            // RemoteBridge + WS + ScriptManager + opt-in services
            // are independent — no ordering constraint matters
            // post-HUD-dispose (which already released port 18080).
            // HubServices.DisposeAsync includes ConnectionStatus.Timer
            // and every panel source subscription.
            var tasks = new List<Task<long>>(7);

            if (services is not null)
            {
                tasks.Add(RunStepAsync("HubServices.DisposeAsync", async () =>
                {
                    if (services is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
                    else if (services is IDisposable d) d.Dispose();
                }));
            }

            tasks.Add(RunStepAsync("ScriptManager.DisposeAsync",
                async () => await ScriptManager.Instance.DisposeAsync().ConfigureAwait(false)));

            tasks.Add(RunStepAsync("WS.StopAsync",
                () => WS.Instance.StopAsync()));

            if (HubHost.RemoteBridge is { } remoteBridge)
            {
                HubHost.RemoteBridge = null;
                tasks.Add(RunStepAsync("RemoteBridge.StopAsync",
                    () => remoteBridge.StopAsync()));
            }

            tasks.Add(RunStepAsync("Bus.StopAsync",
                () => Bus.Instance.StopAsync()));

            tasks.Add(RunStepAsync("HubBootstrapper.ShutdownOptInServicesAsync",
                () => HubBootstrapper.ShutdownOptInServicesAsync()));

            try
            {
                await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The aggregate cap fired before every task drained.
                // Per-step logging above already named the stalled ones.
                GlobalLogger.Log(
                    $"Shutdown coordinator hit the {ShutdownTimeoutMs}ms aggregate cap " +
                    $"(elapsed {swTotal.ElapsedMilliseconds}ms). Forcing process exit.",
                    "MainWindow.Shutdown", LogLevel.CriticalError);
            }

            swTotal.Stop();
            GlobalLogger.Log(
                $"Shutdown coordinator complete (elapsed {swTotal.ElapsedMilliseconds}ms). " +
                "Hard-terminating the process (TerminateProcess) so native DLL teardown can't wedge a zombie.",
                "MainWindow.Shutdown", LogLevel.System);
            }
            finally
            {
                // The exit MUST happen even if the coordinator body faulted in
                // a way the per-step catches didn't cover — under
                // OnExplicitShutdown nothing else ends this process.
                Services.HubProcessExit.TerminateSelf("coordinated shutdown");
            }
        });
    }

    // Multi-window Architect replay — read RecentSiblingsStore and
    // re-spawn each surviving sibling once the MainWindow's content tree is
    // up. Fires once via a self-detaching Loaded handler (mirrors App.xaml.cs's
    // splash-close pattern). The store is NOT cleared after replay — sibling
    // windows themselves Touch/Remove their entries on open/close.
    private void WireArchitectSiblingReplay()
    {
        void RunReplay()
        {
            try
            {
                var entries = RecentSiblingsStore.Load();
                if (entries is null || entries.Count == 0) return;

                // FocusOrder: lower = more recently focused (0 = front-most).
                // Replay in descending order so the front-most window ends up
                // Activated last and reclaims focus, per the store docstring.
                entries.Sort((a, b) => b.FocusOrder.CompareTo(a.FocusOrder));

                foreach (var entry in entries)
                {
                    string path = entry?.Path ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (!File.Exists(path))
                    {
                        GlobalLogger.Log(
                            $"Architect sibling replay: skipping missing file '{path}'.",
                            "MainWindow", LogLevel.Debug);
                        continue;
                    }
                    // Independent SafeRunAsync per file so a single fault
                    // doesn't bring down the rest of the replay.
                    _ = HubAsyncBoundary.SafeRunAsync(
                        () => ArchitectWindowRegistry.OpenFileAsync(path),
                        "MainWindow", $"ArchitectSiblingReplay('{path}')");
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("MainWindow", "ArchitectSiblingReplay", ex);
            }
        }

        if (Content is not FrameworkElement fel) return;
        if (fel.IsLoaded)
        {
            RunReplay();
            return;
        }
        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            if (onLoaded is not null) fel.Loaded -= onLoaded;
            onLoaded = null;
            RunReplay();
        };
        fel.Loaded += onLoaded;
    }

    // Subscribe to AppWindow.Closing so unsaved Architect / Visualist work
    // surfaces a save / discard / cancel prompt before the window goes away.
    // MainWindow.Closed itself doesn't
    // expose Cancel — AppWindow.Closing is the one that does.
    private void WireSavePromptOnClose()
    {
        // _appWindow was set by ConfigureAppWindow earlier in the ctor.
        // Re-resolve as a fallback so this method is safe to call even if
        // ConfigureAppWindow runs in a different order on some startup paths.
        if (_appWindow is null)
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            if (_appWindow is null) return;
        }
        _appWindow.Closing += OnAppWindowClosing;
    }

    // Three-state close gate so a re-entry between the prompt
    // completion and the re-issued Close() can distinguish "still asking"
    // from "already approved". Plain-bool _promptInFlight pre-fix flipped
    // back to false BEFORE Close was dispatched — a Closing fire that
    // landed in that window saw a clean flag and re-prompted (or, worse,
    // the dispatcher reordered and the user got the close blocked by an
    // already-answered prompt). Interlocked.CompareExchange enforces the
    // single-prompter contract; PromptCompleted lets the re-issued Close
    // pass through without re-prompting.
    private const int PromptIdle      = 0;
    private const int PromptInFlight  = 1;
    private const int PromptCompleted = 2;
    private int _promptState = PromptIdle;

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Top-to-bottom try around the entire close handler.
        // async void escapes to the dispatcher's unhandled-exception path —
        // a throw from the dirty scan, the args.Cancel toggle, or the
        // DispatcherQueue.TryEnqueue at the bottom would silently tear down
        // the window. The inner PromptSaveBeforeCloseAsync block already had
        // its own catch (preserved below); the outer wrap covers everything
        // else.
        try
        {
            // Re-issued Close after the prompts resolved — let it
            // pass. State stays in PromptCompleted because there is no
            // "back to idle" path: once the user has answered Save/Discard,
            // the window is going down. A fault below (handled in the catch)
            // is the only path that returns to Idle so the user can retry.
            if (Interlocked.CompareExchange(ref _promptState, PromptCompleted, PromptCompleted) == PromptCompleted)
            {
                return;
            }

            var hosts = new IPillarShellHost?[] { _architectView, _visualistView };
            bool anyDirty = false;
            foreach (var h in hosts)
            {
                if (h is null) continue;
                if (h.IsDirty) { anyDirty = true; break; }
            }
            if (!anyDirty) return;

            // CompareExchange establishes a single in-flight prompter. A
            // concurrent Closing fire (e.g. user double-clicks the X) sees
            // PromptInFlight and bails after cancelling its close.
            int prior = Interlocked.CompareExchange(ref _promptState, PromptInFlight, PromptIdle);
            if (prior != PromptIdle)
            {
                args.Cancel = true;
                return;
            }
            args.Cancel = true;

            bool userAgreed = true;
            try
            {
                foreach (var h in hosts)
                {
                    if (h is null) continue;
                    if (!h.IsDirty) continue;
                    bool proceed = await h.PromptSaveBeforeCloseAsync();
                    if (!proceed) { userAgreed = false; break; }
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("MainWindow", "PromptSaveBeforeCloseAsync", ex);
                userAgreed = false;
            }

            if (!userAgreed)
            {
                // User cancelled — return the gate to Idle so a later close
                // attempt can re-prompt cleanly.
                Interlocked.Exchange(ref _promptState, PromptIdle);
                return;
            }

            // All pillars resolved — flip to Completed AFTER the await so
            // the re-issued Close's Closing fire sees the sentinel and
            // lets through. Order matters: flip THEN dispatch, otherwise
            // the dispatcher could run Close before the sentinel is set
            // and we'd re-prompt.
            Interlocked.Exchange(ref _promptState, PromptCompleted);
            DispatcherQueue.TryEnqueue(Close);
        }
        catch (Exception ex)
        {
            // Keep the close gate in a defined state so a subsequent close
            // attempt doesn't get permanently stuck waiting for an in-flight
            // prompt that already faulted.
            Interlocked.Exchange(ref _promptState, PromptIdle);
            GlobalLogger.Error("MainWindow", "OnAppWindowClosing", ex);
        }
    }

    private void ConfigureAppWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // AppWindow.GetFromWindowId can return null
        // (no AppWindow backing the HWND yet on some startup paths). Bail before
        // dereferencing rather than NRE-ing through SetIcon / Changed / Presenter.
        if (_appWindow is null)
        {
            GlobalLogger.Error("MainWindow", "ConfigureAppWindow",
                new InvalidOperationException("AppWindow.GetFromWindowId returned null; skipping window configuration."));
            return;
        }

        _appWindow.SetIcon("Assets/app.ico");

        // Restore last position/size/maximized state from
        // %AppData%/PhoenixControls/Hub/window-state.json — falls back to a
        // centred 1480x920 launch on first run or when the saved record is
        // unusable. The Closed handler inside Attach captures the final state
        // before disposal so the next launch picks up where the user left off.
        MainWindowStateStore.Attach(this);

        // Lower bound on window size — without this the user can drag the
        // chrome to ~120×80 and the 4-pane workspace + HubChrome rows
        // collapse to garbage. WinAppSDK 1.5 doesn't expose
        // OverlappedPresenter.PreferredMinimumWidth/Height (added in 1.6),
        // so we clamp in the resize event: a second pass after Resize is
        // benign because the clamped size already satisfies the min on
        // the re-entry. 960×600 keeps the four headline panels readable
        // and preserves space for the chrome's 30 px main bar + 34 px
        // pillar tab strip.
        _appWindow.Changed += OnAppWindowChangedClampMin;

        // Hide the system title bar entirely — the chrome's custom
        // TrafficLightButton trio handles min / max / close.
        // SetBorderAndTitleBar(true, false) keeps the resize border but
        // drops the caption (and its three buttons) so they don't ghost
        // under the chrome on hover. SetTitleBar (called above) still
        // honours the drag region for window-drag-on-mousedown.
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }

        // Order requirement: SetTitleBar must run AFTER _appWindow is
        // resolved and the Presenter has suppressed the system caption — wiring
        // it before SetBorderAndTitleBar lets the system caption flash through
        // on first paint and the drag region attaches to a chrome that's still
        // settling. ChromeBar.DragRegion is a XAML-named field so the
        // reference resolves from anywhere in this partial class.
        SetTitleBar(ChromeBar.DragRegion);
    }

    private static void OnAppWindowChangedClampMin(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        var s = sender.Size;
        int w = Math.Max(s.Width, 960);
        int h = Math.Max(s.Height, 600);
        if (w != s.Width || h != s.Height)
            sender.Resize(new SizeInt32(w, h));
    }

    // Custom traffic-light handlers — route HubChrome's
    // MinimizeClicked / MaximizeRestoreClicked / CloseClicked into the
    // AppWindow.Presenter so the buttons do what their system-caption
    // counterparts used to. Maximize / Restore is a toggle based on the
    // current presenter state.
    private void OnMinimizeClicked(object? sender, EventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void OnMaximizeRestoreClicked(object? sender, EventArgs e)
    {
        if (_appWindow?.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized)
            presenter.Restore();
        else
            presenter.Maximize();
    }

    private void OnCloseClicked(object? sender, EventArgs e)
    {
        // Route through Close() so AppWindow.Closing still fires and the
        // unsaved-work prompt path (OnAppWindowClosing) gets its chance to
        // intercept. Calling Application.Current.Exit() would bypass it.
        Close();
    }

    /// <summary>
    /// Close the main window WITHOUT the unsaved-work prompt — used by the
    /// update-apply flow, which must never block Hub exit behind a dialog
    /// (the Updater force-closes the suite ~10s in; a prompt would just lose
    /// that race along with the user's answer). Pre-marks the close-prompt
    /// gate as already answered so OnAppWindowClosing passes straight
    /// through; the Closed handler then runs the full shutdown coordinator
    /// exactly like a normal close, ending in the TerminateProcess hard
    /// exit — so the Updater's sentinel-PID wait reliably sees Hub die
    /// (Application.Current.Exit() here used to skip Window.Closed entirely
    /// and exit through the wedge-prone natural teardown with every service
    /// still live).
    /// </summary>
    public void CloseForShutdown()
    {
        Interlocked.Exchange(ref _promptState, PromptCompleted);
        Close();
    }

    /// <summary>
    /// Called by App.OnLaunched after HubBootstrapper.BootAsync to inject
    /// the four panels into the Hub workspace. Forwards to HubWorkspaceView
    /// rather than holding the per-region references locally — the workspace
    /// is now an embedded UserControl swapped via MainPaneRegion.
    /// </summary>
    public void SetPanels(IHubServices services, IPanelFactory factory)
    {
        _services = services;
        _hubWorkspace.SetPanels(services, factory);
    }

    /// <summary>
    /// Connect BOTH design-time pillar bus links to the
    /// Hub IPC bus (:18081) at Hub launch and keep them up for the whole session.
    /// Called once from App.OnLaunchedCore after SetPanels (the bus server has
    /// already been bound by HubBootstrapper.BootAsync at that point). Both
    /// clients are idempotent singletons with their own exponential-backoff
    /// reconnect loop, so a not-yet-listening bus just retries. Stopped only at
    /// real app close (OnMainWindowClosed). Rationale: Majo — "the BUS MUST run
    /// even when not in Visualist"; the link must not be tied to whether a pillar
    /// tab happens to be open. The Architect link previously connected only while
    /// live-debug was toggled on and disconnected when it was off; it now stays
    /// connected for the session (live-debug still gates node-flashing, just not
    /// the connection — see ArchitectViewModel.SetLiveDebugEnabled).
    /// </summary>
    public void StartPillarBusLinks()
    {
        try { Phoenix.Controls.Visualist.WinUI.Core.VisualistBusClient.Instance.Start(); }
        catch (Exception ex) { GlobalLogger.Error("MainWindow", "Visualist bus start", ex); }
        try { Phoenix.Controls.Architect.Core.ArchitectBusClient.Instance.Start(); }
        catch (Exception ex) { GlobalLogger.Error("MainWindow", "Architect bus start", ex); }
        GlobalLogger.Log(
            "Pillar bus links started at launch (Visualist + Architect) — persistent for the session.",
            "MainWindow", LogLevel.System);
    }

    // Thin event-contract wrapper — the chrome's tab strip needs a sync
    // event handler, while NavigateToCore needs an awaitable Task so the
    // deep-link open can't race the (possibly prompt-suspended) swap. Both
    // funnel into SwitchPillarAsync so a single entry-point owns the swap;
    // the tab click has no follow-up dispatch, so the swap's bool result
    // is irrelevant here and deliberately ignored.
    private async void OnPillarTabClicked(object? sender, PillarKind kind)
        => await SwitchPillarAsync(kind);

    // Returns true when the requested pillar is active on return — the swap
    // completed, or the tab was already active. Returns false when the
    // outgoing pillar's dirty prompt was declined (or faulted, treated as a
    // decline) so callers with a follow-up action (NavigateToCore's
    // openTarget dispatch) know the target pillar never came up.
    private async Task<bool> SwitchPillarAsync(PillarKind kind)
    {
        // Single-HUB collapse — pillar tabs swap MainPaneRegion
        // content rather than spawning sibling exes. Each pillar's MainView
        // is constructed lazily on first activation; subsequent clicks reuse
        // the same instance so VM state (open file, undo stack) survives a
        // round-trip away from the tab.

        // No-op when the user clicks the already-active tab — no prompt, no swap.
        if (kind == _activePillar) return true;

        // CHANGELOG promises a save-before-close prompt on pillar swap as well
        // as Hub exit. Mirror OnAppWindowClosing's pattern: if the OUTGOING
        // pillar is dirty, prompt; on cancel short-circuit the swap entirely.
        // Hub itself has no shell-host so Hub→pillar swaps skip the prompt.
        IPillarShellHost? outgoing = _activePillar switch
        {
            PillarKind.Architect => _architectView,
            PillarKind.Visualist => _visualistView,
            _                    => null,
        };
        if (outgoing is not null && outgoing.IsDirty)
        {
            bool proceed;
            try
            {
                proceed = await outgoing.PromptSaveBeforeCloseAsync();
            }
            catch (Exception ex)
            {
                // Treat a faulted prompt as "user cancelled" — same posture as
                // OnAppWindowClosing's catch block. Don't strand the user on a
                // half-swapped tab.
                GlobalLogger.Error("MainWindow", "PromptSaveBeforeCloseAsync on swap", ex);
                proceed = false;
            }
            if (!proceed) return false;
        }

        switch (kind)
        {
            case PillarKind.Hub:
                MainPaneRegion.Content = _hubWorkspace;
                break;
            case PillarKind.Architect:
                if (_architectView is null)
                {
                    _architectView = new Phoenix.Controls.Architect.WinUI.MainView(this);
                    _architectView.ShellStateChanged += OnPillarShellStateChanged;
                }
                _architectView.PillarSwitchRequested -= OnEmbeddedPillarSwitch;
                _architectView.PillarSwitchRequested += OnEmbeddedPillarSwitch;
                MainPaneRegion.Content = _architectView;
                break;
            case PillarKind.Visualist:
                if (_visualistView is null)
                {
                    // Narrowed from IHubServices to ILayerRegistrySource —
                    // Visualist only consumes layer presence for the LayerRail dot,
                    // so hand it just that surface instead of the whole IHubServices
                    // bag.
                    _visualistView = new Phoenix.Controls.Visualist.WinUI.MainView(this, _services?.Layers);
                    _visualistView.ShellStateChanged += OnPillarShellStateChanged;
                }
                MainPaneRegion.Content = _visualistView;
                break;
        }
        _activePillar = kind;
        // Pillar-aware chrome: the menu strip swaps to the active pillar's
        // menu set so the user gets Architect's File/Edit/View/Help when
        // Architect is active, Visualist's when Visualist is active.
        // Pass the active MainView as the undo/redo source so the chrome's
        // Edit-menu Undo/Redo items can disable when the stacks are empty.
        ICanExecuteUndoRedo? undoRedo = kind switch
        {
            PillarKind.Architect => _architectView,
            PillarKind.Visualist => _visualistView,
            _                    => null,
        };
        ChromeBar.SetPillar(kind, undoRedo);
        UpdateWindowTitle();
        UpdateChromeFilename();
        return true;
    }

    private void OnPillarShellStateChanged(object? sender, System.EventArgs e)
    {
        // HasThreadAccess fast-path. Pillar shell state mostly
        // changes from UI-thread interactions (save, open, undo) — no need to
        // hop the dispatcher in the common case.
        void Apply() { UpdateWindowTitle(); UpdateChromeFilename(); }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    private void UpdateWindowTitle()
    {
        // `*` suffix matches the standard editor convention for unsaved work.
        bool dirty = (_visualistView?.IsDirty ?? false) || (_architectView?.IsDirty ?? false);
        Title = dirty ? "Phoenix Controls *" : "Phoenix Controls";
    }

    // Push the active pillar's current filename + dirty bullet into the
    // chrome bar's filename chip. The chip used to stay empty for both
    // editor pillars — only Architect's pillar-local chrome (collapsed
    // post-T15) wrote it.
    private void UpdateChromeFilename()
    {
        IPillarShellHost? host = _activePillar switch
        {
            PillarKind.Architect => _architectView,
            PillarKind.Visualist => _visualistView,
            _                    => null,
        };
        if (host is null)
        {
            ChromeBar.FileName = string.Empty;
            ChromeBar.SetCloseFileButtonVisible(false);
            return;
        }
        string name = host.CurrentDocumentDisplayName ?? "(unsaved)";
        if (host.IsDirty) name = "• " + name;
        ChromeBar.FileName = name;

        // Gate the close-X visibility on whether the active
        // pillar has a real document to close. Hub itself is never a "file
        // host" surface so the affordance stays hidden when Hub is active.
        bool hasDoc = _activePillar switch
        {
            PillarKind.Architect => _architectView?.HasOpenDocument ?? false,
            PillarKind.Visualist => _visualistView?.HasOpenDocument ?? false,
            _                    => false,
        };
        ChromeBar.SetCloseFileButtonVisible(hasDoc);
    }

    /// <summary>
    /// HubChrome's close-X fires this when the user clicks it.
    /// Dispatches to the active pillar's CloseCurrentDocumentAsync, which
    /// honours the dirty-prompt path. Faults log to GlobalLogger; the void
    /// signature keeps the event-handler contract simple.
    /// </summary>
    private async void OnChromeFileCloseRequested(object? sender, EventArgs e)
    {
        try
        {
            switch (_activePillar)
            {
                case PillarKind.Architect when _architectView is not null:
                    await _architectView.CloseCurrentDocumentAsync();
                    break;
                case PillarKind.Visualist when _visualistView is not null:
                    await _visualistView.CloseCurrentDocumentAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Hub.MainWindow", "OnChromeFileCloseRequested", ex);
        }
    }

    // The embedded Architect chrome can request a hop back to Hub or over
    // to Visualist (it has its own pillar-tab strip). Forward to the same
    // OnPillarTabClicked path so a single navigation entry-point owns the
    // swap logic.
    private void OnEmbeddedPillarSwitch(object? sender, PillarKind kind)
        => OnPillarTabClicked(sender, kind);

    // ── 4th tab: Giveaway pop-out window ─────────────────────────────────
    //
    // The Giveaway tab is NOT a PillarKind surface — clicking it opens the
    // Giveaway page in an independent window (single-instance) rather than
    // swapping MainPaneRegion. A second click focuses the already-open window
    // instead of spawning a duplicate. The active pillar / menu strip / tab
    // selection are left untouched so the underlying Hub / Architect /
    // Visualist surface stays exactly as it was.
    private void OnGiveawayTabClicked(object? sender, EventArgs e)
    {
        // Single-instance: focus the existing window if one is already open.
        if (_giveawayWindow is not null)
        {
            try { WindowFront.Show(_giveawayWindow); }
            catch (Exception ex) { GlobalLogger.Error("MainWindow", "Activate existing Giveaway window", ex); }
            return;
        }

        if (_services is null)
        {
            GlobalLogger.Log("Giveaway tab clicked before HubServices wired — ignoring.",
                "MainWindow", LogLevel.Debug);
            return;
        }

        try
        {
            // Capture the UI-thread dispatcher so the VM marshals source-event
            // refreshes back to the right queue (same posture as the workspace
            // pop-outs in HubWorkspaceView).
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var vm = new Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel.GiveawayViewModel(
                _services.Giveaway, dispatcher, _services.Chat);
            var view = new Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel.GiveawayView(vm);
            var window = PopOutWindowFactory.Create(view, "popout.title.giveaway", "Giveaway");

            _giveawayWindow = window;
            _giveawayView = view;

            // Closing the window disposes the view (→ VM → source-event
            // unsubscribe) and clears the single-instance fields so a later
            // tab click re-opens cleanly.
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_giveawayWindow, window))
                {
                    _giveawayWindow = null;
                    _giveawayView = null;
                }
                try { view.Dispose(); }
                catch (Exception ex) { GlobalLogger.Error("MainWindow", "Giveaway window dispose failed", ex); }
            };

            WindowFront.Show(window);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("MainWindow", "OpenGiveawayWindow failed", ex);
            _giveawayWindow = null;
            _giveawayView = null;
        }
    }

    /// <summary>
    /// Design_Orders §4.7 high-contrast fallback — when the user has an HC
    /// theme active, the custom coal/ember chrome would render unreadable
    /// against the system HC palette. Collapse the ChromeBar and let WinUI
    /// restore the system title bar so caption / drag region / caption
    /// buttons all come from the OS. Toggles re-fire via the
    /// Phoenix.Controls.Hub.WinUI.Services.HighContrastWatcher subscription so a runtime HC switch flows through.
    /// </summary>
    private void ApplyHighContrastFallback()
    {
        bool hc = _hcWatcher?.IsHighContrast == true;
        try
        {
            ChromeBar.Visibility = hc ? Visibility.Collapsed : Visibility.Visible;
            ExtendsContentIntoTitleBar = !hc;
            // When falling back to system chrome, clear the custom drag region
            // so WinUI doesn't keep treating ChromeBar's collapsed bounds as
            // the title bar.
            if (hc) SetTitleBar(null);
            else SetTitleBar(ChromeBar.DragRegion);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("MainWindow", "ApplyHighContrastFallback failed", ex);
        }
    }

    // ── Menu wire-up ─────────────────────────────────────────────────────
    // Dispatches on the stable Menu / Item *tokens* the chrome emits
    // (the previous switch on user-facing English
    // labels broke as soon as the chrome ran through Localizer.T).
    private void OnMenuItemInvoked(object? sender, MenuItemInvokedEventArgs e)
    {
        try
        {
            // Pillar-prefixed tokens (architect.* / visualist.*) come from
            // HubChrome's pillar-swapped menu strip — dispatch into the
            // matching MainView's public action surface. Bare tokens
            // (file/view/tools/help) are Hub's own menu items.
            if (e.Item.StartsWith("architect.", StringComparison.Ordinal))
            {
                HandleArchitectMenu(e.Item);
                return;
            }
            if (e.Item.StartsWith("visualist.", StringComparison.Ordinal))
            {
                HandleVisualistMenu(e.Item);
                return;
            }

            switch (e.Menu)
            {
                case "file":   HandleFileMenu(e.Item); break;
                case "view":   HandleViewMenu(e.Item); break;
                case "tools":  HandleToolsMenu(e.Item); break;
                case "help":   HandleHelpMenu(e.Item); break;
                default:
                    GlobalLogger.Log($"Menu item invoked but no handler: {e.Menu} → {e.Item}",
                        "MainWindow", LogLevel.System);
                    break;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("MainWindow", $"menu '{e.Menu} → {e.Item}' failed", ex);
        }
    }

    private void HandleArchitectMenu(string item)
    {
        if (_architectView is null)
        {
            GlobalLogger.Log($"Architect menu '{item}' invoked but Architect view not yet constructed.",
                "MainWindow", LogLevel.System);
            return;
        }
        switch (item)
        {
            case "architect.file.new":        _architectView.NewGraph(); break;
            case "architect.file.open":       _ = _architectView.OpenFileAsync(); break;
            case "architect.file.openRecent": _ = _architectView.OpenRecentAsync(); break;
            case "architect.file.welcome":    _architectView.ShowWelcome(); break;
            case "architect.file.save":       _ = _architectView.SaveFileAsync(); break;
            case "architect.file.restoreBackup": _ = _architectView.RestoreFromBackupAsync(); break;
            case "architect.file.export":     _architectView.ExportPhx(); break;
            case "architect.edit.undo":           _architectView.Undo(); break;
            case "architect.edit.redo":           _architectView.Redo(); break;
            case "architect.edit.find":           _architectView.FindNode(); break;
            // Edit → Group (Ctrl+G). Was a dead menu item — the entry + its
            // advertised accelerator existed in HubChrome but no dispatch case
            // ran GroupSelection, so clicking it (or hitting Ctrl+G while the
            // chrome, not the canvas, had focus) did nothing. The focused
            // canvas already handles Ctrl+G itself and marks the event handled,
            // so this case only fires on the menu click / chrome-focus path —
            // no double-Group.
            case "architect.edit.group":          _architectView.GroupSelection(); break;
            case "architect.view.liveDebug":      _architectView.ToggleLiveDebug(); break;
            case "architect.view.showGrid":       _architectView.ToggleShowGrid(); break;
            case "architect.view.frameSelection": _architectView.FrameSelection(); break;
            case "architect.help.nodeReference":  _architectView.OpenNodeDocs(); break;
            default:
                GlobalLogger.Log($"Architect menu '{item}' has no handler yet.",
                    "MainWindow", LogLevel.System);
                break;
        }
    }

    private void HandleVisualistMenu(string item)
    {
        if (_visualistView is null)
        {
            GlobalLogger.Log($"Visualist menu '{item}' invoked but Visualist view not yet constructed.",
                "MainWindow", LogLevel.System);
            return;
        }
        switch (item)
        {
            case "visualist.file.newLayer":            _visualistView.NewLayer(); break;
            case "visualist.file.openLayer":           _visualistView.OpenLayerDialog(); break;
            case "visualist.file.openRecent":          _ = _visualistView.OpenRecentLayerAsync(); break;
            case "visualist.file.save":                _visualistView.SaveLayer(); break;
            case "visualist.file.saveAs":              _visualistView.SaveLayerAs(); break;
            case "visualist.edit.undo":                _visualistView.Undo(); break;
            case "visualist.edit.redo":                _visualistView.Redo(); break;
            case "visualist.edit.addWidget":           _visualistView.AddWidget(); break;
            case "visualist.view.layerCanvas":         _visualistView.ShowLayerCanvas(); break;
            case "visualist.view.widgetEditor":        _visualistView.ShowWidgetEditor(); break;
            case "visualist.help.compositorReference": _visualistView.ShowCompositorReference(); break;
            // Window → New
            // Visualist Window. Routes through the Visualist window
            // registry so the new sibling is registered + activated; the
            // Hub-embedded MainView continues to live in the pillar-tab
            // region. Fire-and-forget the returned Task — the sibling
            // owns its own lifecycle.
            case "visualist.window.new":               _ = _visualistView.OpenNewWindowAsync(); break;
            // Window → Preset Gallery.
            // Opens a non-modal sibling window listing every built-in
            // WidgetPreset; Apply routes through VisualistViewModel.ApplyPreset.
            case "visualist.window.presetGallery":     _visualistView.OpenPresetGallery(); break;
            // Tools → Media Library (CHANGELOG 0.6.4 affordance
            // restored). Fire-and-forget the Task; the dialog's lifecycle
            // is owned by the ContentDialog itself.
            case "visualist.tools.mediaLibrary":       _ = _visualistView.ShowMediaLibraryAsync(); break;
            // File ▸ Import Media (Ctrl+I)
            // and Window ▸ Switch. The MainView command methods are present; these two
            // dispatch cases were the missing wiring.
            case "visualist.file.importMedia":         _ = _visualistView.ImportMediaAsync(); break;
            case "visualist.window.switch":            _ = _visualistView.ShowWindowSwitcherAsync(); break;
            default:
                GlobalLogger.Log($"Visualist menu '{item}' has no handler yet.",
                    "MainWindow", LogLevel.System);
                break;
        }
    }

    private void HandleFileMenu(string item)
    {
        switch (item)
        {
            case "openLogicFolder":
                OpenFolder(ResolveLogicDirectory());
                break;
            case "openAssetsFolder":
                OpenFolder(ResolveAssetsDirectory());
                break;
            case "openActionPackFolder":
                OpenFolder(ResolveStreamerBotDirectory());
                break;
            case "exit":
                // Route through Close() like the traffic-light X so the
                // unsaved-work prompt AND the shutdown coordinator both run.
                // Application.Current.Exit() skipped Window.Closed entirely —
                // no teardown, no coordinator, and the process left through
                // the natural XAML/CLR exit whose native DLL teardown can
                // wedge into an unkillable zombie (the "app doesn't shut
                // down, update fails until reboot" report).
                Close();
                break;
        }
    }

    private void HandleViewMenu(string item)
    {
        // EventLog has no fixed
        // slot in the 4-pane grid; clicking View → Event Log opens it in a
        // new pop-out window. Out-of-band path so we don't have to expand
        // the PopOutKind enum + state-restore plumbing for a panel the
        // workspace doesn't embed.
        if (string.Equals(item, "eventLog", StringComparison.Ordinal))
        {
            if (!ReferenceEquals(MainPaneRegion.Content, _hubWorkspace))
                MainPaneRegion.Content = _hubWorkspace;
            _hubWorkspace.OpenEventLogPopOut();
            return;
        }

        // "Recent Webhooks" tail
        // panel, out-of-band of the 4-pane workspace grid (same shape as
        // eventLog above). Each click spawns a fresh pop-out window whose
        // VM subscribes to HUDServer.OnWebhookFired.
        if (string.Equals(item, "webhook", StringComparison.Ordinal))
        {
            if (!ReferenceEquals(MainPaneRegion.Content, _hubWorkspace))
                MainPaneRegion.Content = _hubWorkspace;
            _hubWorkspace.OpenWebhookPopOut();
            return;
        }

        // View toggles only apply when the Hub workspace is visible. Forward
        // to HubWorkspaceView so the matching panel region toggles. Click on
        // a View item while Architect/Visualist is active foregrounds the
        // Hub tab first (otherwise the toggle would be invisible).
        if (!ReferenceEquals(MainPaneRegion.Content, _hubWorkspace))
            MainPaneRegion.Content = _hubWorkspace;

        ContentControl? region = item switch
        {
            "liveFeed"  => _hubWorkspace.LiveFeedRegion,
            "chat"      => _hubWorkspace.ChatRegion,
            "script"    => _hubWorkspace.ScriptRegion,
            "systemLog" => _hubWorkspace.SystemLogRegion,
            _ => null,
        };
        if (region is null) return;
        region.Visibility = region.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void HandleToolsMenu(string item)
    {
        switch (item)
        {
            case "settings":
                _ = ShowSettingsAsync();
                break;
            case "documentation":
                // In-app documentation. Replaces the previous
                // Process.Start of the GitHub README with a singleton-ish
                // WinUI 3 Window driven by HubFeatureRegistry. Subsequent
                // Tools → Documentation clicks activate the existing window
                // rather than spawning duplicates.
                DocumentationWindow.OpenOrFocus();
                break;
            case "checkUpdates":
                _ = RunUpdateCheckAsync();
                break;
        }
    }

    private void HandleHelpMenu(string item)
    {
        switch (item)
        {
            case "about":
                // Pre-fix Help →
                // About only logged the version line. Open the proper About
                // dialog instead; the System Log line is kept as a fallback
                // breadcrumb so a missing XamlRoot still leaves a trace.
                {
                    var xamlRootForAbout = Content?.XamlRoot;
                    if (xamlRootForAbout is null)
                    {
                        GlobalLogger.Log(
                            $"Phoenix Controls — {ResolveVersionLabel()} (About dialog unavailable: no XamlRoot)",
                            "MainWindow", LogLevel.System);
                    }
                    else
                    {
                        _ = AboutDialog.ShowAsync(xamlRootForAbout);
                    }
                }
                break;
            case "openGithub":
                OpenUrl(GitHubUrl);
                break;
            case "sampleGraphs":
                // Re-open the Welcome dialog ignoring the SeenWelcomeDialog
                // gate, then route to Architect on pick — mirrors App's
                // first-launch RunWelcomeAndRouteAsync but inline because that
                // helper is private to App. Fire-and-forget through
                // HubAsyncBoundary so menu handlers stay sync void.
                var xamlRoot = Content?.XamlRoot;
                if (xamlRoot is null) break;
                _ = HubAsyncBoundary.SafeRunAsync(
                    async () =>
                    {
                        var (picked, path) = await App.ShowWelcomeDialogAsync(xamlRoot);
                        if (picked && !string.IsNullOrEmpty(path))
                            NavigateTo(PillarKind.Architect, openTarget: path);
                    },
                    "MainWindow", "App.ShowWelcomeDialogAsync (Help → Sample Graphs)");
                break;
            case "readme":
                // In-app HTML README — bundled offline page in the DocViewer.
                DocViewerWindow.OpenOrFocus(new DocViewRequest(
                    "readme.html", Title: "Phoenix Controls — README"));
                break;
            case "changelog":
                // In-app HTML Changelog — bundled offline page in the DocViewer.
                DocViewerWindow.OpenOrFocus(new DocViewRequest(
                    "changelog.html", Title: "Phoenix Controls — Changelog"));
                break;
        }
    }

    private async System.Threading.Tasks.Task ShowSettingsAsync()
    {
        try
        {
            var dialog = new SettingsDialog
            {
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("MainWindow", "ShowSettingsAsync", ex);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            Directory.CreateDirectory(path);
            SysProcess.Start(new SysProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("MainWindow", $"OpenFolder failed for '{path}'", ex);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            SysProcess.Start(new SysProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("MainWindow", $"OpenUrl failed for '{url}'", ex);
        }
    }

    private static string ResolveLogicDirectory()
    {
        string rel = ConfigManager.Current.LogicDirectory;
        if (string.IsNullOrEmpty(rel)) rel = "data/logic";
        if (Path.IsPathRooted(rel)) return rel;

        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(probe); i++)
        {
            string candidate = Path.Combine(probe, rel);
            if (Directory.Exists(candidate)) return candidate;
            probe = Path.GetDirectoryName(probe);
        }
        return Path.Combine(AppContext.BaseDirectory, rel);
    }

    private static string ResolveAssetsDirectory()
    {
        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(probe); i++)
        {
            string candidate = Path.Combine(probe, "data", "assets");
            if (Directory.Exists(candidate)) return candidate;
            probe = Path.GetDirectoryName(probe);
        }
        return Path.Combine(AppContext.BaseDirectory, "data", "assets");
    }

    // data/streamerbot — ships the Phoenix Controls action-pack import file
    // (PhoenixActionPack.sb) + its setup guide (PhoenixActionPack.md). Mirrors
    // ResolveAssetsDirectory so the File-menu entry opens it the same way.
    private static string ResolveStreamerBotDirectory()
    {
        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(probe); i++)
        {
            string candidate = Path.Combine(probe, "data", "streamerbot");
            if (Directory.Exists(candidate)) return candidate;
            probe = Path.GetDirectoryName(probe);
        }
        return Path.Combine(AppContext.BaseDirectory, "data", "streamerbot");
    }

    private static string ResolveVersionLabel()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? "v0.0.0"
            : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private void WireUpdateChecker()
    {
        _updateChecker = new UpdateChecker();
        _updateChecker.StatusChanged += OnUpdateStatusChanged;

        if (!ConfigManager.Current.UpdateCheckOnStartup) return;

        // Defer the eager GitHub HTTPS round-trip
        // until after the first Loaded fire so it doesn't compete with
        // first-frame paint and panel inflation. ChromeBar fires Loaded once
        // the visual tree is up; wrap with a one-shot self-detach so the
        // check doesn't re-run on theme/visual reloads. The old code wrapped
        // CheckAsync in an extra `async () => { _ = await ... }` closure plus
        // ConfigureAwait(false) — neither did anything useful.
        _onDeferStartupUpdateCheck = null;
        _onDeferStartupUpdateCheck = (sender, args) =>
        {
            if (_onDeferStartupUpdateCheck is not null)
                ChromeBar.Loaded -= _onDeferStartupUpdateCheck;
            _onDeferStartupUpdateCheck = null;
            if (_updateChecker is null) return;
            _ = HubAsyncBoundary.SafeRunAsync(
                () => _updateChecker.CheckAsync(),
                "MainWindow", "UpdateChecker.CheckAsync (startup, deferred)");
        };
        ChromeBar.Loaded += _onDeferStartupUpdateCheck;
    }

    private async System.Threading.Tasks.Task RunUpdateCheckAsync()
    {
        if (_updateChecker is null) return;
        GlobalLogger.Log("Checking for updates…", "UpdateChecker", LogLevel.System);
        await _updateChecker.CheckAsync().ConfigureAwait(false);
    }

    private void OnUpdateStatusChanged(UpdateStatus status)
    {
        switch (status)
        {
            case UpdateStatus.UpToDate ut:
                GlobalLogger.Log($"Phoenix Controls is up to date (local v{ut.LocalSha}).",
                    "UpdateChecker", LogLevel.System);
                break;
            case UpdateStatus.ReleaseAvailable ra:
                GlobalLogger.Log(
                    $"Update available: {ra.LocalVersion} → {ra.RemoteTag}.",
                    "UpdateChecker", LogLevel.System);
                // StatusChanged fires on a thread-pool continuation
                // (CheckAsync runs ConfigureAwait(false)); the prompt is a
                // ContentDialog, so marshal onto the UI thread first.
                DispatcherQueue?.TryEnqueue(() =>
                {
                    _ = HubAsyncBoundary.SafeRunAsync(
                        () => ShowUpdatePromptAsync(ra),
                        "MainWindow", "update prompt");
                });
                break;
            case UpdateStatus.NetworkError ne:
                // Demoted to Debug — the underlying cause (HTTP non-success or
                // missing release) is already logged at UpdateChecker.cs's HTTP
                // call site (and 404/no-release is silenced there entirely as a
                // normal first-run state). Logging again here at System tier
                // was producing a duplicate "Update check failed: …" line per
                // start for projects without a published Release.
                GlobalLogger.Log($"Update check failed: {ne.Message}",
                    "UpdateChecker", LogLevel.Debug);
                break;
        }
    }

    /// <summary>
    /// Modal update prompt — fires on every launch (and every manual
    /// re-check) while a newer release exists. Deliberately NO per-version
    /// suppression: the prompt keeps nudging until the user actually
    /// updates or turns the startup check off in Settings. "Install &amp;
    /// restart" hands off to the same <see cref="UpdateApplyFlow"/> tail as
    /// Settings → Force Download so both entry points share one apply path.
    /// </summary>
    private async Task ShowUpdatePromptAsync(UpdateStatus.ReleaseAvailable release)
    {
        if (_updatePromptOpen) return;
        // The startup check is deferred past the first Loaded fire, so a
        // missing XamlRoot here means the window is tearing down — skip
        // quietly; the next launch prompts again.
        XamlRoot? root = (Content as FrameworkElement)?.XamlRoot;
        if (root is null) return;

        _updatePromptOpen = true;
        try
        {
            // Phoenix chrome — the prompt is code-built (dynamic version
            // strings), so it can't inherit a XAML dialog's StaticResource
            // brushes; pull the same Coal/Ember tokens the About/Settings
            // dialogs use from the app resources so it reads as part of the
            // shell family instead of a bare WinUI dialog.
            var eyebrow = new TextBlock
            {
                Text = "PHOENIX CONTROLS",
                FontFamily = ResolveFont("DisplayFont", "Segoe UI"),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 180,
                Foreground = ResolveBrush("EmberPrimaryBrush", Microsoft.UI.Colors.Orange),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var body = new TextBlock
            {
                Text = string.Format(
                    Localizer.T("dialog.update_prompt.body_format",
                        "Phoenix Controls {0} is available — you are running {1}.\n\nInstall now? Hub closes, applies the update, and restarts automatically."),
                    release.RemoteTag, release.LocalVersion),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = ResolveFont("SansFont", "Segoe UI"),
                FontSize = 13,
                Foreground = ResolveBrush("CoalBodyTextBrush", Microsoft.UI.Colors.Gainsboro),
            };
            var promptContent = new StackPanel { Width = 420 };
            promptContent.Children.Add(eyebrow);
            promptContent.Children.Add(body);

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = Localizer.T("dialog.update_prompt.title", "Update available"),
                Content = promptContent,
                PrimaryButtonText = Localizer.T("dialog.update_prompt.install", "Install & restart"),
                CloseButtonText   = Localizer.T("dialog.update_prompt.later", "Later"),
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Dark,
                Background = ResolveBrush("CoalShellBrush", Microsoft.UI.Colors.Black),
                BorderBrush = ResolveBrush("CoalCardBrush", Microsoft.UI.Colors.DimGray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
            };

            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // WinUI 3 allows one ContentDialog per XamlRoot — another
                // dialog (e.g. the first-run Welcome) may already be up.
                // Not worth a retry loop: the prompt re-fires next launch.
                GlobalLogger.Log(
                    $"Update prompt skipped — another dialog is likely open ({ex.Message}).",
                    "MainWindow", LogLevel.Debug);
                return;
            }
            if (result != ContentDialogResult.Primary) return;

            // Prompt is closed by now (ShowAsync returned), so the progress
            // dialog may take the XamlRoot. On success Hub is already exiting.
            UpdateApplyFlow.BeginApplyWithProgress(release, root, "MainWindow");
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    /// <summary>
    /// Resolve an app-resource brush by key for a runtime-built dialog, falling
    /// back to a solid colour if the key is missing or non-Brush. Mirrors
    /// <c>SettingsDialog.ResolveBrushOrFallback</c> so code-built prompts pick
    /// up the Phoenix Coal/Ember tokens the same way the XAML dialogs do.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.Brush ResolveBrush(string key, global::Windows.UI.Color fallback)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var found)
            && found is Microsoft.UI.Xaml.Media.Brush b)
            return b;
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(fallback);
    }

    /// <summary>
    /// Resolve an app-resource font family (the <c>*Font</c> tokens are
    /// <c>&lt;x:String&gt;</c> resources, so a direct cast would throw) with a
    /// system fallback.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.FontFamily ResolveFont(string key, string fallback)
    {
        string family = (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var found)
            && found is string s && !string.IsNullOrWhiteSpace(s))
            ? s : fallback;
        return new Microsoft.UI.Xaml.Media.FontFamily(family);
    }
}
