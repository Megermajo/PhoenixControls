using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Compact overview navigator anchored to the bottom-right of
/// the canvas host. Mirrors every <see cref="NodeViewModel"/> +
/// <see cref="FrameViewModel"/> position into a scaled 200×140 DIP map and
/// overlays a translucent gold viewport rectangle that tracks the live
/// pan/zoom.
///
/// User interactions:
///   * Tap on the map surface → centre canvas viewport on the clicked
///     canvas-space point (calls back into <see cref="LogicCanvasView"/> via
///     the <see cref="CenterRequested"/> event).
///   * Drag the viewport rectangle → translate canvas viewport
///     proportionally to the drag delta in canvas-space pixels.
///   * Header × → hide the panel (persisted via ArchitectMinimapVisible).
///
/// Paint code stays per-pillar. The node + frame
/// rect rendering uses NodeViewModel.HeaderColor directly so Math nodes,
/// Twitch nodes, Logic nodes, etc. each look distinct at a glance — Twitch
/// purple vs Math teal vs Logic amber.
/// </summary>
public sealed partial class MiniMapOverlay : UserControl
{
    /// <summary>
    /// Logical canvas dimensions. Matches <see cref="LogicCanvasView"/>'s
    /// 8000×8000 surface; used as a soft upper bound when the live
    /// node/frame bounding box is empty (fresh / empty graph) so the
    /// viewport rect still has a sensible 1:1 mapping while the user pans
    /// around an empty canvas.
    /// </summary>
    private const double CanvasExtent = 8000.0;

    /// <summary>Min size for the bounding box (in canvas-space pixels) so a
    /// single-node graph doesn't render as a sub-pixel dot.</summary>
    private const double MinBoundsExtent = 600.0;

    private LogicCanvasViewModel? _vm;
    private LogicCanvasView? _host;

    // Cached bounding box (canvas-space) so MapSurface tap → canvas-space
    // and viewport-rect drag → canvas-space share the exact same projection.
    private double _boundsX, _boundsY, _boundsW = CanvasExtent, _boundsH = CanvasExtent;
    private double _scale = 1.0;
    private double _mapWidth = 200.0, _mapHeight = 124.0;

    // Brush cache keyed by "<hex>|<alpha-hex>". Pre-fix RenderFrames /
    // RenderNodes called SolidBrushFromHex per frame + per node on every
    // Rebuild(), each allocating a fresh SolidColorBrush. On a 146-node graph
    // at display cadence that was thousands of brush allocations per second
    // that then lingered in the visual tree until the Rectangle was replaced.
    // Caching by colour+alpha collapses repeated identical colours (the common
    // case — most nodes share a category header colour) to one shared brush.
    // Cleared on Detach so a re-attached overlay starts clean.
    private readonly Dictionary<string, SolidColorBrush> _brushCache = new();

    // Coalesce Rebuild() at displayed-frame
    // cadence. Pre-fix, every NodeViewModel X/Y/Width/Height PropertyChanged
    // (which fires per pointer-move during a node OR frame drag) called
    // Rebuild(), which clears NodeLayer + FrameLayer and constructs a
    // fresh Rectangle per node + per frame. With a 146-node graph and a
    // 120 Hz touchpad, that's ~35,000 Rectangle allocations per second per
    // dragged node — pure GC pressure that surfaced as the single-node
    // drag stutter Majo flagged in the 0.11.x audit. Now PropertyChanged
    // just flips the dirty bit; the CompositionTarget.Rendering subscription
    // hooked in Attach() drains it once per displayed frame. Same idiom
    // as the marquee-apply / snap-guides / frame-content-drag coalesces
    // already on the LogicCanvasView side.
    private bool _rebuildDirty;
    private bool _viewportRectDirty;
    // Colour-only minimap edits (node header / frame
    // colour) no longer force a full O(N+F) Rectangle rebuild. _colorDirty drives
    // an in-place rect.Fill/Stroke repaint via these per-VM rect caches (populated
    // in RenderNodes/RenderFrames, cleared on Rebuild + Detach). Geometry edits
    // (X/Y/W/H) still RequestRebuild; OnRenderingTick checks _rebuildDirty FIRST,
    // so a pending rebuild always supersedes (and rebuilds) the caches — the
    // colour pass only runs when the node/frame set is unchanged since the last
    // rebuild, keeping the cache keys valid.
    private bool _colorDirty;
    private readonly Dictionary<NodeViewModel, Rectangle> _nodeRectCache = new();
    private readonly Dictionary<FrameViewModel, Rectangle> _frameRectCache = new();
    private bool _renderingHooked;

