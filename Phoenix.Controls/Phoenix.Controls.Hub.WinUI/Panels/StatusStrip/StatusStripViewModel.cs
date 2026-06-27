using System;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
// B44 — Visibility lives on the XAML namespace; alias-importing it here
// keeps the property accessors readable instead of saying
// Microsoft.UI.Xaml.Visibility.Visible everywhere.
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace Phoenix.Controls.Hub.WinUI.Panels.StatusStrip;

public sealed class StatusStripViewModel : ObservableObject, System.IDisposable
{
    private readonly IConnectionStatus _status;
    // C1 (2026-05-14): per-VM dispatcher pump, ctor-injected by PanelFactory.
    private readonly UiDispatcherPump _ui;

    private ConnectionState _streamerBot;
    private ConnectionState _hudOverlay;
    private ConnectionState _ipcBus;
    private bool _disposed;

    // QC44-01 (2026-05-15): clock + version stamp restored per Design_Orders
    // §4.8. The strip used to be a left-zone-only widget; the spec asks for
    // left status zone + center contextual + right meta (clock + version).
    // Center contextual is wired empty for now — call sites will publish
    // labels (e.g. "Script Engine ready", per-layer FPS readout) here as
    // the §4.8 hooks land.
    private string _clock = string.Empty;
    private string _centerContextual = string.Empty;
    private DispatcherTimer? _clockTimer;
    // B8 / B44 (audit 2026-05-24) — 1 s dispatcher tick polls
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
    // B44 — visibility/opacity backing for the WS badge. Hidden entirely
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

        // QC44-01: 1-minute resolution is enough for the §4.8 HH:mm format.
        // Aligned to dispatcher so binding updates land on the UI thread
        // without an explicit Post.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clockTimer.Tick += (_, _) => RefreshClock();
        _clockTimer.Start();
        RefreshClock();

        // B8 / B44 (audit 2026-05-24) — center / WS readouts. Polls every
        // second through DispatcherTimer so updates land on the UI thread
        // without an explicit Post wrapper. RefreshStats also runs once
        // up-front so the strip renders meaningful initial values before
        // the first tick.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();
        RefreshStats();
    }

    /// <summary>
    /// B8 / B44 readouts — read LayerRegistry / HUDServer / AppConfig and
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

        // B44 (audit/winui-regressions-2026-05-24, finished sweep) — WS server
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
        // B44 — three rendered states drive the badge's Visibility/Opacity.
        // The audit explicitly asks for "hide entirely when disabled" and
        // "dim when 0 clients"; we additionally distinguish "starting…" from
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
                // Audit: "when enabled but 0 clients — dim it." Full opacity
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

    /// <summary>B8 — formatted "Layers: N" readout.</summary>
    public string LayersText => _layersText;
    /// <summary>B8 — formatted "FPS: X.X" readout (or "FPS: —" before HUD ready).</summary>
    public string FpsText => _fpsText;
    /// <summary>B44 — formatted "WSS :PORT · N clients" badge.</summary>
    public string WsText => _wsText;

    /// <summary>
    /// B44 — Collapsed when the master toggle is off (hides the badge entirely
    /// per audit) and Visible otherwise.
    /// </summary>
    public Visibility WsBadgeVisibility => _wsBadgeVisibility;

    /// <summary>
    /// B44 — 0.55 when listening with no clients OR starting; 1.0 once a real
    /// client connects. Audit calls for a dim affordance on the idle state.
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
        // B8 / B44 — stop the stats poll timer alongside the clock timer.
        var s = _statsTimer;
        _statsTimer = null;
        if (s is not null)
        {
            try { s.Stop(); } catch { }
        }
    }

    public ConnectionState StreamerBot { get => _streamerBot; private set => Set(ref _streamerBot, value); }
    public ConnectionState HudOverlay  { get => _hudOverlay;  private set => Set(ref _hudOverlay,  value); }
    public ConnectionState IpcBus      { get => _ipcBus;      private set => Set(ref _ipcBus,      value); }

    // Hub UI sweep P2 — pull live port labels off AppConfig / Bus instead of
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
            return $"port {p.ToString(CultureInfo.InvariantCulture)}";
        }
    }
    // Bus listen port — Bus.cs hardcodes 18081 (private ctor default) and
    // doesn't expose a public accessor. Mirror the constant here so changes
    // to Bus.cs ripple through a single grep, and the status strip never
    // diverges from where the bus actually listens.
    private const int BusListenPort = 18081;
    public string IpcBusSub      => $"port {BusListenPort.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Right-zone clock per Design_Orders §4.8 — HH:mm in MonoFont. Updates
    /// every 15 s so the minute roll lands within the spec's perceptible
    /// window without spinning the dispatcher every second.
    /// </summary>
    public string Clock { get => _clock; private set => Set(ref _clock, value); }

    /// <summary>
    /// Center contextual slot per Design_Orders §4.8. Wired empty for now —
    /// the §4.8 hooks (e.g. "Script Engine ready", per-layer FPS readout)
    /// land in a follow-up; this property is the published surface so they
    /// can publish without re-touching the XAML.
    /// </summary>
    public string CenterContextual
    {
        get => _centerContextual;
        set => Set(ref _centerContextual, value ?? string.Empty);
    }

    // _status.StateChanged may fire on a background thread (Track 4's bridge to
    // Bus / WS event loops). Marshal to the UI thread before
    // touching x:Bind-watched properties — otherwise WinUI raises COMException
    // when the bound DependencyProperty updates off the dispatcher.
    //  Payload-typed handler. The status strip re-pulls all three
    // channels each fire (the per-channel diff lives in the property setters'
    // change detection) so we don't branch on e.Channel here.
    private void OnStateChanged(object? sender, ConnectionStateChange e)
    {
        // Perf-review H1: HasThreadAccess fast-path baked into Post.
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
