using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.Core;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

// Interactive widget-graph canvas for Visualist's per-trigger
// graph editor. Replaces the read-only stack-of-cards `GraphNodeStack`
// with a positioned canvas that supports:
//
//   • Pan      — middle-mouse drag on empty canvas (CompositeTransform.TranslateX/Y).
//   • Zoom     — mouse wheel; cursor-anchored so the point under the wheel
//                stays put. Bounded to [0.25, 3.0] to keep nodes legible.
//   • Drag     — left-mouse press + move on a node updates the node's
//                Node.Location; first real motion (>2px) calls
//                PushUndo on the document and marks dirty on commit.
//   • Select   — left-click on a node selects it; clicking empty space
//                clears. Shift-click toggles a node in/out of the
//                multi-select. Lasso (rubber-band) and the spawn palette /
//                right-click menus all landed later.
//
// Still out of scope (follow-ups):
//   live preview thumbnails on each node body, timeline scrubbing,
//   widget gallery / preset thumbnails, group-drag for multi-select,
//   right-click numeric pin → Animate keyframe context. The seam where
//   each of those plugs in is called out by comment in the right place
//   below.
public sealed partial class WidgetGraphCanvas : UserControl
{
    private VisualistViewModel? _vm;
    private WidgetTrigger?      _trigger;

    private readonly Dictionary<string, WidgetGraphNodeView> _nodeViews = new(StringComparer.Ordinal);
    private WidgetGraphNodeView? _selectedNode;

    private const double MinZoom = 0.25;
    private const double MaxZoom = 3.0;
    private const double DragDeadZonePx = 2.0;

    // Grid snap. 20px matches the loose visual rhythm of pin rows
    // (~22px each) without being so coarse that small adjustments feel jumpy.
    // Suppress-snap is wired to Alt rather than Shift because Shift is already
    // taken by multi-select extend (OnNodePointerPressed / IsShiftDown). Alt's
    // sole canvas role today is the momentary-disable modifier — no
    // conflict.
    private const int SnapStepPx = 20;

    /// <summary>
    /// Persistent grid-snap toggle. Off by default so existing authors aren't
    /// surprised; flips on via the canvas right-click flyout. In-memory only —
    /// no settings round-trip yet (separate sprint if Majo wants it sticky).
    /// </summary>
    private bool SnapEnabled;

    // Re-entrancy guard for SnapToggle.Checked/Unchecked. The
    // setter syncs the CheckBox state, which would otherwise re-fire the
    // CheckBox event and recurse back into the setter.
    //
    // Upgraded from a bool to a nesting DEPTH counter so overlapping
    // sync passes (checkbox click + RMB-flyout toggle landing in the same frame)
    // can't have an inner finally reset the flag while an outer pass is still
    // suppressing — the event only fires when depth returns to 0. Guard reads as
    // `_suppressSnapToggleDepth > 0`.
    private int _suppressSnapToggleDepth;

    /// <summary>
    /// Public surface for the canvas header strip's snap toggle.
    /// Reads/writes the same <see cref="SnapEnabled"/> field the RMB flyout
    /// owns so both UI paths stay coherent. Setter logs a single System line
    /// the same shape as the flyout path (no modal).
    /// </summary>
    public bool IsSnapEnabled
    {
        get => SnapEnabled;
        set
        {
            if (SnapEnabled == value) return;
            SnapEnabled = value;
            SyncSnapToggleVisual();
            RefreshGridDots();
            GlobalLogger.Log(
                SnapEnabled
                    ? $"Grid snap enabled ({SnapStepPx}px). Hold Alt during a drag to suspend snap momentarily."
                    : "Grid snap disabled.",
                "WidgetGraphCanvas", LogLevel.System);
        }
    }

    // Dotted-grid backdrop. Paints small ember-tinted dots
    // at every SnapStepPx grid intersection in the currently-visible viewport
    // when SnapEnabled is true. Hidden / empty when off. Called from:
    //   - IsSnapEnabled setter (toggle on/off)
    //   - End of pan + zoom call sites (debounced via dispatcher)
    //   - HostGrid.SizeChanged (viewport resize)
    //   - LayoutUpdated bootstrap (initial paint after first arrange)
    //
    // Performance posture: dots are only painted for the visible-world rect,
    // not the full 4000×4000 WorldCanvas. Hard cap at MaxGridDots keeps the
    // visual element count bounded; at zooms where the visible rect would
    // exceed the cap the dots are dropped entirely (zoom is so low the dots
    // wouldn't be legible anyway).
    private const int MaxGridDots = 4096;
    private bool _gridDotsRefreshScheduled;

    // Pool the Ellipse children. The previous implementation called
    // GridDots.Children.Clear() and allocated up to 4096 fresh Ellipses on
    // every pan / zoom / resize tick — at ~60 dispatcher ticks per second of
    // a continuous pan that's ~245k allocations + tree mutations per second,
    // plus GC pressure. Pooling keeps the visual children stable across
    // refreshes; pan/zoom just repositions the existing Ellipses. The pool
    // grows lazily up to MaxGridDots, never shrinks, and excess pool slots
    // are hidden via Visibility=Collapsed (cheap in WinUI 3 — Canvas does
    // not measure collapsed children).
    private readonly List<Ellipse> _gridDotPool = new();

    // The dot brush is theme-derived but stable for the session. Cache the
    // first resolved instance so we don't pay the ResolveBrush cost (which
    // itself allocates a SolidColorBrush) per refresh.
    private Brush? _gridDotBrush;

    /// <summary>
    /// Coalesce repaint requests across a burst of pan / zoom events.
    /// Schedules one ImmediateRefreshGridDots() per dispatcher tick.
    /// </summary>
    private void RefreshGridDots()
    {
        if (_gridDotsRefreshScheduled) return;
        _gridDotsRefreshScheduled = true;
        DispatcherQueue?.TryEnqueue(() =>
        {
            _gridDotsRefreshScheduled = false;
            ImmediateRefreshGridDots();
        });
    }

    private void ImmediateRefreshGridDots()
    {
        if (GridDots is null) return;

        if (!SnapEnabled)
        {
            HideAllGridDots();
            return;
        }

        // Visible-world rect = inverse(WorldTransform)(host viewport).
        double scale = WorldTransform.ScaleX <= 0 ? 1.0 : WorldTransform.ScaleX;
        double w = HostGrid?.ActualWidth  ?? 0;
        double h = HostGrid?.ActualHeight ?? 0;
        if (w <= 0 || h <= 0) { HideAllGridDots(); return; }

        double worldLeft = -WorldTransform.TranslateX / scale;
        double worldTop  = -WorldTransform.TranslateY / scale;
        double worldRight  = worldLeft + w / scale;
        double worldBottom = worldTop  + h / scale;

        // Clamp to canvas bounds — no point painting outside the 4000×4000
        // logical surface.
        worldLeft   = Math.Max(0,    worldLeft);
        worldTop    = Math.Max(0,    worldTop);
        worldRight  = Math.Min(4000, worldRight);
        worldBottom = Math.Min(4000, worldBottom);
        if (worldRight <= worldLeft || worldBottom <= worldTop)
        {
            HideAllGridDots();
            return;
        }

        // Round to grid cells.
        int x0 = (int)(Math.Ceiling(worldLeft / SnapStepPx) * SnapStepPx);
        int y0 = (int)(Math.Ceiling(worldTop  / SnapStepPx) * SnapStepPx);
        int x1 = (int)(Math.Floor  (worldRight  / SnapStepPx) * SnapStepPx);
        int y1 = (int)(Math.Floor  (worldBottom / SnapStepPx) * SnapStepPx);

        int cols = Math.Max(0, (x1 - x0) / SnapStepPx + 1);
        int rows = Math.Max(0, (y1 - y0) / SnapStepPx + 1);
        long total = (long)cols * rows;
        if (total <= 0 || total > MaxGridDots)
        {
            // Too dense at low zoom; drop.
            HideAllGridDots();
            return;
        }

        var dotBrush = _gridDotBrush ??= ResolveBrush("CoalSecondaryTextBrush", 0x60, 0xA8, 0x96, 0x83);
        double dotSize = 1.6 / scale; // visual dot stays ~1.6 DIPs regardless of zoom
        double halfDot = dotSize / 2.0;

        int idx = 0;
        for (int gy = y0; gy <= y1; gy += SnapStepPx)
        {
            for (int gx = x0; gx <= x1; gx += SnapStepPx)
            {
                Ellipse dot;
                if (idx < _gridDotPool.Count)
                {
                    dot = _gridDotPool[idx];
                }
                else
                {
                    dot = new Ellipse
                    {
                        Fill = dotBrush,
                        IsHitTestVisible = false,
                    };
                    _gridDotPool.Add(dot);
                    GridDots.Children.Add(dot);
                }

                if (dot.Visibility != Visibility.Visible) dot.Visibility = Visibility.Visible;
                if (dot.Width  != dotSize) dot.Width  = dotSize;
                if (dot.Height != dotSize) dot.Height = dotSize;
                global::Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dot, gx - halfDot);
                global::Microsoft.UI.Xaml.Controls.Canvas.SetTop (dot, gy - halfDot);
                idx++;
            }
        }

