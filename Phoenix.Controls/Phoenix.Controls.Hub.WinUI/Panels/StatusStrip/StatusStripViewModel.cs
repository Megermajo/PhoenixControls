using System;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
// Visibility lives on the XAML namespace; alias-importing it here
// keeps the property accessors readable instead of saying
// Microsoft.UI.Xaml.Visibility.Visible everywhere.
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace Phoenix.Controls.Hub.WinUI.Panels.StatusStrip;

public sealed class StatusStripViewModel : ObservableObject, System.IDisposable
{
    private readonly IConnectionStatus _status;
    // Per-VM dispatcher pump, ctor-injected by PanelFactory.
    private readonly UiDispatcherPump _ui;

    private ConnectionState _streamerBot;
    private ConnectionState _hudOverlay;
    private ConnectionState _ipcBus;
    private bool _disposed;

    // Clock + version stamp restored per Design_Orders
    // §4.8. The strip used to be a left-zone-only widget; the spec asks for
    // left status zone + center contextual + right meta (clock + version).
    // Center contextual is fed by the script-engine lifecycle producer
    // below ("Script Engine ready" / "▶ <name>" / transient error flash) —
    // see OnScriptLifecycle / UpdateCenterContextual.
    private string _clock = string.Empty;
    private string _centerContextual = string.Empty;

    // Center-contextual script-engine producer state. The running map is
    // refcounted (name → in-flight run count) because the Overlap policy
    // lets N runs of the same script execute concurrently — a plain set
    // would collapse them to one entry and the first Finished/Error would
    // clear the readout while sibling runs are still going. Mutated
    // exclusively on the dispatcher (every lifecycle event is Post'ed
    // before it touches VM state) so no lock is needed; the error flash
    // timer holds the "⚠ <name>" readout for a few seconds before the
    // computed state resumes.
    private readonly ScriptManager? _scriptManager;
    private readonly Action<ScriptManager.ScriptLifecycleEvent>? _onScriptLifecycle;
    private readonly Dictionary<string, int> _runningScripts = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _errorFlashTimer;
    private bool _errorFlashActive;
    private DispatcherTimer? _clockTimer;
    // 1 s dispatcher tick polls
    // LayerRegistry.ActiveLayerCount + HUDServer.CurrentBroadcastFps +
    // the WS server's port / enabled flag (LIVE WS state requires
    // service-side access we don't have in scope; we surface
    // AppConfig.WebSocketServerEnabled + .WebSocketServerPort instead).
    // Higher resolution than the existing clock timer (15 s) because the
    // FPS / layer readouts are user-visible activity indicators.
    private DispatcherTimer? _statsTimer;
    private string _layersText = string.Empty;
    private string _fpsText    = string.Empty;
    private string _wsText     = string.Empty;
    // Visibility/opacity backing for the WS badge. Hidden entirely
    // when the master toggle is off; dimmed when listening but no clients
    // are connected; fully opaque on any non-zero client count.
    private Visibility _wsBadgeVisibility = Visibility.Collapsed;
    private double     _wsBadgeOpacity    = 1.0;

