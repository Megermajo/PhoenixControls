using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Services;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

/// <summary>
/// Docked media library for the Widget Editor. Thumbnail grid + kind chips
/// + Import + Delete (reference-guarded) + Refresh + drag-out. Restores the
/// pre-WinUI MediaLibraryPanel that the rework demoted to a menu-only delete-list
/// dialog. Data + import route through <see cref="MediaLibrary"/>; the live layer
/// for the delete reference-scan comes from the bound <see cref="VisualistViewModel"/>
/// (DataContext). Visualist-local.
/// </summary>
public sealed partial class MediaLibraryPanel : UserControl
{
    /// <summary>Clipboard/drag format the node canvas reads to spawn a loader node.
    /// Payload is "<c>{kind}|{relativePath}</c>" (kind = image/video/audio).</summary>
    public const string MediaDragFormat = "Phoenix.Controls.Visualist.MediaEntry";

    private readonly ObservableCollection<Row> _rows = new();
    private CancellationTokenSource? _thumbCts;
    private MediaLibrary.Kind? _activeFilter;

    public MediaLibraryPanel()
    {
        InitializeComponent();
        MediaGrid.ItemsSource = _rows;
        MediaGrid.RightTapped += OnGridRightTapped;
        KeyDown += OnPanelKeyDown;
        IsTabStop = true;
        // Seed the persistent drag-hint footer from the localizer (literal
        // fallback when the key is absent so it works without a resource edit).
        DragHint.Text = Localizer.T(
            "visualist.medialib.hint.drag",
            "Drag a tile onto the node graph to add a loader node.");
        Loaded   += (_, _) => PopulateRows();
        Unloaded += (_, _) => _thumbCts?.Cancel();
    }

    private VisualistViewModel? Vm => DataContext as VisualistViewModel;

