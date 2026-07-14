using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
        // Re-seed the editors only when the selection actually changed or
        // the draft is pristine (still equals the last seeded text) — so a
        // stale draft never leaks across giveaways, but an uncommitted
        // mid-typing draft survives the GiveawaysChanged refreshes every
        // giveaway lifecycle event fires.
        if (_capSeededGiveawayId != g?.Id || _capPerUserDraft == _capPerUserSeeded)
            SeedCapPerUserDraft(g);
        if (_subBonusSeededGiveawayId != g?.Id || _subBonusDraft == _subBonusSeeded)
            SeedSubBonusDraft(g);
        if (_modBonusSeededGiveawayId != g?.Id || _modBonusDraft == _modBonusSeeded)
            SeedModBonusDraft(g);
        if (_ticketPriceSeededGiveawayId != g?.Id || _ticketPriceDraft == _ticketPriceSeeded)
            SeedTicketPriceDraft(g);
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

    // ── Settings card collapse toggle ───────────────────────────────────
    private bool _settingsExpanded = true;

    public bool SettingsExpanded
    {
        get => _settingsExpanded;
        set
        {
            if (Set(ref _settingsExpanded, value))
            {
                Raise(nameof(SettingsBodyVisibility));
                Raise(nameof(SettingsChevronGlyph));
                Raise(nameof(SettingsToggleAutomationName));
            }
        }
    }

    public Visibility SettingsBodyVisibility => _settingsExpanded ? Visibility.Visible : Visibility.Collapsed;

    // Segoe MDL2: E70E = ChevronUp (expanded → click collapses),
    // E70D = ChevronDown (collapsed → click expands).
    public string SettingsChevronGlyph => _settingsExpanded ? "\uE70E" : "\uE70D";

    // Assistive tech reads the toggle's CURRENT state + action from the name
    // (the chevron glyph alone is visual-only).
    public string SettingsToggleAutomationName => _settingsExpanded
        ? "Collapse giveaway settings (currently expanded)"
        : "Expand giveaway settings (currently collapsed)";

    public void ToggleSettingsExpanded() => SettingsExpanded = !SettingsExpanded;

    // ── Per-user cap editor (Settings card) ─────────────────────────────
    private string _capPerUserDraft = string.Empty;
    // Dirtiness tracking: the giveaway the draft was last seeded for and the
    // exact seeded text. Draft != seeded ⇒ the user has typed, and
    // ApplySelection must not clobber it for the same giveaway.
    private long? _capSeededGiveawayId;
    private string _capPerUserSeeded = string.Empty;

    /// <summary>Raw text of the "Max tickets per user" field. 0 = unlimited.</summary>
    public string CapPerUserDraft
    {
        get => _capPerUserDraft;
        set => Set(ref _capPerUserDraft, value ?? string.Empty);
    }

    /// <summary>Seeds the cap draft from the stored value and marks it pristine.</summary>
    private void SeedCapPerUserDraft(GiveawayInfo? g)
    {
        _capSeededGiveawayId = g?.Id;
        _capPerUserSeeded = g?.CapPerUser.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        CapPerUserDraft = _capPerUserSeeded;
    }

    // IGiveawaySource carries no settings-write surface; the cap write routes
    // to the shared Hub-runtime GiveawayService — the same instance
    // GiveawaySourceBridge adapts, so its GiveawaysChanged event round-trips
    // back into this VM. Resolved lazily (only on save) and swappable so a
    // design/test host never touches the real databank.
    internal Func<long, int, Task> CapPerUserSaver { get; set; } =
        static (id, cap) => Phoenix.Controls.Hub.Core.GiveawayService.Instance.SetCapPerUserAsync(id, cap);

    /// <summary>Drops an in-progress cap edit back to the stored value (pristine).</summary>
    public void RevertCapPerUserDraft()
        => SeedCapPerUserDraft(_selected);

    /// <summary>
    /// Persists the cap draft. Numeric-range validation only: non-numeric
    /// input reverts to the stored value, negatives clamp to 0.
    /// </summary>
    public async Task CommitCapPerUserAsync()
    {
        var g = _selected;
        if (g is null) return;

        if (!int.TryParse(_capPerUserDraft.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int cap))
        {
            RevertCapPerUserDraft();
            return;
        }
        cap = Math.Max(0, cap);

        if (cap == g.CapPerUser)
        {
            // Normalize cosmetic variants ("007", padded input) without a
            // write; the value matches the stored one, so this is a re-seed.
            SeedCapPerUserDraft(g);
            return;
        }

        try
        {
            await CapPerUserSaver(g.Id, cap).ConfigureAwait(false);
            // Committed — mark the draft pristine (field-only; no UI raise off
            // the dispatcher) so the follow-up refresh re-seeds it from the
            // freshly stored value.
            _capSeededGiveawayId = g.Id;
            _capPerUserSeeded = _capPerUserDraft;
            await RefreshGiveawaysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "SetCapPerUserAsync failed", ex);
        }
    }

    // ── Subscriber-bonus factor editor (Settings card) ──────────────────
    // Same draft/seed/commit shape as the cap editor. The factor is a ticket
    // WEIGHT multiplier applied to subscribers at draw time: 1 = no bonus
    // (default), 2 = a sub's tickets count double. Subscriber status is
    // gathered fresh from Streamer.bot right before the draw.
    private string _subBonusDraft = string.Empty;
    private long? _subBonusSeededGiveawayId;
    private string _subBonusSeeded = string.Empty;

    /// <summary>Raw text of the "Subscriber bonus" factor field. 1 = no bonus.</summary>
    public string SubBonusDraft
    {
        get => _subBonusDraft;
        set => Set(ref _subBonusDraft, value ?? string.Empty);
    }

    private static string FormatFactor(double f) => f.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Seeds the factor draft from the stored value and marks it pristine.</summary>
    private void SeedSubBonusDraft(GiveawayInfo? g)
    {
        _subBonusSeededGiveawayId = g?.Id;
        _subBonusSeeded = g is null ? string.Empty : FormatFactor(g.SubscriberBonusFactor);
        SubBonusDraft = _subBonusSeeded;
    }

    // Same lazy/swappable saver seam as CapPerUserSaver.
    internal Func<long, double, Task> SubBonusSaver { get; set; } =
        static (id, factor) => Phoenix.Controls.Hub.Core.GiveawayService.Instance.SetSubscriberBonusFactorAsync(id, factor);

    /// <summary>Drops an in-progress factor edit back to the stored value.</summary>
    public void RevertSubBonusDraft()
        => SeedSubBonusDraft(_selected);

    /// <summary>
    /// Persists the factor draft. Numeric-range validation only: non-numeric
    /// input reverts to the stored value; values below 1 clamp to 1 (a factor
    /// under 1 would penalize subs). Accepts "1,5", "1.5" and a trailing "x".
    /// </summary>
    public async Task CommitSubBonusAsync()
    {
        var g = _selected;
        if (g is null) return;

        string raw = _subBonusDraft.Trim().TrimEnd('x', 'X', '×').Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor)
            || double.IsNaN(factor) || double.IsInfinity(factor))
        {
            RevertSubBonusDraft();
            return;
        }
        factor = Math.Clamp(factor, 1.0, 100.0);

        if (Math.Abs(factor - g.SubscriberBonusFactor) < 0.0001)
        {
            SeedSubBonusDraft(g); // normalize cosmetic variants without a write
            return;
        }

        try
        {
            await SubBonusSaver(g.Id, factor).ConfigureAwait(false);
            _subBonusSeededGiveawayId = g.Id;
            _subBonusSeeded = _subBonusDraft;
            await RefreshGiveawaysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "SetSubscriberBonusFactorAsync failed", ex);
        }
    }

    // ── Moderator-bonus factor editor (Settings card) ───────────────────
    // Mirror of the subscriber-bonus editor above. The factor is the ticket
    // WEIGHT multiplier applied to moderators at draw time: 1 = no bonus
    // (default). Mod status comes from the IsSub/IsMod flags the entry
    // script recorded (no live re-check, unlike subs); it multiplies with
    // the subscriber factor for entrants who are both.
    private string _modBonusDraft = string.Empty;
    private long? _modBonusSeededGiveawayId;
    private string _modBonusSeeded = string.Empty;

    /// <summary>Raw text of the "Moderator bonus" factor field. 1 = no bonus.</summary>
    public string ModBonusDraft
    {
        get => _modBonusDraft;
        set => Set(ref _modBonusDraft, value ?? string.Empty);
    }

    /// <summary>Seeds the factor draft from the stored value and marks it pristine.</summary>
    private void SeedModBonusDraft(GiveawayInfo? g)
    {
        _modBonusSeededGiveawayId = g?.Id;
        _modBonusSeeded = g is null ? string.Empty : FormatFactor(g.ModBonusFactor);
        ModBonusDraft = _modBonusSeeded;
    }

    // Same lazy/swappable saver seam as SubBonusSaver.
    internal Func<long, double, Task> ModBonusSaver { get; set; } =
        static (id, factor) => Phoenix.Controls.Hub.Core.GiveawayService.Instance.SetModeratorBonusFactorAsync(id, factor);

    /// <summary>Drops an in-progress factor edit back to the stored value.</summary>
    public void RevertModBonusDraft()
        => SeedModBonusDraft(_selected);

    /// <summary>
    /// Persists the factor draft. Same rules as the subscriber factor:
    /// non-numeric reverts, below-1 clamps to 1, accepts "1,5" / "1.5" / "2x".
    /// </summary>
    public async Task CommitModBonusAsync()
    {
        var g = _selected;
        if (g is null) return;

        string raw = _modBonusDraft.Trim().TrimEnd('x', 'X', '×').Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor)
            || double.IsNaN(factor) || double.IsInfinity(factor))
        {
            RevertModBonusDraft();
            return;
        }
        factor = Math.Clamp(factor, 1.0, 100.0);

        if (Math.Abs(factor - g.ModBonusFactor) < 0.0001)
        {
            SeedModBonusDraft(g); // normalize cosmetic variants without a write
            return;
        }

        try
        {
            await ModBonusSaver(g.Id, factor).ConfigureAwait(false);
            _modBonusSeededGiveawayId = g.Id;
            _modBonusSeeded = _modBonusDraft;
            await RefreshGiveawaysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "SetModeratorBonusFactorAsync failed", ex);
        }
    }

    // ── Ticket-price editor (Settings card) ─────────────────────────────
    // Same draft/seed/commit shape as the cap editor. The price is the
    // channel-point cost PER TICKET charged by Giveaway.Ticket nodes that
    // carry a currency table and follow this giveaway (Public = true);
    // 0 = free entry (default).
    private string _ticketPriceDraft = string.Empty;
    private long? _ticketPriceSeededGiveawayId;
    private string _ticketPriceSeeded = string.Empty;

    /// <summary>Raw text of the "Ticket price" field. 0 = free.</summary>
    public string TicketPriceDraft
    {
        get => _ticketPriceDraft;
        set => Set(ref _ticketPriceDraft, value ?? string.Empty);
    }

    /// <summary>Seeds the price draft from the stored value and marks it pristine.</summary>
    private void SeedTicketPriceDraft(GiveawayInfo? g)
    {
        _ticketPriceSeededGiveawayId = g?.Id;
        _ticketPriceSeeded = g?.TicketPrice.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TicketPriceDraft = _ticketPriceSeeded;
    }

    // Same lazy/swappable saver seam as CapPerUserSaver.
    internal Func<long, int, Task> TicketPriceSaver { get; set; } =
        static (id, price) => Phoenix.Controls.Hub.Core.GiveawayService.Instance.SetTicketPriceAsync(id, price);

    /// <summary>Drops an in-progress price edit back to the stored value.</summary>
    public void RevertTicketPriceDraft()
        => SeedTicketPriceDraft(_selected);

    /// <summary>
    /// Persists the price draft. Numeric-range validation only: non-numeric
    /// input reverts to the stored value, negatives clamp to 0.
    /// </summary>
    public async Task CommitTicketPriceAsync()
    {
        var g = _selected;
        if (g is null) return;

        if (!int.TryParse(_ticketPriceDraft.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int price))
        {
            RevertTicketPriceDraft();
            return;
        }
        price = Math.Max(0, price);

        if (price == g.TicketPrice)
        {
            SeedTicketPriceDraft(g); // normalize cosmetic variants without a write
            return;
        }

        try
        {
            await TicketPriceSaver(g.Id, price).ConfigureAwait(false);
            _ticketPriceSeededGiveawayId = g.Id;
            _ticketPriceSeeded = _ticketPriceDraft;
            await RefreshGiveawaysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "SetTicketPriceAsync failed", ex);
        }
    }

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
        Raise(nameof(IsDefault));
        Raise(nameof(DefaultToggleLabel));
        Raise(nameof(CanClose));
        Raise(nameof(CanDraw));
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

    // A draw with a sub-bonus factor blocks on the Streamer.bot gather for
    // seconds — gate re-entry so a double-click (or a re-draw while one is in
    // flight) can't queue a second draw. The service serializes draws too;
    // this flag keeps the button honest.
    private bool _drawInFlight;
    public bool CanDraw => HasSelection && !_drawInFlight;

    /// <summary>Draws a weighted winner and opens the reveal overlay.</summary>
    public async Task DrawWinnerAsync()
    {
        var g = _selected;
        if (g is null || _drawInFlight) return;
        _drawInFlight = true;
        Raise(nameof(CanDraw));   // still on the caller's (UI) thread

        (string WinnerName, int WinnerTickets, int OddsOneIn)? result;
        try { result = await _source.DrawWinnerAsync(g.Id).ConfigureAwait(false); }
        catch (Exception ex)
        {
            GlobalLogger.Error("GiveawayViewModel", "DrawWinnerAsync failed", ex);
            ClearDrawInFlight();
            return;
        }
        if (result is null)
        {
            // Empty pool — nothing to draw.
            GlobalLogger.Log("Draw winner skipped — entrant pool is empty.", "GiveawayViewModel", LogLevel.System);
            ClearDrawInFlight();
            return;
        }

        // Odds come from the service's weighted pool (subscriber bonus factor
        // included) — never re-derived here from raw ticket counts.
        int odds = result.Value.OddsOneIn;

        _ui.Post(() =>
        {
            if (_disposed) return;
            _drawInFlight = false;
            Raise(nameof(CanDraw));
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

    private void ClearDrawInFlight()
        => _ui.Post(() =>
        {
            _drawInFlight = false;
            if (!_disposed) Raise(nameof(CanDraw));
        });

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
