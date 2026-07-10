using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.LiveFeedPanel;

public sealed class LiveFeedViewModel : ObservableObject, IDisposable
{
    // Cap on Rows. ItemsRepeater virtualises but ObservableCollection grows
    // unbounded, so over a long stream session memory + GC pressure climb
    // until the panel stutters. The dequeue-oldest-on-cap trim mirrors
    // GlobalLogger's 2000-entry ring; raise via config if needed.
    private const int MaxRows = 5000;

    // Amortised at-cap eviction. RemoveAt(0) on a full 5000-row
    // List/ObservableCollection is O(n) per event (element shift, plus a
    // CollectionChanged.Remove the ItemsRepeater has to re-index for), and
    // at sustained event load the buffers sit at cap permanently. Instead
    // both collections may overshoot MaxRows by up to this many rows before
    // one pass cuts them back to the cap — a single RemoveRange shift for
    // _buffer, a single Reset + refill for Rows. Row count between trims
    // varies within [MaxRows, MaxRows + OverflowTrimBatch]; still bounded.
    // Mirrors SystemLogViewModel.OverflowTrimBatch.
    private const int OverflowTrimBatch = 128;

    private readonly ILiveFeedSource _source;
    // per-VM dispatcher pump, ctor-injected by PanelFactory.
    private readonly UiDispatcherPump _ui;
    // _buffer holds every row the VM has ingested, unfiltered.
    // Rows is the filter-visible subset rendered in the panel. On chip
    // toggle RebuildVisibleRows() clears Rows and re-applies the predicate
    // against _buffer — without this, prior-filter rows stayed on screen
    // after the user changed chips. Mirrors SystemLogViewModel's buffer /
    // VisibleRows split. The VM does its own filtering rather than relying
    // on ILiveFeedSource.SetFilter (which is a shared slot across pop-outs
    // and would corrupt the buffer for other observers).
    private readonly List<LiveFeedRowVm> _buffer = new();
    private LiveFeedFilter _selectedFilter = LiveFeedFilter.All;
    // Errors chip is selectable independently of
    // the LiveFeedFilter enum because the enum lives in Records.cs. When
    // _errorsChipSelected is true the filter predicate returns IsError rows
    // only; the existing chip selectors clear the flag because they're meant
    // to be exclusive.
    private bool _errorsChipSelected;
    // per-user filter wired from the row's right-
    // click "Filter to user: <Username>" menu. Empty string disables the
    // filter; otherwise rows pass only when Who matches (case-insensitive,
    // exact). Persistence is intentionally session-only — operators set
    // this to narrow during incident response and expect a fresh feed on
    // restart.
    private string? _userFilter;
    // handler for GlobalLogger.OnLogEntry. Subscribed alongside the
    // existing source.EntryAdded so the panel can surface CriticalError
    // entries inline with stream activity. Held in a field so the
    // -= in Dispose can unsubscribe deterministically.
    private readonly Action<Log> _onLog;
    private bool _disposed;

    // dispatcher-hop coalescing. EntryAdded
    // fires once per bus message; raid bursts can land 200+/sec. Accumulate
    // pending rows on the producer side and enqueue a single FlushPending
    // callback per dispatcher tick — subsequent arrivals join the in-flight
    // batch instead of scheduling a new hop.
    private readonly object _pendingLock = new();
    private List<LiveFeedRowVm> _pendingAdds = new();
    private bool _flushScheduled;

