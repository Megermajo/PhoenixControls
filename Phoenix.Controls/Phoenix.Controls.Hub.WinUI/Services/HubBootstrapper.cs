using System.IO;
using System.Net;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.Core.Translation;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Phoenix.Controls.ViewerServer;

namespace Phoenix.Controls.Hub.WinUI.Services;

// Boot orchestrator for the WinUI Hub. Drives the numbered boot steps and
// reports each through the IProgress<SplashProgress> contract (defined in
// App.xaml.cs as `record struct SplashProgress(string Status, double Fraction)`).
//
// ── Wire-up (App.xaml.cs) ───────────────────────────────────────────────
// App.OnLaunched → OnLaunchedCore shows the splash, runs the heavy pre-UI
// bootstrap (log writer / AppData migrator / DB.Initialize / Localizer),
// then calls BootAsync(dispatcher, navigator, progress). The navigator is a
// DeferredPillarNavigator — the real implementation (MainWindow) can't exist
// until boot completes, so the shim is Attach()ed to MainWindow right after
// construction and forwards from then on; pre-attach calls are dropped with
// a log line (nothing is queued or replayed). Panels are injected via
// MainWindow.SetPanels(services, new PanelFactory()) before Activate so the
// first frame paints with content; the splash closes on first Loaded.
//
// IUiDispatcher wraps a Microsoft.UI.Dispatching.DispatcherQueue on the App
// side. This bootstrapper deliberately references no WinUI 3 types — the
// Hub.WinUI csproj brings WindowsAppSDK for chrome/splash/MainWindow code,
// but the bridge stays SDK-agnostic so a test fixture can drive it with a
// synchronous dispatcher.
// ────────────────────────────────────────────────────────────────────────
public static class HubBootstrapper
{
    // Step 8 wires the opt-in event-trigger services
    // (Hotkey / Clipboard / WebSocket-server / LiveCaption). They are gated on
    // their respective AppConfig flags so a fresh-install Hub doesn't claim
    // global hotkeys, watch the clipboard, bind an extra port, or poll
    // LiveCaptions until the streamer opts in via Settings.
    private const int TotalBootSteps = 8;

    // Hold strong references so the GC doesn't reclaim
    // a service while its background loop is still expected to fire. The
    // existing HUDServer / RemoteBridgeServer live forever via HubHost; these
    // four don't (yet) have a process-wide accessor, so the bootstrapper itself
    // owns them. Torn down at shutdown via ShutdownOptInServicesAsync so the
    // Hub.WinUI process can exit cleanly (pre-fix the LiveCaptionService poll
    // loop was a foreground Task that kept the CLR alive after window close).
    private static HotkeyService?           s_hotkeyService;
    private static ClipboardService?        s_clipboardService;
    private static WebSocketServerService?  s_webSocketServerService;
    private static LiveCaptionService?      s_liveCaptionService;
    // Direct OBS WebSocket v5
    // client. Pinned for the same GC reason as the other opt-in services;
    // the message pump inside Websocket.Client is otherwise reclaimable
    // shortly after BootAsync returns, silently killing inbound OBS
    // subscriptions. Constructed only when AppConfig.ObsWebSocketEnabled
    // is true; torn down in ShutdownOptInServicesAsync.
    private static ObsWebSocketClient?      s_obsWebSocketClient;

    // LayerWatcher owns a FileSystemWatcher on data/layers/*.phxlayer.
    // Pinned here for the same reason as the opt-in services above: the watcher
    // is the only strong reference, and without a process-lifetime pin the GC
    // can reclaim the LayerWatcher (and its FileSystemWatcher) shortly after
    // BootAsync returns, silently severing layer hot-reload. Pre-fix the
    // production path constructed no LayerWatcher at all — the layer registry
    // stayed empty and every /hud/&lt;id&gt; route 404'd.
    private static LayerWatcher? s_layerWatcher;

    // LogicWatcher + SchedulerService were both
    // never instantiated in production after T15. Logic hot-reload on .phx
    // save was dead (only an explicit Settings-driven Refresh got the
    // registry to pick up changes), and every Schedule.Cron / RunAt /
    // Recurring entry across both AppConfig.Schedules and on_schedule*/
    // on_interval headers in .phx files silently never fired. Pin both
    // here for the same GC reason as the opt-in services above. Wired in
    // BootAsync step 7; torn down in ShutdownOptInServicesAsync.
    private static LogicWatcher? s_logicWatcher;

    // V2 ViewerServer wiring. The server (HTTP+WS on :18090
    // serving the WebViewer bundle + a read-only snapshot of Hub state)
    // and its Hub-side read-model adapter both live for the process
    // lifetime once started. Pinned in statics for the same reason as
    // the opt-in services above — there's no graceful Hub shutdown today.
    private static ViewerServer.ViewerServer? s_viewerServer;
    private static HubViewerReadModel?        s_viewerReadModel;

    // Process-lifetime CTS handed to ViewerServer.StartAsync so its accept
    // loop is genuinely cancellable on Hub shutdown instead of relying on
    // listener.Close() to abort GetContextAsync mid-flight.
    private static CancellationTokenSource?   s_optInCts;

