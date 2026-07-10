using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.ChatPanel;

public sealed class ChatViewModel : ObservableObject, IDisposable
{
    // Default chat-row cap used when AppConfig.ChatMaxRows is missing
    // or non-positive. The "X / N" indicator string (CountText) and the trim-
    // oldest cap both derive from _maxRows so they can never drift apart;
    // previously both ends hard-coded 2000 in separate spots.
    private const int DefaultMaxRows = 2000;
    private readonly int _maxRows;

    // Amortised at-cap eviction. RemoveAt(0) on a full 2000-row
    // ObservableCollection is O(n) per chat message (element shift, plus a
    // CollectionChanged.Remove the view has to re-index for), and during an
    // active stream the buffer sits at cap permanently. Rows may overshoot
    // _maxRows by up to this many rows before one Reset + refill cuts it
    // back to the cap — CountText can briefly read above the cap between
    // trims, still bounded. Mirrors SystemLogViewModel.OverflowTrimBatch.
    private const int OverflowTrimBatch = 128;

    private readonly IChatSource _source;
    // Per-VM dispatcher pump, ctor-injected by PanelFactory.
    // Replaces the prior static DispatcherQueueOwner slot.
    private readonly UiDispatcherPump _ui;
    private readonly IConnectionStatus? _status;
    private string _draft = string.Empty;
    private bool _disposed;
    private ConnectionState _streamerBotState;

    // Dispatcher-hop coalescing. Raid bursts
    // can land 200+ chat messages/sec; previous code did one dq.TryEnqueue per
    // message. Accumulate pending rows on the producer side and enqueue a
    // single FlushPending per dispatcher tick. Also tracks last-emitted
    // CountText so the Raise is skipped when the displayed string doesn't
    // change.
    private readonly object _pendingLock = new();
    private System.Collections.Generic.List<ChatRowVm> _pendingAdds = new();
    private bool _flushScheduled;
    private string _lastEmittedCountText;

    public ChatViewModel(IChatSource source, DispatcherQueue? dispatcher, IConnectionStatus? status = null)
    {
        _source = source;
        _ui = new UiDispatcherPump(dispatcher);
        _status = status;
        // Pull the chat buffer cap from AppConfig. ConfigManager
        // is safe to read at construction: Hub.WinUI's startup loads the
        // config before the workspace view materialises panel VMs (same
        // ordering SystemLogViewModel relies on).
        int configured = ConfigManager.Current?.ChatMaxRows ?? DefaultMaxRows;
        _maxRows = configured > 0 ? configured : DefaultMaxRows;
        _lastEmittedCountText = $"0 / {_maxRows}";
        _source.MessageReceived += OnMessageReceived;
        if (_status is not null)
        {
            _status.StateChanged += OnConnectionStateChanged;
            _streamerBotState = _status.StreamerBot;
        }
        else
        {
            // No status feed → assume connected so the composer stays usable
            // in test/design-time hosts that don't construct a real
            // IConnectionStatus.
            _streamerBotState = ConnectionState.Connected;
        }
        // Fan-out: every subscriber to
        // MessageReceived gets every chat line; no primary-subscriber gate.
        foreach (var m in _source.Snapshot())
        {
            Rows.Add(new ChatRowVm(m));
        }
        TrimRowsToCap();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.MessageReceived -= OnMessageReceived;
        if (_status is not null) _status.StateChanged -= OnConnectionStateChanged;
    }

    public ObservableCollection<ChatRowVm> Rows { get; } = new();

    /// <summary>
    /// Runtime theme-swap hook. Drops the static fallback brush
    /// caches and re-resolves UsernameBrush / BadgeBrush on every materialised
    /// row so x:Bind OneWay picks up the new theme without rebuilding the
    /// row VMs. Called by ChatView.OnActualThemeChanged.
    /// </summary>
    public void RefreshBrushes()
    {
        ChatRowVm.InvalidateBrushCache();
        foreach (var row in Rows) row.RefreshBrushes();
    }

    public string Draft
    {
        get => _draft;
        set => Set(ref _draft, value);
    }

    // Both numerator and denominator now derive from runtime state
    // (_maxRows comes from AppConfig.ChatMaxRows; trim-oldest in FlushPending
    // uses the same field). The literal "/ 2000" used to appear here AND in
    // the trim cap, which silently desynced if either side moved.
    public string CountText => $"{Rows.Count} / {_maxRows}";

