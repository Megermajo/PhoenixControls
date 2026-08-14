using System.IO;
using System.Net;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.Core.Translation;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Paths = Phoenix.Controls.Shared.Core.Paths;
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
    // existing HUDServer lives forever via HubHost; these
    // four don't (yet) have a process-wide accessor, so the bootstrapper itself
    // owns them. Torn down at shutdown via ShutdownOptInServicesAsync so the
    // Hub.WinUI process can exit cleanly (pre-fix the LiveCaptionService poll
    // loop was a foreground Task that kept the CLR alive after window close).
    private static HotkeyService?           s_hotkeyService;
    private static ClipboardService?        s_clipboardService;
    private static WebSocketServerService?  s_webSocketServerService;
    private static LiveCaptionService?      s_liveCaptionService;
    // One-shot latch for the "captions are restricted to N layer(s)" notice.
    // Interlocked because CaptionChanged fires from the UIA poll loop, and an
    // int-with-Exchange is the cheapest correct once-only gate; a caption arrives
    // several times a second, so re-logging would bury the SystemLog.
    private static int s_captionScopeNoticeLogged;
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

    // TimerService (subathon countdown) is an always-on core service — a
    // subathon must keep counting even with no Timer window open — so it is not
    // AppConfig-gated. Pinned here for the same GC reason as the services above:
    // its 1 Hz tick loop is a background Task with no other strong root. Brought
    // up after DB init in BootAsync; ShutdownAsync (checkpoint + stop loop) runs
    // from ShutdownOptInServicesAsync.
    private static TimerService? s_timerService;

    // CountersService (named databank-backed counters) is an always-on-capable
    // core service — it self-gates on Config.Enabled (OFF by default), so bringing
    // it up unconditionally is safe (fully dormant until enabled). Pinned here for
    // the same GC reason as TimerService. Brought up after DB init in BootAsync.
    private static CountersService? s_countersService;

    // SchedulingService (recurring timed chat messages) is an always-on-capable
    // core service — it self-gates on Config.Enabled (OFF by default) AND on the
    // live-state, so bringing it up unconditionally is safe (fully dormant until
    // enabled). Pinned here for the same GC reason as TimerService: its 1 Hz tick
    // loop is a background Task with no other strong root. Brought up after DB init.
    private static SchedulingService? s_schedulingService;

    // UserManagementService (welcoming + groups) and AlertsService (event responses)
    // are always-on-capable, self-gated (OFF by default) and purely event-driven —
    // no tick loop. Pinned for symmetry with the sibling tool services.
    private static UserManagementService? s_userManagementService;
    private static AlertsService? s_alertsService;

    // SongRequestService (YouTube request queue) is an always-on-capable core service —
    // it self-gates on Config.Enabled (OFF by default). Pinned here for the same GC
    // reason as TimerService: its overlay heartbeat is a background Task whose only
    // strong root would otherwise be the singleton itself. Brought up after DB init.
    private static SongRequestService? s_songRequestService;

    // PollsService (chat poll + points betting) is an always-on-capable core service —
    // it self-gates on Config.Enabled (OFF by default). Pinned here for the same GC
    // reason as SongRequestService: its poll.* overlay heartbeat is a background Task
    // whose only strong root would otherwise be the singleton itself. Brought up after
    // DB init, and torn down BEFORE LoyaltyService, because its shutdown refunds an
    // open pot through the points economy.
    private static PollsService? s_pollsService;

    // RanksService (watch-time / points rank ladder) is an always-on-capable core service
    // — it self-gates on Config.Enabled (OFF by default). Pinned here for the same GC
    // reason as PollsService: its rank.* overlay heartbeat is a background Task whose only
    // strong root would otherwise be the singleton itself. Brought up after DB init, and
    // AFTER LoyaltyService, because its points metric and its currency noun read that
    // service's config — and because the watch-minute accrual it wires rides Loyalty's
    // earn tick.
    private static RanksService? s_ranksService;

    // SoundboardService (chat-triggered clip playback) is an always-on-capable core
    // service — it self-gates on Config.Enabled (OFF by default). Unlike its Polls /
    // Ranks / SongRequest neighbours it owns NO background Task and NO overlay heartbeat
    // (it publishes no live-channel key: a soundboard has no ambient state to report —
    // it is a momentary fire, and a key that could only ever say "the last clip played"
    // would be exactly the finished-poll-painted-forever failure). It is pinned here for
    // symmetry with the sibling tool services rather than for a GC reason.
    private static SoundboardService? s_soundboardService;

    // AutomodService (spam filter) is an always-on-capable core service — it
    // self-gates on Config.Enabled (OFF by default), so bringing it up
    // unconditionally is safe (fully dormant until enabled). No tick loop and no
    // overlay seam — moderation is quiet. Pinned here for the same GC reason as
    // TimerService. Brought up after DB init in BootAsync.
    private static AutomodService? s_automodService;

    // QuotesService (databank-backed quote store) is an always-on-capable core
    // service — it self-gates on Config.Enabled (OFF by default), so bringing it up
    // unconditionally is safe (fully dormant until enabled). Stateless over the DB,
    // no tick loop and no overlay seam. Pinned here for the same GC reason as
    // TimerService. Brought up after DB init in BootAsync.
    private static QuotesService? s_quotesService;

    // CustomCommandsService (text/variable-only custom chat commands) is an
    // always-on-capable core service — it self-gates on Config.Enabled (OFF by
    // default), so bringing it up unconditionally is safe (fully dormant until
    // enabled). Stateless (config only; no data table), no tick loop and no overlay
    // seam. Pinned here for the same GC reason as TimerService. Brought up after DB
    // init in BootAsync.
    private static CustomCommandsService? s_customCommandsService;

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

    // Hub-wide shutdown CTS. Distinct from s_optInCts above, which only governs
    // the opt-in services' own background loops (ViewerServer's accept loop, the
    // OBS connect retry): this one is the *script* shutdown signal.
    // ScriptManager.BeginExecutionTracked links every per-script execution CTS to
    // the token installed via ScriptManager.SetGlobalShutdownToken, so cancelling
    // this source propagates into every in-flight script run — including ones that
    // start after CancelAllScripts has already walked the active-CTS map, since
    // those link to an already-cancelled token and come up cancelled.
    // Created + installed at the top of BootAsync (before anything can start a
    // script); cancelled first and disposed last in ShutdownOptInServicesAsync.
    private static CancellationTokenSource?   s_shutdownCts;

    public static async Task<HubServices> BootAsync(
        IUiDispatcher dispatcher,
        IPillarNavigator navigator,
        IProgress<SplashProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(navigator);

        // ── Step 0 prep — install the Hub-wide script shutdown token ─────
        // ScriptManager has always linked every per-script execution CTS to a
        // static shutdown token, but nothing in production ever installed one, so
        // the token stayed CancellationToken.None (the documented "never fires"
        // sentinel) for the whole process lifetime and the link was inert. Create
        // the source and hand its token over before ANY boot step runs: the bus
        // starts accepting at step 4 and Bus.RouteIncomingMessage dispatches
        // straight into ScriptManager.ExecuteOnBusScriptsAsync, so a script can be
        // running well before the step-6 ScriptManager bring-up. Installing here
        // means no execution can ever be created against the inert None token.
        //
        // SetGlobalShutdownToken overwrites rather than accumulates, so a second
        // BootAsync in one process simply retires the previous token — in-flight
        // executions already linked to it keep the CTS they were built with.
        s_shutdownCts = new CancellationTokenSource();
        ScriptManager.SetGlobalShutdownToken(s_shutdownCts.Token);

        // ── Step 0 — update-backup self-heal ─────────────────────────────
        // If a previous update wiped user data (the pre-2026-07-14 Updater
        // swap replaced the whole install root), fill missing files back in
        // from the retained <installRoot>.bak.* sibling BEFORE anything reads
        // the data tree — the ConfigManager snapshot below (legacy in-tree
        // config.json migration) and LayerWatcher / ScriptManager enumeration
        // of data\layers\ + data\logic\. Fill-only, marker-guarded, never
        // throws; instant no-op when no backups exist.
        UpdateBackupSelfHeal.Run();

        // Wipe sentinel — runs AFTER self-heal so it only fires on files the
        // heal could not recover. Compares the live logic/layer tree to a
        // fingerprint kept in %AppData% (outside the install tree, survives the
        // swap): if authored files present last session have vanished, it logs a
        // loud warning instead of booting silently on shipped seeds. Covers the
        // no-backup wipe the heals cannot rescue. Never throws / blocks.
        WipeSentinel.Run();

        // Snapshot AppConfig once at boot. The ViewerServer (and other opt-in
        // services) read from this same source-of-truth; pulling the read into
        // a local keeps the gate sites obvious and matches ConfigManager.Current's
        // intent (it's a snapshot, not a live observer — Settings changes don't
        // retro-modify a running boot).
        // (Dev's copy of this comment still cited the legacy RemoteBridgeServer
        // gate at step 4; that subsystem was removed on this line, so the 1.1
        // wording is the accurate one.)
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

        // Overlay Live Channel — same dispatcher, same "wire it before anything
        // can publish" rule as LayerRuntime above. The store is the single
        // Hub→overlay data path: tools and scripts publish keys into it from any
        // thread and its 1 Hz pump coalesces the dirty set into one LIVE_PATCH per
        // layer. Assigning the dispatcher here (rather than at step 4 with
        // HUDServer.StartAsync) means the very first publish — TimerService's
        // startup tick, a LIVE_HELLO answered the instant OBS reconnects — already
        // has somewhere to land. StartPump is idempotent (loop-gated internally),
        // so it is safe next to the assignment even though no layer can have
        // subscribed yet: an empty dirty set makes every tick a no-op until the
        // first LIVE_HELLO arrives.
        OverlayLiveStore.Instance.Dispatcher = hudServer.SendToLayerAsync;
        // The addressed seam, used for exactly one frame: the LIVE_SNAPSHOT that answers
        // a LIVE_HELLO. A HELLO is per-socket and a snapshot means "repaint everything",
        // so fanning it at the layer made one browser's connect repaint every other
        // browser already on that layer — N sources ⇒ N² render passes per save.
        OverlayLiveStore.Instance.SocketDispatcher = hudServer.SendToSocketAsync;
        OverlayLiveStore.Instance.StartPump();

        // Auto-sync to OBS: when LayerWatcher (re)registers a .phxlayer, the
        // connected /hud/<id> browsers must refresh WITHOUT a manual OBS
        // "Refresh cache of current page". The /hud broadcast half is owned by
        // HUDServer itself (ctor subscribes LayerRegistry.LayerReloaded →
        // PushLayerReloadedAsync, Stop() unhooks) — subscribing it here as an
        // anonymous lambda pinned every disposed HUDServer on the singleton
        // registry for the rest of the session. Only the bus half stays here:
        // Bus.Instance is itself process-lifetime, so this subscription pins
        // nothing disposable. Wired BEFORE LayerWatcher.Start() below so the
        // startup scan's registrations are covered too.
        LayerRegistry.Instance.LayerReloaded += layerId =>
        {
            // Visualist's design-time pillar (VisualistBusClient → LayerCanvasView.
            // OnBusLayerReloaded) forces an off-cadence canvas-preview capture and
            // recovers a preview whose last navigation failed. Target="*" is
            // harmless for Architect (ignores it).
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

        // ── One-shot Pre-Build opt-in migration (1.1 test-build cleanup) ─
        // Every Pre-Build tool is opt-in by design: fresh installs persist no
        // config, and all master toggles default OFF. But the SQLite DB in
        // Roaming AppData survives in-place upgrades, so earlier 1.1 test
        // sessions can leave tool blobs Enabled=true and timers checkpointed
        // Running — which then reload as "active" after this patch installs.
        // Runs BEFORE any tool service loads its blob so each InitializeAsync
        // reads the corrected value; guarded by an AppConfig flag (mirrors the
        // SeenWelcomeDialog / ArchitectInspectorDockedMigrated one-shot idiom)
        // so ONLY this patch's first boot forces anything — later patches
        // never override the streamer's choices again.
        if (!ConfigManager.Current.PreBuildToolsForcedOffMigrated)
        {
            try
            {
                var (disabled, paused) = await PreBuildOptInMigration.RunAsync(DB.Instance).ConfigureAwait(false);

                // Flag saved only after a clean pass — a DB-level failure above
                // throws past this, so the migration retries on the next boot
                // instead of half-migrating silently. Accepted residual: if the
                // config.json WRITE itself fails silently, the flag stays false
                // on disk and the migration re-runs (re-forcing tools OFF) next
                // boot — annoying but fail-closed, which is the right direction
                // for an opt-in guarantee.
                ConfigManager.Current.PreBuildToolsForcedOffMigrated = true;
                await ConfigManager.SaveAsync(Paths.AppConfigJson).ConfigureAwait(false);

                if (disabled > 0 || paused > 0)
                {
                    GlobalLogger.Log(
                        $"Pre-Build opt-in migration: forced {disabled} tool(s) OFF, paused {paused} running timer(s). " +
                        "One-time cleanup — re-enable tools in their pages when wanted.",
                        "HubBootstrapper", LogLevel.System);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "Pre-Build opt-in migration failed (will retry next boot)", ex);
            }
        }

        // ── One-shot tip-defaults migration (donation ingestion) ─────────
        // Separate flag and separate class from the opt-in migration above: that
        // one flips a tool's top-level master Enabled key, these settings are
        // nested (Loyalty Earn.TipEnabled) and inside typed per-timer blobs.
        // Must run BEFORE LoyaltyService/TimerService initialize below so each
        // reads the corrected config. Same failure contract: the flag is set only
        // after a clean pass, so a DB fault retries next boot rather than leaving
        // tips armed.
        if (!ConfigManager.Current.TipDefaultsForcedOffMigrated)
        {
            try
            {
                var (loyaltyChanged, timersChanged) = await TipDefaultsMigration.RunAsync(DB.Instance).ConfigureAwait(false);

                ConfigManager.Current.TipDefaultsForcedOffMigrated = true;
                await ConfigManager.SaveAsync(Paths.AppConfigJson).ConfigureAwait(false);

                if (loyaltyChanged || timersChanged > 0)
                {
                    GlobalLogger.Log(
                        $"Tip-defaults migration: Loyalty tip earn {(loyaltyChanged ? "forced OFF" : "already off")}, " +
                        $"{timersChanged} timer(s) had their tip rate zeroed. Donations now pay out only once you set it up.",
                        "HubBootstrapper", LogLevel.System);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HubBootstrapper", "Tip-defaults migration failed (will retry next boot)", ex);
            }
        }

        // ── ViewerPresenceService (who is watching) — always-on data source ─
        // NOT a tool and NOT opt-in. There is no master toggle, no pre-build page
        // owns it, and nothing user-visible fires from it: it samples Streamer.bot's
        // chatter list on a cadence and turns that into the three things the rest of
        // the suite reads — one shared active-viewer snapshot (instead of every
        // consumer opening its own GetActiveViewers round-trip and getting a
        // different answer), the platform-role cache a role question about an
        // arbitrary login falls back to, and passive watch-time minutes.
        //
        // That last one is why it carries no config gate here: watch time has to
        // record with every pre-build tool switched off, which is exactly the install
        // where the old accrual — a passenger on Loyalty's earn tick, additionally
        // gated behind the Ranks tool being enabled — recorded nothing at all. The
        // streamer-facing switches (WatchTimeTrackingEnabled / WatchTimeOnlyWhenLive /
        // ViewerPresencePollSeconds) are read live inside the loop, so gating the
        // bring-up would only mean a Settings change needed a restart.
        //
        // There is no InitializeAsync to await — Start() spins the loop, and the loop
        // does its own CREATE-TABLE-IF-NOT-EXISTS and mirror load on the way in. It
        // sits here, after step 5 (the Streamer.bot connect the sweep rides) and step 6
        // (the ScriptManager ctor; the FetchProvider / BotAccountsProvider /
        // RoleFallbackProvider seams are wired on the ScriptManager side, because the
        // pillar rule keeps every Streamer.bot round-trip there and this service holds
        // no socket of its own). A still-null seam only costs a wasted sample — the
        // loop tolerates it, returning an empty list and accruing nothing — but there
        // is no reason to spend one.
        //
        // Ordered BEFORE RanksService below. The ladder no longer owns watch-time
        // accrual — this service does, and it announces each write through its
        // WatchTimeCredited event. Starting the publisher first is what lets Ranks
        // attach during its own InitializeAsync rather than race the first credit.
        try
        {
            ViewerPresenceService.Instance.Start();
            GlobalLogger.Log("ViewerPresenceService online — viewer presence + watch time recording (always-on data source, no toggle).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "ViewerPresenceService startup failed", ex);
        }

        // ── TimerService (subathon countdown) — always-on ────────────────
        // Load persisted timers and start the 1 Hz tick. No overlay seam to wire
        // any more: the tick publishes timer.<slug>.* / timer.__default.* straight
        // into the Overlay Live Channel (already dispatcher-wired at step 3), which
        // routes per subscribed key instead of blind-broadcasting a TIMER_UPDATE to
        // every browser. The RaiseScriptEvent / BusEmit seams are wired on the
        // ScriptManager side (ScriptManager.Timer.cs). StreamOnline/Offline routing to
        // SetStreamLive happens in ExecuteGenericEventAsync. No AppConfig gate —
        // a subathon must run even with no Timer window open.
        try
        {
            await TimerService.Instance.InitializeAsync().ConfigureAwait(false);
            s_timerService = TimerService.Instance;
            TimerService.Instance.StartTicking();
            GlobalLogger.Log("TimerService online — subathon countdown active (always-on).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "TimerService startup failed", ex);
            s_timerService = null;
        }

        // ── LoyaltyService (viewer points economy) — always-on-capable ───
        // Load persisted config + ensure the wallet tables, then start the watch-time
        // earn tick. No overlay seam to wire: every balance change publishes
        // loyalty.leaderboard / loyalty.currency into the Overlay Live Channel (still
        // gated on the tool's own Config.Overlay.Enabled switch). The service SELF-GATES
        // on Config.Enabled (OFF by default), so starting it unconditionally is safe — it
        // stays fully dormant (no earn, no commands, no games) until enabled. The
        // RaiseScriptEvent / BusEmit / ActiveViewers / BotAccounts / FireVisualTrigger
        // seams are wired on the ScriptManager side (ScriptManager.Loyalty.cs).
        try
        {
            await LoyaltyService.Instance.InitializeAsync().ConfigureAwait(false);
            LoyaltyService.Instance.StartEarning();
            GlobalLogger.Log("LoyaltyService online — points economy ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "LoyaltyService startup failed", ex);
        }

        // ── CountersService (named counters) — always-on-capable ─────────
        // Load persisted config + ensure the OPEN counts table. No overlay seam to
        // wire: every count change publishes counter.<name>.count into the Overlay
        // Live Channel. The service SELF-GATES on Config.Enabled (OFF by default), so
        // starting it unconditionally is safe — it stays fully dormant (no chat
        // commands, no overlay push) until enabled. The RaiseScriptEvent seam is
        // wired on the ScriptManager side (ScriptManager.Counters.cs).
        try
        {
            await CountersService.Instance.InitializeAsync().ConfigureAwait(false);
            s_countersService = CountersService.Instance;
            GlobalLogger.Log("CountersService online — named counters ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "CountersService startup failed", ex);
            s_countersService = null;
        }

        // ── SchedulingService (recurring timed chat messages) — always-on ─
        // Load persisted config, then start the 1 Hz tick loop. The service
        // SELF-GATES on Config.Enabled (OFF by default) AND the OnlyWhenLive /
        // live-state, so starting it unconditionally is safe — it posts nothing
        // until enabled. No overlay seam (chat-only). The ChatSend seam (token
        // resolution + SendTwitchChatCore) is wired on the ScriptManager side
        // (ScriptManager.Scheduling.cs).
        try
        {
            await SchedulingService.Instance.InitializeAsync().ConfigureAwait(false);
            s_schedulingService = SchedulingService.Instance;
            SchedulingService.Instance.StartTicking();
            GlobalLogger.Log("SchedulingService online — recurring messages ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "SchedulingService startup failed", ex);
            s_schedulingService = null;
        }

        // ── UserManagementService (welcoming + groups + queue) — always-on-capable ─
        // Load persisted config + the welcomed-this-stream set. Its ONLY tick is the
        // viewer queue's 5 s overlay heartbeat, started by InitializeAsync and
        // self-gated inside: an Overlay Live Channel key must be republished on a
        // declared cadence to report Stale honestly once nobody is maintaining it.
        // Everything else is event-driven (the observe-only greeting tap, the handling
        // queue provider, the role overlay). The service SELF-GATES on Config.Enabled
        // (OFF by default) so starting it unconditionally is safe — dormant means no
        // greeting, no queue commands, no overlay publish and a passthrough role
        // overlay. Seams wired on the ScriptManager side (ScriptManager.UserManagement.cs).
        try
        {
            await UserManagementService.Instance.InitializeAsync().ConfigureAwait(false);
            s_userManagementService = UserManagementService.Instance;
            GlobalLogger.Log("UserManagementService online — welcoming + groups + viewer queue ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "UserManagementService startup failed", ex);
            s_userManagementService = null;
        }

        // ── AlertsService (event responses) — always-on-capable ──────────
        // Load persisted config. NO tick loop (purely event-driven via the
        // ExecuteGenericEventAsync tap). SELF-GATES on Config.Enabled (OFF by
        // default). Seams wired on the ScriptManager side (ScriptManager.Alerts.cs).
        try
        {
            await AlertsService.Instance.InitializeAsync().ConfigureAwait(false);
            s_alertsService = AlertsService.Instance;
            GlobalLogger.Log("AlertsService online — event responses ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "AlertsService startup failed", ex);
            s_alertsService = null;
        }

        // ── SongRequestService (YouTube request queue) — always-on-capable ─
        // Load persisted config. InitializeAsync also starts the overlay heartbeat, which
        // is self-gated inside: an Overlay Live Channel key must be republished on its
        // declared cadence to report Stale honestly once nobody is maintaining it, and
        // starting the pump unconditionally is what lets enabling the tool mid-stream
        // start painting within one interval instead of after a restart. The request queue
        // itself is session state and is deliberately never restored. The service
        // SELF-GATES on Config.Enabled (OFF by default) so starting it unconditionally is
        // safe — dormant means no chat commands, no queue and no overlay publish. Seams
        // (script events, the YouTube resolve, the Loyalty charge/refund) are wired on the
        // ScriptManager side (ScriptManager.SongRequest.cs).
        try
        {
            await SongRequestService.Instance.InitializeAsync().ConfigureAwait(false);
            s_songRequestService = SongRequestService.Instance;
            GlobalLogger.Log("SongRequestService online — request queue ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "SongRequestService startup failed", ex);
            s_songRequestService = null;
        }

        // ── PollsService (chat poll + points betting) — always-on-capable ──
        // Load persisted config. InitializeAsync also starts the overlay heartbeat, which
        // is self-gated inside: a poll.* key must be republished on its declared cadence to
        // report Stale honestly, and the pump is also what RETRACTS the keys once a closed
        // poll's result has finished lingering — so it has to run even while the tool is
        // dormant. The live poll itself is session state and is deliberately never
        // restored (a wall-clock countdown cannot resume across a restart). The service
        // SELF-GATES on Config.Enabled (OFF by default). Seams (script events, the Loyalty
        // charge / refund / payout trio, the announcement, the native mirror) are wired on
        // the ScriptManager side (ScriptManager.Polls.cs), which ran back at step 6 — so an
        // open pot is already refundable by the time this returns.
        //
        // Ordered AFTER LoyaltyService above: the stakes ride that economy, and its config
        // must be loaded before this tool can charge or refund anything.
        try
        {
            await PollsService.Instance.InitializeAsync().ConfigureAwait(false);
            s_pollsService = PollsService.Instance;
            GlobalLogger.Log("PollsService online — chat polls ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "PollsService startup failed", ex);
            s_pollsService = null;
        }

        // ── RanksService (watch-time / points rank ladder) — always-on-capable ──
        // Load persisted config and CREATE the two OPEN data tables ("WatchTime" and
        // "Ranks"), which is why this has to run even while the tool is dormant: a streamer
        // who enables the ladder mid-stream must not have to restart the Hub before minutes
        // can be recorded. InitializeAsync also starts the overlay heartbeat, self-gated
        // inside for the same reason as Polls' — a rank.* key must be republished on its
        // declared cadence to report Stale honestly, and the pump is what RETRACTS the keys
        // when the tool is switched off. The service SELF-GATES on Config.Enabled (OFF by
        // default). Its five seams (script event, announcement, currency noun, balance
        // table, and the watch-minute accrual pair wired onto LOYALTY) are set on the
        // ScriptManager side (ScriptManager.Ranks.cs), which ran back at step 6.
        //
        // Ordered AFTER LoyaltyService above: the points metric reads that service's
        // configured balance table and currency noun, and the accrual rides its earn tick.
        try
        {
            await RanksService.Instance.InitializeAsync().ConfigureAwait(false);
            s_ranksService = RanksService.Instance;
            GlobalLogger.Log("RanksService online — rank ladder ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "RanksService startup failed", ex);
            s_ranksService = null;
        }

        // ── SoundboardService (chat-triggered clip playback) — always-on-capable ──
        // Load the persisted config and nothing else: there is no data table to create
        // and no heartbeat to start, because the tool's "data" is the clip FILES in
        // data/media. The service SELF-GATES on Config.Enabled (OFF by default), so
        // starting it unconditionally is safe — it stays fully dormant (no chat parsing,
        // no overlay fires) until enabled. Its single seam (the shared visual fan-out) is
        // wired on the ScriptManager side (ScriptManager.Soundboard.cs), which ran back at
        // step 6.
        //
        // Ordering note: this runs AFTER PreBuildOptInMigration like every sibling, which
        // is what lets InitializeAsync read the corrected Enabled value rather than a
        // stale true carried across an in-place upgrade.
        try
        {
            await SoundboardService.Instance.InitializeAsync().ConfigureAwait(false);
            s_soundboardService = SoundboardService.Instance;
            GlobalLogger.Log("SoundboardService online — clip playback ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "SoundboardService startup failed", ex);
            s_soundboardService = null;
        }

        // ── AutomodService (spam filter) — always-on-capable ─────────────
        // Load persisted config, ensure the OPEN strikes table + compile the
        // blocklist regexes. NO tick loop and NO overlay seam (moderation is quiet).
        // The service SELF-GATES on Config.Enabled (OFF by default), so starting it
        // unconditionally is safe — it stays fully dormant (no scanning, no chat
        // commands) until enabled. The RaiseScriptEvent / BotAccountsProvider seams
        // are wired on the ScriptManager side (ScriptManager.Automod.cs).
        try
        {
            await AutomodService.Instance.InitializeAsync().ConfigureAwait(false);
            s_automodService = AutomodService.Instance;
            GlobalLogger.Log("AutomodService online — spam filter ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "AutomodService startup failed", ex);
            s_automodService = null;
        }

        // ── QuotesService (quote store) — always-on-capable ──────────────
        // Load persisted config + ensure the OPEN "Quotes" table. Stateless over the
        // DB — no tick loop, no overlay seam. The service SELF-GATES on Config.Enabled
        // (OFF by default), so starting it unconditionally is safe — it stays fully
        // dormant (no chat commands, no Quote.OnAdded event) until enabled. The
        // RaiseScriptEvent seam is wired on the ScriptManager side (ScriptManager.Quotes.cs).
        try
        {
            await QuotesService.Instance.InitializeAsync().ConfigureAwait(false);
            s_quotesService = QuotesService.Instance;
            GlobalLogger.Log("QuotesService online — quote store ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "QuotesService startup failed", ex);
            s_quotesService = null;
        }

        // ── CustomCommandsService (custom chat commands) — always-on-capable ──
        // Load persisted config. Stateless — no data table (the {count}/{count.next}
        // tokens consume the Counters tool), no tick loop, no overlay seam. The service
        // SELF-GATES on Config.Enabled (OFF by default), so starting it unconditionally
        // is safe — it stays fully dormant (no chat commands, no Command.OnCustom event)
        // until enabled. The RaiseScriptEvent / counter / channel seams are wired on the
        // ScriptManager side (ScriptManager.CustomCommands.cs).
        try
        {
            await CustomCommandsService.Instance.InitializeAsync().ConfigureAwait(false);
            s_customCommandsService = CustomCommandsService.Instance;
            GlobalLogger.Log("CustomCommandsService online — custom chat commands ready (always-on, dormant until enabled).",
                "HubBootstrapper", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubBootstrapper", "CustomCommandsService startup failed", ex);
            s_customCommandsService = null;
        }

        GlobalLogger.Log("Hub services online. Bus / HUD / Streamer.bot / Scripts / Scheduler / Timer / Loyalty / Counters / Automod / Quotes / CustomCommands wired.",
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

                    // Capture pipeline: capture → translate (passthrough by
                    // default) → gate on AppConfig.LiveCaptionsBroadcastToOverlays
                    // → publish into the Overlay Live Channel. Pre-fix captions
                    // were captured into the service's private buffer with no
                    // production subscriber at all; then the wired CAPTION_UPDATE
                    // envelope carried text/source/lang/ts while the browser read
                    // original/translated/sourceLanguage/targetLanguage — ZERO key
                    // overlap, so every caption frame delivered empty strings. The
                    // channel keys below ARE the reader's contract, which is what
                    // finally closes that gap.
                    s_liveCaptionService.CaptionChanged += async (text) =>
                    {
                        try
                        {
                            string translated = text;
                            // TranslateAsync is safe to call even with the
                            // passthrough translator (returns input unchanged);
                            // explicit try block so a translator fault doesn't
                            // poison the publish — overlays still get the
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

                            // LiveCaptionsAllowedLayers keeps its meaning: a
                            // non-empty list restricts captions to those layer ids
                            // and nothing else. Per-key routing does NOT subsume
                            // that — a shared/restream layer that binds a caption
                            // socket would subscribe caption.* and start painting
                            // speech exactly where the streamer forbade it. So the
                            // list is threaded to the store as a publish SCOPE, which
                            // gates the dirty set, the periodic resync sweep and the
                            // LIVE_SNAPSHOT alike. Empty list => null => every
                            // subscribing layer, the historical "legacy fan-out"
                            // meaning, byte for byte.
                            var allow = cfg.LiveCaptionsAllowedLayers ?? new System.Collections.Generic.List<string>();
                            System.Collections.Generic.IReadOnlyCollection<string>? captionScope =
                                allow.Count > 0 ? allow : null;

                            // One-shot notice so a blank caption overlay is
                            // self-diagnosing: the allowlist is the first thing to
                            // suspect, and it is invisible from the graph side.
                            if (captionScope is not null
                                && System.Threading.Interlocked.Exchange(ref s_captionScopeNoticeLogged, 1) == 0)
                            {
                                GlobalLogger.Log(
                                    $"LiveCaption: captions are restricted to the {allow.Count} layer(s) in "
                                    + $"LiveCaptionsAllowedLayers ({string.Join(", ", allow)}). Any other layer whose widgets read "
                                    + "caption.* will stay blank by design — clear the setting to allow every layer.",
                                    "HubBootstrapper", LogLevel.System);
                            }

                            // Only the two keys that HAVE a reader. compositor.js's
                            // CAPTION_SOCKETS is exactly { Original, Translated };
                            // captionState.sourceLanguage / targetLanguage are
                            // written and never read, so publishing
                            // caption.source_language / caption.target_language
                            // would ship dead keys. No expected interval: speech is
                            // event-driven, and a caption that hasn't changed for a
                            // minute is still the current caption, not a stale one.
                            var live = OverlayLiveStore.Instance;
                            live.PublishString("caption.original", text, "tool:LiveCaption",
                                allowedLayers: captionScope);
                            // Mirrors the browser's own `translated || original`
                            // fallback: a translator that returns empty must not
                            // blank the caption.
                            live.PublishString("caption.translated",
                                string.IsNullOrEmpty(translated) ? text : translated, "tool:LiveCaption",
                                allowedLayers: captionScope);
                        }
                        catch (Exception ex)
                        {
                            GlobalLogger.Error(
                                "LiveCaption",
                                "caption publish pipeline threw on CaptionChanged",
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
        var shutdownCts  = s_shutdownCts;
        var obsWs        = s_obsWebSocketClient;
        var timerSvc     = s_timerService;
        var schedulingSvc = s_schedulingService;
        var userMgmtSvc  = s_userManagementService;
        var songSvc      = s_songRequestService;
        var pollsSvc     = s_pollsService;
        var ranksSvc     = s_ranksService;

        // CountersService has no tick loop / open sockets to drain (stateless over
        // the DB), so there's nothing to await here — just release the GC-pin so a
        // re-entrant shutdown sees an empty slate. Read-then-null so the field is
        // observed (not a write-only pin).
        if (s_countersService != null) s_countersService = null;
        // AutomodService is likewise stateless over the DB (no tick / sockets to
        // drain) — just release the GC-pin. Read-then-null so the field is observed.
        if (s_automodService != null) s_automodService = null;
        // QuotesService is likewise stateless over the DB (no tick / sockets to drain)
        // — just release the GC-pin. Read-then-null so the field is observed.
        if (s_quotesService != null) s_quotesService = null;
        // CustomCommandsService is likewise stateless (config only; no tick / sockets /
        // data table) — just release the GC-pin. Read-then-null so the field is observed.
        if (s_customCommandsService != null) s_customCommandsService = null;
        // SoundboardService is likewise stateless (config only; no tick / heartbeat /
        // sockets / data table) — just release the GC-pin. It is NOT in the awaited
        // teardown block below because it has nothing to drain: its only outbound action
        // is a fire of the shared visual fan-out, which the LayerRuntime owns and tears
        // down on its own schedule. Read-then-null so the field is observed.
        if (s_soundboardService != null) s_soundboardService = null;
        s_timerService            = null;
        s_schedulingService       = null;
        s_userManagementService   = null;
        s_songRequestService      = null;
        s_pollsService            = null;
        s_ranksService            = null;
        s_alertsService           = null;
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
        s_shutdownCts             = null;
        s_obsWebSocketClient      = null;

        // ── Hub-wide script cancel — ahead of every teardown below ───────
        // Fire the token that ScriptManager linked every per-script execution CTS
        // to (installed in BootAsync). This runs before ProcessInstanceManager /
        // SchedulerService / OverlayLiveStore and the per-service drains that
        // follow, and that ordering is the point: an in-flight script gets its
        // cancel while the services its commands call are still alive, so it
        // unwinds on an OperationCanceledException instead of faulting against a
        // half-torn-down dependency.
        //
        // Defence in depth, NOT a replacement for ScriptManager's own teardown:
        // MainWindow's shutdown coordinator runs ScriptManager.DisposeAsync
        // (→ StopAsync → CancelAllScripts) as a sibling step in the same parallel
        // batch and that path is untouched. What the linked token adds is coverage
        // for executions that begin after CancelAllScripts has already walked the
        // active-CTS map — those link to an already-cancelled token in
        // BeginExecutionTracked and so are born cancelled, where the map walk
        // could only ever cancel what it could see at that instant.
        try { shutdownCts?.Cancel(); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "Hub shutdown CTS cancel failed", ex); }

        // Stop every live process instance first so their per-instance schedule
        // timers cancel before the shared scheduler is drained.
        try { ProcessInstanceManager.Instance.StopAll(); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "ProcessInstanceManager.StopAll failed", ex); }

        // Drain the SchedulerService before LogicWatcher so a final
        // OnRefresh-driven Reload can't queue tasks against a dying CTS.
        try { SchedulerService.Instance.Stop(); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "SchedulerService.Stop failed", ex); }

        // Overlay Live Channel: drain the 1 Hz patch pump BEFORE the publishers
        // below (Timer / Scheduling / Loyalty). Two reasons for that ordering:
        //
        //   • The store is the downstream end of the publish chain. Publishing
        //     always writes the store, so a publisher's final shutdown tick is
        //     harmless once the pump is gone — the value just sits there and the
        //     process exits. Draining the store LAST would invert that: each
        //     publisher teardown would re-dirty a still-running pump, which would
        //     then build and fan out a patch frame nobody can receive.
        //   • MainWindow disposes the HUDServer synchronously in its Step 2, before
        //     the coordinator task that calls this method even starts. By the time
        //     we get here the store's dispatcher already points at a closed
        //     listener with no open sockets, so every additional tick is pure waste.
        //
        // ShutdownAsync cancels the loop CTS and awaits the drain, so it cannot
        // outlive this call and race the process exit.
        try { await OverlayLiveStore.Instance.ShutdownAsync().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "OverlayLiveStore.ShutdownAsync failed", ex); }

        // TimerService: stop the 1 Hz tick loop and checkpoint every timer to the
        // DB so a subathon resumes from its last remaining value on next boot.
        if (timerSvc is not null)
        {
            try { await timerSvc.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "TimerService.ShutdownAsync failed", ex); }
        }

        // SchedulingService: cancel the 1 Hz tick loop and drain it. Config is already
        // persisted on every edit (UpdateConfigAsync), so there is nothing to flush.
        if (schedulingSvc is not null)
        {
            try { await schedulingSvc.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "SchedulingService.ShutdownAsync failed", ex); }
        }

        // UserManagementService: cancel the viewer queue's overlay heartbeat and drain
        // it. Config is persisted on every edit and the queue lives in the databank, so
        // there is nothing to flush — this only stops the pump from re-dirtying the
        // Overlay Live Channel that was drained a few lines above.
        if (userMgmtSvc is not null)
        {
            try { await userMgmtSvc.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "UserManagementService.ShutdownAsync failed", ex); }
        }

        // SongRequestService: REFUND every still-waiting request's PointsPaid, then cancel
        // the songrequest.* overlay heartbeat and drain it. The queue is session state that
        // is never restored, but that is exactly why it is NOT free to drop: a price is
        // debited the moment a request lands, so ShutdownAsync gives every queued viewer's
        // charge back before the pump stops (even with the tool toggled off — switching it
        // off does not empty the queue). Config is persisted on every edit, so the refund
        // is the only flush.
        //
        // ★ Ordered BEFORE LoyaltyService below, and that is load-bearing rather than
        // tidy — same contract as PollsService: the refunds run through the points economy
        // (the RefundPoints delegate), so tearing Loyalty down first would leave the
        // debited charges with nothing able to give them back.
        if (songSvc is not null)
        {
            try { await songSvc.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "SongRequestService.ShutdownAsync failed", ex); }
        }

        // PollsService: CANCEL any open poll — refunding every stake — then cancel the
        // poll.* overlay heartbeat and drain it.
        //
        // ★ Ordered BEFORE LoyaltyService below, and that is load-bearing rather than
        // tidy: the refund runs through the points economy, so tearing Loyalty down first
        // would leave debited stakes with nothing able to give them back. The poll itself
        // is session state that is never restored, so a cancel is the only honest ending —
        // closing would settle a vote that never finished.
        if (pollsSvc is not null)
        {
            try { await pollsSvc.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "PollsService.ShutdownAsync failed", ex); }
        }

        // ViewerPresenceService: cancel the sampling loop, drain it, and flush the
        // whole minutes the carried sub-minute remainders had already earned. The
        // flush is the reason this is an awaited step rather than a fire-and-forget
        // stop: the open "WatchTime" table stores whole MINUTES, so a viewer present
        // for 90 s across two samples is holding 30 s of real, watched time in memory
        // when the Hub closes. Dropping it on every shutdown would quietly shave time
        // off exactly the viewers who stay to the end of a stream.
        //
        // ★ Ordered BEFORE RanksService below, and that is load-bearing rather than
        // tidy: the final flush raises WatchTimeCredited, and the ladder evaluates a
        // promotion off that event. Tearing Ranks down first would drop the last
        // credit's evaluation on the floor — the minutes would be in the table with
        // nobody to notice the rank they just earned.
        try { await ViewerPresenceService.Instance.ShutdownAsync().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "ViewerPresenceService.ShutdownAsync failed", ex); }

        // RanksService: cancel the rank.* overlay heartbeat and drain it. Config is
        // persisted on every edit and both data tables live in the databank, so there is
        // nothing to flush — this only stops the pump from re-dirtying the Overlay Live
        // Channel that was drained a few lines above.
        //
        // Ordered BEFORE LoyaltyService below because the ladder's points metric reads that
        // service's config: a heartbeat that outlived it would publish a board resolved
        // against a torn-down economy.
        if (ranksSvc is not null)
        {
            try { await ranksSvc.ShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "RanksService.ShutdownAsync failed", ex); }
        }

        // CountersService: cancel the 15 s databank sweep (the db.*-write watcher added
        // with the 2026-08 tool-node cut) and drain it. Counts live in the open table and
        // config is persisted on every edit — nothing to flush, same story as Ranks.
        try { await CountersService.Instance.ShutdownAsync().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "CountersService.ShutdownAsync failed", ex); }

        // LoyaltyService: cancel the earn tick, resolve any open raffle (so entrants
        // who paid a fee are paid out), and flush the config to the databank. Static
        // singleton like TimerService.Instance — no captured static field needed.
        try { await LoyaltyService.Instance.ShutdownAsync().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "LoyaltyService.ShutdownAsync failed", ex); }

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

        // Hub-wide shutdown CTS goes LAST — every teardown above has had its chance
        // to observe the cancel by now. Cancel-then-dispose is the safe order for a
        // token other code still holds: BeginExecutionTracked's
        // CreateLinkedTokenSource sees an already-cancelled token and completes the
        // registration inline, so a script that starts during this drain still gets
        // a cancelled CTS rather than touching the disposed source. A second
        // shutdown pass finds s_shutdownCts already nulled at the top of this
        // method, so `shutdownCts` is null here and neither the cancel nor this
        // dispose can run twice on the same instance.
        if (shutdownCts is not null)
        {
            try { shutdownCts.Dispose(); }
            catch (Exception ex) { GlobalLogger.Error("HubBootstrapper", "Hub shutdown CTS dispose failed", ex); }
        }

        GlobalLogger.Log("Opt-in services torn down (Hotkey/Clipboard/WebSocket/LiveCaption/ViewerServer/OBS) + OverlayLiveStore + Timer + DiscordService.",
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
