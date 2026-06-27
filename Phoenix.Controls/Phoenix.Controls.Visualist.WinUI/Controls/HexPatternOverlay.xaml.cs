using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

// Alias avoids the implicit-usings clash with System.IO.Path —
// `Microsoft.UI.Xaml.Shapes.Path` is the shape primitive we want.
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

// Tiled flat-top hex grid, stroke-only. Sized to fill its parent on
// SizeChanged. COPIED from Hub.WinUI's HexPatternOverlay per
// feedback_visualist_architect_chrome_independence (the two pillars never share
// paint code) and adapted for use as the Visualist layer-canvas pasteboard
// backdrop — the "Hex" preview-background variant paints this around the layer
// rect so the layer bounds are visible against the textured pasteboard, while the
// layer interior stays flat ("hex outside, flat inside").
//
// WinUI 3 has no DrawingBrush / TileBrush equivalent, so a static SVG asset can't
// be tiled without re-rasterising. Painting Path elements into a Canvas at layout
// time is the cheapest way to get a vector hex grid that adapts to resize without
// an asset-pipeline detour. Repaints only fire on SizeChanged, so steady-state
// cost is zero.
public sealed partial class HexPatternOverlay : UserControl
{
    // UI-thread debounce so a SizeChanged storm during a window resize-drag
    // triggers RebuildGrid exactly once after the user settles, instead of running
    // the Path allocation on every intermediate frame. DispatcherTimer (not
    // Threading.Timer) so Tick lands on the UI thread directly — RebuildGrid
    // mutates HexCanvas.Children and would crash on a worker.
    private readonly DispatcherTimer _rebuildDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(150),
    };

    public HexPatternOverlay()
    {
        InitializeComponent();
        _rebuildDebounce.Tick += OnRebuildDebounceTick;
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
    }

    // Radius of a single hex (distance from centre to a corner) in DIPs.
    public double HexRadius
    {
        get => (double)GetValue(HexRadiusProperty);
        set => SetValue(HexRadiusProperty, value);
    }
    public static readonly DependencyProperty HexRadiusProperty =
        DependencyProperty.Register(nameof(HexRadius), typeof(double), typeof(HexPatternOverlay),
            new PropertyMetadata(28.0, OnGridParamChanged));

    // Stroke colour. Defaults to coal-paper at ~7% alpha — discreet by design.
    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }
    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush), typeof(HexPatternOverlay),
            new PropertyMetadata(
                new SolidColorBrush(Color.FromArgb(0x12, 0xF5, 0xEF, 0xE3)),
                OnGridParamChanged));

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(HexPatternOverlay),
            new PropertyMetadata(1.0, OnGridParamChanged));

    private static void OnGridParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HexPatternOverlay self) self.RebuildGrid();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // First paint (or recovery after Unloaded → Loaded with the canvas cleared)
        // bypasses the debounce so the very first layout pass actually renders the
        // grid — otherwise the overlay flashes blank for ~150 ms.
        if (HexCanvas is not null && HexCanvas.Children.Count == 0)
        {
            RebuildGrid();
            return;
        }

        _rebuildDebounce.Stop();
        _rebuildDebounce.Start();
    }

    private void OnRebuildDebounceTick(object? sender, object e)
    {
        _rebuildDebounce.Stop();
        RebuildGrid();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop the debounce so a tick scheduled during the last resize burst can't
        // fire after the control is detached. Subscriptions stay wired — WinUI
        // controls can go Loaded → Unloaded → Loaded again on re-parenting.
        _rebuildDebounce.Stop();
    }

    private void RebuildGrid()
    {
        if (HexCanvas is null) return;
        HexCanvas.Children.Clear();

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0 || HexRadius <= 0) return;

        // Clip to our own bounds. The grid intentionally bleeds one tile past each
        // edge (rowStart/colStart = -1, i.e. NEGATIVE coordinates) so a hex never
        // visibly clips at the corners — but a WinUI Canvas does NOT clip its
        // children, and neither does the parent Grid clip a row's content, so those
        // bleed hexes would render ABOVE/around the overlay and (in Visualist, where
        // this sits in the stage row beneath the command bar) spill OVER the toolbar,
        // hiding the backdrop swatches. Clipping the Canvas to (0,0,w,h) keeps the
        // bleed invisible while still covering the corners. (Hub never hit this — it
        // uses the overlay full-window where there's nothing above it to overlap.)
        HexCanvas.Clip = new RectangleGeometry
        {
            Rect = new global::Windows.Foundation.Rect(0, 0, w, h),
        };

        // Flat-top hex math:
        //   width  = 2 * r
        //   x-step = 1.5 * r   (column pitch)
        //   y-step = sqrt(3) * r
        //   odd columns shift by y-step / 2
        double r = HexRadius;
        double xStep = 1.5 * r;
        double yStep = Math.Sqrt(3.0) * r;
        double yHalf = yStep * 0.5;

        // Bleed one tile past each edge so a hex never visibly clips at the corners.
        int colStart = -1;
        int colEnd   = (int)Math.Ceiling(w / xStep) + 1;
        int rowStart = -1;
        int rowEnd   = (int)Math.Ceiling(h / yStep) + 1;

        var stroke = StrokeBrush;
        double thickness = StrokeThickness;

        for (int col = colStart; col <= colEnd; col++)
        {
            double cx = col * xStep;
            double yOffset = (col & 1) == 0 ? 0 : yHalf;
            for (int row = rowStart; row <= rowEnd; row++)
            {
                double cy = row * yStep + yOffset;
                var hex = BuildHexPath(cx, cy, r, stroke, thickness);
                HexCanvas.Children.Add(hex);
            }
        }
    }

    // Flat-top hexagon — 6 vertices computed analytically (cheaper than Beziers,
    // since hex sides are straight lines).
    private static XamlPath BuildHexPath(double cx, double cy, double r, Brush stroke, double thickness)
    {
        double half = r * 0.5;
        double yEdge = Math.Sqrt(3.0) * 0.5 * r;

        var figure = new PathFigure
        {
            StartPoint = new global::Windows.Foundation.Point(cx - r, cy),
            IsClosed = true,
            IsFilled = false,
        };
        figure.Segments.Add(new LineSegment { Point = new global::Windows.Foundation.Point(cx - half, cy - yEdge) });
        figure.Segments.Add(new LineSegment { Point = new global::Windows.Foundation.Point(cx + half, cy - yEdge) });
        figure.Segments.Add(new LineSegment { Point = new global::Windows.Foundation.Point(cx + r,    cy) });
        figure.Segments.Add(new LineSegment { Point = new global::Windows.Foundation.Point(cx + half, cy + yEdge) });
        figure.Segments.Add(new LineSegment { Point = new global::Windows.Foundation.Point(cx - half, cy + yEdge) });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new XamlPath
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = thickness,
            Fill = null,
        };
    }
}