    // ── filter chips ─────────────────────────────────────────────────────
    private void OnFilterChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        _activeFilter = tb.Name switch
        {
            "ImageChip" => MediaLibrary.Kind.Image,
            "VideoChip" => MediaLibrary.Kind.Video,
            "AudioChip" => MediaLibrary.Kind.Audio,
            _           => (MediaLibrary.Kind?)null,
        };
        AllChip.IsChecked   = _activeFilter is null;
        ImageChip.IsChecked = _activeFilter == MediaLibrary.Kind.Image;
        VideoChip.IsChecked = _activeFilter == MediaLibrary.Kind.Video;
        AudioChip.IsChecked = _activeFilter == MediaLibrary.Kind.Audio;
        PopulateRows();
    }

    // ── data load ────────────────────────────────────────────────────────
    /// <summary>Public refresh hook so the host can repaint after an external edit.</summary>
    public void Refresh() => PopulateRows();

    private void PopulateRows()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        _rows.Clear();

        IReadOnlyList<MediaLibrary.MediaItem> items;
        try { items = MediaLibrary.Enumerate(); }
        catch (Exception ex) { GlobalLogger.Error("MediaLibraryPanel", "Enumerate", ex); items = Array.Empty<MediaLibrary.MediaItem>(); }

        foreach (var item in items)
        {
            var kind = MediaLibrary.ClassifyKind(item.FullPath);
            if (_activeFilter is { } f && kind != f) continue;
            _rows.Add(new Row(item, kind));
        }

        bool empty = _rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MediaGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        // Show the drag-gesture footer only when there's something to drag;
        // while empty the EmptyHint already carries the onboarding message.
        DragHint.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        _ = LoadThumbnailsAsync(_rows.ToList(), _thumbCts.Token);
    }

    private async Task LoadThumbnailsAsync(List<Row> rows, CancellationToken ct)
    {
        // Disk-backed thumbnail cache (shared with MediaPickerDialog): try
        // MediaLibrary's cache (LocalAppData/Visualist/thumbcache, keyed on path +
        // mtime) before paying a decode. Hit → replay cached PNG bytes into a
        // BitmapImage. Miss → decode + downscale to a 96px PNG, show it, store it.
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) return;
            if (row.Kind != MediaLibrary.Kind.Image) continue;
            try
            {
                byte[]? png = await MediaLibrary.GetThumbnailAsync(row.FullPath, ct);
                if (ct.IsCancellationRequested) return;

                if (png is null)
                {
                    png = await DecodeToThumbnailPngAsync(row.FullPath, ct);
                    if (ct.IsCancellationRequested) return;
                    if (png is not null) MediaLibrary.StoreThumbnail(row.FullPath, png);
                }
                if (png is null) continue; // unreadable — glyph stays

                if (!await SetRowThumbnailAsync(row, png, ct)) return;
            }
            catch (OperationCanceledException) { return; }
            catch { /* unreadable image — glyph stays */ }
        }
    }

    /// <summary>
    /// Decode <paramref name="fullPath"/> and re-encode it downscaled (long edge
    /// ≤ 96px) to PNG bytes for the disk cache. Mirrors WidgetThumbnailCapture's
    /// BitmapDecoder + BitmapEncoder.ScaledWidth pattern. Null on decode failure.
    /// </summary>
    private static async Task<byte[]?> DecodeToThumbnailPngAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(fullPath);
            using var src = await file.OpenReadAsync();
            if (ct.IsCancellationRequested) return null;

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(src);
            uint srcW = decoder.PixelWidth, srcH = decoder.PixelHeight;
            if (srcW == 0 || srcH == 0) return null;

            const int maxDim = 96;
            uint dstW = srcW, dstH = srcH;
            uint longEdge = Math.Max(srcW, srcH);
            if (longEdge > maxDim)
            {
                double scale = (double)maxDim / longEdge;
                dstW = Math.Max(1u, (uint)Math.Round(srcW * scale));
                dstH = Math.Max(1u, (uint)Math.Round(srcH * scale));
            }

            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform
                {
                    ScaledWidth       = dstW,
                    ScaledHeight      = dstH,
                    InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
                },
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            if (ct.IsCancellationRequested) return null;

            using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, outStream);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                dstW, dstH, 96.0, 96.0, pixelData.DetachPixelData());
            await encoder.FlushAsync();

            outStream.Seek(0);
            using var reader = new Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0));
            uint len = (uint)outStream.Size;
            await reader.LoadAsync(len);
            var bytes = new byte[len];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Marshal a BitmapImage built from cached/decoded PNG bytes onto the row on
    /// the UI thread. Returns <c>false</c> when the dispatcher is gone.
    /// </summary>
    private async Task<bool> SetRowThumbnailAsync(Row row, byte[] png, CancellationToken ct)
    {
        if (DispatcherQueue is null) return false;
        var tcs = new TaskCompletionSource<bool>();
        bool queued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (ct.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(ras))
                {
                    writer.WriteBytes(png);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                ras.Seek(0);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ras);
                row.SetThumbnail(bmp);
                tcs.TrySetResult(true);
            }
            catch { tcs.TrySetResult(false); }
        });
        if (!queued) return false;
        await tcs.Task;
        return true;
    }

    // ── import / refresh ─────────────────────────────────────────────────
    private void OnRefreshClick(object sender, RoutedEventArgs e) => PopulateRows();

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = TryGetHwnd();
            if (hwnd == IntPtr.Zero)
            {
                GlobalLogger.Log("Media import: no window handle — skipped.", "MediaLibraryPanel", LogLevel.System);
                return;
            }
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var (_, ext) in MediaLibrary.PickerExtensions()) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;

            int imported = 0;
            foreach (var file in files)
            {
                try { if (MediaLibrary.Import(file.Path) is not null) imported++; }
                catch (Exception ex) { GlobalLogger.Error("MediaLibraryPanel", $"import '{file.Name}'", ex); }
            }
            if (imported > 0)
            {
                GlobalLogger.Log($"Imported {imported} media file(s) into data/media.", "MediaLibraryPanel", LogLevel.System);
                PopulateRows();
            }
        }
        catch (Exception ex) { GlobalLogger.Error("MediaLibraryPanel", "Import", ex); }
    }

    private IntPtr TryGetHwnd()
    {
        try
        {
            var root = XamlRoot;
            if (root?.ContentIslandEnvironment is null) return IntPtr.Zero;
            return Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        }
        catch { return IntPtr.Zero; }
    }

    // ── drag-out (source half) ───────────────────────────────────────────
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 0 || e.Items[0] is not Row row) { e.Cancel = true; return; }
        string kind = row.Kind switch
        {
            MediaLibrary.Kind.Image => "image",
            MediaLibrary.Kind.Video => "video",
            MediaLibrary.Kind.Audio => "audio",
            _                       => "other",
        };
        if (kind == "other") { e.Cancel = true; return; }   // no loader node for non-media

        // The custom DataPackage format does NOT round-trip reliably
        // across the WinUI 3 drag boundary, so the graph drop was no-opping.
        // Carry the FULL payload ("{kind}|{relativePath}") in the Text format too
        // (the reliable channel the canvas drop reads first) — NOT the bare path.
        // Keep the custom format as belt-and-suspenders for any consumer that
        // prefers it. Contract: payload = "{kind}|{relativePath}", kind ∈
        // {image,video,audio}.
        string payload = $"{kind}|{row.RelativePath}";
        e.Data.SetText(payload);
        e.Data.SetData(MediaDragFormat, payload);
        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    // ── delete (toolbar button: right-click + Del-key + Delete button) ────
    /// <summary>Toolbar Delete — operates on the current grid selection.
    /// Shares the reference-guarded <see cref="DeleteWithGuardAsync"/> path with
    /// the context menu and Del key.</summary>
    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (MediaGrid.SelectedItem is Row row)
            await DeleteWithGuardAsync(row);
    }

    /// <summary>Keep the toolbar Delete button enabled only when a tile is
    /// selected (discoverable but never armed against an empty selection).</summary>
    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteButton.IsEnabled = MediaGrid.SelectedItem is Row;
    }

    private void OnGridRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        Row? row = ResolveRow(e.OriginalSource);
        if (row is null) return;
        MediaGrid.SelectedItem = row;
        var flyout = new MenuFlyout();
        var del = new MenuFlyoutItem
        {
            Text = Localizer.T("common.context.delete", "Delete"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ErrBrush"],
        };
        del.Click += async (_, _) => await DeleteWithGuardAsync(row);
        flyout.Items.Add(del);
        flyout.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }

    private async void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && MediaGrid.SelectedItem is Row row)
        {
            e.Handled = true;
            await DeleteWithGuardAsync(row);
        }
    }

    private Row? ResolveRow(object? originalSource)
    {
        var d = originalSource as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.DataContext is Row r) return r;
            d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
        }
        return MediaGrid.SelectedItem as Row;
    }

    private async Task DeleteWithGuardAsync(Row row)
    {
        if (XamlRoot is null) return;
        try
        {
            IReadOnlyList<string> refs = MediaLibrary.FindReferences(
                row.Item, Vm?.SelectedLayer,
                string.IsNullOrEmpty(Vm?.ActiveLayerFileName) ? null : Vm!.ActiveLayerFileName);

            string body = refs.Count == 0
                ? string.Format(
                    Localizer.T("visualist.media.delete.body_format",
                        "Delete \"{0}\" from data/media? This can't be undone."),
                    row.RelativePath)
                : string.Format(
                      Localizer.T("visualist.media.delete.body_referenced_format",
                          "\"{0}\" is still used by:\n\n• {1}"),
                      row.RelativePath, string.Join("\n• ", refs.Take(8))) +
                  (refs.Count > 8
                      ? string.Format(
                          Localizer.T("visualist.media.delete.body_more_format", "\n• …and {0} more"),
                          refs.Count - 8)
                      : "") +
                  Localizer.T("visualist.media.delete.body_anyway",
                      "\n\nDelete it anyway? The referencing nodes will fail to load it.");

            var dlg = new ContentDialog
            {
                XamlRoot          = XamlRoot,
                Title             = Localizer.T("visualist.media.delete.title", "Delete media file?"),
                Content           = body,
                PrimaryButtonText = Localizer.T("common.button.delete", "Delete"),
                CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
                DefaultButton     = ContentDialogButton.Close,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            if (MediaLibrary.DeleteFromDisk(row.Item))
                GlobalLogger.Log($"Deleted media file: {row.RelativePath}", "MediaLibraryPanel", LogLevel.System);
            PopulateRows();
        }
        catch (Exception ex) { GlobalLogger.Error("MediaLibraryPanel", $"delete '{row.RelativePath}'", ex); }
    }

    // ── row view-model (own copy; per-pillar) ────────────────────────────
    public sealed class Row : INotifyPropertyChanged
    {
        public Row(MediaLibrary.MediaItem item, MediaLibrary.Kind kind) { Item = item; Kind = kind; }
        public MediaLibrary.MediaItem Item { get; }
        public MediaLibrary.Kind Kind { get; }
        public string FileName     => Item.FileName;
        public string RelativePath => Item.RelativePath;
        public string FullPath     => Item.FullPath;
        public string KindGlyph => Kind switch
        {
            MediaLibrary.Kind.Image => "IMG",
            MediaLibrary.Kind.Video => "VID",
            MediaLibrary.Kind.Audio => "AUD",
            _                       => "?",
        };
        private BitmapImage? _thumb;
        public BitmapImage? Thumbnail => _thumb;
        public Visibility PlaceholderVisibility => _thumb is null ? Visibility.Visible : Visibility.Collapsed;
        public void SetThumbnail(BitmapImage b)
        {
            _thumb = b;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderVisibility)));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