    public LiveFeedViewModel(ILiveFeedSource source, DispatcherQueue? dispatcher)
    {
        _source = source;
        _ui = new UiDispatcherPump(dispatcher);

        // restore persisted chip state. Strings
        // come from AppConfig.LiveFeedActiveChips and map to either the
        // LiveFeedFilter enum or the "Errors" sentinel. An empty /
        // missing list leaves the default "All" selection in place so old
        // configs migrate silently.
        var cfg = ConfigManager.Current;
        if (cfg?.LiveFeedActiveChips is { Count: > 0 } persistedChips)
        {
            foreach (var name in persistedChips)
            {
                if (string.Equals(name, "Errors", StringComparison.OrdinalIgnoreCase))
                {
                    _errorsChipSelected = true;
                    _selectedFilter = LiveFeedFilter.All;
                    break;
                }
                if (Enum.TryParse<LiveFeedFilter>(name, ignoreCase: true, out var f))
                {
                    _selectedFilter = f;
                    _errorsChipSelected = false;
                    break;
                }
            }
        }

        // fan-out: every subscriber receives every
        // row. Adding to EntryAdded IS the subscribe; the matching -= in
        // Dispose IS the unsubscribe. No primary-subscriber gate.
        _source.EntryAdded += OnEntryAdded;

        // Subscribe to GlobalLogger.OnLogEntry so
        // CriticalError entries land in the LiveFeed alongside stream
        // events. We do this here at the VM (not in LiveFeedSource —
        // that file is in Services/) so each pop-out gets its own
        // subscription that survives the original panel being torn down.
        _onLog = entry =>
        {
            if (entry.Level != LogLevel.CriticalError) return;
            string detail = entry.Message ?? string.Empty;
            // LiveFeedKind value here is meaningless for IsError rows —
            // the renderer keys off IsError, not Kind, for the icon and
            // brush. Visual is the default ClassifyStreamEvent fall-back
            // so it's the least confusing placeholder.
            var feedEntry = new LiveFeedEntry(
                Timestamp: ToUtcOffset(entry.Timestamp),
                Kind:      LiveFeedKind.Visual,
                Who:       string.IsNullOrEmpty(entry.Source) ? "Hub" : entry.Source!,
                Detail:    detail);
            var errorRow = new LiveFeedRowVm(feedEntry, isError: true);
            _ui.Post(() => IngestErrorRow(errorRow));
        };
        GlobalLogger.OnLogEntry += _onLog;

        foreach (var entry in _source.Snapshot())
        {
            var row = new LiveFeedRowVm(entry);
            _buffer.Add(row);
            if (RowMatchesFilter(row)) Rows.Add(row);
        }
        TrimToCaps();
    }

    /// <summary>
    /// Appends an error row (sourced from GlobalLogger.CriticalError)
    /// into the panel's buffer + visible rows under the same trim policy
    /// as the normal source-ingress path. Marshalled onto the UI thread
    /// by the caller via <c>_ui.Post</c>.
    /// </summary>
    private void IngestErrorRow(LiveFeedRowVm row)
    {
        bool wasEmpty = _buffer.Count == 0;
        _buffer.Add(row);
        if (RowMatchesFilter(row)) Rows.Add(row);
        TrimToCaps();
        if (wasEmpty && _buffer.Count > 0)
        {
            Raise(nameof(IsEmpty));
            Raise(nameof(EmptyStateVisibility));
        }
    }