        // Hide any pool slots beyond the visible-this-tick count.
        for (int i = idx; i < _gridDotPool.Count; i++)
        {
            var leftover = _gridDotPool[i];
            if (leftover.Visibility != Visibility.Collapsed)
                leftover.Visibility = Visibility.Collapsed;
        }
    }

    private void HideAllGridDots()
    {
        for (int i = 0; i < _gridDotPool.Count; i++)
        {
            var dot = _gridDotPool[i];
            if (dot.Visibility != Visibility.Collapsed)
                dot.Visibility = Visibility.Collapsed;
        }
    }


    // Resolve a theme brush by key with literal ARGB fallback. The fallback
    // ensures that designer-time / pre-Application paths still get a sane
    // brush instead of throwing — callers route the wire-preview colours
    // through this so future theme tweaks ripple here without code edits.
    //
    // The ARGB fallbacks the wire-preview path passes are NOT
    // arbitrary placeholders — they are deliberate, theme-agnostic SEMANTIC
    // constants matching Design_Orders §2 (green = compatible/connected, red =
    // error/incompatible). They are chosen so that if the "OkBrush"/"ErrBrush"
    // theme token is ever missing or renamed, the wire still reads green-for-ok /
    // red-for-err rather than degrading to a neutral that loses the compat signal.
    // Keep them green/red; do NOT substitute a grey neutral here.
    private static Brush ResolveBrush(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush sb)
            {
                // Re-tint to the requested alpha so the wire-preview keeps
                // its semi-transparent feel even when the theme brush is
                // opaque.
                return new SolidColorBrush(Color.FromArgb(a, sb.Color.R, sb.Color.G, sb.Color.B));
            }
        }
        catch { /* designer / pre-app — fall through to literal */ }
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    // Node-drag state — null when no drag is active.
    private WidgetGraphNodeView? _dragNode;
    private Point  _dragStartCanvasPoint;
    private Point  _dragStartNodeLocation;
    private bool   _dragHasPushedUndo;

    // Pan state — captured on middle-button press, released on up.
    private bool   _isPanning;
    private Point  _panStartPointerWorld;

    // Wire-drag state. When the user presses on a pin we capture
    // the source socket + node and start drawing a temp path into LinkLayer
    // until the pointer comes up — over a target pin → create Link
    // (if compatible); over empty space or an incompatible pin → log and
    // discard. Compatibility check lives in IsCompatibleDrop below — Visualist
    // owns its own table per the pillar-isolation rule (Architect's
    // NodeRegistry.AreCompatible doesn't carry the visual-only DataTypes
    // Image/Color/Scalar/Vector*/Audio).
    private Node?     _wireSourceNode;
    private Socket?   _wireSourceSocket;
    private FrameworkElement?  _wireSourcePin;
    private Microsoft.UI.Xaml.Shapes.Path? _tempWirePath;

    // Reverse-drag support. Unreal / Fusion (and the pre-WinUI
    // Visualist baseline's NormalizeLinkDirection) let the author begin a wire
    // from EITHER end of an edge. When the drag starts on an INPUT pin this is
    // true; the move/release paths then look for an OUTPUT drop target and swap
    // the producer/consumer roles so IsCompatibleDrop + the created Link still
    // resolve output→input. Set in OnPinPointerPressed, consumed in the move /
    // release wire handlers, cleared on every drag teardown alongside the rest
    // of the _wireSource* state.
    private bool _wireSourceIsInput;

    // Deterministic pin-press hand-off — ported from Architect's
    // LogicCanvasView.Pointer.cs (_pendingPinPress / NotePinPress /
    // ConsumePendingPinPress). The pin's own pointer-pressed handler stamps the
    // pressed socket here; because routed PointerPressed BUBBLES, the pin
    // descendant's handler runs BEFORE the node-view ancestor's
    // OnNodePointerPressed in the SAME event pass — so by the time the node
    // handler reads this field it holds the exact socket the user pressed. This
    // replaces the fragile "did the press land precisely on the 14×14 pin Grid"
    // routing as the PRIMARY wire-start source: a press a few px off the glyph
    // (on the row label / body) previously started a node-drag instead of a
    // wire-drag ("pipe preview only spawns 1 in 10"). HitTestPinAt stays as the
    // best-effort geometry fallback for the rare case the note didn't arrive.
    private Socket? _pendingPinPress;
    private Node?   _pendingPinPressNode;

    /// <summary>
    /// Called from a socket pin's pointer-pressed
    /// (WidgetGraphNodeView raises it via the canvas hook) to stamp the pressed
    /// socket + owner for the imminent OnNodePointerPressed. See
    /// <see cref="_pendingPinPress"/>.
    /// </summary>
    internal void NotePinPress(Socket socket, Node ownerNode)
    {
        _pendingPinPress     = socket;
        _pendingPinPressNode = ownerNode;
    }

    /// <summary>
    /// Read-and-clear the pin stamped by the most recent
    /// pin-target press. Always clears so a stamp can never leak into a later,
    /// unrelated press (e.g. a node-body press that never reached a pin handler).
    /// Returns null when no pin was pressed this pass.
    /// </summary>
    private (Node Node, Socket Socket)? ConsumePendingPinPress()
    {
        var sock = _pendingPinPress;
        var node = _pendingPinPressNode;
        _pendingPinPress     = null;
        _pendingPinPressNode = null;
        if (sock is null || node is null) return null;
        return (node, sock);
    }

    // Lasso multi-select state.
    private readonly List<WidgetGraphNodeView> _selectedNodes = new();
    private bool _isLassoing;
    private Point _lassoStart;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _lassoRect;

    // Group-drag state. When the press lands on a node that's
    // already in the multi-select set, we snapshot every selected node's
    // origin so PointerMoved can apply the same dx/dy to all of them
    // instead of the single-node path.
    private bool _isGroupDragging;
    private readonly Dictionary<WidgetGraphNodeView, Point> _groupDragOrigins = new();

    // Parameter paths armed for DaVinci-style record. While armed, an
    // inline value-pill commit drops/updates a keyframe at the playhead instead
    // of only writing the static attribute. Canvas-scoped (the node views are
    // recreated on every Rebuild); cleared on Bind so arm state never bleeds
    // across triggers/widgets.
    private readonly HashSet<string> _armedParams = new(StringComparer.Ordinal);

    // Selected wire (gold highlight). The wire model has no selection
    // concept, so we track the Link + its rendered Path here; RedrawLinks
    // re-applies the highlight if the link survived the rebuild.
    private Link? _selectedLink;
    private Microsoft.UI.Xaml.Shapes.Path? _selectedLinkPath;

    // Pin the pointer currently rests on. The node view owns the pin-LOCAL
    // hover feedback (scale 14→18 + glow + tooltip in BuildPinPath); the
    // canvas layers the part only it can see — the wires incident to the
    // hovered pin get an emphasized stroke so the author can trace where the
    // pin connects across a dense graph. Tracked as (node id, socket id)
    // rather than Path references because RedrawLinks rebuilds every wire
    // Path wholesale: it re-applies this highlight from the ids the same way
    // it re-applies _selectedLink, so a frame-coalesced redraw landing
    // mid-hover doesn't silently drop the effect.
    private string? _hoveredPinNodeId;
    private string? _hoveredPinSocketId;

    // Frame-coalesce parity with Architect's LogicCanvasView. Pointer
    // moves during a node drag previously called RedrawLinks() per pointer
    // event, which on a high-Hz mouse + a dense graph fired the full wire
    // rebuild dozens of times per frame. The Rendering tick consumes the dirty
    // flag exactly once per displayed frame and clears it; pointer-move
    // handlers only set the flag. Architect mirrors this with `_panDirty` +
    // `LinkViewModel.RecomputeIfDirty()`; the WidgetGraphCanvas's wire layer
    // is rebuilt wholesale rather than per-link, so one bool covers it.
    private bool _redrawDirty;
    private bool _renderingHooked;

    // Wire Paths from the last full RedrawLinks pass, keyed by the Link
    // they render. Lets the per-frame tick during a node drag re-route just
    // the wires incident to the moved node set (mutating each Path's Data in
    // place via UpdateBezierWire) instead of rebuilding every wire — Path +
    // geometry + brush + closures — for the whole graph on every frame.
    // Rebuilt wholesale by RedrawLinks; cleared on Rebuild so no stale Path
    // outlives its node views.
    private readonly Dictionary<Link, Microsoft.UI.Xaml.Shapes.Path> _linkPaths = new();

    // Reusable moved-node-id scratch set for the incident-wire reroute —
    // cleared and refilled per frame rather than allocated per frame.
    private readonly HashSet<string> _rerouteNodeIds = new(StringComparer.Ordinal);

    // Pin centres snapshotted while a wire drag is live. Pins cannot move
    // mid-wire-drag (the pointer is captured for the wire, so no node drag can
    // run), which makes the per-move HitTestPinAt sweep a pure function of the
    // drag-start layout — one TransformToVisual pass at first use replaces an
    // O(nodes·pins) layout query per pointer-move. Dropped on drag end and on
    // RedrawLinks/Rebuild so it can never outlive a layout change.
    private List<(Node Node, Socket Socket, Point Center)>? _pinCenterCache;

    // Self-healing wire redraw. RedrawLinks skips any wire
    // whose pin centre can't be resolved yet (the node body hasn't finished its
    // measure/arrange, so the pin reports zero size / a stale transform). Before,
    // nothing re-ran the pass once layout settled, so the wire stayed invisible until
    // the user moved a node (which forced a fresh RedrawLinks) — the reported "pipes
    // disappear and only respawn when moving a node". Now a pass that skipped a wire
    // for that reason schedules a bounded retry on a later tick. The budget refills on
    // any fully-resolved pass and on every Rebuild cycle (OnFirstLayoutUpdated), so the
    // bound only stops a pathological never-laid-out loop, never normal authoring.
    private const int RedrawRetryBudgetMax = 8;
    private int _redrawRetryBudget = RedrawRetryBudgetMax;

    // Sentinel size for the viewport clip rect before the
    // first layout pass measures HostGrid (ActualWidth/Height are 0 in the
    // ctor). Large enough never to clip real content; the SizeChanged handler
    // replaces it with the live footprint. Mirrors WidgetView's ClipSentinel.
    private const double GraphViewportClipSentinel = 1_000_000d;

    public WidgetGraphCanvas()
    {
        InitializeComponent();

        // Wire the canvas-header snap toggle to the same SnapEnabled
        // state the RMB flyout owns. _suppressSnapToggleDepth guards against
        // re-entrancy when the setter syncs the visual.
        SnapToggle.Content = Localizer.T("visualist.canvas.snap.toggle", "Snap to grid");
        SnapToggle.IsChecked = SnapEnabled;
        SnapToggle.Checked   += OnSnapToggleChanged;
        SnapToggle.Unchecked += OnSnapToggleChanged;

        HostGrid.PointerPressed  += OnHostPointerPressed;
        HostGrid.PointerMoved    += OnHostPointerMoved;
        HostGrid.PointerReleased += OnHostPointerReleased;
        HostGrid.PointerCanceled    += OnHostPointerLost;
        HostGrid.PointerCaptureLost += OnHostPointerLost;
        HostGrid.PointerWheelChanged += OnHostPointerWheelChanged;
        HostGrid.Tapped += OnHostTapped;
        HostGrid.RightTapped += OnHostRightTapped;

        // Keyboard zoom — Ctrl+0 reset, Ctrl++/= zoom in, Ctrl+- zoom out.
        // Make HostGrid
        // focusable + Focus on PointerPressed so the keys land on the canvas
        // rather than bubbling to the embedding view.
        HostGrid.IsTabStop = true;
        HostGrid.KeyDown += OnHostKeyDown;
        HostGrid.PointerPressed += (_, _) =>
        {
            try { HostGrid.Focus(FocusState.Programmatic); } catch { }
        };

        // Clip the graph viewport to its own bounds. WinUI
        // Grid/Canvas do NOT clip their children (there is no WPF-style
        // ClipToBounds), so a node panned/zoomed so its transformed position
        // lands above the viewport top used to render OVER the editor toolbar
        // (the Media/Audio tabs + trigger strip that sit in the rows ABOVE this
        // control). A RectangleGeometry tracked on SizeChanged — the same idiom
        // WidgetView uses for WidgetFrame — contains the WorldCanvas/LinkLayer
        // tree to the viewport. Seed a large sentinel so content stays visible
        // before the first layout pass (ActualWidth/Height are 0 in the ctor);
        // the SizeChanged handler below stamps the real footprint over it.
        HostGrid.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, GraphViewportClipSentinel, GraphViewportClipSentinel),
        };

        // Repaint the dotted-grid backdrop when the host
        // viewport resizes (pillar split-bar drag, pop-out window). Also
        // re-stamp the viewport clip rect so it tracks the
        // live size and keeps clipping out-of-bounds nodes off the toolbar.
        HostGrid.SizeChanged += (_, e) =>
        {
            RefreshGridDots();
            if (HostGrid.Clip is Microsoft.UI.Xaml.Media.RectangleGeometry rg)
                rg.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        };

        // Single CompositionTarget.Rendering subscription for the
        // frame-coalesced wire-redraw pipeline. Symmetric Loaded/Unloaded so
        // a tab-swap recycle doesn't leak the handler.
        Loaded   += OnCanvasLoaded;
        Unloaded += OnCanvasUnloaded;
    }

    private void OnCanvasLoaded(object sender, RoutedEventArgs e)
    {
        if (_renderingHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRenderingTick;
        _renderingHooked = true;
    }

    private void OnCanvasUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_renderingHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingTick;
        _renderingHooked = false;
    }

    /// <summary>
    /// Per-frame consumer of the redraw dirty flag. Cheap when
    /// nothing changed (single bool check), so the permanent subscription
    /// doesn't waste cycles on an idle canvas. Mirrors Architect's
    /// LogicCanvasView.OnRenderingTick.
    /// </summary>
    private void OnRenderingTick(object? sender, object e)
    {
        if (!_redrawDirty) return;
        _redrawDirty = false;
        try
        {
            RerouteDraggedNodeWires();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "OnRenderingTick.RedrawLinks", ex);
        }
    }

    /// <summary>
    /// Pointer-move handlers call this instead of RedrawLinks()
    /// directly so multiple moves within one frame collapse to a single
    /// rebuild. Setting the flag is O(1); the actual wire pass runs on the
    /// next CompositionTarget.Rendering tick.
    /// </summary>
    private void QueueRedrawLinks() => _redrawDirty = true;

    /// <summary>
    /// Per-frame wire pass for the node-drag pipeline. While a node drag is
    /// live only the wires incident to the moved node set can change shape, so
    /// mutate just those Paths' Data in place (identical curve math via
    /// UpdateBezierWire) and leave every other wire — plus the temp-wire
    /// z-order and the selection highlight Path — untouched. Anything the fast
    /// path can't vouch for (no live drag state, a link without a cached Path,
    /// a pin mid-layout) falls back to the full RedrawLinks rebuild, which
    /// remains the sole authority for structural changes.
    /// </summary>
    private void RerouteDraggedNodeWires()
    {
        var links = _trigger?.Graph?.Links;
        if (links is null) { RedrawLinks(); return; }

        _rerouteNodeIds.Clear();
        if (_isGroupDragging)
        {
            foreach (var v in _groupDragOrigins.Keys) _rerouteNodeIds.Add(v.Node.Id);
        }
        else if (_dragNode is { } dragged)
        {
            _rerouteNodeIds.Add(dragged.Node.Id);
        }

        // The tick can land after the release cleared the drag state (or the
        // full pass hasn't populated the Path map yet) — full rebuild then,
        // exactly what every queued redraw did before the fast path existed.
        if (_rerouteNodeIds.Count == 0 || _linkPaths.Count == 0)
        {
            RedrawLinks();
            return;
        }

        foreach (Link link in links)
        {
            if (!_rerouteNodeIds.Contains(link.FromNodeId)
                && !_rerouteNodeIds.Contains(link.ToNodeId))
                continue;
            // Links whose endpoint views don't exist are skipped by the full
            // pass too — nothing to reroute.
            if (!_nodeViews.TryGetValue(link.FromNodeId, out var fromView)) continue;
            if (!_nodeViews.TryGetValue(link.ToNodeId,   out var toView))   continue;
            if (!_linkPaths.TryGetValue(link, out var path)
                || !TryGetPinCenter(fromView, link.FromSocketId, out var p1)
                || !TryGetPinCenter(toView,   link.ToSocketId,   out var p2))
            {
                RedrawLinks();
                return;
            }
            UpdateBezierWire(path, p1, p2, path.Stroke);
        }
    }

    private void OnHostKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // When an inline value-pill or F2-rename TextBox owns focus, none
        // of the canvas shortcuts may fire: F2 would re-target the rename, Delete
        // would destroy the node being edited (data loss), Ctrl+A/C/X/V would act
        // on nodes instead of text. The focused TextBox owns its keys — bail
        // before any canvas handling. (IsTextInputFocused was added for the
        // F/Space palette; this extends the guard to the whole handler.)
        if (IsTextInputFocused()) return;

        // Esc clears the node + wire selection at canvas scope (parity with
        // the layer canvas + every blueprint editor). A focused pill / rename box
        // consumes Esc itself (cancel), so this only fires on the bare canvas.
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            bool had = _selectedNodes.Count > 0 || _selectedNode is not null || _selectedLink is not null;
            SelectNode(null);
            ClearLinkSelection();
            if (had) e.Handled = true;
            return;
        }

        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        // Save / SaveAs / Undo / Redo chords. HubChrome registers
        // matching KeyboardAccelerator objects at the chrome scope; this is
        // the canvas-focus path (focus lands on HostGrid after a click). Save
        // is VirtualKey.S — same row across QWERTY/QWERTZ/AZERTY.
        //
        // Undo / Redo are keyed on the layout-mapped VirtualKey (the LABEL on the
        // keytop), NOT the physical scancode. The old scancode anchor (0x2C/0x15)
        // tracks the PHYSICAL key position, which on a German QWERTZ keyboard is
        // the "Y"/"Z" keytops INVERTED from QWERTY — so Strg+Z (the Z keytop) hit
        // the redo scancode and undo never fired for QWERTZ users (Majo's
        // "Ctrl+Z doesn't work / doesn't accept all keyboard variations"). e.Key
        // == VirtualKey.Z resolves to whatever key produces 'Z' on the active
        // layout, so the visible Z keytop always undoes on every layout.
        try
        {
            if (ctrl)
            {
                if (e.Key == Windows.System.VirtualKey.Z)
                {
                    bool shiftZ = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                                   & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
                    var mvZ = FindPillarMainView();
                    if (mvZ is not null)
                    {
                        if (shiftZ) mvZ.Redo(); else mvZ.Undo();
                        e.Handled = true;
                        return;
                    }
                }
                if (e.Key == Windows.System.VirtualKey.Y)
                {
                    var mvY = FindPillarMainView();
                    if (mvY is not null)
                    {
                        mvY.Redo();
                        e.Handled = true;
                        return;
                    }
                }
            }
            if (e.Key == Windows.System.VirtualKey.S && ctrl)
            {
                bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                              & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
                var mv = FindPillarMainView();
                if (mv is not null)
                {
                    if (shift) mv.SaveLayerAs(); else mv.SaveLayer();
                    e.Handled = true;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "Save/Undo chord", ex);
        }

        // F2 — inline rename
        // of the selected node. See BeginNodeRename for the label-vs-type
        // rationale (Title is the registry template key and must stay immutable;
        // the editor writes the per-instance Node.Description label instead).
        if (e.Key == Windows.System.VirtualKey.F2)
        {
            BeginNodeRename();
            e.Handled = true;
            return;
        }

        // F (and Space, for Architect muscle-memory) opens the searchable
        // spawn palette. Manifesto §4.6: "Search palette (F key) and compatible-
        // socket spawn menus carry over from Architect." The WinUI Visualist
        // dropped it (only the cascading right-click menu remained). Guard against
        // a focused text editor (inline value pill / F2 rename box) so typing 'f'
        // or a space there doesn't pop the palette.
        if (!ctrl
            && (e.Key == Windows.System.VirtualKey.F || e.Key == Windows.System.VirtualKey.Space)
            && !IsTextInputFocused())
        {
            ShowSpawnSearchPalette(null);
            e.Handled = true;
            return;
        }

        // Cut/Copy/Paste/Select-All/Delete on the
        // node multi-selection. Per-pillar Visualist clipboard buffer.
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            // A selected wire takes Delete precedence over node deletion.
            if (_selectedLink is { } link)
            {
                DeleteLink(link);
                _selectedLink = null;
                _selectedLinkPath = null;
                e.Handled = true;
                return;
            }
            if (DeleteSelectedNodes()) e.Handled = true;
            return;
        }
        if (ctrl)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.A:
                    SelectAllNodes();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.C:
                    CopySelectedNodes();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.X:
                    CutSelectedNodes();
                    e.Handled = true;
                    return;
                case Windows.System.VirtualKey.V:
                    // Paste now reads the OS clipboard's
                    // PhoenixControls.SubGraph payload (Architect copy) when
                    // the in-process buffer is empty, so fire-and-forget the
                    // async path via the dispatcher.
                    // route through the error-boundary
                    // wrapper so a clipboard / deserialize fault doesn't fault an
                    // unobserved Task (AsyncErrorBoundary is Hub-only, off-limits here).
                    _ = PasteWithErrorBoundaryAsync();
                    e.Handled = true;
                    return;
            }
        }

        if (!ctrl) return;

        double oldScale = WorldTransform.ScaleX == 0 ? 1.0 : WorldTransform.ScaleX;
        double? targetScale = e.Key switch
        {
            Windows.System.VirtualKey.Number0      => 1.0,
            Windows.System.VirtualKey.Add          => Math.Min(MaxZoom, oldScale * 1.1),
            (Windows.System.VirtualKey)187         => Math.Min(MaxZoom, oldScale * 1.1),  // VK_OEM_PLUS / Ctrl+=
            Windows.System.VirtualKey.Subtract     => Math.Max(MinZoom, oldScale / 1.1),
            (Windows.System.VirtualKey)189         => Math.Max(MinZoom, oldScale / 1.1),  // VK_OEM_MINUS
            _                                       => null,
        };
        if (targetScale is not double newScale) return;
        newScale = Math.Clamp(newScale, MinZoom, MaxZoom);
        if (Math.Abs(newScale - oldScale) < 0.0001) { e.Handled = true; return; }

        // Anchor on the host viewport centre — there's no cursor on a key
        // press, mirroring the wheel handler's anchor pattern but at centre.
        double cx = HostGrid.ActualWidth  / 2;
        double cy = HostGrid.ActualHeight / 2;
        double tx = WorldTransform.TranslateX;
        double ty = WorldTransform.TranslateY;
        double worldX = (cx - tx) / oldScale;
        double worldY = (cy - ty) / oldScale;
        WorldTransform.ScaleX = newScale;
        WorldTransform.ScaleY = newScale;
        WorldTransform.TranslateX = cx - newScale * worldX;
        WorldTransform.TranslateY = cy - newScale * worldY;
        RefreshGridDots();
        e.Handled = true;
    }

    // ── F2 inline rename ─────────────────────────────────────────────────────
    //
    // The pre-T15 audit flagged the missing F2=rename idiom. There
    // is a constraint the baseline never hit: a Visualist node's `Title` is the
    // WidgetNodeRegistry template key (DuplicateNode / preview / the exporter all
    // resolve off it), so renaming Title would break node identity. The faithful
    // recovery is to rename the per-instance LABEL — `Node.Description`, the same
    // editable instance field Architect's inspector exposes — and surface it as
    // the node's hover tooltip (the node body's TitleText is owned by
    // WidgetGraphNodeView, which is outside this lane, so we can't repaint the
    // header in-place; the tooltip is the visible feedback channel we can drive
    // from the canvas).
    //
    // The editor is a code-behind TextBox overlaid on WorldCanvas at the node's
    // location — no XAML change (the .xaml is outside this lane). Enter / blur
    // commit; Escape reverts.
    private TextBox? _renameEditor;
    private WidgetGraphNodeView? _renameTarget;

    private void BeginNodeRename()
    {
        var target = _selectedNode
                     ?? (_selectedNodes.Count == 1 ? _selectedNodes[0] : null);
        if (target is null) return;
        CommitNodeRename(); // close any in-flight editor first

        _renameTarget = target;
        double left = global::Microsoft.UI.Xaml.Controls.Canvas.GetLeft(target);
        double top  = global::Microsoft.UI.Xaml.Controls.Canvas.GetTop(target);
        double width = target.ActualWidth > 0 ? target.ActualWidth : target.MinWidth;
        if (width <= 0) width = 160;

        var box = new TextBox
        {
            Text   = string.IsNullOrEmpty(target.Node.Description) ? target.Node.Title : target.Node.Description,
            Width  = width,
            MinHeight = 28,
            FontFamily = ResolveFontFamily("MonoFont"),
            FontSize = 11,
            Background = ResolveBrush("CoalRaisedBrush", 0xFF, 0x1B, 0x17, 0x13),
            Foreground = ResolveBrush("CoalPrimaryTextBrush", 0xFF, 0xE6, 0xDD, 0xD0),
            BorderBrush = ResolveBrush("EmberPrimaryBrush", 0xFF, 0xE5, 0xA2, 0x4E),
            BorderThickness = new Thickness(1),
        };
        ToolTipService.SetToolTip(box,
            Localizer.T("visualist.canvas.rename.tooltip",
                        "Rename label — Enter to commit, Esc to cancel."));
        global::Microsoft.UI.Xaml.Controls.Canvas.SetLeft(box, left);
        global::Microsoft.UI.Xaml.Controls.Canvas.SetTop (box, top);
        global::Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(box, 10_000);

        box.KeyDown += OnRenameEditorKeyDown;
        box.LostFocus += (_, _) => CommitNodeRename();

        WorldCanvas.Children.Add(box);
        _renameEditor = box;
        box.SelectAll();
        try { box.Focus(FocusState.Programmatic); } catch { }
    }

    private void OnRenameEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitNodeRename();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelNodeRename();
            e.Handled = true;
        }
    }

    private void CommitNodeRename()
    {
        if (_renameEditor is null || _renameTarget is null)
        {
            TeardownRenameEditor();
            return;
        }
        var view = _renameTarget;
        string newLabel = (_renameEditor.Text ?? "").Trim();
        // Detach LostFocus before teardown so removing the editor doesn't re-enter.
        TeardownRenameEditor();

        // Empty input is treated as "clear the instance label" (fall back to the
        // template title display). Only snapshot + mark dirty when it changed.
        string current = view.Node.Description ?? "";
        if (string.Equals(newLabel, current, StringComparison.Ordinal)) return;

        _vm?.Document?.PushUndo();
        view.Node.Description = newLabel;
        // Surface the label as the node's hover tooltip (the only visible
        // feedback we can drive without touching the node-view header).
        ApplyNodeLabelTooltip(view);
        _vm?.Document?.MarkDirty();
        GlobalLogger.Log(
            string.IsNullOrEmpty(newLabel)
                ? $"Cleared label on '{view.Node.Title}'."
                : $"Renamed '{view.Node.Title}' label → \"{newLabel}\".",
            "WidgetGraphCanvas", LogLevel.System);
    }

    private void CancelNodeRename() => TeardownRenameEditor();

    private void TeardownRenameEditor()
    {
        if (_renameEditor is not null)
        {
            _renameEditor.KeyDown -= OnRenameEditorKeyDown;
            WorldCanvas.Children.Remove(_renameEditor);
            _renameEditor = null;
        }
        _renameTarget = null;
    }

    private static void ApplyNodeLabelTooltip(WidgetGraphNodeView view)
    {
        string label = view.Node.Description ?? "";
        if (string.IsNullOrEmpty(label))
            ToolTipService.SetToolTip(view, null);
        else
            ToolTipService.SetToolTip(view, $"{view.Node.Title} — {label}");
    }

    private static FontFamily ResolveFontFamily(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found))
            {
                if (found is FontFamily ff) return ff;
                if (found is string s) return new FontFamily(s);
            }
        }
        catch { /* designer / pre-app */ }
        return new FontFamily("Consolas");
    }

    /// <summary>
    /// Tear down any in-flight pan/lasso when the pointer capture is lost
    /// (window deactivation, alt-tab, focus stolen by a flyout). Without this
    /// the lasso rectangle stays orphan in LinkLayer and _isPanning stays
    /// true, so the next drag can't start cleanly.
    /// </summary>
    private void OnHostPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ClearTransientHotkeyContext();
            ProtectedCursor = null;
        }
        if (_isLassoing)
        {
            _isLassoing = false;
            if (_lassoRect is not null) LinkLayer.Children.Remove(_lassoRect);
            _lassoRect = null;
        }
    }

    /// <summary>
    /// Bind the canvas to a Visualist VM and a specific trigger object. Pass
    /// null trigger to clear the canvas (e.g. when no widget is selected).
    /// </summary>
    public void Bind(VisualistViewModel vm, WidgetTrigger? trigger)
    {
        _vm = vm;
        // Record-arm is a transient authoring mode scoped to the trigger
        // being edited; reset it when the bound trigger changes so a pill that
        // was armed on one trigger doesn't silently record on another.
        if (!ReferenceEquals(_trigger, trigger)) _armedParams.Clear();
        _trigger = trigger;
        // Wire selection doesn't survive a trigger swap.
        _selectedLink = null;
        _selectedLinkPath = null;
        Rebuild();
    }

    /// <summary>
    /// Re-render ONE node's body (inline pills / sockets) in place from its
    /// current model attributes, WITHOUT a Bind/Rebuild — so the selection, the
    /// active preview manipulator, and every other node view survive. Used after
    /// a manipulator drag (bug #4 — the old full RebuildGraphView wiped the node
    /// selection on mouse-up, cancelling further edits) and after an Inspector
    /// param edit (bug #3 — the node-body pills didn't reflect the right-pane
    /// change). No-ops when the node isn't currently realised.
    /// </summary>
    public void RefreshNode(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        if (!_nodeViews.TryGetValue(nodeId!, out var view)) return;
        view.Refresh();
        // A pill's text length change can shift the node's content height, which
        // moves its socket anchors — re-draw incident wires on the next tick
        // (after the node re-arranges) so they stay attached to their bubbles.
        DispatcherQueue?.TryEnqueue(RedrawLinks);
    }

    // ─── rendering ───────────────────────────────────────────────────────

    private void Rebuild()
    {
        WorldCanvas.Children.Clear();
        LinkLayer.Children.Clear();
        _linkPaths.Clear();       // Paths just left the tree — drop the reroute map
        _pinCenterCache = null;   // node views are being replaced wholesale
        // unhook pin pointer / right-tap handlers from
        // the outgoing node views before dropping them, otherwise every Rebuild
        // (graph load / add / delete / paste) leaks the prior views via their live
        // routed-event subscriptions. Symmetric to HookPinHandlers (called per view
        // in the rebuild's add-loop below).
        foreach (var oldView in _nodeViews.Values)
            UnhookPinHandlers(oldView);
        _nodeViews.Clear();
        _selectedNode = null;
        NotifySelectedNodeChanged(null);   // graph rebind clears the manipulator
        _tempWirePath = null;
        _wireSourceNode   = null;
        _wireSourceSocket = null;
        _wireSourcePin    = null;
        // The hovered pin element leaves the tree with its node view; the
        // stale ids would otherwise re-arm the wire highlight on the rebuilt
        // Paths with no pointer over the replacement pin.
        _hoveredPinNodeId   = null;
        _hoveredPinSocketId = null;

        var nodes = _trigger?.Graph?.Nodes;
        if (_trigger is null)
        {
            // Advertise only the gesture that works in this state.
            EmptyHint.Text = Localizer.T("visualist.canvas.emptyHint.noWidget",
                "No widget selected.\n\nDouble-click a widget on the Layer Canvas (or pick one from the Widgets list) to author its trigger graphs here.");
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        // Manifesto §4.5 — every per-trigger graph carries an auto-injected
        // Display sink. If the loaded graph is missing one (legacy file, hand
        // authored, or a freshly added trigger via AddTrigger which creates an
        // empty Graph), inject one now and surface the existing-but-previously-
        // unreferenced lang token via GlobalLogger so authors notice the
        // recovery happened. The injection runs before the empty-state check
        // so the canvas is never rendered without its terminal node.
        if (_trigger.Graph is { } graph && !graph.Nodes.Any(DisplaySinkNode.Is))
        {
            var injected = DisplaySinkNode.Build();
            // Anchor the auto-injected sink at canvas-bottom-right (relative
            // to the 4000×4000 WorldCanvas) so it lands clear of the typical
            // node-spawn region near the origin. Authors can drag it elsewhere
            // freely; the position is just a sensible default.
            injected.Location = new System.Drawing.Point(680, 360);
            graph.Nodes.Add(injected);
            if (_vm?.Document is { } d) d.MarkDirty();

            GlobalLogger.Log(
                Localizer.T("visualist.canvas.status.displayRequired"),
                "WidgetGraphCanvas", LogLevel.System);

            // Refresh the local handle since Rebuild reads the same Nodes list.
            nodes = graph.Nodes;
        }

        if (nodes is null || nodes.Count == 0)
        {
            // Onboarding for an empty trigger graph, listing only gestures
            // that work today: right-click spawn palette, F search, drag-from-Media.
            string body = Localizer.T("visualist.canvas.emptyHint.body",
                "is empty.\n\n· Right-click the canvas to open the spawn palette\n· Press F to search for a node\n· Drag a tile from the Media library onto the canvas");
            EmptyHint.Text = $"Trigger \"{_trigger.Name}\" {body}";
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;

        // Stagger nodes that all share Location (0,0) so a freshly-authored
        // graph from a non-positional source is at least readable. Nodes
        // saved by the WinForms Visualist already carry sensible Locations.
        int staggerIdx = 0;
        foreach (Node node in nodes)
        {
            if (node.Location.X == 0 && node.Location.Y == 0)
            {
                node.Location = new System.Drawing.Point(40 + staggerIdx * 220, 40 + (staggerIdx % 4) * 160);
                staggerIdx++;
            }

            var view = new WidgetGraphNodeView(node)
            {
                MinWidth = 160,
            };
            global::Microsoft.UI.Xaml.Controls.Canvas.SetLeft(view, node.Location.X);
            global::Microsoft.UI.Xaml.Controls.Canvas.SetTop (view, node.Location.Y);

            view.PointerPressed  += OnNodePointerPressed;
            view.PointerMoved    += OnNodePointerMoved;
            view.PointerReleased += OnNodePointerReleased;

            WorldCanvas.Children.Add(view);
            _nodeViews[node.Id] = view;

            // Inline value-pill commit. The node view renders an editable
            // pill next to every input socket with a matching Node.Attributes
            // key; commits route here so the write gets undo + dirty + rebuild.
            view.SetAttributeCommit(OnNodeAttributeCommitted);

            // Inline ◀ ◇ ▶ arm/seek cluster on animatable scalar pills.
            // Capture `node` so the per-attr callbacks resolve the parameter
            // path. SetPillAnimation re-renders the node so the clusters appear.
            var capturedNode = node;
            view.SetPillAnimation(
                show:      (socket, key) => IsArmSeekPill(capturedNode, socket, key),
                armed:     key => _armedParams.Contains(ParamPath(capturedNode, key)),
                toggleArm: key => ToggleParamArm(capturedNode, key),
                seekPrev:  key => SeekParam(capturedNode, key, -1),
                seekNext:  key => SeekParam(capturedNode, key, +1));

            // Wire-drag from any pin. Hooked at Add-time so the
            // event seam survives Refresh()-driven re-renders within the node.
            HookPinHandlers(view);
            // Right-click context menu (Duplicate / Delete +
            // contextual Edit Shape / Browse Media / Animate entries).
            HookNodeContextFlyout(view);
            // Surface a persisted F2-rename label as the node's
            // hover tooltip so it survives graph reloads.
            ApplyNodeLabelTooltip(view);
        }

        // Layout pass needs to complete before TransformToVisual gives real
        // pin coords — defer link-render to LayoutUpdated so the first paint
        // already shows wires. Unsubscribe first: Rebuild runs on EVERY trigger-tab
        // switch (Bind → Rebuild), and if a prior switch's LayoutUpdated handler
        // hadn't fired yet, a bare `+=` stacks duplicate subscriptions on the shared
        // WorldCanvas. Multiple OnFirstLayoutUpdated runs then redraw links / previews
        // against stale per-trigger state, leaving later tabs' graphs blank or
        // overlapping. One subscription at a time keeps each tab's graph correct.
        WorldCanvas.LayoutUpdated -= OnFirstLayoutUpdated;
        WorldCanvas.LayoutUpdated += OnFirstLayoutUpdated;
    }

    private void OnFirstLayoutUpdated(object? sender, object e)
    {
        WorldCanvas.LayoutUpdated -= OnFirstLayoutUpdated;
        // Fresh layout cycle → fresh retry budget so the
        // self-healing redraw always has attempts to spend after a load / trigger swap.
        _redrawRetryBudget = RedrawRetryBudgetMax;
        RedrawLinks();
        // Populate body-preview thumbnails after the first layout
        // pass so the node views exist when SetPreview is called.
        RefreshPreviews();
        // Initial dotted-grid paint once HostGrid has
        // measured. SnapEnabled defaults to off, but seeding here means the
        // first toggle-on paints immediately rather than waiting for a pan.
        RefreshGridDots();
        // Bug #4 (sockets/wires misaligned on enter; self-corrects on a node
        // move) — this first WorldCanvas LayoutUpdated can fire BEFORE each
        // node view's inner pin StackPanel has finished its own measure/arrange,
        // so TryGetPinCenter's pin.TransformToVisual returns stale/zero coords
        // and the wires anchor to the wrong points. Moving a node later forces
        // that node's re-arrange and a RedrawLinks, which is why it "fixes
        // itself". Re-run RedrawLinks one more time on a dispatcher tick — by
        // then the node bodies have completed arrange, so the pins report real
        // positions and every wire snaps to its bubble without a manual nudge.
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RedrawLinks);
    }

    // ─── link rendering ──────────────────────────────────────────────────

    private void RedrawLinks()
    {
        // Preserve the temp wire (if a drag is active) — only links from the
        // graph model are reset. Strip everything else.
        // Re-add the in-flight temp wire LAST (after the model links
        // below) rather than first, so the drag preview always renders ON TOP of
        // the committed wires. Previously it was re-added first, which let a
        // model wire painted afterward obscure the preview when a RedrawLinks()
        // landed mid-drag (e.g. a Delete-key link removal). Combined with the
        // in-place mutation in OnWorldPointerMovedForWire, the preview's element
        // identity + z-order now both survive a mid-drag redraw.
        var keep = _tempWirePath;
        LinkLayer.Children.Clear();
        _linkPaths.Clear();       // rebuilt below alongside the Paths themselves
        _pinCenterCache = null;   // layout may shift under a mid-drag redraw

        var links = _trigger?.Graph?.Links;
        if (links is null) { _selectedLink = null; _selectedLinkPath = null; return; }

        // Drop a stale wire selection if the link was deleted since the
        // last paint, so the highlight + Delete-key target never dangle.
        if (_selectedLink is { } sel0 && !links.Any(l => l.Id == sel0.Id))
            _selectedLink = null;
        _selectedLinkPath = null;

        bool layoutPending = false;
        foreach (Link link in links)
        {
            if (!_nodeViews.TryGetValue(link.FromNodeId, out var fromView)) continue;
            if (!_nodeViews.TryGetValue(link.ToNodeId,   out var toView))   continue;

            // Both endpoint nodes are realised; if a pin
            // centre still can't be resolved it's almost always because the node body
            // hasn't completed measure/arrange (pin size 0 / stale transform). Flag
            // that as layoutPending so the post-loop schedules a retry — instead of
            // dropping the wire until the next node move. A pin element that's genuinely
            // absent (stale link / removed socket) is NOT flagged, so it never loops.
            bool gotFrom = TryGetPinCenter(fromView, link.FromSocketId, out var p1);
            bool gotTo   = TryGetPinCenter(toView,   link.ToSocketId,   out var p2);
            if (!gotFrom || !gotTo)
            {
                if (PinAwaitingLayout(fromView, link.FromSocketId)
                    || PinAwaitingLayout(toView, link.ToSocketId))
                    layoutPending = true;
                continue;
            }

            // Resolve the source socket so the wire reflects the Flow/data
            // distinction (exec wires read white + heavier; data wires carry the
            // type colour) instead of a single flat neutral.
            Socket? srcSocket = fromView.Node.Sockets?
                .FirstOrDefault(s => string.Equals(s.Id, link.FromSocketId, StringComparison.Ordinal));
            bool isFlow   = srcSocket?.DataType == SocketDataType.Flow;
            bool selected = _selectedLink is { } sl && sl.Id == link.Id;

            var path = BuildBezierWire(p1, p2,
                WireBrushFor(srcSocket, isFlow, selected),
                WireThicknessFor(isFlow, selected));
            // Wire right-click flyout. The wire is hit-testable
            // because we leave LinkLayer.IsHitTestVisible=False but each
            // path opts back in with its own hit visibility (XAML inherits
            // false from the parent if the parent is false-hit, so we lift
            // the parent at runtime instead).
            AttachWireContextFlyout(path, link);
            // Left-tap selects the wire (gold highlight). Capture the link
            // + path so the selection survives the closure and the next redraw.
            var capturedLink = link;
            path.Tapped += (s, ev) => { SelectLink(capturedLink); ev.Handled = true; };
            LinkLayer.Children.Add(path);
            _linkPaths[link] = path;   // feed the node-drag incident reroute
            if (selected) _selectedLinkPath = path;

            // Re-apply the pin-hover emphasis the same way selection is
            // re-applied above — a frame-coalesced redraw landing mid-hover
            // otherwise rebuilds this Path with base styling and silently
            // drops the effect. Suppressed while a wire drag is live (the
            // drag preview owns wire coloring; BeginWireDrag also clears the
            // hover state, so this guard is belt-and-braces).
            if (_wireSourceNode is null && IsIncidentToHoveredPin(link))
                ApplyWireStyle(link, path, hovered: true);
        }

        // Re-add the in-flight temp wire LAST so the drag preview
        // sits on top of every committed wire (z-order), and re-attaches even if
        // a mid-drag RedrawLinks() cleared the layer beneath it.
        if (keep is not null) LinkLayer.Children.Add(keep);

        // Lift LinkLayer hit-test so right-click on a wire reaches the
        // path's ContextFlyout. Done after children are added so the temp
        // wire and the lasso rectangle still render but stay
        // non-blocking via their own IsHitTestVisible=false flags.
        LinkLayer.IsHitTestVisible = true;

        // Self-heal a pass that skipped wires only because
        // their nodes weren't laid out yet: re-run on a later (Low) tick so they
        // appear on their own. Bounded by _redrawRetryBudget against a node that
        // never lays out; a fully-resolved pass refills the budget.
        if (layoutPending && _redrawRetryBudget > 0)
        {
            _redrawRetryBudget--;
            DispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RedrawLinks);
        }
        else if (!layoutPending)
        {
            _redrawRetryBudget = RedrawRetryBudgetMax;
        }
    }

    // True when the socket's pin element EXISTS on the node
    // view but hasn't been measured yet (zero size) — i.e. the wire skip is a layout
    // race a retry will clear, not a genuinely absent pin (stale link / removed
    // socket), which must NOT arm a retry. Mirrors TryGetPinCenter's size guard.
    private static bool PinAwaitingLayout(WidgetGraphNodeView nodeView, string socketId)
        => nodeView.PinElements.TryGetValue(socketId, out var pin)
           && (pin.ActualWidth <= 0 || pin.ActualHeight <= 0);

    // Resolve the pin FrameworkElement for a (node, socket) pair so
    // BeginWireDrag can record _wireSourcePin when the drag is started from the
    // deterministic hand-off path (where we only hold the resolved socket, not the
    // pin element the press landed on). Best-effort: a missing view/socket yields
    // null, and BeginWireDrag treats _wireSourcePin as optional.
    private FrameworkElement? ResolvePinElement(Node node, Socket socket)
    {
        if (_nodeViews.TryGetValue(node.Id, out var view)
            && view.PinElements.TryGetValue(socket.Id, out var pin))
            return pin;
        return null;
    }

    // Pin world-coord = center of the pin Rectangle relative to WorldCanvas.
    // TransformToVisual drops out the parent pan/zoom transform automatically
    // since both elements share the ViewSurface tree; the returned point is
    // already in WorldCanvas-local coordinates.
    private bool TryGetPinCenter(WidgetGraphNodeView nodeView, string socketId, out Point center)
    {
        center = default;
        if (!nodeView.PinElements.TryGetValue(socketId, out var pin)) return false;
        if (pin.ActualWidth <= 0 || pin.ActualHeight <= 0) return false;
        try
        {
            var t = pin.TransformToVisual(WorldCanvas);
            center = t.TransformPoint(new Point(pin.ActualWidth / 2.0, pin.ActualHeight / 2.0));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Wire brush. A selected wire is §2 gold; a Flow (exec) wire reads as
    // a bright neutral white the way every blueprint editor paints exec lines;
    // a data wire carries its source socket's DataType colour
    // (WidgetSocketPalette.EffectiveColor — back-filled when Socket.Color is the
    // default white). Slightly transparent so dense graphs don't drown the canvas.
    private static Brush WireBrushFor(Socket? srcSocket, bool isFlow, bool selected)
    {
        if (selected) return s_wireSelected;
        if (isFlow)   return s_wireExec;
        if (srcSocket is not null)
        {
            // One brush per distinct type colour, minted on first use —
            // brushes are shareable immutable-in-practice resources (same
            // pattern as s_wireFallback), so per-wire allocation is waste.
            var c = WidgetSocketPalette.EffectiveColor(srcSocket);
            uint key = 0xCC000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            if (!s_wireTypeBrushes.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(Color.FromArgb(0xCC, c.R, c.G, c.B));
                s_wireTypeBrushes[key] = brush;
            }
            return brush;
        }
        return s_wireFallback;
    }

    // Session-stable wire brushes (see WireBrushFor). Selected = §2 gold
    // (SelectionBrush); exec = bright neutral white.
    private static readonly Brush s_wireSelected =
        new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
    private static readonly Brush s_wireExec =
        new SolidColorBrush(Color.FromArgb(0xE0, 0xEC, 0xE6, 0xDA));
    private static readonly Dictionary<uint, Brush> s_wireTypeBrushes = new();

    // Exec wires are heavier than data wires; selection thickens further.
    private static double WireThicknessFor(bool isFlow, bool selected)
        => selected ? 3.5 : (isFlow ? 3.0 : 2.0);

    // pre-allocated neutral wire fallback when the
    // source socket can't be resolved.
    private static readonly Brush s_wireFallback =
        new SolidColorBrush(Color.FromArgb(0x60, 0xA8, 0x96, 0x83));

    // Left-tap on a wire selects it (gold highlight). Clearing node /
    // wire selection is symmetric so a wire and a node are never both "selected".
    private void SelectLink(Link link)
    {
        ClearSelection();          // drop any node selection
        _selectedLink = link;
        RedrawLinks();             // repaint so the gold highlight applies
    }

    private void ClearLinkSelection()
    {
        if (_selectedLink is null) return;
        _selectedLink = null;
        _selectedLinkPath = null;
        RedrawLinks();
    }

    // Bezier control-point math ported from Architect's
    // BezierPath.cs (chrome-independence: COPIED, not referenced). The naive
    // `dx = |p2.X - p1.X| * 0.5` collapses to a near-straight line on
    // back-routing feedback loops (target sits LEFT of source) — the wire dives
    // through the source node body. The Architect formula composes the inset from
    // a horizontal half-distance, a vertical-distance term so steep loops bow
    // outward, a back-routing kicker when the wire runs right-to-left, and a
    // MinTangent floor so even short runs leave the endpoint with a visible
    // curve. Plus a self-loop branch (both endpoints coincident → arc upward and
    // around the source) so a self-connection is discoverable instead of a tight
    // arc hidden behind the node body. Visualist-local mirror — kept here so the
    // pillar owns its own wire paint.
    private const double WireMinTangent      = 40.0;
    private const double WireSelfLoopEpsilon = 0.5;
    private const double WireSelfLoopArc     = 80.0;

    private static double WireControlDistance(Point p1, Point p2)
    {
        double horizontal = Math.Abs(p2.X - p1.X) * 0.5;
        double vertical   = Math.Abs(p2.Y - p1.Y) * 0.35;
        double backKick   = (p2.X < p1.X) ? Math.Abs(p2.Y - p1.Y) * 0.45 : 0.0;
        return Math.Max(WireMinTangent, horizontal + vertical + backKick);
    }

    // Resolve the two cubic control points for a wire (p1 → p2), honouring the
    // self-loop branch. Shared by BuildBezierWire + UpdateBezierWire so the
    // committed wires and the in-flight drag preview trace the identical curve.
    private static (Point C1, Point C2) WireControlPoints(Point p1, Point p2)
    {
        if (Math.Abs(p2.X - p1.X) < WireSelfLoopEpsilon
            && Math.Abs(p2.Y - p1.Y) < WireSelfLoopEpsilon)
        {
            // Self-loop: arc upward and around the source node.
            return (new Point(p1.X + WireMinTangent, p1.Y - WireSelfLoopArc),
                    new Point(p2.X - WireMinTangent, p2.Y - WireSelfLoopArc));
        }
        double dx = WireControlDistance(p1, p2);
        return (new Point(p1.X + dx, p1.Y), new Point(p2.X - dx, p2.Y));
    }

    // Cubic bezier wire — control points pulled horizontally so wires sweep
    // out of output sockets and into input sockets the way every blueprint
    // editor (UE / Fusion / Unity Shader Graph) does it.
    private static Microsoft.UI.Xaml.Shapes.Path BuildBezierWire(Point p1, Point p2, Brush stroke, double thickness = 2.0)
    {
        var (c1, c2) = WireControlPoints(p1, p2);

        var fig = new PathFigure { StartPoint = p1 };
        fig.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = p2 });
        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data            = geom,
            Stroke          = stroke,
            StrokeThickness = thickness,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    // ─── pan + zoom ──────────────────────────────────────────────────────

    private void OnHostPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(HostGrid);
        if (pp.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            SetTransientHotkeyContext(VisualistHotkeyContext.WidgetGraphPanning);
            _panStartPointerWorld = pp.Position;
            HostGrid.CapturePointer(e.Pointer);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            e.Handled = true;
            return;
        }

        // Left-button on empty canvas starts a lasso. Inside-node
        // presses are handled by OnNodePointerPressed and never bubble here
        // because the node-view is in front of the empty canvas region.
        if (pp.Properties.IsLeftButtonPressed
            && e.OriginalSource is FrameworkElement fe
            && !IsInsideAnyNodeView(fe))
        {
            _isLassoing = true;
            // Lasso rectangle lives in WorldCanvas-coords so pan/zoom keeps
            // the rectangle in lockstep with the nodes underneath.
            var worldPos = TransformHostToWorld(pp.Position);
            _lassoStart = worldPos;
            _lassoRect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                // §2 gold marquee (SelectionBrush #FFD700), not brass Ember.
                Stroke          = ResolveBrush("SelectionBrush", 0xFF, 0xFF, 0xD7, 0x00),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill            = ResolveBrush("SelectionBrush", 0x22, 0xFF, 0xD7, 0x00),
                Width  = 0,
                Height = 0,
                IsHitTestVisible = false,
            };
            global::Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_lassoRect, worldPos.X);
            global::Microsoft.UI.Xaml.Controls.Canvas.SetTop (_lassoRect, worldPos.Y);
            LinkLayer.Children.Add(_lassoRect);
            HostGrid.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnHostPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            var pos = e.GetCurrentPoint(HostGrid).Position;
            double dx = pos.X - _panStartPointerWorld.X;
            double dy = pos.Y - _panStartPointerWorld.Y;
            WorldTransform.TranslateX += dx;
            WorldTransform.TranslateY += dy;
            _panStartPointerWorld = pos;
            RefreshGridDots();
            e.Handled = true;
            return;
        }

        if (_isLassoing && _lassoRect is not null)
        {
            var worldPos = TransformHostToWorld(e.GetCurrentPoint(HostGrid).Position);
            double x = Math.Min(_lassoStart.X, worldPos.X);
            double y = Math.Min(_lassoStart.Y, worldPos.Y);
            double w = Math.Abs(worldPos.X - _lassoStart.X);
            double h = Math.Abs(worldPos.Y - _lassoStart.Y);
            global::Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_lassoRect, x);
            global::Microsoft.UI.Xaml.Controls.Canvas.SetTop (_lassoRect, y);
            _lassoRect.Width  = w;
            _lassoRect.Height = h;
            e.Handled = true;
        }
    }

    private void OnHostPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ClearTransientHotkeyContext();
            HostGrid.ReleasePointerCapture(e.Pointer);
            ProtectedCursor = null;
            e.Handled = true;
            return;
        }

        if (_isLassoing && _lassoRect is not null)
        {
            // Resolve which nodes the rectangle intersects, then commit the
            // selection. Single-click degenerate (zero-area lasso) clears
            // selection — handled by IsInsideAnyNodeView=false in OnHostTapped
            // already, so the lasso branch here only commits non-trivial rects.
            double rx = global::Microsoft.UI.Xaml.Controls.Canvas.GetLeft(_lassoRect);
            double ry = global::Microsoft.UI.Xaml.Controls.Canvas.GetTop (_lassoRect);
            var lassoRect = new Rect(rx, ry, _lassoRect.Width, _lassoRect.Height);

            ClearSelection();
            if (lassoRect.Width >= 3 && lassoRect.Height >= 3)
            {
                foreach (var view in _nodeViews.Values)
                {
                    var nx = view.Node.Location.X;
                    var ny = view.Node.Location.Y;
                    double nw = Math.Max(view.ActualWidth, view.MinWidth);
                    double nh = Math.Max(view.ActualHeight, 60);
                    var nodeRect = new Rect(nx, ny, nw, nh);
                    if (RectsIntersect(lassoRect, nodeRect))
                        AddToSelection(view);
                }
            }

            LinkLayer.Children.Remove(_lassoRect);
            _lassoRect = null;
            _isLassoing = false;
            HostGrid.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private static bool RectsIntersect(Rect a, Rect b) =>
        a.X < b.X + b.Width && a.X + a.Width > b.X &&
        a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    // Map a HostGrid-local point into WorldCanvas-local coords by inverting
    // the WorldTransform (TranslateX/Y + ScaleX/Y). Reused by the lasso start
    // and the right-click spawn-at-cursor flow so a node spawned from a
    // right-click context menu lands exactly under the cursor regardless of
    // current pan/zoom.
    private Point TransformHostToWorld(Point hostPoint)
    {
        double s = WorldTransform.ScaleX <= 0 ? 1.0 : WorldTransform.ScaleX;
        return new Point(
            (hostPoint.X - WorldTransform.TranslateX) / s,
            (hostPoint.Y - WorldTransform.TranslateY) / s);
    }

    // Cursor-anchored zoom: re-position TranslateX/Y so the world point
    // under the pointer stays stationary across the scale change. This is the
    // standard graph-editor behaviour — without the anchor compensation,
    // zooming away from a node on the canvas edge sends it sliding off-screen.
    private void OnHostPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(HostGrid);
        int delta = pp.Properties.MouseWheelDelta;
        if (delta == 0) return;

        double oldScale = WorldTransform.ScaleX == 0 ? 1.0 : WorldTransform.ScaleX;
        double factor   = delta > 0 ? 1.1 : 1.0 / 1.1;
        double newScale = Math.Clamp(oldScale * factor, MinZoom, MaxZoom);
        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        // Map cursor → current world coords using the inverse transform: a
        // point at (px, py) in HostGrid corresponds to world coord
        // ((px - tx) / s, (py - ty) / s). After the scale change we want
        // s2 * world + t2 == (px, py) again, so t2 = (px, py) - s2 * world.
        Point cursor = pp.Position;
        double tx = WorldTransform.TranslateX;
        double ty = WorldTransform.TranslateY;
        double worldX = (cursor.X - tx) / oldScale;
        double worldY = (cursor.Y - ty) / oldScale;

        WorldTransform.ScaleX = newScale;
        WorldTransform.ScaleY = newScale;
        WorldTransform.TranslateX = cursor.X - newScale * worldX;
        WorldTransform.TranslateY = cursor.Y - newScale * worldY;
        RefreshGridDots();
        e.Handled = true;
    }

    // ─── selection ───────────────────────────────────────────────────────

    private void OnHostTapped(object sender, TappedRoutedEventArgs e)
    {
        // A tap that didn't bubble through a node means the user clicked
        // empty canvas — clear selection.
        if (e.OriginalSource is FrameworkElement fe && IsInsideAnyNodeView(fe))
            return;
        SelectNode(null);
        ClearLinkSelection();   // empty-canvas click also drops a wire highlight
    }

    private bool IsInsideAnyNodeView(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is WidgetGraphNodeView) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>
    /// True when a pointer press originated on an inline editable value pill — those
    /// carry <c>Tag="editpill"</c> on their <see cref="Border"/> container (see
    /// <c>WidgetGraphNodeView.BuildValuePill</c>), or a live edit <see cref="TextBox"/>.
    /// Walks up from the press's OriginalSource. OnNodePointerPressed consults this so
    /// a pill press is left to the pill's own Tapped→TextBox edit instead of capturing
    /// the pointer for a node-drag (which opens the edit box but blocks all typing).
    /// Per-pillar copy of the same helper in LayerCanvasView — Visualist owns its
    /// gesture helpers.
    /// </summary>
    private static bool IsEditAffordancePress(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is TextBox) return true;
            if (d is FrameworkElement fe && fe.Tag is string tag && tag == "editpill") return true;
            d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void SelectNode(WidgetGraphNodeView? view)
    {
        // Single-node select replaces the multi-select. Multi-select is
        // committed by OnHostPointerReleased's lasso branch via AddToSelection.
        ClearSelection();
        if (view is null) return;
        AddToSelection(view);
    }

    private void ClearSelection()
    {
        foreach (var v in _selectedNodes) v.IsSelected = false;
        _selectedNodes.Clear();
        if (_selectedNode is { } old) old.IsSelected = false;
        _selectedNode = null;
        NotifySelectedNodeChanged(null);
        RecomputeHotkeyContext();
    }

    private void AddToSelection(WidgetGraphNodeView view)
    {
        if (_selectedNodes.Contains(view)) return;
        view.IsSelected = true;
        _selectedNodes.Add(view);
        // Keep the legacy _selectedNode pointer in sync for single-target
        // operations (delete-via-key, duplicate) until they grow batch logic.
        _selectedNode = view;
        NotifySelectedNodeChanged(view.Node);
        RecomputeHotkeyContext();
    }

    // ─── per-node drag ───────────────────────────────────────────────────

    private void OnNodePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not WidgetGraphNodeView view) return;
        var pp = e.GetCurrentPoint(WorldCanvas);
        if (!pp.Properties.IsLeftButtonPressed) return;

        // A press on an inline value pill belongs to the pill's own Tapped→TextBox
        // edit. Capturing the pointer for a node-drag (below) opens the edit box but
        // then blocks every keystroke — the "node values can't be edited" bug, which
        // is also why an Image.Load keeps its empty default Path and the widget shows
        // "Display: load failed". Consume the press and leave the gesture to the pill.
        if (IsEditAffordancePress(e.OriginalSource))
        {
            e.Handled = true;
            return;
        }

        // Wire-drag branch — runs FIRST so a press on (or right at
        // the edge of) a pin starts a wire-drag instead of a node-drag. Mirrors
        // Architect's OnHostPointerPressed: PRIMARY source is the deterministic
        // note the pin's own pointer-pressed stamped via NotePinPress (this
        // ancestor handler runs after the pin descendant in the same bubbling
        // pass); FALLBACK is the geometry probe HitTestPinAt for the rare press
        // that reached the node body without a pin handler firing. Either way, if
        // a pin resolves we begin the identical wire-drag and bail before the
        // node-drag/selection path captures the pointer. Without this, a press a
        // few px off the 14×14 pin glyph fell through to the node-drag below —
        // the root of "pipe preview only spawns 1 in 10".
        var notedPin = ConsumePendingPinPress();
        var pinHit   = notedPin ?? HitTestPinAt(pp.Position);
        if (pinHit is { } ph)
        {
            // Drop any wire highlight first (a node and a wire are never both
            // "selected"), then start the drag from the resolved pin.
            if (_selectedLink is not null) ClearLinkSelection();
            BeginWireDrag(ph.Node, ph.Socket, ResolvePinElement(ph.Node, ph.Socket), e.Pointer, pp.Position);
            e.Handled = true;
            return;
        }

        // Pressing a node drops any wire highlight (a node and a wire are
        // never both "selected").
        if (_selectedLink is not null) ClearLinkSelection();

        // Shift-click extends the multi-select set without starting a drag
        // (the previous SelectNode unconditionally cleared
        // the prior multi-select). Group-drag is a separate follow-up;
        // shift-click is just for adjusting the selection set today.
        if (IsShiftDown())
        {
            if (_selectedNodes.Contains(view))
            {
                view.IsSelected = false;
                _selectedNodes.Remove(view);
                if (ReferenceEquals(_selectedNode, view))
                    _selectedNode = _selectedNodes.Count > 0 ? _selectedNodes[^1] : null;
                RecomputeHotkeyContext();
            }
            else
            {
                AddToSelection(view);
            }
            e.Handled = true;
            return;
        }

        // No shift. If the pressed node is already part of the multi-select
        // set, all selected nodes drag together. Otherwise the
        // press collapses the selection to just this node and starts a
        // single-node drag.
        if (_selectedNodes.Contains(view) && _selectedNodes.Count > 1)
        {
            _isGroupDragging = true;
            _groupDragOrigins.Clear();
            foreach (var n in _selectedNodes)
                _groupDragOrigins[n] = new Point(n.Node.Location.X, n.Node.Location.Y);
            // Keep the pressed node as the anchor for inspector / single-target ops.
            _selectedNode = view;
        }
        else
        {
            SelectNode(view);
            _isGroupDragging = false;
        }

        _dragNode               = view;
        _dragStartCanvasPoint   = pp.Position;
        _dragStartNodeLocation  = new Point(view.Node.Location.X, view.Node.Location.Y);
        _dragHasPushedUndo      = false;
        view.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    // Visualist owns its own copy of the keyboard-state probe — Architect's
    // LogicCanvasView.Marquee.cs has the equivalent helper.
    private static bool IsShiftDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    // VirtualKey.Menu == Alt in Win32-speak. Alt is unused elsewhere on the
    // canvas (Shift = multi-select extend, Ctrl = clipboard / zoom), so it's
    // the safe modifier for hold-to-snap.
    private static bool IsAltDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    /// <summary>
    /// True when grid snap should apply right now.
    ///
    /// Semantics: <see cref="SnapEnabled"/> is the persistent toggle; Alt held
    /// during a drag is a MOMENTARY DISABLE (matches the CHANGELOG 0.9.10 spec
    /// "Alt-hold momentary disable" and the Photoshop / Fusion convention
    /// where the modifier suppresses the active constraint rather than adding
    /// one). The operator was previously `SnapEnabled || IsAltDown()`, which
    /// inverted the intent — Alt would force snap on while the toggle was off,
    /// the exact opposite of what the docs promised.
    /// </summary>
    private bool ShouldSnap() => SnapEnabled && !IsAltDown();

    private static int SnapToGrid(double v, int step = SnapStepPx)
        => (int)Math.Round(v / step) * step;
    private static int SnapToGrid(int v, int step = SnapStepPx)
        => (int)Math.Round(v / (double)step) * step;

    // Walk up the visual tree until we hit the Visualist pillar's
    // MainView. Used by the Save / SaveAs / Undo / Redo chord handlers to
    // route into the existing public surface (SaveLayer / SaveLayerAs / Undo /
    // Redo) without adding a new public event on this canvas. The walk is
    // O(tree-depth) — cheap enough to call per chord, no caching needed.
    private Phoenix.Controls.Visualist.WinUI.MainView? FindPillarMainView()
    {
        DependencyObject? cur = this;
        while (cur is not null)
        {
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
            if (cur is Phoenix.Controls.Visualist.WinUI.MainView mv) return mv;
        }
        return null;
    }

    // CheckBox event handler. Routes through the IsSnapEnabled
    // setter so the System-log line and (eventually) the dotted-grid backdrop
    // fire from one place regardless of which surface the user touched.
    private void OnSnapToggleChanged(object sender, RoutedEventArgs e)
    {
        // Suppressed while any sync pass is in flight (depth > 0).
        if (_suppressSnapToggleDepth > 0) return;
        IsSnapEnabled = SnapToggle.IsChecked == true;
    }

    // Push SnapEnabled into the CheckBox visual without re-firing
    // the Checked/Unchecked handler. Called by the IsSnapEnabled setter and
    // by the RMB-flyout toggle path so both stay in sync.
    private void SyncSnapToggleVisual()
    {
        try
        {
            // Increment/decrement the nesting depth instead of a flat
            // bool so a nested sync (e.g. RMB-flyout toggle inside a checkbox-driven
            // sync) doesn't clear suppression for the outer pass on its own finally.
            _suppressSnapToggleDepth++;
            SnapToggle.IsChecked = SnapEnabled;
        }
        finally
        {
            _suppressSnapToggleDepth--;
        }
    }

    // Push the engine-side PreviewSnapshot map into each node view.
    // Called after Rebuild() and after every graph-mutating commit
    // (paste / wire add / wire delete / node delete / attribute commit
    // path via MarkDirty). NodeEvaluator.EvaluatePreviews is a sync graph
    // walk; cheap enough to call once per mutation but DON'T spin it on
    // every pointer-move tick.
    private void RefreshPreviews()
    {
        try
        {
            if (_trigger?.Graph is not { } graph)
            {
                foreach (var v in _nodeViews.Values) v.SetPreview(null);
                return;
            }
            var snapshots = Phoenix.Controls.Visualist.Core.NodeEvaluator.EvaluatePreviews(graph);
            foreach (var kvp in _nodeViews)
            {
                snapshots.TryGetValue(kvp.Key, out var snap);
                kvp.Value.SetPreview(snap);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "RefreshPreviews", ex);
        }
    }

    // Commit an inline value-pill edit. Mirrors every other attribute
    // mutation on this canvas (Browse Media / Edit Shape / manipulator): snapshot
    // for undo, write the raw value, mark dirty, rebuild (Rebuild's LayoutUpdated
    // re-runs RefreshPreviews so a changed value re-renders the node thumbnail).
    private void OnNodeAttributeCommitted(Node node, string attrKey, string newValue)
    {
        try
        {
            if (node is null || string.IsNullOrEmpty(attrKey)) return;
            node.Attributes ??= new Dictionary<string, string>();
            string original = node.Attributes.TryGetValue(attrKey, out var v) ? v : "";
            bool armed = _armedParams.Contains(ParamPath(node, attrKey));
            // When armed the playhead may sit on an existing keyframe even if the
            // raw value text didn't change vs the static attribute, so don't
            // short-circuit on equality — let the record path run.
            if (string.Equals(original, newValue, StringComparison.Ordinal) && !armed) return;
            _vm?.Document?.PushUndo();
            node.Attributes[attrKey] = newValue;
            // Record-on-change: an armed pill drops/updates a keyframe at
            // the playhead with the committed value (the static attribute stays
            // as the at-rest fallback).
            if (armed) RecordKeyframeAtPlayhead(node, attrKey, newValue);
            _vm?.Document?.MarkDirty();
            Rebuild();
            // Mirror this node-BODY pill edit onto the right-pane
            // Inspector (which listens to NodeBodyCommitted), so editing a value on
            // the node and editing it in the Inspector stay in sync both ways. Uses
            // the dedicated event (not NodeParamCommitted) so an Inspector edit can't
            // round-trip back and rebuild the form the user is dragging.
            _vm?.NotifyNodeBodyCommitted(node);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "OnNodeAttributeCommitted", ex);
        }
    }

    // ─── keyframe arm + record + seek helpers ──────────────────────

    private string ParamPath(Node node, string attrKey)
        => AnimatedPinRegistry.MakeParameterPath(node, attrKey);

    // A pill carries the inline arm/seek cluster when its socket is an
    // animatable INPUT scalar whose attribute key matches the socket (the
    // single-component case — vector pins keyframe per-component via the
    // existing right-click Animate gesture, not the inline cluster).
    private static bool IsArmSeekPill(Node node, Socket socket, string attrKey)
    {
        if (socket is null || socket.Type != SocketType.Input) return false;
        if (!AnimatedPinRegistry.IsAnimatablePinType(socket.DataType)) return false;
        // Vectors store per-component attributes (XYZ/W); the inline pill is the
        // scalar case where the attr key equals the socket name.
        if (socket.DataType is SocketDataType.Vector2 or SocketDataType.Vector3 or SocketDataType.Vector4)
            return false;
        return string.Equals(attrKey, socket.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void ToggleParamArm(Node node, string attrKey)
    {
        string path = ParamPath(node, attrKey);
        bool nowArmed;
        if (_armedParams.Contains(path)) { _armedParams.Remove(path); nowArmed = false; }
        else                             { _armedParams.Add(path);    nowArmed = true; }

        // Arming a not-yet-animated parameter seeds a keyframe at the playhead
        // with the current literal — mirrors DaVinci's stopwatch, which drops a
        // key when you enable it, and makes the record loop immediately useful.
        if (nowArmed && _trigger?.Timeline is { } tl)
        {
            bool hasAny = tl.Keyframes.Any(k =>
                k != null && string.Equals(k.ParameterPath, path, StringComparison.Ordinal));
            if (!hasAny)
            {
                double val = AnimatedPinRegistry.ReadComponentLiteral(node, attrKey);
                double t   = Math.Max(0, _vm?.PlayheadMs ?? 0);
                _vm?.Document?.PushUndo();
                if (tl.DurationMs < t) tl.DurationMs = t;
                tl.Keyframes.Add(new Keyframe
                {
                    ParameterPath = path,
                    TimeMs        = t,
                    Value         = System.Text.Json.JsonSerializer.SerializeToElement(val),
                    Curve         = KeyframeCurve.Linear,
                });
                _vm?.Document?.MarkDirty();
                _vm?.NotifyActiveTriggerChanged();
            }
        }
        Rebuild();   // refresh the ◇/◆ arm visual on every pill
    }

    private void RecordKeyframeAtPlayhead(Node node, string attrKey, string rawValue)
    {
        if (_trigger?.Timeline is not { } tl) return;
        string path = ParamPath(node, attrKey);
        double t    = Math.Max(0, _vm?.PlayheadMs ?? 0);

        System.Text.Json.JsonElement val =
            double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double num)
            && !double.IsNaN(num) && !double.IsInfinity(num)
                ? System.Text.Json.JsonSerializer.SerializeToElement(num)
                : System.Text.Json.JsonSerializer.SerializeToElement(rawValue);

        if (tl.DurationMs < t) tl.DurationMs = t;

        // Upsert: a keyframe within half a millisecond of the playhead on this
        // parameter is updated in place; otherwise a new one is inserted.
        var existing = tl.Keyframes.FirstOrDefault(k =>
            k != null && string.Equals(k.ParameterPath, path, StringComparison.Ordinal)
            && Math.Abs(k.TimeMs - t) < 0.5);
        if (existing is not null) existing.Value = val;
        else tl.Keyframes.Add(new Keyframe
        {
            ParameterPath = path,
            TimeMs        = t,
            Value         = val,
            Curve         = KeyframeCurve.Linear,
        });
        _vm?.NotifyActiveTriggerChanged();
    }

    private void SeekParam(Node node, string attrKey, int dir)
    {
        if (_vm is null || _trigger?.Timeline is not { } tl) return;
        string path = ParamPath(node, attrKey);
        double cur  = _vm.PlayheadMs;

        double best = double.NaN;
        foreach (var k in tl.Keyframes)
        {
            if (k is null || !string.Equals(k.ParameterPath, path, StringComparison.Ordinal)) continue;
            double t = k.TimeMs;
            if (double.IsNaN(t) || double.IsInfinity(t)) continue;
            if (dir < 0)
            {
                if (t < cur - 0.5 && (double.IsNaN(best) || t > best)) best = t;
            }
            else
            {
                if (t > cur + 0.5 && (double.IsNaN(best) || t < best)) best = t;
            }
        }
        if (!double.IsNaN(best)) _vm.PlayheadMs = best;
    }

    // ─── drag a media tile from the docked panel onto the graph ────
    //
    // Restores the pre-WinUI "drag a tile, see it in OBS" gesture: dropping a
    // MediaLibraryPanel tile spawns the matching loader node (Image/Video/Audio.Load)
    // at the cursor, pre-fills its Path, and auto-wires image/video into the
    // Display sink so the preview lights up immediately. Audio has no Image
    // output, so it spawns unwired. Single PushUndo covers the spawn + wire.
    private void OnMediaDragOver(object sender, DragEventArgs e)
    {
        // Accept the drop when EITHER the custom media format OR the
        // StandardDataFormats.Text payload is present. The custom DataPackage
        // format (e.Data.SetData/GetDataAsync) does NOT reliably survive the
        // round-trip across controls in WinUI 3, so DragOver was rejecting the
        // drop outright (-> nothing happened on drop). StandardDataFormats.Text
        // round-trips reliably; the MediaLibraryPanel source mirrors the same
        // "{kind}|{relativePath}" payload into the Text slot, so we accept on
        // either signal here and resolve the payload in OnMediaDrop.
        if (_trigger?.Graph is not null
            && (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
                || e.DataView.Contains(Phoenix.Controls.Visualist.WinUI.Controls.MediaLibraryPanel.MediaDragFormat)))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption          = "Add media node";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = false;
            }
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void OnMediaDrop(object sender, DragEventArgs e)
    {
        if (_trigger?.Graph is not { } graph) return;
        string fmt = Phoenix.Controls.Visualist.WinUI.Controls.MediaLibraryPanel.MediaDragFormat;
        var deferral = e.GetDeferral();
        try
        {
            // Resolve the payload from StandardDataFormats.Text FIRST
            // (the reliable round-trip channel), falling back to the custom
            // DataPackage format only if Text is absent. Both carry the same
            // "{kind}|{relativePath}" contract. The custom format alone was the
            // root cause of the silent no-op: it frequently fails to survive
            // GetDataAsync across controls in WinUI 3.
            string raw = "";
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                raw = await e.DataView.GetTextAsync() ?? "";
            }
            // Fall back to the custom format when Text is absent, OR when the Text
            // slot is present but doesn't carry the "{kind}|{rel}" contract (no '|').
            // The latter makes the drop robust even if the source mirrors only the
            // bare relative path into Text — the custom format still carries the
            // kind-prefixed payload.
            if ((string.IsNullOrEmpty(raw) || raw.IndexOf('|') < 0) && e.DataView.Contains(fmt))
            {
                string custom = await e.DataView.GetDataAsync(fmt) as string ?? "";
                if (custom.IndexOf('|') >= 0) raw = custom;
            }
            if (string.IsNullOrEmpty(raw)) return;
            int bar = raw.IndexOf('|');
            if (bar < 0) return;
            string kind = raw.Substring(0, bar);
            string rel  = raw.Substring(bar + 1);
            string title = kind switch
            {
                "image" => "Image.Load",
                "video" => "Video.Load",
                "audio" => "Audio.Load",
                _       => "",
            };
            if (title.Length == 0 || string.IsNullOrEmpty(rel)) return;

            var worldPt = TransformHostToWorld(e.GetPosition(HostGrid));
            int x = (int)Math.Round(worldPt.X);
            int y = (int)Math.Round(worldPt.Y);

            if (_vm?.Document is { } doc) doc.PushUndo();

            // Belt-and-suspenders: ensure the node catalog is registered before we
            // instantiate. The pillar-startup RegisterAll (MainView ctor) normally
            // covers this, but if a drop somehow lands first, an empty registry
            // would throw "Unknown node template" and the drop would silently fail.
            if (WidgetNodeRegistry.Templates.Count == 0) NodeTemplates.RegisterAll();

            var node = WidgetNodeRegistry.Instantiate(title, new System.Drawing.Point(x, y));
            // Path attribute is JSON-quoted (the runtime parser treats it as a
            // string literal) — matches the node-picker / drag-drop convention.
            node.Attributes["Path"] = System.Text.Json.JsonSerializer.Serialize(rel);
            graph.Nodes.Add(node);

            // Auto-wire image/video → the Display sink (single fan-in: replace
            // any prior inbound link on the sink). Audio.Load has no Image output.
            if (kind is "image" or "video")
            {
                var sink = graph.Nodes.Find(n => DisplaySinkNode.Is(n));
                if (sink is not null)
                {
                    var srcOut = node.Sockets.Find(s => s.Type == SocketType.Output && s.Name == "Image");
                    var sinkIn = sink.Sockets.Find(s => s.Type == SocketType.Input);
                    if (srcOut is not null && sinkIn is not null)
                    {
                        graph.Links.RemoveAll(l => l.ToNodeId == sink.Id && l.ToSocketId == sinkIn.Id);
                        graph.Links.Add(new Link
                        {
                            FromNodeId   = node.Id,
                            FromSocketId = srcOut.Id,
                            ToNodeId     = sink.Id,
                            ToSocketId   = sinkIn.Id,
                        });
                    }
                }
            }

            if (_vm?.Document is { } d2) d2.MarkDirty();
            Rebuild();

            // Success breadcrumb so a future no-op is diagnosable from
            // the System log (did the drop reach here at all, or fail upstream?).
            GlobalLogger.Log($"Visualist: spawned {title} from media drop", "WidgetGraphCanvas", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "OnMediaDrop", ex);
        }
        finally { deferral.Complete(); }
    }

    // ─── node-selection → preview manipulator wire ──────────────────
    //
    // The pre-WinUI WidgetGraphCanvas exposed OnSelectedNodeChanged, which
    // WidgetEditorForm forwarded to the preview so selecting a spatial node lit
    // its manipulator handles. The WinUI canvas dropped the event (selection was
    // private), so SetActiveNode (SET_MANIPULATOR) had zero callers. Restore the
    // event; WidgetEditorView subscribes and forwards to the embedded/popout
    // preview. Fired only on a genuine primary-selection identity change.
    public event Action<Node?>? OnSelectedNodeChanged;
    private Node? _lastNotifiedSelectedNode;

    private void NotifySelectedNodeChanged(Node? node)
    {
        if (ReferenceEquals(_lastNotifiedSelectedNode, node)) return;
        _lastNotifiedSelectedNode = node;
        try { OnSelectedNodeChanged?.Invoke(node); }
        catch (Exception ex) { GlobalLogger.Error("WidgetGraphCanvas", "OnSelectedNodeChanged", ex); }
    }

    private void OnNodePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragNode is null || sender is not WidgetGraphNodeView view) return;
        if (!ReferenceEquals(_dragNode, view)) return;

        var pp = e.GetCurrentPoint(WorldCanvas);
        double dx = pp.Position.X - _dragStartCanvasPoint.X;
        double dy = pp.Position.Y - _dragStartCanvasPoint.Y;

        double total = Math.Abs(dx) + Math.Abs(dy);
        if (total < DragDeadZonePx && !_dragHasPushedUndo) return;

        if (!_dragHasPushedUndo && _vm?.Document is { } doc)
        {
            // First real motion → snapshot for undo. MarkDirty fires on
            // release rather than per-pixel so the dirty pip doesn't strobe
            // during the drag.
            doc.PushUndo();
            _dragHasPushedUndo = true;
        }

        bool snap = ShouldSnap();

        if (_isGroupDragging)
        {
            // Apply the same delta to every snapshotted node — preserves
            // relative formation. No bounds clamp here because the widget
            // graph canvas is unbounded (pan/zoom view); negative
            // coordinates are valid and Architect's logic canvas does the
            // same on its group-drag.
            //
            // When snap is active we snap the *delta* (not each
            // resulting position). Snapping the per-node positions would
            // shear the formation: two nodes 10px apart on the X axis would
            // collapse onto the same column the moment the cursor crossed a
            // grid line. Snapping the delta preserves their offsets and just
            // moves the whole group in 20px hops.
            int idx = snap ? SnapToGrid(dx) : (int)Math.Round(dx);
            int idy = snap ? SnapToGrid(dy) : (int)Math.Round(dy);
            foreach (var (n, origin) in _groupDragOrigins)
            {
                int nx = (int)origin.X + idx;
                int ny = (int)origin.Y + idy;
                n.Node.Location = new System.Drawing.Point(nx, ny);
                global::Microsoft.UI.Xaml.Controls.Canvas.SetLeft(n, nx);
                global::Microsoft.UI.Xaml.Controls.Canvas.SetTop (n, ny);
            }
        }
        else
        {
            double newX = _dragStartNodeLocation.X + dx;
            double newY = _dragStartNodeLocation.Y + dy;

            // Round to integer so saved .phxlayer doesn't accumulate sub-pixel
            // drift. Snap-mode rounds to the 20px grid per axis instead.
            int finalX = snap ? SnapToGrid(newX) : (int)Math.Round(newX);
            int finalY = snap ? SnapToGrid(newY) : (int)Math.Round(newY);
            view.Node.Location = new System.Drawing.Point(finalX, finalY);
            global::Microsoft.UI.Xaml.Controls.Canvas.SetLeft(view, view.Node.Location.X);
            global::Microsoft.UI.Xaml.Controls.Canvas.SetTop (view, view.Node.Location.Y);
        }

        // Re-route wires that touch the moved node(s) so they trail in real
        // time. Frame-coalesced via QueueRedrawLinks; the next
        // CompositionTarget.Rendering tick runs RerouteDraggedNodeWires exactly
        // once per displayed frame, mutating only the incident wires' Path.Data
        // in place. Previously this was a direct RedrawLinks() call per
        // pointer-move event, which fired the full wire pass dozens of times
        // per frame on a high-Hz mouse.
        QueueRedrawLinks();

        e.Handled = true;
    }

    private void OnNodePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragNode is null || sender is not WidgetGraphNodeView view) return;
        if (!ReferenceEquals(_dragNode, view)) return;

        view.ReleasePointerCapture(e.Pointer);

        if (_dragHasPushedUndo && _vm?.Document is { } doc)
            doc.MarkDirty();

        _dragNode = null;
        _dragHasPushedUndo = false;
        _isGroupDragging = false;
        _groupDragOrigins.Clear();
        e.Handled = true;
    }

    // ─── pin-press → wire-drop ───────────────────────────────────────────

    private void HookPinHandlers(WidgetGraphNodeView nodeView)
    {
        foreach (var (socketId, pin) in nodeView.PinElements)
        {
            // Capture the socket via the pin Tag (set in WidgetGraphNodeView.BuildPinPath
            // on the outer container). Each pin is a 14×14 Grid hosting the glyph
            // Path + hover-glow Ellipse; pointer events bubble up through the node
            // view, so we attach to the pin directly to keep the node-drag and
            // wire-drag paths from competing.
            pin.PointerPressed  += OnPinPointerPressed;
            pin.PointerEntered  += OnPinPointerEntered;
            pin.PointerExited   += OnPinPointerExited;
            // Move + Released are routed via WorldCanvas pointer-capture
            // (set up in OnPinPointerPressed) so we don't lose tracking when
            // the pointer leaves the small pin rectangle mid-drag.

            // Right-click on a pin opens a context flyout. Two distinct
            // affordances share OnPinRightTapped, gated by socket side:
            //   • INPUT animatable pins → Animate / Remove-animation menu.
            //     Non-animatable inputs (Flow / String / Bool /
            //     Image / Color …) get no Animate item.
            //   • OUTPUT pins → compatible-socket spawn submenu
            //     (baseline parity) — spawn a node whose input matches this
            //     output's type, pre-grouped by category.
            // We attach RightTapped ONLY where the handler will do
            // something (the predicate below mirrors OnPinRightTapped's own
            // branch gate). Previously RightTapped was attached to ANY
            // animatable pin regardless of side, leaving dead subscriptions on
            // animatable OUTPUT pins the input-only handler returned early on.
            if (pin.Tag is Socket socket && PinWantsRightTapMenu(socket))
            {
                pin.RightTapped += OnPinRightTapped;
            }
        }
    }

    // symmetric teardown for HookPinHandlers. Without
    // it, Rebuild() dropped its old node views with the pin pointer / right-tap
    // handlers still subscribed — a per-rebuild handler leak (graph load / add /
    // delete / paste all rebuild). Mirrors the `+=` set above with `-=`, including
    // the same RightTapped gate so we only detach exactly what we hooked.
    private void UnhookPinHandlers(WidgetGraphNodeView nodeView)
    {
        foreach (var (socketId, pin) in nodeView.PinElements)
        {
            pin.PointerPressed  -= OnPinPointerPressed;
            pin.PointerEntered  -= OnPinPointerEntered;
            pin.PointerExited   -= OnPinPointerExited;
            if (pin.Tag is Socket socket && PinWantsRightTapMenu(socket))
            {
                pin.RightTapped -= OnPinRightTapped;
            }
        }
    }

    // Single source of truth for which sockets get a RightTapped
    // subscription, so Hook / Unhook stay symmetric and OnPinRightTapped never
    // fires on a pin that has no menu to show. OUTPUT pins always carry the
    // compatible-socket spawn submenu; INPUT pins carry the Animate menu only
    // when their type is keyframable.
    private static bool PinWantsRightTapMenu(Socket socket)
        => socket.Type == SocketType.Output
           || (socket.Type == SocketType.Input && AnimatedPinRegistry.IsAnimatablePinType(socket.DataType));

    private void OnPinRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement pin) return;
        if (pin.Tag is not Socket socket) return;
        Node? owner = ResolveOwnerNode(pin);
        if (owner is null) return;
        if (_trigger is null || _vm is null) return;

        // OUTPUT pins → compatible-socket spawn submenu (baseline
        // parity). The Animate gesture below is INPUT-only (you keyframe a node's
        // input parameter, not its computed output), so the two affordances split
        // cleanly by socket side. HookPinHandlers only attaches RightTapped to the
        // sides that have a menu (see PinWantsRightTapMenu), so this branch is the
        // sole gate — no dead subscriptions remain on the other side.
        if (socket.Type == SocketType.Output)
        {
            ShowCompatibleSpawnMenu(pin, owner, socket);
            e.Handled = true;
            return;
        }

        bool isAnimated = AnimatedPinRegistry.IsPinAnimated(_trigger.Timeline, owner, socket);
        var flyout = new MenuFlyout();

        if (isAnimated)
        {
            var remove = new MenuFlyoutItem { Text = $"Remove animation on \"{socket.Name}\"" };
            remove.Click += (_, _) => RemovePinAnimation(owner, socket);
            flyout.Items.Add(remove);
        }
        else
        {
            var animate = new MenuFlyoutItem { Text = $"Animate \"{socket.Name}\"" };
            animate.Click += (_, _) => SeedPinAnimation(owner, socket);
            flyout.Items.Add(animate);
        }

        flyout.ShowAt(pin);
        e.Handled = true;
    }

    // Compatible-socket spawn menu (baseline parity). Right-clicking
    // an OUTPUT pin offers to spawn a node whose INPUT type is compatible with this
    // output, grouped by category, then auto-wires the new node's matching input to
    // the source pin. Mirrors BuildPaletteSubmenu's category grouping but pre-filters
    // templates to compatible inputs so the author only sees nodes that will connect.
    // Uses the same Visualist-local DataType compat table as the wire-drop path
    // (AreDataTypesCompatible) — Architect's NodeRegistry.AreCompatible is pillar-
    // isolated and lacks the visual-only DataTypes (Image / Color / Scalar / Vector*).
    private void ShowCompatibleSpawnMenu(FrameworkElement pin, Node sourceNode, Socket sourceOutput)
    {
        if (_trigger?.Graph is null) return;

        // Spawn just to the right of the pin so the new node lands near the wire's
        // natural destination. Resolve from the pin's visual position (per verifier
        // note: not a world-coord parameter).
        Point spawnWorld;
        if (_nodeViews.TryGetValue(sourceNode.Id, out var srcView)
            && TryGetPinCenter(srcView, sourceOutput.Id, out var center))
        {
            spawnWorld = new Point(center.X + 160, center.Y);
        }
        else
        {
            // Fallback: drop near the source node's stored location.
            spawnWorld = new Point(sourceNode.Location.X + 220, sourceNode.Location.Y);
        }

        var flyout = new MenuFlyout();
        var spawn  = new MenuFlyoutSubItem { Text = $"Spawn compatible node for \"{sourceOutput.Name}\"" };

        var outType = sourceOutput.DataType;
        var byCategory = WidgetNodeRegistry.Templates.Values
            // Keep only templates that expose at least one input the source output
            // can drive (output→input direction matches the wire-drop semantics).
            .Where(t => t.Inputs.Any(inp => AreDataTypesCompatible(outType, inp.DataType)))
            .GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        bool any = false;
        foreach (var grp in byCategory)
        {
            var sub = new MenuFlyoutSubItem { Text = grp.Key };
            foreach (var tmpl in grp.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
            {
                string captureTitle = tmpl.Title;
                var item = new MenuFlyoutItem { Text = WidgetNodeRegistry.GetDisplayTitle(captureTitle) };
                item.Click += (_, _) => SpawnCompatibleNode(captureTitle, spawnWorld, sourceNode, sourceOutput);
                sub.Items.Add(item);
                any = true;
            }
            if (sub.Items.Count > 0) spawn.Items.Add(sub);
        }

        if (!any)
        {
            // No compatible template — show a disabled hint rather than an empty
            // submenu (no modal).
            spawn.Items.Add(new MenuFlyoutItem
            {
                Text       = "(no compatible nodes)",
                IsEnabled  = false,
            });
        }

        flyout.Items.Add(spawn);
        flyout.ShowAt(pin);
    }

    // Spawn a node by title at the world point, then auto-wire the
    // source output into the new node's first compatible input. Reuses SpawnNodeAt's
    // guards (Audio.Play singleton, snap, undo) by spawning through it, then adds the
    // wire as part of the same undo step the spawn pushed.
    private void SpawnCompatibleNode(string title, Point worldPoint, Node sourceNode, Socket sourceOutput)
    {
        if (_trigger?.Graph is not { } graph) return;

        // SpawnNodeAt pushes undo + marks dirty + rebuilds. Capture the link count
        // afterward so we can tell whether a node was actually added (the singleton
        // guard may have rejected the spawn).
        int beforeCount = graph.Nodes.Count;
        SpawnNodeAt(title, worldPoint);
        if (graph.Nodes.Count <= beforeCount) return;   // spawn was gated/rejected

        // The freshly-spawned node is the last one added by SpawnNodeAt.
        var newNode = graph.Nodes[graph.Nodes.Count - 1];
        var inSocket = newNode.Sockets.Find(s =>
            s.Type == SocketType.Input && AreDataTypesCompatible(sourceOutput.DataType, s.DataType));
        if (inSocket is null) return;   // nothing to wire (shouldn't happen — we pre-filtered)

        // Single fan-in: replace any prior inbound wire on the chosen input.
        graph.Links.RemoveAll(l => l.ToNodeId == newNode.Id && l.ToSocketId == inSocket.Id);
        graph.Links.Add(new Link
        {
            FromNodeId   = sourceNode.Id,
            FromSocketId = sourceOutput.Id,
            ToNodeId     = newNode.Id,
            ToSocketId   = inSocket.Id,
        });

        if (_vm?.Document is { } d) d.MarkDirty();
        Rebuild();
        GlobalLogger.Log(
            $"Visualist: spawned {title} from '{sourceOutput.Name}' and auto-wired to '{inSocket.Name}'.",
            "WidgetGraphCanvas", LogLevel.System);
    }

    private void SeedPinAnimation(Node owner, Socket socket)
    {
        if (_trigger is null || _vm is null) return;
        try
        {
            _vm.Document?.PushUndo();
            int added = AnimatedPinRegistry.SeedAnimation(
                _trigger.Timeline, owner, socket, _vm.PlayheadMs);
            if (added > 0)
            {
                _vm.Document?.MarkDirty();
                _vm.NotifyActiveTriggerChanged();
                GlobalLogger.Log(
                    $"Animated pin '{socket.Name}' on '{owner.Title}' — {added} keyframes seeded at {_vm.PlayheadMs:0}ms.",
                    "WidgetGraphCanvas", LogLevel.System);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", $"SeedAnimation '{socket.Name}'", ex);
        }
    }

    private void RemovePinAnimation(Node owner, Socket socket)
    {
        if (_trigger is null || _vm is null) return;
        try
        {
            _vm.Document?.PushUndo();
            int removed = AnimatedPinRegistry.RemoveAnimation(_trigger.Timeline, owner, socket);
            if (removed > 0)
            {
                _vm.Document?.MarkDirty();
                _vm.NotifyActiveTriggerChanged();
                GlobalLogger.Log(
                    $"Removed animation on pin '{socket.Name}' on '{owner.Title}' — {removed} keyframes dropped.",
                    "WidgetGraphCanvas", LogLevel.System);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", $"RemoveAnimation '{socket.Name}'", ex);
        }
    }

    private void OnPinPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement pin) return;
        if (pin.Tag is not Socket socket) return;
        var pp = e.GetCurrentPoint(WorldCanvas);
        if (!pp.Properties.IsLeftButtonPressed) return;

        Node? sourceNode = ResolveOwnerNode(pin);
        if (sourceNode is null) return;

        // This handler IS the press landing precisely on the pin —
        // start the drag directly (BeginWireDrag clears any pending stamp the
        // node-view's hand-off handler may have set on this same press, so it
        // can't leak past this Handled press). The deterministic NotePinPress /
        // ConsumePendingPinPress path in OnNodePointerPressed only matters for the
        // presses that DON'T reach this handler (routed to the node body a few px
        // off the glyph) — the case that previously started a node-drag.
        BeginWireDrag(sourceNode, socket, pin, e.Pointer, pp.Position);
        e.Handled = true;
    }

    /// <summary>
    /// Start a wire-drag from <paramref name="socket"/> on
    /// <paramref name="sourceNode"/>. Factored out of OnPinPointerPressed so the
    /// deterministic hand-off path (OnNodePointerPressed → ConsumePendingPinPress /
    /// HitTestPinAt) can begin the identical drag when the press routed to the
    /// node body instead of the pin glyph. Captures the pointer on WorldCanvas so
    /// the move/release tracking survives the cursor leaving the small pin.
    /// </summary>
    private void BeginWireDrag(Node sourceNode, Socket socket, FrameworkElement? pin, Pointer pointer, Point worldPos)
    {
        // Once a drag has actually begun, drop any pending pin
        // stamp so it can't leak into a later, unrelated node-body press. This
        // matters because the direct pin path (OnPinPointerPressed) sets
        // e.Handled=true, which stops the bubble before OnNodePointerPressed can
        // consume the stamp — clearing it here closes that window.
        _pendingPinPress     = null;
        _pendingPinPressNode = null;

        // Fresh drag → fresh pin-centre snapshot (built lazily on the
        // first hover hit-test; see HitTestPinAt).
        _pinCenterCache = null;

        // A drag almost always starts on a hovered pin. Drop the incident-wire
        // hover emphasis now — the pointer capture moves to WorldCanvas, so the
        // pin's PointerExited may never fire, and the drag preview owns wire
        // coloring for the whole drag.
        ClearPinHoverHighlight();

        _wireSourceNode    = sourceNode;
        _wireSourceSocket  = socket;
        _wireSourcePin     = pin;
        // Remember which end we grabbed so the move/release paths can
        // reverse producer/consumer roles for an input-initiated (reverse) drag.
        _wireSourceIsInput = socket.Type == SocketType.Input;
        SetTransientHotkeyContext(VisualistHotkeyContext.WidgetGraphDraggingWire);

        // Render an in-flight bezier from the pin centre to the cursor.
        if (_nodeViews.TryGetValue(sourceNode.Id, out var fromView)
            && TryGetPinCenter(fromView, socket.Id, out var src))
        {
            _tempWirePath = BuildBezierWire(src, worldPos,
                ResolveBrush("EmberPrimaryBrush", 0xCC, 0xE5, 0xA2, 0x4E));
            LinkLayer.Children.Add(_tempWirePath);
        }

        WorldCanvas.PointerMoved       += OnWorldPointerMovedForWire;
        WorldCanvas.PointerReleased    += OnWorldPointerReleasedForWire;
        WorldCanvas.PointerCanceled    += OnWorldPointerCancelledForWire;
        WorldCanvas.PointerCaptureLost += OnWorldPointerCancelledForWire;
        WorldCanvas.CapturePointer(pointer);
    }

    /// <summary>
    /// Wire-drag aborted by lost capture / cancelled pointer. Strip the
    /// preview path, drop the source state, and detach the per-drag handlers
    /// — DO NOT commit a wire (the cursor may not be over a real target).
    /// </summary>
    private void OnWorldPointerCancelledForWire(object sender, PointerRoutedEventArgs e)
    {
        WorldCanvas.PointerMoved       -= OnWorldPointerMovedForWire;
        WorldCanvas.PointerReleased    -= OnWorldPointerReleasedForWire;
        WorldCanvas.PointerCanceled    -= OnWorldPointerCancelledForWire;
        WorldCanvas.PointerCaptureLost -= OnWorldPointerCancelledForWire;
        try { WorldCanvas.ReleasePointerCapture(e.Pointer); } catch { /* already released */ }

        if (_tempWirePath is not null) LinkLayer.Children.Remove(_tempWirePath);
        _tempWirePath      = null;
        _wireSourceNode    = null;
        _wireSourceSocket  = null;
        _wireSourcePin     = null;
        _wireSourceIsInput = false;
        _pinCenterCache    = null;
        ClearTransientHotkeyContext();
    }

    private void OnWorldPointerMovedForWire(object sender, PointerRoutedEventArgs e)
    {
        if (_wireSourceNode is null || _wireSourceSocket is null) return;
        var pp = e.GetCurrentPoint(WorldCanvas);

        if (!_nodeViews.TryGetValue(_wireSourceNode.Id, out var fromView)) return;
        if (!TryGetPinCenter(fromView, _wireSourceSocket.Id, out var src)) return;

        // Hover hit-test — does the cursor sit over a pin of the OPPOSITE side
        // from the drag source? If yes, re-colour the temp wire green/red based
        // on type-compat. This is the "preview" the parity plan calls out —
        // author sees ahead of release whether the drop will succeed.
        //
        // Reverse-drag aware: a forward drag (source = output) looks
        // for an INPUT drop target; a reverse drag (source = input) looks for an
        // OUTPUT drop target. In the reverse case the compat check is re-issued
        // with swapped roles (hovered output as producer, source input as
        // consumer) so Input→Output resolves exactly like Output→Input.
        var hover = HitTestPinAt(pp.Position);
        Brush stroke;
        // The valid target side is the opposite of the side we grabbed.
        SocketType targetSide = _wireSourceIsInput ? SocketType.Output : SocketType.Input;
        if (hover.HasValue && hover.Value.Socket.Type == targetSide)
        {
            // Resolve producer/consumer with the source-side role swap so the
            // compat table (output→input) reads correctly in both directions.
            bool ok = _wireSourceIsInput
                ? IsCompatibleDrop(hover.Value.Socket, _wireSourceSocket, hover.Value.Node, _wireSourceNode)
                : IsCompatibleDrop(_wireSourceSocket, hover.Value.Socket, _wireSourceNode, hover.Value.Node);
            // Wire-preview brushes route through the theme's status tokens
            // so the green/red read consistent with the rest of the suite.
            stroke = ok
                ? ResolveBrush("OkBrush",  0xEE, 0x6F, 0xA4, 0x6B)
                : ResolveBrush("ErrBrush", 0xEE, 0xC9, 0x53, 0x3C);
        }
        else
        {
            stroke = ResolveBrush("EmberPrimaryBrush", 0xCC, 0xE5, 0xA2, 0x4E);
        }

        // Mutate the existing temp Path in place instead of removing
        // + recreating it every frame. Recreating the visual element caused a
        // visible pop on each pointer-move (and churned LinkLayer.Children); now
        // we update only the geometry (Data) and Stroke brush of the one stable
        // Path. This also keeps the _tempWirePath reference identity stable across
        // a mid-drag RedrawLinks() (which preserves it via its `keep` local),
        // closing the stale-reference window the link-deletion finding flagged.
        if (_tempWirePath is null)
        {
            _tempWirePath = BuildBezierWire(src, pp.Position, stroke);
            LinkLayer.Children.Add(_tempWirePath);
        }
        else
        {
            UpdateBezierWire(_tempWirePath, src, pp.Position, stroke);
            // Defensive: if a mid-drag RedrawLinks somehow dropped the path from
            // the layer, re-attach it so the preview stays visible.
            if (!LinkLayer.Children.Contains(_tempWirePath))
                LinkLayer.Children.Add(_tempWirePath);
        }
        e.Handled = true;
    }

    // Recompute an existing wire Path's bezier geometry + swap its
    // Stroke without allocating a new Path. Mirrors BuildBezierWire's control-
    // point math so the curve shape is identical; only the visual element is
    // reused. Keeps mid-drag wire-preview updates jank-free (no element pop).
    private static void UpdateBezierWire(Microsoft.UI.Xaml.Shapes.Path path, Point p1, Point p2, Brush stroke)
    {
        // Use the SAME control-point math as BuildBezierWire (incl.
        // the back-routing kicker + self-loop branch) so the in-flight drag
        // preview's curve shape matches the committed wire exactly — previously
        // this duplicated the naive `dx = Max(40, |dx|*0.5)` formula, so a
        // back-routed drag preview flattened while the committed wire bowed out.
        var (c1, c2) = WireControlPoints(p1, p2);

        var fig = new PathFigure { StartPoint = p1 };
        fig.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = p2 });
        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        path.Data   = geom;
        path.Stroke = stroke;
    }

    private void OnWorldPointerReleasedForWire(object sender, PointerRoutedEventArgs e)
    {
        WorldCanvas.PointerMoved       -= OnWorldPointerMovedForWire;
        WorldCanvas.PointerReleased    -= OnWorldPointerReleasedForWire;
        WorldCanvas.PointerCanceled    -= OnWorldPointerCancelledForWire;
        WorldCanvas.PointerCaptureLost -= OnWorldPointerCancelledForWire;
        WorldCanvas.ReleasePointerCapture(e.Pointer);

        var pp = e.GetCurrentPoint(WorldCanvas);
        var src = _wireSourceSocket;
        var srcNode = _wireSourceNode;
        bool srcIsInput = _wireSourceIsInput;
        if (_tempWirePath is not null) LinkLayer.Children.Remove(_tempWirePath);
        _tempWirePath = null;
        _wireSourceNode    = null;
        _wireSourceSocket  = null;
        _wireSourcePin     = null;
        _wireSourceIsInput = false;
        _pinCenterCache    = null;
        ClearTransientHotkeyContext();

        if (src is null || srcNode is null) return;

        var hover = HitTestPinAt(pp.Position);
        if (hover is null) return;

        // Resolve producer (output) / consumer (input) honouring the
        // drag direction. Forward drag: src is the output, the drop target must
        // be an input. Reverse drag (src is an input): the drop target must be an
        // OUTPUT, and we swap roles so the producer is the hovered output and the
        // consumer is the source input. Either way `fromSocket`/`fromNode` end up
        // on the output side and `toSocket`/`toNode` on the input side.
        SocketType requiredTargetSide = srcIsInput ? SocketType.Output : SocketType.Input;
        if (hover.Value.Socket.Type != requiredTargetSide)
        {
            // Released over the wrong-side pin or non-pin region — silent discard,
            // log via GlobalLogger so a curious user can find a trail in the
            // System Log panel without a modal dialog.
            string want = srcIsInput ? "output" : "input";
            GlobalLogger.Log(
                $"WidgetGraphCanvas: wire from '{src.Name}' rejected — drop target was not an {want} socket.",
                "WidgetGraphCanvas", LogLevel.System);
            return;
        }

        // Producer (output) / consumer (input) after the role swap.
        Node   fromNode; Socket fromSocket;
        Node   toNode;   Socket toSocket;
        if (srcIsInput)
        {
            fromNode = hover.Value.Node; fromSocket = hover.Value.Socket;
            toNode   = srcNode;          toSocket   = src;
        }
        else
        {
            fromNode = srcNode;          fromSocket = src;
            toNode   = hover.Value.Node; toSocket   = hover.Value.Socket;
        }

        if (!IsCompatibleDrop(fromSocket, toSocket, fromNode, toNode))
        {
            GlobalLogger.Log(
                $"WidgetGraphCanvas: wire '{fromSocket.Name}' → '{toSocket.Name}' rejected — incompatible types " +
                $"({fromSocket.DataType} → {toSocket.DataType}).",
                "WidgetGraphCanvas", LogLevel.System);
            return;
        }

        // Compatible — create the Link.
        var graph = _trigger?.Graph;
        if (graph is null) return;

        if (_vm?.Document is { } doc) doc.PushUndo();

        // Single fan-in: an input socket takes exactly ONE inbound wire.
        // Dropping a wire onto an already-connected input REPLACES the prior link
        // (the WinForms editor did RemoveAll before Add). Appending a second wire
        // left a dead link that NodeEvaluator's FirstOrDefault input-resolution
        // silently ignored — the author saw two wires but the graph evaluated one
        // (non-deterministic source) and the orphan persisted in the .phxlayer.
        // One PushUndo (above) covers the remove+add as a single step.
        // Keyed on the resolved consumer (input) end so the fan-in
        // rule holds for reverse-drag too.
        graph.Links.RemoveAll(l =>
            l.ToNodeId == toNode.Id && l.ToSocketId == toSocket.Id);

        graph.Links.Add(new Link
        {
            FromNodeId   = fromNode.Id,
            FromSocketId = fromSocket.Id,
            ToNodeId     = toNode.Id,
            ToSocketId   = toSocket.Id,
        });

        if (_vm?.Document is { } d2) d2.MarkDirty();
        RedrawLinks();
        // A new wire can change an UpstreamImage preview's source.
        RefreshPreviews();
        e.Handled = true;
    }

    private void OnPinPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Canvas-scope hover affordance: emphasize every wire incident to the
        // hovered pin so its connections read at a glance across a dense
        // graph. The pin-LOCAL feedback (scale 14→18 + glow + tooltip)
        // already lives in WidgetGraphNodeView.BuildPinPath — the committed
        // wire Paths are the one surface only the canvas can restyle.
        // Skipped while a wire drag is live: the drag preview owns wire
        // coloring (green/red compat) for the whole drag.
        if (_wireSourceNode is not null) return;
        if (sender is not FrameworkElement pin || pin.Tag is not Socket socket) return;

        // Drop any lingering highlight first (a missed PointerExited must
        // never leave two pins' wires emphasized at once).
        ClearPinHoverHighlight();

        Node? owner = ResolveOwnerNode(pin);
        if (owner is null) return;
        _hoveredPinNodeId   = owner.Id;
        _hoveredPinSocketId = socket.Id;
        RestyleIncidentWires(hovered: true);
    }

    private void OnPinPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Symmetric companion to OnPinPointerEntered — restore the incident
        // wires to their base style exactly (type colour / exec white / gold
        // selection all re-resolve through the same tokens RedrawLinks uses).
        ClearPinHoverHighlight();
    }

    // Restore any hover-emphasized wires to base styling, THEN drop the
    // hovered-pin ids (the restore pass needs them to find its targets).
    // Idempotent — safe from every caller (exit, drag start, re-enter).
    private void ClearPinHoverHighlight()
    {
        if (_hoveredPinSocketId is null) return;
        RestyleIncidentWires(hovered: false);
        _hoveredPinNodeId   = null;
        _hoveredPinSocketId = null;
    }

    // True when the link touches the hovered pin. Matched on the
    // (node id, socket id) pair — the same key the wire model itself carries —
    // so the highlight can never bleed onto another node's like-named socket.
    private bool IsIncidentToHoveredPin(Link link)
        => _hoveredPinNodeId is not null
           && ((link.FromNodeId == _hoveredPinNodeId && link.FromSocketId == _hoveredPinSocketId)
            || (link.ToNodeId   == _hoveredPinNodeId && link.ToSocketId   == _hoveredPinSocketId));

    // Restyle the rendered Paths of every link incident to the hovered pin,
    // in place — no rebuild, no geometry churn. _linkPaths is the same
    // Link→Path map the node-drag incident reroute feeds from, so any wire
    // RedrawLinks skipped (endpoint mid-layout) is simply absent and picks up
    // the correct style from the re-apply inside the next RedrawLinks pass.
    private void RestyleIncidentWires(bool hovered)
    {
        foreach (var (link, path) in _linkPaths)
        {
            if (!IsIncidentToHoveredPin(link)) continue;
            ApplyWireStyle(link, path, hovered);
        }
    }

    // Style one committed wire Path. Base styling re-resolves through the
    // exact brush/thickness tokens RedrawLinks uses (type-coloured data wire,
    // exec white, gold selection), so a restore is pixel-identical to a fresh
    // paint. Hover emphasis routes through the theme's OkBrush status token —
    // green reads "connected" in the §2 palette semantics — at the selection
    // weight, so the emphasized stroke matches the established highlight
    // weight without stealing the selection gold; a wire that is BOTH
    // selected and hovered keeps its gold (selection outranks hover).
    private void ApplyWireStyle(Link link, Microsoft.UI.Xaml.Shapes.Path path, bool hovered)
    {
        Socket? srcSocket = null;
        if (_nodeViews.TryGetValue(link.FromNodeId, out var fromView))
        {
            srcSocket = fromView.Node.Sockets?
                .FirstOrDefault(s => string.Equals(s.Id, link.FromSocketId, StringComparison.Ordinal));
        }
        bool isFlow   = srcSocket?.DataType == SocketDataType.Flow;
        bool selected = _selectedLink is { } sl && sl.Id == link.Id;

        if (hovered && !selected)
        {
            path.Stroke          = ResolveBrush("OkBrush", 0xEE, 0x6F, 0xA4, 0x6B);
            path.StrokeThickness = WireThicknessFor(isFlow, selected: true);
        }
        else
        {
            path.Stroke          = WireBrushFor(srcSocket, isFlow, selected);
            path.StrokeThickness = WireThicknessFor(isFlow, selected);
        }
    }

    private Node? ResolveOwnerNode(FrameworkElement pin)
    {
        DependencyObject? walker = pin;
        while (walker is not null)
        {
            if (walker is WidgetGraphNodeView ngv) return ngv.Node;
            walker = VisualTreeHelper.GetParent(walker);
        }
        return null;
    }

    private (Node Node, Socket Socket)? HitTestPinAt(Point worldPoint)
    {
        // While a wire drag is live this fires on every pointer-move, and the
        // pins cannot move for the whole drag (the pointer is captured for the
        // wire) — so sweep the drag-start snapshot of pin centres instead of
        // paying a TransformToVisual layout query per pin per move. The
        // snapshot applies the same skip rules as the live sweep below, so the
        // geometric result is identical. Non-drag callers (the press-time pin
        // probe) fall through to the live sweep — a one-off per press.
        if (_wireSourceNode is not null)
        {
            var cache = _pinCenterCache ??= BuildPinCenterCache();
            foreach (var (node, socket, center) in cache)
            {
                double cdx = worldPoint.X - center.X;
                double cdy = worldPoint.Y - center.Y;
                if (cdx * cdx + cdy * cdy <= 256)
                    return (node, socket);
            }
            return null;
        }

        // Iterate every pin and test whether the world-coord point is inside
        // its on-canvas rectangle. With dozens of nodes this is cheap; sprint
        // D will revisit if larger graphs profile slow.
        foreach (var (nodeId, view) in _nodeViews)
        {
            foreach (var (socketId, pin) in view.PinElements)
            {
                if (pin.ActualWidth <= 0 || pin.ActualHeight <= 0) continue;
                Point center;
                try
                {
                    var t = pin.TransformToVisual(WorldCanvas);
                    center = t.TransformPoint(new Point(pin.ActualWidth / 2.0, pin.ActualHeight / 2.0));
                }
                catch (Exception ex)
                {
                    // Log instead of silently skipping. A
                    // TransformToVisual failure here means this pin can never be a
                    // drop target (its position is unresolvable), which would look
                    // like a dead socket to the author — surface it in the System
                    // log (no modal)
                    // so the failure is diagnosable rather than invisible.
                    GlobalLogger.Error("WidgetGraphCanvas",
                        $"HitTestPinAt.TransformToVisual (node '{view.Node.Title}', socket '{socketId}')", ex);
                    continue;
                }

                // Widened hit radius (~16px, was ~12px) ported from
                // Architect's generous pin targeting — the painted glyph is small
                // (14×14, the visible shape smaller still), so a 16px catch radius
                // means the cursor doesn't have to land pixel-perfect to start /
                // commit a wire. 256 = 16².
                double dx = worldPoint.X - center.X;
                double dy = worldPoint.Y - center.Y;
                if (dx * dx + dy * dy <= 256 && pin.Tag is Socket s)
                    return (view.Node, s);
            }
        }
        return null;
    }

    // Snapshot every resolvable pin's WorldCanvas-space centre for the
    // wire-drag fast path above. Mirrors the live sweep's skip rules exactly:
    // unmeasured pins, pins without a Socket Tag, and pins whose transform
    // throws are all excluded — a pin the live sweep could never return is
    // never cached either. The transform failure is logged once per drag
    // (here) instead of once per pointer-move.
    private List<(Node Node, Socket Socket, Point Center)> BuildPinCenterCache()
    {
        var cache = new List<(Node Node, Socket Socket, Point Center)>();
        foreach (var (nodeId, view) in _nodeViews)
        {
            foreach (var (socketId, pin) in view.PinElements)
            {
                if (pin.ActualWidth <= 0 || pin.ActualHeight <= 0) continue;
                if (pin.Tag is not Socket s) continue;
                Point center;
                try
                {
                    var t = pin.TransformToVisual(WorldCanvas);
                    center = t.TransformPoint(new Point(pin.ActualWidth / 2.0, pin.ActualHeight / 2.0));
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("WidgetGraphCanvas",
                        $"HitTestPinAt.TransformToVisual (node '{view.Node.Title}', socket '{socketId}')", ex);
                    continue;
                }
                cache.Add((view.Node, s, center));
            }
        }
        return cache;
    }

    // Visualist-side type-compat (per the pillar-isolation rule — Architect's
    // NodeRegistry.AreCompatible doesn't carry visual-only DataTypes
    // Image/Color/Scalar/Vector*/Audio):
    //   - reject self-loops (same node)
    //   - reject same-side wires (Out→Out / In→In)
    //   - Any matches anything
    //   - equal types match
    //   - Int↔Float widening (carry the Architect rule for math chains)
    //   - everything else: strict equality
    private static bool IsCompatibleDrop(
        Socket src, Socket dst,
        Node srcNode, Node dstNode)
    {
        if (ReferenceEquals(srcNode, dstNode)) return false;
        if (src.Type == dst.Type) return false; // Out→Out or In→In
        return AreDataTypesCompatible(src.DataType, dst.DataType);
    }

    // Pure DataType-pair compat — the table that IsCompatibleDrop's
    // socket check delegates to, exposed so the compatible-socket spawn menu can
    // filter registry templates (which carry SocketSpec DataTypes, not live
    // Sockets). Same rules as above: Any ↔ anything, equal types, Int↔Float
    // widening; everything else strict equality. Direction-agnostic (the matrix
    // is symmetric), so callers pass producer-then-consumer for readability only.
    private static bool AreDataTypesCompatible(SocketDataType a, SocketDataType b)
    {
        if (a == SocketDataType.Any || b == SocketDataType.Any) return true;
        if (a == b) return true;
        if ((a == SocketDataType.Int   && b == SocketDataType.Float) ||
            (a == SocketDataType.Float && b == SocketDataType.Int))  return true;
        return false;
    }

    // ─── right-click context menus + spawn palette ───────────────────────

    private void OnHostRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Right-click over a node or wire is handled by their own menus —
        // this fires only for empty-canvas right-clicks. Filter out node hits
        // by walking the visual tree.
        if (e.OriginalSource is FrameworkElement fe && IsInsideAnyNodeView(fe)) return;
        if (e.OriginalSource is Microsoft.UI.Xaml.Shapes.Path) return; // wire menu handles this

        var hostPos  = e.GetPosition(HostGrid);
        var worldPos = TransformHostToWorld(hostPos);

        var flyout = new MenuFlyout();

        // Grid-snap toggle. ToggleMenuFlyoutItem renders the
        // checkmark for free; click flips SnapEnabled and logs a single
        // System line so the System Log panel carries a trail without a
        // modal.
        var snapToggle = new ToggleMenuFlyoutItem
        {
            Text      = $"Snap to Grid ({SnapStepPx}px)",
            IsChecked = SnapEnabled,
        };
        snapToggle.Click += (_, _) =>
        {
            // Route through IsSnapEnabled so the canvas-header
            // CheckBox stays coherent with the flyout state. The setter
            // emits the System log line itself; no separate log call here.
            // Labels reflect the corrected Alt-hold semantics
            // (momentary DISABLE while held). The pre-fix copy advertised the
            // opposite behaviour, which drifted from CHANGELOG 0.9.10.
            IsSnapEnabled = !SnapEnabled;
        };
        flyout.Items.Add(snapToggle);
        flyout.Items.Add(new MenuFlyoutSeparator());

        // Searchable spawn palette atop the cascading category menu, so a
        // node can be found by name without walking 50+ templates across submenus.
        var search = new MenuFlyoutItem
        {
            Text = Localizer.T("visualist.canvas.context.search_nodes", "Search Nodes… (F)"),
        };
        search.Click += (_, _) => ShowSpawnSearchPalette(worldPos);
        flyout.Items.Add(search);

        var add = new MenuFlyoutSubItem { Text = Localizer.T("visualist.canvas.context.add_node", "Add Node…") };
        BuildPaletteSubmenu(add, worldPos);
        flyout.Items.Add(add);
        flyout.ShowAt(HostGrid, hostPos);
        e.Handled = true;
    }

    // True when keyboard focus sits in a text-input control (inline value
    // pill, F2 rename box, search palette box) so the bare-F / Space palette
    // shortcut doesn't fire while the user is typing.
    private bool IsTextInputFocused()
    {
        try
        {
            DependencyObject? fe = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (fe is not null)
            {
                if (fe is TextBox || fe is NumberBox) return true;
                fe = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(fe);
            }
        }
        catch { /* XamlRoot not ready — treat as not-focused */ }
        return false;
    }

    /// <summary>
    /// Show the searchable spawn palette. A flyout with a filter TextBox +
    /// result list over <see cref="WidgetNodeRegistry"/> templates; Enter / click
    /// spawns the selected node via <see cref="SpawnNodeAt"/> (which carries the
    /// snap / singleton / wire-splice logic). <paramref name="worldPosOverride"/>
    /// is the spawn point (the right-click world point); null anchors at the
    /// viewport centre (the bare-F keypress has no cursor).
    /// </summary>
    private void ShowSpawnSearchPalette(Point? worldPosOverride)
    {
        double cx = HostGrid.ActualWidth  / 2;
        double cy = HostGrid.ActualHeight / 2;
        Point worldPos = worldPosOverride ?? TransformHostToWorld(new Point(cx, cy));

        var box = new TextBox
        {
            PlaceholderText = Localizer.T("visualist.canvas.search.placeholder", "Search nodes…"),
            MinWidth = 260,
        };
        var list = new ListView
        {
            MaxHeight = 300,
            MinWidth  = 260,
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
        };
        // Items are plain DATA objects (SpawnPaletteItem),
        // NOT pre-built ListViewItem containers. Stuffing ListViewItem instances
        // straight into Items made the list highlight on click but never raise
        // ItemClick — the reported "I can click a result but no node spawns" — because
        // the framework only wires its item-click plumbing onto the containers IT
        // generates. With data items + a ToString that shows the display title, the
        // ListView generates its own containers and ItemClick fires normally.
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(6), MinWidth = 260 };
        panel.Children.Add(box);
        panel.Children.Add(list);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };

        var titles = WidgetNodeRegistry.Templates.Values
            .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Title)
            .ToList();

        void Populate(string filter)
        {
            list.Items.Clear();
            foreach (var title in titles)
            {
                string disp = WidgetNodeRegistry.GetDisplayTitle(title);
                if (string.IsNullOrWhiteSpace(filter)
                    || disp.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    list.Items.Add(new SpawnPaletteItem(disp, title));
                }
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        Populate("");

        void Commit(string? title)
        {
            if (string.IsNullOrEmpty(title)) return;
            flyout.Hide();
            SpawnNodeAt(title!, worldPos);
        }

        box.TextChanged += (_, _) => Populate(box.Text);
        box.KeyDown += (_, ev) =>
        {
            switch (ev.Key)
            {
                case Windows.System.VirtualKey.Enter:
                    Commit(((list.SelectedItem ?? list.Items.FirstOrDefault()) as SpawnPaletteItem)?.Title);
                    ev.Handled = true;
                    break;
                case Windows.System.VirtualKey.Escape:
                    flyout.Hide();
                    ev.Handled = true;
                    break;
                case Windows.System.VirtualKey.Down when list.Items.Count > 0:
                    list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1);
                    ev.Handled = true;
                    break;
                case Windows.System.VirtualKey.Up when list.Items.Count > 0:
                    list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                    ev.Handled = true;
                    break;
            }
        };
        list.ItemClick += (_, args) => Commit((args.ClickedItem as SpawnPaletteItem)?.Title);

        flyout.ShowAt(HostGrid, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = new Point(cx, cy),
        });
        try { box.Focus(FocusState.Programmatic); } catch { /* pre-realised */ }
    }

    /// <summary>
    /// Backing data item for the spawn search palette.
    /// A plain data object (not a ListViewItem container) so the ListView generates
    /// its own containers and raises ItemClick on a mouse click — see the note in
    /// ShowSpawnSearchPalette. <see cref="ToString"/> returns the display title so
    /// the list renders it without needing a DisplayMemberPath / DataTemplate.
    /// </summary>
    private sealed class SpawnPaletteItem
    {
        public SpawnPaletteItem(string display, string title)
        {
            Display = display;
            Title   = title;
        }

        public string Display { get; }
        public string Title   { get; }

        public override string ToString() => Display;
    }

    private void BuildPaletteSubmenu(MenuFlyoutSubItem root, Point worldSpawnPoint)
    {
        // Group templates by category, then list each title under the category
        // submenu. WidgetNodeRegistry.AllowedCategories already filters out
        // the Architect-only categories so we can iterate Templates safely.
        var byCategory = WidgetNodeRegistry.Templates.Values
            .GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var grp in byCategory)
        {
            var sub = new MenuFlyoutSubItem { Text = grp.Key };
            foreach (var tmpl in grp.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
            {
                string captureTitle = tmpl.Title;
                var item = new MenuFlyoutItem { Text = WidgetNodeRegistry.GetDisplayTitle(captureTitle) };
                item.Click += (_, _) => SpawnNodeAt(captureTitle, worldSpawnPoint);
                sub.Items.Add(item);
            }
            root.Items.Add(sub);
        }
    }

    private void SpawnNodeAt(string title, Point worldPoint)
    {
        if (_trigger?.Graph is not { } graph) return;

        // Audio.Play singleton-per-trigger guard. The DeleteNode flyout
        // comment claims "the only one Audio.Play per trigger rule already
        // gates duplicate drops" but no such gate exists on the spawn path —
        // this is that gate. Logs via GlobalLogger (no modal). Mirror gates
        // live in DuplicateNode and PasteNodesFromClipboard.
        if (string.Equals(title, AudioSinkNode.Title, StringComparison.Ordinal)
            && HasAudioPlay(graph))
        {
            GlobalLogger.Log(
                $"WidgetGraphCanvas: Audio.Play rejected — trigger '{_trigger?.Name}' already carries one. " +
                "Only one Audio.Play sink per trigger is allowed.",
                "WidgetGraphCanvas", LogLevel.System);
            return;
        }

        // Viewer node drop-on-wire splice (Manifesto §4.6). Before
        // pushing undo + spawning, check whether the drop point lies on top
        // of an existing wire. If so, splice the Viewer node into that wire
        // (source → Viewer.In and Viewer.Out → destination) instead of
        // dropping it on empty canvas.
        if (string.Equals(title, "Viewer", StringComparison.Ordinal)
            && TryFindLinkUnderPoint(worldPoint, out Link? hitLink) && hitLink is not null)
        {
            SpawnViewerSplicingLink(worldPoint, hitLink);
            return;
        }

        if (_vm?.Document is { } doc) doc.PushUndo();

        // Snap the spawn point to the 20px grid when snap is on so
        // newly-spawned nodes sit aligned with anything else the user has
        // dropped on grid. Same rule as the drag path.
        bool snap = ShouldSnap();
        int spawnX = snap ? SnapToGrid(worldPoint.X) : (int)Math.Round(worldPoint.X);
        int spawnY = snap ? SnapToGrid(worldPoint.Y) : (int)Math.Round(worldPoint.Y);

        var node = WidgetNodeRegistry.Instantiate(
            title,
            new System.Drawing.Point(spawnX, spawnY));
        graph.Nodes.Add(node);

        if (_vm?.Document is { } d2) d2.MarkDirty();
        Rebuild();
    }

    /// <summary>
    /// True when the trigger graph already carries an Audio.Play sink.
    /// Visualist-local check (per-pillar isolation) — no AudioSinkNode count
    /// helper exists on the engine surface today.
    /// </summary>
    private static bool HasAudioPlay(Graph graph)
    {
        foreach (var n in graph.Nodes)
        {
            if (AudioSinkNode.Is(n)) return true;
        }
        return false;
    }

    /// <summary>
    /// Locate a Link whose rendered Path passes within
    /// <see cref="WireHitTolerancePx"/> of <paramref name="worldPoint"/>.
    /// Uses a sampled-bezier hit test (matches the BuildBezierWire control
    /// point geometry above) so the math doesn't depend on the Path's
    /// post-layout actual stroke.
    /// </summary>
    private bool TryFindLinkUnderPoint(Point worldPoint, out Link? hit)
    {
        hit = null;
        if (_trigger?.Graph is not Graph graph) return false;
        if (graph.Links.Count == 0) return false;

        double bestDistSq = WireHitTolerancePx * WireHitTolerancePx;
        Link? best = null;

        foreach (var link in graph.Links)
        {
            if (!_nodeViews.TryGetValue(link.FromNodeId, out var fromView)) continue;
            if (!_nodeViews.TryGetValue(link.ToNodeId,   out var toView))   continue;
            if (!TryGetPinCenter(fromView, link.FromSocketId, out var p1)) continue;
            if (!TryGetPinCenter(toView,   link.ToSocketId,   out var p2)) continue;

            // Sample 32 points along the bezier — enough resolution that a
            // 6px-tolerance hit test catches a 200px wire reliably without
            // the cost of an analytic distance solver.
            // Use the SAME control-point math as BuildBezierWire
            // (back-routing kicker + self-loop branch) so the click hit-test
            // samples the curve that is actually drawn — otherwise a back-routed
            // or self-looping wire would be selectable along a phantom path while
            // the visible wire bows elsewhere.
            var (c1, c2) = WireControlPoints(p1, p2);

            for (int i = 0; i <= 32; i++)
            {
                double t = i / 32.0;
                double mt = 1 - t;
                double x = mt * mt * mt * p1.X + 3 * mt * mt * t * c1.X + 3 * mt * t * t * c2.X + t * t * t * p2.X;
                double y = mt * mt * mt * p1.Y + 3 * mt * mt * t * c1.Y + 3 * mt * t * t * c2.Y + t * t * t * p2.Y;
                double ex = worldPoint.X - x;
                double ey = worldPoint.Y - y;
                double dsq = ex * ex + ey * ey;
                if (dsq < bestDistSq)
                {
                    bestDistSq = dsq;
                    best = link;
                }
            }
        }

        hit = best;
        return best is not null;
    }

    /// <summary>
    /// Sever <paramref name="hitLink"/> and spawn a Viewer node at
    /// <paramref name="worldPoint"/>, wiring source → Viewer.In and
    /// Viewer.Out → destination. Bookkeeps a single undo entry so Ctrl+Z
    /// restores the original wire + drops the Viewer in one step.
    /// </summary>
    private void SpawnViewerSplicingLink(Point worldPoint, Link hitLink)
    {
        if (_trigger?.Graph is not Graph graph) return;

        bool snap = ShouldSnap();
        int spawnX = snap ? SnapToGrid(worldPoint.X) : (int)Math.Round(worldPoint.X);
        int spawnY = snap ? SnapToGrid(worldPoint.Y) : (int)Math.Round(worldPoint.Y);

        // Instantiate before PushUndo so a registry/template failure can't leave
        // the undo stack with an entry for a splice that never happened.
        Node viewer;
        try
        {
            viewer = WidgetNodeRegistry.Instantiate(
                "Viewer",
                new System.Drawing.Point(spawnX, spawnY));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "SpawnViewerSplicingLink: Viewer instantiation failed — splice aborted", ex);
            return;
        }

        if (_vm?.Document is { } doc) doc.PushUndo();

        // Viewer has passthrough sockets registered as ("In", Any) input and
        // ("Out", Any) output — see NodeTemplates.cs line 923. Fall back to
        // first-by-direction lookups so a future rename doesn't silently
        // drop the splice on the floor.
        Socket? viewerIn  = viewer.Sockets.Find(s =>
            s.Type == SocketType.Input  && string.Equals(s.Name, "In", StringComparison.OrdinalIgnoreCase))
            ?? viewer.Sockets.Find(s => s.Type == SocketType.Input);
        Socket? viewerOut = viewer.Sockets.Find(s =>
            s.Type == SocketType.Output && string.Equals(s.Name, "Out", StringComparison.OrdinalIgnoreCase))
            ?? viewer.Sockets.Find(s => s.Type == SocketType.Output);

        if (viewerIn is null || viewerOut is null)
        {
            // Defensive — the Viewer template has both sockets today; if a
            // future registry edit drops them, fall through to the regular
            // spawn-at-point path so the drop isn't lost.
            graph.Nodes.Add(viewer);
            if (_vm?.Document is { } d) d.MarkDirty();
            Rebuild();
            GlobalLogger.Log(
                "WidgetGraphCanvas: Viewer template has no In/Out sockets — falling back to plain spawn.",
                "WidgetGraphCanvas", LogLevel.System);
            return;
        }

        // Snapshot the original endpoints before we mutate the link list.
        string srcNodeId   = hitLink.FromNodeId;
        string srcSocketId = hitLink.FromSocketId;
        string dstNodeId   = hitLink.ToNodeId;
        string dstSocketId = hitLink.ToSocketId;

        graph.Links.RemoveAll(l => l.Id == hitLink.Id);
        graph.Nodes.Add(viewer);

        graph.Links.Add(new Link
        {
            FromNodeId   = srcNodeId,
            FromSocketId = srcSocketId,
            ToNodeId     = viewer.Id,
            ToSocketId   = viewerIn.Id,
        });
        graph.Links.Add(new Link
        {
            FromNodeId   = viewer.Id,
            FromSocketId = viewerOut.Id,
            ToNodeId     = dstNodeId,
            ToSocketId   = dstSocketId,
        });

        if (_vm?.Document is { } d2) d2.MarkDirty();
        graph.MarkStructuralChange();
        Rebuild();
        RefreshPreviews();
        GlobalLogger.Log(
            "WidgetGraphCanvas: spliced Viewer onto wire.",
            "WidgetGraphCanvas", LogLevel.System);
    }

    // Pixel tolerance used by TryFindLinkUnderPoint. Six pixels matches
    // the audit brief's suggested hit radius and keeps the splice forgiving
    // without grabbing a wire the user wasn't pointing at.
    private const double WireHitTolerancePx = 6.0;

    // Per-node right-click context menu — contextual entries (Edit Shape… /
    // Browse Media… / Animate <param>…) + Duplicate + Delete. Wired in
    // HookNodeContextFlyout, called from Rebuild.
    //
    // The pre-T15 WinForms
    // canvas hit-tested the right-click position at menu-open time to decide
    // which contextual entries to surface (Edit Shape on Mask.Polygon/Bezier,
    // Browse Media on a media-loader Path pill, Animate <name> on a numeric
    // attribute pill). The WinUI node view carries no inline attribute pills
    // (those rows render socket glyphs only — WidgetGraphNodeView owns that and
    // is off-limits to this lane), so we recover the *capability* by inspecting
    // the node's own attribute dictionary on flyout-open and prepending the
    // matching entries. The flyout is per-node, so the node identity is known
    // without a coordinate hit-test; we still rebuild on Opening so a mid-session
    // attribute change (e.g. a freshly-typed Path) is reflected.
    private void HookNodeContextFlyout(WidgetGraphNodeView view)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => RebuildNodeFlyout(flyout, view.Node);
        // Seed once so the very first open (before Opening has a chance to run
        // on some WinUI builds) already has the base entries.
        RebuildNodeFlyout(flyout, view.Node);
        view.ContextFlyout = flyout;
    }

    private void RebuildNodeFlyout(MenuFlyout flyout, Node node)
    {
        // Flush any in-flight inline edit before reading the node's
        // attributes so the Animate "<param>" entries reflect the value the
        // author just typed — not the last committed one. Inline pill / rename
        // editors commit on LostFocus; the flyout's Opening event fires BEFORE
        // the menu steals focus, so without this nudge the FIRST menu open could
        // enumerate stale attribute values. CommitNodeRename closes our own
        // overlay editor (this file); CommitFocusedInlineEditor forces a focused
        // pill TextBox (owned by WidgetGraphNodeView) to fire its LostFocus
        // commit by moving keyboard focus to the canvas.
        CommitNodeRename();
        CommitFocusedInlineEditor();

        flyout.Items.Clear();

        // ── Edit Shape… (Mask.Polygon / Mask.Bezier) ────────────────────────
        if (IsShapeNode(node))
        {
            var editShape = new MenuFlyoutItem
            {
                Text = Localizer.T("visualist.canvas.menu.editShape", "Edit Shape…"),
            };
            editShape.Click += (_, _) => _ = OpenShapeEditorAsync(node);
            flyout.Items.Add(editShape);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        // ── Browse Media… (Image/Video/Audio.Load) ─────────────────────────
        // Always show Browse Media for media-loader nodes, matching
        // the baseline WinForms behaviour. Previously the menu item was gated on
        // the Path attribute already EXISTING (attrs.ContainsKey), so a freshly
        // spawned loader (or one whose Path key hadn't been seeded yet) showed no
        // Browse option and the user had no way to set a path — a silent dead end.
        // OpenMediaPickerForNodeAsync writes Path via the dictionary indexer, which
        // creates the key if absent, so missing Path is handled gracefully there.
        if (IsMediaLoaderTitle(node.Title))
        {
            var browse = new MenuFlyoutItem
            {
                Text = Localizer.T("visualist.canvas.menu.browseMedia", "Browse Media…"),
            };
            browse.Click += (_, _) => _ = OpenMediaPickerForNodeAsync(node);
            flyout.Items.Add(browse);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        // ── Animate "<name>"… (numeric attribute parameters) ────────────────
        bool addedAnimate = false;
        foreach (var (attrKey, attrValue) in EnumerateAnimatableAttributes(node))
        {
            var animate = new MenuFlyoutItem
            {
                Text = string.Format(
                    Localizer.T("visualist.canvas.menu.animateParameter", "Animate \"{0}\"…"),
                    attrKey),
            };
            string keyCopy = attrKey;
            double valueCopy = attrValue;
            animate.Click += (_, _) => AnimateParameter(node, keyCopy, valueCopy);
            flyout.Items.Add(animate);
            addedAnimate = true;
        }
        if (addedAnimate) flyout.Items.Add(new MenuFlyoutSeparator());

        // ── base entries ────────────────────────────────────────────────────
        var dup = new MenuFlyoutItem { Text = Localizer.T("common.context.duplicate", "Duplicate") };
        dup.Click += (_, _) => DuplicateNode(node);
        var del = new MenuFlyoutItem { Text = Localizer.T("common.context.delete", "Delete") };
        del.Click += (_, _) => DeleteNode(node);
        flyout.Items.Add(dup);
        flyout.Items.Add(del);
    }

    // If an inline value-pill / numeric editor currently owns
    // keyboard focus, force its commit by moving focus to the canvas. Inline
    // editors in WidgetGraphNodeView commit on LostFocus → OnNodeAttributeCommitted,
    // which writes the typed value into node.Attributes. We can't reach into the
    // pill's internals from this lane, but nudging focus is a clean, public way to
    // flush the pending commit before the context menu enumerates attributes.
    private void CommitFocusedInlineEditor()
    {
        try
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement;
            if (focused is TextBox or NumberBox)
            {
                // Re-home focus onto this UserControl (a Control, so Focus is
                // available); the editor's LostFocus handler then runs and commits
                // its value. SnapToggle is a fallback focusable if the control
                // itself declines focus.
                if (!Focus(FocusState.Programmatic))
                    SnapToggle.Focus(FocusState.Programmatic);
            }
        }
        catch
        {
            // FocusManager can throw mid-teardown / before XamlRoot is ready —
            // a failed flush just means the menu reads committed state, which is
            // the pre-fix behaviour. Never surface this to the user.
        }
    }

    // ── contextual-menu helpers ──────────────────────────────────────────────

    private const string MediaPathAttributeKey = "Path";

    private static readonly string[] s_mediaLoaderTitles = { "Image.Load", "Video.Load", "Audio.Load" };

    private static bool IsShapeNode(Node node)
        => string.Equals(node.Title, "Mask.Polygon", StringComparison.Ordinal)
        || string.Equals(node.Title, "Mask.Bezier",  StringComparison.Ordinal);

    private static bool IsMediaLoaderTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        foreach (var t in s_mediaLoaderTitles)
            if (string.Equals(title, t, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Yields (attrKey, currentValue) for every numeric attribute on the node
    /// that is a real authorable parameter — skips the <c>__Range</c> companion
    /// metadata keys and the shape-vertex JSON (which the ShapeEditor owns) and
    /// only surfaces attributes that parse as a finite number. Mirrors the
    /// baseline's "right-click landed on a numeric inline pill" gate without
    /// needing the pill geometry.
    /// </summary>
    private static IEnumerable<(string Key, double Value)> EnumerateAnimatableAttributes(Node node)
    {
        if (node.Attributes is null) yield break;
        foreach (var (key, raw) in node.Attributes)
        {
            if (string.IsNullOrEmpty(key)) continue;
            // Companion metadata keys carry a "__" marker (e.g. "Feather__Range")
            // — they describe the editable range, they aren't parameters.
            if (key.Contains("__", StringComparison.Ordinal)) continue;
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double v)
                && !double.IsNaN(v) && !double.IsInfinity(v))
            {
                yield return (key, v);
            }
        }
    }

    /// <summary>
    /// Open the ShapeEditor for a Mask.Polygon / Mask.Bezier node. The dialog
    /// mutates the node's Vertices/Closed attributes only on OK; we snapshot for
    /// undo first and mark dirty + refresh on commit. The editor's
    /// per-vertex Animate gesture is bridged into the active trigger's timeline
    /// (mirroring the baseline WidgetEditorForm subscriber).
    /// </summary>
    private async System.Threading.Tasks.Task OpenShapeEditorAsync(Node node)
    {
        if (XamlRoot is null) return;
        try
        {
            var dialog = new Dialogs.ShapeEditorDialog(node) { XamlRoot = XamlRoot };
            dialog.OnRequestAnimateVertex += (vertexIndex, _, _, isBezier) =>
                AnimateVertex(node, vertexIndex, isBezier);

            // Snapshot before showing so an OK commit is undoable as one step.
            _vm?.Document?.PushUndo();
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _vm?.Document?.MarkDirty();
                Rebuild();
                RefreshPreviews();
                GlobalLogger.Log(
                    $"Shape edited on '{node.Title}'.",
                    "WidgetGraphCanvas", LogLevel.System);
            }
            // On Cancel the dialog restored the original attributes; the undo
            // snapshot we pushed is harmless (a no-op step), matching how the
            // pin-animate path also pushes before mutating.
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "OpenShapeEditor", ex);
        }
    }

    /// <summary>
    /// Append per-vertex keyframes at TimeMs=0 for the given vertex, reading the
    /// current coords from the node's Vertices JSON. Bezier mode also seeds the
    /// four cp* tracks so the user immediately gets all six. Parameter paths come
    /// from <see cref="ShapeData.FormatVertexPath"/> so the producer/consumer
    /// contract is single-sourced. Faithful port of the baseline
    /// WidgetEditorForm.AppendVertexKeyframes.
    /// </summary>
    private void AnimateVertex(Node node, int vertexIndex, bool isBezier)
    {
        if (_trigger is null || _vm is null) return;
        try
        {
            string verticesJson = node.Attributes is { } attrs && attrs.TryGetValue("Vertices", out var v) ? v : "[]";
            var verts = ShapeData.Parse(verticesJson);
            if (vertexIndex < 0 || vertexIndex >= verts.Count) return;
            var vert = verts[vertexIndex];

            var timeline = _trigger.Timeline;
            // Seed vertex keyframes at the PLAYHEAD (was a hardcoded 0ms),
            // matching the pin/parameter animate gestures.
            double seedMs = Math.Max(0, _vm.PlayheadMs);
            if (timeline.DurationMs < seedMs) timeline.DurationMs = seedMs;
            if (timeline.DurationMs <= 0) timeline.DurationMs = 1000;
            _vm.Document?.PushUndo();

            void Add(string axis, double value) => timeline.Keyframes.Add(new Keyframe
            {
                ParameterPath = ShapeData.FormatVertexPath(vertexIndex, axis),
                TimeMs        = seedMs,
                Value         = System.Text.Json.JsonSerializer.SerializeToElement(value),
                Curve         = KeyframeCurve.Linear,
            });

            int before = timeline.Keyframes.Count;
            Add("x", vert.X);
            Add("y", vert.Y);
            if (isBezier)
            {
                if (vert.Cp1X.HasValue) Add("cp1x", vert.Cp1X.Value);
                if (vert.Cp1Y.HasValue) Add("cp1y", vert.Cp1Y.Value);
                if (vert.Cp2X.HasValue) Add("cp2x", vert.Cp2X.Value);
                if (vert.Cp2Y.HasValue) Add("cp2y", vert.Cp2Y.Value);
            }

            _vm.Document?.MarkDirty();
            _vm.NotifyActiveTriggerChanged();
            GlobalLogger.Log(
                $"Animated vertex {vertexIndex} on '{node.Title}' — {timeline.Keyframes.Count - before} keyframes seeded at {seedMs:0}ms.",
                "WidgetGraphCanvas", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", $"AnimateVertex {vertexIndex}", ex);
        }
    }

    /// <summary>
    /// Open the media picker for an Image/Video/Audio.Load node and write the
    /// chosen RELATIVE path back into the node's <c>Path</c> attribute, quoted
    /// to match the attribute-value convention (loader nodes store the path as a
    /// JSON-quoted string literal). Undo-snapshotted; marks dirty + refreshes on
    /// commit. Faithful port of the baseline OpenMediaPickerForNode.
    /// </summary>
    private async System.Threading.Tasks.Task OpenMediaPickerForNodeAsync(Node node)
    {
        if (XamlRoot is null) return;
        try
        {
            var kind = node.Title switch
            {
                "Image.Load" => Dialogs.MediaPickerDialog.MediaKind.Image,
                "Video.Load" => Dialogs.MediaPickerDialog.MediaKind.Video,
                "Audio.Load" => Dialogs.MediaPickerDialog.MediaKind.Audio,
                _            => (Dialogs.MediaPickerDialog.MediaKind?)null,
            };
            var dialog = new Dialogs.MediaPickerDialog(kind) { XamlRoot = XamlRoot };
            // The dialog sets SelectedRelativePath on either commit path (the
            // "Use Selected" Primary button or a double-click tile, the latter
            // closing via Hide() → ContentDialogResult.None). A Cancel never
            // sets it, so the property is the single reliable commit signal.
            await dialog.ShowAsync();
            string? rel = dialog.SelectedRelativePath;
            if (string.IsNullOrEmpty(rel)) return;

            _vm?.Document?.PushUndo();
            // Guard against a null Attributes bag (the property has a
            // public setter, so deserialization could leave it null). The indexer
            // below would NPE otherwise — now that Browse Media is shown for media
            // loaders unconditionally, a path-less node must still set cleanly.
            node.Attributes ??= new Dictionary<string, string>();
            // Store as a JSON-quoted string literal — the runtime parser treats
            // the loader Path attribute as an expression, so it must be quoted
            // (matches MediaLibrary.UnquoteAttribute's read side).
            node.Attributes[MediaPathAttributeKey] = "\"" + rel.Replace("\\", "/") + "\"";
            _vm?.Document?.MarkDirty();
            Rebuild();
            RefreshPreviews();
            GlobalLogger.Log(
                $"Set media path on '{node.Title}' → {rel}.",
                "WidgetGraphCanvas", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "OpenMediaPickerForNode", ex);
        }
    }

    /// <summary>
    /// Seed a single keyframe at TimeMs=0 for a numeric attribute parameter —
    /// distinct from the socket-level Animate gesture (which seeds a two-keyframe
    /// arc). Mirrors the baseline OnRequestAnimateParameter handler. Also raises
    /// the public <see cref="OnRequestAnimateParameter"/> event so an external
    /// subscriber (editor view / tests) can observe the request.
    /// </summary>
    private void AnimateParameter(Node node, string attrKey, double initialValue)
    {
        if (_trigger is null || _vm is null) return;
        try
        {
            string path = AnimatedPinRegistry.MakeParameterPath(node, attrKey);
            var timeline = _trigger.Timeline;
            // Seed at the PLAYHEAD, not a hardcoded TimeMs=0. Anchoring at
            // 0 always dropped the keyframe at the start of the trigger
            // regardless of where the author had scrubbed, so a record at t≠0
            // landed in the wrong place and the value never animated from the
            // intended moment.
            double seedMs = Math.Max(0, _vm.PlayheadMs);
            if (timeline.DurationMs < seedMs) timeline.DurationMs = seedMs;
            if (timeline.DurationMs <= 0) timeline.DurationMs = 1000;
            _vm.Document?.PushUndo();
            timeline.Keyframes.Add(new Keyframe
            {
                ParameterPath = path,
                TimeMs        = seedMs,
                Value         = System.Text.Json.JsonSerializer.SerializeToElement(initialValue),
                Curve         = KeyframeCurve.Linear,
            });
            _vm.Document?.MarkDirty();
            _vm.NotifyActiveTriggerChanged();
            OnRequestAnimateParameter?.Invoke(path, initialValue);
            GlobalLogger.Log(
                $"Animated parameter '{attrKey}' on '{node.Title}' — 1 keyframe seeded at {seedMs:0}ms.",
                "WidgetGraphCanvas", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", $"AnimateParameter '{attrKey}'", ex);
        }
    }

    /// <summary>
    /// Raised when the user picks "Animate &lt;parameter&gt;…" on a numeric
    /// attribute. Args: (parameterPath, parsedValue). The canvas already seeds
    /// the keyframe itself (so the feature works standalone); this event lets the
    /// hosting editor view refresh its timeline panel or layer on additional
    /// behaviour. Distinct from the socket-level pin Animate gesture.
    /// </summary>
    public event Action<string, double>? OnRequestAnimateParameter;

    private void DeleteNode(Node node)
    {
        if (_trigger?.Graph is not { } graph) return;

        // Manifesto §4.5 — Display is the auto-injected, non-removable terminal
        // node for every per-trigger graph. Block the per-node Delete flyout +
        // surface a System log line so the rejection is discoverable without a
        // modal.
        // Same guard mirrored in DeleteSelectedNodes / CutSelectedNodes below.
        if (DisplaySinkNode.Is(node))
        {
            GlobalLogger.Log(
                Localizer.T("visualist.canvas.status.displayRequired"),
                "WidgetGraphCanvas", LogLevel.System);
            return;
        }

        // Audio.Play sink carries the same removable-guard parity as
        // Display once it's been placed. Audio.Play is not auto-injected (so
        // a graph that never authored one keeps producing no audio), but once
        // the sink is in the graph it's structurally a terminal — the
        // "only one Audio.Play per trigger" rule already gates duplicate
        // drops, and removing it via the canvas would leave the user's
        // upstream wiring dangling. To opt out of audio, unwire the input
        // instead of deleting the sink. Same shape as the Display guard:
        // silent System-tier log line.
        if (AudioSinkNode.Is(node))
        {
            GlobalLogger.Log(
                Localizer.T("visualist.canvas.status.audioRequired"),
                "WidgetGraphCanvas", LogLevel.System);
            return;
        }

        if (_vm?.Document is { } doc) doc.PushUndo();

        // Drop the node and every link that touches it. Same shape the
        // WinForms WidgetGraphCanvas uses for its Delete handler.
        graph.Links.RemoveAll(l => l.FromNodeId == node.Id || l.ToNodeId == node.Id);
        graph.Nodes.RemoveAll(n => n.Id == node.Id);

        if (_vm?.Document is { } d2) d2.MarkDirty();
        Rebuild();
    }

    private void DuplicateNode(Node src)
    {
        if (_trigger?.Graph is not { } graph) return;

        // Audio.Play is singleton-per-trigger. Duplicating an existing
        // Audio.Play would put a second one in the same graph; reject with a
        // System-tier log line (no modal).
        if (AudioSinkNode.Is(src))
        {
            GlobalLogger.Log(
                $"WidgetGraphCanvas: Audio.Play duplicate rejected — trigger '{_trigger?.Name}' already carries one. " +
                "Only one Audio.Play sink per trigger is allowed.",
                "WidgetGraphCanvas", LogLevel.System);
            return;
        }

        if (_vm?.Document is { } doc) doc.PushUndo();

        // Deep-clone via WidgetNodeRegistry.Instantiate plus value copy of
        // attributes — this preserves the canonical socket layout from the
        // current registry (catches the "old graph carries stale sockets"
        // case the way LayerSerializer.Read does on file open).
        var clone = WidgetNodeRegistry.Instantiate(
            src.Title,
            new System.Drawing.Point(src.Location.X + 30, src.Location.Y + 30));
        foreach (var (k, v) in src.Attributes) clone.Attributes[k] = v;
        graph.Nodes.Add(clone);

        if (_vm?.Document is { } d2) d2.MarkDirty();
        Rebuild();
    }

    // Per-wire right-click — Delete. Wires are Path elements in LinkLayer
    // tagged with the Link they render. We turn LinkLayer
    // hit-testing back on (it was off earlier) just for the right-click
    // path. The flyout itself is built on demand inside the shared
    // ContextRequested handler — only ever for the one wire actually
    // right-clicked — instead of a MenuFlyout + item + Click closure being
    // minted for every wire on every RedrawLinks pass.
    private void AttachWireContextFlyout(Microsoft.UI.Xaml.Shapes.Path path, Link link)
    {
        path.Tag = link;
        path.ContextRequested += OnWireContextRequested;
    }

    private void OnWireContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not Microsoft.UI.Xaml.Shapes.Path path) return;
        if (path.Tag is not Link link) return;

        var flyout = new MenuFlyout();
        var del = new MenuFlyoutItem { Text = Localizer.T("visualist.canvas.context.delete_wire", "Delete Wire") };
        del.Click += (_, _) => DeleteLink(link);
        flyout.Items.Add(del);

        // Anchor at the request position (mouse right-click); the
        // positionless overload covers a keyboard-invoked context request.
        if (args.TryGetPosition(path, out var pos)) flyout.ShowAt(path, pos);
        else                                        flyout.ShowAt(path);
        args.Handled = true;
    }

    private void DeleteLink(Link link)
    {
        if (_trigger?.Graph is not { } graph) return;
        if (_vm?.Document is { } doc) doc.PushUndo();
        graph.Links.RemoveAll(l => l.Id == link.Id);
        if (_vm?.Document is { } d2) d2.MarkDirty();
        RedrawLinks();
        // Wire deletion can break an UpstreamImage chain; refresh.
        RefreshPreviews();
    }

    // ─── Cut / Copy / Paste / Select-All / Delete ─────────────
    // Operates on the node multi-selection (_selectedNodes). The clipboard
    // buffer is per-app-process — Visualist owns its own copy per the
    // per-pillar paint isolation rule. JSON shape mirrors what the
    // Architect clipboard does for nodes + the links connecting them.

    private void SelectAllNodes()
    {
        if (_trigger?.Graph?.Nodes is not { Count: > 0 } nodes) return;
        ClearSelection();
        foreach (var node in nodes)
        {
            if (_nodeViews.TryGetValue(node.Id, out var view))
                AddToSelection(view);
        }
    }

    private bool CopySelectedNodes()
    {
        if (_selectedNodes.Count == 0) return false;
        if (_trigger?.Graph is not Graph graph) return false;

        try
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in _selectedNodes) ids.Add(v.Node.Id);

            var snap = new WidgetGraphClipboardPayload
            {
                Nodes = _selectedNodes
                    .Select(v => CloneNode(v.Node))
                    .ToList(),
                Links = graph.Links
                    .Where(l => ids.Contains(l.FromNodeId) && ids.Contains(l.ToNodeId))
                    .Select(CloneLink)
                    .ToList(),
            };
            WidgetGraphClipboard.Json = System.Text.Json.JsonSerializer.Serialize(snap, ClipboardJsonOptions);
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "CopySelectedNodes", ex);
            return false;
        }
    }

    /// <summary>Cut = Copy + Delete in a single undo entry.</summary>
    private void CutSelectedNodes()
    {
        // Strip the Display + Audio.Play sinks out of the selection before
        // the cut — neither may leave the graph (Manifesto §4.5 for Display;
        // for Audio.Play parity). Copy still grabs every selected
        // node (so a paste round-trip of "select all → cut → paste" doesn't
        // silently drop a sink the user expected to come along), and the
        // subsequent Delete pass enforces the non-removable rule for both.
        if (!CopySelectedNodes()) return;
        DeleteSelectedNodes();
    }

    // Synchronous fallback retained for the menu path and tests that call
    // the legacy entry point. New keyboard path uses the async overload
    // which adds OS-clipboard read for cross-pillar Architect → Visualist paste.
    // route through the error-boundary wrapper so the
    // fire-and-forget Task can't fault unobserved.
    private void PasteNodesFromClipboard() => _ = PasteWithErrorBoundaryAsync();

    // error boundary for the fire-and-forget paste
    // callers (Ctrl+V in OnKeyDown, PasteNodesFromClipboard). PasteNodesFromClipboardAsync
    // try/catches its own body, but a synchronous throw before the first await
    // would fault the unobserved Task. This wrapper guarantees any fault — sync or
    // async — lands in GlobalLogger. AsyncErrorBoundary itself can't be used: it
    // lives in Phoenix.Controls.Hub, off-limits to Visualist under the pillar rule.
    private async System.Threading.Tasks.Task PasteWithErrorBoundaryAsync()
    {
        try
        {
            await PasteNodesFromClipboardAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetGraphCanvas", "PasteWithErrorBoundaryAsync", ex);
        }
    }

    /// <summary>
    /// Paste path now tries the OS clipboard's
    /// PhoenixControls.SubGraph payload first (the same format Architect's
    /// Copy writes via <c>Windows.ApplicationModel.DataTransfer.Clipboard</c>)
    /// before falling back to the in-process Visualist buffer. Cross-pillar
    /// paste filters out any node whose category is outside
    /// <see cref="WidgetNodeRegistry.AllowedCategories"/> or that lacks a
    /// Visualist template — Architect-only nodes (DB.GetValue / Twitch.* /
    /// Bus.* / etc.) are dropped silently with a single Communication-tier
    /// log line summarizing the skipped count
    /// (no modal).
    ///
    /// Same template-existence + category-allowlist guard runs on the
    /// in-process buffer too. Even a same-process Visualist→Visualist paste
    /// from a future palette swap can't smuggle in a forbidden category.
    /// </summary>
    private async System.Threading.Tasks.Task PasteNodesFromClipboardAsync()
    {
        if (_trigger?.Graph is not Graph graph) return;

        WidgetGraphClipboardPayload? snap = null;

        // Try the OS clipboard first when the in-process buffer is empty so
        // a cross-pillar paste from Architect wins. When the in-process
        // buffer carries a fresh Visualist copy, prefer that — Architect's
        // Copy also writes a SubGraph payload to the OS clipboard but the
        // in-process buffer carrying content means the user just copied
        // inside Visualist and that intent wins.
        if (string.IsNullOrEmpty(WidgetGraphClipboard.Json))
        {
            snap = await TryReadSubGraphFromSystemClipboardAsync();
        }

        if (snap is null)
        {
            if (string.IsNullOrEmpty(WidgetGraphClipboard.Json)) return;
            try
            {
                snap = System.Text.Json.JsonSerializer.Deserialize<WidgetGraphClipboardPayload>(
                    WidgetGraphClipboard.Json!, ClipboardJsonOptions);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WidgetGraphCanvas", "PasteNodesFromClipboard (deserialize)", ex);
                return;
            }
        }

        if (snap is null || snap.Nodes is null || snap.Nodes.Count == 0) return;

        // Pillar-category validation. Reject any node whose title has
        // no Visualist template (Architect-only) OR whose template's
        // category isn't in AllowedCategories OR is explicitly in
        // ForbiddenCategories. The category check via the registry's
        // template is the source of truth — Node.Category on disk reflects
        // the authoring pillar at the time of save and can be trusted for
        // the forbidden-category fast-fail too.
        int beforeFilter = snap.Nodes.Count;
        snap.Nodes = snap.Nodes
            .Where(n =>
            {
                var template = WidgetNodeRegistry.Get(n.Title);
                if (template is null) return false;
                if (WidgetNodeRegistry.ForbiddenCategories.Contains(template.Category)) return false;
                if (!WidgetNodeRegistry.AllowedCategories.Contains(template.Category)) return false;
                return true;
            })
            .ToList();
        int skipped = beforeFilter - snap.Nodes.Count;
        if (skipped > 0)
        {
            GlobalLogger.Log(
                $"WidgetGraphCanvas: paste skipped {skipped} node(s) outside Visualist's allowed categories " +
                "(likely Architect-only logic / Twitch / DB / Bus / AI nodes).",
                "WidgetGraphCanvas", LogLevel.Communication);
        }
        if (snap.Nodes.Count == 0) return;

        // Audio.Play singleton-per-trigger. If the destination graph
        // already carries an Audio.Play, drop any Audio.Play coming in from
        // the clipboard before the paste lands (and surface a System log
        // line so the silent removal is still discoverable).
        bool hasExistingAudio = HasAudioPlay(graph);
        bool stripppedAudio = false;
        if (hasExistingAudio)
        {
            int before = snap.Nodes.Count;
            snap.Nodes.RemoveAll(n => AudioSinkNode.Is(n));
            int removed = before - snap.Nodes.Count;
            if (removed > 0) stripppedAudio = true;
        }
        else
        {
            // Even when no Audio.Play exists yet, the clipboard payload could
            // carry multiple Audio.Play nodes (e.g. a select-all copy from a
            // graph that itself was authored before the singleton rule). Keep
            // only the first; log the rejection of the rest.
            int audioSeen = 0;
            snap.Nodes.RemoveAll(n =>
            {
                if (!AudioSinkNode.Is(n)) return false;
                audioSeen++;
                if (audioSeen > 1)
                {
                    stripppedAudio = true;
                    return true;
                }
                return false;
            });
        }
        if (stripppedAudio)
        {
            GlobalLogger.Log(
                $"WidgetGraphCanvas: paste dropped extra Audio.Play sink(s) — trigger '{_trigger?.Name}' " +
                "may carry only one Audio.Play.",
                "WidgetGraphCanvas", LogLevel.System);
        }
        if (snap.Nodes.Count == 0) return;

        if (_vm?.Document is { } doc) doc.PushUndo();

        // Map old → new node Id and old → new socket Id so links between
        // pasted nodes wire to the freshly-spawned sockets, not the originals.
        var nodeIdMap   = new Dictionary<string, string>(StringComparer.Ordinal);
        var socketIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var pastedNodes = new List<Node>(snap.Nodes.Count);
        bool snapPaste = ShouldSnap();

        foreach (var src in snap.Nodes)
        {
            string newNodeId = Guid.NewGuid().ToString();
            nodeIdMap[src.Id] = newNodeId;
            src.Id = newNodeId;
            foreach (var s in src.Sockets)
            {
                string newSocketId = Guid.NewGuid().ToString();
                socketIdMap[s.Id] = newSocketId;
                s.Id = newSocketId;
            }
            // +30px offset matches DuplicateNode so paste-then-paste makes
            // two visibly distinct copies that don't stack invisibly.
            // Snap each pasted node's final position to the grid
            // when snap is on. Done after the +30px offset so a pasted
            // group doesn't visually overlap the originals when they were
            // already grid-aligned (snap of +30 lands at the next 20px row).
            int px = src.Location.X + 30;
            int py = src.Location.Y + 30;
            if (snapPaste)
            {
                px = SnapToGrid(px);
                py = SnapToGrid(py);
            }
            src.Location = new System.Drawing.Point(px, py);
            graph.Nodes.Add(src);
            pastedNodes.Add(src);
        }

        if (snap.Links is { Count: > 0 })
        {
            foreach (var lk in snap.Links)
            {
                if (!nodeIdMap.TryGetValue(lk.FromNodeId, out var fromN)) continue;
                if (!nodeIdMap.TryGetValue(lk.ToNodeId,   out var toN))   continue;
                if (!socketIdMap.TryGetValue(lk.FromSocketId, out var fromS)) continue;
                if (!socketIdMap.TryGetValue(lk.ToSocketId,   out var toS))   continue;
                graph.Links.Add(new Link
                {
                    Id           = Guid.NewGuid().ToString(),
                    FromNodeId   = fromN,
                    ToNodeId     = toN,
                    FromSocketId = fromS,
                    ToSocketId   = toS,
                });
            }
        }

        if (_vm?.Document is { } d2) d2.MarkDirty();
        graph.MarkStructuralChange();
        Rebuild();

        // After paste, the newly-pasted items are the multi-selection.
        // Rebuild() cleared _selectedNodes and rebuilt _nodeViews; reseat
        // the selection against the new ids.
        ClearSelection();
        foreach (var n in pastedNodes)
        {
            if (_nodeViews.TryGetValue(n.Id, out var view))
                AddToSelection(view);
        }
    }

    private bool DeleteSelectedNodes()
    {
        if (_trigger?.Graph is not Graph graph) return false;
        if (_selectedNodes.Count == 0) return false;

        // Manifesto §4.5 — Display is non-removable. Filter it out of the
        // delete set so a Ctrl+A → Delete pass leaves the sink intact, and
        // surface a System log line when the filter actually removes a node
        // from the selection so the user understands why one node stayed.
        //
        // Same parity for Audio.Play — once placed it's a structural
        // terminal and the canvas keeps it pinned. Filter it out separately
        // so the user gets a distinct status line per sink kind instead of
        // one ambiguous "something stayed" message.
        bool selectionHadDisplay = _selectedNodes.Any(v => DisplaySinkNode.Is(v.Node));
        bool selectionHadAudio   = _selectedNodes.Any(v => AudioSinkNode.Is(v.Node));
        var ids = new HashSet<string>(
            _selectedNodes
                .Where(v => !DisplaySinkNode.Is(v.Node) && !AudioSinkNode.Is(v.Node))
                .Select(v => v.Node.Id),
            StringComparer.Ordinal);
        if (selectionHadDisplay)
        {
            GlobalLogger.Log(
                Localizer.T("visualist.canvas.status.displayRequired"),
                "WidgetGraphCanvas", LogLevel.System);
        }
        if (selectionHadAudio)
        {
            GlobalLogger.Log(
                Localizer.T("visualist.canvas.status.audioRequired"),
                "WidgetGraphCanvas", LogLevel.System);
        }
        if (ids.Count == 0) return false;

        if (_vm?.Document is { } doc) doc.PushUndo();
        graph.Links.RemoveAll(l => ids.Contains(l.FromNodeId) || ids.Contains(l.ToNodeId));
        graph.Nodes.RemoveAll(n => ids.Contains(n.Id));
        if (_vm?.Document is { } d2) d2.MarkDirty();

        graph.MarkStructuralChange();
        Rebuild();
        return true;
    }

    // JSON serializer used by the in-process clipboard. System.Drawing.Point /
    // Size round-trip through their X/Y / Width/Height properties, but
    // System.Drawing.Color does NOT: it has no settable members, so System.Text
    // .Json deserializes it to default(Color) == ARGB(0,0,0,0) (transparent
    // black). The old comment claimed Color "rides as a
    // struct (not load-bearing)" — wrong: a pasted Socket.Color came back fully
    // transparent, which defeats WidgetSocketPalette.EffectiveColor's
    // white-default → DataType back-fill (it only back-fills when Color is the
    // default WHITE, A==255), so every pasted pin rendered with the wrong /
    // invisible colour, and Node.HeaderColor was lost too. Register a
    // Color↔ARGB-int converter — same fix Architect's clipboard already carries
    // (LogicCanvasView.Clipboard.cs ClipboardColorConverter) and the .phxlayer
    // serializer uses (LayerSerializer.LayerColorJsonConverter). Re-declared
    // locally per the pillar chrome-independence rule.
    private static readonly System.Text.Json.JsonSerializerOptions ClipboardJsonOptions =
        new()
        {
            WriteIndented = false,
            Converters = { new WidgetClipboardColorConverter() },
        };

    // Round-trips System.Drawing.Color as an ARGB int
    // (reads the legacy {R,G,B,A} object form too, for forward-compat with any
    // payload an older build may have produced). Without this the clipboard
    // round-trip silently transparent-blacks every socket colour on paste.
    private sealed class WidgetClipboardColorConverter
        : System.Text.Json.Serialization.JsonConverter<System.Drawing.Color>
    {
        public override System.Drawing.Color Read(
            ref System.Text.Json.Utf8JsonReader reader,
            Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options)
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.Number)
                return System.Drawing.Color.FromArgb(reader.GetInt32());

            if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject)
            {
                byte r = 0, g = 0, b = 0, a = 255;
                while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndObject)
                {
                    if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) continue;
                    string name = reader.GetString() ?? "";
                    reader.Read();
                    switch (name)
                    {
                        case "R": r = reader.GetByte(); break;
                        case "G": g = reader.GetByte(); break;
                        case "B": b = reader.GetByte(); break;
                        case "A": a = reader.GetByte(); break;
                        default:  reader.Skip();        break;
                    }
                }
                return System.Drawing.Color.FromArgb(a, r, g, b);
            }

            reader.Skip();
            return System.Drawing.Color.White;
        }

        public override void Write(
            System.Text.Json.Utf8JsonWriter writer,
            System.Drawing.Color value,
            System.Text.Json.JsonSerializerOptions options)
            => writer.WriteNumberValue(value.ToArgb());
    }

    // OS-clipboard format identifier for cross-pillar sub-graph paste.
    // Mirrors Architect's `LogicCanvasView.Clipboard.cs:SubGraphClipboardFormat`
    // verbatim. Locally re-declared rather than lifted from Architect.Core
    // per the pillar chrome-independence rule — Visualist
    // and Architect each own their own clipboard chrome. If Architect ever
    // renames its constant, this one MUST be updated in lockstep (no
    // compile-time link between the two).
    private const string SubGraphClipboardFormat = "PhoenixControls.SubGraph";

    /// <summary>
    /// Try to read a PhoenixControls.SubGraph payload from the OS
    /// clipboard. Returns null when the clipboard either doesn't contain
    /// the format (no Architect copy has run this session) or the payload
    /// fails to deserialize. The payload shape Architect writes carries
    /// {Nodes, Links, Frames}; Visualist only consumes Nodes + Links —
    /// Frames are silently dropped because Visualist's graph model has no
    /// frame concept.
    /// </summary>
    private static async System.Threading.Tasks.Task<WidgetGraphClipboardPayload?> TryReadSubGraphFromSystemClipboardAsync()
    {
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!view.Contains(SubGraphClipboardFormat)) return null;
            var raw = await view.GetDataAsync(SubGraphClipboardFormat) as string;
            if (string.IsNullOrEmpty(raw)) return null;
            // Architect writes a {Nodes, Links, Frames} envelope. Deserialize
            // into Visualist's local DTO (which carries Nodes + Links only) —
            // System.Text.Json silently ignores the unmapped Frames property
            // with PropertyNameCaseInsensitive matching Nodes/Links.
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };
            return System.Text.Json.JsonSerializer.Deserialize<WidgetGraphClipboardPayload>(raw!, opts);
        }
        catch (Exception ex)
        {
            // Clipboard reads can throw under contention / locked-clipboard
            // conditions; log + degrade to "no payload" so the in-process
            // fallback path still runs.
            GlobalLogger.Error("WidgetGraphCanvas", "TryReadSubGraphFromSystemClipboardAsync", ex);
            return null;
        }
    }

    private static Node CloneNode(Node src)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(src, ClipboardJsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<Node>(json, ClipboardJsonOptions)!;
    }

    private static Link CloneLink(Link src)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(src, ClipboardJsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<Link>(json, ClipboardJsonOptions)!;
    }
}

/// <summary>
/// JSON DTO for the WidgetGraphCanvas clipboard. Holds the cloned node list
/// plus every link whose endpoints sit inside that node set; links to outside
/// the selection are intentionally dropped on copy.
/// </summary>
internal sealed class WidgetGraphClipboardPayload
{
    public List<Node> Nodes { get; set; } = new();
    public List<Link> Links { get; set; } = new();
}

/// <summary>
/// Per-app-process clipboard buffer for cut/copy/paste of widget-graph
/// nodes. Visualist owns its own buffer per the pillar
/// chrome-independence rule — Architect's
/// equivalent lives next to LogicCanvasView, not in a shared spot.
/// </summary>
internal static class WidgetGraphClipboard
{
    public static string? Json;
}