    public StatusStripViewModel(IConnectionStatus status, DispatcherQueue? dispatcher)
    {
        _status = status;
        _ui = new UiDispatcherPump(dispatcher);
        _status.StateChanged += OnStateChanged;
        Pull();

        // 1-minute resolution is enough for the §4.8 HH:mm format.
        // Aligned to dispatcher so binding updates land on the UI thread
        // without an explicit Post.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clockTimer.Tick += (_, _) => RefreshClock();
        _clockTimer.Start();
        RefreshClock();

        // Center / WS readouts. Polls every
        // second through DispatcherTimer so updates land on the UI thread
        // without an explicit Post wrapper. RefreshStats also runs once
        // up-front so the strip renders meaningful initial values before
        // the first tick.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();
        RefreshStats();

        // Center-contextual producer per Design_Orders §4.8 — the slot
        // mirrors the script engine's live state ("Script Engine ready"
        // when idle, "▶ <name>" for a single run, "▶ N scripts running"
        // beyond that, and a transient "⚠ <name>" flash on error).
        // Lifecycle events arrive on the engine/bus thread; each is Post'ed
        // to the dispatcher before touching VM state so the x:Bind-fed
        // setter never fires off-thread. Design-time / fake hosting has no
        // engine singleton to observe — resolution is guarded and the slot
        // simply stays empty there.
        ScriptManager? manager = null;
        try { manager = ScriptManager.Instance; }
        catch { /* design-time host — no engine available */ }
        _scriptManager = manager;
        if (_scriptManager is not null)
        {
            _onScriptLifecycle = evt => _ui.Post(() => OnScriptLifecycle(evt));
            _scriptManager.ScriptLifecycle += _onScriptLifecycle;
            UpdateCenterContextual();
        }
    }

    /// <summary>
    /// Readouts — read LayerRegistry / HUDServer / AppConfig and
    /// re-raise the bound text properties when their formatted strings
    /// change. Single allocation per refresh on identity miss, zero on
    /// hit so the 1 Hz tick is cheap.
    /// </summary>
    private void RefreshStats()
    {
        if (_disposed) return;

        // Active layers — LayerRegistry singleton is alive for the Hub
        // lifetime, so the singleton access is safe at every tick.
        int activeLayers = 0;
        try { activeLayers = LayerRegistry.Instance.ActiveLayerCount; } catch { }
        string layersText = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.T("panel.statusstrip.layers_format", "Layers: {0}"),
            activeLayers);
        if (!string.Equals(_layersText, layersText, StringComparison.Ordinal))
        {
            _layersText = layersText;
            Raise(nameof(LayersText));
        }

        // FPS — HUDServer is null until HubBootstrapper Step 3 assigns
        // HubHost.HUD. Surface "—" until that happens.
        double fps = 0.0;
        bool hudReady = false;
        try
        {
            if (HubHost.HUD is { } hud)
            {
                hudReady = true;
                fps = hud.CurrentBroadcastFps;
            }
        }
        catch { hudReady = false; }
        string fpsText = hudReady
            ? string.Format(CultureInfo.CurrentCulture,
                Localizer.T("panel.statusstrip.fps_format", "FPS: {0:0.0}"),
                fps)
            : Localizer.T("panel.statusstrip.fps_idle", "FPS: —");
        if (!string.Equals(_fpsText, fpsText, StringComparison.Ordinal))
        {
            _fpsText = fpsText;
            Raise(nameof(FpsText));
        }

