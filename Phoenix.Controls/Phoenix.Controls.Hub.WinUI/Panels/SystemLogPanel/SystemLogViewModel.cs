using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.SystemLogPanel;

public sealed class SystemLogViewModel : ObservableObject, IDisposable
{
    // Default cap used when AppConfig.SystemLogMaxRows is missing or
    // non-positive. The view is virtualised (ListView with the default
    // VirtualizingStackPanel), so a 10 000-entry buffer costs essentially
    // nothing — only on-screen rows are materialised. Larger than the
    // upstream GlobalLogger ring (2000) on purpose: the panel keeps its
    // own history so the user can scroll back further than the source's
    // Snapshot replay window.
    private const int DefaultMaxRows = 10_000;
    private readonly int _maxRows;

    // QC11-03 — overflow trim batch size. When _buffer / VisibleRows blow
    // past _maxRows we trim this many entries at the head in a single
    // operation rather than per-entry. _buffer.RemoveRange is O(n) for the
    // shift but called once per OverflowTrimBatch entries (instead of N
    // RemoveAt(0) calls, each O(n)). For VisibleRows we walk through a
    // collection-Reset (clear+refill) when more than this many would be
    // removed, so the ListView gets a single CollectionChanged.Reset
    // instead of an arrow-storm of Remove events that virtualisation has
    // to chew through one by one. Both paths amortise the O(n) cost down
    // from per-entry to per-batch.
    private const int OverflowTrimBatch = 128;

    private readonly ISystemLogSource _source;
    // C1 (2026-05-14): per-VM dispatcher pump, ctor-injected by PanelFactory.
    private readonly UiDispatcherPump _ui;
    // _buffer holds every level-filtered row that arrived from the source.
    // VisibleRows is the search-filtered subset rendered in the panel — the
    // search predicate runs at the VM (not the source) because the source
    // filter is shared across consumers; a per-keystroke source filter would
    // corrupt Snapshot() for other observers (TODO 2026-05-07 round 2 P2).
    private readonly List<SystemLogRowVm> _buffer = new();

    // Perf-review C3 (2026-05-14): dispatcher-hop coalescing. EntryAdded
    // fires synchronously on the producer thread; the previous code did one
    // dq.TryEnqueue per entry, which under a 1000-logs/sec burst (script
    // engine debug + bus log fan-out) queued 1000 dispatcher items/sec. We
    // now accumulate pending rows on the producer side and enqueue a single
    // FlushPending callback per dispatcher tick — subsequent arrivals join
    // the in-flight batch instead of scheduling a new hop.
    private readonly object _pendingLock = new();
    private List<SystemLogRowVm> _pendingAdds = new();
    private bool _flushScheduled;
    // DBG defaults to off to match the chip default — the SystemLog used to
    // flood the panel with LogicExecution-tier per-script-run lines before
    // those got demoted to Debug. Power users toggle the chip on for
    // verbose tracing.
    private SystemLogLevel _filterMask = SystemLogLevel.Info | SystemLogLevel.Warn | SystemLogLevel.Error;
    private string _searchText = string.Empty;
    // C7 (audit 2026-05-24) — exact-match source filter wired through the
    // row right-click "Filter to source: <Source>" menu item (B6). Null /
    // empty means "no source filter" — every row that passes the level +
    // search predicates surfaces. Persisted to AppConfig.SystemLogSourceFilter.
    private string? _sourceFilter;
    private bool _disposed;

