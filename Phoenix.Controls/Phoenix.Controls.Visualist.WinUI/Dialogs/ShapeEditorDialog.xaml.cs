using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// Visualist WinUI regression audit 2026-05-31 (Area 3 P0) — port of the
/// pre-T15 WinForms <c>ShapeEditor</c> (Phoenix.Controls.Visualist/Controls/
/// ShapeEditor.cs). Modal <see cref="ContentDialog"/> that authors the
/// <c>Vertices</c> JSON list on a <c>Mask.Polygon</c> / <c>Mask.Bezier</c>
/// node.
///
/// <para>
/// Coordinate space inside the editor is normalised 0..1 to match the kernel
/// contract; the canvas paints at a fixed surface (≈600×400) but every drag
/// clamps the underlying vertex coords to the unit square. Bezier mode is
/// auto-detected from the node title — <c>Mask.Bezier</c> shows the cp1/cp2
/// handles for the selected vertex; <c>Mask.Polygon</c> hides them.
/// </para>
///
/// <para>
/// API surface mirrors the baseline + <see cref="CurveEditorDialog"/>: the
/// dialog mutates the passed node only on the OK (Primary) button, leaves it
/// untouched on Cancel, and exposes <see cref="OnRequestAnimateVertex"/> so
/// the caller (graph canvas / editor view) can append timeline keyframes —
/// the editor itself never touches the timeline (same separation as
/// <c>WidgetGraphCanvas.OnRequestAnimateParameter</c>).
/// </para>
///
/// <para>
/// Visualist-local per feedback_visualist_architect_chrome_independence.md —
/// no Shared lift. Guardrails (min/max vertex limits) route through
/// <see cref="GlobalLogger"/> rather than nested modals per
/// feedback_no_modal_dialogs_for_repeatable_rejections.md.
/// </para>
/// </summary>
public sealed partial class ShapeEditorDialog : ContentDialog
{
    private readonly Node   _node;
    private readonly bool   _isBezier;
    private readonly string _origVerticesJson;
    private readonly string _origClosed;

    private List<ShapeData.Vertex> _verts;
    private bool _closed;
    private int  _selected = -1;

    // Editor-local undo / redo — each snapshot captures (vertices JSON, closed).
    private readonly Stack<(string Json, bool Closed)> _undo = new();
    private readonly Stack<(string Json, bool Closed)> _redo = new();

    // Re-entrancy guard so programmatic ClosedToggle / numeric-box writes don't
    // re-fire their own change handlers back into a snapshot.
    private bool _suppressEvents;

    // Drag state on the surface. -1 / null = no drag.
    private int _dragVertex = -1;
    private string? _dragHandle; // "cp1" | "cp2" | null

    private const double VertexRadius = 6.0;
    private const double HandleRadius = 4.0;
    private const double HitSlop      = 4.0;
    private const double SurfacePad   = 8.0;

    /// <summary>
    /// Fires when the user clicks "Animate Vertex" (or the per-vertex
    /// right-click "Animate" item). Args: (vertexIndex, currentX, currentY,
    /// isBezier). The caller appends keyframes at <c>TimeMs=0</c> with the
    /// <see cref="ShapeData.FormatVertexPath"/> grammar.
    /// </summary>
    public event Action<int, double, double, bool>? OnRequestAnimateVertex;

