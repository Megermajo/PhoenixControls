using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Architect.WinUI.Databank.Contracts;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Databank;

// IRelationalSource implementation backed by the running DB
// singleton. Writes route through DB's already-validated identifier guards
// + its two table-protection registries (the write lock on data, the
// app-owned lock on schema) so the Databank tab inherits the same safety
// invariants the script-engine db.* commands enforce.
//
// DB ensures its own lock + WAL setup; we never reach for a
// SqliteConnection directly. The async methods all hop off the UI thread
// because DB's _lock can hold for tens of ms on a contended
// graph save — the view awaits these from a background task.
public sealed class DbRelationalSource : IRelationalSource
{
    private readonly DB _db;

    // Per-table total-row-count cache. Pre-fix
    // GetRowSnapshotAsync issued a full COUNT(*) on every invocation —
    // including page navigation and sort, where the row count cannot have
    // changed. We cache the count keyed by table name and only re-query when
    // the cache misses (first snapshot of a table) or a mutation invalidates
    // it. Mutations (insert / delete / create / drop / column-shape changes)
    // call InvalidateRowCount so the next snapshot re-counts the truth.
    private readonly Dictionary<string, int> _rowCountCache =
        new(System.StringComparer.OrdinalIgnoreCase);
    private readonly object _rowCountCacheLock = new();

    // Per-table column-list cache mirroring _rowCountCache. Pre-fix
    // GetRowSnapshotAsync re-issued PRAGMA table_info on every page
    // navigation and sort through GetColumnsAsync, though the column shape
    // cannot change during a table session. Every column-DDL path (add /
    // drop / rename / change-type) plus create/drop table invalidates, and
    // the browser VM clears the cache on table-selection change so staleness
    // is bounded to one table session. Row counts are deliberately NOT
    // cached beyond _rowCountCache's mutation-invalidated scheme — the
    // COUNT(*) probes double as external-write detection.
    private readonly Dictionary<string, IReadOnlyList<ColumnInfo>> _columnCache =
        new(System.StringComparer.OrdinalIgnoreCase);
    private readonly object _columnCacheLock = new();

    /// <summary>Wrap the live <see cref="DB.Instance"/>.</summary>
    public DbRelationalSource() : this(DB.Instance) { }

    /// <summary>Wrap any DB — used by tests with a fixture-redirected instance.</summary>
    public DbRelationalSource(DB db)
    {
        _db = db;
    }

    public string DisplayPath => _db.DatabasePath ?? string.Empty;

    // ── Row-count cache helpers ─────────────────────────────────────────

    private bool TryGetCachedRowCount(string tableName, out int count)
    {
        lock (_rowCountCacheLock)
            return _rowCountCache.TryGetValue(tableName, out count);
    }

    private void StoreRowCount(string tableName, int count)
    {
        lock (_rowCountCacheLock)
            _rowCountCache[tableName] = count;
    }

    /// <summary>
    /// Drop the cached total-row-count for a
    /// table (or all tables when <paramref name="tableName"/> is null) so
    /// the next <see cref="GetRowSnapshotAsync"/> re-issues COUNT(*). Called
    /// from every mutation path so paging/sort stay cheap while edits stay
    /// truthful.
    /// </summary>
    public void InvalidateRowCount(string? tableName = null)
    {
        lock (_rowCountCacheLock)
        {
            if (tableName is null) _rowCountCache.Clear();
            else                   _rowCountCache.Remove(tableName);
        }
    }

    // ── Column-list cache helpers ───────────────────────────────────────

    private bool TryGetCachedColumns(string tableName, out IReadOnlyList<ColumnInfo>? columns)
    {
        lock (_columnCacheLock)
            return _columnCache.TryGetValue(tableName, out columns);
    }

    private void StoreColumns(string tableName, IReadOnlyList<ColumnInfo> columns)
    {
        lock (_columnCacheLock)
            _columnCache[tableName] = columns;
    }

