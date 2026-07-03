using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Services;

// Status-strip aggregator. The three upstream signals each have a different
// observation surface in the existing Hub:
//
//   • Streamer.bot WebSocket — WS.IsConnected + OnConnectionStatusChanged
//   • HUD overlay server     — HUDServer.IsStarted (no event; polled)
//   • IPC bus listener       — Bus.IsListening (no event; polled)
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

    private readonly Action<bool>          _onWsChanged;

    private ConnectionState _streamerBot;
    private ConnectionState _hudOverlay;
    private ConnectionState _ipcBus;
    private int _disposed;

    public ConnectionStatus(IUiDispatcher uiDispatcher, HUDServer hud)
    {
        _ui = uiDispatcher;
        _hud = hud;

        _onWsChanged = connected => Sample();

        WS.Instance.OnConnectionStatusChanged += _onWsChanged;

        // Initial reading — populates the cached state without firing
        // StateChanged (no transition has occurred yet from the consumer's POV).
        StreamerBot = WS.Instance.IsConnected ? ConnectionState.Connected : ConnectionState.Disconnected;
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
        // No more ternary-collapse to Disconnected.
        // The previous shape (`IsConnected ? Connected : Disconnected`)
        // silently overwrote any richer state (Errored, Degraded,
        // Connecting) on every resampling tick. Connected is still derived
        // from the boolean source-of-truth — but when the source reports
        // "not connected" we preserve a previously-set richer state and only
        // fall to Disconnected from an actual transition. That keeps the
        // door open for future plumbing (TODO — wire WS / HUD /
        // Bus failure events into Errored / Degraded) without re-touching
        // this aggregator.
        var newSb  = Resample(_streamerBot, WS.Instance.IsConnected);
        var newHud = Resample(_hudOverlay,  HUDServer.IsStarted);
        var newBus = Resample(_ipcBus,      Bus.Instance.IsListening);

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
    /// Combine a boolean "is the underlying transport up?" signal with the
    /// previously-cached state, preserving richer states (Errored, Degraded,
    /// Connecting) when the transport is reported down. Used by
    /// <see cref="Sample"/> to avoid the ternary-collapse.
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
    }
}
