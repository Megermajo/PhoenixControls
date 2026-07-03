using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

// Alias avoids the implicit-usings clash with System.IO.Path —
// `Microsoft.UI.Xaml.Shapes.Path` is the shape primitive we want.
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Phoenix.Controls.Hub.WinUI.Controls;

// Tiled flat-top hex grid, stroke-only. Sized to fill its parent on
// SizeChanged. Designed as a discreet decorative overlay for the Hub
// workspace background — never neon, never hit-testable.
//
// WinUI 3 has no DrawingBrush / TileBrush equivalent, so a static SVG
// asset can't be tiled without re-rasterising. Painting Path elements
// into a Canvas at layout time is the cheapest way to get a vector hex
// grid that adapts to window resize without an asset-pipeline detour.
//
// Repaint cost: ~hexRadius⁻² hex paths per area, each a 6-point Path.
// At the default 28px radius a 1920×1080 canvas materialises ~2200
// paths — well within WinUI 3's Canvas-child budget. Repaints only
// fire on SizeChanged, so steady-state cost is zero.
public sealed partial class HexPatternOverlay : UserControl
{
    // UI-thread debounce so a SizeChanged storm during a window
    // resize-drag triggers RebuildGrid exactly once after the user settles,
    // instead of running the ~2200-Path allocation on every intermediate
    // frame. 150 ms sits in the TODO.md spec window (100–200 ms): long
    // enough that a continuous drag coalesces into a single rebuild, short
    // enough that the grid catches up to the final layout without a
    // perceptible lag once the user releases. DispatcherTimer (not
    // Threading.Timer) so Tick lands on the UI thread directly —
    // RebuildGrid mutates HexCanvas.Children and would crash on a worker.
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
    // 28 is the visual sweet spot for the Hub workspace: large enough that
    // the grid reads as architecture rather than noise, small enough that
    // four panels' worth of canvas tiles smoothly without an obvious seam.
    public double HexRadius
    {
        get => (double)GetValue(HexRadiusProperty);
        set => SetValue(HexRadiusProperty, value);
    }
    public static readonly DependencyProperty HexRadiusProperty =
        DependencyProperty.Register(nameof(HexRadius), typeof(double), typeof(HexPatternOverlay),
            new PropertyMetadata(28.0, OnGridParamChanged));

    // Stroke colour. Defaults to coal-paper at ~7% alpha — discreet by
    // design (alpha ~5–8%).
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
        // First paint (or recovery after Unloaded → Loaded with the canvas
        // cleared) bypasses the debounce so the very first layout pass
        // actually renders the grid — otherwise the overlay flashes blank
        // for ~150 ms while the timer is in flight. Subsequent re-layouts
        // during a resize-drag coalesce through the debounce so we don't
        // allocate ~2200 Path elements per intermediate SizeChanged frame.
        if (HexCanvas is not null && HexCanvas.Children.Count == 0)
        {
            RebuildGrid();
            return;
        }

        // Reset the debounce window — Stop+Start restarts the interval; a
        // no-op when the timer isn't currently running.
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
        // Stop the debounce so a tick scheduled during the last resize
        // burst can't fire after the control is detached. Tick handlers and
        // SizeChanged/Unloaded subscriptions stay wired — WinUI controls
        // can go Loaded → Unloaded → Loaded again on parent re-parenting,
        // and we want the overlay to keep tracking SizeChanged through
        // that lifecycle. The timer is the only state that holds a
        // delayed callback into RebuildGrid; stopping it is the
        // load-bearing cleanup step.
        _rebuildDebounce.Stop();
    }

    private void RebuildGrid()
    {
        if (HexCanvas is null) return;
        HexCanvas.Children.Clear();

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0 || HexRadius <= 0) return;

        // Flat-top hex math:
        //   width  = 2 * r
        //   height = sqrt(3) * r
        //   x-step = 1.5 * r   (column pitch)
        //   y-step = sqrt(3) * r
        //   odd columns shift by y-step / 2
        double r = HexRadius;
        double xStep = 1.5 * r;
        double yStep = Math.Sqrt(3.0) * r;
        double yHalf = yStep * 0.5;

        // Bleed one tile past each edge so a hex never visibly clips at the
        // viewport corners. Otherwise the grid reads as floating instead of
        // tiled-to-edge.
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

    // Flat-top hexagon — 6 vertices computed analytically (cheaper than
    // PathFigure / Beziers, since hex sides are straight lines).
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