    /// <summary>
    /// Drop the cached column list for a table (or all tables when
    /// <paramref name="tableName"/> is null) so the next
    /// <see cref="GetColumnsAsync"/> re-issues PRAGMA table_info. Called
    /// from every column-DDL path and on table create/drop; the browser VM
    /// additionally clears the whole cache when the selected table changes
    /// so each table session starts from the on-disk truth.
    /// </summary>
    public void InvalidateColumns(string? tableName = null)
    {
        lock (_columnCacheLock)
        {
            if (tableName is null) _columnCache.Clear();
            else                   _columnCache.Remove(tableName);
        }
    }

    /// <summary>
    /// Selective single-table refresh used by
    /// the VM's mutation-refresh path. Pre-fix every cell edit re-listed ALL
    /// tables (including system tables) and re-counted rows + columns for each
    /// — O(tables) PRAGMA + COUNT round-trips per keystroke-level edit. This
    /// refreshes only the one table the user just mutated: it invalidates +
    /// re-counts that table and re-reads its column count, returning a fresh
    /// <see cref="TableInfo"/> the VM can splice into its sidebar list without
    /// touching the (unchanged) system-table metadata.
    /// </summary>
    public async Task<TableInfo?> RefreshTableInfoAsync(string tableName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tableName)) return null;
        InvalidateRowCount(tableName);
        ct.ThrowIfCancellationRequested();
        int rows;
        try
        {
            rows = await _db.GetRowCountAsync(tableName).ConfigureAwait(false);
            StoreRowCount(tableName, rows);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log($"DbRelationalSource: row-count failed for '{tableName}': {ex.Message}",
                "DbRelationalSource", LogLevel.System);
            rows = 0;
        }
        int cols;
        try
        {
            var colList = await _db.GetTableColumnsAsync(tableName).ConfigureAwait(false);
            cols = colList.Count;
        }
        catch (Exception ex)
        {
            GlobalLogger.Log($"DbRelationalSource: column-list failed for '{tableName}': {ex.Message}",
                "DbRelationalSource", LogLevel.System);
            cols = 0;
        }
        return new TableInfo(
            tableName, rows, cols,
            IsSystem:   DB.IsSystemTableName(tableName),
            IsAppOwned: DB.IsAppOwnedTableName(tableName));
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct = default)
    {
        // Include the protected tables — the Architect Databank Browser groups
        // the write-locked ones under a "System" header and renders them
        // read-only so power-users can inspect them without opening a SQLite
        // viewer. Both flags are stamped here from the two DB registries: the
        // data affordances gate on TableInfo.IsSystem, the schema affordances
        // on TableInfo.IsAppOwned, so neither kind of control is left enabled
        // over an operation the persistence layer refuses.
        var names = await _db.GetAllTableNamesAsync().ConfigureAwait(false);
        var infos = new List<TableInfo>(names.Count);
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            int rows;
            int cols;
            try
            {
                rows = await _db.GetRowCountAsync(name).ConfigureAwait(false);
                // Seed the count cache so a subsequent snapshot of this table
                // can skip its own COUNT(*).
                StoreRowCount(name, rows);
            }
            catch (Exception ex)
            {
                // Keep the per-table degradation
                // fallback (one bad table shouldn't blank the whole list) but
                // log the cause — pre-fix a locked DB / schema mismatch silently
                // showed "0 rows" with no breadcrumb.
                GlobalLogger.Log($"DbRelationalSource: row-count failed for '{name}': {ex.Message}",
                    "DbRelationalSource", LogLevel.System);
                rows = 0;
            }
            try
            {
                var colList = await _db.GetTableColumnsAsync(name).ConfigureAwait(false);
                cols = colList.Count;
            }
            catch (Exception ex)
            {
                GlobalLogger.Log($"DbRelationalSource: column-list failed for '{name}': {ex.Message}",
                    "DbRelationalSource", LogLevel.System);
                cols = 0;
            }
            infos.Add(new TableInfo(
                name, rows, cols,
                IsSystem:   DB.IsSystemTableName(name),
                IsAppOwned: DB.IsAppOwnedTableName(name)));
        }
        return infos;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default)
    {
        // Serve the column list from the per-table cache when possible —
        // GetRowSnapshotAsync calls here on every page step and sort, where
        // the column shape cannot have changed. DDL paths and the VM's
        // table-selection change invalidate, so a miss re-reads the truth.
        if (TryGetCachedColumns(tableName, out var cachedColumns) && cachedColumns is not null)
        {
            ct.ThrowIfCancellationRequested();
            return cachedColumns;
        }

        // Single PRAGMA table_info round-trip.
        // Pre-fix this issued TWO queries against PRAGMA table_info for the
        // same table — GetTableColumnTypesAsync (a Dictionary) plus
        // GetTableColumnsAsync (the ordered name list) — and merged them, so
        // every row-snapshot (page nav, sort, filter, table switch) paid for
        // two reads of identical metadata. GetSchemaAsync already returns
        // per-column rows in declared CREATE TABLE order (rowid filtered out),
        // which is exactly the (name, type) pair ColumnInfo needs, so drive
        // the projection off it and drop the redundant second query.
        var schema = await _db.GetSchemaAsync(tableName).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var infos = new List<ColumnInfo>(schema.Count);
        foreach (var s in schema)
            infos.Add(new ColumnInfo(s.Name, s.SqlType ?? string.Empty));
        StoreColumns(tableName, infos);
        return infos;
    }

    public async Task<RowSnapshot> GetRowSnapshotAsync(
        string tableName,
        int maxRows = 500,
        int offset = 0,
        string? orderBy = null,
        bool orderDescending = false,
        CancellationToken ct = default)
    {
        var columns = await GetColumnsAsync(tableName, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var orderedColumnNames = columns.Select(c => c.Name).ToList();
        var raw = await _db.GetRowsWithRowIdAsync(
            tableName, orderedColumnNames, maxRows, offset, orderBy, orderDescending)
            .ConfigureAwait(false);

        var rows = new List<RelationalRow>(raw.Count);
        foreach (var (rowId, cells) in raw)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(new RelationalRow(rowId, cells));
        }
        // Total-row count lets the browser footer show "Showing 500 of 1247
        // rows" and the paging controls know when the user is on the last
        // page. The count is a property of the
        // table, not of the page window — page navigation and sort can't
        // change it — so we cache it per table and only issue COUNT(*) on a
        // cache miss (first snapshot of this table) or after a mutation
        // invalidated the entry. Worst case the table errors on count — fall
        // back to the page's own length so the footer still renders something
        // sane (and don't poison the cache with that guess).
        int totalRowCount;
        if (TryGetCachedRowCount(tableName, out int cached))
        {
            totalRowCount = cached;
        }
        else
        {
            try
            {
                totalRowCount = await _db.GetRowCountAsync(tableName).ConfigureAwait(false);
                StoreRowCount(tableName, totalRowCount);
            }
            catch (Exception ex)
            {
                // Keep the page-length fallback; log it.
                GlobalLogger.Log($"DbRelationalSource: total-row-count failed for '{tableName}': {ex.Message}",
                    "DbRelationalSource", LogLevel.System);
                totalRowCount = rows.Count;
            }
        }
        return new RowSnapshot(columns, rows, totalRowCount, offset);
    }

    public async Task<IReadOnlyList<ColumnSchemaInfo>> GetSchemaAsync(string tableName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var schema = await _db.GetSchemaAsync(tableName).ConfigureAwait(false);
        return schema;
    }

    public async Task<long> InsertRowAsync(
        string tableName,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // DB.InsertUserRowAsync expects non-null strings; coerce nulls to ""
        // so the insert lands an empty-string cell instead of throwing.
        var coerced = new Dictionary<string, string>(values.Count);
        foreach (var kv in values)
            coerced[kv.Key] = kv.Value ?? string.Empty;
        long newId = await _db.InsertUserRowAsync(tableName, coerced).ConfigureAwait(false);
        InvalidateRowCount(tableName);
        return newId;
    }

    public async Task UpdateCellAsync(
        string tableName,
        long rowId,
        string columnName,
        string? newValue,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // ONE write path for every table, Vars included. The old code detoured
        // a Vars/VarValue edit through DB.SetVariableAsync because SetCellAsync
        // refused the whole table; the 2026-08 unlock dropped Vars from the
        // write-lock registry, so that detour stopped being a workaround and
        // became a HOLE: SetVariableAsync is the script engine's own key-
        // addressed writer and carries no reserved-key guard (the engine writes
        // its global._* / state.* bookkeeping through it), whereas SetCellAsync
        // screens every rowid-addressed Vars write through DB's per-row
        // engine-key gate. Going back through SetCellAsync puts browser edits
        // under that gate — the same one that already refuses a VarKey rename
        // into a reserved key from this very method.
        bool wrote = await _db.SetCellAsync(tableName, rowId, columnName, newValue ?? string.Empty)
            .ConfigureAwait(false);

        // The one thing the retired detour DID carry that the rowid path does
        // not: SetVariableAsync's upsert ends in `LastModified=CURRENT_TIMESTAMP`.
        // That upsert and the column's CREATE TABLE default are the only two
        // things in the codebase that ever write it, so a rowid UPDATE of
        // VarValue leaves the stamp untouched — and a browser edit would show
        // the grid a LastModified older than the value sitting next to it.
        // Re-stamped here in the UTC second-precision
        // shape SQLite's CURRENT_TIMESTAMP produces, and only after the value
        // write came back ACCEPTED — a row the engine-key gate refused must not
        // collect a fresh timestamp for a change that never landed. The column
        // cannot go missing underneath this: Vars is app-owned, so DDL against
        // it is refused.
        if (wrote
            && string.Equals(tableName, "Vars", System.StringComparison.OrdinalIgnoreCase)
            && string.Equals(columnName, "VarValue", System.StringComparison.OrdinalIgnoreCase))
        {
            await _db.SetCellAsync(
                tableName, rowId, "LastModified",
                System.DateTime.UtcNow.ToString(
                    "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
    }

    public async Task DeleteRowAsync(string tableName, long rowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _db.DeleteRowAsync(tableName, rowId).ConfigureAwait(false);
        InvalidateRowCount(tableName);
    }

    public async Task AddColumnAsync(
        string tableName,
        string columnName,
        string sqlType,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _db.AddColumnAsync(tableName, columnName, sqlType).ConfigureAwait(false);
        InvalidateColumns(tableName);
    }

    // Column-level mutations. Delegate to DB which holds the lock /
    // PRAGMA / DDL surface; we just forward the call. The browser VM
    // refreshes its local schema view after each one via
    // RefreshAfterColumnMutationAsync so the inspector + header strip stay
    // in sync with the on-disk shape.

    public async Task<bool> DropColumnAsync(
        string tableName,
        string columnName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool dropped = await _db.DropColumnAsync(tableName, columnName).ConfigureAwait(false);
        // Invalidate regardless of the outcome — a spurious invalidation only
        // costs one PRAGMA re-read; a missed one shows stale headers.
        InvalidateColumns(tableName);
        return dropped;
    }

    public async Task<bool> RenameColumnAsync(
        string tableName,
        string oldName,
        string newName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool renamed = await _db.RenameColumnAsync(tableName, oldName, newName).ConfigureAwait(false);
        InvalidateColumns(tableName);
        return renamed;
    }

    public async Task<bool> ChangeColumnTypeAsync(
        string tableName,
        string columnName,
        string newAffinityType,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool changed = await _db.ChangeColumnTypeAsync(tableName, columnName, newAffinityType).ConfigureAwait(false);
        InvalidateColumns(tableName);
        return changed;
    }

    public async Task CreateTableAsync(
        string tableName,
        IReadOnlyList<(string Name, string SqlType)> columns,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // DB.CreateUserTableAsync auto-injects "rowid INTEGER PRIMARY KEY
        // AUTOINCREMENT" and only accepts {TEXT, INTEGER, REAL, BOOLEAN};
        // unknown types fall back to TEXT inside DB.
        var list = columns.Select(c => (c.Name, c.SqlType)).ToList();
        await _db.CreateUserTableAsync(tableName, list).ConfigureAwait(false);
        InvalidateRowCount(tableName);
        InvalidateColumns(tableName);
    }

    public async Task ClearTableAsync(string tableName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _db.ClearTableAsync(tableName).ConfigureAwait(false);
        InvalidateRowCount(tableName);
    }

    public async Task DropTableAsync(string tableName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _db.DropUserTableAsync(tableName).ConfigureAwait(false);
        InvalidateRowCount(tableName);
        InvalidateColumns(tableName);
    }
}
