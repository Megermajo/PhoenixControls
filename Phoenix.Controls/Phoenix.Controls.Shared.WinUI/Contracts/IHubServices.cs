namespace Phoenix.Controls.Shared.WinUI.Contracts;

// Top-level container for everything Hub.WinUI panels consume.
// The bridge implements this against the existing WinForms Hub
// services (Bus / HUDServer / WS / ScriptManager /
// GlobalLogger). Panels bind directly to the sub-interfaces.
//
// Contract requires IAsyncDisposable so the host (MainWindow's
// Closed handler) can hand-walk one entry point and have every sub-service
// — most of which subscribe to Bus / GlobalLogger / WS for the process
// lifetime — get a chance to detach. The production HubServices already
// implements DisposeAsync; declaring it here means a fresh impl
// (test double, alt host) can't silently skip cleanup. IAsyncDisposable
// rather than IDisposable because the bridge does async work (e.g.
// awaiting HUDServer shutdown).
public interface IHubServices : IAsyncDisposable
{
    ILiveFeedSource       LiveFeed  { get; }
    IChatSource           Chat      { get; }
    IScriptHostMonitor    Scripts   { get; }
    ISystemLogSource      SystemLog { get; }
    IConnectionStatus     Status    { get; }
    ILayerRegistrySource  Layers    { get; }
    IGiveawaySource       Giveaway  { get; }
}

/// <summary>
/// Read + command surface the Hub Giveaway page binds to. Backed by the Hub
/// runtime GiveawayService (DB-backed); HubServices bridges runtime models to
/// the DTO records in Records.cs. The page is the second front-end onto the
/// same logic the giveaway.* script commands invoke — "one implementation,
/// two front-ends" (button + node).
/// </summary>
public interface IGiveawaySource
{
    /// <summary>All giveaways, newest first.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<GiveawayInfo>> ListAsync(
        System.Threading.CancellationToken ct = default);

    System.Threading.Tasks.Task<GiveawayInfo?> GetAsync(
        long id, System.Threading.CancellationToken ct = default);

    /// <summary>Entrants for a giveaway, sorted by tickets descending.</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<GiveawayEntrantInfo>> GetEntrantsAsync(
        long id, System.Threading.CancellationToken ct = default);

    /// <summary>Recent activity for one giveaway (newest first).</summary>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<GiveawayActivityEntry>> GetActivityAsync(
        long id, System.Threading.CancellationToken ct = default);

    /// <summary>Creates a new open giveaway from a title and returns it.</summary>
    System.Threading.Tasks.Task<GiveawayInfo> CreateAsync(
        string title, System.Threading.CancellationToken ct = default);

    /// <summary>Closes a giveaway (sets it inactive; keeps the ticket pool for the draw/display).</summary>
    System.Threading.Tasks.Task CloseAsync(
        long id, System.Threading.CancellationToken ct = default);

    /// <summary>Draws a weighted winner; null when the pool is empty.</summary>
    System.Threading.Tasks.Task<(string WinnerName, int WinnerTickets)?> DrawWinnerAsync(
        long id, System.Threading.CancellationToken ct = default);

    /// <summary>Marks (or clears) a giveaway as the app-wide default.</summary>
    System.Threading.Tasks.Task SetDefaultAsync(
        long id, bool isDefault, System.Threading.CancellationToken ct = default);

    /// <summary>Id of the current default giveaway, or null when none is set.</summary>
    long? DefaultGiveawayId { get; }

    /// <summary>Fires when the giveaway list or any giveaway's state changes.</summary>
    event System.EventHandler? GiveawaysChanged;

    /// <summary>Fires with the affected giveaway id when its entrant pool changes.</summary>
    event System.EventHandler<long>? EntrantsChanged;
}

