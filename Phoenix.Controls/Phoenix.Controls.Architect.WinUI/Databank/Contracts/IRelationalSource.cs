using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Databank.Contracts;

// Read+write abstraction the Databank Browser binds to. Wraps whatever
// relational backend the running Hub is using (DB on disk for
// production; an in-memory fixture for tests) so the view code does not
// touch ADO.NET / SqliteConnection directly.
//
// Why an interface: tests want to feed the
// browser deterministic table contents without spinning up the real
// DB singleton, and a future web Viewer might need
// a remote-relational source rather than the local SQLite file.
//
// Originally read-only — write methods (+ Row / + Column / + Table /
// SetCell / DeleteRow / ClearTable) were added when the WinUI Architect
// inherited the post-T15 edit surface that the WinForms DatabankDialogs
// used to host.
public interface IRelationalSource
{
    /// <summary>List every table in display order, protected ones included —
    /// the browser groups and gates them from the <see cref="TableInfo"/>
    /// flags rather than by omission, so they stay inspectable.</summary>
    Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct = default);

    /// <summary>Column schema for a specific table.</summary>
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Row snapshot for the table — bounded by <paramref name="maxRows"/> so the
    /// view never tries to materialise a million-row table at once. Each row
    /// carries its SQLite <c>rowid</c> so the inspector can issue SetCell /
    /// DeleteRow against it without re-querying. Supports paging via
    /// <paramref name="offset"/> and optional ordering via
    /// <paramref name="orderBy"/> / <paramref name="orderDescending"/>.
    /// </summary>
    Task<RowSnapshot> GetRowSnapshotAsync(
        string tableName,
        int maxRows = 500,
        int offset = 0,
        string? orderBy = null,
        bool orderDescending = false,
        CancellationToken ct = default);

    /// <summary>
    /// Per-column schema for <paramref name="tableName"/>. Drives the Architect
    /// Databank inspector's Schema tab. Empty list when the table is unknown or
    /// the underlying source errors out.
    /// </summary>
    Task<IReadOnlyList<ColumnSchemaInfo>> GetSchemaAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Display path for the underlying backing store, surfaced in the design's
    /// bottom-right "data/databank.sqlite" mono caption. Empty string when no
    /// backing file exists (e.g. in-memory fixtures).
    /// </summary>
    string DisplayPath { get; }

    /// <summary>
    /// Insert a new row with the given column → value pairs. Returns the new
    /// row's <c>rowid</c> so the view can highlight the inserted row.
    /// </summary>
    Task<long> InsertRowAsync(
        string tableName,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct = default);

    /// <summary>Update one cell of an existing row identified by <paramref name="rowId"/>.</summary>
    Task UpdateCellAsync(
        string tableName,
        long rowId,
        string columnName,
        string? newValue,
        CancellationToken ct = default);

    /// <summary>Delete the row identified by <paramref name="rowId"/>.</summary>
    Task DeleteRowAsync(string tableName, long rowId, CancellationToken ct = default);

    /// <summary>Add a new column to an existing table.</summary>
    Task AddColumnAsync(
        string tableName,
        string columnName,
        string sqlType,
        CancellationToken ct = default);

    /// <summary>
    /// Drop a column from a user table. Implementations MUST reject
    /// app-owned tables and the primary-key column, logging the rejection
    /// rather than throwing. Returns <c>true</c> when the column was
    /// dropped.
    /// </summary>
    Task<bool> DropColumnAsync(
        string tableName,
        string columnName,
        CancellationToken ct = default);

    /// <summary>
    /// Rename a column on a user table. Implementations MUST validate
    /// the new identifier (alphanumeric + underscore, not a reserved
    /// SQLite rowid alias, not a collision with an existing column) and
    /// reject app-owned tables. Returns <c>true</c> on success.
    /// </summary>
    Task<bool> RenameColumnAsync(
        string tableName,
        string oldName,
        string newName,
        CancellationToken ct = default);

    /// <summary>
    /// Change a column's declared SQLite affinity (table-recreation
    /// pattern; SQLite has no <c>ALTER COLUMN ... TYPE</c>). Allowed
    /// affinities are <c>TEXT / INTEGER / REAL / BLOB / NUMERIC</c>;
    /// app-owned tables are rejected. Returns <c>true</c> on success.
    /// </summary>
    Task<bool> ChangeColumnTypeAsync(
        string tableName,
        string columnName,
        string newAffinityType,
        CancellationToken ct = default);

    /// <summary>
    /// Create a brand-new user table with the supplied columns. Implementations
    /// MUST inject the implicit <c>rowid INTEGER PRIMARY KEY AUTOINCREMENT</c>
    /// so subsequent rowid-aware reads/writes work without reflection.
    /// </summary>
    Task CreateTableAsync(
        string tableName,
        IReadOnlyList<(string Name, string SqlType)> columns,
        CancellationToken ct = default);

    /// <summary>Delete every row in a table without dropping the table itself.</summary>
    Task ClearTableAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Drop a user table entirely. Implementations MUST reject app-owned
    /// tables — DROP TABLE is a schema change, so the whole
    /// <c>DB.IsAppOwnedTableName</c> set is refused by the persistence layer,
    /// not just the two write-locked ones.
    /// </summary>
    Task DropTableAsync(string tableName, CancellationToken ct = default);
}

/// <summary>
/// One table in the databank. The two protection flags mirror the two
/// registries in <c>DB.cs</c> and answer DIFFERENT questions — a UI that
/// gates both kinds of operation on one of them enables controls the
/// persistence layer then refuses.
///
/// <para><see cref="IsSystem"/> — stamped from <c>DB.IsSystemTableName</c>
/// (the write lock): no caller may change this table's DATA. Only
/// <c>PairedDevices</c> and <c>RemoteAuditLog</c> carry it. Gates cell
/// editing, row insert/delete and clear.</para>
///
/// <para><see cref="IsAppOwned"/> — stamped from
/// <c>DB.IsAppOwnedTableName</c>: Phoenix Controls owns this table's
/// SCHEMA, so DDL (create/drop table, add/drop/rename/retype column) is
/// refused while rows and cells stay open. Covers the 25 tool-maintained
/// tables, including the two above. Gates the column context menu and the
/// drop-table affordance.</para>
///
/// The persistence layer enforces both guards independently — these flags
/// are presentation only.
/// </summary>
public sealed record TableInfo(
    string Name,
    int    RowCount,
    int    ColumnCount,
    bool   IsSystem   = false,
    bool   IsAppOwned = false);

public sealed record ColumnInfo(string Name, string SqlType);

/// <summary>
/// One row of a row-snapshot, paired with the SQLite <c>rowid</c> so the
/// view can identify it for cell-edit / delete operations after a snapshot
/// has been materialised.
/// </summary>
public sealed record RelationalRow(long RowId, IReadOnlyList<string?> Cells);

public sealed record RowSnapshot(
    IReadOnlyList<ColumnInfo>    Columns,
    IReadOnlyList<RelationalRow> Rows,
    int                          TotalRowCount = 0,
    int                          Offset        = 0);
