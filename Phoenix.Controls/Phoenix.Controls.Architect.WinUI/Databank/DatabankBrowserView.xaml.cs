using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.WinUI.Databank.Contracts;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Phoenix.Controls.Architect.WinUI.Databank;

// View for the Databank Browser tab — wraps DatabankBrowserViewModel as
// DataContext and forwards row/table-selection clicks back to the VM.
// Closes the "Databank tab is a literal blank" bug — Architect's MainView
// swaps this in when the Databank top-tab is active.
//
// Edit surface: toolbar buttons (+ Table / + Column / + Row / Delete Row)
// and inline cell editing via double-tap → ContentDialog. Cell edits
// route through DatabankBrowserViewModel.UpdateCellAsync, which calls the
// IRelationalSource and mirrors the new value into the row VM so the
// grid updates without a full snapshot reload.
public sealed partial class DatabankBrowserView : UserControl
{
    private DatabankBrowserViewModel? _vm;
    private bool _syncingSelection;
    private readonly List<TextBlock> _sortIndicators = new();

    public DatabankBrowserView()
    {
        // 0.10.2: guard the XAML parse. If a theme resource or {Binding}
        // converter is missing at parse time, the ctor would otherwise
        // propagate an unhandled exception that crashed the Architect
        // pillar when the Databank tab was clicked. Log + rethrow so the
        // pillar host (MainView.ApplyTab) can downgrade to a placeholder
        // rather than kill the whole window.
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "DatabankBrowserView",
                "InitializeComponent threw — Databank tab will show a placeholder.",
                ex);
            throw;
        }
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_vm is { } old) old.PropertyChanged -= OnVmPropertyChanged;
        _vm = args.NewValue as DatabankBrowserViewModel;
        if (_vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            SyncSelectionFromVm();
            SyncLoadingHint();
            SyncToolbarEnableStates();
            RefreshSortIndicators();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
            // Dispose the VM on unload so its
            // in-flight load CancellationTokenSource and filter-debounce
            // DispatcherQueueTimer are released even when a load is still
            // pending. Dispose() is idempotent and guards its own re-entry.
            old.Dispose();
            _vm = null;
        }
        _sortIndicators.Clear();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DatabankBrowserViewModel.SelectedTable):
                SyncSelectionFromVm();
                SyncToolbarEnableStates();
                break;
            case nameof(DatabankBrowserViewModel.SelectedRowIndex):
                SyncRowSelectionFromVm();
                SyncToolbarEnableStates();
                break;
            case nameof(DatabankBrowserViewModel.IsLoadingRows):
                SyncLoadingHint();
                break;
            case nameof(DatabankBrowserViewModel.HasSelectedTable):
            case nameof(DatabankBrowserViewModel.HasSelectedRow):
            case nameof(DatabankBrowserViewModel.IsSystemTableSelected):
            case nameof(DatabankBrowserViewModel.CanMutateSelectedTable):
                SyncToolbarEnableStates();
                break;
            case nameof(DatabankBrowserViewModel.SortColumn):
            case nameof(DatabankBrowserViewModel.SortDescending):
                RefreshSortIndicators();
                break;
        }
    }

    private void SyncSelectionFromVm()
    {
        if (_vm is null) return;
        _syncingSelection = true;
        try
        {
            var sel = _vm.SelectedTable;
            if (sel is null)
            {
                if (SystemTableList is not null) SystemTableList.SelectedItem = null;
                if (UserTableList   is not null) UserTableList.SelectedItem   = null;
                return;
            }
            // Push the selection into whichever group owns it; clear the other
            // so the visual single-select reads as one row across both lists.
            if (sel.IsSystem)
            {
                if (UserTableList   is not null) UserTableList.SelectedItem   = null;
                if (SystemTableList is not null) SystemTableList.SelectedItem = sel;
            }
            else
            {
                if (SystemTableList is not null) SystemTableList.SelectedItem = null;
                if (UserTableList   is not null) UserTableList.SelectedItem   = sel;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SyncRowSelectionFromVm()
    {
        if (_vm is null) return;
        if (RowList.SelectedIndex == _vm.SelectedRowIndex) return;
        RowList.SelectedIndex = _vm.SelectedRowIndex;
    }

    private void SyncLoadingHint()
    {
        if (_vm is null) return;
        LoadingHint.Visibility = _vm.IsLoadingRows ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Gate the destructive toolbar buttons on CanMutateSelectedTable so
    /// selecting a system row (Vars / EventLog / SystemHistory / paired-device
    /// table) visibly disables + Row / + Column / Delete Row. + Table stays
    /// enabled at all times because creating a new user table is never tied
    /// to the current selection.
    /// </summary>
    private void SyncToolbarEnableStates()
    {
        if (_vm is null) return;
        bool canMutate = _vm.CanMutateSelectedTable;
        bool canDelRow = canMutate && _vm.HasSelectedRow;
        if (NewRowButton    is not null) NewRowButton.IsEnabled    = canMutate;
        if (NewColumnButton is not null) NewColumnButton.IsEnabled = canMutate;
        if (DeleteRowButton is not null) DeleteRowButton.IsEnabled = canDelRow;
    }

    private void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _syncingSelection) return;
        if (sender is not ListView list) return;
        if (list.SelectedItem is TableInfo info)
        {
            // Single-select across two lists — clear the OTHER list so the
            // visual selection reads as one row across both. Guard via
            // _syncingSelection so this doesn't ping-pong with SyncSelectionFromVm.
            _syncingSelection = true;
            try
            {
                if (ReferenceEquals(list, SystemTableList) && UserTableList is not null)
                    UserTableList.SelectedItem = null;
                else if (ReferenceEquals(list, UserTableList) && SystemTableList is not null)
                    SystemTableList.SelectedItem = null;
            }
            finally
            {
                _syncingSelection = false;
            }
            if (!info.Equals(_vm.SelectedTable))
                _vm.SelectedTable = info;
        }
    }

    /// <summary>
    /// Drag-source for table-list rows — packages the table name as
    /// <c>phx-databank-table</c> so the canvas's drag-drop partial can
    /// spawn a DB.RowCount node bound to it.
    /// UE Blueprints lets the user drag a content-browser asset onto the
    /// graph to spawn a pre-wired node — this is the Databank equivalent.
    /// </summary>
    private void OnTableDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items is null || e.Items.Count == 0) return;
        if (e.Items[0] is not TableInfo table || string.IsNullOrEmpty(table.Name)) return;
        e.Data.Properties["phx-databank-table"] = table.Name;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetText($"databank-table:{table.Name}");
    }

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SelectedRowIndex = RowList.SelectedIndex;
    }

    // Keep the column header strip horizontally aligned with the row grid as
    // the user scrolls the row scroll-viewer. The header is its own
    // ScrollViewer so the divider line stays anchored under the column row;
    // we just mirror the offset on every view-change.
    private void OnRowScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer sv && HeaderScroll is not null)
            HeaderScroll.ChangeView(sv.HorizontalOffset, null, null, disableAnimation: true);
    }

    // ─── Toolbar handlers ───────────────────────────────────────────────

    private async void OnNewTableClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            var (name, columns) = await PromptNewTableAsync();
            if (string.IsNullOrWhiteSpace(name) || columns is null || columns.Count == 0) return;
            await _vm.CreateTableAsync(name, columns);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnNewTableClick", ex);
        }
    }

    private async void OnNewRowClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            // Pre-fill dialog restored. Pre-fix this
            // called InsertBlankRowAsync, dropping a row with seed defaults and
            // forcing the user to double-tap each cell individually. The
            // baseline WinForms AddRowDialog showed all columns at once for
            // batch entry; PromptAddRowAsync rebuilds that with type-aware
            // fields + Tab navigation, then inserts the entered values in one go.
            var values = await PromptAddRowAsync();
            if (values is null) return; // user cancelled
            await _vm.InsertRowAsync(values);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnNewRowClick", ex);
        }
    }

    private async void OnNewColumnClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            var (name, type) = await PromptNewColumnAsync();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) return;
            await _vm.AddColumnAsync(name, type);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnNewColumnClick", ex);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            await _vm.ReloadTablesAsync();
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnRefreshClick", ex);
        }
    }

    // ─── Sidebar Open / Delete / Export bar (mirrors right-click) ───────
    // These buttons are the clickable mirror of the table-list context menu
    // so users who don't think to right-click the sidebar still discover the
    // table-level operations. They share the same code paths.

    private void OnOpenTableClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        // "Open" is conceptually a focus operation — the table is already
        // selected, but the row grid may not have been focused since the
        // user clicked between panels. Move keyboard focus into the row list
        // so arrow-key navigation works. The selection itself is a no-op.
        try
        {
            RowList?.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnOpenTableClick", ex);
        }
    }

    private async void OnDeleteTableClick(object sender, RoutedEventArgs e)
    {
        // Top-level guard on the async void so a
        // fault after the await can't escape to the dispatcher (the callee is
        // guarded today, but a top-level catch is the durable safety net).
        try
        {
            if (_vm?.SelectedTable is { } table)
                await ConfirmAndDropTableAsync(table);
        }
        catch (Exception ex) { Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnDeleteTableClick", ex); }
    }

    private async void OnExportTableClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm?.SelectedTable is { } table)
                await ExportTableAsCsvAsync(table);
        }
        catch (Exception ex) { Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnExportTableClick", ex); }
    }

    // ─── Paging / sort / context menu / InfoBar handlers ────────────────

    private async void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            await _vm.GoToPreviousPageAsync();
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnPrevPageClick", ex);
        }
    }

    private async void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try
        {
            await _vm.GoToNextPageAsync();
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnNextPageClick", ex);
        }
    }

    /// <summary>Click on a column header sorts by that column. Tag carries the
    /// ColumnInfo from the binding.</summary>
    private async void OnColumnHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        if ((sender as FrameworkElement)?.Tag is not ColumnInfo col) return;
        try
        {
            await _vm.SortByColumnAsync(col.Name);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnColumnHeaderTapped", ex);
        }
    }

    /// <summary>
    /// Drag source for column headers. Packages the
    /// <c>phx-databank-column={tableName}|{columnName}|{columnType}</c>
    /// payload so the canvas drag-drop partial can open the
    /// Get / Set / Increment / FindRow / GetColumn picker pre-bound to
    /// this column. Pre-T15 <c>MainForm.cs:865-920</c> had the same
    /// gesture; the WinUI rewrite gives it parity.
    /// </summary>
    private void OnColumnHeaderDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (_vm is null) return;
        if ((sender as FrameworkElement)?.Tag is not ColumnInfo col)
        {
            // Drop the drag if the binding isn't a ColumnInfo (defensive —
            // would only happen if the XAML template diverges from this
            // handler's contract).
            e.Cancel = true;
            return;
        }
        string? tableName = _vm.SelectedTable?.Name;
        string payload = BuildDatabankColumnDragPayload(tableName, col);
        if (string.IsNullOrEmpty(payload))
        {
            e.Cancel = true;
            return;
        }
        e.Data.Properties["phx-databank-column"] = payload;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetText($"databank-column:{payload}");
    }

    /// <summary>
    /// Right-click context menu for column headers.
    /// Originally only routed to the canvas spawn picker; later expanded
    /// to host the column-level schema mutations (Rename / Change Type /
    /// Delete) at the top of the flyout, with the canvas spawn picker
    /// nested under a "Spawn graph node…" item so both gestures stay
    /// reachable.
    ///
    /// System-table + primary-key rejections gray the items out rather
    /// than popping a modal. The three confirmation dialogs the user
    /// clicks through ARE user-initiated and confirmation-shaped, which
    /// is the carve-out.
    /// </summary>
    private void OnColumnHeaderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not FrameworkElement header) return;
        if (header.Tag is not ColumnInfo col) return;
        string? tableName = _vm.SelectedTable?.Name;
        if (string.IsNullOrEmpty(tableName))
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                "databank-column.right-click.no-table",
                "DatabankBrowserView",
                Phoenix.Controls.Shared.Models.LogLevel.System);
            e.Handled = true;
            return;
        }

        bool isSystem = _vm.IsSystemTableSelected;
        bool isPk     = IsPrimaryKeyColumn(col.Name);
        bool canMutate = !isSystem && !isPk;

        try
        {
            var flyout = new MenuFlyout();

            var rename = new MenuFlyoutItem
            {
                Text      = Localizer.T("architect.databank.context.rename_column", "Rename column…"),
                IsEnabled = canMutate,
            };
            rename.Click += async (_, _) => await PromptAndRenameColumnAsync(col);
            flyout.Items.Add(rename);

            var changeType = new MenuFlyoutItem
            {
                Text      = Localizer.T("architect.databank.context.change_column_type", "Change type…"),
                IsEnabled = canMutate,
            };
            changeType.Click += async (_, _) => await PromptAndChangeColumnTypeAsync(col);
            flyout.Items.Add(changeType);

            var delete = new MenuFlyoutItem
            {
                Text      = Localizer.T("architect.databank.context.delete_column", "Delete column…"),
                IsEnabled = canMutate,
            };
            delete.Click += async (_, _) => await ConfirmAndDropColumnAsync(col);
            flyout.Items.Add(delete);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // Preserve the canvas spawn-picker entry point so
            // the historic gesture (drag a column header onto the canvas
            // OR right-click → Spawn) keeps working. We route into the
            // canvas's shared menu builder when one is reachable; if not,
            // the item is greyed out (Databank tab open in a future
            // standalone shell, headless test).
            var canvas = FindSiblingLogicCanvasView();
            var spawn = new MenuFlyoutItem
            {
                Text      = Localizer.T("architect.databank.context.spawn_graph_node", "Spawn graph node…"),
                IsEnabled = canvas is not null,
            };
            if (canvas is not null)
            {
                // Position the spawn picker relative to the header so the
                // user's eye lands on the new flyout without a jump. The
                // canvas-side builder fans the menu out via ShowAt(anchor, pos).
                var anchor = header;
                var pos = e.GetPosition(header);
                spawn.Click += (_, _) =>
                {
                    try
                    {
                        canvas.ShowDatabankColumnSpawnMenuFromShell(
                            tableName!,
                            col.Name,
                            col.SqlType ?? string.Empty,
                            anchor:    anchor,
                            anchorPos: pos);
                    }
                    catch (Exception ex)
                    {
                        Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                            "DatabankBrowserView", "Spawn graph node sub-menu", ex);
                    }
                };
            }
            flyout.Items.Add(spawn);

            flyout.ShowAt(header, e.GetPosition(header));
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "DatabankBrowserView", "OnColumnHeaderRightTapped", ex);
        }
        e.Handled = true;
    }

    /// <summary>
    /// True when <paramref name="columnName"/> matches a primary-key
    /// column in the selected table's schema snapshot. Used to grey-out
    /// the column-mutation menu items rather than letting the user click
    /// through to a banner-error (per the no-modal-rejections rule).
    /// </summary>
    private bool IsPrimaryKeyColumn(string columnName)
    {
        if (_vm is null) return false;
        foreach (var s in _vm.Schema)
        {
            if (string.Equals(s.Name, columnName, StringComparison.OrdinalIgnoreCase))
                return s.PrimaryKey;
        }
        return false;
    }

    /// <summary>
    /// Confirmation dialog before dropping a column. The wording
    /// matches the existing delete-table dialog ("permanently delete X
    /// and all its data") so the destructive-affordance language stays
    /// consistent across the Databank surface.
    /// </summary>
    private async System.Threading.Tasks.Task ConfirmAndDropColumnAsync(ColumnInfo col)
    {
        if (_vm is null || col is null) return;
        try
        {
            var confirm = new ContentDialog
            {
                Title             = Localizer.T("architect.databank.dialog.delete_column.title", "Delete column?"),
                Content           = string.Format(
                    Localizer.T("architect.databank.dialog.delete_column.content_format",
                        "This will permanently delete column '{0}' and all its data."),
                    col.Name),
                PrimaryButtonText = Localizer.T("common.button.delete", "Delete"),
                CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = XamlRoot,
            };
            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;
            await _vm.DropColumnAsync(col.Name);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "DatabankBrowserView", "ConfirmAndDropColumnAsync", ex);
        }
    }

    /// <summary>
    /// Prompt for a new column name and call into the VM. Validation
    /// happens on three layers: this dialog's primary-button click can be
    /// gated by the user pressing Cancel; the VM's IsValidColumnIdentifier
    /// gate surfaces a banner on a malformed name; the DB layer enforces
    /// the reserved-alias + collision rules and logs to the System Log on
    /// rejection.
    /// </summary>
    private async System.Threading.Tasks.Task PromptAndRenameColumnAsync(ColumnInfo col)
    {
        if (_vm is null || col is null) return;
        try
        {
            var nameBox = new TextBox
            {
                PlaceholderText     = "column_name",
                IsSpellCheckEnabled = false,
                Text                = col.Name,
            };
            // Select-all on focus so the user can just start typing the
            // replacement. WinUI doesn't auto-select on focus by default.
            nameBox.Loaded += (_, _) =>
            {
                try { nameBox.SelectAll(); nameBox.Focus(FocusState.Programmatic); }
                catch { /* best effort */ }
            };
            var help = new TextBlock
            {
                Text         = Localizer.T("architect.databank.dialog.rename_column.help",
                    "Letters, digits, and underscores only. Cannot start with a digit. Reserved aliases: rowid, oid, _rowid_."),
                FontSize     = 11,
                Opacity      = 0.75,
                TextWrapping = TextWrapping.Wrap,
            };
            var stack = new StackPanel { Spacing = 6, MinWidth = 360 };
            stack.Children.Add(new TextBlock
            {
                Text     = string.Format(
                    Localizer.T("architect.databank.dialog.rename_column.subtitle_format",
                        "Renaming '{0}'"),
                    col.Name),
                FontSize = 11,
                Opacity  = 0.75,
            });
            stack.Children.Add(nameBox);
            stack.Children.Add(help);

            var dialog = new ContentDialog
            {
                Title             = Localizer.T("architect.databank.dialog.rename_column.title", "Rename column"),
                Content           = stack,
                PrimaryButtonText = Localizer.T("common.button.rename", "Rename"),
                CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = XamlRoot,
            };
            var res = await dialog.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            string newName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(newName)) return;
            if (string.Equals(newName, col.Name, StringComparison.Ordinal)) return;
            await _vm.RenameColumnAsync(col.Name, newName);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "DatabankBrowserView", "PromptAndRenameColumnAsync", ex);
        }
    }

    /// <summary>
    /// Prompt for a new SQLite affinity (TEXT / INTEGER / REAL /
    /// BLOB / NUMERIC) and apply via the VM's ChangeColumnTypeAsync.
    /// Defaults the ComboBox to the column's current type when it matches
    /// one of the offered affinities, else falls back to TEXT.
    /// </summary>
    private async System.Threading.Tasks.Task PromptAndChangeColumnTypeAsync(ColumnInfo col)
    {
        if (_vm is null || col is null) return;
        try
        {
            // SQLite affinity surface — matches DB.ChangeColumnTypeAsync's
            // _allowedColumnAffinities set. Kept in the same order as
            // SQLite's affinity rules table so the most common pick
            // (TEXT) is at the top.
            var affinities = new[] { "TEXT", "INTEGER", "REAL", "BLOB", "NUMERIC" };
            int defaultIdx = 0;
            for (int i = 0; i < affinities.Length; i++)
            {
                if (string.Equals(affinities[i], col.SqlType, StringComparison.OrdinalIgnoreCase))
                {
                    defaultIdx = i;
                    break;
                }
            }
            var typeCombo = new ComboBox
            {
                ItemsSource   = affinities,
                SelectedIndex = defaultIdx,
            };
            var warn = new TextBlock
            {
                Text         = Localizer.T("architect.databank.dialog.change_column_type.warn",
                    "SQLite will coerce existing values on insert into the new affinity. Mismatched rows " +
                    "(e.g. a text label coerced to INTEGER) round-trip as 0."),
                FontSize     = 11,
                Opacity      = 0.75,
                TextWrapping = TextWrapping.Wrap,
            };
            var stack = new StackPanel { Spacing = 6, MinWidth = 360 };
            stack.Children.Add(new TextBlock
            {
                Text     = string.Format(
                    Localizer.T("architect.databank.dialog.change_column_type.subtitle_format",
                        "Changing type of '{0}' (currently {1})"),
                    col.Name, string.IsNullOrEmpty(col.SqlType) ? "TEXT" : col.SqlType),
                FontSize = 11,
                Opacity  = 0.75,
            });
            stack.Children.Add(typeCombo);
            stack.Children.Add(warn);

            var dialog = new ContentDialog
            {
                Title             = Localizer.T("architect.databank.dialog.change_column_type.title", "Change column type"),
                Content           = stack,
                PrimaryButtonText = Localizer.T("common.button.apply", "Apply"),
                CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = XamlRoot,
            };
            var res = await dialog.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            string newType = (typeCombo.SelectedItem as string) ?? string.Empty;
            if (string.IsNullOrEmpty(newType)) return;
            // No-op when the user opens the dialog and picks the same type;
            // skip the DB round-trip rather than churning the WAL for nothing.
            if (string.Equals(newType, col.SqlType, StringComparison.OrdinalIgnoreCase)) return;
            await _vm.ChangeColumnTypeAsync(col.Name, newType);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "DatabankBrowserView", "PromptAndChangeColumnTypeAsync", ex);
        }
    }

    /// <summary>
    /// Shared payload builder for column drag. The drag
    /// path on the canvas side parses the same <c>{table}|{column}|{type}</c>
    /// triplet, so any future tweak to the schema only needs to change two
    /// call sites (this builder and the canvas's parser).
    /// </summary>
    private static string BuildDatabankColumnDragPayload(string? tableName, ColumnInfo col)
    {
        if (col is null) return string.Empty;
        if (string.IsNullOrEmpty(tableName) || string.IsNullOrEmpty(col.Name)) return string.Empty;
        string type = col.SqlType ?? string.Empty;
        return $"{tableName}|{col.Name}|{type}";
    }

    /// <summary>
    /// Locate the sibling <see cref="Canvas.LogicCanvasView"/> hosted by the
    /// same Architect MainView. The Databank tab and the Logic tab are
    /// siblings under MainPaneRegion (a ContentControl) — at any given
    /// moment only one of them is in the live visual tree, but MainView
    /// keeps the canvas cached as a long-lived field so its parent chain
    /// stays intact and is reachable via the shared <see cref="XamlRoot"/>.
    /// We walk up to the root and then descend looking for the canvas; nulls
    /// out cleanly when the host isn't an Architect MainView (e.g. headless
    /// test or a future Databank-only window).
    /// </summary>
    private Canvas.LogicCanvasView? FindSiblingLogicCanvasView()
    {
        // Walk up to find a MainView (or any ancestor that exposes a
        // LogicCanvasView via descendants).
        DependencyObject? walker = this;
        FrameworkElement? rootAncestor = null;
        while (walker is not null)
        {
            walker = VisualTreeHelper.GetParent(walker);
            if (walker is FrameworkElement fe) rootAncestor = fe;
        }
        // Fall back to XamlRoot.Content when the visual-tree walk stops
        // short (Databank is shown via ContentControl.Content swap, so its
        // visual parent chain may not currently include MainView until the
        // user clicks back into Logic).
        if (XamlRoot?.Content is FrameworkElement xroot)
            rootAncestor = xroot;
        if (rootAncestor is null) return null;
        return FindDescendantCanvas(rootAncestor);
    }

    private static Canvas.LogicCanvasView? FindDescendantCanvas(DependencyObject root)
    {
        if (root is Canvas.LogicCanvasView hit) return hit;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindDescendantCanvas(child);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Register each per-column sort glyph so SortColumn / SortDescending
    /// changes on the VM can repaint exactly the right indicators without a
    /// visual-tree walk on every change.</summary>
    private void OnSortIndicatorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        if (!_sortIndicators.Contains(tb)) _sortIndicators.Add(tb);
        tb.Unloaded += OnSortIndicatorUnloaded;
        UpdateSortIndicator(tb);
    }

    private void OnSortIndicatorUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb) _sortIndicators.Remove(tb);
    }

    private void RefreshSortIndicators()
    {
        for (int i = _sortIndicators.Count - 1; i >= 0; i--)
        {
            var tb = _sortIndicators[i];
            if (tb is null) { _sortIndicators.RemoveAt(i); continue; }
            UpdateSortIndicator(tb);
        }
    }

    private void UpdateSortIndicator(TextBlock tb)
    {
        if (_vm is null) { tb.Text = string.Empty; return; }
        if (tb.Tag is not ColumnInfo col) { tb.Text = string.Empty; return; }
        if (!string.Equals(col.Name, _vm.SortColumn, StringComparison.Ordinal))
        {
            tb.Text = string.Empty;
            return;
        }
        tb.Text = _vm.SortDescending ? "▼" : "▲";
    }

    private void OnInfoBarCloseClick(InfoBar sender, object args)
    {
        _vm?.ClearError();
    }

    /// <summary>Right-click context menu on a table-list row. Open just nudges
    /// the selection; Delete / Export are disabled for system tables since the
    /// persistence layer rejects them and a greyed-out item is clearer than a
    /// silent error.</summary>
    private void OnTableListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        if (e.OriginalSource is not FrameworkElement src) return;
        // Walk the visual tree to find the ListViewItem the right-click hit
        // and read its DataContext — the right-click doesn't change
        // ListView.SelectedItem on its own.
        TableInfo? table = null;
        DependencyObject? walker = src;
        while (walker is not null)
        {
            if (walker is FrameworkElement fe && fe.DataContext is TableInfo info)
            {
                table = info;
                break;
            }
            walker = VisualTreeHelper.GetParent(walker);
        }
        if (table is null) return;

        var flyout = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = Localizer.T("common.button.open", "Open") };
        open.Click += (_, _) => { if (_vm is { } vm) vm.SelectedTable = table; };
        flyout.Items.Add(open);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var del = new MenuFlyoutItem
        {
            Text = Localizer.T("architect.databank.context.delete_table", "Delete table…"),
            IsEnabled = !table.IsSystem,
        };
        del.Click += async (_, _) => await ConfirmAndDropTableAsync(table);
        flyout.Items.Add(del);

        var export = new MenuFlyoutItem
        {
            Text = Localizer.T("architect.databank.context.export_csv", "Export as CSV…"),
            // System tables are read-only but legitimate to inspect/export —
            // allow the export so streamers can copy Vars / EventLog into
            // analysis tools without going through SQLite directly.
            IsEnabled = true,
        };
        export.Click += async (_, _) => await ExportTableAsCsvAsync(table);
        flyout.Items.Add(export);

        flyout.ShowAt(src, e.GetPosition(src));
        e.Handled = true;
    }

    private async System.Threading.Tasks.Task ConfirmAndDropTableAsync(TableInfo table)
    {
        if (_vm is null || table is null) return;
        try
        {
            var confirm = new ContentDialog
            {
                Title             = Localizer.T("architect.databank.dialog.delete_table.title", "Delete table?"),
                Content           = string.Format(
                    Localizer.T("architect.databank.dialog.delete_table.content_format",
                        "This will permanently drop '{0}' and every row in it."),
                    table.Name),
                PrimaryButtonText = Localizer.T("common.button.delete", "Delete"),
                CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = XamlRoot,
            };
            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;
            await _vm.DropTableAsync(table);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "ConfirmAndDropTableAsync", ex);
        }
    }

    private async System.Threading.Tasks.Task ExportTableAsCsvAsync(TableInfo table)
    {
        if (_vm is null || table is null) return;
        try
        {
            IntPtr hwnd = TryGetHwnd();
            if (hwnd == IntPtr.Zero)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    "Cannot show file picker: no XamlRoot / AppWindow handle.",
                    "DatabankBrowserView", Phoenix.Controls.Shared.Models.LogLevel.System);
                return;
            }
            string suggested = table.Name + ".csv";
            string? path = CustomFilePicker.PickSaveFile(
                hwnd,
                defaultFolder: null,
                suggestedFileName: suggested,
                filters: new[] { ("CSV (Comma-separated)", ".csv") });
            if (string.IsNullOrEmpty(path)) return;

            await _vm.ExportSnapshotAsync(table, async snapshot =>
            {
                // RFC 4180-ish CSV writer: cells are wrapped in double quotes
                // and internal quotes are doubled. Embedded newlines are
                // preserved inside the quoted field — Excel + LibreOffice
                // both round-trip this without manual import help.
                var sb = new StringBuilder();
                for (int c = 0; c < snapshot.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(CsvField(snapshot.Columns[c].Name));
                }
                sb.Append("\r\n");
                foreach (var row in snapshot.Rows)
                {
                    for (int c = 0; c < snapshot.Columns.Count; c++)
                    {
                        if (c > 0) sb.Append(',');
                        sb.Append(CsvField(c < row.Cells.Count ? row.Cells[c] : null));
                    }
                    sb.Append("\r\n");
                }
                // Async write keeps the UI thread responsive on
                // large CSVs. UTF-8 with BOM matches Excel's expectations.
                await File.WriteAllTextAsync(path, sb.ToString(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "ExportTableAsCsvAsync", ex);
        }
    }

    private static string CsvField(string? value)
    {
        if (value is null) return "\"\"";
        // Doubled internal quotes — RFC 4180.
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private IntPtr TryGetHwnd()
    {
        // WinAppSDK 1.5+ — every UserControl can reach its hosting AppWindow
        // through XamlRoot.ContentIslandEnvironment.AppWindowId. The
        // Win32Interop conversion gives the HWND COM pickers need.
        try
        {
            var root = XamlRoot;
            if (root?.ContentIslandEnvironment is null) return IntPtr.Zero;
            var wid = root.ContentIslandEnvironment.AppWindowId;
            return Microsoft.UI.Win32Interop.GetWindowFromWindowId(wid);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        // Gather the multi-selection (Ctrl/Shift-click drives RowList.SelectedItems,
        // which is now Extended). Fall back to the single anchor row when
        // SelectedItems is empty (e.g. a VM-driven selection). Distinct guards
        // against any duplicate, and the rowid is the stable delete key.
        var rowIds = RowList.SelectedItems
            .OfType<DatabankRowViewModel>()
            .Select(r => r.RowId)
            .Distinct()
            .ToList();
        if (rowIds.Count == 0 && _vm.SelectedRow is { } anchor)
            rowIds.Add(anchor.RowId);
        if (rowIds.Count == 0) return;
        try
        {
            string title = rowIds.Count == 1
                ? Localizer.T("architect.databank.dialog.delete_row.title", "Delete row?")
                : Localizer.T("architect.databank.dialog.delete_rows.title", "Delete rows?");
            string content = rowIds.Count == 1
                ? Localizer.T("architect.databank.dialog.delete_row.content",
                    "This will permanently delete the selected row.")
                : string.Format(
                    Localizer.T("architect.databank.dialog.delete_rows.content_format",
                        "This will permanently delete {0} selected rows."),
                    rowIds.Count);
            var confirm = new ContentDialog
            {
                Title             = title,
                Content           = content,
                PrimaryButtonText = Localizer.T("common.button.delete", "Delete"),
                CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = XamlRoot,
            };
            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;
            await _vm.DeleteRowsAsync(rowIds);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnDeleteRowClick", ex);
        }
    }

    // ─── Inline cell edit ───────────────────────────────────────────────

    // ─── Inline cell edit ───────────────────────────────────────────────
    // Pre-fix every double-tap on a cell opened a modal ContentDialog with
    // a generic TextBox regardless of column SqlType — BOOLEAN columns
    // forced the user to type "true"/"false", INTEGER columns accepted any
    // string. Now we flip Cell.IsEditing → the XAML swaps in a type-aware
    // overlay (CheckBox / numeric TextBox / text TextBox). Commit on Enter
    // or focus-loss validates parse + persists via UpdateCellAsync.

    // The pre-edit baseline used to live here as a
    // single view-level field, which raced when the user double-tapped a second
    // cell before the first edit committed (the second tap clobbered the first
    // cell's baseline). The baseline now lives per-cell on DatabankCellViewModel,
    // captured in its IsEditing setter; the rollback paths below read
    // cell.EditBaselineValue so each cell rolls back to its OWN baseline.

    private void OnCellDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DatabankCellViewModel cell) return;
        // The cell captures its own baseline on the IsEditing false→true
        // transition — no view-level capture needed.
        cell.IsEditing = true;
    }

    private void OnCellEditKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DatabankCellViewModel cell) return;
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitCellEdit(sender, cell);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            // Esc rolls back to THIS cell's own baseline captured at double-tap
            // time.
            cell.Value = cell.EditBaselineValue;
            cell.IsEditing = false;
            e.Handled = true;
        }
    }

    private void OnCellEditLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DatabankCellViewModel cell) return;
        if (!cell.IsEditing) return;
        CommitCellEdit(sender, cell);
    }

    private async void OnCellBoolToggled(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DatabankCellViewModel cell) return;
        // Top-level guard so a fault after the await
        // to PersistCellAsync can't escape this async void handler to the
        // dispatcher (unhandled-exception crash). Matches the pattern on every
        // peer handler (OnDeleteTableClick / OnExportTableClick / etc).
        try
        {
            // Capture this cell's baseline BEFORE clearing IsEditing — the cell
            // VM only holds the baseline while in edit mode.
            string? baseline = cell.EditBaselineValue;
            // CheckBox edits commit immediately — no separate Enter/blur step.
            cell.IsEditing = false;
            await PersistCellAsync(cell, baseline);
        }
        catch (Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error("DatabankBrowserView", "OnCellBoolToggled", ex);
        }
    }

    private async void CommitCellEdit(object sender, DatabankCellViewModel cell)
    {
        if (_vm is null || cell is null) return;
        // Type-aware parse gate — Integer/Real cells reject obviously bad
        // input rather than letting the DB layer fail silently. The numeric
        // TextBox InputScope on the XAML side narrows the keyboard but
        // doesn't strictly enforce; do the final validation here.
        if (cell.IsIntegerEditor && !string.IsNullOrEmpty(cell.Value)
            && !long.TryParse(cell.Value, out _))
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                $"Edit rejected: '{cell.Value}' is not an integer for column {cell.Column}",
                "DatabankBrowserView", Phoenix.Controls.Shared.Models.LogLevel.System);
            cell.Value = cell.EditBaselineValue;
            cell.IsEditing = false;
            return;
        }
        if (cell.IsRealEditor && !string.IsNullOrEmpty(cell.Value)
            && !double.TryParse(cell.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                $"Edit rejected: '{cell.Value}' is not a number for column {cell.Column}",
                "DatabankBrowserView", Phoenix.Controls.Shared.Models.LogLevel.System);
            cell.Value = cell.EditBaselineValue;
            cell.IsEditing = false;
            return;
        }

        // Capture the baseline BEFORE clearing IsEditing — the cell VM only
        // holds the baseline while in edit mode, and PersistCellAsync below
        // needs it for its rollback.
        string? baseline = cell.EditBaselineValue;
        cell.IsEditing = false;
        if (string.Equals(cell.Value ?? string.Empty, baseline ?? string.Empty,
                StringComparison.Ordinal))
            return; // no-op edit
        await PersistCellAsync(cell, baseline);
    }

    /// <summary>
    /// Persist a committed cell edit. <paramref name="baselineValue"/> is this
    /// cell's own pre-edit value (captured before IsEditing was cleared) so a
    /// failed write rolls the cell back to ITS baseline, not whatever a
    /// concurrently-edited sibling cell last left in a shared field.
    /// </summary>
    private async System.Threading.Tasks.Task PersistCellAsync(DatabankCellViewModel cell, string? baselineValue)
    {
        if (_vm is null) return;
        var row = FindRowForCell(cell);
        if (row is null) return;
        var newValue = cell.Value;
        try
        {
            await _vm.UpdateCellAsync(row, cell.Column, newValue);
        }
        catch (Exception ex)
        {
            // Persist failed — roll back the optimistic local edit and
            // surface to System Log so the user knows their change didn't
            // land. Matches pre-fix modal-path rollback behaviour.
            cell.Value = baselineValue;
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "DatabankBrowserView", "PersistCellAsync failed", ex);
        }
    }

    /// <summary>
    /// Locate the row VM whose Cells list contains <paramref name="cell"/>.
    /// Avoids depending on the visual-tree walk that the prior modal path
    /// used (which broke once we moved editing inline since the editor
    /// shares the same row as its TextBlock).
    /// </summary>
    private DatabankRowViewModel? FindRowForCell(DatabankCellViewModel cell)
    {
        if (_vm is null) return null;
        foreach (var r in _vm.VisibleRows)
            foreach (var c in r.Cells)
                if (ReferenceEquals(c, cell)) return r;
        return null;
    }

    // Walks up the visual tree from the cell text-block to the parent
    // row's DatabankRowViewModel. The row VM lives on the inner
    // ItemsControl's DataContext (the outer ListView ItemTemplate's root
    // binding). FrameworkElement.Tag on the inner ItemsControl is also
    // bound to the row in XAML as a fallback.
    private static DatabankRowViewModel? FindAncestorRowVm(DependencyObject start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement fe)
            {
                if (fe.DataContext is DatabankRowViewModel direct) return direct;
                if (fe.Tag is DatabankRowViewModel viaTag) return viaTag;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ─── ContentDialog prompts ──────────────────────────────────────────

    /// <summary>
    /// Batch-entry "Add row" dialog. Dynamically builds
    /// one labelled field per non-rowid column in <see cref="DatabankBrowserViewModel.VisibleColumns"/>,
    /// type-aware: BOOLEAN → true/false ComboBox, INTEGER/REAL → numeric TextBox,
    /// everything else → text TextBox. Seeds each field with the same type-aware
    /// default the VM uses for blank rows so the user can accept-and-go or edit
    /// inline. Returns the column→value map on commit, or null on cancel. The
    /// Vars system table gets dedicated VarKey / VarValue field labels (it's
    /// normally unreachable here since the +Row button disables for system
    /// tables, but the labelling is kept for parity with the WinForms baseline
    /// and defense in depth). Tab navigation between fields is the WinUI default
    /// for the stacked controls; the first field is focused on open.
    /// </summary>
    private async System.Threading.Tasks.Task<IReadOnlyDictionary<string, string?>?> PromptAddRowAsync()
    {
        if (_vm is null) return null;

        // Snapshot the columns up-front — VisibleColumns can mutate if a reload
        // races, and we want a stable field set for the dialog's lifetime.
        var columns = _vm.VisibleColumns
            .Where(c => !string.Equals(c.Name, "rowid", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (columns.Count == 0) return null;

        bool isVars = string.Equals(_vm.SelectedTable?.Name, "Vars", StringComparison.OrdinalIgnoreCase);

        var stack = new StackPanel { Spacing = 8, MinWidth = 360 };
        // Per-column accessors so we can read each field's value back uniformly
        // on commit regardless of which control type rendered it.
        var fieldReaders = new List<(string Column, Func<string?> Read)>(columns.Count);

        foreach (var col in columns)
        {
            string label = col.Name;
            if (isVars)
            {
                if (string.Equals(col.Name, "VarKey", StringComparison.OrdinalIgnoreCase))
                    label = Localizer.T("architect.databank.dialog.add_row.var_key", "VarKey");
                else if (string.Equals(col.Name, "VarValue", StringComparison.OrdinalIgnoreCase))
                    label = Localizer.T("architect.databank.dialog.add_row.var_value", "VarValue");
            }

            stack.Children.Add(new TextBlock
            {
                Text     = $"{label}  ({(string.IsNullOrEmpty(col.SqlType) ? "TEXT" : col.SqlType)})",
                FontSize = 11,
                Opacity  = 0.75,
            });

            string seed = DatabankBrowserViewModel.SeedDefaultForSqlType(col.SqlType);
            var editor = DatabankCellViewModel_ResolveEditorKind(col.SqlType);

            if (editor == DatabankCellEditor.Bool)
            {
                var combo = new ComboBox
                {
                    ItemsSource   = new[] { "false", "true" },
                    SelectedIndex = string.Equals(seed, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                stack.Children.Add(combo);
                fieldReaders.Add((col.Name, () => combo.SelectedItem as string ?? "false"));
            }
            else
            {
                var box = new TextBox
                {
                    Text                = seed,
                    IsSpellCheckEnabled = false,
                    AcceptsReturn       = false,
                };
                if (editor is DatabankCellEditor.Integer or DatabankCellEditor.Real)
                    box.InputScope = new Microsoft.UI.Xaml.Input.InputScope
                    {
                        Names = { new Microsoft.UI.Xaml.Input.InputScopeName(
                            Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) },
                    };
                stack.Children.Add(box);
                fieldReaders.Add((col.Name, () => box.Text));
            }
        }

        // Focus the first input when the dialog opens so the user can Tab through.
        var firstInput = stack.Children.OfType<Control>().FirstOrDefault();
        if (firstInput is not null)
            firstInput.Loaded += (_, _) =>
            {
                try
                {
                    firstInput.Focus(FocusState.Programmatic);
                    if (firstInput is TextBox tb) tb.SelectAll();
                }
                catch { /* best effort */ }
            };

        var scroller = new ScrollViewer
        {
            Content                       = stack,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight                     = 420,
        };

        var dialog = new ContentDialog
        {
            Title             = Localizer.T("architect.databank.dialog.add_row.title", "Add row"),
            Content           = scroller,
            PrimaryButtonText = Localizer.T("common.button.add", "Add"),
            CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot,
        };
        var res = await dialog.ShowAsync();
        if (res != ContentDialogResult.Primary) return null;

        var values = new Dictionary<string, string?>(fieldReaders.Count, StringComparer.Ordinal);
        foreach (var (column, read) in fieldReaders)
            values[column] = read();
        return values;
    }

    /// <summary>
    /// Self-contained mirror of DatabankCellViewModel's
    /// private ResolveEditor so the add-row dialog can pick a type-aware field
    /// without exposing the VM's internal resolver. Kept in lockstep with the
    /// VM's ResolveEditor / SeedDefaultForSqlType type-keyword rules.
    /// </summary>
    private static DatabankCellEditor DatabankCellViewModel_ResolveEditorKind(string? sqlType)
    {
        if (string.IsNullOrEmpty(sqlType)) return DatabankCellEditor.Text;
        var t = sqlType.ToUpperInvariant();
        if (t.Contains("BOOL")) return DatabankCellEditor.Bool;
        if (t.Contains("INT"))  return DatabankCellEditor.Integer;
        if (t.Contains("REAL") || t.Contains("FLOAT") || t.Contains("DOUBLE") || t.Contains("NUMERIC"))
            return DatabankCellEditor.Real;
        return DatabankCellEditor.Text;
    }

    // SQLite affinities offered in the per-column
    // type picker. Mirrors the change-column-type dialog's affinity set so the
    // table-creation and column-edit surfaces stay consistent.
    private static readonly string[] _newTableColumnAffinities =
        { "TEXT", "INTEGER", "REAL", "BLOB", "NUMERIC" };

    /// <summary>
    /// Guided column-builder "Create table" dialog.
    /// Pre-fix this was a single freeform TextBox ("col1:TEXT, col2:INTEGER")
    /// parsed by ParseColumnSpec — error-prone and undiscoverable. Now it builds
    /// the column list interactively: a table-name TextBox plus a growable list
    /// of column rows (name TextBox + affinity ComboBox + remove button) and a
    /// "+ Add column" button, matching the WinForms CreateTableDialog UX. On
    /// commit it reads the live control state into the (name, type) list instead
    /// of parsing text. DB.CreateUserTableAsync re-validates each identifier and
    /// the 'rowid' primary key is injected automatically.
    /// </summary>
    private async System.Threading.Tasks.Task<(string? Name, IReadOnlyList<(string Name, string SqlType)>? Columns)> PromptNewTableAsync()
    {
        var nameBox = new TextBox
        {
            PlaceholderText     = "table_name",
            IsSpellCheckEnabled = false,
        };
        nameBox.Loaded += (_, _) =>
        {
            try { nameBox.Focus(FocusState.Programmatic); } catch { /* best effort */ }
        };

        // Live list of column-row controls; each entry exposes readers for its
        // name + selected affinity, plus the StackPanel that hosts the row so a
        // remove button can pull it out.
        var columnRows = new List<(Func<string> ReadName, Func<string> ReadType, StackPanel Container)>();

        // The vertical list the +Add column button grows. Declared before the
        // local AddColumnRow function so the closure can reference it.
        var columnsList = new StackPanel { Spacing = 6 };

        void AddColumnRow(string seedName = "", string seedType = "TEXT")
        {
            var colName = new TextBox
            {
                PlaceholderText     = "column_name",
                IsSpellCheckEnabled = false,
                Text                = seedName,
                Width               = 180,
            };
            int seedIdx = 0;
            for (int i = 0; i < _newTableColumnAffinities.Length; i++)
                if (string.Equals(_newTableColumnAffinities[i], seedType, StringComparison.OrdinalIgnoreCase))
                {
                    seedIdx = i;
                    break;
                }
            var colType = new ComboBox
            {
                ItemsSource   = _newTableColumnAffinities,
                SelectedIndex = seedIdx,
                MinWidth      = 110,
            };
            var removeBtn = new Button
            {
                Content = "−", // minus sign
                Width   = 32,
            };
            ToolTipService.SetToolTip(removeBtn,
                Localizer.T("architect.databank.dialog.create_table.remove_column", "Remove column"));
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 6,
            };
            row.Children.Add(colName);
            row.Children.Add(colType);
            row.Children.Add(removeBtn);

            var entry = (ReadName: (Func<string>)(() => colName.Text?.Trim() ?? string.Empty),
                         ReadType: (Func<string>)(() => colType.SelectedItem as string ?? "TEXT"),
                         Container: row);
            columnRows.Add(entry);
            columnsList.Children.Add(row);

            removeBtn.Click += (_, _) =>
            {
                // Keep at least one column row so the dialog always has a field
                // to fill — removing the last row would let the user create a
                // zero-column table (DB rejects it, but the empty UI is worse).
                if (columnRows.Count <= 1) return;
                columnRows.Remove(entry);
                columnsList.Children.Remove(row);
            };
        }

        // Seed one empty column row so the dialog opens ready to type.
        AddColumnRow();

        var addColumnButton = new Button
        {
            Content = Localizer.T("architect.databank.dialog.create_table.add_column", "+ Add column"),
        };
        addColumnButton.Click += (_, _) => AddColumnRow();

        var help = new TextBlock
        {
            Text         = Localizer.T("architect.databank.dialog.create_table.help",
                "The 'rowid' primary key is added automatically. Column names: letters, digits, and underscores only."),
            FontSize     = 11,
            Opacity      = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel { Spacing = 8, MinWidth = 380 };
        stack.Children.Add(new TextBlock { Text = "Table name", FontSize = 11, Opacity = 0.75 });
        stack.Children.Add(nameBox);
        stack.Children.Add(new TextBlock { Text = "Columns", FontSize = 11, Opacity = 0.75 });
        stack.Children.Add(columnsList);
        stack.Children.Add(addColumnButton);
        stack.Children.Add(help);

        var scroller = new ScrollViewer
        {
            Content                       = stack,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight                     = 440,
        };

        var dialog = new ContentDialog
        {
            Title             = Localizer.T("architect.databank.dialog.create_table.title", "Create user table"),
            Content           = scroller,
            PrimaryButtonText = Localizer.T("common.button.create", "Create"),
            CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot,
        };
        var res = await dialog.ShowAsync();
        if (res != ContentDialogResult.Primary) return (null, null);

        // Read the live control state into the column list, skipping empty-name
        // rows. DB.CreateUserTableAsync re-validates each identifier before DDL.
        var columns = new List<(string Name, string SqlType)>(columnRows.Count);
        foreach (var (readName, readType, _) in columnRows)
        {
            string n = readName();
            if (n.Length == 0) continue;
            columns.Add((n, readType()));
        }
        return (nameBox.Text?.Trim(), columns);
    }

    private async System.Threading.Tasks.Task<(string? Name, string? Type)> PromptNewColumnAsync()
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "column_name",
            IsSpellCheckEnabled = false,
        };
        var typeCombo = new ComboBox
        {
            ItemsSource = new[] { "TEXT", "INTEGER", "REAL", "BOOLEAN" },
            SelectedIndex = 0,
        };
        var stack = new StackPanel { Spacing = 6, MinWidth = 320 };
        stack.Children.Add(new TextBlock { Text = "Column name", FontSize = 11, Opacity = 0.75 });
        stack.Children.Add(nameBox);
        stack.Children.Add(new TextBlock { Text = "SQL type",    FontSize = 11, Opacity = 0.75 });
        stack.Children.Add(typeCombo);

        var dialog = new ContentDialog
        {
            Title             = "Add column",
            Content           = stack,
            PrimaryButtonText = "Add",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot,
        };
        var res = await dialog.ShowAsync();
        if (res != ContentDialogResult.Primary) return (null, null);

        return (nameBox.Text?.Trim(), typeCombo.SelectedItem as string);
    }

    // ParseColumnSpec was removed — the freeform
    // "col1:TEXT, col2:INTEGER" text path it parsed is gone, replaced by the
    // interactive column-builder in PromptNewTableAsync which reads the live
    // control state directly.
}