// Pop-outs fan out, no primary-subscriber gate.
//
// The previous IPrimarySubscriberGate contract limited each source to a
// single VM rendering its rows; the decision to allow N pop-outs of the
// same panel killed that model — fan-out is now source-side, every VM
// that subscribes to the source's multicast event gets every row. The
// gate type and its hand-off plumbing (HubWorkspaceView's
// Release/Reacquire calls) were removed alongside this change.
//
// Each source's `event EventHandler<T>` IS the subscriber list — adding
// a handler in the VM ctor and removing it in Dispose is the
// fan-out subscribe/unsubscribe protocol. No further per-source list
// management is required.

public interface ILiveFeedSource
{
    IReadOnlyList<LiveFeedEntry> Snapshot();              // initial render
    event EventHandler<LiveFeedEntry> EntryAdded;         // append-only stream

    // Filtering is per-VM, not per-source. Each LiveFeedViewModel
    // keeps its own _buffer + _selectedFilter so two pop-outs of the same
    // panel can run independent filters without trampling each other —
    // the source slot was a global trap (calling SetFilter from one
    // pop-out would silently change what every other subscriber received
    // on the next ingress event). The method is preserved as a no-op for
    // contract stability; new code MUST NOT call it. Will be removed
    // once a clean breaking-change window opens.
    [System.Obsolete("Filtering is per-VM. Use LiveFeedViewModel.SelectedFilter / Snapshot + local predicate. SetFilter on the shared source corrupts other subscribers' views.")]
    void SetFilter(LiveFeedFilter filter);
}

public interface IChatSource
{
    IReadOnlyList<ChatMessage> Snapshot();
    event EventHandler<ChatMessage> MessageReceived;
    Task SendAsBotAsync(string text, CancellationToken ct = default);
}

public interface IScriptHostMonitor
{
    IReadOnlyList<ScriptStatus> Snapshot();
    event EventHandler<ScriptStatus> StatusChanged;
    Task SetEnabledAsync(string scriptName, bool enabled, CancellationToken ct = default);
    Task ReloadAsync(string scriptName, CancellationToken ct = default);
    Task OpenInArchitectAsync(string scriptName, CancellationToken ct = default);
}

public interface ISystemLogSource
{
    IReadOnlyList<SystemLogEntry> Snapshot();             // last ~2000 (matches GlobalLogger ring)
    event EventHandler<SystemLogEntry> EntryAdded;
    void SetLevelFilter(SystemLogLevel mask);
}

public interface IConnectionStatus
{
    ConnectionState StreamerBot { get; }
    ConnectionState HudOverlay  { get; }
    ConnectionState IpcBus      { get; }
    // Visualist / Architect bus-client status was dropped here —
    // post-T15 they're sibling libraries embedded in Hub.WinUI, not
    // separate processes, so the bus-handshake signal that used to mean
    // "Architect is up" no longer has a meaningful UX surface. The aggregator
    // (ConnectionStatus.cs) keeps watching the bus-client events for future
    // surfaces, but the contract slot is gone until a consumer materialises.
    //
    // StateChanged is typed with a payload describing which channel
    // changed and the previous/current state, instead of the earlier bare
    // EventHandler with EventArgs.Empty. The producer fans one event per
    // changed channel inside a single Sample() tick — subscribers see one
    // fire per real transition rather than a coalesced "something flipped"
    // pulse they'd have to diff against a snapshot to interpret.
    event EventHandler<ConnectionStateChange> StateChanged;
}

/// <summary>
/// Per-layer presence tracker. Visualist's LayerRail binds Active dots
/// to LiveLayerChanged so the rail reflects whether each layer is being
/// rendered by at least one OBS browser source.
/// </summary>
public interface ILayerRegistrySource
{
    /// <summary>True if any WebSocket is currently registered for the layer.</summary>
    bool IsLayerActive(string layerId);

    /// <summary>Snapshot of layer ids that currently have at least one connection.</summary>
    IReadOnlyList<string> GetActiveLayerIds();

    /// <summary>Fires (layerId, isActive) when a layer crosses 0 to 1 connections.</summary>
    event Action<string, bool> LiveLayerChanged;
}
