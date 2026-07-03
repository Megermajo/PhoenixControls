using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Architect.WinUI.Databank.Contracts;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Databank;

// View-model for the Databank Browser tab. Lifts data through the
// IRelationalSource contract so tests / future remote viewers
// can swap the source without touching this code.
//
// Bind:
//   <ListBox      ItemsSource="{Binding Tables}" SelectedItem="{Binding SelectedTable, Mode=TwoWay}"/>
//   <TextBox      Text="{Binding Filter, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/>
//   <ItemsControl ItemsSource="{Binding VisibleColumns}"/>   (header strip)
//   <ItemsControl ItemsSource="{Binding VisibleRows}"/>      (row grid)
//
// Row selection — the design highlights one row in ember (architect.jsx
// row index 3 in DatabankBrowser) — is bound through SelectedRowIndex
// so the inspector can show the chosen record's fields.
//
// Edit surface (post-T15 follow-up): InsertRowAsync / DeleteSelectedRowAsync /
// UpdateCellAsync / AddColumnAsync / CreateTableAsync wrap the
// IRelationalSource writes with auto-refresh of the affected table so
// the grid stays in sync without callers driving a separate reload.
public sealed class DatabankBrowserViewModel : ObservableObject, IDisposable
{
    // True while the VM is "released" — between a
    // tab-away (View.OnUnloaded → Dispose) and the next reuse. Architect caches
    // both the DatabankBrowserView and this VM (ArchitectViewModel.DatabankBrowser),
    // re-inserting the view into MainPaneRegion on every Databank tab visit, so
    // Dispose() must release the leaked transient handles (in-flight load CTS +
    // filter-debounce timer) WITHOUT permanently bricking the VM. The flag stops
    // a late timer tick / reload continuation from touching just-released state
    // and is cleared the moment the VM is used again (table selection / reload),
    // which lazily re-creates the timer and a fresh CTS.
    private bool _released;
    /// <summary>Maximum rows per page — also the value handed to
    /// <see cref="IRelationalSource.GetRowSnapshotAsync"/> for the LIMIT clause.
    /// 500 matches the historic cap and keeps the worst-case grid materialise
    /// cost predictable.</summary>
    public const int PageSize = 500;

    private readonly IRelationalSource _source;
    // Undo controller shared with the canvas via the
    // owning ArchitectViewModel. Null when the VM is constructed standalone
    // (tests, headless wiring); cell edits then write through with no
    // undo entry, which matches pre-sprint behaviour. AVM passes its
    // History through the constructor so cell edits join the same Ctrl+Z
    // timeline as graph edits.
    private readonly UndoRedoController? _history;
    private CancellationTokenSource? _loadCts;
    private TableInfo? _selectedTable;
    private string _filter             = string.Empty;
    private string _tableListFilter    = string.Empty;
    private int _selectedRowIndex      = -1;
    private bool _isLoadingRows;
    private RowSnapshot? _currentSnapshot;
    private int _pageIndex;
    private int _totalRowCount;
    private string? _sortColumn;
    private bool _sortDescending;
    private string? _lastErrorMessage;
    private List<TableInfo> _allTables = new();
    // Debounce timer for the row filter so a
    // burst of keystrokes collapses to one VisibleRows rebuild (~180ms after the
    // last key) instead of clearing + rebuilding up to PageSize row-VMs per key.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _filterDebounce;

    // Captured on construction — the UI (DispatcherQueue)
    // thread in the running app; null under headless tests (no DispatcherQueue).
    // The async load/mutation methods fetch their snapshot OFF the UI thread via
    // ConfigureAwait(false), then marshal the ObservableCollection edits + bound-
    // property writes back here through RunOnUiAsync / RunOnUi. Raising
    // PropertyChanged (or a CollectionChanged) for an x:Bind target off the UI
    // thread throws a COMException out of WinRT
    // (PropertyChangedEventArgsRuntimeClassFactory.CreateInstance) — which crashed
    // the databank view on column-sort and on table reload. Capturing in the ctor
    // (which the host constructs on the UI thread) mirrors how the filter-debounce
    // timer above resolves its DispatcherQueue.
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    public DatabankBrowserViewModel(IRelationalSource source)
        : this(source, history: null) { }