    public static async Task<HubServices> BootAsync(
        IUiDispatcher dispatcher,
        IPillarNavigator navigator,
        IProgress<SplashProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(navigator);

        // Snapshot AppConfig once at boot. The legacy
        // RemoteBridgeServer at step 4 is gated on cfg.RemoteEnabled and the
        // ViewerServer at the end of the method uses the same source-of-truth;
        // pulling the read into a local keeps the gate site obvious and
        // matches ConfigManager.Current's intent (it's a snapshot, not a live
        // observer — Settings changes don't retro-modify a running boot).
        var cfg = ConfigManager.Current;

        // ── Step 1 — open the SQLite databank ────────────────────────────
        // DB.Instance.Initialize is now
        // run during PillarBootstrap.RunHeavyPreUiAsync before BootAsync is
        // entered, so by the time we get here the databank is already open.
        // The call is idempotent (singleton-guarded), but we still skip it
        // here to keep the progress label honest and shave the no-op lock.
        Report(progress, 1, Localizer.T("splash.status.databank", "OPENING DATABANK · phoenix_v3.db"));
        ct.ThrowIfCancellationRequested();

        // ── Step 2 — start the GlobalLogger writer ───────────────────────
        // The writer drains the bounded log channel into the DB. Returns a
        // long-running Task; SafeRunAsync guards it so a future writer fault
        // surfaces in the log instead of crashing the boot.
        Report(progress, 2, Localizer.T("splash.status.logger", "STARTING LOGGER"));
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => GlobalLogger.StartLogWriterAsync(),
            "HubBootstrapper", "GlobalLogger.StartLogWriterAsync");
        GlobalLogger.Log("Phoenix Controls Hub (WinUI) initialising.", "HubBootstrapper", LogLevel.System);
        ct.ThrowIfCancellationRequested();

        // ── Step 3 prep — construct HUDServer + wire LayerRuntime dispatcher ─
        // The dispatcher MUST be assigned BEFORE Bus.StartAsync starts
        // accepting connections. Pre-fix the assignment lived in Step 4 *after*
        // the bus was already listening: any VISUAL_TRIGGER routed to
        // LayerRuntime.EnqueueTriggerAsync during that window hit the "dispatcher
        // not configured" branch and was silently dropped. Constructing the
        // HUDServer instance here (the only thing the dispatcher assignment
        // needs) closes that race entirely — HUDServer.StartAsync still fires
        // at step 4 below, so the HTTP listener bind hasn't shifted.
        //
        // WS.Initialize() reads ConfigManager.Current.HUDServerPort; it's a
        // pure side-effect-free initializer with no network IO of its own.
        WS.Instance.Initialize();
        var hudServer = new HUDServer(WS.Instance.HUDPort);
        HubHost.HUD = hudServer;
        LayerRuntime.Instance.Dispatcher = hudServer.SendToLayerAsync;

        // Auto-sync to OBS: when LayerWatcher (re)registers a .phxlayer, push
        // LAYER_RELOADED to every connected /hud/<id> browser so OBS browser
        // sources — and Visualist's live canvas preview — refresh WITHOUT a
        // manual OBS "Refresh cache of current page". LayerRegistry raises
        // LayerReloaded on every register/replace; HUDServer.PushLayerReloadedAsync
        // stamps the reload grace-window and broadcasts. The method, the event,
        // and compositor.js's LAYER_RELOADED handler all existed — this single
        // subscription, which both HUDServer and LayerRegistry XML-doc reference as
        // "HubBootstrapper wires LayerRegistry.LayerReloaded to this", was never
        // actually made, so saves only reached OBS after a manual refresh. Wired
        // BEFORE LayerWatcher.Start() below so the startup scan's registrations are
        // covered too (no /hud clients are connected yet, so they no-op harmlessly).
        LayerRegistry.Instance.LayerReloaded += layerId =>
        {
            // (a) OBS browser sources AND Visualist's hidden capture-page WebView2
            //     (both connected to /hud/<id>) reload via compositor.js's
            //     LAYER_RELOADED handler → location.reload().
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => hudServer.PushLayerReloadedAsync(layerId),
                "HubBootstrapper", "PushLayerReloadedAsync");