    private static DateTimeOffset ToUtcOffset(DateTime dt) =>
        dt.Kind switch
        {
            DateTimeKind.Utc        => new DateTimeOffset(dt, TimeSpan.Zero),
            DateTimeKind.Local      => new DateTimeOffset(dt),
            _                       => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)),
        };

    /// <summary>
    /// True when no rows have been ingested AND the user hasn't narrowed
    /// the feed with a filter chip — the "waiting for activity" first-launch
    /// surface. An active filter that excludes everything intentionally
    /// hides the placeholder so the operator can see that they filtered
    /// the feed (rather than the feed having gone dark).
    /// </summary>
    public bool IsEmpty => _buffer.Count == 0 && _selectedFilter == LiveFeedFilter.All;

    /// <summary>
    /// Empty-state placeholder Visibility shim — WinUI 3 x:Bind doesn't
    /// implicitly convert bool to Visibility, and a dedicated converter
    /// type is overkill for one binding. Re-raised alongside
    /// <see cref="IsEmpty"/> on every buffer change.
    /// </summary>
    public Visibility EmptyStateVisibility =>
        IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.EntryAdded -= OnEntryAdded;
        GlobalLogger.OnLogEntry -= _onLog;
    }

    /// <summary>
    /// Clear the panel's local buffer + visible
    /// rows. Mirrors SystemLogViewModel.ClearBuffer; upstream sources
    /// keep accumulating and ingress re-populates naturally.
    /// </summary>
    public void ClearBuffer()
    {
        _buffer.Clear();
        Rows.Clear();
        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyStateVisibility));
    }

    /// <summary>
    /// Per-user filter wired from the row's
    /// right-click "Filter to user" menu. Setting rebuilds Rows so the
    /// new predicate lands immediately. Persistence is session-only.
    /// </summary>
    public string? UserFilter
    {
        get => _userFilter;
        set
        {
            string? next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_userFilter, next, StringComparison.Ordinal)) return;
            _userFilter = next;
            RebuildVisibleRows();
            Raise(nameof(UserFilter));
            Raise(nameof(HasUserFilter));
            Raise(nameof(IsEmpty));
            Raise(nameof(EmptyStateVisibility));
        }
    }

    public bool HasUserFilter => !string.IsNullOrEmpty(_userFilter);

    /// <summary>
    /// "Errors" chip selector. Selects the chip
    /// and mutates the buffer-aware filter so only error rows surface.
    /// Exclusive with the LiveFeedFilter chip set per the All / Subs /
    /// Raids / Visual / Redeem / Follow precedent.
    /// </summary>
    public void SelectErrors()
    {
        if (_errorsChipSelected) return;
        _errorsChipSelected = true;
        _selectedFilter = LiveFeedFilter.All; // reset enum to neutral
        RebuildVisibleRows();
        PersistChipState();
        Raise(nameof(IsAllSelected));
        Raise(nameof(IsSubsSelected));
        Raise(nameof(IsRaidsSelected));
        Raise(nameof(IsVisualSelected));
        Raise(nameof(IsRedeemSelected));
        Raise(nameof(IsFollowSelected));
        Raise(nameof(IsErrorsSelected));
        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyStateVisibility));
    }

    /// <summary>True when the "Errors" chip is the active filter.</summary>
    public bool IsErrorsSelected => _errorsChipSelected;

    public ObservableCollection<LiveFeedRowVm> Rows { get; } = new();

    /// <summary>
    /// Runtime theme-swap hook. Drops the static brush caches and
    /// re-resolves IconBrush on every buffered row so x:Bind OneWay picks up
    /// the new theme without rebuilding the row VMs. Iterates _buffer (the
    /// full ingested set), not Rows, so filtered-out rows also refresh —
    /// switching back to a filter that re-shows them doesn't reveal stale
    /// brushes.
    /// </summary>
    public void RefreshBrushes()
    {
        LiveFeedRowVm.InvalidateBrushCache();
        foreach (var row in _buffer) row.RefreshBrushes();
    }

    public LiveFeedFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            // Picking any enum-backed chip deselects Errors. We
            // mutate the flag first so the chip state's setter sees the
            // post-toggle baseline.
            bool errorsChanged = _errorsChipSelected;
            _errorsChipSelected = false;
            if (Set(ref _selectedFilter, value) || errorsChanged)
            {
                // On filter change, rebuild Rows from _buffer so
                // stale rows from the previous filter drop off-screen.
                RebuildVisibleRows();
                // persist the active chip so the panel restores its
                // filter set on restart.
                PersistChipState();
                Raise(nameof(IsAllSelected));
                Raise(nameof(IsSubsSelected));
                Raise(nameof(IsRaidsSelected));
                Raise(nameof(IsVisualSelected));
                Raise(nameof(IsRedeemSelected));
                Raise(nameof(IsFollowSelected));
                Raise(nameof(IsErrorsSelected));
                // Empty-state surface is filter-aware.
                Raise(nameof(IsEmpty));
                Raise(nameof(EmptyStateVisibility));
            }
        }
    }

    /// <summary>
    /// Write the active chip to AppConfig and
    /// persist. We store a single chip name; the panel is single-select
    /// today and an empty list deserialises to "All" by default in the
    /// ctor restore path.
    /// </summary>
    private void PersistChipState()
    {
        try
        {
            var cfg = ConfigManager.Current;
            if (cfg is null) return;
            string name = _errorsChipSelected
                ? "Errors"
                : _selectedFilter.ToString();
            cfg.LiveFeedActiveChips = new List<string> { name };
            // Deferred save — the chip toggle runs on the UI thread and a
            // synchronous config write (DPAPI wrap + File.Replace) can stall
            // for hundreds of ms on AV/OneDrive-backed %AppData%. Shutdown
            // paths still use the synchronous Save per ConfigManager's doc.
            ConfigManager.SaveDeferred(Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LiveFeedViewModel", "PersistChipState failed", ex);
        }
    }

    // Each chip is "selected" only when the Errors chip is OFF — Errors is
    // mutually exclusive with the LiveFeedFilter chip set.
    public bool IsAllSelected    => !_errorsChipSelected && _selectedFilter == LiveFeedFilter.All;
    public bool IsSubsSelected   => !_errorsChipSelected && _selectedFilter == LiveFeedFilter.Subs;
    public bool IsRaidsSelected  => !_errorsChipSelected && _selectedFilter == LiveFeedFilter.Raids;
    public bool IsVisualSelected => !_errorsChipSelected && _selectedFilter == LiveFeedFilter.Visual;
    // Redeem + Follow chip selectors. Pairs with the Records.cs
    // enum values — every LiveFeedKind the source can emit now has a chip
    // in the panel header.
    public bool IsRedeemSelected => !_errorsChipSelected && _selectedFilter == LiveFeedFilter.Redeem;
    public bool IsFollowSelected => !_errorsChipSelected && _selectedFilter == LiveFeedFilter.Follow;

    public void SelectAll()    => SelectedFilter = LiveFeedFilter.All;
    public void SelectSubs()   => SelectedFilter = LiveFeedFilter.Subs;
    public void SelectRaids()  => SelectedFilter = LiveFeedFilter.Raids;
    public void SelectVisual() => SelectedFilter = LiveFeedFilter.Visual;
    public void SelectRedeem() => SelectedFilter = LiveFeedFilter.Redeem;
    public void SelectFollow() => SelectedFilter = LiveFeedFilter.Follow;

    private void OnEntryAdded(object? sender, LiveFeedEntry entry)
    {
        // fan-out: this VM is one of N subscribers.
        // Each one renders its own copy of the row in its own Window.
        var row = new LiveFeedRowVm(entry);
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

    private void FlushPending()
    {
        List<LiveFeedRowVm> batch;
        lock (_pendingLock)
        {
            batch = _pendingAdds;
            _pendingAdds = new List<LiveFeedRowVm>();
            _flushScheduled = false;
        }
        bool wasEmpty = _buffer.Count == 0;
        foreach (var row in batch)
        {
            _buffer.Add(row);
            if (RowMatchesFilter(row)) Rows.Add(row);
        }
        // Trim oldest once per batch to bound memory under sustained load.
        TrimToCaps();
        if (wasEmpty && _buffer.Count > 0)
        {
            // First row(s) just landed — collapse the empty-state placeholder.
            Raise(nameof(IsEmpty));
            Raise(nameof(EmptyStateVisibility));
        }
    }

    /// <summary>
    /// Drops the oldest rows from <c>_buffer</c> and <see cref="Rows"/> once
    /// either has overshot <see cref="MaxRows"/> by more than
    /// <see cref="OverflowTrimBatch"/>, cutting back to exactly the cap in
    /// one pass. Display order is preserved — only head (oldest) entries go.
    /// </summary>
    private void TrimToCaps()
    {
        int bufferOver = _buffer.Count - MaxRows;
        if (bufferOver > OverflowTrimBatch)
        {
            _buffer.RemoveRange(0, bufferOver);
        }

        int rowsOver = Rows.Count - MaxRows;
        if (rowsOver > OverflowTrimBatch)
        {
            // Head-first per-index removal — NOT Clear+refill: a Reset drops
            // the ListView scroll anchor, yanking a reader who scrolled up
            // off their position on every trim. Per-index head removals stay
            // cheap under UI virtualization and keep the viewport continuous,
            // exactly like the pre-batching per-event eviction.
            for (int i = 0; i < rowsOver; i++) Rows.RemoveAt(0);
        }
    }

    /// <summary>
    /// Rebuilds the visible <see cref="Rows"/> collection from
    /// <c>_buffer</c> under the current <see cref="SelectedFilter"/>.
    /// Mirrors <c>SystemLogViewModel.RebuildVisibleRows</c>.
    /// </summary>
    private void RebuildVisibleRows()
    {
        Rows.Clear();
        foreach (var row in _buffer)
        {
            if (RowMatchesFilter(row)) Rows.Add(row);
        }
    }

    /// <summary>
    /// Predicate keyed on <see cref="SelectedFilter"/>. Centralised so the
    /// ingress path (FlushPending) and the rebuild path agree. Filter
    /// values without a dedicated chip (Chat, Redeem, Follow) still resolve
    /// — they're reachable through the contract enum even if the UI never
    /// surfaces them right now.
    /// </summary>
    private bool RowMatchesFilter(LiveFeedRowVm row)
    {
        // Errors chip surfaces ONLY error rows (and otherwise hides
        // every stream/visual row). The other chips only see non-error
        // rows so a long error tail doesn't drown out the normal feed.
        if (_errorsChipSelected)
        {
            if (!row.IsError) return false;
        }
        else
        {
            if (row.IsError) return false;
            bool kindMatches = _selectedFilter switch
            {
                LiveFeedFilter.All    => true,
                LiveFeedFilter.Chat   => row.Entry.Kind == LiveFeedKind.Chat,
                LiveFeedFilter.Subs   => row.Entry.Kind == LiveFeedKind.Sub,
                LiveFeedFilter.Raids  => row.Entry.Kind == LiveFeedKind.Raid,
                LiveFeedFilter.Visual => row.Entry.Kind == LiveFeedKind.Visual,
                LiveFeedFilter.Redeem => row.Entry.Kind == LiveFeedKind.Redeem,
                LiveFeedFilter.Follow => row.Entry.Kind == LiveFeedKind.Follow,
                _                     => true,
            };
            if (!kindMatches) return false;
        }

        // per-user narrow. Empty filter passes everything; otherwise
        // exact-match (case-insensitive) against the row's Who field.
        if (_userFilter is { Length: > 0 }
            && !string.Equals(row.Who, _userFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }
}