    public SystemLogViewModel(ISystemLogSource source, DispatcherQueue? dispatcher)
    {
        _source = source;
        _ui = new UiDispatcherPump(dispatcher);
        // Pull the panel buffer cap from AppConfig (defaults to 10000 when
        // missing or non-positive — see DefaultMaxRows). ConfigManager is
        // safe to read at construction: Hub.WinUI's startup loads the
        // config before the workspace view materialises panel VMs.
        int configured = ConfigManager.Current?.SystemLogMaxRows ?? DefaultMaxRows;
        _maxRows = configured > 0 ? configured : DefaultMaxRows;

        // C7 (audit 2026-05-24) — restore persisted filter state. The level
        // chips persist as a list of SystemLogLevel string names; if any
        // names parse cleanly we use that set instead of the default mask.
        // An empty list (or unrecognised contents) leaves the default
        // Info+Warn+Error mask in place so old configs migrate silently.
        // The source filter restore is unconditional — null/empty already
        // means "no filter" so a missing key produces correct behaviour.
        var cfg = ConfigManager.Current;
        if (cfg is not null)
        {
            if (cfg.SystemLogActiveLevels is { Count: > 0 } persistedLevels)
            {
                SystemLogLevel parsed = SystemLogLevel.None;
                foreach (var name in persistedLevels)
                {
                    if (Enum.TryParse<SystemLogLevel>(name, ignoreCase: true, out var lvl))
                        parsed |= lvl;
                }
                if (parsed != SystemLogLevel.None) _filterMask = parsed;
            }
            _sourceFilter = string.IsNullOrEmpty(cfg.SystemLogSourceFilter) ? null : cfg.SystemLogSourceFilter;
        }

        _source.EntryAdded += OnEntryAdded;
        // HUB-UX-D7 (2026-05-14) — fan-out: every subscriber to EntryAdded
        // gets every log row; no primary-subscriber gate.
        // Push the initial filter mask down to the source so it can drop
        // unsubscribed levels before the per-entry roundtrip. The VM also
        // re-checks the mask on ingress (defence-in-depth) so toggling a
        // chip rebuilds VisibleRows from history without a fresh round of
        // upstream notifications.
        _source.SetLevelFilter(_filterMask);
        foreach (var e in _source.Snapshot())
        {
            var row = new SystemLogRowVm(e);
            _buffer.Add(row);
            if (RowMatchesAll(row)) VisibleRows.Add(row);
        }
        // QC11-03 — single batched trim post-snapshot. A snapshot replay
        // larger than _maxRows is the only realistic way to overflow at
        // ctor time, and even then we'd rather pay one RemoveRange than N
        // RemoveAt(0)s. _buffer is the only collection that can actually
        // be over here — VisibleRows is filtered, never larger.
        TrimBufferToCap();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.EntryAdded -= OnEntryAdded;
    }

    public ObservableCollection<SystemLogRowVm> VisibleRows { get; } = new();

