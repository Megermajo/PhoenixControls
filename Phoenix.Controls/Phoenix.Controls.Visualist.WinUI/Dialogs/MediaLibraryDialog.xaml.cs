using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Services;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// Sprint C — Visualist media-library browse + delete surface. Reintroduces
/// the affordance pre-0.9.0 Visualist shipped (CHANGELOG 0.6.4 "delete
/// media files from the library panel"). Per
/// <c>feedback_visualist_architect_chrome_independence.md</c> this dialog
/// lives in the Visualist pillar and owns its own chrome tokens — no
/// Shared/UI/ helpers.
///
/// <para>
/// Reference-handling policy: <b>block-and-log</b>. Before any delete the
/// dialog scans every <c>.phxlayer</c> in <c>data/layers/</c> (plus the
/// optional in-memory live layer) for <c>Image.Load</c> / <c>Video.Load</c>
/// / <c>Audio.Load</c> nodes whose <c>Path</c> attribute matches the
/// selected file. Hits abort the delete with a logged refusal listing every
/// hit. Rationale: silently clearing references is destructive — a Visualist
/// designer who deletes a placeholder asset expects the layer they're
/// editing to break visibly, not to lose `Path` attributes scattered across
/// unrelated layers.
/// </para>
///
/// <para>
/// The confirmation step is a nested <see cref="ContentDialog"/> — exempt
/// from <c>feedback_no_modal_dialogs_for_repeatable_rejections.md</c>
/// because file deletion is irreversible and one of the listed exceptions.
/// All other rejection paths (already-gone, blocked-by-references, IO
/// failure) route through <see cref="GlobalLogger"/>.
/// </para>
/// </summary>
public sealed partial class MediaLibraryDialog : ContentDialog
{
    /// <summary>
    /// Decorated row the ListView binds to — adds the human-readable size
    /// label on top of the bare <see cref="MediaLibrary.MediaItem"/>.
    /// </summary>
    public sealed record Row(
        string FileName,
        string RelativePath,
        string FullPath,
        long   SizeBytes,
        string SizeLabel)
    {
        public static Row From(MediaLibrary.MediaItem item)
            => new(item.FileName, item.RelativePath, item.FullPath, item.SizeBytes, FormatSize(item.SizeBytes));

        public MediaLibrary.MediaItem ToItem()
            => new(FileName, RelativePath, FullPath, SizeBytes);

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)            return $"{bytes} B";
            if (bytes < 1024 * 1024)     return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }

    /// <summary>
    /// In-memory layer + its on-disk filename to consult during reference
    /// scanning. Both nullable — null means "no live document to inspect".
    /// Set by the caller (MainView) before <see cref="ContentDialog.ShowAsync"/>.
    /// </summary>
    public Layer?  LiveLayer { get; set; }
    public string? LiveLayerFileName { get; set; }

    public MediaLibraryDialog()
    {
        InitializeComponent();
        // Loaded fires after XamlRoot is set so nested ContentDialogs that
        // we open from button handlers can adopt the same root.
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        try
        {
            var items = MediaLibrary.Enumerate();
            var rows  = items.Select(Row.From).ToList();
            MediaList.ItemsSource = rows;

            bool empty = rows.Count == 0;
            EmptyHint.Visibility  = empty ? Visibility.Visible : Visibility.Collapsed;
            MediaList.Visibility  = empty ? Visibility.Collapsed : Visibility.Visible;

            HeaderLine.Text = string.Format(
                Localizer.T("visualist.dialog.media_library.header_format",
                            "data/media — {0} file(s) under {1}"),
                rows.Count,
                MediaLibrary.MediaRoot);

            DeleteButton.IsEnabled = false;
            StatusLine.Text = empty
                ? Localizer.T("visualist.dialog.media_library.empty_hint",
                              "data/media is empty — nothing to delete.")
                : Localizer.T("visualist.dialog.media_library.pick_hint",
                              "Select a file to inspect or delete.");
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "MediaLibraryDialog.Reload", ex);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteButton.IsEnabled = MediaList.SelectedItem is Row;
        if (MediaList.SelectedItem is Row r)
        {
            StatusLine.Text = string.Format(
                Localizer.T("visualist.dialog.media_library.selected_format",
                            "Selected: {0}"),
                r.RelativePath);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not Row row) return;
        var item = row.ToItem();

        // Reference scan first — block-and-log when the file is in use.
        IReadOnlyList<string> refs;
        try { refs = MediaLibrary.FindReferences(item, LiveLayer, LiveLayerFileName); }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist",
                $"MediaLibrary reference scan for '{row.RelativePath}'", ex);
            refs = Array.Empty<string>();
        }

        if (refs.Count > 0)
        {
            string preview = string.Join("\n  • ", refs.Take(10));
            string suffix = refs.Count > 10
                ? $"\n  … and {refs.Count - 10} more"
                : string.Empty;

            GlobalLogger.Log(
                $"Delete refused — '{row.RelativePath}' is referenced by {refs.Count} location(s):\n  • {preview}{suffix}",
                "Visualist",
                LogLevel.System);

            StatusLine.Text = string.Format(
                Localizer.T("visualist.dialog.media_library.blocked_format",
                            "Cannot delete — referenced by {0} location(s). See System Log."),
                refs.Count);
            return;
        }

        // Reference-free — ask the user to confirm. Irreversible action
        // gets a confirmation dialog per the no-modal-dialogs exception.
        var confirm = new ContentDialog
        {
            XamlRoot          = this.XamlRoot,
            Title             = Localizer.T("visualist.dialog.media_library.confirm.title",
                                            "Delete media file?"),
            Content           = string.Format(
                Localizer.T("visualist.dialog.media_library.confirm.content_format",
                            "Permanently delete \"{0}\" from data/media?\n\nThis cannot be undone."),
                row.RelativePath),
            PrimaryButtonText = Localizer.T("common.button.delete", "Delete"),
            CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
            DefaultButton     = ContentDialogButton.Close,
        };
        var res = await confirm.ShowAsync();
        if (res != ContentDialogResult.Primary) return;

        // Disk delete — on failure log and bail (no in-memory state to
        // clean up; the dialog re-enumerates on Reload).
        bool deleted;
        try { deleted = MediaLibrary.DeleteFromDisk(item); }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist",
                $"MediaLibrary delete '{row.RelativePath}'", ex);
            StatusLine.Text = string.Format(
                Localizer.T("visualist.dialog.media_library.delete_failed_format",
                            "Delete failed — see System Log. ({0})"),
                ex.GetType().Name);
            return;
        }

        if (!deleted)
        {
            GlobalLogger.Log(
                $"Media file already gone from disk: {row.FullPath}",
                "Visualist", LogLevel.System);
        }
        else
        {
            GlobalLogger.Log(
                $"Deleted media file: {row.RelativePath}",
                "Visualist", LogLevel.System);
        }

        // Refresh the list so the row disappears.
        Reload();
    }
}