            // (b) Visualist's design-time pillar (VisualistBusClient → LayerCanvasView.
            //     OnBusLayerReloaded) forces an off-cadence canvas-preview capture and
            //     recovers a preview whose last navigation failed. The handler already
            //     existed but nothing was sending this bus message (same missing wire
            //     as the /hud broadcast above), so the live canvas preview previously
            //     only refreshed via the capture page's own /hud reload, lagging up to
            //     a capture interval. Target="*" is harmless for Architect (ignores it).
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => Bus.Instance.BroadcastAsync(new BusMessage
                {
                    Type    = "LAYER_RELOADED",
                    Source  = "Hub",
                    Target  = "*",
                    Payload = layerId,
                }),
                "HubBootstrapper", "Bus LAYER_RELOADED");
        };

        // ── Step 3 — IPC bus on 18081 ────────────────────────────────────
        // StartAsync runs the HttpListener accept loop forever; we don't await.
        // The shutdown CT is the bootstrap CT — outer code controls lifetime.
        // Pass the bootstrap CT as the expected token: a shutdown
        // cancellation on this exact CT is graceful; any other OCE (e.g. a
        // faulted upstream race) flows through to GlobalLogger.Error.
        Report(progress, 3, Localizer.T("splash.status.bus", "BINDING IPC BUS · 18081"));
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => Bus.Instance.StartAsync(ct),
            "HubBootstrapper", "Bus.StartAsync",
            ct);
        // Replace the hard Task.Delay(100) with
        // a poll on Bus.IsListening. The listener typically flips IsListening
        // inside ~5-15 ms; the 100 ms sleep was pure latency tax with no
        // observability if the bind actually failed. Cap at 500 ms so a
        // genuinely stuck StartAsync (port conflict, ACL block) doesn't pin
        // the splash forever — the SafeRun wrapper still surfaces the fault.
        await WaitForBusListeningAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);

        // ── Step 4 — HUD overlay HTTP+WS server ──────────────────────────
        // HUDServer is NOT a singleton (MainForm constructs it). Its
        // instance + LayerRuntime.Dispatcher wiring moved up above the bus
        // bind; this step now just kicks off the HTTP+WS accept
        // loop for the already-constructed instance. The dispatcher has been
        // live since before Step 3's listener started accepting connections,
        // so any VISUAL_TRIGGER that races in cannot land on a null dispatcher.
        Report(progress, 4, Localizer.T("splash.status.hud", "STARTING HUD SERVER · 18080"));

        // Construct LayerWatcher and start it so data/layers/
        // is scanned into LayerRegistry and subsequent .phxlayer saves
        // hot-reload. Pre-fix only test fixtures constructed a watcher in
        // production; the registry stayed empty, /hud/<id> 404'd, and every
        // VISUAL_TRIGGER fast-failed with "unknown layer". Pin to a static
        // field — the FileSystemWatcher inside the watcher is GC-rooted only
        // through the LayerWatcher instance, so without a process-lifetime
        // strong ref the watcher is reclaimable and reloads silently stop
        // shortly after BootAsync returns (same pattern as s_hotkeyService /
        // s_clipboardService above). Start() itself is fast (a directory
        // enumeration + FileSystemWatcher subscribe); deserializing each
        // discovered layer happens synchronously inside ScanAll, but that
        // pre-T15 cost was already in the WinForms shell's bring-up so the
        // performance characteristic is unchanged.
        // Perf: LayerWatcher.Start() enumerates data/layers/ and synchronously
        // deserializes every discovered .phxlayer inside ScanAll (with
        // Thread.Sleep retry loops on locked files), which blocks the splash
        // dispatcher. Offload to a thread-pool worker — mirroring the
        // ScriptManager step below — so the splash storyboard stays responsive;
        // we await before proceeding so LayerRegistry is populated for Step 4+.
        await Task.Run(() =>
        {
            try
            {
                s_layerWatcher = new LayerWatcher();
                s_layerWatcher.Start();
                GlobalLogger.Log(
                    "LayerWatcher started — layer hot-reload active on '"
                    + s_layerWatcher.LayersPath + "'.",
                    "HubBootstrapper", LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "LayerWatcher startup failed", ex);
                s_layerWatcher = null;
            }
        }, ct).ConfigureAwait(false);

        _ = AsyncErrorBoundary.SafeRunAsync(
            () => hudServer.StartAsync(),
            "HubBootstrapper", "HUDServer.StartAsync");

        // RemoteBridgeServer (legacy Viewer-pairing wire) is now
        // gated on AppConfig.RemoteEnabled. Pre-fix the bridge constructed
        // and bound port 18083 on every Hub boot regardless of the toggle —
        // identical to the gating bug v2 ViewerServer documented at the
        // bottom of this method, just on the older surface. ScriptManager
        // broadcasts process-lifecycle to paired Viewers via the
        // null-conditional HubHost.RemoteBridge?.BroadcastProcessStateAsync,
        // so the off-branch (HubHost.RemoteBridge stays null) just no-ops
        // those broadcasts cleanly without bricking other Hub services.
        if (cfg.RemoteEnabled)
        {
            try
            {
                var remoteBridge = new RemoteBridgeServer();
                HubHost.RemoteBridge = remoteBridge;
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => remoteBridge.StartAsync(),
                    "HubBootstrapper", "RemoteBridgeServer.StartAsync");
                GlobalLogger.Log(
                    "RemoteBridgeServer starting (AppConfig.RemoteEnabled=true).",
                    "HubBootstrapper", LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "RemoteBridgeServer startup failed", ex);
                HubHost.RemoteBridge = null;
            }
        }
        else
        {
            GlobalLogger.Log(
                "RemoteBridgeServer disabled — AppConfig.RemoteEnabled=false. " +
                "Viewer-pairing broadcasts will no-op.",
                "HubBootstrapper", LogLevel.System);
        }
        ct.ThrowIfCancellationRequested();

        // ── Step 5 — Streamer.bot client connect ─────────────────────────
        // WS uses exponential-backoff reconnect internally, so a
        // bot that's down at boot doesn't block the splash; we just kick off
        // the connect attempt and proceed. The status strip shows the
        // connection state once the panel is wired (surfaced
        // via IConnectionStatus.StreamerBot).
        Report(progress, 5, Localizer.T("splash.status.streamer_bot", "CONNECTING TO STREAMER.BOT · 8080"));
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => WS.Instance.StartAsync(),
            "HubBootstrapper", "WS.StartAsync");
        ct.ThrowIfCancellationRequested();

        // ── Step 6 — ScriptManager + ScriptRegistry ──────────────────────
        // Touching Instance constructs the singleton; the ctor registers
        // every command handler with the engine. After that we point the
        // registry at the configured logic directory and load the .phx
        // catalogue eagerly so Snapshot() returns real data once panels open.
        //
        // The ScriptManager ctor itself calls
        // ScriptRegistry.LoadScripts internally (ScriptManager.cs end-of-ctor)
        // *and* this bootstrapper called it a second time on the UI thread.
        // File enumeration over data/logic/ on slow disks (OneDrive-backed
        // AppData, antivirus interception) blocked the splash. Run the whole
        // step on a thread-pool worker so the dispatcher stays free for the
        // splash storyboard. Subsequent steps still see the populated registry
        // because we await the Task.Run before returning.
        Report(progress, 6, Localizer.T("splash.status.scripts", "LOADING SCRIPTS"));
        string logicPath = ResolveLogicPath();
        await Task.Run(() =>
        {
            _ = ScriptManager.Instance;     // ctor side effects: command registration + initial LoadScripts
            // ScriptRegistry.LoadScripts is idempotent; the second call here
            // exists because ScriptManager's ctor resolves logicPath from
            // ConfigManager.Current.LogicDirectory directly while the
            // bootstrapper's ResolveLogicPath walks up from BaseDirectory to
            // find the actual data/logic/ tree. The second call ensures the
            // registry ends up pointed at the correct path even when ctor's
            // resolution missed.
            ScriptRegistry.Instance.LoadScripts(logicPath);
            // Live-process templates live under <logicPath>/processes/<graph>/ and
            // are NOT loaded as normal scripts. ProcessInstanceManager pulls one at
            // process.start time; load the index now so the first start is a lookup.
            ProcessTemplateRegistry.Instance.Load(logicPath);
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // ── Step 7 — wire bus → script triggers ──────────────────────────
        // Bus.RouteIncomingMessage already calls
        // ScriptManager.ExecuteOnBusScriptsAsync for every inbound bus
        // envelope — the wiring is implicit in the singleton's behaviour.
        // The work for this step is therefore: (a) run any on_startup blocks
        // so scripts that init databank state are alive before the user sees
        // the UI, and (b) emit a sentinel log so a future bring-up regression
        // is visible in the System Log panel.
        Report(progress, 7, Localizer.T("splash.status.triggers", "WIRING TRIGGERS"));
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => ScriptManager.Instance.ExecuteOnStartupScriptsAsync(),
            "HubBootstrapper", "ExecuteOnStartupScriptsAsync");

        // LogicWatcher: never instantiated in
        // production after the T15 WinForms-shell retirement. Without it,
        // edits to .phx files in data/logic/ never reach the runtime —
        // operators had to restart Hub or open the Settings dialog to pick
        // up changes. Construct + Start() here so a saved .phx triggers
        // ScriptRegistry.Refresh on its 250 ms debounce.
        try
        {
            s_logicWatcher = new LogicWatcher();
            s_logicWatcher.Start();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "LogicWatcher startup failed", ex);
            s_logicWatcher = null;
        }

        // SchedulerService: same gap. Singleton existed since T15
        // but Start() was never called, so every Schedule.Cron / RunAt /
        // Recurring entry across AppConfig.Schedules AND in-script
        // on_schedule(...) / on_schedule_once(...) / on_interval(...)
        // header blocks was dormant. Wire it AFTER LogicWatcher so the
        // OnRefresh hook (below) is attachable, and AFTER step 6 because
        // LoadSchedulesFromScripts re-reads the same logic dir.
        try
        {
            SchedulerService.Instance.Start();
            if (s_logicWatcher is not null)
            {
                // Reload schedules whenever a saved .phx might have added or
                // removed an on_schedule(...) / on_interval(...) header — the
                // OnRefresh fires post-debounce after ScriptRegistry.Refresh,
                // so the registry state SchedulerService re-scans is fresh.
                s_logicWatcher.OnRefresh += static () =>
                {
                    // Refresh process templates first so a freshly-saved template is
                    // available for the next process.start (live instances keep their
                    // start-time snapshot — see ProcessInstanceManager).
                    try { ProcessTemplateRegistry.Instance.Refresh(); }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("HubBootstrapper",
                            "ProcessTemplateRegistry.Refresh from LogicWatcher.OnRefresh failed", ex);
                    }
                    try { SchedulerService.Instance.Reload(); }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error(
                            "HubBootstrapper",
                            "SchedulerService.Reload from LogicWatcher.OnRefresh failed",
                            ex);
                    }
                };
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "SchedulerService.Start failed", ex);
        }

        GlobalLogger.Log("Hub services online. Bus / HUD / Streamer.bot / Scripts / Scheduler wired.",
            "HubBootstrapper", LogLevel.System);

        // ── Step 8 — opt-in event-trigger services ───────────────────────
        // HotkeyService / ClipboardService / WebSocketServerService
        // shipped wired through CommandManifest, ExporterRegistry and Settings
        // checkboxes in 0.7.0, but the runtime instances were never created.
        // Toggling Settings persisted a bit and did nothing. Construct them
        // here, gated on their AppConfig flags so a fresh install still boots
        // with zero global keystroke / clipboard / extra-port surface.
        //
        // LiveCaptionService has the same shape: implemented,
        // advertised, never instantiated. Same gating treatment.
        //
        // Gating at construction time is the primary defense (lazier
        // resource use — no HotkeyWindow, no ClipboardWindow, no HttpListener
        // bind, no UIA poll loop). The services also keep their internal
        // toggles where present, defense-in-depth.
        //
        // Each construct+Start is wrapped so one service's failure can't brick
        // Hub boot — the user sees a logged error and the remaining services
        // continue to come up.
        Report(progress, 8, Localizer.T("splash.status.opt_in_services", "STARTING OPT-IN SERVICES"));
        StartOptInServices();

        // ── Compose contract surface ─────────────────────────────────────
        // Single-HUB collapse: no LauncherService — pillar
        // navigation goes through IPillarNavigator, implemented by
        // Hub.MainWindow and threaded in by App.OnLaunched.
        var live     = new LiveFeedSource(dispatcher);
        var chat     = new ChatSource(dispatcher);
        var scripts  = new ScriptHostMonitor(dispatcher, navigator);
        var sysLog   = new SystemLogSource(dispatcher);
        var status   = new ConnectionStatus(dispatcher, hudServer);
        var layers   = new LayerRegistrySource(LayerRegistry.Instance);
        var services = new HubServices(live, chat, scripts, sysLog, status, layers);

        // V2 ViewerServer bring-up. Gated on
        // AppConfig.ViewerServerEnabled (default false) so a fresh-install
        // Hub doesn't bind :18090 or expose the v2 web bundle until the
        // streamer opts in. Wired here, AFTER HubServices composition, so
        // the read-model adapter can wrap the same panel-facing surface.
        // Bring-up is fire-and-forget (StartAsync internally awaits the
        // listener bind, then returns) and wrapped in SafeRunAsync so a
        // urlacl / port-conflict fault surfaces in the System Log without
        // bricking the rest of the boot.
        StartViewerServerIfEnabled(services);

        return services;
    }

    /// <summary>
    /// Bring up the opt-in event-trigger services
    /// (HotkeyService / ClipboardService / WebSocketServerService /
    /// LiveCaptionService). Each is gated on its AppConfig flag and wrapped
    /// in its own try block: a fault in one service must not bring down the
    /// others or the Hub bootstrap itself. The references are pinned in
    /// static fields so the GC can't reclaim a service while its background
    /// listener (Win32 hidden window, HttpListener accept loop, UIA poll
    /// task) is still expected to fire.
    /// </summary>
    private static void StartOptInServices()
    {
        var cfg = ConfigManager.Current;
        var scripts = ScriptManager.Instance;

        // Global hotkeys (on_hotkey("Ctrl+Shift+P"):)
        if (cfg.HotkeysEnabled)
        {
            try
            {
                s_hotkeyService = new HotkeyService(scripts);
                s_hotkeyService.Start();
                GlobalLogger.Log("HotkeyService started (AppConfig.HotkeysEnabled=true).",
                    "HubBootstrapper", LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "HotkeyService startup failed", ex);
                s_hotkeyService = null;
            }
        }

        // Clipboard watcher (on_clipboard:)
        if (cfg.ClipboardWatchEnabled)
        {
            try
            {
                s_clipboardService = new ClipboardService(scripts);
                s_clipboardService.Start();
                GlobalLogger.Log("ClipboardService started (AppConfig.ClipboardWatchEnabled=true).",
                    "HubBootstrapper", LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "ClipboardService startup failed", ex);
                s_clipboardService = null;
            }
        }

        // External WebSocket listener (on_websocket("name"):).
        // The "don't construct if disabled" gate here is the primary defense; the
        // service's own StartAsync short-circuit (added below) is defense-in-depth
        // so a bootstrap-side bug reaching StartAsync with the flag off still
        // surfaces a clear log entry rather than silently binding the port.
        if (cfg.WebSocketServerEnabled)
        {
            try
            {
                s_webSocketServerService = new WebSocketServerService(scripts);
                // Publish the
                // service via HubHost so the Hub StatusStrip badge can read
                // ConnectedClientCount + IsListening once per second without
                // peeking at the bootstrapper's private static field.
                HubHost.WebSocketServer = s_webSocketServerService;
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => s_webSocketServerService.StartAsync(),
                    "HubBootstrapper", "WebSocketServerService.StartAsync");
                GlobalLogger.Log("WebSocketServerService starting (AppConfig.WebSocketServerEnabled=true).",
                    "HubBootstrapper", LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "WebSocketServerService startup failed", ex);
                s_webSocketServerService = null;
                HubHost.WebSocketServer = null;
            }
        }

        // Windows 11 LiveCaptions UIA bridge.
        // Per the brief: call IsLiveCaptionsSupported during construction; if the
        // toggle is on but the OS doesn't support it, log a warning so the user
        // (on Win10 / pre-22621 Win11) understands why nothing is happening.
        // The service's PollLoopAsync also self-guards on the same check, but
        // surfacing the diagnostic at boot is much friendlier than silent skip.
        if (cfg.LiveCaptionsEnabled)
        {
            try
            {
                if (!LiveCaptionService.IsLiveCaptionsSupported(Environment.OSVersion.Version))
                {
                    GlobalLogger.Log(
                        "LiveCaptionService: AppConfig.LiveCaptionsEnabled=true but this OS "
                        + "(version " + Environment.OSVersion.Version + ") does not support "
                        + "Windows 11 LiveCaptions UIA discovery (requires build 22621+). "
                        + "Service will not start.",
                        "HubBootstrapper", LogLevel.Communication);
                }
                else
                {
                    s_liveCaptionService = new LiveCaptionService(cfg.LiveCaptionsAutoLaunch);

                    // Broadcast pipeline. Pre-fix
                    // captions were captured into the service's private buffer
                    // and the CaptionChanged event had no production subscriber
                    // (only test fixtures listened), so Caption.LiveCaption
                    // nodes + compositor.js's captionState waited on a
                    // CAPTION_UPDATE that was never emitted. Wire the chain
                    // here: capture → translate (passthrough by default) →
                    // gate on AppConfig.LiveCaptionsBroadcastToOverlays → push
                    // a CAPTION_UPDATE envelope to every active layer that
                    // either appears in LiveCaptionsAllowedLayers or — when
                    // that allowlist is empty — every layer (legacy fan-out).
                    s_liveCaptionService.CaptionChanged += async (text) =>
                    {
                        try
                        {
                            string translated = text;
                            // TranslateAsync is safe to call even with the
                            // passthrough translator (returns input unchanged);
                            // explicit try block so a translator fault doesn't
                            // poison the broadcast — overlays still get the
                            // raw caption on translation failure.
                            try
                            {
                                translated = await TranslationService.Instance
                                    .TranslateAsync(text, cfg.CaptionTargetLanguage ?? "")
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                GlobalLogger.Error(
                                    "LiveCaption",
                                    "TranslationService.TranslateAsync threw; broadcasting raw text",
                                    ex);
                            }

                            if (!cfg.LiveCaptionsBroadcastToOverlays) return;

                            var hud = HubHost.HUD;
                            if (hud is null) return;

                            var allow = cfg.LiveCaptionsAllowedLayers ?? new System.Collections.Generic.List<string>();
                            var payload = new
                            {
                                type   = "CAPTION_UPDATE",
                                text   = translated,
                                source = "live_caption",
                                lang   = cfg.CaptionTargetLanguage ?? "",
                                ts     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            };

                            if (allow.Count == 0)
                            {
                                await hud.BroadcastToAllLayersAsync(payload).ConfigureAwait(false);
                            }
                            else
                            {
                                foreach (var layerId in allow)
                                {
                                    try
                                    {
                                        await hud.SendToLayerAsync(layerId, payload).ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        GlobalLogger.Error(
                                            "LiveCaption",
                                            $"SendToLayerAsync('{layerId}') failed for CAPTION_UPDATE",
                                            ex);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            GlobalLogger.Error(
                                "LiveCaption",
                                "broadcast pipeline threw on CaptionChanged",
                                ex);
                        }
                    };

                    s_liveCaptionService.Start();
                    GlobalLogger.Log(
                        "LiveCaptionService started (AppConfig.LiveCaptionsEnabled=true, "
                        + "AutoLaunch=" + cfg.LiveCaptionsAutoLaunch
                        + ", BroadcastToOverlays=" + cfg.LiveCaptionsBroadcastToOverlays + ").",
                        "HubBootstrapper", LogLevel.System);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "LiveCaptionService startup failed", ex);
                s_liveCaptionService = null;
            }
        }

        // Direct OBS WS v5
        // event subscription. Off by default; when ObsWebSocketEnabled is
        // true we construct the client, route its EventReceived through
        // ScriptManager.DispatchObsEvent (powering on_obs handlers) AND
        // emit an OBS_EVENT bus broadcast for Architect's debug-trace
        // surface. Dual-routing mirrors WS.cs's OBS.SceneChanged dispatch:
        // the script and bus paths have different consumers
        // and lifetimes, so collapsing them would tie Architect debug
        // visibility to script subscription.
        if (cfg.ObsWebSocketEnabled)
        {
            try
            {
                s_obsWebSocketClient = new ObsWebSocketClient(
                    cfg.ObsWebSocketHost ?? "127.0.0.1",
                    cfg.ObsWebSocketPort,
                    cfg.ObsWebSocketPassword ?? string.Empty,
                    cfg.ObsEventSubscriptionMask);

                s_obsWebSocketClient.EventReceived += (eventType, payload) =>
                {
                    // Two independent fan-outs — see comment above.
                    // 1) Script-side: on_obs("<eventType>") handlers.
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        () => scripts.DispatchObsEvent(eventType, payload),
                        "HubBootstrapper", $"DispatchObsEvent({eventType})");
                    // 2) Bus-side: OBS_EVENT envelope so Architect's
                    //    debug-trace + future panels can show the live
                    //    event stream. Payload carries the raw OBS
                    //    eventData JSON; consumers parse as needed.
                    //    Target=* is broadcast (every connected client).
                    //    Hand-build the JSON because the inner eventData is
                    //    already a serialised JSON object we want to splice
                    //    in verbatim (an anonymous-object serialise would
                    //    double-quote it as a string). The eventType is run
                    //    through System.Text.Json.JsonSerializer.Serialize so
                    //    any embedded quotes / control chars are properly
                    //    escaped — OBS event names are PascalCase ASCII today
                    //    but we don't want to bake that assumption into the
                    //    wire format.
                    string envelopeJson = "{\"eventType\":"
                        + System.Text.Json.JsonSerializer.Serialize(eventType)
                        + ",\"eventData\":" + payload + "}";
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        () => Bus.Instance.BroadcastAsync(new Phoenix.Controls.Shared.Models.BusMessage
                        {
                            Type    = "OBS_EVENT",
                            Source  = "Hub",
                            Target  = "*",
                            Payload = envelopeJson,
                        }),
                        "HubBootstrapper", $"Bus.BroadcastAsync(OBS_EVENT/{eventType})");
                };

                // Share s_optInCts with ViewerServer / other opt-in services
                // so a unified shutdown cancellation cuts all of them at once.
                s_optInCts ??= new CancellationTokenSource();
                var obsConnectCt = s_optInCts.Token;
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => s_obsWebSocketClient.ConnectAsync(obsConnectCt),
                    "HubBootstrapper", "ObsWebSocketClient.ConnectAsync");

                GlobalLogger.Log(
                    "ObsWebSocketClient starting (AppConfig.ObsWebSocketEnabled=true, "
                    + "host=" + (cfg.ObsWebSocketHost ?? "127.0.0.1")
                    + ":" + cfg.ObsWebSocketPort
                    + ", subscriptionMask=0x" + cfg.ObsEventSubscriptionMask.ToString("X")
                    + ").",
                    "HubBootstrapper", LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "ObsWebSocketClient startup failed", ex);
                s_obsWebSocketClient = null;
            }
        }
    }

    /// <summary>
    /// Construct and start the v2 <see cref="ViewerServer.ViewerServer"/>
    /// when <see cref="AppConfig.ViewerServerEnabled"/> is set. Gated at
    /// construction time (no HttpListener bind unless enabled) so the
    /// fresh-install posture stays "loopback-only zero surface".
    /// </summary>
    private static void StartViewerServerIfEnabled(IHubServices services)
    {
        var cfg = ConfigManager.Current;
        if (!cfg.ViewerServerEnabled)
        {
            GlobalLogger.Log(
                "ViewerServer (v2) disabled — AppConfig.ViewerServerEnabled=false. " +
                "Skipping bind on port " + cfg.ViewerServerPort + ".",
                "HubBootstrapper", LogLevel.System);
            return;
        }

        try
        {
            s_viewerReadModel = new HubViewerReadModel(services, cfg.ViewerServerChannel);

            // AssetsRoot resolves under the running exe's bin tree because
            // the WebViewer ProjectReference is wired with
            // OutputItemType="Content" + the dist/** Link rewrite of
            // "ViewerAssets/...". The folder lands at {AppBase}/ViewerAssets.
            string assetsRoot = Path.Combine(AppContext.BaseDirectory, "ViewerAssets");

            var options = new ViewerServerOptions
            {
                Channel        = cfg.ViewerServerChannel,
                Port           = cfg.ViewerServerPort,
                LanModeEnabled = cfg.ViewerServerLan,
                BindAddress    = cfg.ViewerServerLan ? IPAddress.Any : IPAddress.Loopback,
                AssetsRoot     = assetsRoot,
            };

            s_viewerServer = new ViewerServer.ViewerServer(options, s_viewerReadModel);
            s_optInCts ??= new CancellationTokenSource();
            var startCt = s_optInCts.Token;
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => s_viewerServer.StartAsync(startCt),
                "HubBootstrapper", "ViewerServer.StartAsync");

            GlobalLogger.Log(
                "ViewerServer (v2) starting on port " + cfg.ViewerServerPort +
                " (channel='" + cfg.ViewerServerChannel + "', LAN=" + cfg.ViewerServerLan +
                ", assets='" + assetsRoot + "').",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "ViewerServer startup failed", ex);
            try { s_viewerReadModel?.Dispose(); } catch { }
            s_viewerServer = null;
            s_viewerReadModel = null;
        }
    }

    /// <summary>
    /// Tear down every service brought up by <see cref="StartOptInServices"/>
    /// plus the v2 <see cref="ViewerServer.ViewerServer"/>. Idempotent: safe to
    /// call twice; second call no-ops because every static field has been
    /// nulled. Each per-service call is wrapped so a fault in one teardown
    /// doesn't strand the rest still pinning the CLR.
    /// </summary>
    public static async Task ShutdownOptInServicesAsync()
    {
        // Snapshot + null the statics up front so a re-entry (e.g. App exiting
        // while a previous shutdown is still draining) sees an empty slate.
        var hotkey       = s_hotkeyService;
        var clipboard    = s_clipboardService;
        var webSocket    = s_webSocketServerService;
        var liveCaption  = s_liveCaptionService;
        var viewerSrv    = s_viewerServer;
        var viewerModel  = s_viewerReadModel;
        var layerWatcher = s_layerWatcher;
        var logicWatcher = s_logicWatcher;
        var optInCts     = s_optInCts;
        var obsWs        = s_obsWebSocketClient;

        s_hotkeyService           = null;
        s_clipboardService        = null;
        s_webSocketServerService  = null;
        // Clear the HubHost accessor in lockstep with the bootstrapper's
        // private field so a late StatusStrip read during shutdown doesn't
        // dereference a service we just told the kernel to tear down.
        HubHost.WebSocketServer   = null;
        s_liveCaptionService      = null;
        s_viewerServer            = null;
        s_viewerReadModel         = null;
        s_layerWatcher            = null;
        s_logicWatcher            = null;
        s_optInCts                = null;
        s_obsWebSocketClient      = null;

        // Stop every live process instance first so their per-instance schedule
        // timers cancel before the shared scheduler is drained.
        try { ProcessInstanceManager.Instance.StopAll(); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "ProcessInstanceManager.StopAll failed", ex); }

        // Drain the SchedulerService before LogicWatcher so a final
        // OnRefresh-driven Reload can't queue tasks against a dying CTS.
        try { SchedulerService.Instance.Stop(); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "SchedulerService.Stop failed", ex); }

        try { optInCts?.Cancel(); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "opt-in CTS cancel failed", ex); }

        // LiveCaptionService first — its Dispose blocks on the foreground
        // poll-task drain (1.5s cap), so kicking it first lets that wall-clock
        // cost overlap with the synchronous Stop calls below.
        if (liveCaption is not null)
        {
            try { liveCaption.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "LiveCaptionService shutdown failed", ex); }
        }

        if (hotkey is not null)
        {
            try { hotkey.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "HotkeyService shutdown failed", ex); }
        }

        if (clipboard is not null)
        {
            try { clipboard.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "ClipboardService shutdown failed", ex); }
        }

        if (webSocket is not null)
        {
            try { await webSocket.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "WebSocketServerService shutdown failed", ex); }
        }

        // DisposeAsync awaits DisconnectAsync internally (sends Normal
        // Closure to OBS + disposes the underlying WebsocketClient). The
        // client's own teardown shifts the blocking dispose to a thread-pool
        // task so this await stays UI-thread friendly.
        if (obsWs is not null)
        {
            try { await obsWs.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "ObsWebSocketClient shutdown failed", ex); }
        }

        if (viewerSrv is not null)
        {
            try { await viewerSrv.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "ViewerServer shutdown failed", ex); }
        }

        if (viewerModel is not null)
        {
            try { viewerModel.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "HubViewerReadModel shutdown failed", ex); }
        }

        if (layerWatcher is not null)
        {
            try { layerWatcher.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "LayerWatcher shutdown failed", ex); }
        }

        if (logicWatcher is not null)
        {
            try { logicWatcher.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "LogicWatcher shutdown failed", ex); }
        }

        if (optInCts is not null)
        {
            try { optInCts.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "opt-in CTS dispose failed", ex); }
        }

        // Dispose the DiscordService singleton so
        // its long-lived shared HttpClient releases its socket handles on Hub
        // shutdown. DiscordService is a core (always-on) service rather than an
        // opt-in one, but this method is the bootstrapper's single teardown
        // surface and runs from MainWindow's shutdown coordinator, so it's the
        // correct home for the disposal. DiscordService is a Lazy<> singleton
        // (DiscordService.Instance); the `as IDisposable` cast stays null-safe
        // whether or not DiscordService implements IDisposable. The Instance
        // getter forces construction — harmless: the HttpClient is cheap and we
        // immediately dispose it. Wrapped per the per-service teardown idiom so
        // a fault here can't strand the closing summary log.
        try { (DiscordService.Instance as IDisposable)?.Dispose(); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "DiscordService shutdown failed", ex); }

        GlobalLogger.Log("Opt-in services torn down (Hotkey/Clipboard/WebSocket/LiveCaption/ViewerServer/OBS) + DiscordService.",
            "HubBootstrapper", LogLevel.System);
    }

    // SplashProgress.Fraction is a 0.0–1.0 fill ratio. Map step N out of
    // TotalBootSteps to N / TotalBootSteps so the progress bar advances
    // monotonically; the splash clamps to [0, 1] anyway, so trailing
    // arithmetic from a future totals change can't push past full.
    private static void Report(IProgress<SplashProgress>? progress, int step, string status)
    {
        if (progress is null) return;
        double fraction = (double)step / TotalBootSteps;
        progress.Report(new SplashProgress(status, fraction));
    }

    // Poll Bus.IsListening with a 5 ms cadence and a hard
    // deadline. Replaces the previous unconditional Task.Delay(100). Warm
    // listener typically flips in 5-15 ms — old code paid 100 ms every boot.
    private static async Task WaitForBusListeningAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!Bus.Instance.IsListening && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
        // Surface a degraded boot when the bus never flips IsListening
        // within the budget. Pillars connecting to a not-yet-listening bus will
        // fall through to their exponential-backoff reconnect path; without
        // this log the user sees the connection-failure symptom in the status
        // strip but has no breadcrumb pointing at the bind step.
        if (!Bus.Instance.IsListening)
        {
            GlobalLogger.Log(
                $"Bus.Instance.IsListening did not flip true within {timeout.TotalMilliseconds:0} ms — pillars will reconnect via backoff.",
                "HubBootstrapper", LogLevel.Communication);
        }
    }

    // Mirrors LogicWatcher.ResolveProjectSourcePath: respects an absolute
    // LogicDirectory in AppConfig, otherwise walks up from the running exe
    // looking for a folder containing the configured relative path. Anchors
    // on the same shape as the existing Hub so the WinUI build picks up the
    // same data/logic/ tree without diverging.
    private static string ResolveLogicPath()
    {
        string rel = ConfigManager.Current.LogicDirectory;
        if (string.IsNullOrEmpty(rel)) rel = "data/logic";
        if (Path.IsPathRooted(rel))
        {
            Directory.CreateDirectory(rel);
            return rel;
        }

        // Walk up from AppContext.BaseDirectory looking for a sibling that
        // matches the relative path. Bound at 8 levels to cover both source
        // (bin/Debug/net8.0-windows/) and published layouts.
        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(probe); i++)
        {
            string candidate = Path.Combine(probe, rel);
            if (Directory.Exists(candidate)) return candidate;
            probe = Path.GetDirectoryName(probe);
        }

        // Fall back to {bin}/data/logic so the bootstrapper never throws —
        // the directory will be empty but ScriptRegistry.LoadScripts copes.
        string fallback = Path.Combine(AppContext.BaseDirectory, rel);
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