    // Drag state for the viewport rect → canvas-pan gesture. Captured on
    // PointerPressed, applied per PointerMoved, released on
    // PointerReleased / Cancelled / CaptureLost.
    private bool _dragging;
    private Point _dragStartHost;       // pointer point in MapSurface coords
    private double _dragStartPanX;
    private double _dragStartPanY;
    private Microsoft.UI.Xaml.Input.Pointer? _capturedPointer;

    /// <summary>
    /// Fired when the user taps the map surface or drags the viewport rect.
    /// The argument is a canvas-space point the host should centre the
    /// viewport on. <see cref="LogicCanvasView"/> wires this to a viewport-
    /// centring helper that sets PanX/PanY so the viewport centre lands on
    /// the requested canvas coord.
    /// </summary>
    public event EventHandler<Point>? CenterRequested;

    /// <summary>
    /// 0.11.x polish — retired. Pre-polish this fired from the corner ×
    /// glyph the overlay used to host; that affordance was retired along
    /// with the MAP title strip per Majo's "minimap should be a chrome-
    /// less thumbnail" feedback. The chrome's View → Minimap toggle
    /// remains the visibility control. Kept as a no-op event for ABI
    /// stability (LogicCanvasView still subscribes; no raise site now).
    /// </summary>
#pragma warning disable CS0067 // event declared but never used — see XML comment
    public event EventHandler? HideRequested;
#pragma warning restore CS0067