    public ShapeEditorDialog(Node node)
    {
        InitializeComponent();
        _node = node ?? throw new ArgumentNullException(nameof(node));

        _isBezier         = string.Equals(node.Title, "Mask.Bezier", StringComparison.Ordinal);
        _origVerticesJson = node.Attributes.TryGetValue("Vertices", out var vj) ? vj : "[]";
        _origClosed       = node.Attributes.TryGetValue("Closed",   out var cl) ? cl : "true";

        _verts = ShapeData.Parse(_origVerticesJson);
        if (_verts.Count == 0)
            _verts = _isBezier ? ShapeData.DefaultBezier() : ShapeData.DefaultPolygon();
        _closed = !string.Equals(_origClosed, "false", StringComparison.OrdinalIgnoreCase);

        Title = _isBezier
            ? Localizer.T("visualist.shape.title.bezier", "Edit Bezier Shape")
            : Localizer.T("visualist.shape.title.polygon", "Edit Polygon Shape");
        EyebrowText.Text  = _isBezier ? "VISUALIST · BEZIER" : "VISUALIST · POLYGON";
        SubtitleText.Text = _isBezier
            ? "Author the bezier mask. Drag a vertex to move; drag the green/yellow handles to shape the curve."
            : "Author the polygon mask. Click empty space to add, drag a vertex to move.";

        // Bezier-only rows.
        BezierHandlePanel.Visibility = _isBezier ? Visibility.Visible : Visibility.Collapsed;

        // R49 — route the remaining user-facing strings through the Localizer
        // (the WinUI port had left the toggle / buttons / section headers / hint
        // hardcoded). Fallbacks preserve the current English text.
        ApplyLocalization();

        _suppressEvents = true;
        ClosedToggle.IsChecked = _closed;
        _suppressEvents = false;

        PrimaryButtonClick += OnOkClick;
        CloseButtonClick   += OnCancelClick;
        KeyDown            += OnDialogKeyDown;

        SyncSelectedFields();
        // First paint happens after the surface gets its size via
        // OnSurfaceSizeChanged; RenderSurface is also safe to call before that
        // (it early-returns on a zero-size surface).
    }

    // R49 — Localizer routing for the strings the XAML had hardcoded.
    private string _defaultHint = "";
    private void ApplyLocalization()
    {
        ClosedToggle.Content        = Localizer.T("visualist.shape.closed_path",    "Closed path");
        AddVertexButton.Content     = Localizer.T("visualist.shape.add_vertex",     "Add Vertex");
        DeleteVertexButton.Content  = Localizer.T("visualist.shape.delete_vertex",  "Delete Vertex");
        AnimateVertexButton.Content = Localizer.T("visualist.shape.animate_vertex", "Animate Vertex");
        SelectedVertexLabel.Text    = Localizer.T("visualist.shape.section.selected", "SELECTED VERTEX");
        BezierHandlesLabel.Text     = Localizer.T("visualist.shape.section.handles",  "BEZIER HANDLES");
        ActionsLabel.Text           = Localizer.T("visualist.shape.section.actions",  "ACTIONS");
        _defaultHint                = Localizer.T("visualist.shape.hint",
            "Ctrl+Z / Ctrl+Y undo-redo · Del removes the selected vertex (min 2).");
        HintText.Text               = _defaultHint;
    }