    /// <summary>
    /// Header search filter — substring match (case-insensitive) over Source
    /// + Message + LevelText. Updates VisibleRows in place. Empty string
    /// disables the filter.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_searchText, next, StringComparison.Ordinal)) return;
            _searchText = next;
            RebuildVisibleRows();
            Raise(nameof(SearchText));
        }
    }

    /// <summary>
    /// C7 (audit 2026-05-24) — exact-match source filter. Setting persists
    /// the value to AppConfig.SystemLogSourceFilter (debounced via
    /// ConfigManager.Save) and rebuilds VisibleRows so the panel reflects
    /// the new predicate immediately. Null / empty clears the filter.
    /// </summary>
    public string? SourceFilter
    {
        get => _sourceFilter;
        set
        {
            string? next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_sourceFilter, next, StringComparison.Ordinal)) return;
            _sourceFilter = next;
            RebuildVisibleRows();
            PersistFilterState();
            Raise(nameof(SourceFilter));
            Raise(nameof(HasSourceFilter));
        }
    }

    /// <summary>True when a source filter is active — surfaced for an
    /// optional "Clear source filter" affordance in the panel chrome.</summary>
    public bool HasSourceFilter => !string.IsNullOrEmpty(_sourceFilter);

    /// <summary>
    /// B2 (audit 2026-05-24) — clears the panel's local buffer. The upstream
    /// GlobalLogger ring is NOT touched (its public API has no Clear() and
    /// rewriting that is out of scope); per-process restart is still required
    /// to drop the upstream history. Operators get an immediate "empty panel"
    /// affordance which is what the audit asks for; new entries arriving
    /// after the clear fan back in via the normal EntryAdded subscription.
    /// </summary>
    public void ClearBuffer()
    {
        _buffer.Clear();
        VisibleRows.Clear();
    }

    public bool ShowDebug
    {
        get => _filterMask.HasFlag(SystemLogLevel.Debug);
        set => SetFlag(SystemLogLevel.Debug, value);
    }
    public bool ShowInfo
    {
        get => _filterMask.HasFlag(SystemLogLevel.Info);
        set => SetFlag(SystemLogLevel.Info, value);
    }
    public bool ShowWarn
    {
        get => _filterMask.HasFlag(SystemLogLevel.Warn);
        set => SetFlag(SystemLogLevel.Warn, value);
    }
    public bool ShowError
    {
        get => _filterMask.HasFlag(SystemLogLevel.Error);
        set => SetFlag(SystemLogLevel.Error, value);
    }

    private void SetFlag(SystemLogLevel flag, bool on)
    {
        var next = on ? (_filterMask | flag) : (_filterMask & ~flag);
        if (next == _filterMask) return;
        _filterMask = next;
        _source.SetLevelFilter(_filterMask);
        // Rebuild VisibleRows from _buffer so toggling the chip immediately
        // reflects the new level filter — without this, entries already in
        // the buffer stayed visible after the user unticked their level
        // until the buffer rolled over.
        RebuildVisibleRows();
        // C7 (audit 2026-05-24) — persist chip state so a relaunch lands at
        // the same set of visible levels. The save is fire-and-forget; a
        // disk failure surfaces as a log entry, not a thrown exception that
        // would tear the chip handler.
        PersistFilterState();
        Raise(nameof(ShowDebug));
        Raise(nameof(ShowInfo));
        Raise(nameof(ShowWarn));
        Raise(nameof(ShowError));
    }

    /// <summary>
    /// C7 (audit 2026-05-24) — writes the active level chip set + source
    /// filter to AppConfig and asks ConfigManager to persist. Called on
    /// every level toggle and on every SourceFilter setter; the save
    /// itself is cheap (a single JSON write) but we still swallow IO
    /// failures so a transient disk-full doesn't fault the UI handler.
    /// </summary>
    private void PersistFilterState()
    {
        try
        {
            var cfg = ConfigManager.Current;
            if (cfg is null) return;

            var levels = new List<string>(4);
            if (_filterMask.HasFlag(SystemLogLevel.Debug)) levels.Add(nameof(SystemLogLevel.Debug));
            if (_filterMask.HasFlag(SystemLogLevel.Info))  levels.Add(nameof(SystemLogLevel.Info));
            if (_filterMask.HasFlag(SystemLogLevel.Warn))  levels.Add(nameof(SystemLogLevel.Warn));
            if (_filterMask.HasFlag(SystemLogLevel.Error)) levels.Add(nameof(SystemLogLevel.Error));
            cfg.SystemLogActiveLevels = levels;
            cfg.SystemLogSourceFilter = _sourceFilter;
            ConfigManager.Save(Paths.AppConfigJson);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "SystemLogViewModel", "PersistFilterState failed", ex);
        }
    }

    private void OnEntryAdded(object? sender, SystemLogEntry entry)
    {
        // HUB-UX-D7 (2026-05-14) — fan-out: this VM is one of N subscribers.
        var row = new SystemLogRowVm(entry);

        // Perf-review C3: enqueue once per "burst gap", then drain everything
        // that piled up between schedule and execution. A 1000-log/sec burst
        // typically produces ~1-2 dispatcher hops per frame instead of 1000.
        bool needsSchedule;
        lock (_pendingLock)
        {
            _pendingAdds.Add(row);
            needsSchedule = !_flushScheduled;
            _flushScheduled = true;
        }
        if (!needsSchedule) return;

        // Perf-review H1: HasThreadAccess fast-path baked into Post.
        _ui.Post(FlushPending);
    }

    private void FlushPending()
    {
        List<SystemLogRowVm> batch;
        lock (_pendingLock)
        {
            batch = _pendingAdds;
            _pendingAdds = new List<SystemLogRowVm>();
            _flushScheduled = false;
        }
        // QC11-03 — append every row first, then trim once at the tail of
        // the batch. The previous code did a per-entry RemoveAt(0) on both
        // _buffer and VisibleRows; each call is O(n) (List shifts every
        // remaining element down, ObservableCollection raises one Remove
        // per call), so a 1000-log/sec burst at full cap produced 2000
        // O(n) operations per second. The new path lets one RemoveRange
        // (or a single Reset for VisibleRows) cover the whole burst's
        // worth of overflow.
        foreach (var row in batch)
        {
            _buffer.Add(row);
            if (RowMatchesAll(row)) VisibleRows.Add(row);
        }
        TrimBufferToCap();
        TrimVisibleRowsToCap();
    }

    /// <summary>
    /// Drop head entries from <see cref="_buffer"/> in a single
    /// <see cref="List{T}.RemoveRange"/> call once it has overflown the
    /// cap. We trim slightly past the cap (in <see cref="OverflowTrimBatch"/>
    /// strides) so that bursty ingest doesn't re-enter the trim path on
    /// every row.
    /// </summary>
    private void TrimBufferToCap()
    {
        int over = _buffer.Count - _maxRows;
        if (over <= 0) return;
        // Round up to the batch granularity so a stream of small overflows
        // amortises into one shift rather than many.
        int trim = ((over + OverflowTrimBatch - 1) / OverflowTrimBatch) * OverflowTrimBatch;
        if (trim > _buffer.Count) trim = _buffer.Count;
        _buffer.RemoveRange(0, trim);
    }

    /// <summary>
    /// Cap <see cref="VisibleRows"/> head removals. ObservableCollection
    /// has no RemoveRange, so a small overflow is handled with up to one
    /// batch's worth of RemoveAt(0); a large overflow (e.g. a chip toggle
    /// invalidated the level filter and re-added a giant slice) is handled
    /// with a single Reset + refill, which fires one CollectionChanged
    /// instead of N. The Reset path matches the existing RebuildVisibleRows
    /// contract so the View's OnVisibleRowsChanged keeps working.
    /// </summary>
    private void TrimVisibleRowsToCap()
    {
        int over = VisibleRows.Count - _maxRows;
        if (over <= 0) return;

        if (over > OverflowTrimBatch)
        {
            // Bulk trim — Reset + refill from _buffer is cheaper than N
            // RemoveAt(0) calls on an ObservableCollection (each fires a
            // CollectionChanged.Remove that the ListView has to process).
            RebuildVisibleRows();
            return;
        }

        for (int i = 0; i < over; i++)
        {
            VisibleRows.RemoveAt(0);
        }
    }

    private void RebuildVisibleRows()
    {
        VisibleRows.Clear();
        foreach (var row in _buffer)
        {
            if (RowMatchesAll(row)) VisibleRows.Add(row);
        }
    }

    /// <summary>
    /// QC11-09/10 — runtime theme-swap hook. Drops the static fallback
    /// brush cache and re-resolves LevelBrush / MessageBrush on every
    /// buffered row so x:Bind OneWay picks up the new theme without
    /// rebuilding the row VMs. Hub doesn't ship a live theme switcher
    /// today, so this is a forward-looking entry-point — call from the
    /// future App.RequestedTheme listener / settings dialog "apply".
    /// </summary>
    public void RefreshBrushes()
    {
        SystemLogRowVm.InvalidateBrushCache();
        foreach (var row in _buffer) row.RefreshBrushes();
    }

    /// <summary>
    /// Combined level + search filter. Used at ingress (OnEntryAdded),
    /// snapshot replay (ctor), and rebuild (chip toggle / search change).
    /// Defence-in-depth — even if the source forwards an entry below the
    /// current level threshold, the VM drops it here.
    /// </summary>
    private bool RowMatchesAll(SystemLogRowVm row)
    {
        if ((_filterMask & row.Level) == 0) return false;
        // C7 (audit 2026-05-24) — exact, case-insensitive source filter.
        // Applied alongside the level mask and search predicate so all
        // three contributions compose without re-ordering at call sites.
        if (_sourceFilter is { Length: > 0 }
            && !string.Equals(row.Source, _sourceFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        return RowMatchesSearch(row);
    }

    private bool RowMatchesSearch(SystemLogRowVm row)
    {
        if (_searchText.Length == 0) return true;
        return (row.Source?.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
            || (row.Message?.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
            || (row.LevelText?.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