    // Composer is enabled only when SB is fully connected; mid-reconnect
    // sends would queue against a dead socket and surface as the generic
    // "DROPPED (not connected)" critical-error log line — the user sees
    // nothing happen. Disabling the composer + Send button up-front makes
    // the failure visible without a round-trip.
    public bool IsConnected => _streamerBotState == ConnectionState.Connected;

    // Surfaced as the placeholder/inline hint when connection is degraded.
    // Empty string when fully connected so the composer's normal
    // "Send as bot…" placeholder remains.
    public string ConnectionHintText => _streamerBotState switch
    {
        ConnectionState.Connected    => string.Empty,
        ConnectionState.Connecting   => "reconnecting…",
        ConnectionState.Degraded     => "reconnecting…",
        ConnectionState.Errored      => "Streamer.bot disconnected",
        ConnectionState.Disconnected => "Streamer.bot disconnected",
        _                            => string.Empty,
    };

    public Microsoft.UI.Xaml.Visibility ConnectionHintVisibility
        => string.IsNullOrEmpty(ConnectionHintText)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    // Explicit disabled-state opacity for the SEND
    // button. The XAML overrides Background/Foreground/BorderBrush with
    // ember tokens, which suppresses WinUI's built-in Disabled visual state;
    // without this, the button stayed ember-bright when IsEnabled flipped
    // false and Majo's 0.10.6 screenshot read it as "still active while
    // Streamer.bot disconnected." Driving Opacity off the same flag matches
    // the system convention of dim = unavailable.
    public double SendButtonOpacity => IsConnected ? 1.0 : 0.45;

    public async Task SendDraftAsync()
    {
        var text = _draft?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (!IsConnected) return;
        Draft = string.Empty;
        await _source.SendAsBotAsync(text);
    }

    private void OnMessageReceived(object? sender, ChatMessage msg)
    {
        // Fan-out: this VM is one of N subscribers.
        var row = new ChatRowVm(msg);
        bool needsSchedule;
        lock (_pendingLock)
        {
            _pendingAdds.Add(row);
            needsSchedule = !_flushScheduled;
            _flushScheduled = true;
        }
        if (!needsSchedule) return;

        // HasThreadAccess fast-path baked into Post.
        _ui.Post(FlushPending);
    }

    // Payload-typed handler. The ChatPanel only cares about the
    // StreamerBot channel; the diff happens in PullConnectionState so we
    // ignore other channels' transitions cheaply rather than gating here.
    private void OnConnectionStateChanged(object? sender, ConnectionStateChange e)
    {
        if (_status is null) return;
        _ui.Post(PullConnectionState);
    }

    private void PullConnectionState()
    {
        if (_status is null) return;
        var next = _status.StreamerBot;
        if (next == _streamerBotState) return;
        _streamerBotState = next;
        Raise(nameof(IsConnected));
        Raise(nameof(SendButtonOpacity));
        Raise(nameof(ConnectionHintText));
        Raise(nameof(ConnectionHintVisibility));
    }

    private void FlushPending()
    {
        System.Collections.Generic.List<ChatRowVm> batch;
        lock (_pendingLock)
        {
            batch = _pendingAdds;
            _pendingAdds = new System.Collections.Generic.List<ChatRowVm>();
            _flushScheduled = false;
        }
        foreach (var row in batch)
        {
            Rows.Add(row);
        }
        TrimRowsToCap();
        // Only raise CountText when the string actually
        // changed. Whenever a trim lands Rows back on the cap, CountText is
        // constant across appends; the prior code re-fired the property
        // change on every append forever.
        string next = CountText;
        if (!string.Equals(_lastEmittedCountText, next, StringComparison.Ordinal))
        {
            _lastEmittedCountText = next;
            Raise(nameof(CountText));
        }
    }

    /// <summary>
    /// Drops the oldest rows once <see cref="Rows"/> has overshot
    /// <c>_maxRows</c> by more than <see cref="OverflowTrimBatch"/>, cutting
    /// back to exactly the cap in one pass. Removal is head-first per index —
    /// NOT Clear+refill — because a Reset drops the ListView's scroll anchor,
    /// and a viewer scrolled up reading history would be yanked off their
    /// position every trim; per-index head removals are cheap under UI
    /// virtualization (off-screen index adjustments) and keep the viewport
    /// continuous, exactly like the pre-batching per-message eviction.
    /// </summary>
    private void TrimRowsToCap()
    {
        int over = Rows.Count - _maxRows;
        if (over <= OverflowTrimBatch) return;
        for (int i = 0; i < over; i++) Rows.RemoveAt(0);
    }
}