    // R50 — surface a guard rejection (vertex min/max) as an INLINE hint in the
    // dialog instead of only a System log line the author can't see. Non-modal
    // per feedback_no_modal_dialogs_for_repeatable_rejections; auto-reverts.
    private DispatcherTimer? _hintTimer;
    private void ShowGuardHint(string message)
    {
        if (HintText is null) return;
        HintText.Text       = message;
        HintText.Foreground = Token("ErrBrush", Color.FromArgb(0xFF, 0xC9, 0x53, 0x3C));
        _hintTimer ??= CreateHintTimer();
        _hintTimer.Stop();
        _hintTimer.Start();
    }
    private DispatcherTimer CreateHintTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (HintText is null) return;
            HintText.Text       = _defaultHint;
            HintText.Foreground = Token("CoalMutedTextBrush", Color.FromArgb(0xFF, 0x9E, 0x96, 0x8B));
        };
        return t;
    }

    // ── surface coord mapping ────────────────────────────────────────────

    private Point UnitToSurface(double ux, double uy)
    {
        double w = Math.Max(1.0, ShapeSurface.ActualWidth  - SurfacePad * 2);
        double h = Math.Max(1.0, ShapeSurface.ActualHeight - SurfacePad * 2);
        return new Point(SurfacePad + ux * w, SurfacePad + uy * h);
    }

    private (double ux, double uy) SurfaceToUnit(double sx, double sy)
    {
        double w = Math.Max(1.0, ShapeSurface.ActualWidth  - SurfacePad * 2);
        double h = Math.Max(1.0, ShapeSurface.ActualHeight - SurfacePad * 2);
        double ux = Math.Clamp((sx - SurfacePad) / w, 0, 1);
        double uy = Math.Clamp((sy - SurfacePad) / h, 0, 1);
        return (ux, uy);
    }

    // ── rendering ────────────────────────────────────────────────────────

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => RenderSurface();

    // R51 — drive the Canvas size from its (now-stretchy) frame so the
    // normalised-coord painting re-lays out when the dialog is resized. The
    // frame paints a 1px border, so the Canvas fills the interior.
    private void OnSurfaceFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ShapeSurface is null) return;
        ShapeSurface.Width  = Math.Max(1.0, e.NewSize.Width  - 2);
        ShapeSurface.Height = Math.Max(1.0, e.NewSize.Height - 2);
        // The Canvas SizeChanged (OnSurfaceSizeChanged) repaints.
    }

    private void RenderSurface()
    {
        if (ShapeSurface is null) return;
        ShapeSurface.Children.Clear();

        double sw = ShapeSurface.ActualWidth;
        double sh = ShapeSurface.ActualHeight;
        if (sw <= 0 || sh <= 0) return;

        Brush borderBrush = Token("CoalCardBrush", Color.FromArgb(0xFF, 0x22, 0x1C, 0x16));
        Brush gridBrush   = Token("CoalMutedTextBrush", Color.FromArgb(0x40, 0x9E, 0x96, 0x8B), 0x40);
        Brush strokeBrush = Token("EmberPrimaryBrush", Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E));
        Brush fillBrush   = Token("EmberPrimaryBrush", Color.FromArgb(0x28, 0xE5, 0xA2, 0x4E), 0x28);
        Brush selBrush    = Token("Ember200Brush", Color.FromArgb(0xFF, 0xF2, 0xC7, 0x7F));
        Brush vertBrush   = Token("OkBrush", Color.FromArgb(0xFF, 0x6F, 0xA4, 0x6B));
        Brush vertEdge    = Token("CoalPaperBrush", Color.FromArgb(0xFF, 0xF5, 0xEF, 0xE3));

        // Bounding rectangle of the unit square.
        var tl = UnitToSurface(0, 0);
        var br = UnitToSurface(1, 1);
        ShapeSurface.Children.Add(new Rectangle
        {
            Width  = Math.Max(0, br.X - tl.X),
            Height = Math.Max(0, br.Y - tl.Y),
            Stroke = borderBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        }.At(tl.X, tl.Y));

        // Quarter grid lines.
        for (int i = 1; i < 4; i++)
        {
            var hA = UnitToSurface(i / 4.0, 0);
            var hB = UnitToSurface(i / 4.0, 1);
            ShapeSurface.Children.Add(new Line { X1 = hA.X, Y1 = hA.Y, X2 = hB.X, Y2 = hB.Y, Stroke = gridBrush, StrokeThickness = 0.5, IsHitTestVisible = false });
            var vA = UnitToSurface(0, i / 4.0);
            var vB = UnitToSurface(1, i / 4.0);
            ShapeSurface.Children.Add(new Line { X1 = vA.X, Y1 = vA.Y, X2 = vB.X, Y2 = vB.Y, Stroke = gridBrush, StrokeThickness = 0.5, IsHitTestVisible = false });
        }

        // The path preview (polyline or bezier), mirroring the kernel paint.
        if (_verts.Count >= 2)
        {
            var geom = BuildPathGeometry();
            ShapeSurface.Children.Add(new XamlPath
            {
                Data = geom,
                Stroke = strokeBrush,
                StrokeThickness = 2,
                Fill = fillBrush,
                IsHitTestVisible = false,
            });
        }

        // Bezier handles for the selected vertex (drawn under the dots).
        if (_isBezier && _selected >= 0 && _selected < _verts.Count)
        {
            var v = _verts[_selected];
            var anchor = UnitToSurface(v.X, v.Y);
            if (v.Cp1X.HasValue && v.Cp1Y.HasValue)
                DrawHandle(anchor, UnitToSurface(v.Cp1X.Value, v.Cp1Y.Value), Color.FromArgb(0xFF, 0x6F, 0xC8, 0x6B));
            if (v.Cp2X.HasValue && v.Cp2Y.HasValue)
                DrawHandle(anchor, UnitToSurface(v.Cp2X.Value, v.Cp2Y.Value), Color.FromArgb(0xFF, 0xF2, 0xC7, 0x7F));
        }

        // Vertex dots.
        for (int i = 0; i < _verts.Count; i++)
        {
            var p = UnitToSurface(_verts[i].X, _verts[i].Y);
            ShapeSurface.Children.Add(new Ellipse
            {
                Width  = VertexRadius * 2,
                Height = VertexRadius * 2,
                Fill   = i == _selected ? selBrush : vertBrush,
                Stroke = vertEdge,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            }.At(p.X - VertexRadius, p.Y - VertexRadius));
        }

        VertexCountText.Text = $"{_verts.Count} vert{(_verts.Count == 1 ? "ex" : "ices")}";
    }

    private Geometry BuildPathGeometry()
    {
        var figure = new PathFigure
        {
            StartPoint = UnitToSurface(_verts[0].X, _verts[0].Y),
            IsClosed   = _closed,
            IsFilled   = _closed,
        };

        if (_isBezier)
        {
            for (int i = 1; i < _verts.Count; i++)
                AddBezierSegment(figure, _verts[i - 1], _verts[i]);
            if (_closed && _verts.Count >= 2)
                AddBezierSegment(figure, _verts[_verts.Count - 1], _verts[0]);
        }
        else
        {
            for (int i = 1; i < _verts.Count; i++)
                figure.Segments.Add(new LineSegment { Point = UnitToSurface(_verts[i].X, _verts[i].Y) });
            // Closing edge is implied by IsClosed for the polyline case.
        }

        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        return geom;
    }

    private void AddBezierSegment(PathFigure figure, ShapeData.Vertex a, ShapeData.Vertex b)
    {
        // Outgoing handle of A (cp2) → incoming handle of B (cp1), matching the
        // baseline ShapeCanvas.OnPaint contract.
        var c1 = UnitToSurface(a.Cp2X ?? a.X, a.Cp2Y ?? a.Y);
        var c2 = UnitToSurface(b.Cp1X ?? b.X, b.Cp1Y ?? b.Y);
        var end = UnitToSurface(b.X, b.Y);
        figure.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = end });
    }

    private void DrawHandle(Point anchor, Point handle, Color color)
    {
        ShapeSurface.Children.Add(new Line
        {
            X1 = anchor.X, Y1 = anchor.Y, X2 = handle.X, Y2 = handle.Y,
            Stroke = new SolidColorBrush(Color.FromArgb(0x80, color.R, color.G, color.B)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 3 },
            IsHitTestVisible = false,
        });
        ShapeSurface.Children.Add(new Ellipse
        {
            Width  = HandleRadius * 2,
            Height = HandleRadius * 2,
            Fill   = new SolidColorBrush(color),
            Stroke = Token("CoalPaperBrush", Color.FromArgb(0xFF, 0xF5, 0xEF, 0xE3)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        }.At(handle.X - HandleRadius, handle.Y - HandleRadius));
    }

    // ── hit testing ──────────────────────────────────────────────────────

    private int HitVertex(double sx, double sy)
    {
        double r2 = (VertexRadius + HitSlop) * (VertexRadius + HitSlop);
        for (int i = _verts.Count - 1; i >= 0; i--)
        {
            var p = UnitToSurface(_verts[i].X, _verts[i].Y);
            double dx = sx - p.X, dy = sy - p.Y;
            if (dx * dx + dy * dy <= r2) return i;
        }
        return -1;
    }

    private string? HitHandle(double sx, double sy)
    {
        if (!_isBezier || _selected < 0 || _selected >= _verts.Count) return null;
        var v = _verts[_selected];
        double r2 = (HandleRadius + HitSlop) * (HandleRadius + HitSlop);
        if (v.Cp1X.HasValue && v.Cp1Y.HasValue)
        {
            var p = UnitToSurface(v.Cp1X.Value, v.Cp1Y.Value);
            double dx = sx - p.X, dy = sy - p.Y;
            if (dx * dx + dy * dy <= r2) return "cp1";
        }
        if (v.Cp2X.HasValue && v.Cp2Y.HasValue)
        {
            var p = UnitToSurface(v.Cp2X.Value, v.Cp2Y.Value);
            double dx = sx - p.X, dy = sy - p.Y;
            if (dx * dx + dy * dy <= r2) return "cp2";
        }
        return null;
    }

    // ── pointer ──────────────────────────────────────────────────────────

    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(ShapeSurface);
        if (!pp.Properties.IsLeftButtonPressed) return;
        double sx = pp.Position.X, sy = pp.Position.Y;

        // Handle first — handles overlap the vertex hit zone on the selected vertex.
        var handle = HitHandle(sx, sy);
        if (handle is not null)
        {
            PushUndo();
            _dragHandle = handle;
            try { ShapeSurface.CapturePointer(e.Pointer); } catch { }
            e.Handled = true;
            return;
        }

        int hit = HitVertex(sx, sy);
        if (hit >= 0)
        {
            SetSelected(hit);
            PushUndo();
            _dragVertex = hit;
            try { ShapeSurface.CapturePointer(e.Pointer); } catch { }
            e.Handled = true;
            return;
        }

        // Empty space → append a vertex at the click position.
        var (ux, uy) = SurfaceToUnit(sx, sy);
        AppendVertexAt(ux, uy);
        e.Handled = true;
    }

    private void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragVertex < 0 && _dragHandle is null) return;
        var pos = e.GetCurrentPoint(ShapeSurface).Position;
        var (ux, uy) = SurfaceToUnit(pos.X, pos.Y);
        if (_dragVertex >= 0)
        {
            MoveVertex(_dragVertex, ux, uy);
        }
        else if (_dragHandle is not null)
        {
            MoveHandle(_selected, _dragHandle, ux, uy);
        }
        e.Handled = true;
    }

    private void OnSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragVertex < 0 && _dragHandle is null) return;
        _dragVertex = -1;
        _dragHandle = null;
        try { ShapeSurface.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;
    }

    private void OnSurfaceRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var pos = e.GetPosition(ShapeSurface);
        int hit = HitVertex(pos.X, pos.Y);
        if (hit < 0) return;
        SetSelected(hit);

        var flyout = new MenuFlyout();
        var anim = new MenuFlyoutItem { Text = Localizer.T("visualist.shape.menu.animate_vertex", "Animate this vertex") };
        anim.Click += (_, _) => RaiseAnimateVertex(hit);
        var del = new MenuFlyoutItem { Text = Localizer.T("visualist.shape.menu.delete_vertex", "Delete vertex") };
        del.Click += (_, _) => DeleteSelected();
        flyout.Items.Add(anim);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(del);
        flyout.ShowAt(ShapeSurface, pos);
        e.Handled = true;
    }

    // ── mutation ─────────────────────────────────────────────────────────

    private void SetSelected(int idx)
    {
        if (idx < 0 || idx >= _verts.Count) idx = -1;
        _selected = idx;
        SyncSelectedFields();
        RenderSurface();
    }

    private void MoveVertex(int idx, double x, double y)
    {
        if (idx < 0 || idx >= _verts.Count) return;
        _verts[idx].X = Math.Clamp(x, 0, 1);
        _verts[idx].Y = Math.Clamp(y, 0, 1);
        if (idx == _selected) SyncSelectedFields();
        RenderSurface();
    }

    private void MoveHandle(int idx, string handle, double ux, double uy)
    {
        if (!_isBezier || idx < 0 || idx >= _verts.Count) return;
        double cx = Math.Clamp(ux, 0, 1);
        double cy = Math.Clamp(uy, 0, 1);
        var v = _verts[idx];
        if (handle == "cp1") { v.Cp1X = cx; v.Cp1Y = cy; }
        else                 { v.Cp2X = cx; v.Cp2Y = cy; }
        if (idx == _selected) SyncSelectedFields();
        RenderSurface();
    }

    private void AppendVertexAt(double x, double y)
    {
        if (_verts.Count >= ShapeData.MaxVertices)
        {
            GlobalLogger.Log(
                $"Shape editor: vertex limit ({ShapeData.MaxVertices}) reached on '{_node.Title}' — add ignored.",
                "ShapeEditorDialog", LogLevel.System);
            ShowGuardHint(Localizer.T("visualist.shape.guard.max",
                $"Vertex limit reached ({ShapeData.MaxVertices}) — can't add more."));
            return;
        }
        PushUndo();
        var nv = _isBezier
            ? new ShapeData.Vertex { X = x, Y = y, Cp1X = x, Cp1Y = y, Cp2X = x, Cp2Y = y }
            : new ShapeData.Vertex { X = x, Y = y };
        _verts.Add(nv);
        _selected = _verts.Count - 1;
        SyncSelectedFields();
        RenderSurface();
    }

    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _verts.Count) return;
        if (_verts.Count <= 2)
        {
            GlobalLogger.Log(
                $"Shape editor: minimum 2 vertices required on '{_node.Title}' — delete ignored.",
                "ShapeEditorDialog", LogLevel.System);
            ShowGuardHint(Localizer.T("visualist.shape.guard.min",
                "A shape needs at least 2 vertices — can't delete."));
            return;
        }
        PushUndo();
        _verts.RemoveAt(_selected);
        _selected = Math.Min(_selected, _verts.Count - 1);
        SyncSelectedFields();
        RenderSurface();
    }

    private void RaiseAnimateVertex(int idx)
    {
        if (idx < 0 || idx >= _verts.Count) return;
        var v = _verts[idx];
        OnRequestAnimateVertex?.Invoke(idx, v.X, v.Y, _isBezier);
    }

    // ── button / toggle handlers ──────────────────────────────────────────

    private void OnAddVertexClick(object sender, RoutedEventArgs e) => AppendVertexAt(0.5, 0.5);

    private void OnDeleteVertexClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OnAnimateVertexClick(object sender, RoutedEventArgs e) => RaiseAnimateVertex(_selected);

    private void OnClosedToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        PushUndo();
        _closed = ClosedToggle.IsChecked == true;
        RenderSurface();
    }

    // ── numeric fields ─────────────────────────────────────────────────────

    private void SyncSelectedFields()
    {
        bool en = _selected >= 0 && _selected < _verts.Count;
        DeleteVertexButton.IsEnabled  = en && _verts.Count > 2;
        AnimateVertexButton.IsEnabled = en;

        _suppressEvents = true;
        try
        {
            if (!en)
            {
                foreach (var b in new[] { XBox, YBox, Cp1XBox, Cp1YBox, Cp2XBox, Cp2YBox })
                {
                    b.Text = "";
                    b.IsEnabled = false;
                }
                return;
            }
            var v = _verts[_selected];
            SetBox(XBox, v.X);
            SetBox(YBox, v.Y);
            if (_isBezier)
            {
                SetBox(Cp1XBox, v.Cp1X ?? v.X);
                SetBox(Cp1YBox, v.Cp1Y ?? v.Y);
                SetBox(Cp2XBox, v.Cp2X ?? v.X);
                SetBox(Cp2YBox, v.Cp2Y ?? v.Y);
            }
        }
        finally { _suppressEvents = false; }
    }

    private static void SetBox(TextBox box, double value)
    {
        box.Text = value.ToString("F3", CultureInfo.InvariantCulture);
        box.IsEnabled = true;
    }

    private void OnCoordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox box)
        {
            CommitCoord(box);
            e.Handled = true;
        }
    }

    private void OnCoordLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitCoord(box);
    }

    private void CommitCoord(TextBox box)
    {
        if (_suppressEvents) return;
        if (_selected < 0 || _selected >= _verts.Count) return;
        string axis = (box.Tag as string) ?? "";
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            SyncSelectedFields(); // restore the previous display
            return;
        }
        val = Math.Clamp(val, 0, 1);
        PushUndo();
        var v = _verts[_selected];
        switch (axis)
        {
            case "x":    v.X    = val; break;
            case "y":    v.Y    = val; break;
            case "cp1x": v.Cp1X = val; break;
            case "cp1y": v.Cp1Y = val; break;
            case "cp2x": v.Cp2X = val; break;
            case "cp2y": v.Cp2Y = val; break;
        }
        RenderSurface();
    }

    // ── undo / redo ──────────────────────────────────────────────────────

    private void PushUndo()
    {
        _undo.Push((ShapeData.Serialize(_verts), _closed));
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var snap = _undo.Pop();
        _redo.Push((ShapeData.Serialize(_verts), _closed));
        ApplySnapshot(snap);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var snap = _redo.Pop();
        _undo.Push((ShapeData.Serialize(_verts), _closed));
        ApplySnapshot(snap);
    }

    private void ApplySnapshot((string Json, bool Closed) snap)
    {
        _verts = ShapeData.Parse(snap.Json);
        _closed = snap.Closed;
        _suppressEvents = true;
        ClosedToggle.IsChecked = _closed;
        _suppressEvents = false;
        _selected = Math.Min(_selected, _verts.Count - 1);
        SyncSelectedFields();
        RenderSurface();
    }

    // ── dialog-level keys ──────────────────────────────────────────────────

    private void OnDialogKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = (InputKeyboardSourceState(VirtualKey.Control));
        if (ctrl && e.Key == VirtualKey.Z) { Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.Y) { Redo(); e.Handled = true; return; }
        // Del removes the selected vertex — but only when focus isn't inside a
        // numeric box (so the user can still delete a digit while typing).
        if (e.Key == VirtualKey.Delete && _selected >= 0 && FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private static bool InputKeyboardSourceState(VirtualKey key)
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    // ── OK / Cancel ──────────────────────────────────────────────────────

    private void OnOkClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _node.Attributes["Vertices"] = ShapeData.Serialize(_verts);
        _node.Attributes["Closed"]   = _closed ? "true" : "false";
    }

    private void OnCancelClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Restore originals — defensive even though we only mutate on OK; the
        // caller may have inspected the live node mid-edit.
        _node.Attributes["Vertices"] = _origVerticesJson;
        _node.Attributes["Closed"]   = _origClosed;
    }

    // ── theme helpers ──────────────────────────────────────────────────────

    private static Brush Token(string key, Color fallback) => Token(key, fallback, 0xFF);

    private static Brush Token(string key, Color fallback, byte alpha)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush sb)
            {
                return alpha == 0xFF
                    ? sb
                    : new SolidColorBrush(Color.FromArgb(alpha, sb.Color.R, sb.Color.G, sb.Color.B));
            }
        }
        catch { /* designer / pre-app — fall through */ }
        return new SolidColorBrush(fallback);
    }
}

// Small fluent helper so Canvas.SetLeft/SetTop reads inline at the call site.
internal static class ShapeEditorCanvasExtensions
{
    public static T At<T>(this T element, double left, double top) where T : FrameworkElement
    {
        // Fully qualified: the sibling namespace Phoenix.Controls.Visualist.WinUI.Canvas
        // shadows the unqualified `Canvas`, so spell out the WinUI control here.
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(element, left);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(element, top);
        return element;
    }
}
