using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.GiveawayPanel;

/// <summary>
/// ViewModel for the Hub Giveaway page (giveaway.jsx — GiveawayScreen). Binds
/// to the already-defined <see cref="IGiveawaySource"/> contract for all
/// read/command surfaces and to <see cref="IChatSource"/> for the draw-winner
/// "Announce in chat" action. The page is the button-side front-end onto the
/// same logic the giveaway.* script nodes invoke ("one implementation, two
/// front-ends").
///
/// Subscribes to GiveawaysChanged / EntrantsChanged in the ctor and
/// unsubscribes in <see cref="Dispose"/>, mirroring the other panel VMs.
/// All source events are marshalled to the UI thread through the injected
/// dispatcher pump before touching the ObservableCollections.
/// </summary>
public sealed class GiveawayViewModel : ObservableObject, IDisposable
{
    private readonly IGiveawaySource _source;
    private readonly IChatSource? _chat;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueue? _dispatcher;

    private bool _disposed;
    private bool _loaded;

    // Selected giveaway, fully hydrated.
    private GiveawayInfo? _selected;
    // Picker filter text (title / id contains, case-insensitive).
    private string _pickerFilter = string.Empty;
    private List<GiveawayInfo> _allGiveaways = new();

    // Draw-winner overlay state.
    private bool _winnerOverlayOpen;
    private string _winnerName = string.Empty;
    private int _winnerTickets;
    private string _winnerOdds = string.Empty;

    // Create flow.
    private bool _createDialogOpen;
    private string _createTitleDraft = string.Empty;

    public GiveawayViewModel(IGiveawaySource source, DispatcherQueue? dispatcher, IChatSource? chat = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _chat = chat;
        _dispatcher = dispatcher;
        _ui = new UiDispatcherPump(dispatcher);

        _source.GiveawaysChanged += OnGiveawaysChanged;
        _source.EntrantsChanged += OnEntrantsChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.GiveawaysChanged -= OnGiveawaysChanged;
        _source.EntrantsChanged -= OnEntrantsChanged;
    }

    // ── Bound collections ───────────────────────────────────────────────
    public ObservableCollection<GiveawayPickerItemVm> PickerItems { get; } = new();
    public ObservableCollection<GiveawayEntrantRowVm> Entrants { get; } = new();
    public ObservableCollection<GiveawayActivityRowVm> Activity { get; } = new();

    // ── Initial load ────────────────────────────────────────────────────