    public MiniMapOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// Bind the overlay to a parent canvas. Hooks every refresh trigger
    /// (Nodes / Frames collection-changed, VM PropertyChanged for Zoom /
    /// Pan, ActualSize changes on the host) so the minimap re-renders
    /// without polling. Idempotent — calling with a different host
    /// detaches the prior subscription first.
    /// </summary>
    public void Attach(LogicCanvasView host, LogicCanvasViewModel? vm)
    {
        Detach();
        _host = host;
        _vm = vm;
        if (_vm is not null)
        {
            _vm.Nodes.CollectionChanged  += OnVmCollectionChanged;
            _vm.Frames.CollectionChanged += OnVmCollectionChanged;
            _vm.PropertyChanged          += OnVmPropertyChanged;
            _vm.GraphLoaded              += OnVmGraphLoaded;
            HookNodes(_vm);
            HookFrames(_vm);
        }
        if (_host is not null)
        {
            _host.SizeChanged += OnHostSizeChanged;
        }
        // Hook a per-frame drain so the
        // dirty-flag pattern in OnNodePropertyChanged + OnFramePropertyChanged
        // actually flushes. Idempotent (the `_renderingHooked` flag guards
        // against double-subscription on a re-Attach without an intervening
        // Detach). The first Rebuild below populates the surface immediately
        // so the user doesn't see an empty minimap waiting for the first
        // Rendering tick.
        if (!_renderingHooked)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRenderingTick;
            _renderingHooked = true;
        }
        Rebuild();
    }

    /// <summary>Detach all subscriptions wired by <see cref="Attach"/>.</summary>
    public void Detach()
    {
        if (_vm is not null)
        {
            _vm.Nodes.CollectionChanged  -= OnVmCollectionChanged;
            _vm.Frames.CollectionChanged -= OnVmCollectionChanged;
            _vm.PropertyChanged          -= OnVmPropertyChanged;
            _vm.GraphLoaded              -= OnVmGraphLoaded;
            UnhookNodes(_vm);
            UnhookFrames(_vm);
        }
        if (_host is not null)
        {
            _host.SizeChanged -= OnHostSizeChanged;
        }
        // Release the per-frame drain when the
        // overlay's parent host swaps so an unattached overlay doesn't keep
        // the canvas's render thread ticking.
        if (_renderingHooked)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingTick;
            _renderingHooked = false;
        }
        _rebuildDirty = false;
        _viewportRectDirty = false;
        _colorDirty = false;
        // Release the brush cache so a detached overlay doesn't pin shared
        // SolidColorBrush instances; the next Attach repopulates lazily.
        _brushCache.Clear();
        // Release the per-VM rect caches too so a detached
        // overlay doesn't pin Rectangle instances; repopulated by the next Rebuild.
        _nodeRectCache.Clear();
        _frameRectCache.Clear();
        _vm = null;
        _host = null;
    }

    /// <summary>
    /// Drain the dirty flags at displayed-frame
    /// cadence. Cheap when nothing is dirty (two bool checks). Runs Rebuild
    /// (which covers viewport-rect via its final UpdateViewportRect call)
    /// when the heavy flag is set, otherwise runs only the cheap viewport-
    /// rect update.
    /// </summary>
    private void OnRenderingTick(object? sender, object e)
    {
        if (_rebuildDirty)
        {
            _rebuildDirty = false;
            _viewportRectDirty = false;
            _colorDirty = false; // a full rebuild repaints every rect with its current colour
            Rebuild();
        }
        else if (_colorDirty)
        {
            // Colour-only repaint, no Rectangle churn.
            // Runs only when no geometry rebuild is pending (checked first), so
            // the rect caches still match the live node/frame set.
            _colorDirty = false;
            UpdateNodeAndFrameColors();
        }
        else if (_viewportRectDirty)
        {
            _viewportRectDirty = false;
            UpdateViewportRect();
        }
    }

    /// <summary>
    /// Flip the heavy-rebuild dirty flag. Drained
    /// in <see cref="OnRenderingTick"/>. Replaces direct <see cref="Rebuild"/>
    /// calls from PropertyChanged / CollectionChanged handlers so a drag-
    /// burst doesn't allocate fresh Rectangle instances per node per pointer
    /// move.
    /// </summary>
    private void RequestRebuild() => _rebuildDirty = true;

    /// <summary>
    /// Flip the cheap viewport-rect dirty flag.
    /// Used for pan / zoom PropertyChanged paths where the node + frame
    /// dot positions haven't moved in canvas-space.
    /// </summary>
    private void RequestViewportRectUpdate() => _viewportRectDirty = true;

    private void HookNodes(LogicCanvasViewModel vm)
    {
        foreach (var n in vm.Nodes) n.PropertyChanged += OnNodePropertyChanged;
    }

    private void UnhookNodes(LogicCanvasViewModel vm)
    {
        foreach (var n in vm.Nodes) n.PropertyChanged -= OnNodePropertyChanged;
    }

    private void HookFrames(LogicCanvasViewModel vm)
    {
        foreach (var f in vm.Frames) f.PropertyChanged += OnFramePropertyChanged;
    }

    private void UnhookFrames(LogicCanvasViewModel vm)
    {
        foreach (var f in vm.Frames) f.PropertyChanged -= OnFramePropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Rebuild();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => RequestViewportRectUpdate();

    private void OnVmCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Re-hook per-VM PropertyChanged on add / replace so a node's
        // Translate ripple still triggers a minimap repaint. Cheap on
        // typical graphs.
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is NodeViewModel n)  n.PropertyChanged -= OnNodePropertyChanged;
                if (item is FrameViewModel f) f.PropertyChanged -= OnFramePropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is NodeViewModel n)  n.PropertyChanged += OnNodePropertyChanged;
                if (item is FrameViewModel f) f.PropertyChanged += OnFramePropertyChanged;
            }
        }
        RequestRebuild();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Pan / zoom changes only need a viewport-rect update — the node /
        // frame dots haven't moved in canvas-space, so the expensive
        // rebuild is unnecessary.
        if (e.PropertyName is nameof(LogicCanvasViewModel.PanX)
                            or nameof(LogicCanvasViewModel.PanY)
                            or nameof(LogicCanvasViewModel.Zoom))
        {
            RequestViewportRectUpdate();
        }
    }

    private void OnVmGraphLoaded(object? sender, EventArgs e)
    {
        // A fresh graph means re-hook every NodeVM / FrameVM (LoadGraph
        // builds new VMs) and recompute bounds.
        if (_vm is not null)
        {
            HookNodes(_vm);
            HookFrames(_vm);
        }
        RequestRebuild();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Geometry edits warrant a full rebuild (rect
        // positions/sizes change); a colour-only edit just repaints the existing
        // rect's Fill in place (no Rectangle churn).
        if (e.PropertyName is nameof(NodeViewModel.X)
                            or nameof(NodeViewModel.Y)
                            or nameof(NodeViewModel.Width)
                            or nameof(NodeViewModel.Height))
        {
            RequestRebuild();
        }
        else if (e.PropertyName == nameof(NodeViewModel.HeaderColorHex))
        {
            _colorDirty = true;
        }
    }

    private void OnFramePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Geometry → full rebuild; colour → in-place
        // repaint. (The minimap frame rect derives both fill and stroke from
        // FrameColorHex; FrameFillHex isn't drawn on the minimap but is routed to
        // the cheap colour pass anyway so it never forces a rebuild.)
        if (e.PropertyName is nameof(FrameViewModel.X)
                            or nameof(FrameViewModel.Y)
                            or nameof(FrameViewModel.Width)
                            or nameof(FrameViewModel.Height))
        {
            RequestRebuild();
        }
        else if (e.PropertyName is nameof(FrameViewModel.FrameColorHex)
                                or nameof(FrameViewModel.FrameFillHex))
        {
            _colorDirty = true;
        }
    }

    // ─── Rendering ───────────────────────────────────────────────────────

    /// <summary>Compute bounds + scale, then repaint node + frame rects.</summary>
    private void Rebuild()
    {
        if (!IsLoaded) return;

        _mapWidth  = Math.Max(1.0, MapSurface.ActualWidth  > 0 ? MapSurface.ActualWidth  : Width  - 0);
        // 0.11.x polish — the MAP header strip was retired; MapSurface now
        // fills the full overlay height, so the fallback subtracts zero
        // instead of the old 16-DIP header-strip height.
        _mapHeight = Math.Max(1.0, MapSurface.ActualHeight > 0 ? MapSurface.ActualHeight : Height - 0);

        ComputeBounds();
        _scale = Math.Min(_mapWidth / Math.Max(1.0, _boundsW),
                          _mapHeight / Math.Max(1.0, _boundsH));
        if (_scale <= 0 || double.IsNaN(_scale) || double.IsInfinity(_scale))
            _scale = 1.0;

        RenderFrames();
        RenderNodes();
        UpdateViewportRect();
    }

    private void ComputeBounds()
    {
        // Default to the 8000×8000 surface so an empty graph still shows
        // the viewport at the right scale (user can pan around blank
        // canvas while the .phxg loads).
        double minX = 0, minY = 0, maxX = MinBoundsExtent, maxY = MinBoundsExtent;
        bool any = false;
        if (_vm is not null)
        {
            foreach (var n in _vm.Nodes)
            {
                any = true;
                minX = Math.Min(minX, n.X);
                minY = Math.Min(minY, n.Y);
                maxX = Math.Max(maxX, n.X + n.Width);
                maxY = Math.Max(maxY, n.Y + n.Height);
            }
            foreach (var f in _vm.Frames)
            {
                any = true;
                minX = Math.Min(minX, f.X);
                minY = Math.Min(minY, f.Y);
                maxX = Math.Max(maxX, f.X + f.Width);
                maxY = Math.Max(maxY, f.Y + f.Height);
            }
        }

        if (!any)
        {
            // Empty graph — anchor at origin with a comfortable extent so
            // the viewport rect renders at a recognisable size.
            minX = 0; minY = 0; maxX = MinBoundsExtent; maxY = MinBoundsExtent;
        }
        else
        {
            // Pad the bounding box by 80px on each side so nodes near the
            // edge don't render flush against the minimap border.
            const double pad = 80.0;
            minX -= pad; minY -= pad; maxX += pad; maxY += pad;

            // Floor the size so a tightly-clustered single-node graph
            // doesn't render a 1×1 box scaled up to fill the minimap.
            double w = maxX - minX, h = maxY - minY;
            if (w < MinBoundsExtent) { double pad2 = (MinBoundsExtent - w) / 2; minX -= pad2; maxX += pad2; }
            if (h < MinBoundsExtent) { double pad2 = (MinBoundsExtent - h) / 2; minY -= pad2; maxY += pad2; }
        }

        _boundsX = minX;
        _boundsY = minY;
        _boundsW = Math.Max(1.0, maxX - minX);
        _boundsH = Math.Max(1.0, maxY - minY);
    }

    private void RenderFrames()
    {
        FrameLayer.Children.Clear();
        _frameRectCache.Clear(); // rebuild the colour cache
        if (_vm is null) return;

        foreach (var f in _vm.Frames)
        {
            // Soften the minimap frame fill from the
            // canvas-side #40<rgb> (~25%) down to ~9% alpha. On the
            // bright minimap surface, 25% reads as opaque saturated
            // blocks that overpower the per-node dots; the minimap's
            // purpose is density overview, not frame chrome. Reading
            // from FrameColorHex (7-char, no alpha) with an explicit
            // defaultAlpha keeps the colour identity while pushing the
            // fill into the "ghost background" range. Stroke stays at
            // 0x80 so the frame outline is still legible.
            var rect = new Rectangle
            {
                Width  = Math.Max(1.0, f.Width  * _scale),
                Height = Math.Max(1.0, f.Height * _scale),
                Fill   = SolidBrushFromHex(f.FrameColorHex, defaultAlpha: 0x18),
                Stroke = SolidBrushFromHex(f.FrameColorHex, defaultAlpha: 0x80),
                StrokeThickness = 0.5,
                IsHitTestVisible = false,
            };
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, (f.X - _boundsX) * _scale);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop (rect, (f.Y - _boundsY) * _scale);
            FrameLayer.Children.Add(rect);
            _frameRectCache[f] = rect; // for in-place colour repaint
        }
    }

    private void RenderNodes()
    {
        NodeLayer.Children.Clear();
        _nodeRectCache.Clear(); // rebuild the colour cache
        if (_vm is null) return;

        foreach (var n in _vm.Nodes)
        {
            double w = Math.Max(2.0, n.Width  * _scale);
            double h = Math.Max(2.0, n.Height * _scale);
            var rect = new Rectangle
            {
                Width  = w,
                Height = h,
                Fill   = SolidBrushFromHex(n.HeaderColorHex, defaultAlpha: 0xE0),
                IsHitTestVisible = false,
            };
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, (n.X - _boundsX) * _scale);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop (rect, (n.Y - _boundsY) * _scale);
            NodeLayer.Children.Add(rect);
            _nodeRectCache[n] = rect; // for in-place colour repaint
        }
    }

    // In-place colour repaint for node + frame minimap
    // rects, avoiding a full O(N+F) Rectangle rebuild on a colour-only edit. Only
    // touches Fill/Stroke (brushes are cached in _brushCache); positions/sizes are
    // untouched. A cache miss (vm not yet rendered) is skipped — it can't occur
    // while no rebuild is pending (OnRenderingTick checks _rebuildDirty first), but
    // the guard keeps it crash-safe under any ordering.
    private void UpdateNodeAndFrameColors()
    {
        if (_vm is null) return;
        foreach (var n in _vm.Nodes)
        {
            if (_nodeRectCache.TryGetValue(n, out var rect))
                rect.Fill = SolidBrushFromHex(n.HeaderColorHex, defaultAlpha: 0xE0);
        }
        foreach (var f in _vm.Frames)
        {
            if (_frameRectCache.TryGetValue(f, out var rect))
            {
                rect.Fill   = SolidBrushFromHex(f.FrameColorHex, defaultAlpha: 0x18);
                rect.Stroke = SolidBrushFromHex(f.FrameColorHex, defaultAlpha: 0x80);
            }
        }
    }

    /// <summary>
    /// Project the live host viewport (canvas-space rect visible inside
    /// LogicCanvasView.HostRoot) onto the minimap. The host shows a
    /// canvas-space rect of (HostRoot.ActualWidth / Zoom) by
    /// (HostRoot.ActualHeight / Zoom) anchored at (-PanX/Zoom, -PanY/Zoom).
    /// </summary>
    private void UpdateViewportRect()
    {
        if (_host is null || _vm is null) return;

        double zoom = Math.Max(_vm.Zoom, 0.0001);
        double hostW = Math.Max(1.0, _host.ActualWidth);
        double hostH = Math.Max(1.0, _host.ActualHeight);
        double viewCanvasW = hostW / zoom;
        double viewCanvasH = hostH / zoom;
        double viewCanvasX = -_vm.PanX / zoom;
        double viewCanvasY = -_vm.PanY / zoom;

        double mapX = (viewCanvasX - _boundsX) * _scale;
        double mapY = (viewCanvasY - _boundsY) * _scale;
        double mapW = Math.Max(8.0, viewCanvasW * _scale);
        double mapH = Math.Max(8.0, viewCanvasH * _scale);

        // Clamp so the rect doesn't render outside the visible surface;
        // the user can still pan past the bounding box, but the rect
        // visually saturates at the edge rather than spilling off the
        // panel.
        if (mapX < 0) { mapW = Math.Max(8.0, mapW + mapX); mapX = 0; }
        if (mapY < 0) { mapH = Math.Max(8.0, mapH + mapY); mapY = 0; }
        if (mapX + mapW > _mapWidth)  mapW = Math.Max(8.0, _mapWidth  - mapX);
        if (mapY + mapH > _mapHeight) mapH = Math.Max(8.0, _mapHeight - mapY);

        ViewportRect.Width  = mapW;
        ViewportRect.Height = mapH;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(ViewportRect, mapX);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop (ViewportRect, mapY);
    }

    // ─── Interaction ─────────────────────────────────────────────────────

    /// <summary>
    /// Tap on the bare map surface → centre the host canvas's viewport on
    /// the corresponding canvas-space point. Ignored when the tap hits
    /// the viewport rect itself (the rect's own pointer handlers steer
    /// the drag-pan gesture instead).
    /// </summary>
    private void OnMapSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        // The viewport rectangle is hit-testable; bare-surface taps land
        // here. Convert tap point → canvas-space and dispatch.
        var pt = e.GetPosition(MapSurface);
        double canvasX = _boundsX + pt.X / Math.Max(0.0001, _scale);
        double canvasY = _boundsY + pt.Y / Math.Max(0.0001, _scale);
        CenterRequested?.Invoke(this, new Point(canvasX, canvasY));
        e.Handled = true;
    }

    /// <summary>
    /// Start a drag-pan gesture on the viewport rectangle. Captures the
    /// pointer so PointerMoved keeps firing even if the cursor strays
    /// outside the rect (or off the minimap entirely).
    /// </summary>
    private void OnViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement fe) return;
        _dragStartHost  = e.GetCurrentPoint(MapSurface).Position;
        _dragStartPanX  = _vm.PanX;
        _dragStartPanY  = _vm.PanY;
        _capturedPointer = e.Pointer;
        _dragging = fe.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// Drag-pan: translate the host canvas viewport by the inverse of the
    /// cursor delta scaled into canvas-space, so dragging the viewport
    /// rect right pans the canvas content left (the rect "follows" the
    /// cursor across the minimap).
    /// </summary>
    private void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _vm is null) return;
        var now = e.GetCurrentPoint(MapSurface).Position;
        double dxMap = now.X - _dragStartHost.X;
        double dyMap = now.Y - _dragStartHost.Y;
        double zoom = Math.Max(_vm.Zoom, 0.0001);
        double scaleSafe = Math.Max(0.0001, _scale);
        // dxMap (minimap DIPs) → canvas-space pixels → host pan offset.
        // PanX is in host-pixel units (because ViewTransform.TranslateX
        // operates in host space, and ApplyViewTransform writes PanX
        // straight into TranslateX); dragging right on the minimap should
        // move the canvas viewport to higher canvas-X, which is the same
        // as PanX decreasing.
        double canvasDx = dxMap / scaleSafe;
        double canvasDy = dyMap / scaleSafe;
        _vm.PanX = _dragStartPanX - canvasDx * zoom;
        _vm.PanY = _dragStartPanY - canvasDy * zoom;
        // The host's ApplyViewTransform listens on PanX/Y via the
        // LogicCanvasViewModel's OnPropertyChanged path; nudge it directly
        // so the viewport responds even if the host's rendering loop
        // batched the pending pan.
        _host?.ApplyViewTransformForMinimap();
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_capturedPointer is not null && sender is FrameworkElement fe)
        {
            try { fe.ReleasePointerCapture(_capturedPointer); }
            catch { /* best-effort */ }
        }
        _capturedPointer = null;
        e.Handled = true;
    }

    // ─── Pointer-event guards (HostBorder root) ──────────────────────────

    // Mark any pointer event that reaches the minimap's root grid as
    // handled so it never bubbles out into LogicCanvasView.HostRoot.
    // Without this guard, a click on bare MapSurface area (outside the
    // ViewportRect, which already sets e.Handled) bubbled up to the
    // canvas's OnHostPointerPressed and started a marquee, which raced
    // the Tapped-driven CenterRequested pan jump and wedged Architect
    // (Majo: "clicking on the map to jump the app froze
    // and had to be closed via external help"). ViewportRect's own
    // PointerPressed handler already marks events handled at the inner
    // level, so this root-level guard only fires for events that no
    // descendant claimed — which is exactly the bare-surface case that
    // previously leaked into the canvas's marquee branch.
    private void OnMinimapPointerPressed(object sender, PointerRoutedEventArgs e)  => e.Handled = true;
    private void OnMinimapPointerMoved(object sender, PointerRoutedEventArgs e)    => e.Handled = true;
    private void OnMinimapPointerReleased(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    // ─── Brush helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Parse a #RRGGBB or #AARRGGBB hex string into a SolidColorBrush, returning
    /// a cached shared instance for repeated colour+alpha pairs. Falls back to a
    /// neutral grey on parse failure so a malformed HeaderColorHex doesn't blank
    /// the minimap entry. The <paramref name="defaultAlpha"/> applies only to
    /// #RRGGBB inputs. Caching collapses the per-node / per-frame brush
    /// allocation that fired on every Rebuild() — most nodes share a category
    /// header colour, so the cache hit-rate is high.
    /// </summary>
    private SolidColorBrush SolidBrushFromHex(string? hex, byte defaultAlpha)
    {
        // Cache key folds the hex AND the requested default alpha — the same
        // #RRGGBB renders at different alphas for a frame fill (0x18) vs. stroke
        // (0x80), so alpha must participate in the key.
        string key = (hex ?? string.Empty) + "|" + defaultAlpha.ToString("X2");
        if (_brushCache.TryGetValue(key, out var cached))
            return cached;

        var color = ParseBrushColor(hex, defaultAlpha);
        var brush = new SolidColorBrush(color);
        _brushCache[key] = brush;
        return brush;
    }

    private static Color ParseBrushColor(string? hex, byte defaultAlpha)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#')
            return Color.FromArgb(defaultAlpha, 0x80, 0x80, 0x80);
        try
        {
            byte a, r, g, b;
            if (hex.Length == 7)
            {
                a = defaultAlpha;
                r = Convert.ToByte(hex.Substring(1, 2), 16);
                g = Convert.ToByte(hex.Substring(3, 2), 16);
                b = Convert.ToByte(hex.Substring(5, 2), 16);
            }
            else if (hex.Length == 9)
            {
                a = Convert.ToByte(hex.Substring(1, 2), 16);
                r = Convert.ToByte(hex.Substring(3, 2), 16);
                g = Convert.ToByte(hex.Substring(5, 2), 16);
                b = Convert.ToByte(hex.Substring(7, 2), 16);
            }
            else
            {
                return Color.FromArgb(defaultAlpha, 0x80, 0x80, 0x80);
            }
            return Color.FromArgb(a, r, g, b);
        }
        catch
        {
            return Color.FromArgb(defaultAlpha, 0x80, 0x80, 0x80);
        }
    }
}
