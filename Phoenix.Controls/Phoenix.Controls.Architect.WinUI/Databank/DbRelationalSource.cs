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
// + system-table protections so the Databank tab inherits the same safety
// invariants the script-engine db.* commands enforce.
//
// DB ensures its own lock + WAL setup; we never reach for a
// SqliteConnection directly. The async methods all hop off the UI thread
// because DB's _lock can hold for tens of ms on a contended
// graph save — Track 5's view will await these from a background task.
public sealed class DbRelationalSource : IRelationalSource
{
    private readonly DB _db;

    //  Per-table total-row-count cache. Pre-fix
    // GetRowSnapshotAsync issued a full COUNT(*) on every invocation —
    // including page navigation and sort, where the row count cannot have
    // changed. We cache the count keyed by table name and only re-query when
    // the cache misses (first snapshot of a table) or a mutation invalidates
    // it. Mutations (insert / delete / create / drop / column-shape changes)
    // call InvalidateRowCount so the next snapshot re-counts the truth.
    private readonly Dictionary<string, int> _rowCountCache =
        new(System.StringComparer.OrdinalIgnoreCase);
    private readonly object _rowCountCacheLock = new();

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
    ///  Drop the cached total-row-count for a
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

    /// <summary>
    ///  Selective single-table refresh used by
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
        return new TableInfo(tableName, rows, cols, DB.IsSystemTableName(tableName));
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct = default)
    {
        // Include system tables — the Architect Databank Browser groups them
        // under a "System" header and renders them as read-only so power-users
        // can inspect Vars / EventLog / SystemHistory without opening a SQLite
        // viewer. The destructive toolbar buttons still gate on
        // TableInfo.IsSystem so we don't lose the defense-in-depth guard.
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
                // can skip its own COUNT(*) ().
                StoreRowCount(name, rows);
            }
            catch (Exception ex)
            {
                //  Keep the per-table degradation
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
            infos.Add(new TableInfo(name, rows, cols, DB.IsSystemTableName(name)));
        }
        return infos;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default)
    {
        //  Single PRAGMA table_info round-trip.
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
        // page.  The count is a property of the
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
                //  Keep the page-length fallback; log it.
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
        InvalidateRowCount(tableName); // 
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

        // B9 — Vars is a protected system table on SetCellAsync, but the
        // segregated SetVariableAsync write path exists for exactly this
        // case. Route VarValue edits through it so the Databank Browser can
        // edit values in-place; leave non-VarValue Vars columns
        // (VarKey / LastModified) on the locked-down SetCellAsync path so
        // the PK + timestamp schema contract stays intact for other tables.
        if (string.Equals(tableName, "Vars", System.StringComparison.OrdinalIgnoreCase) &&
            string.Equals(columnName, "VarValue", System.StringComparison.OrdinalIgnoreCase))
        {
            string? key = await _db.GetVarKeyByRowIdAsync(rowId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key))
            {
                // Row vanished between snapshot + edit (rare — concurrent
                // delete). Fall through to SetCellAsync which will log the
                // system-table rejection rather than silently dropping.
                await _db.SetCellAsync(tableName, rowId, columnName, newValue ?? string.Empty).ConfigureAwait(false);
                return;
            }
            await _db.SetVariableAsync(key, newValue ?? string.Empty).ConfigureAwait(false);
            return;
        }

        await _db.SetCellAsync(tableName, rowId, columnName, newValue ?? string.Empty).ConfigureAwait(false);
    }

    public async Task DeleteRowAsync(string tableName, long rowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _db.DeleteRowAsync(tableName, rowId).ConfigureAwait(false);
        InvalidateRowCount(tableName); // 
    }

    public async Task AddColumnAsync(
        string tableName,
        string columnName,
        string sqlType,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _db.AddColumnAsync(tableName, columnName, sqlType).ConfigureAwait(false);
    }

    // C9 — Column-level mutations. Delegate to DB which holds the lock /
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
        return await _db.DropColumnAsync(tableName, columnName).ConfigureAwait(false);
    }

    public async Task<bool> RenameColumnAsync(
        string tableName,
        string oldName,
        string newName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _db.RenameColumnAsync(tableName, oldName, newName).ConfigureAwait(false);
    }

    public async Task<bool> ChangeColumnTypeAsync(
        string tableName,
        string columnName,
        string newAffinityType,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _db.ChangeColumnTypeAsync(tableName, columnName, newAffinityType).ConfigureAwait(false);
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
        InvalidateRowCount(tableName); // 
    }

    public async Task ClearTableAsync(string tableName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _db.ClearTableAsync(tableName).ConfigureAwait(false);
        InvalidateRowCount(tableName); // 
    }

    public async Task DropTableAsync(string tableName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _db.DropUserTableAsync(tableName).ConfigureAwait(false);
        InvalidateRowCount(tableName); // 
    }
}