    /// <summary>
    /// Constructor overload that plumbs an
    /// <see cref="UndoRedoController"/> so cell edits enter the same undo
    /// stack as graph edits. Pass null for tests / headless wiring where
    /// the undo stack isn't relevant.
    /// </summary>
    public DatabankBrowserViewModel(IRelationalSource source, UndoRedoController? history)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _history = history;
        // Capture the UI dispatcher (null in headless tests).
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Tables           = new ObservableCollection<TableInfo>();
        SystemTables     = new ObservableCollection<TableInfo>();
        UserTables       = new ObservableCollection<TableInfo>();
        VisibleColumns   = new ObservableCollection<ColumnInfo>();
        VisibleRows      = new ObservableCollection<DatabankRowViewModel>();
        Schema           = new ObservableCollection<ColumnSchemaInfo>();
    }

    // Run a UI-bound mutation (collection edits + bound-
    // property writes) on the captured UI thread and await its completion. Runs
    // inline when already on the UI thread (the natural pre-await call paths) or
    // when there is no dispatcher (headless tests, where nothing is XAML-bound).
    // Awaitable so a reload's "apply" block is sequenced before the method returns.
    private System.Threading.Tasks.Task RunOnUiAsync(Action apply)
    {
        var ui = _ui;
        if (ui is null || ui.HasThreadAccess)
        {
            apply();
            return System.Threading.Tasks.Task.CompletedTask;
        }
        var tcs = new System.Threading.Tasks.TaskCompletionSource(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(() =>
            {
                try { apply(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
        {
            // Dispatcher shut down (tab torn down mid-flight) — best-effort inline.
            apply();
            tcs.TrySetResult();
        }
        return tcs.Task;
    }

    // Fire-and-forget marshal for void call sites (error banner writes) that can't
    // await — the underlying field is already logged synchronously by the caller;
    // only the bound-property notification needs the UI thread.
    private void RunOnUi(Action apply)
    {
        var ui = _ui;
        if (ui is null || ui.HasThreadAccess) { apply(); return; }
        if (!ui.TryEnqueue(() => { try { apply(); } catch { /* notification-only; logged at source */ } }))
            apply();
    }

    /// <summary>Flat union of <see cref="SystemTables"/> and <see cref="UserTables"/>
    /// in declared order — kept around for callers that don't care about the
    /// System/User grouping (drag-drop spawn, tests, etc.).</summary>
    public ObservableCollection<TableInfo>            Tables         { get; }

    /// <summary>System (Hub-managed) tables — rendered under a "System" header,
    /// dimmed + lock-glyph in the sidebar.</summary>
    public ObservableCollection<TableInfo>            SystemTables   { get; }

    /// <summary>User-authored tables — full-strength text, mutable.</summary>
    public ObservableCollection<TableInfo>            UserTables     { get; }

    public ObservableCollection<ColumnInfo>           VisibleColumns { get; }
    public ObservableCollection<DatabankRowViewModel> VisibleRows    { get; }

    /// <summary>Per-column schema (name / type / nullable / default / PK) for
    /// the selected table — drives the DatabankInspector's Schema tab.</summary>
    public ObservableCollection<ColumnSchemaInfo>     Schema         { get; }

    public string DisplayPath => _source.DisplayPath;

    /// <summary>The relational source backing the browser — exposed so write
    /// commands in the view layer (toolbar buttons) can route through the
    /// same contract the VM uses for reads.</summary>
    public IRelationalSource Source => _source;

    public TableInfo? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (SetField(ref _selectedTable, value))
            {
                SelectedRowIndex = -1;
                // Reset paging + sort whenever the user moves to a different table.
                // Carrying offset/sort across tables surprises users (you click a new
                // table and land on page 3 of nothing) and re-issues a needlessly
                // expensive query for the new table's first page.
                _pageIndex      = 0;
                _sortColumn     = null;
                _sortDescending = false;
                // Wrap the reload so a faulted DB query (locked file, schema
                // mismatch) lands in GlobalLogger instead of disappearing as
                // an unobserved task exception.
                _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => ReloadRowsAsync(),
                    "DatabankBrowserViewModel", $"ReloadRows({value?.Name ?? "<null>"})");
                _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => ReloadSchemaAsync(),
                    "DatabankBrowserViewModel", $"ReloadSchema({value?.Name ?? "<null>"})");
                OnPropertyChanged(nameof(SelectedTableHeader));
                OnPropertyChanged(nameof(HasSelectedTable));
                OnPropertyChanged(nameof(IsEmptyState));
                OnPropertyChanged(nameof(IsSystemTableSelected));
                OnPropertyChanged(nameof(CanMutateSelectedTable));
                OnPropertyChanged(nameof(PageIndex));
                OnPropertyChanged(nameof(SortColumn));
                OnPropertyChanged(nameof(SortDescending));
            }
        }
    }

    public string SelectedTableHeader =>
        _selectedTable is null
            ? string.Empty
            : $"· {_selectedTable.RowCount} rows · {_selectedTable.ColumnCount} columns · sqlite";

    /// <summary>True when a table is selected — drives the toolbar Add Row /
    /// Add Column / Delete Row enable state.</summary>
    public bool HasSelectedTable => _selectedTable is not null;

    /// <summary>Inverse of <see cref="HasSelectedTable"/> — drives the empty-state
    /// placeholder in the centre pane.</summary>
    public bool IsEmptyState => _selectedTable is null;

    /// <summary>True when the selected table is one of the protected Hub-managed
    /// tables (Vars / EventLog / SystemHistory / Viewer remote-bridge tables).
    /// The destructive toolbar buttons disable on this; the persistence layer
    /// guards in depth.</summary>
    public bool IsSystemTableSelected => _selectedTable?.IsSystem == true;

    /// <summary>True when a table is selected AND it isn't protected — the
    /// composite gate the toolbar destructive buttons actually need.</summary>
    public bool CanMutateSelectedTable => HasSelectedTable && !IsSystemTableSelected;

    /// <summary>Row-grid filter (matches against any cell substring). Renamed
    /// from the original "Filter" so the XAML can distinguish it from
    /// <see cref="TableListFilter"/>; the XAML binding still points here.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
                RequestFilterApply();
        }
    }

    // Debounce the row-filter rebuild. The
    // two-way binding stays snappy (SetField fired immediately above); only the
    // VisibleRows rebuild is deferred ~180ms past the last keystroke. Falls back
    // to a synchronous apply off the UI thread (tests / headless) where there's
    // no DispatcherQueue.
    private void RequestFilterApply()
    {
        // A binding update reaching us means the VM
        // is live again — clear the released flag so the lazily-created timer
        // below isn't immediately suppressed when the tab is reopened.
        _released = false;
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq is null) { ApplyFilter(); return; }
        if (_filterDebounce is null)
        {
            _filterDebounce = dq.CreateTimer();
            _filterDebounce.Interval = System.TimeSpan.FromMilliseconds(180);
            _filterDebounce.IsRepeating = false;
            _filterDebounce.Tick += (_, _) => { if (!_released) ApplyFilter(); };
        }
        _filterDebounce.Stop();   // restart the countdown each keystroke
        _filterDebounce.Start();
    }

    /// <summary>Sidebar filter — substring match against table names. Empty
    /// shows the full System + User split. Separated from <see cref="Filter"/>
    /// so users can narrow the table list without disturbing their row search.</summary>
    public string TableListFilter
    {
        get => _tableListFilter;
        set
        {
            if (SetField(ref _tableListFilter, value ?? string.Empty))
                RebuildTableListProjections();
        }
    }

    /// <summary>Zero-based page index into the current table's rows. Toolbar
    /// paging buttons drive this; <see cref="ReloadRowsAsync"/> reads it.</summary>
    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (SetField(ref _pageIndex, value))
            {
                OnPropertyChanged(nameof(CanPagePrev));
                OnPropertyChanged(nameof(CanPageNext));
                OnPropertyChanged(nameof(PageFooterText));
            }
        }
    }

    /// <summary>Total row count of the selected table (from the latest snapshot).</summary>
    public int TotalRowCount
    {
        get => _totalRowCount;
        private set
        {
            if (SetField(ref _totalRowCount, value))
            {
                OnPropertyChanged(nameof(CanPagePrev));
                OnPropertyChanged(nameof(CanPageNext));
                OnPropertyChanged(nameof(PageFooterText));
            }
        }
    }

    /// <summary>True when there's a previous page to step back to.
    /// Gated on !IsLoadingRows so the paging buttons
    /// disable during an in-flight query (no rapid double-paging).</summary>
    public bool CanPagePrev => _pageIndex > 0 && !_isLoadingRows;

    /// <summary>True when there's a next page (more rows beyond the current LIMIT+OFFSET).</summary>
    public bool CanPageNext => (_pageIndex + 1) * PageSize < _totalRowCount && !_isLoadingRows;

    /// <summary>Footer caption: "Showing N–M of T rows" — also covers the "all rows"
    /// edge case when total ≤ PageSize.</summary>
    public string PageFooterText
    {
        get
        {
            if (_selectedTable is null) return string.Empty;
            if (_totalRowCount == 0)     return "no rows";
            int from = _pageIndex * PageSize + 1;
            int to   = System.Math.Min(_totalRowCount, (_pageIndex + 1) * PageSize);
            if (_totalRowCount <= PageSize) return $"showing {_totalRowCount} of {_totalRowCount} rows";
            return $"showing {from}–{to} of {_totalRowCount} rows";
        }
    }

    /// <summary>Currently-applied sort column, or null for default (rowid ascending).</summary>
    public string? SortColumn
    {
        get => _sortColumn;
        private set => SetField(ref _sortColumn, value);
    }

    /// <summary>True when <see cref="SortColumn"/> is sorted descending; false for ascending
    /// (and meaningless when SortColumn is null).</summary>
    public bool SortDescending
    {
        get => _sortDescending;
        private set => SetField(ref _sortDescending, value);
    }

    /// <summary>Latest user-visible error message — surfaced in the row-grid
    /// InfoBar. Set when a command throws; cleared on the next successful op so
    /// stale errors don't linger past the user's recovery.</summary>
    public string? LastErrorMessage
    {
        get => _lastErrorMessage;
        private set
        {
            if (SetField(ref _lastErrorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>True when <see cref="LastErrorMessage"/> is non-empty — drives the
    /// InfoBar's Visibility.</summary>
    public bool HasError => !string.IsNullOrEmpty(_lastErrorMessage);

    public int SelectedRowIndex
    {
        get => _selectedRowIndex;
        set
        {
            if (SetField(ref _selectedRowIndex, value))
            {
                OnPropertyChanged(nameof(SelectedRow));
                OnPropertyChanged(nameof(HasSelectedRow));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The currently selected row VM, or null when none / out of range.</summary>
    public DatabankRowViewModel? SelectedRow
    {
        get
        {
            // Snapshot the collection reference + a
            // local index copy so the bounds check and the indexer read the same
            // state. ApplyFilter() can Clear()+rebuild VisibleRows on the filter
            // debounce tick between the Count check and the index access; reading
            // both off one captured reference closes that race window.
            var rows  = VisibleRows;
            int index = _selectedRowIndex;
            return (index >= 0 && index < rows.Count) ? rows[index] : null;
        }
    }

    /// <summary>True when SelectedRow resolves to a non-null VM — drives
    /// the Delete-Row toolbar button enable state.</summary>
    public bool HasSelectedRow => SelectedRow is not null;

    public bool IsLoadingRows
    {
        get => _isLoadingRows;
        private set
        {
            if (SetField(ref _isLoadingRows, value))
            {
                // paging buttons disable while loading.
                OnPropertyChanged(nameof(CanPagePrev));
                OnPropertyChanged(nameof(CanPageNext));
            }
        }
    }

    /// <summary>True when a row filter is active but the
    /// current page has no matching rows — drives a "no matches" message so an
    /// empty grid reads as "filtered to nothing", not "broken / still loading".</summary>
    public bool HasNoFilterMatches
        => _currentSnapshot is not null && !string.IsNullOrEmpty(_filter) && VisibleRows.Count == 0;

    /// <summary>Caption for the no-matches message.</summary>
    public string NoMatchesText => $"No rows on this page match “{_filter}”";

    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Raised when the schema list for the selected table has been
    /// (re-)loaded. Architect's host wires this so the inspector's Schema
    /// tab can mirror the current table's columns without polling.
    /// </summary>
    public event EventHandler? SchemaChanged;

    /// <summary>Clear the LastErrorMessage banner — called from every successful
    /// command path so the InfoBar fades on the next happy operation.</summary>
    public void ClearError()
    {
        // Called from success paths that run on a thread-pool
        // continuation (after a source mutation's ConfigureAwait(false)); the
        // LastErrorMessage write raises PropertyChanged, so marshal it.
        RunOnUi(() =>
        {
            if (_lastErrorMessage is not null)
                LastErrorMessage = null;
        });
    }

    private void ReportError(string message, Exception? ex)
    {
        // Log immediately on the calling thread (the logger is thread-safe); only
        // the bound-property write needs the UI thread.
        if (ex is not null)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "DatabankBrowserViewModel", message, ex);
        }
        else
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                message, "DatabankBrowserViewModel",
                Phoenix.Controls.Shared.Models.LogLevel.System);
        }
        RunOnUi(() => LastErrorMessage = message);
    }

    /// <summary>Refresh the table list from the source. Call this when the tab is shown.</summary>
    public async Task ReloadTablesAsync(CancellationToken ct = default)
    {
        try
        {
            var infos = await _source.ListTablesAsync(ct).ConfigureAwait(false);

            // The projections + SelectedTable assignment below
            // mutate ObservableCollections and raise PropertyChanged; marshal them.
            await RunOnUiAsync(() =>
            {
                _allTables = infos.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                RebuildTableListProjections();

                // Keep the previous selection if its table is still present, else
                // pick the first user table (preferred) or first system table —
                // landing on a user table matches the historic behaviour where
                // "Users" was pre-selected on first paint.
                TableInfo? next = null;
                if (_selectedTable is not null)
                    next = infos.FirstOrDefault(t => t.Name == _selectedTable.Name);
                next ??= infos.FirstOrDefault(t => !t.IsSystem)
                      ?? (infos.Count > 0 ? infos[0] : null);
                SelectedTable = next;
            }).ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError($"Failed to list tables: {ex.Message}", ex);
        }
    }

    /// <summary>True when any system table survives the current sidebar
    /// filter — drives the visibility of the "SYSTEM" group header.</summary>
    public bool HasSystemTables => SystemTables.Count > 0;

    /// <summary>True when any user table survives the current sidebar filter —
    /// drives the visibility of the "USER" group header. Always true on an
    /// unfiltered fresh databank because <c>Vars</c> + <c>EventLog</c> +
    /// <c>SystemHistory</c> are user tables' siblings, but a tight filter
    /// (e.g. typing "vars") may legitimately empty this group.</summary>
    public bool HasUserTables => UserTables.Count > 0;

    /// <summary>Rebuild <see cref="Tables"/> / <see cref="SystemTables"/> /
    /// <see cref="UserTables"/> from <see cref="_allTables"/>, applying the
    /// current sidebar filter. Called on every reload and on every TableListFilter
    /// change — the source list is small (rarely more than a few dozen tables),
    /// so rebuilding is cheaper than diffing.</summary>
    private void RebuildTableListProjections()
    {
        Tables.Clear();
        SystemTables.Clear();
        UserTables.Clear();

        bool hasFilter = !string.IsNullOrEmpty(_tableListFilter);
        string needle  = hasFilter ? _tableListFilter.ToLowerInvariant() : string.Empty;

        foreach (var t in _allTables)
        {
            if (hasFilter && !t.Name.ToLowerInvariant().Contains(needle)) continue;
            Tables.Add(t);
            if (t.IsSystem) SystemTables.Add(t);
            else            UserTables.Add(t);
        }
        OnPropertyChanged(nameof(HasSystemTables));
        OnPropertyChanged(nameof(HasUserTables));
    }

    private async Task ReloadRowsAsync()
    {
        // A reload means the VM is in active use
        // again after any prior tab-away release — re-arm so a stale released
        // flag can't suppress the freshly-allocated CTS / later timer ticks.
        _released = false;
        // Cancel + dispose the previous CTS before allocating a fresh one.
        // Without the dispose, every table-switch left an undisposed
        // CancellationTokenSource (each carries a WaitHandle) — a small but
        // real GC handle accretion under rapid table-list navigation.
        var old = _loadCts;
        _loadCts = new CancellationTokenSource();
        try { old?.Cancel(); } catch { /* already cancelled / disposed */ }
        old?.Dispose();
        var ct = _loadCts.Token;

        // This method is entered from both the UI thread (user
        // sort / page / table-select) AND a thread-pool continuation (via
        // RefreshAfterMutationAsync after a source write's ConfigureAwait(false)).
        // So even the grid-reset clears below can run off the UI thread — marshal
        // every ObservableCollection / bound-property touch. The DB fetch itself
        // stays off the UI thread (ConfigureAwait(false)) so the grid never blocks.
        var selected = _selectedTable;
        await RunOnUiAsync(() =>
        {
            VisibleColumns.Clear();
            VisibleRows.Clear();
            _currentSnapshot = null;
            if (selected is null)
            {
                TotalRowCount = 0;
                OnPropertyChanged(nameof(PageFooterText));
                OnPropertyChanged(nameof(SelectedRow));
            }
            else
            {
                IsLoadingRows = true;
            }
        }).ConfigureAwait(false);

        if (selected is null) return;

        try
        {
            int offset = _pageIndex * PageSize;
            var snapshot = await _source.GetRowSnapshotAsync(
                selected.Name, PageSize, offset, _sortColumn, _sortDescending, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            await RunOnUiAsync(() =>
            {
                _currentSnapshot = snapshot;
                foreach (var c in snapshot.Columns) VisibleColumns.Add(c);
                TotalRowCount = snapshot.TotalRowCount;
                ApplyFilter();
            }).ConfigureAwait(false);
            ClearError();
        }
        catch (OperationCanceledException) { /* superseded by another reload */ }
        catch (Exception ex)
        {
            ReportError(
                $"Failed to load rows for '{selected.Name}': {ex.Message}", ex);
        }
        finally
        {
            await RunOnUiAsync(() => IsLoadingRows = false).ConfigureAwait(false);
        }
    }

    private void ApplyFilter()
    {
        VisibleRows.Clear();
        if (_currentSnapshot is null) return;

        bool hasFilter = !string.IsNullOrEmpty(_filter);
        string needle  = hasFilter ? _filter.ToLowerInvariant() : string.Empty;

        for (int i = 0; i < _currentSnapshot.Rows.Count; i++)
        {
            var row = _currentSnapshot.Rows[i];
            if (!hasFilter || RowMatchesFilter(row.Cells, needle))
                VisibleRows.Add(new DatabankRowViewModel(i, row.RowId, _currentSnapshot.Columns, row.Cells));
        }
        SelectedRowIndex = -1;
        OnPropertyChanged(nameof(SelectedRow));
        // refresh the no-matches message after the rebuild.
        OnPropertyChanged(nameof(HasNoFilterMatches));
        OnPropertyChanged(nameof(NoMatchesText));
    }

    private static bool RowMatchesFilter(IReadOnlyList<string?> cells, string needle)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (c is not null && c.ToLowerInvariant().Contains(needle)) return true;
        }
        return false;
    }

    /// <summary>
    /// Refresh the per-column schema for the currently-selected table. Called
    /// from the SelectedTable setter so the inspector's Schema tab mirrors the
    /// browser without external coordination; the SchemaChanged event lets
    /// listeners (DatabankInspectorViewModel) re-bind their list view.
    /// </summary>
    private async Task ReloadSchemaAsync()
    {
        // Entered from the SelectedTable setter (UI) and from
        // RefreshAfterColumnMutationAsync (thread-pool continuation). Schema is an
        // ObservableCollection bound to the inspector's Schema tab, and listeners
        // re-bind on SchemaChanged — so both the Clear/Add and the event raise must
        // land on the UI thread.
        var selected = _selectedTable;
        if (selected is null)
        {
            await RunOnUiAsync(() =>
            {
                Schema.Clear();
                SchemaChanged?.Invoke(this, EventArgs.Empty);
            }).ConfigureAwait(false);
            return;
        }
        try
        {
            var rows = await _source.GetSchemaAsync(selected.Name).ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                Schema.Clear();
                foreach (var r in rows) Schema.Add(r);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportError($"Failed to load schema for '{selected.Name}': {ex.Message}", ex);
        }
        await RunOnUiAsync(() => SchemaChanged?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    // ── Edit surface ────────────────────────────────────────────────────

    /// <summary>
    /// Insert a new row into the selected table, seeded with type-aware
    /// defaults per column (<c>0</c> for INTEGER/BOOLEAN, <c>0.0</c> for REAL,
    /// <c>""</c> for TEXT). Pre-fix every default was an empty string, which
    /// caused INTEGER columns to round-trip as the literal "" and fail the
    /// inline cell editor's parse gate later. System tables are rejected.
    /// </summary>
    public async Task<long> InsertBlankRowAsync()
    {
        if (_selectedTable is null) return 0;
        if (_selectedTable.IsSystem)
        {
            ReportError($"'{_selectedTable.Name}' is a system table and cannot be modified.", null);
            return 0;
        }
        var values = BuildSeedDefaultsForCurrentColumns();
        try
        {
            long newId = await _source.InsertRowAsync(_selectedTable.Name, values).ConfigureAwait(false);
            await RefreshAfterMutationAsync().ConfigureAwait(false);
            ClearError();
            return newId;
        }
        catch (Exception ex)
        {
            ReportError($"Failed to insert row: {ex.Message}", ex);
            return 0;
        }
    }

    /// <summary>Insert a row with explicit column → value bindings.</summary>
    public async Task<long> InsertRowAsync(IReadOnlyDictionary<string, string?> values)
    {
        if (_selectedTable is null) return 0;
        try
        {
            long newId = await _source.InsertRowAsync(_selectedTable.Name, values).ConfigureAwait(false);
            await RefreshAfterMutationAsync().ConfigureAwait(false);
            ClearError();
            return newId;
        }
        catch (Exception ex)
        {
            ReportError($"Failed to insert row: {ex.Message}", ex);
            return 0;
        }
    }

    /// <summary>Delete the currently-selected row and refresh.</summary>
    public async Task DeleteSelectedRowAsync()
    {
        if (_selectedTable is null) return;
        if (_selectedTable.IsSystem)
        {
            ReportError($"'{_selectedTable.Name}' is a system table and cannot be modified.", null);
            return;
        }
        var row = SelectedRow;
        if (row is null) return;
        try
        {
            await _source.DeleteRowAsync(_selectedTable.Name, row.RowId).ConfigureAwait(false);
            await RefreshAfterMutationAsync().ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError($"Failed to delete row: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Delete a batch of rows by rowid (the multi-select Ctrl/Shift delete in the
    /// Databank Browser), then refresh once. Single-row delete routes here too
    /// with a one-element list. System tables are rejected at this layer (and
    /// again in the persistence layer). A per-row failure is logged + surfaced
    /// but does NOT abort the rest of the batch. The row grid is explicitly
    /// reloaded after the deletes so the removed rows visibly disappear
    /// regardless of which refresh path <see cref="RefreshAfterMutationAsync"/>
    /// takes (its selective DbRelationalSource path only re-counts the sidebar).
    /// </summary>
    public async Task DeleteRowsAsync(IReadOnlyCollection<long> rowIds)
    {
        if (_selectedTable is null) return;
        if (rowIds is null || rowIds.Count == 0) return;
        if (_selectedTable.IsSystem)
        {
            ReportError($"'{_selectedTable.Name}' is a system table and cannot be modified.", null);
            return;
        }
        string table = _selectedTable.Name;
        int failed = 0;
        Exception? lastEx = null;
        foreach (var rowId in rowIds)
        {
            try
            {
                await _source.DeleteRowAsync(table, rowId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failed++;
                lastEx = ex;
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "DatabankBrowserViewModel", $"Failed to delete row {rowId} from '{table}'", ex);
            }
        }
        // Sidebar row-count + the row grid both reflect the deletion.
        await RefreshAfterMutationAsync().ConfigureAwait(false);
        await ReloadRowsAsync().ConfigureAwait(false);
        if (failed > 0)
            ReportError(
                $"Failed to delete {failed} of {rowIds.Count} row(s) from '{table}'. See System Log for details.",
                lastEx);
        else
            ClearError();
    }

    /// <summary>
    /// Persist a cell edit. The row VM mirrors the new value optimistically
    /// before the call; on failure the snapshot reload restores the truth.
    ///
    /// When an <see cref="UndoRedoController"/> is
    /// wired in via the constructor, the pre-edit cell value is captured
    /// BEFORE the DB write and pushed as an undo operation. Ctrl+Z then
    /// replays the pre-edit value through <see cref="ApplyUndoCellRewrite"/>,
    /// which writes via <see cref="IRelationalSource"/> directly so the
    /// undo / redo target the original (tableName, rowId, columnName)
    /// tuple regardless of what the UI now shows.
    /// </summary>
    public async Task UpdateCellAsync(DatabankRowViewModel row, string columnName, string? newValue)
    {
        if (_selectedTable is null) return;
        if (row is null) return;

        // Capture identifying state BEFORE the write so the undo
        // operation targets a stable (tableName, rowId, columnName)
        // tuple even if the user navigates to a different table after
        // the edit lands. row.RowId stays valid as long as the row
        // hasn't been deleted; if the rowid has gone away by the time
        // Undo fires, the IRelationalSource update is a no-op (UPDATE
        // ... WHERE rowid=? affects zero rows) and the local mirror
        // attempt below falls through cleanly.
        string  tableName   = _selectedTable.Name;
        long    rowId       = row.RowId;
        string? oldValue    = row[columnName];
        bool    captureUndo = _history is not null;

        try
        {
            await _source.UpdateCellAsync(tableName, rowId, columnName, newValue).ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError($"Failed to update cell {columnName}: {ex.Message}", ex);
            throw; // Let the caller's catch-and-rollback path see the failure.
        }

        if (captureUndo)
        {
            // Snapshot the row reference + identifying tuple in the closure.
            // The captured tableName lets Undo / Redo target the original
            // table even if the user navigates away after the edit lands;
            // ApplyUndoCellRewrite uses the captured tuple directly and
            // bypasses the SelectedTable lookup that UpdateCellAsync's
            // forward path does.
            var capturedRow      = row;
            var capturedTable    = tableName;
            var capturedRowId    = rowId;
            var capturedColumn   = columnName;
            var capturedOldValue = oldValue;
            var capturedNewValue = newValue;
            _history!.CreateOperation(
                undo: () => ApplyUndoCellRewrite(capturedTable, capturedRowId, capturedRow, capturedColumn, capturedOldValue),
                redo: () => ApplyUndoCellRewrite(capturedTable, capturedRowId, capturedRow, capturedColumn, capturedNewValue));
        }
        // No full reload: the optimistic mirror in the row VM is now
        // authoritative. A full reload would scroll-jump and clear filter
        // selection on every cell edit, which is jarring during data entry.
    }

    /// <summary>
    /// Replay helper for the Undo / Redo callbacks. Mirrors
    /// the value into the row VM (so the bound cell repaints even when
    /// the row isn't the current selection) and persists through
    /// <see cref="IRelationalSource.UpdateCellAsync"/> directly — bypassing
    /// the public <see cref="UpdateCellAsync"/> entry point so the captured
    /// (tableName, rowId) tuple drives the write even if the user has
    /// navigated to a different table. Fire-and-forget via
    /// AsyncErrorBoundary so a faulted rollback surfaces through
    /// GlobalLogger rather than bubbling out of the synchronous Ctrl+Z
    /// keyboard handler.
    /// </summary>
    private void ApplyUndoCellRewrite(
        string                tableName,
        long                  rowId,
        DatabankRowViewModel? row,
        string                columnName,
        string?               value)
    {
        // Mirror into the local row VM up-front so the row grid repaints
        // even before the async DB write completes (matches the
        // optimistic-update pattern PersistCellAsync uses for forward
        // edits). The mirror is best-effort — the row VM may have been
        // recycled if the table reloaded since the edit.
        row?.ApplyLocalEdit(columnName, value);
        _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
            async () =>
            {
                try
                {
                    await _source.UpdateCellAsync(tableName, rowId, columnName, value).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                        "DatabankBrowserViewModel",
                        $"Undo cell rewrite failed for {tableName}.{columnName} (rowid={rowId})", ex);
                }
            },
            "DatabankBrowserViewModel", $"UndoCellRewrite({tableName}.{columnName})");
    }

    /// <summary>Add a new column (TEXT/INTEGER/REAL/BOOLEAN) to the selected table.</summary>
    public async Task AddColumnAsync(string columnName, string sqlType)
    {
        if (_selectedTable is null) return;
        if (_selectedTable.IsSystem)
        {
            ReportError($"'{_selectedTable.Name}' is a system table and cannot be modified.", null);
            return;
        }
        if (string.IsNullOrWhiteSpace(columnName)) return;
        try
        {
            await _source.AddColumnAsync(_selectedTable.Name, columnName, sqlType).ConfigureAwait(false);
            await RefreshAfterMutationAsync().ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError($"Failed to add column '{columnName}': {ex.Message}", ex);
        }
    }

    // Column-level mutations on the Architect Databank Browser.
    // Each method delegates to IRelationalSource (which routes through DB)
    // and forces a schema + row reload so the inspector's Schema tab, the
    // column-header strip, and the row grid all reflect the new shape
    // without callers having to drive a separate refresh.

    /// <summary>
    /// Drop a column from the selected table. Rejected for system
    /// tables at this layer (with a logged + banner-surfaced reason) and
    /// for primary-key columns at the persistence layer. On success the
    /// row snapshot reloads so the bound column-header strip drops the
    /// removed column and the row cells re-index to the new column list.
    /// </summary>
    public async Task<bool> DropColumnAsync(string columnName)
    {
        if (_selectedTable is null) return false;
        if (_selectedTable.IsSystem)
        {
            ReportError($"'{_selectedTable.Name}' is a system table and cannot be modified.", null);
            return false;
        }
        if (string.IsNullOrWhiteSpace(columnName)) return false;
        try
        {
            bool ok = await _source.DropColumnAsync(_selectedTable.Name, columnName).ConfigureAwait(false);
            if (!ok)
            {
                // DB-layer rejection (PK, non-existent column, exception
                // already logged via GlobalLogger). Surface a concise
                // banner so the user sees the click didn't land; the
                // detailed reason is in the System Log.
                ReportError(
                    $"Could not drop column '{columnName}'. See System Log for details.",
                    null);
                return false;
            }
            await RefreshAfterColumnMutationAsync().ConfigureAwait(false);
            ClearError();
            return true;
        }
        catch (Exception ex)
        {
            ReportError($"Failed to drop column '{columnName}': {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Rename a column on the selected table. The new name passes
    /// through the persistence layer's identifier + reserved-alias guards
    /// before the ALTER lands; banner errors surface on rejection.
    /// </summary>
    public async Task<bool> RenameColumnAsync(string oldName, string newName)
    {
        if (_selectedTable is null) return false;
        if (_selectedTable.IsSystem)
        {
            ReportError($"'{_selectedTable.Name}' is a system table and cannot be modified.", null);
            return false;
        }
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
        // Identifier shape gate at the VM layer too — surface the rejection
        // as a banner so the user gets a quick "not a valid identifier"
        // signal instead of just a System Log entry. The DB layer re-checks.
        if (!IsValidColumnIdentifier(newName))
        {
            ReportError(
                $"'{newName}' is not a valid column name. Use letters, digits, and underscores only (must not start with a digit).",
                null);
            return false;
        }
        try
        {
            bool ok = await _source.RenameColumnAsync(_selectedTable.Name, oldName, newName).ConfigureAwait(false);
            if (!ok)
            {
                ReportError(
                    $"Could not rename '{oldName}' to '{newName}'. See System Log for details.",
                    null);
                return false;
            }
            await RefreshAfterColumnMutationAsync().ConfigureAwait(false);
            ClearError();
            return true;
        }
        catch (Exception ex)
        {
            ReportError($"Failed to rename column '{oldName}': {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Change a column's declared SQLite affinity. The persistence
    /// layer performs the table-recreation pattern under a single
    /// transaction; on success the schema + row reload picks up the new
    /// type so the inline cell editor (TextBox / NumericBox / CheckBox
    /// fork) re-selects on next double-tap.
    /// </summary>
    public async Task<bool> ChangeColumnTypeAsync(string columnName, string newAffinityType)
    {
        if (_selectedTable is null) return false;
        if (_selectedTable.IsSystem)
        {
            ReportError($"'{_selectedTable.Name}' is a system table and cannot be modified.", null);
            return false;
        }
        if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(newAffinityType))
            return false;
        try
        {
            bool ok = await _source.ChangeColumnTypeAsync(_selectedTable.Name, columnName, newAffinityType)
                .ConfigureAwait(false);
            if (!ok)
            {
                ReportError(
                    $"Could not change type of '{columnName}' to {newAffinityType}. See System Log for details.",
                    null);
                return false;
            }
            await RefreshAfterColumnMutationAsync().ConfigureAwait(false);
            ClearError();
            return true;
        }
        catch (Exception ex)
        {
            ReportError($"Failed to change column type for '{columnName}': {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is a syntactically
    /// valid SQLite-style identifier (alphanumeric + underscore, first
    /// character not a digit). Mirrors the DB layer's IsValidIdentifier
    /// regex so the banner-error path catches the obvious cases before the
    /// round-trip.
    /// </summary>
    public static bool IsValidColumnIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        char first = name[0];
        if (!(char.IsLetter(first) || first == '_')) return false;
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    /// <summary>
    /// Reload both the table aggregate (row/col counts in the
    /// sidebar) AND the current page's row snapshot + schema. Used after
    /// every column-shape mutation so VisibleColumns / Schema / row cells
    /// all reflect the new on-disk shape together. Distinct from
    /// <see cref="RefreshAfterMutationAsync"/> which only re-pulls the
    /// table list (sufficient for row-level edits where the column list
    /// is unchanged).
    /// </summary>
    private async Task RefreshAfterColumnMutationAsync()
    {
        await ReloadTablesAsync().ConfigureAwait(false);
        await ReloadRowsAsync().ConfigureAwait(false);
        await ReloadSchemaAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Create a new user table with the supplied columns and select it on
    /// the next refresh so the user lands inside their freshly-created
    /// table.
    /// </summary>
    public async Task CreateTableAsync(string tableName, IReadOnlyList<(string Name, string SqlType)> columns)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return;
        try
        {
            await _source.CreateTableAsync(tableName, columns).ConfigureAwait(false);
            await ReloadTablesAsync().ConfigureAwait(false);
            // Landing on the freshly-created table — the
            // SelectedTable setter raises PropertyChanged + kicks a reload, so it
            // must run on the UI thread (we're on a pool continuation here).
            await RunOnUiAsync(() =>
            {
                var fresh = Tables.FirstOrDefault(t => t.Name == tableName);
                if (fresh is not null) SelectedTable = fresh;
            }).ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError($"Failed to create table '{tableName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Drop a user table outright. Rejected for system tables both here and
    /// at the persistence layer (defense in depth). On success the table list
    /// reloads and the selection falls through to the next user table.
    /// </summary>
    public async Task DropTableAsync(TableInfo table)
    {
        if (table is null) return;
        if (table.IsSystem)
        {
            ReportError($"'{table.Name}' is a system table and cannot be dropped.", null);
            return;
        }
        try
        {
            await _source.DropTableAsync(table.Name).ConfigureAwait(false);
            // If the dropped table was the active selection, clear it so the
            // post-reload selection-recovery picks the next user table.
            // SelectedTable setter raises PropertyChanged +
            // kicks a reload — marshal it (we're on a pool continuation here).
            await RunOnUiAsync(() =>
            {
                if (_selectedTable?.Name == table.Name)
                    SelectedTable = null;
            }).ConfigureAwait(false);
            await ReloadTablesAsync().ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError($"Failed to drop table '{table.Name}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Materialise the full table and hand its rows to
    /// <paramref name="onSnapshot"/> so the view layer can serialize the data
    /// (CSV / JSON / whatever) using Windows.Storage pickers. Keeps file I/O
    /// out of the VM and out of <see cref="IRelationalSource"/> while still
    /// routing the read through DB's serialized connection.
    /// </summary>
    public async Task ExportSnapshotAsync(TableInfo table, Func<RowSnapshot, Task> onSnapshot)
    {
        if (table is null || onSnapshot is null) return;
        try
        {
            // Pull the full table for export, not just the current page —
            // streamers exporting a chat-log table for analysis want every
            // row, not the current 500-row window. int.MaxValue lets SQLite
            // cap to whatever the engine allows.
            var snapshot = await _source.GetRowSnapshotAsync(
                table.Name, int.MaxValue, 0, null, false).ConfigureAwait(false);
            await onSnapshot(snapshot).ConfigureAwait(false);
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError($"Failed to export '{table.Name}': {ex.Message}", ex);
        }
    }

    /// <summary>Step to the next page of rows, if any.</summary>
    public async Task GoToNextPageAsync()
    {
        if (!CanPageNext) return;
        PageIndex = _pageIndex + 1;
        await ReloadRowsAsync().ConfigureAwait(false);
    }

    /// <summary>Step to the previous page of rows, if any.</summary>
    public async Task GoToPreviousPageAsync()
    {
        if (!CanPagePrev) return;
        PageIndex = _pageIndex - 1;
        await ReloadRowsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sort the row grid by <paramref name="columnName"/>. Clicking the same
    /// column twice toggles direction; clicking a different column resets to
    /// ascending. Sort is server-side (ORDER BY runs in SQLite) so the page
    /// is the correctly-ordered window — not just an in-memory shuffle of the
    /// rows the current page happens to hold.
    /// </summary>
    public async Task SortByColumnAsync(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return;
        if (string.Equals(_sortColumn, columnName, StringComparison.Ordinal))
        {
            SortDescending = !_sortDescending;
        }
        else
        {
            SortColumn     = columnName;
            SortDescending = false;
        }
        // Reset to first page on every sort change — keeping the user on
        // page 3 when the order shifted underneath is more confusing than
        // useful, and it matches every well-known data grid (Excel, DBeaver).
        PageIndex = 0;
        await ReloadRowsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Build the +Row seed map for the current column set. Defaults stay
    /// string-typed (the storage is sqlite_text-backed via DB.cs's identifier
    /// guards) but match the inline editor's parse gate so the user can edit
    /// the cell without first having to dismiss a validation error.
    /// </summary>
    private Dictionary<string, string?> BuildSeedDefaultsForCurrentColumns()
    {
        var values = new Dictionary<string, string?>(VisibleColumns.Count);
        foreach (var c in VisibleColumns) values[c.Name] = SeedDefaultForSqlType(c.SqlType);
        return values;
    }

    /// <summary>
    /// Type-aware seed default. INTEGER / BOOLEAN columns get <c>0</c>, REAL
    /// gets <c>0.0</c>, TEXT and anything else get the empty string. Public
    /// for tests + so the view can preview the value in any future "create
    /// with defaults" dialog.
    /// </summary>
    public static string SeedDefaultForSqlType(string? sqlType)
    {
        if (string.IsNullOrEmpty(sqlType)) return string.Empty;
        var t = sqlType.ToUpperInvariant();
        if (t.Contains("BOOL")) return "0";
        if (t.Contains("INT"))  return "0";
        if (t.Contains("REAL") || t.Contains("FLOAT") || t.Contains("DOUBLE") || t.Contains("NUMERIC"))
            return "0.0";
        return string.Empty;
    }

    private async Task RefreshAfterMutationAsync()
    {
        // Selective single-table refresh.
        // Pre-fix this re-listed EVERY table — including the read-only system
        // tables (Vars / EventLog / SystemHistory / paired-device) — and
        // re-counted rows + columns for each, on every cell edit / insert /
        // delete. On a databank with many tables that multiplied the latency
        // of each edit for no benefit (only the mutated table's counts can
        // have changed). When the backing source supports it, refresh just the
        // currently-selected table's TableInfo and splice it into the sidebar
        // projections in place. Fall back to the full list refresh for
        // sources that don't expose the selective path (test fixtures).
        var table = _selectedTable;
        if (table is not null && _source is DbRelationalSource dbSource)
        {
            var fresh = await dbSource.RefreshTableInfoAsync(table.Name).ConfigureAwait(false);
            if (fresh is not null)
            {
                ReplaceTableInfo(table.Name, fresh);
                return;
            }
        }
        // Re-pull the full table aggregate (row/col counts in the sidebar) so
        // post-mutation totals stay accurate for non-DbRelationalSource backings.
        await ReloadTablesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Replace a single table's <see cref="TableInfo"/>
    /// in the backing list + visible projections after a selective refresh,
    /// preserving sidebar order and the current selection. Keeps the cheap
    /// per-mutation path from churning the whole table list.
    /// </summary>
    private void ReplaceTableInfo(string tableName, TableInfo fresh)
    {
        for (int i = 0; i < _allTables.Count; i++)
        {
            if (string.Equals(_allTables[i].Name, tableName, StringComparison.OrdinalIgnoreCase))
            {
                _allTables[i] = fresh;
                break;
            }
        }
        // Point _selectedTable at the refreshed record (value-different from the
        // old one because the row/column counts changed) BEFORE rebuilding the
        // projections, so the new instance is the one added to the bound lists.
        // Assign the field directly rather than via the setter so we DON'T
        // trigger the setter's full row reload (which would scroll-jump + clear
        // the edit the user just made — the whole reason this selective path
        // exists).
        bool selectionRefreshed = false;
        if (_selectedTable is not null &&
            string.Equals(_selectedTable.Name, tableName, StringComparison.OrdinalIgnoreCase))
        {
            _selectedTable = fresh;
            selectionRefreshed = true;
        }

        // RebuildTableListProjections clears + repopulates the bound table lists
        // (now containing `fresh`), which drops the ListView's SelectedItem.
        RebuildTableListProjections();

        if (selectionRefreshed)
        {
            // Re-announce SelectedTable so the View's SyncSelectionFromVm
            // re-highlights the row — the list now holds `fresh`, so the
            // value-equality selection match succeeds. SelectedTableHeader
            // (· N rows · M columns) also refreshes off the new counts.
            OnPropertyChanged(nameof(SelectedTable));
            OnPropertyChanged(nameof(SelectedTableHeader));
        }
    }

    /// <summary>
    /// Release the per-instance resources the
    /// pre-fix VM leaked when the Databank tab closed or the user navigated
    /// away: the in-flight load <see cref="CancellationTokenSource"/> (each
    /// holds a WaitHandle kernel resource) and the filter-debounce
    /// <see cref="Microsoft.UI.Dispatching.DispatcherQueueTimer"/>. The View
    /// calls this from its Unloaded handler so cleanup runs even when a load
    /// is still pending.
    ///
    /// Architect caches BOTH the view and this VM and re-shows them on every
    /// Databank tab visit, so this release is intentionally non-terminal: it
    /// frees the leaked handles but the VM re-arms transparently on next use
    /// (a table selection re-allocates the CTS via <see cref="ReloadRowsAsync"/>;
    /// a filter keystroke lazily re-creates the timer via
    /// <see cref="RequestFilterApply"/>). Idempotent — safe to call repeatedly.
    /// </summary>
    public void Dispose()
    {
        if (_released) return;
        _released = true;

        // Stop the filter-debounce timer so a pending tick can't fire against a
        // torn-down view, then drop the reference so it's GC-eligible. A fresh
        // timer is created lazily on the next filter keystroke after reuse.
        // DispatcherQueueTimer is not IDisposable; stopping + dropping the
        // reference is the full release.
        var timer = _filterDebounce;
        _filterDebounce = null;
        if (timer is not null)
        {
            try { timer.Stop(); } catch { /* best effort */ }
        }

        // Cancel + dispose any in-flight load CTS. Without this, a load left
        // pending at teardown leaked its CTS (and its WaitHandle) — under
        // rapid tab navigation that accreted handles, especially when
        // Architect runs in-process on Hub's UI thread. ReloadRowsAsync
        // allocates a fresh CTS on the next reload after reuse.
        var cts = _loadCts;
        _loadCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { /* already cancelled / disposed */ }
            cts.Dispose();
        }
    }
}

/// <summary>
/// Cell-by-cell projection of a single row. Holds the raw string values
/// keyed by column name so the view can do `{Binding [Name]}` (or, more
/// pragmatically, an ItemsControl over Cells in column order). Carries the
/// row's <c>rowid</c> so cell-edit / delete-row commands can identify it
/// without re-querying.
/// </summary>
public sealed class DatabankRowViewModel
{
    public DatabankRowViewModel(
        int                       rowIndex,
        long                      rowId,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<string?>    rawCells)
    {
        RowIndex = rowIndex;
        RowId    = rowId;
        var pairs = new List<DatabankCellViewModel>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            pairs.Add(new DatabankCellViewModel(
                column:  col.Name,
                value:   rawCells.Count > i ? rawCells[i] : null,
                sqlType: col.SqlType));
        }
        Cells = pairs;
    }

    public int                                  RowIndex { get; }
    public long                                 RowId    { get; }
    public IReadOnlyList<DatabankCellViewModel> Cells    { get; }

    /// <summary>Look up a cell value by column name; returns null when missing.</summary>
    public string? this[string columnName]
    {
        get
        {
            foreach (var c in Cells) if (c.Column == columnName) return c.Value;
            return null;
        }
    }

    /// <summary>
    /// Mirror an in-place cell edit so the grid stays consistent without a
    /// full reload. <see cref="DatabankCellViewModel"/> raises PropertyChanged
    /// on its <c>Value</c> setter so the bound TextBlock refreshes.
    /// </summary>
    public void ApplyLocalEdit(string columnName, string? newValue)
    {
        foreach (var c in Cells)
        {
            if (c.Column == columnName) { c.Value = newValue; return; }
        }
    }
}

/// <summary>
/// One cell of a row. The Value setter raises PropertyChanged so an inline
/// optimistic edit (the user double-clicks a cell, types, presses Enter)
/// updates the bound TextBlock without waiting for a full snapshot reload.
/// </summary>
/// <summary>
/// Type-aware editor selector for the inline cell-edit overlay. Pre-fix the
/// browser opened a generic modal TextBox for every cell regardless of column
/// SqlType, so BOOLEAN columns made users type the literal string "true" and
/// INTEGER columns accepted any text including garbage.
/// </summary>
public enum DatabankCellEditor { Text, Bool, Integer, Real }

public sealed class DatabankCellViewModel : ObservableObject
{
    private string? _value;
    private bool _isEditing;
    // Pre-edit value captured per cell when this
    // cell enters edit mode. Pre-fix the baseline lived as a single view-level
    // field, so double-tapping a second cell before the first committed
    // overwrote the first cell's baseline — an Esc/validation rollback on the
    // first cell then restored the SECOND cell's value. Holding the baseline on
    // each cell's own state machine isolates rollbacks to the right cell.
    private string? _editBaselineValue;

    public DatabankCellViewModel(string column, string? value, string? sqlType = null)
    {
        Column  = column;
        SqlType = sqlType ?? string.Empty;
        Editor  = ResolveEditor(SqlType);
        _value  = value;
    }

    public string  Column  { get; }
    public string  SqlType { get; }

    /// <summary>Which inline editor the cell template should show in IsEditing mode.</summary>
    public DatabankCellEditor Editor { get; }

    public bool IsBoolEditor    => Editor == DatabankCellEditor.Bool;
    public bool IsIntegerEditor => Editor == DatabankCellEditor.Integer;
    public bool IsRealEditor    => Editor == DatabankCellEditor.Real;
    public bool IsTextEditor    => Editor == DatabankCellEditor.Text;

    // IsEditing × Editor-flag composites — let the XAML stay on single-bool
    // visibility converters (WinUI 3's MultiBinding + custom converter combo
    // is finicky in the row template). Re-fire on IsEditing change.
    public bool ShowTextEditor    => _isEditing && IsTextEditor;
    public bool ShowBoolEditor    => _isEditing && IsBoolEditor;
    public bool ShowIntegerEditor => _isEditing && IsIntegerEditor;
    public bool ShowRealEditor    => _isEditing && IsRealEditor;
    public bool ShowReadOnly      => !_isEditing;

    public string? Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
                OnPropertyChanged(nameof(BoolValue));
        }
    }

    /// <summary>Two-way bool projection for the inline CheckBox editor — round-trips
    /// the string value through "true"/"false" so the underlying storage stays string-typed.</summary>
    public bool BoolValue
    {
        get => string.Equals(_value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_value, "1",    StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    /// <summary>
    /// The value this cell held the instant it
    /// entered edit mode. Captured in the <see cref="IsEditing"/> setter on the
    /// false→true transition (before the user types anything) and consumed by
    /// the view's rollback paths (Esc, parse-validation failure, persist
    /// failure). Per-cell so concurrent inline edits never cross-contaminate.
    /// </summary>
    internal string? EditBaselineValue => _editBaselineValue;

    /// <summary>True while the inline editor is showing — flipped by
    /// <c>DatabankBrowserView.OnCellDoubleTapped</c>. Cleared on commit / Esc.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetField(ref _isEditing, value))
            {
                // Snapshot the pre-edit value on
                // the transition INTO edit mode. _value is still the committed
                // value here (double-tap flips IsEditing before any keystroke),
                // so this captures the true baseline for this cell alone.
                if (value) _editBaselineValue = _value;
                // Re-fire the composite gates so the XAML visibility flips.
                OnPropertyChanged(nameof(ShowTextEditor));
                OnPropertyChanged(nameof(ShowBoolEditor));
                OnPropertyChanged(nameof(ShowIntegerEditor));
                OnPropertyChanged(nameof(ShowRealEditor));
                OnPropertyChanged(nameof(ShowReadOnly));
            }
        }
    }

    private static DatabankCellEditor ResolveEditor(string sqlType)
    {
        if (string.IsNullOrEmpty(sqlType)) return DatabankCellEditor.Text;
        var t = sqlType.ToUpperInvariant();
        if (t.Contains("BOOL")) return DatabankCellEditor.Bool;
        if (t.Contains("INT"))  return DatabankCellEditor.Integer;
        if (t.Contains("REAL") || t.Contains("FLOAT") || t.Contains("DOUBLE") || t.Contains("NUMERIC"))
            return DatabankCellEditor.Real;
        return DatabankCellEditor.Text;
    }
}
