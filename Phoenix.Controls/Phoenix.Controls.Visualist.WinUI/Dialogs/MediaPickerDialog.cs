using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// Visualist WinUI regression audit 2026-05-31 (Area 3 P1) — port of the
/// pre-T15 WinForms <c>MediaPickerDialog</c>
/// (Phoenix.Controls.Visualist/Forms/MediaPickerDialog.cs). Modal media browser
/// behind the graph node "Browse Media…" gesture on Image.Load / Video.Load /
/// Audio.Load nodes. Returns a RELATIVE media path (e.g. <c>images/welcome.png</c>)
/// ready to drop into the loader node's <c>Path</c> attribute via
/// <see cref="SelectedRelativePath"/> after a Primary (Use Selected) result.
///
/// <para>
/// Surface: a 96px thumbnail grid (placeholder glyph → async-decoded real
/// thumbnail for image kinds), kind filter chips (All / Image / Video / Audio),
/// Import (multi-file copy into <c>data/media</c>), Refresh, and OK/Cancel.
/// </para>
///
/// <para>
/// Data side reuses the existing <see cref="MediaLibrary"/> service
/// (<see cref="MediaLibrary.Enumerate"/> / <see cref="MediaLibrary.MediaRoot"/>)
/// that the read-only <see cref="MediaLibraryDialog"/> also consumes. The
/// import + thumbnail decode live here in the dialog. Visualist-local per
/// feedback_visualist_architect_chrome_independence.md.
/// </para>
///
/// <para>
/// [DIALOG-NO-XAML-FIX 2026-06-29] No .xaml / InitializeComponent — a
/// code-constructed library ContentDialog throws XamlParseException at
/// Application.LoadComponent when <c>new</c>'d detached (proven by the 1.0.6
/// runtime stack trace; resource stripping never helped because the throw is in
/// the XAML parse itself). Content is built in code; the default template still
/// resolves at ShowAsync against Hub's app scope. The GridView ItemTemplate is
/// built via XamlReader.Load — deferred template content, so its {Binding} /
/// {ThemeResource} markup resolves at row realization (safe), not at load. See
/// NameTypeDialog / DialogTheme.cs for the full rationale.
/// </para>
/// </summary>
public sealed class MediaPickerDialog : ContentDialog
{
    /// <summary>Coarse media classification derived from the file extension.</summary>
    public enum MediaKind { Image, Video, Audio, Other }

    /// <summary>
    /// Relative path of the picked file (forward-slash separated, e.g.
    /// <c>images/welcome.png</c>) — null until the user commits via "Use
    /// Selected". Read by the caller after a <see cref="ContentDialogResult.Primary"/>.
    /// </summary>
    public string? SelectedRelativePath { get; private set; }

    private readonly MediaKind? _filter;
    private readonly ObservableCollection<Row> _rows = new();
    private CancellationTokenSource? _thumbCts;

    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly TextBlock EyebrowLine;
    private readonly TextBlock HeaderLine;
    private readonly ToggleButton AllChip;
    private readonly ToggleButton ImageChip;
    private readonly ToggleButton VideoChip;
    private readonly ToggleButton AudioChip;
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle DividerLine;
    private readonly GridView MediaGrid;
    private readonly TextBlock EmptyHint;
    private readonly TextBlock StatusLine;
    private readonly Button ImportButton;
    private readonly Button RefreshButton;