    /// <summary>
    /// Kicked off once from the view's Loaded handler. Hydrates the picker
    /// list and selects the default giveaway (or the newest) for the detail +
    /// entrant columns.
    /// </summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshGiveawaysAsync().ConfigureAwait(false);
    }

    private async Task RefreshGiveawaysAsync(CancellationToken ct = default)
    {
        IReadOnlyList<GiveawayInfo> list;
        try { list = await _source.ListAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayViewModel", "ListAsync failed", ex); return; }

        long? preferredId = _selected?.Id
            ?? _source.DefaultGiveawayId
            ?? (list.Count > 0 ? list[0].Id : (long?)null);

        _ui.Post(() =>
        {
            if (_disposed) return;
            _allGiveaways = list.ToList();
            RebuildPicker();

            // Re-select: keep the current selection if it still exists,
            // otherwise fall back to default → newest.
            var pick = (preferredId is { } id ? _allGiveaways.FirstOrDefault(g => g.Id == id) : null)
                       ?? _allGiveaways.FirstOrDefault();
            ApplySelection(pick);
        });

        // Detail data (entrants + activity) for the chosen giveaway is loaded
        // off the ApplySelection path; ApplySelection schedules those fetches.
    }

    private void RebuildPicker()
    {
        PickerItems.Clear();
        IEnumerable<GiveawayInfo> filtered = _allGiveaways;
        if (!string.IsNullOrWhiteSpace(_pickerFilter))
        {
            string q = _pickerFilter.Trim();
            filtered = _allGiveaways.Where(g =>
                g.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || g.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                || g.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var g in filtered) PickerItems.Add(new GiveawayPickerItemVm(g));
    }

    // ── Selection ───────────────────────────────────────────────────────

    /// <summary>Selects a giveaway by id (from a picker row click).</summary>
    public void SelectGiveaway(long id)
    {
        var g = _allGiveaways.FirstOrDefault(x => x.Id == id);
        if (g is null) return;
        ApplySelection(g);
    }

    private void ApplySelection(GiveawayInfo? g)
    {
        _selected = g;
        RaiseDetailProperties();
        if (g is null)
        {
            Entrants.Clear();
            Activity.Clear();
            return;
        }
        // Fetch entrants + activity for the freshly-selected giveaway.
        _ = LoadEntrantsAsync(g.Id);
        _ = LoadActivityAsync(g.Id);
    }

    private async Task LoadEntrantsAsync(long id)
    {
        IReadOnlyList<GiveawayEntrantInfo> list;
        try { list = await _source.GetEntrantsAsync(id).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayViewModel", "GetEntrantsAsync failed", ex); return; }

        _ui.Post(() =>
        {
            if (_disposed) return;
            // Only apply if the selection hasn't moved on under us.
            if (_selected?.Id != id) return;
            RebuildEntrants(list);
        });
    }

    private void RebuildEntrants(IReadOnlyList<GiveawayEntrantInfo> list)
    {
        // Contract guarantees descending-by-tickets, but sort defensively so
        // the rank numbering and weight bars are always correct.
        var sorted = list.OrderByDescending(e => e.Tickets).ToList();
        int max = sorted.Count > 0 ? Math.Max(1, sorted[0].Tickets) : 1;

        Entrants.Clear();
        int rank = 1;
        foreach (var e in sorted)
        {
            bool highlight = _winnerOverlayOpen
                             && string.Equals(e.Username, _winnerName, StringComparison.OrdinalIgnoreCase);
            Entrants.Add(new GiveawayEntrantRowVm(e, rank, max, highlight));
            rank++;
        }
        RaiseEntrantSummary();
    }

    private async Task LoadActivityAsync(long id)
    {
        IReadOnlyList<GiveawayActivityEntry> list;
        try { list = await _source.GetActivityAsync(id).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayViewModel", "GetActivityAsync failed", ex); return; }

        _ui.Post(() =>
        {
            if (_disposed) return;
            if (_selected?.Id != id) return;
            Activity.Clear();
            foreach (var a in list) Activity.Add(new GiveawayActivityRowVm(a));
        });
    }

    // ── Detail-column projections (left card) ───────────────────────────

    public bool HasSelection => _selected is not null;
    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    public string Title => _selected?.Title ?? string.Empty;
    public string MetaId => _selected is null ? string.Empty
        : (string.IsNullOrEmpty(_selected.Key) ? _selected.Id.ToString() : _selected.Key);
    public string MetaOpenedAt => _selected?.OpenedAt ?? string.Empty;
    public string MetaOpenedBy => _selected?.OpenedBy ?? string.Empty;

    public bool IsOpen => _selected?.Status == GiveawayStatus.Open;

    // Status pill text + colors. Open → "open · accepting entries" (ok green
    // with a pulsing dot); closed / drawn use the matching status string.
    public string StatusPillText => _selected?.Status switch
    {
        GiveawayStatus.Open  => "open · accepting entries",
        GiveawayStatus.Closed => "closed",
        GiveawayStatus.Drawn => "drawn · winner picked",
        _ => string.Empty,
    };

    public Brush StatusPillBrush => _selected?.Status switch
    {
        GiveawayStatus.Open  => GiveawayBrushes.Lookup("OkBrush",                 0x6F, 0xA4, 0x6B),
        GiveawayStatus.Drawn => GiveawayBrushes.Lookup("EmberPrimaryBrush",       0xE5, 0xA2, 0x4E),
        _                    => GiveawayBrushes.Lookup("CoalSecondaryTextBrush",  0x9C, 0x8A, 0x72),
    };

    // Pulsing dot only animates while the giveaway is open + accepting.
    public Visibility StatusDotPulseVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;

    public string StatusHint => IsOpen ? "entries accepted from chat" : string.Empty;
    public Visibility StatusHintVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;

    // Stat tiles.
    public string EntrantsValue => _selected?.Entrants.ToString() ?? "0";
    public string TicketsValue => _selected?.Tickets.ToString() ?? "0";
    public string AvgValue => _selected?.Avg ?? "0";
    public string LastEntryValue => _selected?.LastEntry ?? "—";

    // Settings card.
    public string SettingEntryCommand => _selected?.EntryCommand ?? "—";
    public string SettingTicketsPerMessage => _selected?.TicketsPerMessage.ToString() ?? "—";
    public string SettingSubscriberBonus => _selected is null
        ? "—"
        : (_selected.SubscriberBonus > 0
            ? $"+{_selected.SubscriberBonus} ticket / message"
            : "none");
    public string SettingCapPerUser => _selected is null
        ? "—"
        : (_selected.CapPerUser <= 0 ? "unlimited" : _selected.CapPerUser.ToString());
    public string SettingDrawMethod => _selected?.DrawMethod ?? "—";

    // Default toggle.
    public bool IsDefault => _selected is not null && _source.DefaultGiveawayId == _selected.Id;
    public string DefaultToggleLabel => IsDefault ? "Default giveaway" : "Set as default";

    // Close-giveaway button enablement — disabled unless the giveaway is open.
    public bool CanClose => IsOpen;

    private void RaiseDetailProperties()
    {
        Raise(nameof(HasSelection));
        Raise(nameof(DetailVisibility));
        Raise(nameof(EmptyVisibility));
        Raise(nameof(Title));
        Raise(nameof(MetaId));
        Raise(nameof(MetaOpenedAt));
        Raise(nameof(MetaOpenedBy));
        Raise(nameof(IsOpen));
        Raise(nameof(StatusPillText));
        Raise(nameof(StatusPillBrush));
        Raise(nameof(StatusDotPulseVisibility));
        Raise(nameof(StatusHint));
        Raise(nameof(StatusHintVisibility));
        Raise(nameof(EntrantsValue));
        Raise(nameof(TicketsValue));
        Raise(nameof(AvgValue));
        Raise(nameof(LastEntryValue));
        Raise(nameof(SettingEntryCommand));
        Raise(nameof(SettingTicketsPerMessage));
        Raise(nameof(SettingSubscriberBonus));
        Raise(nameof(SettingCapPerUser));
        Raise(nameof(SettingDrawMethod));
        Raise(nameof(IsDefault));
        Raise(nameof(DefaultToggleLabel));
        Raise(nameof(CanClose));
        Raise(nameof(PickerButtonTitle));
        Raise(nameof(PickerButtonId));
        Raise(nameof(PickerButtonDefaultVisibility));
        Raise(nameof(HasPickerSelection));
        Raise(nameof(PickerPlaceholderVisibility));
    }

    // ── Entrant summary (right header) ──────────────────────────────────
    public int EntrantCount => Entrants.Count;
    public int TotalTickets => Entrants.Sum(e => e.Tickets);
    public string EntrantCountText => EntrantCount.ToString();
    public string TotalTicketsText => TotalTickets.ToString();

    private void RaiseEntrantSummary()
    {
        Raise(nameof(EntrantCount));
        Raise(nameof(TotalTickets));
        Raise(nameof(EntrantCountText));
        Raise(nameof(TotalTicketsText));
    }

    // ── Picker button (collapsed-state display in the action strip) ─────
    public bool HasPickerSelection => _selected is not null;
    public Visibility PickerPlaceholderVisibility => _selected is null ? Visibility.Visible : Visibility.Collapsed;
    public string PickerButtonTitle => _selected?.Title ?? "Pick a giveaway…";
    public string PickerButtonId => _selected is null ? string.Empty : MetaId;
    public Visibility PickerButtonDefaultVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;

    public string PickerFilter
    {
        get => _pickerFilter;
        set
        {
            if (Set(ref _pickerFilter, value ?? string.Empty))
                RebuildPicker();
        }
    }

    // ── Create flow ─────────────────────────────────────────────────────
    public bool CreateDialogOpen
    {
        get => _createDialogOpen;
        set => Set(ref _createDialogOpen, value);
    }

    public string CreateTitleDraft
    {
        get => _createTitleDraft;
        set => Set(ref _createTitleDraft, value ?? string.Empty);
    }

    public void BeginCreate()
    {
        CreateTitleDraft = string.Empty;
        CreateDialogOpen = true;
    }

    public void CancelCreate() => CreateDialogOpen = false;

    public async Task ConfirmCreateAsync()
    {
        string title = _createTitleDraft?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title)) return;
        CreateDialogOpen = false;
        try
        {
            var created = await _source.CreateAsync(title).ConfigureAwait(false);
            // GiveawaysChanged will refresh the list; pre-select the new one so
            // the user lands on it even if the event lags.
            _ui.Post(() => { if (!_disposed) _selected = created; });
            await RefreshGiveawaysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "CreateAsync failed", ex);
        }
    }

    // ── Close ───────────────────────────────────────────────────────────
    public async Task CloseSelectedAsync()
    {
        var g = _selected;
        if (g is null || g.Status != GiveawayStatus.Open) return;
        try
        {
            await _source.CloseAsync(g.Id).ConfigureAwait(false);
            await RefreshGiveawaysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "CloseAsync failed", ex);
        }
    }

    // ── Set / clear default ─────────────────────────────────────────────
    public async Task ToggleDefaultAsync()
    {
        var g = _selected;
        if (g is null) return;
        bool makeDefault = !IsDefault;
        try
        {
            await _source.SetDefaultAsync(g.Id, makeDefault).ConfigureAwait(false);
            await RefreshGiveawaysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "SetDefaultAsync failed", ex);
        }
    }

    // ── Draw winner ─────────────────────────────────────────────────────
    public bool WinnerOverlayOpen
    {
        get => _winnerOverlayOpen;
        private set
        {
            if (Set(ref _winnerOverlayOpen, value))
                Raise(nameof(WinnerOverlayVisibility));
        }
    }

    public Visibility WinnerOverlayVisibility => _winnerOverlayOpen ? Visibility.Visible : Visibility.Collapsed;
    public string WinnerName => _winnerName;
    public string WinnerGiveawayTitle => _selected?.Title ?? string.Empty;

    // "{tickets} tickets · 1 in {odds}" — matches the JSX reveal sub-line.
    public string WinnerOddsText => _winnerOdds;

    /// <summary>Draws a weighted winner and opens the reveal overlay.</summary>
    public async Task DrawWinnerAsync()
    {
        var g = _selected;
        if (g is null) return;
        (string WinnerName, int WinnerTickets)? result;
        try { result = await _source.DrawWinnerAsync(g.Id).ConfigureAwait(false); }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "DrawWinnerAsync failed", ex);
            return;
        }
        if (result is null)
        {
            // Empty pool — nothing to draw.
            GlobalLogger.Log("Draw winner skipped — entrant pool is empty.", "GiveawayViewModel", LogLevel.System);
            return;
        }

        int total = TotalTickets;
        int odds = result.Value.WinnerTickets > 0
            ? Math.Max(1, (int)Math.Round((double)total / result.Value.WinnerTickets))
            : 0;

        _ui.Post(() =>
        {
            if (_disposed) return;
            _winnerName = result.Value.WinnerName;
            _winnerTickets = result.Value.WinnerTickets;
            _winnerOdds = $"{_winnerTickets} tickets · 1 in {odds}";
            Raise(nameof(WinnerName));
            Raise(nameof(WinnerGiveawayTitle));
            Raise(nameof(WinnerOddsText));
            WinnerOverlayOpen = true;
            // Re-highlight the winner row in the entrant list.
            HighlightWinnerRow();
        });
    }

    public Task ReDrawAsync() => DrawWinnerAsync();

    public void CloseWinnerOverlay()
    {
        WinnerOverlayOpen = false;
        // Drop the row highlight.
        _winnerName = string.Empty;
        HighlightWinnerRow();
    }

    /// <summary>
    /// Posts a "{winner} won the giveaway" line to chat as the bot. Uses
    /// IChatSource.SendAsBotAsync per the brief; no-ops cleanly when chat
    /// isn't wired (design-time host) so the button never throws.
    /// </summary>
    public async Task AnnounceWinnerAsync()
    {
        if (_chat is null) return;
        if (string.IsNullOrEmpty(_winnerName)) return;
        string title = _selected?.Title ?? "the giveaway";
        string msg = $"🎉 Congratulations @{_winnerName} — you won {title}! ({_winnerTickets} tickets)";
        try { await _chat.SendAsBotAsync(msg).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("GiveawayViewModel", "AnnounceWinnerAsync failed", ex); }
    }

    /// <summary>The winner name, for the "Copy name" clipboard action.</summary>
    public string WinnerNameForCopy => _winnerName;

    private void HighlightWinnerRow()
    {
        // Re-fetch + rebuild the entrant rows so the winner-highlight flag is
        // reapplied (the row VMs are immutable w.r.t. highlight). Cheap — the
        // list is bounded by entrant count and only happens on draw /
        // overlay-close. Re-fetching keeps the role data intact rather than
        // reconstructing partial rows from the existing VMs.
        if (_selected is null) return;
        _ = LoadEntrantsAsync(_selected.Id);
    }

    // ── Source events ───────────────────────────────────────────────────
    private void OnGiveawaysChanged(object? sender, EventArgs e)
        => _ = RefreshGiveawaysAsync();

    private void OnEntrantsChanged(object? sender, long id)
    {
        // Only reload entrants when the changed giveaway is the one on screen.
        if (_selected?.Id != id) return;
        _ = LoadEntrantsAsync(id);
        _ = LoadActivityAsync(id);
    }
}
