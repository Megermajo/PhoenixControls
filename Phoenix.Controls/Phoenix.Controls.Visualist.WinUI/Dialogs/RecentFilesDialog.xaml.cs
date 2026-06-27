using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Visualist.WinUI.Services;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

// Visualist-side parallel of Architect's RecentFilesDialog. Kept duplicated
// per feedback_visualist_architect_chrome_independence.md — dialog chrome is
// paint code, not a cross-pillar service. The MRU backing
// (Phoenix.Controls.Visualist.WinUI.Services.RecentFiles) is also a pillar-
// local copy of the same pattern. (TODO 2026-05-07 round 1 P2 PARTIAL —
// completes the Recent Files surface for Visualist.)
public sealed partial class RecentFilesDialog : ContentDialog
{
    public sealed record Row(string FileName, string FullPath);

    /// <summary>Path the user picked, or null when the dialog closed without picking.</summary>
    public string? PickedPath { get; private set; }

    public RecentFilesDialog()
    {
        InitializeComponent();
        Reload();

        // Commit via the canonical Primary button so the dialog honors the
        // ContentDialog contract (Primary = commit, Close/Esc = cancel).
        // Previously the only commit path was ItemClick → Hide(), which left
        // Esc / Open-button semantics ambiguous. ItemClick stays as a
        // double-click shortcut; Enter-in-the-list is handled below.
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void Reload()
    {
        var paths = RecentFiles.Load();
        var rows = paths
            .Select(p => new Row(Path.GetFileName(p), p))
            .ToList();
        RecentList.ItemsSource = rows;
        bool empty = rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = !empty;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Row r)
        {
            PickedPath = r.FullPath;
            Hide();
        }
    }

    // Primary ("Open") button: commit the currently-selected row. If nothing
    // is selected the click is cancelled so the dialog stays open rather than
    // closing with a null pick.
    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (RecentList.SelectedItem is Row r)
        {
            PickedPath = r.FullPath;
        }
        else
        {
            // No selection — keep the dialog open instead of committing nothing.
            args.Cancel = true;
        }
    }

    // Enter inside the list commits the highlighted row — mirrors Architect's
    // RecentFilesDialog.OnRecentListKeyDown so keyboard-only navigation can open
    // a recent layer without reaching for the Open button.
    private void OnRecentListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        if (RecentList.SelectedItem is Row r)
        {
            PickedPath = r.FullPath;
            e.Handled = true;
            Hide();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        foreach (var p in RecentFiles.Load()) RecentFiles.Remove(p);
        Reload();
    }
}