    // Extension classification — mirrors the baseline filter sets + LayerCanvasView's
    // accepted-drop extensions so the picker, the canvas, and the kernel agree.
    private static readonly HashSet<string> s_imageExt =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".webp", ".jpg", ".jpeg", ".gif", ".bmp" };
    private static readonly HashSet<string> s_videoExt =
        new(StringComparer.OrdinalIgnoreCase) { ".webm", ".mp4", ".mov", ".m4v" };
    private static readonly HashSet<string> s_audioExt =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".ogg", ".m4a", ".flac" };

    public MediaPickerDialog(MediaKind? filter = null)
    {
        // Root attributes that were on <ContentDialog …>.
        Title = "Browse Media";
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        PrimaryButtonText = "Use Selected";
        CloseButtonText = "Cancel";
        IsPrimaryButtonEnabled = false;
        DefaultButton = ContentDialogButton.Primary;

        // ── visual tree (was the <Grid Width=620 Height=460> body) ───────────
        var root = new Grid { Width = 620, Height = 460 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // header (row 0)
        var headerPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(headerPanel, 0);

        // EyebrowLine — keyed Style PickerEyebrow setters applied directly.
        EyebrowLine = new TextBlock
        {
            Text = "VISUALIST · MEDIA",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 180,
            Margin = new Thickness(0, 0, 0, 4),
        };
        HeaderLine = new TextBlock { FontSize = 11, Text = "data/media" };
        headerPanel.Children.Add(EyebrowLine);
        headerPanel.Children.Add(HeaderLine);

        // filter chips (row 1)
        var chipPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(chipPanel, 1);
        AllChip   = MakeChip("All");
        ImageChip = MakeChip("Image");
        VideoChip = MakeChip("Video");
        AudioChip = MakeChip("Audio");
        chipPanel.Children.Add(AllChip);
        chipPanel.Children.Add(ImageChip);
        chipPanel.Children.Add(VideoChip);
        chipPanel.Children.Add(AudioChip);

        // divider (row 2)
        DividerLine = new Microsoft.UI.Xaml.Shapes.Rectangle();
        Grid.SetRow(DividerLine, 2);

        // thumbnail grid (row 3)
        var scroller = new ScrollViewer
        {
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroller, 3);

        MediaGrid = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = BuildItemTemplate(),
        };
        MediaGrid.DoubleTapped += OnGridDoubleTapped;
        MediaGrid.SelectionChanged += OnGridSelectionChanged;
        scroller.Content = MediaGrid;

        // empty hint (row 3, overlaid)
        EmptyHint = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            TextAlignment = TextAlignment.Center,
            Text = "No media in this category. Use Import to add files to data/media.",
        };
        Grid.SetRow(EmptyHint, 3);

        // status line (row 4)
        StatusLine = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Text = "Select a file, then Use Selected — or double-click a tile.",
        };
        Grid.SetRow(StatusLine, 4);

        // import / refresh button row (row 5)
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Spacing = 6,
        };
        Grid.SetRow(buttonPanel, 5);
        ImportButton = new Button { Content = "Import…" };
        ImportButton.Click += OnImportClick;
        RefreshButton = new Button { Content = "Refresh" };
        RefreshButton.Click += OnRefreshClick;
        buttonPanel.Children.Add(ImportButton);
        buttonPanel.Children.Add(RefreshButton);

        root.Children.Add(headerPanel);
        root.Children.Add(chipPanel);
        root.Children.Add(DividerLine);
        root.Children.Add(scroller);
        root.Children.Add(EmptyHint);
        root.Children.Add(StatusLine);
        root.Children.Add(buttonPanel);
        Content = root;

        // Code-constructed library dialog — theme applied in code via DialogTheme;
        // the body carries no directly-resolved resource markup (DataTemplate refs
        // are deferred and safe). See Architect NameTypeDialog / DialogTheme.cs.
        if (DialogTheme.Brush("CoalShellBrush") is { } shell) Background = shell;
        if (DialogTheme.Brush("CoalCardBrush") is { } card) BorderBrush = card;

        // Eyebrow caption (was Style PickerEyebrow's FontFamily / Foreground setters).
        if (DialogTheme.Font("DisplayFont") is { } eyebrowFont) EyebrowLine.FontFamily = eyebrowFont;
        if (DialogTheme.Brush("EmberPrimaryBrush") is { } ember) EyebrowLine.Foreground = ember;

        // Header sub-line.
        if (DialogTheme.Font("MonoFont") is { } mono)
        {
            HeaderLine.FontFamily = mono;
            StatusLine.FontFamily = mono;
            // Filter chips (was Style PickerChip's FontFamily setter).
            AllChip.FontFamily   = mono;
            ImageChip.FontFamily = mono;
            VideoChip.FontFamily = mono;
            AudioChip.FontFamily = mono;
        }
        if (DialogTheme.Brush("CoalSecondaryTextBrush") is { } secondary)
        {
            HeaderLine.Foreground = secondary;
            EmptyHint.Foreground  = secondary;
        }
        if (DialogTheme.Brush("CoalMutedTextBrush") is { } muted) StatusLine.Foreground = muted;

        // Header/body divider rule.
        if (DialogTheme.Brush("BrassGradientBrush") is { } brass) DividerLine.Fill = brass;

        _filter = filter;

        Title = filter switch
        {
            MediaKind.Image => Localizer.T("visualist.mediapicker.title.images", "Browse Images"),
            MediaKind.Video => Localizer.T("visualist.mediapicker.title.videos", "Browse Videos"),
            MediaKind.Audio => Localizer.T("visualist.mediapicker.title.audio",  "Browse Audio"),
            _               => Localizer.T("visualist.mediapicker.title.media",  "Browse Media"),
        };

        MediaGrid.ItemsSource = _rows;

        // Seed the active filter chip from the constructor argument. A loader
        // node opens the picker pre-filtered to its kind, but the user can widen
        // it via the chips.
        SetActiveChip(filter);

        PrimaryButtonClick += OnUseSelected;
        Loaded += (_, _) => Reload();
        Closed += (_, _) => _thumbCts?.Cancel();
    }

    // Keyed Style PickerChip — setters applied directly per recipe §2d.
    private ToggleButton MakeChip(string content)
    {
        var chip = new ToggleButton
        {
            Content = content,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 4, 0),
            MinHeight = 26,
            MinWidth = 60,
        };
        chip.Click += OnFilterChipClick;
        return chip;
    }

    // GridView ItemTemplate — built via XamlReader.Load with the DataTemplate
    // markup preserved VERBATIM from the old XAML (every {Binding} / {ThemeResource}
    // kept). Template content is deferred, so the {ThemeResource} refs resolve at
    // row realization in the live tree — safe, unlike on the dialog root.
    private static DataTemplate BuildItemTemplate()
    {
        const string tpl =
@"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Grid Width=""110"" Height=""124"" Padding=""4"" RowSpacing=""3"">
        <Grid.RowDefinitions>
            <RowDefinition Height=""96"" />
            <RowDefinition Height=""*"" />
        </Grid.RowDefinitions>
        <Border Grid.Row=""0""
                Width=""96"" Height=""96""
                Background=""{ThemeResource CoalRaisedBrush}""
                BorderBrush=""{ThemeResource CoalCardBrush}""
                BorderThickness=""1""
                CornerRadius=""3"">
            <Grid>
                <TextBlock Text=""{Binding KindGlyph}""
                           HorizontalAlignment=""Center""
                           VerticalAlignment=""Center""
                           FontFamily=""{ThemeResource MonoFont}""
                           FontSize=""14""
                           FontWeight=""Bold""
                           Foreground=""{ThemeResource EmberPrimaryBrush}""
                           Visibility=""{Binding PlaceholderVisibility}"" />
                <Image Source=""{Binding Thumbnail}""
                       Stretch=""UniformToFill""
                       HorizontalAlignment=""Stretch""
                       VerticalAlignment=""Stretch"" />
            </Grid>
        </Border>
        <TextBlock Grid.Row=""1""
                   Text=""{Binding FileName}""
                   FontFamily=""{ThemeResource MonoFont}""
                   FontSize=""9""
                   MaxWidth=""100""
                   TextAlignment=""Center""
                   TextTrimming=""CharacterEllipsis""
                   Foreground=""{ThemeResource CoalSecondaryTextBrush}"" />
    </Grid>
</DataTemplate>";
        return (DataTemplate)XamlReader.Load(tpl);
    }

    // ── filter chips ───────────────────────────────────────────────────────

    private void SetActiveChip(MediaKind? kind)
    {
        AllChip.IsChecked   = kind is null;
        ImageChip.IsChecked = kind == MediaKind.Image;
        VideoChip.IsChecked = kind == MediaKind.Video;
        AudioChip.IsChecked = kind == MediaKind.Audio;
    }

    private MediaKind? _activeFilter;

    private void OnFilterChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        MediaKind? picked = tb.Name switch
        {
            "ImageChip" => MediaKind.Image,
            "VideoChip" => MediaKind.Video,
            "AudioChip" => MediaKind.Audio,
            _           => null,
        };
        _activeFilter = picked;
        SetActiveChip(picked);
        PopulateRows();
    }

    // ── data load ──────────────────────────────────────────────────────────

    private void Reload()
    {
        try
        {
            // Default the live filter to the constructor seed on first load.
            _activeFilter ??= _filter;
            SetActiveChip(_activeFilter);
            PopulateRows();
            HeaderLine.Text = string.Format(
                Localizer.T("visualist.mediapicker.header_format", "data/media — {0}"),
                MediaLibrary.MediaRoot);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "MediaPickerDialog.Reload", ex);
        }
    }

    private void PopulateRows()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();

        _rows.Clear();
        IReadOnlyList<MediaLibrary.MediaItem> items;
        try { items = MediaLibrary.Enumerate(); }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "MediaPickerDialog.Enumerate", ex);
            items = Array.Empty<MediaLibrary.MediaItem>();
        }

        foreach (var item in items)
        {
            var kind = ClassifyKind(item.FullPath);
            if (_activeFilter is { } f && kind != f) continue;
            _rows.Add(new Row(item, kind));
        }

        // P2 — clear any stale selection the GridView may retain across the
        // ItemsSource rebind. WinUI's ListView/GridView can hold an object
        // reference that's no longer in the (filtered/refreshed) collection,
        // which left the primary button enabled against a phantom row even when
        // the visible set was empty. Explicitly null it so SelectedItem tracks
        // the new collection.
        MediaGrid.SelectedItem = null;

        bool empty = _rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MediaGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = false;
        StatusLine.Text = empty
            ? Localizer.T("visualist.mediapicker.empty", "No media in this category. Use Import to add files.")
            : Localizer.T("visualist.mediapicker.pick_hint", "Select a file, then Use Selected — or double-click a tile.");

        // Kick off async thumbnail decode for image rows (placeholder → real).
        _ = LoadThumbnailsAsync(_rows.ToList(), _thumbCts.Token);
    }

    private async Task LoadThumbnailsAsync(List<Row> rows, CancellationToken ct)
    {
        // Decode sequentially — a serial walk keeps memory bounded. UI mutations
        // marshal back to the dispatcher per row.
        //
        // P2 — disk-backed thumbnail cache: try MediaLibrary's cache first
        // (LocalAppData/Visualist/thumbcache, keyed by path + mtime). On a hit we
        // replay the cached PNG bytes straight into a BitmapImage — no re-decode.
        // On a miss we decode + downscale the source to a 96px PNG, show it, and
        // store the bytes so the next reload is a cache hit.
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) return;
            if (row.Kind != MediaKind.Image) continue;
            try
            {
                byte[]? png = await MediaLibrary.GetThumbnailAsync(row.FullPath, ct);
                if (ct.IsCancellationRequested) return;

                if (png is null)
                {
                    // Cache miss — decode + downscale the source to PNG bytes.
                    png = await DecodeToThumbnailPngAsync(row.FullPath, ct);
                    if (ct.IsCancellationRequested) return;
                    if (png is not null) MediaLibrary.StoreThumbnail(row.FullPath, png);
                }
                if (png is null) continue; // unreadable — placeholder glyph stays

                if (!await SetRowThumbnailAsync(row, png, ct)) return;
            }
            catch (OperationCanceledException) { return; }
            catch { /* broken / unreadable image — placeholder glyph stays */ }
        }
    }

    /// <summary>
    /// Decode <paramref name="fullPath"/> and re-encode it downscaled (long edge
    /// ≤ 96px) to PNG bytes. Mirrors the BitmapDecoder + BitmapEncoder.ScaledWidth
    /// pattern used by WidgetThumbnailCapture so the cached bytes are small and
    /// uniform regardless of source dimensions. Returns <c>null</c> on any decode
    /// failure (caller leaves the placeholder glyph).
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
    /// Marshal a BitmapImage built from cached/decoded PNG <paramref name="png"/>
    /// onto the row on the UI thread. Returns <c>false</c> when the dispatcher is
    /// gone (dialog closing) so the caller stops the walk.
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

    // ── selection / commit ───────────────────────────────────────────────────

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // P2 — validate the selection is a row still present in the current
        // (possibly just-filtered) collection. A stale reference left over from a
        // prior ItemsSource would otherwise re-enable the primary button against a
        // row the user can't see.
        bool has = MediaGrid.SelectedItem is Row r0 && _rows.Contains(r0);
        IsPrimaryButtonEnabled = has;
        if (has && MediaGrid.SelectedItem is Row r)
            StatusLine.Text = string.Format(
                Localizer.T("visualist.mediapicker.selected_format", "Selected: {0}"), r.RelativePath);
    }

    private void OnGridDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // Double-click a tile = commit. The selection has already updated on the
        // first tap, so SelectedItem is the double-clicked row. Hide() closes the
        // dialog with ContentDialogResult.None — the caller reads SelectedRelativePath
        // (set here) as the commit signal rather than the result enum.
        if (MediaGrid.SelectedItem is not Row row) return;
        SelectedRelativePath = row.RelativePath;
        Hide();
    }

    private void OnUseSelected(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (MediaGrid.SelectedItem is not Row row)
        {
            // Nothing picked — keep the dialog open rather than committing a null.
            args.Cancel = true;
            return;
        }
        SelectedRelativePath = row.RelativePath;
    }

    // ── import / refresh ───────────────────────────────────────────────────

    private void OnRefreshClick(object sender, RoutedEventArgs e) => PopulateRows();

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = TryGetHwnd();
            if (hwnd == IntPtr.Zero)
            {
                GlobalLogger.Log("Media import: no window handle available — import skipped.", "Visualist", LogLevel.System);
                return;
            }

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            foreach (var ext in s_imageExt.Concat(s_videoExt).Concat(s_audioExt))
                picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;

            // R33 — route through the single MediaLibrary.Import convention
            // (kind subfolder + sanitize + collision-safe) instead of the prior
            // flat-copy-to-root that diverged from File→Import Media's layout.
            int imported = 0;
            foreach (var file in files)
            {
                try { if (MediaLibrary.Import(file.Path) is not null) imported++; }
                catch (Exception ex) { GlobalLogger.Error("Visualist", $"Media import '{file.Name}'", ex); }
            }

            if (imported > 0)
            {
                GlobalLogger.Log($"Imported {imported} media file(s) into data/media.", "Visualist", LogLevel.System);
                PopulateRows();
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist", "MediaPickerDialog.Import", ex);
        }
    }

    private IntPtr TryGetHwnd()
    {
        // WinAppSDK 1.5+ — reach the hosting AppWindow through
        // XamlRoot.ContentIslandEnvironment.AppWindowId (same pattern as
        // Architect's DatabankBrowserView.TryGetHwnd). The Win32Interop
        // conversion yields the HWND the COM-backed FileOpenPicker needs.
        try
        {
            var root = XamlRoot;
            if (root?.ContentIslandEnvironment is null) return IntPtr.Zero;
            var wid = root.ContentIslandEnvironment.AppWindowId;
            return Microsoft.UI.Win32Interop.GetWindowFromWindowId(wid);
        }
        catch { return IntPtr.Zero; }
    }

    // ── classification ───────────────────────────────────────────────────────

    private static MediaKind ClassifyKind(string path)
    {
        string ext = Path.GetExtension(path);
        if (s_imageExt.Contains(ext)) return MediaKind.Image;
        if (s_videoExt.Contains(ext)) return MediaKind.Video;
        if (s_audioExt.Contains(ext)) return MediaKind.Audio;
        return MediaKind.Other;
    }

    // ── row view-model ───────────────────────────────────────────────────────

    /// <summary>
    /// GridView-bound row. Carries the bare media item plus the display chrome
    /// (kind glyph placeholder, async-loaded thumbnail). Raises
    /// <see cref="INotifyPropertyChanged"/> when its thumbnail decodes so the
    /// placeholder glyph collapses and the bitmap appears.
    /// </summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private readonly MediaLibrary.MediaItem _item;

        public Row(MediaLibrary.MediaItem item, MediaKind kind)
        {
            _item = item;
            Kind  = kind;
        }

        public MediaKind Kind { get; }
        public string FileName     => _item.FileName;
        public string RelativePath => _item.RelativePath;
        public string FullPath     => _item.FullPath;

        public string KindGlyph => Kind switch
        {
            MediaKind.Image => "IMG",
            MediaKind.Video => "VID",
            MediaKind.Audio => "AUD",
            _               => "?",
        };

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail => _thumbnail;

        // Placeholder shows until a real thumbnail decodes. Video / audio / other
        // never get a bitmap so the glyph stays.
        public Visibility PlaceholderVisibility =>
            _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

        public void SetThumbnail(BitmapImage bmp)
        {
            _thumbnail = bmp;
            OnChanged(nameof(Thumbnail));
            OnChanged(nameof(PlaceholderVisibility));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
