using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

// PreviewSeam: WidgetView is the consumer for both
//   (a) per-node body-preview snapshots (NodeEvaluator.EvaluatePreviews +
//       PreviewSnapshot — engine-side surface that the WinUI canvas does not
//       yet drive), AND
//   (b) first-frame thumbnail capture from a widget's onStartup graph
//       (Manifesto §4.4).
// Both surfaces are intentionally unwired in this branch — the engine seam
// exists, the WinUI host hangs ThumbnailHost as the future render target,
// and the live-preview work plugs the two ends together. See the
// AllowedCategories rationale block in WidgetNodeRegistry.cs for the engine end.

public enum WidgetThumbKind { Label, Text, Gradient }

public sealed partial class WidgetView : UserControl
{
    // Defensive theme-key lookup — bare `(Brush)Resources[key]` throws on a
    // missing key (theme dictionary swap, hot-reload edge). Mirrors the same
    // helper landed for LeftRail/TopTabBar/RailButton in the per-pillar chrome.
    private static readonly Brush s_resourceFallback =
        new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private static Brush Resource(string key)
    {
        try { return (Application.Current.Resources[key] as Brush) ?? s_resourceFallback; }
        catch { return s_resourceFallback; }
    }

    // Theme-key + literal-ARGB fallback for tinted brushes. Mirrors
    // WidgetGraphCanvas.ResolveBrush — kept as a local per-pillar copy
    // (Visualist owns its own paint helpers, never lifts to Shared). Routes the previous inline
    // ARGB literals through the dark-theme tokens so a future theme tweak
    // ripples here without code edits. Colours stay bit-identical to the
    // pre-fix output because EmberPrimaryBrush == #E5A24E and CoalSurfaceBrush
    // == #14110D today; the literal-ARGB tail is only reached when the
    // dictionary swap drops a key.
    private static Brush ResolveBrushTinted(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush sb)
            {
                return new SolidColorBrush(Color.FromArgb(a, sb.Color.R, sb.Color.G, sb.Color.B));
            }
        }
        catch { /* designer / pre-app — fall through to literal */ }
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    // Cached per-state backgrounds — ApplyAppearance fires on every selection
    // toggle and re-render, so allocating a fresh SolidColorBrush each time
    // burned brush handles + GC for nothing. Colours route through the theme
    // tokens (EmberPrimaryBrush + CoalSurfaceBrush) with the previous ARGB
    // literals as fallbacks.
    private static readonly Brush s_selectedFill =
        ResolveBrushTinted("EmberPrimaryBrush", 0x10, 0xE5, 0xA2, 0x4E);
    private static readonly Brush s_idleFill =
        ResolveBrushTinted("CoalSurfaceBrush", 0x66, 0x14, 0x11, 0x0D);

    // Pre-layout clip sentinel. Comfortably larger than any
    // realistic widget footprint so the construction-time clip never masks
    // content before the first SizeChanged stamps the real ActualSize.
    private const double ClipSentinel = 10000.0;

    private string _widgetName = "";
    private WidgetThumbKind _thumbKind = WidgetThumbKind.Label;
    private bool _isSelected;
    // Base64-encoded PNG (optionally "data:image/png;base64," prefixed)
    // captured at last save via WidgetThumbnailCapture. When non-null, the
    // ThumbnailHost renders the decoded bitmap instead of the gradient /
    // text / label stub — so the thumb host no longer stays a placeholder.
    private string? _thumbnailB64;

    // Inline value-pill state. The LayerWidget the pills
    // mutate plus the commit callback that pushes undo + marks the document
    // dirty + invalidates the layer canvas. The view never reaches up through
    // FindAncestor for the VM: LayerCanvasView calls SetEditTarget(...) at
    // BuildView time so each pill commit is a simple delegate invoke.
    private LayerWidget? _editWidget;
    private Action<LayerWidget>? _onCommit;

    // Inline widget rename. The double-click → TextBox editor reuses
    // the SwapPill/RestorePill visibility-flip mechanics. Unlike the geometry
    // pills (which mutate the model then signal the canvas to PushUndo+MarkDirty),
    // rename hands the *new* name back to LayerCanvasView WITHOUT mutating
    // widget.Name here — the canvas pushes undo against the pre-change state,
    // applies the name, then marks dirty + re-renders. That keeps undo ordering
    // correct (snapshot-before-change) per the task brief.
    private Action<LayerWidget, string>? _onNameCommit;
    private string _baselineName = "";

    // Baseline snapshots — captured on edit-start so Esc rolls back without
    // touching the document and a no-op (baseline == new) skips the undo
    // push. Mirrors SocketViewModel.BeginValuePillEdit / EndValuePillEdit
    // from Architect's NodeView.xaml.cs (copied per the per-pillar chrome rule).
    private int _baselineX;
    private int _baselineY;
    private int _baselineW;
    private int _baselineH;
    private WidgetPreset? _baselinePreset;

    // Per-pill suppression flags — RefreshPills writes the read-only text
    // blocks from the model; without the gate a re-render that arrives while
    // a pill is mid-edit would clobber the user's typed-but-uncommitted text.
    private bool _suppressRefresh;

    public WidgetView()
    {
        InitializeComponent();
        ApplyAppearance();

        // Manifesto §4.4 widget-rect-as-clip-rect. The WidgetFrame
        // hosts a ThumbnailHost that may eventually render images / live
        // previews larger than the author-declared rect; without a clip those
        // overflow the widget's footprint and bleed across siblings on the
        // layer canvas. RectangleGeometry tracks the frame's actual size via
        // SizeChanged so a resize / inspector edit keeps the clip in lockstep
        // (resize-aware).
        if (WidgetFrame is not null)
        {
            // InitializeComponent runs before the first layout pass,
            // so ActualWidth/ActualHeight are still 0 here. Seeding the clip with
            // a 0×0 rect would mask ALL widget content (thumbnail, pills, name)
            // for one frame until OnWidgetFrameSizeChanged corrects it. Seed a
            // large sentinel rect instead so content is fully visible until the
            // first SizeChanged stamps the real footprint over it.
            WidgetFrame.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Rect(0, 0, ClipSentinel, ClipSentinel),
            };
            WidgetFrame.SizeChanged += OnWidgetFrameSizeChanged;
        }
    }

    private void OnWidgetFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WidgetFrame?.Clip is RectangleGeometry rg)
        {
            // Re-stamp the clip rect on every size change. RectangleGeometry's
            // Rect is the read-only-after-set sticking point — easier to assign
            // a fresh Rect than to fight property-changed propagation.
            rg.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        }
    }

    public string WidgetName
    {
        get => _widgetName;
        set
        {
            _widgetName = value;
            if (WidgetNameLabel != null)
                WidgetNameLabel.Text = value;
        }
    }

    public WidgetThumbKind ThumbKind
    {
        get => _thumbKind;
        set
        {
            _thumbKind = value;
            ApplyAppearance();
        }
    }

    /// <summary>
    /// Base64 PNG payload (the same string LayerSerializer round-trips
    /// through <c>LayerWidget.Thumbnail</c>). Accepts an optional
    /// <c>data:image/png;base64,</c> prefix; passing <c>null</c> or empty
    /// reverts to the preset-driven stub (gradient / text / label).
    /// </summary>
    public string? ThumbnailB64
    {
        get => _thumbnailB64;
        set
        {
            if (string.Equals(_thumbnailB64, value, StringComparison.Ordinal)) return;
            _thumbnailB64 = value;
            ApplyAppearance();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            ApplyAppearance();
        }
    }

    private void ApplyAppearance()
    {
        if (WidgetFrame is null) return;

        if (_isSelected)
        {
            // Selected widget border is §2 gold (SelectionBrush #FFD700),
            // not brass Ember (Design_Orders §2: gold=selection).
            WidgetFrame.BorderBrush = Resource("SelectionBrush");
            WidgetFrame.Background  = s_selectedFill;
            if (WidgetNameLabel != null)
                WidgetNameLabel.Foreground = Resource("SelectionBrush");
        }
        else
        {
            WidgetFrame.BorderBrush = Resource("CoalMutedTextBrush");
            WidgetFrame.Background  = s_idleFill;
            if (WidgetNameLabel != null)
                WidgetNameLabel.Foreground = Resource("CoalBodyTextBrush");
        }

        if (ThumbnailHost is null) return;

        // If the model carries a captured thumbnail, prefer it over
        // the preset-driven stub. Falls back to the stub when decoding fails
        // (corrupt payload, missing data prefix variant) so a bad capture
        // doesn't break the canvas.
        if (!string.IsNullOrEmpty(_thumbnailB64))
        {
            var img = BuildThumbnailImage(_thumbnailB64);
            if (img is not null)
            {
                ThumbnailHost.Content = img;
                return;
            }
        }

        ThumbnailHost.Content = _thumbKind switch
        {
            WidgetThumbKind.Gradient => BuildGradientSwatch(),
            WidgetThumbKind.Text     => BuildTextStub(),
            _                        => BuildLabelStub(),
        };
    }

    /// <summary>
    /// Decode the base64 PNG into an Image element. Returns null on
    /// any decode failure (malformed base64, non-PNG payload, etc.) so the
    /// caller falls back to the preset-driven stub. Logging only at the
    /// outermost catch — XAML's BitmapImage exceptions surface via the
    /// SetSource path, which is async so a wrapped try/catch is enough.
    /// </summary>
    private static UIElement? BuildThumbnailImage(string b64)
    {
        try
        {
            // Strip the data-URI prefix if present (WidgetThumbnailCapture
            // emits it, but third-party producers / unit tests might not).
            string raw = b64;
            int comma = raw.IndexOf(',');
            if (comma >= 0 && raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[(comma + 1)..];
            }
            byte[] bytes = Convert.FromBase64String(raw);

            // removed sync-over-async DataWriter.StoreAsync()
            // .GetAwaiter().GetResult() on the UI thread (deadlock risk). Wrap the raw bytes
            // in a MemoryStream and hand the BitmapImage its IRandomAccessStream directly —
            // no async pump is required for an in-memory buffer, so nothing blocks the dispatcher.
            var stream = new MemoryStream(bytes).AsRandomAccessStream();
            stream.Seek(0);

            var bmp = new BitmapImage();
            // SetSourceAsync is fire-and-forget here; on completion the
            // BitmapImage swaps its pixel source in place and the bound
            // Image redraws. If the source is malformed the image stays
            // blank but the canvas keeps working — no exception path.
            _ = bmp.SetSourceAsync(stream);

            return new Image
            {
                Source = bmp,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetView", "BuildThumbnailImage decode", ex);
            return null;
        }
    }

    private static UIElement BuildGradientSwatch()
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint   = new Point(1, 1),
        };
        // Route through the dark-theme ember stops (EmberDeepBrush == #C8822B,
        // Ember600Brush == #7A4710 today) with the previous ARGB literals as
        // fallbacks. Colours stay bit-identical to the pre-fix output.
        g.GradientStops.Add(new GradientStop { Color = ResolveColor("EmberDeepBrush", 0xFF, 0xC8, 0x82, 0x2B), Offset = 0.0 });
        g.GradientStops.Add(new GradientStop { Color = ResolveColor("Ember600Brush",  0xFF, 0x7A, 0x47, 0x10), Offset = 1.0 });
        return new Border { Background = g };
    }

    // Color-flavoured sibling of ResolveBrushTinted — GradientStop wants a
    // Color, not a Brush, so the literal ARGB → theme-key lookup returns the
    // raw struct. Same fallback policy: dictionary lookup wins, falls back to
    // the supplied literal on a missing/non-SolidColorBrush key.
    private static Color ResolveColor(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush sb)
            {
                return Color.FromArgb(a, sb.Color.R, sb.Color.G, sb.Color.B);
            }
        }
        catch { /* designer / pre-app — fall through to literal */ }
        return Color.FromArgb(a, r, g, b);
    }

    private static UIElement BuildTextStub()
    {
        var tb = new TextBlock
        {
            FontSize     = 9,
            Foreground   = Resource("CoalBodyTextBrush"),
            Margin       = new Thickness(8),
            TextWrapping = TextWrapping.Wrap,
            Text         = "ember_ash\nmarigold_77\nloomweaver",
        };
        // FontFamily resource may be a string (the family stack) or a
        // FontFamily already; tolerate both and fall back to a system mono.
        try
        {
            if (Application.Current.Resources["MonoFont"] is FontFamily ff) tb.FontFamily = ff;
            else if (Application.Current.Resources["MonoFont"] is string s) tb.FontFamily = new FontFamily(s);
        }
        catch { /* leave default */ }
        return tb;
    }

    private static UIElement BuildLabelStub() => new Border();

    // ─── inline value pills ──────────────────────────────────────────────
    // The user wants geometry edits on the widget body, not buried in the
    // right-pane inspector. The pill chrome
    // mirrors Architect's MiddleAttributeRowTemplate (NodeView.xaml:236-328)
    // and its OnMiddleAttrPillTapped / OnMiddleAttrPillEditKeyDown /
    // OnMiddleAttrPillEditLostFocus handler shape (NodeView.xaml.cs:594-635).
    // Both copies: Architect and Visualist evolve their paint code
    // independently — no Phoenix.Controls.Shared/UI/ extraction.

    /// <summary>
    /// Wires the pill row to a LayerWidget + commit callback. Called by
    /// LayerCanvasView.BuildView. The commit callback runs on Enter / blur
    /// AFTER the model mutation has already landed — it's the layer canvas's
    /// signal to push undo, mark the document dirty, and re-render so the
    /// new geometry takes effect on the surface.
    /// </summary>
    public void SetEditTarget(LayerWidget widget, Action<LayerWidget> onCommit)
    {
        _editWidget = widget;
        _onCommit   = onCommit;
        RefreshPills();
    }

    /// <summary>
    /// Wires the double-click rename commit. <paramref name="onNameCommit"/>
    /// receives the widget plus the newly typed name; the host (LayerCanvasView)
    /// is responsible for PushUndo (pre-change snapshot) → set widget.Name →
    /// MarkDirty → re-render. WidgetView never mutates widget.Name itself so the
    /// undo snapshot is taken before the change lands.
    /// </summary>
    public void SetNameCommit(Action<LayerWidget, string> onNameCommit)
    {
        _onNameCommit = onNameCommit;
    }

    // ─── enter-widget restore — body double-tap → open the editor ───
    // Pre-WinUI parity: the WinForms LayerCanvas raised OnWidgetDoubleClicked
    // from a double-click on a widget rectangle, which MainForm.OpenWidgetEditor
    // turned into a per-widget WidgetEditorForm. The WinUI rework dropped that
    // gesture (the only DoubleTapped left renames the name footer), severing the
    // canvas → editor transition. We restore it on the widget BODY here; the
    // canvas bubbles EnterRequested up to MainView (see LayerCanvasView /
    // MainView). Visualist-local.

    /// <summary>
    /// Raised when the user double-taps the widget body to enter its node
    /// editor. Distinct from rename (name footer) and geometry edits (pills).
    /// </summary>
    public event Action<LayerWidget>? EnterRequested;

    private void OnWidgetBodyDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_editWidget is null) return;
        // The footer name label consumes its own double-tap (rename) with
        // e.Handled=true, so it never reaches here. The inline geometry pills
        // are single-tap, so a double-tap that lands on a pill WOULD bubble to
        // this body handler — ignore those so a pill edit doesn't also enter the
        // editor. Everything else on the body (thumbnail / live preview) enters.
        if (IsWithin(e.OriginalSource as DependencyObject, PillStrip)) return;
        EnterRequested?.Invoke(_editWidget);
        e.Handled = true;
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Re-reads x/y/w/h/preset from the bound widget and re-paints
    /// the pill labels. No-ops while a pill is mid-edit so the user's typed
    /// text isn't clobbered by a model-side re-render race.</summary>
    public void RefreshPills()
    {
        if (_editWidget is null) return;
        if (_suppressRefresh) return;
        PillXText.Text = _editWidget.Rect.X.ToString();
        PillYText.Text = _editWidget.Rect.Y.ToString();
        PillWText.Text = _editWidget.Rect.Width.ToString();
        PillHText.Text = _editWidget.Rect.Height.ToString();
        PillPresetText.Text = _editWidget.Preset?.ToString() ?? "(none)";
    }

    // ── X pill ──────────────────────────────────────────────────────────
    private void OnPillXTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editWidget is null) return;
        _baselineX = _editWidget.Rect.X;
        SwapPill(PillXText, PillXBox, _baselineX.ToString());
        e.Handled = true;
    }

    private void OnPillXKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)      { CommitX(false); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter)  { CommitX(true);  e.Handled = true; }
    }

    private void OnPillXLostFocus(object sender, RoutedEventArgs e) => CommitX(true);

    private void CommitX(bool commit)
    {
        if (_editWidget is null) { RestorePill(PillXText, PillXBox); return; }
        if (!commit) { _editWidget.Rect.X = _baselineX; RestorePill(PillXText, PillXBox); return; }
        // Clamp BEFORE the baseline compare so a typed-but-out-of-range value
        // (e.g. "-10") that clamps back to the baseline is treated as a no-op
        // instead of committing a silent change + pushing a phantom undo entry.
        if (TryParseInt(PillXBox.Text, out int v))
        {
            v = Math.Max(0, v);
            if (v != _baselineX)
            {
                _suppressRefresh = true;
                _editWidget.Rect.X = v;
                _suppressRefresh = false;
                _onCommit?.Invoke(_editWidget);
            }
        }
        RestorePill(PillXText, PillXBox);
    }

    // ── Y pill ──────────────────────────────────────────────────────────
    private void OnPillYTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editWidget is null) return;
        _baselineY = _editWidget.Rect.Y;
        SwapPill(PillYText, PillYBox, _baselineY.ToString());
        e.Handled = true;
    }

    private void OnPillYKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)      { CommitY(false); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter)  { CommitY(true);  e.Handled = true; }
    }

    private void OnPillYLostFocus(object sender, RoutedEventArgs e) => CommitY(true);

    private void CommitY(bool commit)
    {
        if (_editWidget is null) { RestorePill(PillYText, PillYBox); return; }
        if (!commit) { _editWidget.Rect.Y = _baselineY; RestorePill(PillYText, PillYBox); return; }
        // Clamp before the baseline compare — see CommitX for the rationale.
        if (TryParseInt(PillYBox.Text, out int v))
        {
            v = Math.Max(0, v);
            if (v != _baselineY)
            {
                _suppressRefresh = true;
                _editWidget.Rect.Y = v;
                _suppressRefresh = false;
                _onCommit?.Invoke(_editWidget);
            }
        }
        RestorePill(PillYText, PillYBox);
    }

    // ── Width pill ──────────────────────────────────────────────────────
    private void OnPillWTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editWidget is null) return;
        _baselineW = _editWidget.Rect.Width;
        SwapPill(PillWText, PillWBox, _baselineW.ToString());
        e.Handled = true;
    }

    private void OnPillWKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)      { CommitW(false); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter)  { CommitW(true);  e.Handled = true; }
    }

    private void OnPillWLostFocus(object sender, RoutedEventArgs e) => CommitW(true);

    private void CommitW(bool commit)
    {
        if (_editWidget is null) { RestorePill(PillWText, PillWBox); return; }
        if (!commit) { _editWidget.Rect.Width = _baselineW; RestorePill(PillWText, PillWBox); return; }
        if (TryParseInt(PillWBox.Text, out int v))
        {
            // Width / Height clamp to >=1 to match the inspector's NumberBox
            // Minimum="1" — a zero / negative footprint would render an
            // invisible widget the user couldn't recover by clicking. Clamp
            // before the baseline compare so an out-of-range value that clamps
            // back to the baseline is a no-op (see CommitX for the rationale).
            v = Math.Max(1, v);
            if (v != _baselineW)
            {
                _suppressRefresh = true;
                _editWidget.Rect.Width = v;
                _suppressRefresh = false;
                _onCommit?.Invoke(_editWidget);
            }
        }
        RestorePill(PillWText, PillWBox);
    }

    // ── Height pill ─────────────────────────────────────────────────────
    private void OnPillHTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editWidget is null) return;
        _baselineH = _editWidget.Rect.Height;
        SwapPill(PillHText, PillHBox, _baselineH.ToString());
        e.Handled = true;
    }

    private void OnPillHKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)      { CommitH(false); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter)  { CommitH(true);  e.Handled = true; }
    }

    private void OnPillHLostFocus(object sender, RoutedEventArgs e) => CommitH(true);

    private void CommitH(bool commit)
    {
        if (_editWidget is null) { RestorePill(PillHText, PillHBox); return; }
        if (!commit) { _editWidget.Rect.Height = _baselineH; RestorePill(PillHText, PillHBox); return; }
        if (TryParseInt(PillHBox.Text, out int v))
        {
            // Clamp before the baseline compare so an out-of-range value that clamps
            // back to the baseline is a no-op (see CommitX for the rationale).
            v = Math.Max(1, v);
            if (v != _baselineH)
            {
                _suppressRefresh = true;
                _editWidget.Rect.Height = v;
                _suppressRefresh = false;
                _onCommit?.Invoke(_editWidget);
            }
        }
        RestorePill(PillHText, PillHBox);
    }

    // ── Preset pill ─────────────────────────────────────────────────────
    // Pops a Flyout listing the WidgetPreset enum (+ a "(none)" slot for
    // the nullable WidgetPreset?). Selection commits via _onCommit. Mirrors
    // the bool-glyph branch of Architect's MiddleAttributeRowTemplate in
    // spirit: a click commits a discrete choice rather than free-form text.
    private static readonly WidgetPreset?[] s_pillPresets = new WidgetPreset?[]
    {
        null,
        WidgetPreset.Image, WidgetPreset.Video, WidgetPreset.Text, WidgetPreset.Audio,
        WidgetPreset.WebSource, WidgetPreset.Particles, WidgetPreset.Chat, WidgetPreset.CC,
    };

    private void OnPillPresetTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editWidget is null) return;
        _baselinePreset = _editWidget.Preset;

        try
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                IsItemClickEnabled = true,
                MaxHeight = 240,
            };
            foreach (var p in s_pillPresets)
                list.Items.Add(p?.ToString() ?? "(none)");
            int idx = Array.IndexOf(s_pillPresets, _editWidget.Preset);
            if (idx >= 0) list.SelectedIndex = idx;

            var flyout = new Flyout
            {
                Content = list,
                Placement = FlyoutPlacementMode.Bottom,
            };
            list.ItemClick += (_, args) =>
            {
                int i = list.Items.IndexOf(args.ClickedItem);
                if (i >= 0 && i < s_pillPresets.Length)
                {
                    WidgetPreset? newP = s_pillPresets[i];
                    // Only Hide() on a genuine change. Clicking the
                    // already-selected entry is a no-op, so leaving the flyout
                    // open gives the user a clear signal the click changed nothing
                    // (vs. the old behaviour where any click slammed it shut with
                    // no visual feedback). A real change commits and closes.
                    if (newP != _baselinePreset && _editWidget is not null)
                    {
                        _editWidget.Preset = newP;
                        _onCommit?.Invoke(_editWidget);
                        flyout.Hide();
                    }
                }
            };
            flyout.ShowAt(PillPresetText);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetView", "OnPillPresetTapped", ex);
        }
        e.Handled = true;
    }

    // ── Inline rename ────────────────────────────────────────────────────
    // Double-tap the footer name label → swap to an inline TextBox. Enter or
    // focus-loss-with-change commits via _onNameCommit (LayerCanvasView pushes
    // undo against the pre-change state, sets widget.Name, marks dirty,
    // re-renders). Esc — or focus-loss with no change — reverts silently.
    // Mirrors the SwapPill / RestorePill flip used by the geometry pills.

    // True only while a commit/revert is being applied programmatically, so the
    // LostFocus that fires when we collapse the box doesn't double-commit.
    private bool _renameClosing;

    private void OnWidgetNameDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_editWidget is null) return;
        if (WidgetNameLabel is null || WidgetNameBox is null) return;
        _baselineName = _editWidget.Name ?? "";
        SwapPill(WidgetNameLabel, WidgetNameBox, _baselineName);
        e.Handled = true;
    }

    private void OnWidgetNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)     { CommitName(false); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter) { CommitName(true);  e.Handled = true; }
    }

    private void OnWidgetNameLostFocus(object sender, RoutedEventArgs e)
    {
        // Skip the re-entrant LostFocus raised while we collapse the box during
        // an explicit Enter / Esc commit — that path already settled the name.
        if (_renameClosing) return;
        CommitName(true);
    }

    private void CommitName(bool commit)
    {
        if (WidgetNameLabel is null || WidgetNameBox is null) return;
        if (_editWidget is null) { CloseRename(); return; }

        if (commit)
        {
            string typed = (WidgetNameBox.Text ?? "").Trim();
            // Empty rename is a no-op — keeps the widget from losing its handle
            // / inspector label. A genuine "(unnamed)" widget is represented by
            // an empty Name in the model already, so only commit a non-empty,
            // changed value.
            if (typed.Length > 0 && !string.Equals(typed, _baselineName, StringComparison.Ordinal))
            {
                _onNameCommit?.Invoke(_editWidget, typed);
            }
        }

        CloseRename();
    }

    private void CloseRename()
    {
        _renameClosing = true;
        try { RestorePill(WidgetNameLabel, WidgetNameBox); }
        finally { _renameClosing = false; }
    }

    // ── pill swap helpers ───────────────────────────────────────────────
    // Swap the TextBlock ↔ TextBox visibility pair on edit-start / commit.
    // Mirrors the Visibility-binding flip Architect drives off SocketViewModel.IsEditing;
    // here the view owns the state directly since there's no per-pill VM.
    private static void SwapPill(TextBlock readText, TextBox editBox, string seed)
    {
        editBox.Text = seed;
        readText.Visibility = Visibility.Collapsed;
        editBox.Visibility  = Visibility.Visible;
        try
        {
            // Programmatic focus pattern from Architect's pill flow — focus the
            // box on edit-start so the user can type immediately. SelectAll so
            // typing a digit replaces the value instead of appending.
            editBox.Focus(FocusState.Programmatic);
            editBox.SelectAll();
        }
        catch { /* designer / pre-realised tree */ }
    }

    private static void RestorePill(TextBlock readText, TextBox editBox)
    {
        editBox.Visibility  = Visibility.Collapsed;
        readText.Visibility = Visibility.Visible;
    }

    private static bool TryParseInt(string? s, out int v)
        => int.TryParse(s, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out v);

    // ─── Live-preview overlay ────────────────────────────────────────────
    // LayerCanvasView's WidgetCanvasPreviewer crops this widget's rect out of
    // the hidden-WebView2 Hub overlay frame and hands the cropped SoftwareBitmap
    // here. We wrap it in a SoftwareBitmapSource (the only WriteableBitmap-free
    // source that accepts a SoftwareBitmap directly) and show LivePreviewImage
    // over the gradient/text/label stub at the XAML-declared 0.85 opacity.
    //
    // The overlay is purely additive: when the previewer is off / the layer is
    // unsaved / a crop fails, ClearLivePreview() collapses the image and the
    // existing labeled-rectangle rendering shows through unchanged. This lives
    // Visualist-side.

    /// <summary>
    /// Show a cropped live-preview frame over this widget. Takes ownership of
    /// <paramref name="bmp"/> — the SoftwareBitmapSource copies it during
    /// SetBitmapAsync, but we dispose the source's prior frame and the supplied
    /// bitmap once the swap completes so the canvas's per-widget crops don't
    /// accumulate. Pass null (or call <see cref="ClearLivePreview"/>) to hide the
    /// overlay and fall back to the stub. Never throws — failures log + clear.
    /// </summary>
    public async void SetLivePreview(Windows.Graphics.Imaging.SoftwareBitmap? bmp)
    {
        if (LivePreviewImage is null) { bmp?.Dispose(); return; }
        if (bmp is null) { ClearLivePreview(); return; }
        // SoftwareBitmapSource.SetBitmapAsync requires Bgra8 / Premultiplied;
        // WidgetCanvasPreviewer.CropWidgetAsync already produces exactly that,
        // but convert defensively so a future caller can't trip the contract.
        // src is captured outside the try so the finally can dispose the
        // converted frame too — otherwise a SetBitmapAsync fault would leak it.
        Windows.Graphics.Imaging.SoftwareBitmap src = bmp;
        // Track the SoftwareBitmapSource outside the try so a
        // SetBitmapAsync fault disposes the wrapper instead of leaving it for
        // GC. On the success path the source becomes LivePreviewImage.Source
        // and `source` is nulled out so the catch doesn't tear down the live
        // overlay we just attached.
        Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource? source = null;
        try
        {
            if (src.BitmapPixelFormat != Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8
                || src.BitmapAlphaMode != Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
            {
                src = Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                    bmp,
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            }

            source = new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
            await source.SetBitmapAsync(src);

            LivePreviewImage.Source = source;
            LivePreviewImage.Visibility = Visibility.Visible;
            // Show the LIVE preview ONLY, at full opacity, and HIDE the baked
            // thumbnail beneath it. The overlay used to sit at 0.85 opacity over the
            // ThumbnailHost (which renders the previously-baked snapshot), so a snapshot
            // baked while it was opaque/white bled straight through the transparent live
            // crop → "still shows the stupid snapshot / white background instead of a
            // live preview + transparent bg". Opaque live crop + collapsed thumbnail =
            // the widget's transparent regions show the stage, like OBS.
            LivePreviewImage.Opacity = 1.0;
            if (ThumbnailHost is not null) ThumbnailHost.Visibility = Visibility.Collapsed;
            // Ownership transferred to LivePreviewImage — clear the local so the
            // catch's defensive dispose can't reach the attached source.
            source = null;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetView", "SetLivePreview", ex);
            // Dispose the orphaned wrapper if SetBitmapAsync threw after it was
            // constructed but before ownership transferred to the Image.
            try { source?.Dispose(); } catch { }
            ClearLivePreview();
        }
        finally
        {
            // SetBitmapAsync copied the pixels; the originals are now free.
            // Dispose both the (possibly converted) src and the supplied bmp on
            // every path so neither frame leaks on the exception branch.
            if (!ReferenceEquals(src, bmp)) { try { src.Dispose(); } catch { } }
            try { bmp.Dispose(); } catch { }
        }
    }

    /// <summary>Hide the live-preview overlay so the gradient/text/label stub
    /// shows through again. Idempotent.</summary>
    public void ClearLivePreview()
    {
        if (LivePreviewImage is null) return;
        LivePreviewImage.Source = null;
        LivePreviewImage.Visibility = Visibility.Collapsed;
        // Restore the thumbnail/stub fallback only when the live preview is
        // genuinely gone (previewer off, unsaved layer, crop failed) so the widget
        // isn't an empty rectangle. When a live frame is present the thumbnail stays
        // hidden (set in SetLivePreview) so the baked snapshot can't bleed through.
        if (ThumbnailHost is not null) ThumbnailHost.Visibility = Visibility.Visible;
    }

    // ─── Clean thumbnail capture (breaks the baked-thumbnail feedback loop) ──
    //
    // CaptureAllWidgetThumbnailsAsync renders this whole WidgetView to a bitmap
    // and bakes it into LayerWidget.Thumbnail. ThumbnailHost.Content shows the
    // PREVIOUSLY baked thumbnail, so a naive capture re-captures the last baked
    // image and re-bakes it — a self-perpetuating loop. If an error-page frame
    // (Hub down at save time) ever gets baked once, it then re-bakes on EVERY
    // save and shows forever behind the live render (Majo's persistent
    // "404 behind the widget", visible even when the bus is back up).
    //
    // Begin/End wrap the capture so it records ONLY the current live render:
    // the stale ThumbnailHost + editor chrome (pills, name footer) are hidden,
    // and the live overlay is drawn at full opacity. Returns false when there is
    // no valid live frame — the caller then SKIPS the bake and keeps the
    // existing thumbnail rather than baking an empty stub.
    private Visibility _capSavedThumbHostVis;
    private Visibility _capSavedPillStripVis;
    private Visibility _capSavedNameLabelVis;
    private double     _capSavedLiveOpacity;

    public bool BeginThumbnailCapture()
    {
        bool hasLiveFrame = LivePreviewImage is not null
                            && LivePreviewImage.Visibility == Visibility.Visible
                            && LivePreviewImage.Source is not null;
        if (!hasLiveFrame) return false;

        if (ThumbnailHost is not null)
        {
            _capSavedThumbHostVis = ThumbnailHost.Visibility;
            ThumbnailHost.Visibility = Visibility.Collapsed;
        }
        if (PillStrip is not null)
        {
            _capSavedPillStripVis = PillStrip.Visibility;
            PillStrip.Visibility = Visibility.Collapsed;
        }
        if (WidgetNameLabel is not null)
        {
            _capSavedNameLabelVis = WidgetNameLabel.Visibility;
            WidgetNameLabel.Visibility = Visibility.Collapsed;
        }
        _capSavedLiveOpacity = LivePreviewImage!.Opacity;
        LivePreviewImage.Opacity = 1.0;
        return true;
    }

    public void EndThumbnailCapture()
    {
        if (ThumbnailHost   is not null) ThumbnailHost.Visibility   = _capSavedThumbHostVis;
        if (PillStrip       is not null) PillStrip.Visibility       = _capSavedPillStripVis;
        if (WidgetNameLabel is not null) WidgetNameLabel.Visibility = _capSavedNameLabelVis;
        if (LivePreviewImage is not null) LivePreviewImage.Opacity  = _capSavedLiveOpacity;
    }
}
