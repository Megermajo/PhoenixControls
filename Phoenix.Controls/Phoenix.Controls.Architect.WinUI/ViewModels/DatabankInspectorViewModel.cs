using System.Collections.Generic;
using System.Collections.ObjectModel;
using Phoenix.Controls.Architect.WinUI.Databank;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.ViewModels;

// View-model for the right-hand inspector while the Databank Browser tab is
// active. Mirrors architect.jsx:DatabankInspector — the selected row's
// columns rendered as mono Field rows (the design example shows
// Name / Points / SubMonths / LastSeen for one Users row).
//
// Selection is pushed in by ArchitectViewModel whenever the underlying
// DatabankBrowserViewModel raises SelectionChanged. We rebuild Fields
// from the row's cells; Track 5's XAML inspector binds directly. The
// Schema collection mirrors the per-table column schema (PRAGMA
// table_info) so the inspector's Schema toggle has data to render —
// ArchitectViewModel pushes it in via SetSchema whenever the browser
// raises SchemaChanged.
public sealed class DatabankInspectorViewModel : ObservableObject
{
    private string _displayTitle = string.Empty;
    private string _eyebrow      = "Inspector";

    public DatabankInspectorViewModel()
    {
        Fields = new ObservableCollection<InspectorFieldViewModel>();
        Schema = new ObservableCollection<ColumnSchemaInfo>();
    }

    public string DisplayTitle
    {
        get => _displayTitle;
        private set => SetField(ref _displayTitle, value);
    }

    public string Eyebrow
    {
        get => _eyebrow;
        private set => SetField(ref _eyebrow, value);
    }

    public ObservableCollection<InspectorFieldViewModel> Fields { get; }

    /// <summary>Per-column schema for the table that backs the currently-shown
    /// row. Filled by <see cref="SetSchema"/>; the inspector's Schema toggle
    /// in the XAML binds straight to this collection.</summary>
    public ObservableCollection<ColumnSchemaInfo> Schema { get; }

    /// <summary>
    /// Persistence delegate handed in by ArchitectViewModel — invoked when
    /// the user double-tap-edits an inspector field and commits. Pre-fix
    /// (QC19-02) the inspector's Value binding was OneTime against a
    /// getter-only VM property, so edits committed visually but were
    /// silently dropped. The delegate now flows through each field's
    /// <see cref="InspectorFieldViewModel.WriteBackText"/> so the same
    /// IRelationalSource path the row grid uses persists inspector edits
    /// too. Null when no row is selected.
    /// </summary>
    private System.Func<DatabankRowViewModel, string, string?, System.Threading.Tasks.Task>? _persistCell;

    /// <summary>Set the inspector to the chosen row of the chosen table.</summary>
    /// <param name="tableName">Table name for the eyebrow caption (nullable).</param>
    /// <param name="row">Selected row VM, or null to clear the inspector.</param>
    /// <param name="persistCell">
    /// Optional persistence delegate — invoked as <c>persistCell(row, columnName, newValue)</c>
    /// when an inspector field commits a text edit. ArchitectViewModel
    /// wires this to <c>DatabankBrowserViewModel.UpdateCellAsync</c> so the
    /// same write path the row grid uses persists inspector edits too.
    /// </param>
    public void SetRow(
        string?                 tableName,
        DatabankRowViewModel?   row,
        System.Func<DatabankRowViewModel, string, string?, System.Threading.Tasks.Task>? persistCell = null,
        System.Collections.Generic.ICollection<DatabankRowViewModel>? liveRows = null)
    {
        Fields.Clear();
        _persistCell = persistCell;
        if (row is null)
        {
            DisplayTitle = string.Empty;
            Eyebrow      = "Inspector";
            return;
        }

        DisplayTitle = string.IsNullOrEmpty(tableName)
            ? $"row {row.RowIndex + 1}"
            : $"{tableName} · row {row.RowIndex + 1}";
        Eyebrow = "Row";

        // Capture locals for the WriteBackText closures so each field's
        // commit path persists against the correct row + column even after
        // the user reselects a different row mid-edit (the captured row
        // identifies the target rowid; the persist delegate handles the
        // actual snapshot mirror + DB write).
        var capturedRow = row;
        foreach (var c in row.Cells)
        {
            string columnName = c.Column;
            Fields.Add(new InspectorFieldViewModel(
                columnName,
                c.Value ?? string.Empty,
                InspectorFieldKind.MonoText,
                writeBackText: candidate =>
                {
                    // No persist delegate wired — accept the local edit
                    // (the cached VM value updates) but don't reach the
                    // DB. Surfacing this as a silent log keeps with
                    // feedback_no_modal_dialogs_for_repeatable_rejections.
                    if (_persistCell is null)
                    {
                        Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                            $"Inspector edit not persisted: no relational sink wired for column '{columnName}'.",
                            "DatabankInspectorViewModel",
                            Phoenix.Controls.Shared.Models.LogLevel.System);
                        return true;
                    }

                    // Mirror the optimistic cell value into the row VM so
                    // the row grid stays in sync without a full snapshot
                    // reload, then hand off to the persistence delegate.
                    //
                    // Stale-row guard (P1): the closure captured `capturedRow`
                    // at SetRow time. If the user reselected a different row or
                    // the table reloaded before this async commit fires,
                    // `capturedRow` is a discarded VM no longer in the live
                    // VisibleRows (the same ObservableCollection instance the
                    // browser repopulates in place). Mirroring into it then
                    // corrupts the wrong (orphaned) row while the DB persist
                    // still targets the correct rowid.
                    //
                    // Concurrency note (P2): when a live collection IS supplied
                    // the `Contains` check and a subsequent ApplyLocalEdit would
                    // not be atomic — DatabankBrowserViewModel can run
                    // VisibleRows.Clear()/rebuild on another thread in between,
                    // orphaning `capturedRow` after a passing Contains check and
                    // mirroring into a row no longer bound to the grid. So we no
                    // longer mirror on the live-collection path; the DB persist
                    // below targets the correct rowid and the next snapshot
                    // reload repaints the grid. Only the test/standalone path
                    // (no live collection — no concurrent rebuild possible)
                    // keeps the optimistic mirror so that wiring never regresses.
                    if (liveRows is null)
                    {
                        capturedRow.ApplyLocalEdit(columnName, candidate);
                    }

                    // Fire-and-forget through AsyncErrorBoundary — a faulted
                    // DB write surfaces through GlobalLogger instead of an
                    // unobserved task exception. The local edit + cached
                    // value have already updated; if the write fails, the
                    // next snapshot reload restores truth.
                    var persist = _persistCell;
                    _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                        () => persist(capturedRow, columnName, candidate),
                        "DatabankInspectorViewModel",
                        $"PersistCellEdit({columnName})");
                    return true;
                }));
        }
    }

    /// <summary>
    /// Replace the schema column list with <paramref name="rows"/>. Pushed in
    /// by ArchitectViewModel whenever the browser's SelectedTable changes (so
    /// the inspector's Schema tab always matches the table the user is
    /// looking at).
    /// </summary>
    public void SetSchema(IReadOnlyList<ColumnSchemaInfo>? rows)
    {
        Schema.Clear();
        if (rows is null) return;
        foreach (var r in rows) Schema.Add(r);
    }
}
