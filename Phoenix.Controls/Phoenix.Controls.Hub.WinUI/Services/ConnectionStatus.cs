using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Services;

// Status-strip aggregator. The three upstream signals each have a different
// observation surface in the existing Hub:
//
//   • Streamer.bot WebSocket — fully event+state driven: WS.IsConnected /
//     AuthFailedFatal / ReconnectPending, resampled on every
//     OnConnectionStatusChanged fire (WS re-raises it when its GetEvents
//     probe resolves, so richer readings land promptly).
//   • HUD overlay server     — HUDServer.IsStarted (polled) plus the
//     OnFatalError event for the accept-loop-died case.
//   • IPC bus listener       — Bus.IsListening (no failure event; polled).
//
// Where an event exists we subscribe; the rest are sampled by a 30 s sanity
// timer. (Previously the timer ran every 2 s — but WS is event-driven,
// and HUDServer.IsStarted / Bus.IsListening are essentially boot/shutdown
// one-shots that don't need second-by-second resampling. The 30 s interval
// is the catch-all sanity check.) One StateChanged fires per real transition, regardless of which
// sources changed in the same tick. The HUDServer ref is passed in by the
// bootstrapper because it's not a singleton — HubBootstrapper.BootAsync
// constructs it.
//
// The Visualist / Architect bus-client signals tracked here were
// dropped because no consumer ever bound them post-T15 (the
// pillars are sibling libraries embedded in Hub.WinUI now, not separate
// processes; the bus-handshake "Architect is up" signal has no UX
// surface). Bus.OnClientConnectionChanged stays available for any future
// per-pillar bus diagnostic — this aggregator just doesn't republish it.
public sealed class ConnectionStatus : IConnectionStatus, IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly HUDServer _hud;
    private readonly Timer _poll;

    private readonly Action<bool> _onWsChanged;
    private readonly Action       _onHudFatal;

    // Latched by HUDServer.OnFatalError (fires on the accept-loop thread);
    // read by the sampler, which maps it to Errored. Cleared once IsStarted
    // reports true again — i.e. a restarted server supersedes the fault.
    private volatile bool _hudFatal;

    private ConnectionState _streamerBot;
    private ConnectionState _hudOverlay;
    private ConnectionState _ipcBus;
    private int _disposed;

    public ConnectionStatus(IUiDispatcher uiDispatcher, HUDServer hud)
    {
        _ui = uiDispatcher;
        _hud = hud;

        _onWsChanged = connected => Sample();
        _onHudFatal  = () => { _hudFatal = true; Sample(); };

        WS.Instance.OnConnectionStatusChanged += _onWsChanged;
        HUDServer.OnFatalError += _onHudFatal;

        // Initial reading — populates the cached state without firing
        // StateChanged (no transition has occurred yet from the consumer's POV).
        StreamerBot = MapStreamerBot();
        HudOverlay  = HUDServer.IsStarted ? ConnectionState.Connected : ConnectionState.Disconnected;
        IpcBus      = Bus.Instance.IsListening ? ConnectionState.Connected : ConnectionState.Disconnected;

        _poll = new Timer(_ => Sample(), state: null, dueTime: TimeSpan.FromSeconds(30), period: TimeSpan.FromSeconds(30));
    }

    public ConnectionState StreamerBot { get => _streamerBot; private set => _streamerBot = value; }
    public ConnectionState HudOverlay  { get => _hudOverlay;  private set => _hudOverlay  = value; }
    public ConnectionState IpcBus      { get => _ipcBus;      private set => _ipcBus      = value; }

    public event EventHandler<ConnectionStateChange>? StateChanged;

    private void Sample()
    {
        // Per-channel sampling honesty differs by what the source exposes:
        // WS is fully event+state driven, so StreamerBot maps directly from
        // its richer surface (Connected / Errored on fatal auth / Connecting
        // while the reconnector is armed / Disconnected). HUD failures
        // arrive via OnFatalError, latched into _hudFatal until the server
        // reports started again. Bus has no failure event — the 30 s poll
        // is the accepted watchdog there, and Resample preserves any richer
        // reading across its ticks.
        var newSb = MapStreamerBot();

        bool hudUp = HUDServer.IsStarted;
        if (hudUp) _hudFatal = false; // restarted server supersedes the fault
        var newHud = _hudFatal ? ConnectionState.Errored
                               : Resample(_hudOverlay, hudUp);

        var newBus = Resample(_ipcBus, Bus.Instance.IsListening);

        // Snapshot the per-channel transitions BEFORE mutating the
        // cached state so the ConnectionStateChange payload carries the real
        // "previous" value, and so a coalesced multi-channel tick fans into
        // one event per channel instead of a single bare-EventArgs pulse.
        ConnectionStateChange? sbChange  = newSb  != _streamerBot ? new(ConnectionChannel.StreamerBot, _streamerBot, newSb)  : null;
        ConnectionStateChange? hudChange = newHud != _hudOverlay  ? new(ConnectionChannel.HudOverlay,  _hudOverlay,  newHud) : null;
        ConnectionStateChange? busChange = newBus != _ipcBus      ? new(ConnectionChannel.IpcBus,      _ipcBus,      newBus) : null;

        if (sbChange  is null && hudChange is null && busChange is null) return;

        if (sbChange  is not null) _streamerBot = newSb;
        if (hudChange is not null) _hudOverlay  = newHud;
        if (busChange is not null) _ipcBus      = newBus;

        _ui.Post(() =>
        {
            var handler = StateChanged;
            if (handler is null) return;
            if (sbChange  is not null) handler.Invoke(this, sbChange);
            if (hudChange is not null) handler.Invoke(this, hudChange);
            if (busChange is not null) handler.Invoke(this, busChange);
        });
    }

    /// <summary>
    /// Map WS's state surface onto the shared enum. Order matters: a live
    /// socket always wins; a latched fatal auth rejection outranks the
    /// reconnector (WS disables reconnection on fatal auth anyway); an
    /// armed reconnector reads as Connecting rather than a bare
    /// Disconnected.
    /// </summary>
    private static ConnectionState MapStreamerBot()
    {
        var ws = WS.Instance;
        if (ws.IsConnected)      return ConnectionState.Connected;
        if (ws.AuthFailedFatal)  return ConnectionState.Errored;
        if (ws.ReconnectPending) return ConnectionState.Connecting;
        return ConnectionState.Disconnected;
    }

    /// <summary>
    /// Combine a boolean "is the underlying transport up?" signal with the
    /// previously-cached state, preserving richer states (Errored, Degraded,
    /// Connecting) when the transport is reported down. Used by
    /// <see cref="Sample"/> for the HUD / Bus channels to avoid the
    /// ternary-collapse (StreamerBot maps directly from WS state).
    /// </summary>
    private static ConnectionState Resample(ConnectionState previous, bool transportUp)
    {
        if (transportUp) return ConnectionState.Connected;
        // Transport down — keep richer state if previously set; otherwise
        // settle to Disconnected.
        return previous switch
        {
            ConnectionState.Errored    => ConnectionState.Errored,
            ConnectionState.Degraded   => ConnectionState.Degraded,
            ConnectionState.Connecting => ConnectionState.Connecting,
            _                          => ConnectionState.Disconnected,
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _poll.Dispose(); } catch { }
        try { WS.Instance.OnConnectionStatusChanged -= _onWsChanged; } catch { }
        try { HUDServer.OnFatalError -= _onHudFatal; } catch { }
    }
}