        // WS server
        // status badge. Now pulled live through HubHost.WebSocketServer, with
        // a fallback to the AppConfig enabled/port pair while the service is
        // still booting (HubHost.WebSocketServer is null until
        // HubBootstrapper's opt-in phase wires it). Four rendered states:
        //
        //   • disabled        → "WS: off"           (master toggle off)
        //   • enabled, !ready → "WS: starting…"     (bootstrapper not done)
        //   • enabled, ready  → "WS: N / :PORT"     (listening + N clients)
        //   • enabled, bound? → "WS: starting…"     (service alive but
        //                                            HttpListener not bound yet)
        //
        // The ConfigManager.Current path is still relevant for the "off"
        // signal because the bootstrapper short-circuits service construction
        // when the toggle is false — HubHost.WebSocketServer stays null in
        // that case, and using only HubHost would conflate "disabled" with
        // "starting" forever.
        var cfg = ConfigManager.Current;
        bool wsEnabled = cfg?.WebSocketServerEnabled ?? false;
        int  wsPort    = cfg?.WebSocketServerPort    ?? 18083;
        string wsText;
        // Three rendered states drive the badge's Visibility/Opacity:
        // hide entirely when disabled, dim when 0 clients; we additionally
        // distinguish "starting…" from
        // "listening with 0 clients" so the user can tell whether the
        // bootstrapper is still working or the listener is just idle.
        Visibility nextVisibility;
        double     nextOpacity;
        if (!wsEnabled)
        {
            // Disabled — text still updates (cheap) but the TextBlock is
            // collapsed, eliminating the visual footprint entirely.
            wsText           = Localizer.T("panel.statusstrip.ws_off", "WS: off");
            nextVisibility   = Visibility.Collapsed;
            nextOpacity      = 1.0;
        }
        else
        {
            var service = HubHost.WebSocketServer;
            if (service is null || !service.IsListening)
            {
                wsText = string.Format(CultureInfo.InvariantCulture,
                    Localizer.T("panel.statusstrip.ws_starting_format", "WSS :{0} · starting"),
                    wsPort);
                nextVisibility = Visibility.Visible;
                nextOpacity    = 0.55; // dim while booting
            }
            else
            {
                int clients = service.ConnectedClientCount;
                wsText = string.Format(CultureInfo.InvariantCulture,
                    Localizer.T("panel.statusstrip.ws_clients_v2_format", "WSS :{0} · {1} clients"),
                    wsPort, clients);
                nextVisibility = Visibility.Visible;
                // When enabled but 0 clients, dim it. Full opacity
                // once a real client is connected so live activity stands out.
                nextOpacity    = clients > 0 ? 1.0 : 0.55;
            }
        }
        if (!string.Equals(_wsText, wsText, StringComparison.Ordinal))
        {
            _wsText = wsText;
            Raise(nameof(WsText));
        }
        if (_wsBadgeVisibility != nextVisibility)
        {
            _wsBadgeVisibility = nextVisibility;
            Raise(nameof(WsBadgeVisibility));
        }
        if (_wsBadgeOpacity != nextOpacity)
        {
            _wsBadgeOpacity = nextOpacity;
            Raise(nameof(WsBadgeOpacity));
        }
    }

    // Lifecycle → running-map bookkeeping. Runs on the dispatcher (see the
    // ctor wiring). Running increments the name's refcount; Finished/Error
    // decrement and remove the entry at zero, so N overlapping runs of the
    // same script keep the readout alive until the LAST one ends. Queued
    // and Trigger are decorative for this readout: Queued precedes Running
    // without occupying an execution slot, and Trigger is a node-level ping
    // inside an already-Running script. Discarded is likewise a no-op — it
    // replaces Queued/Running for a trigger dropped by the Discard policy,
    // so the same-name execution that IS in flight keeps its map entry
    // until its own Finished/Error.
    private void OnScriptLifecycle(ScriptManager.ScriptLifecycleEvent evt)
    {
        if (_disposed) return;
        switch (evt.Phase)
        {
            case ScriptManager.ScriptExecutionPhase.Running:
                _runningScripts[evt.ScriptName] =
                    _runningScripts.TryGetValue(evt.ScriptName, out int count) ? count + 1 : 1;
                break;
            case ScriptManager.ScriptExecutionPhase.Finished:
                DecrementRunning(evt.ScriptName);
                break;
            case ScriptManager.ScriptExecutionPhase.Error:
                DecrementRunning(evt.ScriptName);
                BeginErrorFlash(evt.ScriptName);
                break;
            default:
                return;
        }
        // While the error flash holds the slot, the computed state waits —
        // the flash's Tick recomputes once it expires.
        if (!_errorFlashActive) UpdateCenterContextual();
    }

    // Refcount decrement with removal at zero. Guarded lookup — an Error
    // can arrive for a script this VM never saw as Running (e.g. a failure
    // before the Running phase, or a VM constructed mid-execution); a bare
    // decrement would mint a negative count and corrupt the readout, so
    // unknown names are simply ignored.
    private void DecrementRunning(string scriptName)
    {
        if (!_runningScripts.TryGetValue(scriptName, out int count)) return;
        if (count <= 1) _runningScripts.Remove(scriptName);
        else _runningScripts[scriptName] = count - 1;
    }

    // Hold "⚠ <name>" in the center slot briefly, then resume the computed
    // state. A second error restarts the shared timer and shows the most
    // recent failure — the System Log stays the detail surface; this
    // readout is just the attention hook.
    private void BeginErrorFlash(string scriptName)
    {
        _errorFlashActive = true;
        CenterContextual = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.T("panel.statusstrip.center_script_error_format", "⚠ {0}"),
            DisplayName(scriptName));
        if (_errorFlashTimer is null)
        {
            _errorFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _errorFlashTimer.Tick += (_, _) =>
            {
                _errorFlashTimer?.Stop();
                _errorFlashActive = false;
                if (!_disposed) UpdateCenterContextual();
            };
        }
        // Stop-then-start restarts the interval so back-to-back errors get
        // a full window each.
        _errorFlashTimer.Stop();
        _errorFlashTimer.Start();
    }

    private void UpdateCenterContextual()
    {
        string text;
        if (_runningScripts.Count == 0)
        {
            text = Localizer.T("panel.statusstrip.center_engine_ready", "Script Engine ready");
        }
        else if (_runningScripts.Count == 1)
        {
            // One distinct script — show its name even when several runs of
            // it overlap; the count would add noise without information.
            string single = string.Empty;
            foreach (var name in _runningScripts.Keys) { single = name; break; }
            text = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.T("panel.statusstrip.center_script_running_format", "▶ {0}"),
                DisplayName(single));
        }
        else
        {
            // Distinct-script count (map keys), not total in-flight runs —
            // "N scripts running" reads as N different scripts.
            text = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.T("panel.statusstrip.center_scripts_running_format", "▶ {0} scripts running"),
                _runningScripts.Count);
        }
        CenterContextual = text;
    }

    // Engine lifecycle events carry the .phx file name; the strip shows the
    // bare stem, matching the Scripts panel's row labels.
    private static string DisplayName(string scriptName) =>
        scriptName.EndsWith(".phx", StringComparison.OrdinalIgnoreCase)
            ? scriptName[..^4]
            : scriptName;

    /// <summary>Formatted "Layers: N" readout.</summary>
    public string LayersText => _layersText;
    /// <summary>Formatted "FPS: X.X" readout (or "FPS: —" before HUD ready).</summary>
    public string FpsText => _fpsText;
    /// <summary>Formatted "WSS :PORT · N clients" badge.</summary>
    public string WsText => _wsText;

    /// <summary>
    /// Collapsed when the master toggle is off (hides the badge entirely)
    /// and Visible otherwise.
    /// </summary>
    public Visibility WsBadgeVisibility => _wsBadgeVisibility;

    /// <summary>
    /// 0.55 when listening with no clients OR starting; 1.0 once a real
    /// client connects — a dim affordance on the idle state.
    /// </summary>
    public double WsBadgeOpacity => _wsBadgeOpacity;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _status.StateChanged -= OnStateChanged;
        // Stop the timer so a pop-out re-host doesn't leave a dangling Tick
        // pinning this VM after the panel is torn down.
        var t = _clockTimer;
        _clockTimer = null;
        if (t is not null)
        {
            try { t.Stop(); } catch { }
        }
        // Stop the stats poll timer alongside the clock timer.
        var s = _statsTimer;
        _statsTimer = null;
        if (s is not null)
        {
            try { s.Stop(); } catch { }
        }
        // Unhook the script-engine lifecycle feed and stop the error-flash
        // timer so neither pins this VM after the panel is torn down.
        if (_scriptManager is not null && _onScriptLifecycle is not null)
        {
            try { _scriptManager.ScriptLifecycle -= _onScriptLifecycle; } catch { }
        }
        var f = _errorFlashTimer;
        _errorFlashTimer = null;
        if (f is not null)
        {
            try { f.Stop(); } catch { }
        }
    }

    public ConnectionState StreamerBot { get => _streamerBot; private set => Set(ref _streamerBot, value); }
    public ConnectionState HudOverlay  { get => _hudOverlay;  private set => Set(ref _hudOverlay,  value); }
    public ConnectionState IpcBus      { get => _ipcBus;      private set => Set(ref _ipcBus,      value); }

    // Pull live port labels off AppConfig / Bus instead of
    // hardcoded strings so changing the Streamer.bot URL or HUD Server Port
    // in Settings is reflected in the status strip on the next read. The Bus
    // listen port is a Bus constant (18081 today, exposed as Bus.ListenPort
    // for re-use). Each getter is cheap — these are referenced by x:Bind
    // OneWay against the dots' Sub property which queries on every Pull().
    public string StreamerBotSub
    {
        get
        {
            var url = ConfigManager.Current?.StreamerBotUrl;
            if (string.IsNullOrWhiteSpace(url)) return "ws://127.0.0.1:8080";
            // Trim trailing slash for the at-a-glance display.
            return url.TrimEnd('/');
        }
    }
    public string HudOverlaySub
    {
        get
        {
            int p = ConfigManager.Current?.HUDServerPort ?? 18080;
            return string.Format(
                Localizer.T("panel.statusstrip.dot.port_format", "port {0}"),
                p.ToString(CultureInfo.InvariantCulture));
        }
    }
    // Bus listen port — Bus.cs hardcodes 18081 (private ctor default) and
    // doesn't expose a public accessor. Mirror the constant here so changes
    // to Bus.cs ripple through a single grep, and the status strip never
    // diverges from where the bus actually listens.
    private const int BusListenPort = 18081;
    public string IpcBusSub      => string.Format(
        Localizer.T("panel.statusstrip.dot.port_format", "port {0}"),
        BusListenPort.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Right-zone clock per Design_Orders §4.8 — HH:mm in MonoFont. Updates
    /// every 15 s so the minute roll lands within the spec's perceptible
    /// window without spinning the dispatcher every second.
    /// </summary>
    public string Clock { get => _clock; private set => Set(ref _clock, value); }

    /// <summary>
    /// Center contextual slot per Design_Orders §4.8, fed by the
    /// script-engine lifecycle producer: "Script Engine ready" when idle,
    /// "▶ &lt;name&gt;" while one script runs, "▶ N scripts running" beyond
    /// that, and a transient "⚠ &lt;name&gt;" flash on error. The setter
    /// stays public so future §4.8 hooks (per-layer FPS readout etc.) can
    /// publish without re-touching the XAML.
    /// </summary>
    public string CenterContextual
    {
        get => _centerContextual;
        set => Set(ref _centerContextual, value ?? string.Empty);
    }

    // _status.StateChanged may fire on a background thread (the bridge to
    // Bus / WS event loops). Marshal to the UI thread before
    // touching x:Bind-watched properties — otherwise WinUI raises COMException
    // when the bound DependencyProperty updates off the dispatcher.
    // Payload-typed handler. The status strip re-pulls all three
    // channels each fire (the per-channel diff lives in the property setters'
    // change detection) so we don't branch on e.Channel here.
    private void OnStateChanged(object? sender, ConnectionStateChange e)
    {
        // HasThreadAccess fast-path baked into Post.
        _ui.Post(Pull);
    }

    private void Pull()
    {
        StreamerBot = _status.StreamerBot;
        HudOverlay  = _status.HudOverlay;
        IpcBus      = _status.IpcBus;
    }

    private void RefreshClock()
    {
        // CurrentCulture so the suite respects an active locale's 12h/24h
        // preference. Design_Orders §4.8 shows HH:mm (24h) as the canonical
        // example but the spec's "MonoValue font + version string" doesn't
        // mandate 24h — defer to user-locale.
        Clock = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
    }
}
