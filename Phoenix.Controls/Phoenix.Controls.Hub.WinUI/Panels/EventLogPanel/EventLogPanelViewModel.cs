using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.EventLogPanel;

/// <summary>
/// VM for the EventLog tail panel (audit B43). Polls the SQLite
/// <c>EventLog</c> table at 2 Hz and keeps the panel's
/// <see cref="VisibleRows"/> collection synchronised with the newest
/// <see cref="MaxRows"/> entries, newest-first.
///
/// Why polling rather than event subscription: <see cref="DB.LogEventAsync"/>
/// is the single writer surface today and exposes no <c>EntryAdded</c> event
/// — wiring one would mean adding a multicast event + plumbing through
/// every call site. Polling at 2 Hz against an indexed <c>ORDER BY rowid
/// DESC LIMIT N</c> query is cheap (the <c>idx_eventlog_ts</c> covers the
/// scan path) and stays robust against external writers (Architect's
/// Databank tab also INSERTs into EventLog directly). If a real event
/// surface lands later, swap the timer body for a subscription — the
/// <see cref="RefreshAsync"/> entry point already merges new rows by RowId
/// so the two paths can coexist briefly during migration.
///
/// Pop-out friendly: each VM owns its own timer + buffer so multiple
/// pop-outs of the same panel run independent polling without trampling
/// shared state. The timer disposes on <see cref="Dispose"/>.
/// </summary>
public sealed class EventLogPanelViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 250;
    private const int PollIntervalMs = 500;

    private static readonly IReadOnlyList<string> EventLogColumns = new[]
    {
        "Timestamp", "EventSource", "EventType", "User", "Payload",
    };

    private readonly UiDispatcherPump _ui;
    private readonly DispatcherTimer _pollTimer;
    private readonly object _rowIdLock = new();
    // Highest rowid we've already merged. Polling reuses this as a
    // short-circuit: when the latest rowid in the DB matches what we
    // already have, the panel is up to date and we skip the row read.
    private long _highWaterRowId = -1;
    private bool _refreshInFlight;
    private bool _disposed;
    private string _searchText = string.Empty;

    public EventLogPanelViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);

        // DispatcherTimer fires on the UI thread by construction, so the
        // Tick handler can mutate VisibleRows directly. The actual DB read
        // is dispatched via Task.Run so the 2 Hz tick never blocks the
        // pump while the SqliteCommand is in flight.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _pollTimer.Tick += OnPollTick;

        // Prime VisibleRows from the existing DB content before the first
        // tick so the panel renders the initial 250-row tail immediately.
        // Observe the priming refresh's faults too.
        _ = RefreshAsync();
        _pollTimer.Start();
    }

    public ObservableCollection<EventLogRowVm> VisibleRows { get; } = new();

    /// <summary>
    /// Substring filter over <c>Source</c>, <c>EventType</c>, <c>User</c>,
    /// and <c>PayloadSummary</c>. Empty string disables the filter.
    /// Rebuilds <see cref="VisibleRows"/> from the in-memory buffer on
    /// every change.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_searchText, next, StringComparison.Ordinal)) return;
            _searchText = next;
            RebuildVisibleRowsFromBuffer();
            Raise(nameof(SearchText));
        }
    }

    /// <summary>
    /// Backing buffer — every loaded row, newest-first, capped at MaxRows.
    /// VisibleRows is the search-filtered projection of this buffer; the
    /// two are kept in sync by <see cref="RebuildVisibleRowsFromBuffer"/>
    /// whenever the buffer changes or the search predicate changes.
    /// </summary>
    private readonly List<EventLogRowVm> _buffer = new(MaxRows);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= OnPollTick;
        }
        catch { /* shutdown best-effort */ }
    }

    private void OnPollTick(object? sender, object e)
    {
        if (_disposed) return;
        // Coalesce overlapping ticks — if a refresh is still in flight when
        // the next tick fires (slow DB, lock contention), skip rather than
        // queue. Polling is statistically smoothing; one missed tick at
        // 2 Hz is invisible to the user.
        //
        // The check-then-act on _refreshInFlight
        // straddled the UI thread (this tick) and the background RefreshAsync
        // continuation, unsynchronized. Guard the compare-and-set under
        // _rowIdLock so exactly one refresh runs; RefreshAsync clears the flag
        // under the same lock in its finally. The lock is held only for the
        // flag flip — never across the await.
        lock (_rowIdLock)
        {
            if (_refreshInFlight) return;
            _refreshInFlight = true;
        }

        // Observe the fire-and-forget refresh: an
        // unhandled fault would otherwise be swallowed and the panel could
        // silently stop tailing. _refreshInFlight is already set above, so call
        // the inner body that assumes ownership and resets the flag.
        _ = RunRefreshGuardedAsync();
    }

    // Wraps the priming refresh + tick refresh so
    // faults are logged instead of becoming unobserved task exceptions.
    private async Task RunRefreshGuardedAsync()
    {
        try
        {
            await RefreshCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("EventLogPanel", "RefreshAsync faulted", ex);
        }
    }

    /// <summary>
    /// Read the newest <see cref="MaxRows"/> rows from EventLog and
    /// reconcile against the in-memory buffer. Cheap-out path: if the
    /// max rowid in the table equals what we last saw, we already have
    /// the tail and only need to skip the read. Otherwise we re-pull
    /// MaxRows (smaller than the panel's natural buffer turnover) and
    /// replace the buffer wholesale — keeps the merge logic trivial and
    /// is O(MaxRows) per refresh, which is negligible.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;

        // Guarded compare-and-set on the in-flight
        // flag (UI-thread ticks and external callers race here). Acquire the
        // gate only for the flag flip; the async body runs unlocked and clears
        // the flag under the same lock in its finally.
        lock (_rowIdLock)
        {
            if (_refreshInFlight) return;
            _refreshInFlight = true;
        }

        try
        {
            await RefreshCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("EventLogPanel", "RefreshAsync faulted", ex);
        }
    }

    /// <summary>
    /// Core refresh body. Assumes the caller has
    /// already won the <see cref="_refreshInFlight"/> gate under
    /// <see cref="_rowIdLock"/>; this method owns clearing it in its finally.
    /// All <see cref="_highWaterRowId"/> reads/writes are taken under the lock
    /// (never across an await).
    /// </summary>
    private async Task RefreshCoreAsync()
    {
        try
        {
            if (_disposed) return;
            var db = DB.Instance;
            if (!db.IsHealthy) return;

            // Fast-path: peek the max rowid via the same indexed access
            // path that GetRowsWithRowIdAsync uses. If it hasn't changed
            // since our last read, skip the full pull.
            long latestRowId = await ReadMaxEventLogRowIdAsync(db).ConfigureAwait(false);
            long highWater;
            lock (_rowIdLock) { highWater = _highWaterRowId; }
            if (latestRowId == highWater && latestRowId >= 0) return;

            // Pull the newest MaxRows; column-order MUST match EventLogColumns.
            // The user-visible columns include Timestamp because we want to
            // surface it explicitly (rather than reading just rowid) — the
            // Databank-style projection already includes every requested
            // column.
            List<(long RowId, string?[] Cells)> rows;
            try
            {
                rows = await db.GetRowsWithRowIdAsync(
                    tableName: "EventLog",
                    columnsInDeclarationOrder: EventLogColumns,
                    maxRows: MaxRows,
                    offset: 0,
                    orderByColumn: "rowid",
                    orderDescending: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("EventLogPanel", "GetRowsWithRowIdAsync failed", ex);
                return;
            }

            // Project the raw cell tuples into row VMs on the pump thread,
            // then hop to the UI thread to swap the buffer + rebuild
            // VisibleRows. The projection itself is allocation-light and
            // pure, so it's safe off-thread.
            var projected = new List<EventLogRowVm>(rows.Count);
            foreach (var (rowId, cells) in rows)
            {
                if (cells.Length < EventLogColumns.Count) continue;
                projected.Add(new EventLogRowVm(
                    rowId,
                    ParseTimestamp(cells[0]),
                    cells[1] ?? string.Empty,
                    cells[2] ?? string.Empty,
                    cells[3] ?? string.Empty,
                    cells[4] ?? string.Empty));
            }

            _ui.Post(() =>
            {
                if (_disposed) return;
                _buffer.Clear();
                _buffer.AddRange(projected);
                // Guard the high-water write; it is
                // read off-thread in the fast-path peek above.
                long newHighWater = projected.Count > 0 ? projected[0].RowId : -1;
                lock (_rowIdLock) { _highWaterRowId = newHighWater; }
                RebuildVisibleRowsFromBuffer();
            });
        }
        finally
        {
            // Release the in-flight gate under the
            // same lock that guards the compare-and-set entry.
            lock (_rowIdLock) { _refreshInFlight = false; }
        }
    }

    /// <summary>
    /// Bare <c>SELECT max(rowid) FROM EventLog</c> peek. Returns -1 when
    /// the table is empty or the query fails — caller treats that as
    /// "no rows" and skips the pull.
    /// </summary>
    private static async Task<long> ReadMaxEventLogRowIdAsync(DB db)
    {
        // We don't have a dedicated peek API on DB.cs; the simplest
        // honest path is to issue a 1-row pull through the existing
        // GetRowsWithRowIdAsync surface and read its rowid. Cost: one
        // index seek under the shared connection lock. Avoids adding
        // a parallel SQLite call path outside DB.cs (per task brief).
        try
        {
            var top = await db.GetRowsWithRowIdAsync(
                tableName: "EventLog",
                columnsInDeclarationOrder: EventLogColumns,
                maxRows: 1,
                offset: 0,
                orderByColumn: "rowid",
                orderDescending: true).ConfigureAwait(false);
            return top.Count > 0 ? top[0].RowId : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// SQLite returns DATETIME columns as a string in the local format
    /// "yyyy-MM-dd HH:mm:ss". Parse defensively — a bogus value falls
    /// back to <see cref="DateTime.UtcNow"/> so the row still renders
    /// (the source-of-truth value is preserved in <c>Payload</c> /
    /// <c>EventType</c> regardless of timestamp parse outcome).
    /// </summary>
    private static DateTime ParseTimestamp(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return DateTime.UtcNow;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }
        return DateTime.UtcNow;
    }

    private void RebuildVisibleRowsFromBuffer()
    {
        VisibleRows.Clear();
        if (_searchText.Length == 0)
        {
            foreach (var row in _buffer) VisibleRows.Add(row);
            return;
        }
        foreach (var row in _buffer)
        {
            if (RowMatchesSearch(row)) VisibleRows.Add(row);
        }
    }

    private bool RowMatchesSearch(EventLogRowVm row)
    {
        return Contains(row.Source) || Contains(row.EventType) || Contains(row.User) || Contains(row.PayloadSummary);

        bool Contains(string s) =>
            !string.IsNullOrEmpty(s) && s.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
