using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Marquee partial — Shift/Ctrl/Alt mode-aware rubber-band selection. Mirrors
// Canvas.Mouse.cs's marquee state machine (mode snapshot at drag-down,
// Replace / Add / Remove / Toggle modes). Live preview rectangle drawn in
// host-space (overlay above the panned canvas).
public sealed partial class LogicCanvasView
{
    private enum MarqueeMode { Replace, Add, Remove, Toggle }
    private MarqueeMode _marqueeMode = MarqueeMode.Replace;

    private bool _marqueeActive;
    private Point _marqueeStartHost;
    private Rectangle? _marqueeOverlay;
    private List<NodeViewModel> _marqueeBaseline = new();
    // Mixed-selection baselines so marquee Add / Remove / Toggle modes
    // compose against the pre-drag link + frame selection too — without
    // these, dragging a marquee with Shift would lose existing selected
    // wires / frames the moment ApplyMarqueeSelection ran.
    private List<LinkViewModel>  _marqueeBaselineLinks  = new();
    private List<FrameViewModel> _marqueeBaselineFrames = new();

    //  Frame-coalesced marquee apply state. Pre-fix every PointerMoved
    // event during a marquee drag (120 Hz on touchpads) directly called
    // ApplyMarqueeSelection which allocated 3 fresh Lists + per-Node Rect + 2
    // fresh HashSets and walked Nodes/Links/Frames. Now UpdateMarquee just
    // stashes the cursor + dirty flag; the CompositionTarget.Rendering tick
    // (OnRenderingTick in LogicCanvasView.xaml.cs) drains the dirty flag and
    // calls ApplyMarqueeSelection at most once per displayed frame. The hit
    // result Lists + HashSets are reused across frames so the alloc cost
    // collapses to the one-time fixed-capacity initial sizing.
    private bool _marqueeApplyDirty;
    private Rect _marqueeApplyRect;
    private readonly List<NodeViewModel> _marqueeNodeHitsBuffer   = new();
    private readonly List<LinkViewModel> _marqueeLinkHitsBuffer   = new();
    private readonly List<FrameViewModel> _marqueeFrameHitsBuffer = new();
    private readonly HashSet<LinkViewModel> _marqueeLinkPicked     = new();
    private readonly HashSet<FrameViewModel> _marqueeFramePicked   = new();
    // Reused work buffer for ComposeMode so the 3 final List<T> allocations
    // per ApplyMarqueeSelection collapse to typed-once persistent buffers.
    private readonly List<NodeViewModel> _marqueeFinalNodes  = new();
    private readonly List<LinkViewModel> _marqueeFinalLinks  = new();
    private readonly List<FrameViewModel> _marqueeFinalFrames = new();

    // Group drag bookkeeping — captured at MouseDown when the user starts dragging
    // a node that's part of a multi-selection. We store each node's start (X, Y)
    // and translate them all together as the cursor moves.
    private List<(NodeViewModel Node, double X, double Y)> _groupDragStarts = new();

    //  Stroke/fill the marquee per selection mode:
    // Add → sage green, Remove → rust red, Replace/Toggle → selection gold.
    private void ApplyMarqueeModeVisual()
    {
        if (_marqueeOverlay is null) return;
        Windows.UI.Color c = _marqueeMode switch
        {
            MarqueeMode.Add    => ArchitectCanvasPalette.OkSageGreen,
            MarqueeMode.Remove => ArchitectCanvasPalette.ErrRustRed,
            _                  => ArchitectCanvasPalette.SelectionGold,
        };
        _marqueeOverlay.Stroke = new SolidColorBrush(c);
        _marqueeOverlay.Fill   = new SolidColorBrush(Windows.UI.Color.FromArgb(40, c.R, c.G, c.B));
    }

    private void BeginMarquee(Point hostPoint)
    {
        if (_vm is null) return;
        _marqueeActive    = true;
        _marqueeStartHost = hostPoint;
        _marqueeMode      = ResolveMarqueeMode();

        // Snapshot the baseline so Add/Remove/Toggle compose against pre-drag selection.
        _marqueeBaseline = new List<NodeViewModel>(_vm.SelectedNodes);
        _marqueeBaselineLinks  = SnapshotSelectedLinks();
        _marqueeBaselineFrames = SnapshotSelectedFrames();

        if (_marqueeOverlay is null)
        {
            // Gold (#FFD700) per Design_Orders selection token — promotes
            // the marquee out of the ember palette into the canvas's
            // standing selection colour so the in-flight rubber-band reads
            // the same as the resulting selected nodes / wires / frames.
            Brush stroke = (Application.Current.Resources["SelectionBrush"] as Brush)
                ?? new SolidColorBrush(ArchitectCanvasPalette.SelectionGold);
            _marqueeOverlay = new Rectangle
            {
                Stroke = stroke,
                //  2px (was 1px) — a 1px gold dash was
                // weak on the dark canvas.
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(ArchitectCanvasPalette.SelectionGoldFill16),
                IsHitTestVisible = false,
            };
            // Parent into OverlayLayer (a Canvas) — Canvas.SetLeft/Top below
            // is honoured in pixel-space. Pre-fix the rectangle was added
            // directly to HostRoot (a Grid) and the attached-property
            // positioning was silently ignored.
            OverlayLayer.Children.Add(_marqueeOverlay);
        }

        //  Tint the rubber-band per mode so Add /
        // Remove / Replace+Toggle are distinguishable mid-drag. Pre-fix all
        // modes painted the same light-blue dashed rectangle regardless of
        // Add/Remove/Toggle/Replace mode (baseline Color.FromArgb(100, 180, 255)).
        // Now modes are distinguished by color: Add=green, Remove=red,
        // Replace/Toggle=gold (per Design_Orders selection token), which improves
        // visual feedback mid-drag. Applied every BeginMarquee since the overlay
        // is cached across drags.
        ApplyMarqueeModeVisual();

        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_marqueeOverlay, hostPoint.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop (_marqueeOverlay, hostPoint.Y);
        _marqueeOverlay.Width  = 0;
        _marqueeOverlay.Height = 0;
        // 0.10.0 UX P2: stay Collapsed at 0×0. A bare click with no drag
        // would otherwise flash a 1×1 dashed pixel between BeginMarquee
        // and EndMarquee (the latter clears it via the empty-click branch).
        // UpdateMarquee flips Visible once w*h crosses the
        // MarqueeMinVisiblePx threshold.
        _marqueeOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Minimum (w*h) area at which the marquee overlay becomes visible. Below
    /// this the user's gesture is read as "click", not "drag", so the
    /// dashed rectangle stays hidden — avoids the 0×0 flash a single click
    /// would otherwise produce. 4 px² is small enough that even very short
    /// drags surface the overlay immediately.
    /// </summary>
    private const double MarqueeMinVisiblePx = 4;

    private void UpdateMarquee(Point hostPoint)
    {
        if (!_marqueeActive || _marqueeOverlay is null || _vm is null) return;
        double x = System.Math.Min(_marqueeStartHost.X, hostPoint.X);
        double y = System.Math.Min(_marqueeStartHost.Y, hostPoint.Y);
        double w = System.Math.Abs(hostPoint.X - _marqueeStartHost.X);
        double h = System.Math.Abs(hostPoint.Y - _marqueeStartHost.Y);

        // Position overlay in HostRoot space. (x, y) is already host-relative —
        // no transform needed. Earlier code did TransformToVisual(HostRoot, HostRoot)
        // which is identity; removed to keep intent clear.
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_marqueeOverlay, x);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop (_marqueeOverlay, y);
        _marqueeOverlay.Width  = w;
        _marqueeOverlay.Height = h;
        // Lift the overlay out of Collapsed once the gesture is large enough
        // to be a drag (UX P2 — 0×0 flash suppression). Below the threshold
        // BeginMarquee's Collapsed default holds, so a click with no drag
        // never paints a stale pixel.
        var desiredVis = (w * h >= MarqueeMinVisiblePx)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_marqueeOverlay.Visibility != desiredVis)
            _marqueeOverlay.Visibility = desiredVis;

        // Marquee-canvas rect (un-transformed) for hit testing intersected nodes.
        var topLeftCanvas     = HostToCanvas(new Point(x, y));
        var bottomRightCanvas = HostToCanvas(new Point(x + w, y + h));
        _marqueeApplyRect = new Rect(
            topLeftCanvas.X,
            topLeftCanvas.Y,
            bottomRightCanvas.X - topLeftCanvas.X,
            bottomRightCanvas.Y - topLeftCanvas.Y);

        //  Defer ApplyMarqueeSelection to the next rendering tick.
        // Pre-fix this ran inline per PointerMoved (120 Hz on touchpads),
        // doing 3 List + 2 HashSet + per-Node Rect allocations every event.
        // Now the rendering tick reads _marqueeApplyDirty + the cached rect
        // and runs the apply at most once per displayed frame. Drag preview
        // already updates inline (the dashed Rectangle above) so the user
        // still sees the live rubber-band at full frame rate.
        _marqueeApplyDirty = true;
    }

    /// <summary>
    ///  Drained from <see cref="LogicCanvasView.OnRenderingTick"/>
    /// at frame cadence — applies any pending marquee selection exactly once
    /// per displayed frame regardless of how many PointerMoved events fired.
    /// </summary>
    internal void DrainMarqueeApply()
    {
        if (!_marqueeApplyDirty) return;
        _marqueeApplyDirty = false;
        ApplyMarqueeSelection(_marqueeApplyRect);
    }

    private void EndMarquee()
    {
        //  Drain any pending apply so the marquee's last cursor
        // position commits even if no rendering tick fired between the final
        // PointerMoved and EndMarquee. Without this a fast release-after-
        // move would leave the selection at one tick stale.
        DrainMarqueeApply();

        // Zero-distance marquee in Replace mode = "click on empty canvas".
        // Pre-fix this was a no-op (UpdateMarquee never ran on a click without
        // pointer movement, so ApplyMarqueeSelection never fired and the prior
        // multi-selection stayed sticky). UE Blueprints treats empty-click as
        // "clear selection"; mirror that. Architect UX review P1-4.
        if (_marqueeActive
            && _marqueeMode == MarqueeMode.Replace
            && _marqueeOverlay is { Width: <= 1, Height: <= 1 }
            && _vm is not null
            && (_vm.SelectedNodes.Count > 0 || _vm.Selection is not null
                || HasAnyLinkOrFrameSelected()))
        {
            _vm.SetMultiSelection(System.Array.Empty<NodeViewModel>());
            ApplyLinkSelection(System.Array.Empty<LinkViewModel>());
            ApplyFrameSelection(System.Array.Empty<FrameViewModel>());
            _vm.Selection = null;
        }

        _marqueeActive = false;
        _marqueeApplyDirty = false;
        if (_marqueeOverlay is not null) _marqueeOverlay.Visibility = Visibility.Collapsed;
    }

    private void ApplyMarqueeSelection(Rect canvasRect)
    {
        if (_vm is null) return;

        //  Reusable hit buffers — cleared at entry, refilled with
        // intersections, then composed via mode-aware buffers and pushed
        // to the VM. Avoids per-event List allocations.
        _marqueeNodeHitsBuffer.Clear();
        _marqueeLinkHitsBuffer.Clear();
        _marqueeFrameHitsBuffer.Clear();

        // Hit-test fallback for 0×0 nodes: a node whose Width / Height
        // haven't been measured yet should still be selectable; treat
        // them as a 1×1 box at their Location so RectsIntersect still
        // hits when the marquee is dragged over them.
        foreach (var n in _vm.Nodes)
        {
            double w = n.Width  <= 0 ? 1 : n.Width;
            double h = n.Height <= 0 ? 1 : n.Height;
            // RectsIntersect takes two Rects, but we can inline the
            // intersection on the four coords directly to skip the Rect
            // struct alloc for the node side. canvasRect is hot-cached.
            double nx = n.X, ny = n.Y;
            if (canvasRect.X < nx + w && canvasRect.X + canvasRect.Width  > nx
             && canvasRect.Y < ny + h && canvasRect.Y + canvasRect.Height > ny)
            {
                _marqueeNodeHitsBuffer.Add(n);
            }
        }

        foreach (var l in _vm.Links)
        {
            if (LinkIntersectsRect(l, canvasRect)) _marqueeLinkHitsBuffer.Add(l);
        }

        //  Frame marquee selection mirrors the WinForms baseline
        // Canvas.HitTest.cs:IsFrameMarqueeSelected — a frame is selected only
        // when the marquee fully encloses it, OR it has at least one child
        // (node/sub-frame inside it) and every child is itself marquee-selected.
        // Plain bounding-box intersection would wrongly grab a frame the marquee
        // merely crosses through with no children selected.
        foreach (var f in _vm.Frames)
        {
            if (IsFrameMarqueeSelected(f, canvasRect))
                _marqueeFrameHitsBuffer.Add(f);
        }

        // Compose the mode-aware final sets into persistent buffers.
        ComposeModeInto(_marqueeBaseline,       _marqueeNodeHitsBuffer,  _marqueeFinalNodes);
        ComposeModeInto(_marqueeBaselineLinks,  _marqueeLinkHitsBuffer,  _marqueeFinalLinks);
        ComposeModeInto(_marqueeBaselineFrames, _marqueeFrameHitsBuffer, _marqueeFinalFrames);

        _vm.SetMultiSelection(_marqueeFinalNodes);
        ApplyLinkSelection(_marqueeFinalLinks);
        ApplyFrameSelection(_marqueeFinalFrames);
    }

    //  Mode composition uses a reusable HashSet so the membership
    // tests collapse from O(n²) (List.Contains/Remove inside the per-hit loop)
    // to O(n). The scratch set is cleared and refilled each call; element order
    // for the destination List is recovered by walking baseline then hits in
    // their natural order, which keeps the prior visual stability of the
    // selection set while avoiding repeated linear scans on large graphs.
    private readonly HashSet<object> _composeScratch = new();

    private void ComposeModeInto<T>(List<T> baseline, List<T> hits, List<T> dest) where T : class
    {
        dest.Clear();
        _composeScratch.Clear();
        switch (_marqueeMode)
        {
            case MarqueeMode.Replace:
                dest.AddRange(hits);
                break;

            case MarqueeMode.Add:
                // baseline ∪ hits, baseline order first then new hits.
                foreach (var b in baseline)
                    if (_composeScratch.Add(b)) dest.Add(b);
                foreach (var h in hits)
                    if (_composeScratch.Add(h)) dest.Add(h);
                break;

            case MarqueeMode.Remove:
                // baseline ∖ hits — O(1) membership test against the hit set.
                foreach (var h in hits) _composeScratch.Add(h);
                foreach (var b in baseline)
                    if (!_composeScratch.Contains(b)) dest.Add(b);
                break;

            case MarqueeMode.Toggle:
                // baseline △ hits (symmetric difference): keep baseline items not
                // re-hit, then append hits not already in baseline.
                var hitSet = _composeScratch; // alias for clarity
                foreach (var h in hits) hitSet.Add(h);
                foreach (var b in baseline)
                    if (!hitSet.Contains(b)) dest.Add(b);
                // Second set tracks baseline membership for the hit pass.
                _composeScratch2.Clear();
                foreach (var b in baseline) _composeScratch2.Add(b);
                foreach (var h in hits)
                    if (!_composeScratch2.Contains(h)) dest.Add(h);
                break;
        }
    }

    // Secondary scratch set used by the Toggle (symmetric-difference) branch.
    private readonly HashSet<object> _composeScratch2 = new();

    /// <summary>
    /// Coarse wire-vs-rect intersection. Samples the bezier at several t
    /// values and returns true if any sample sits inside the rect. The
    /// sample density is enough to catch typical marquee drags without
    /// the cost of analytic bezier-rect intersection; users dragging a
    /// pixel-thin rectangle across a wire's midpoint is not a real case.
    /// </summary>
    private static bool LinkIntersectsRect(LinkViewModel lvm, Rect rect)
    {
        var from = lvm.LastFromAnchor;
        var to   = lvm.LastToAnchor;
        if (from is null || to is null) return false;
        const int steps = 12;
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            var (px, py) = BezierPath.Sample(from.Value.X, from.Value.Y, to.Value.X, to.Value.Y, t);
            if (px >= rect.X && px <= rect.X + rect.Width
                && py >= rect.Y && py <= rect.Y + rect.Height)
                return true;
        }
        return false;
    }

    /// <summary>
    ///  Marquee inclusion rule for a frame, ported from the WinForms
    /// baseline Canvas.HitTest.cs:IsFrameMarqueeSelected. Returns TRUE when the
    /// marquee fully encloses the frame's bounds, OR when the frame has at least
    /// one child (a node whose origin falls inside the frame, or a sub-frame
    /// fully inside the frame) and every such child is itself marquee-selected
    /// (nodes by intersection, sub-frames recursively). Otherwise FALSE — so a
    /// marquee merely crossing a frame's interior never grabs the frame when no
    /// children are selected. <paramref name="marqueeRect"/> is canvas-space.
    /// </summary>
    private bool IsFrameMarqueeSelected(FrameViewModel frame, Rect marqueeRect)
    {
        if (_vm is null) return false;

        var frameRect = new Rect(frame.X, frame.Y, frame.Width, frame.Height);

        // Rule (a): full enclosure.
        if (RectFullyContains(marqueeRect, frameRect))
            return true;

        // Rule (b): all children must satisfy their own marquee rule. Children
        // are positional — nodes whose origin lands inside the frame, and other
        // frames fully nested inside it.
        bool hasChild = false;

        foreach (var n in _vm.Nodes)
        {
            if (!PointInRect(n.X, n.Y, frameRect)) continue;
            hasChild = true;
            double w = n.Width  <= 0 ? 1 : n.Width;
            double h = n.Height <= 0 ? 1 : n.Height;
            // Child node must intersect the marquee.
            if (!(marqueeRect.X < n.X + w && marqueeRect.X + marqueeRect.Width  > n.X
               && marqueeRect.Y < n.Y + h && marqueeRect.Y + marqueeRect.Height > n.Y))
                return false;
        }

        foreach (var sub in _vm.Frames)
        {
            if (ReferenceEquals(sub, frame)) continue;
            var subRect = new Rect(sub.X, sub.Y, sub.Width, sub.Height);
            if (!RectFullyContains(frameRect, subRect)) continue;
            hasChild = true;
            if (!IsFrameMarqueeSelected(sub, marqueeRect))
                return false;
        }

        return hasChild;
    }

    private static bool RectFullyContains(Rect outer, Rect inner)
        => inner.X >= outer.X
        && inner.Y >= outer.Y
        && inner.X + inner.Width  <= outer.X + outer.Width
        && inner.Y + inner.Height <= outer.Y + outer.Height;

    // Half-open rect [X, X+W) / [Y, Y+H) so a node sitting exactly on the frame's
    // right/bottom edge is treated as OUTSIDE — consistent with the strict-`<`
    // marquee/node intersection test above (a point on the boundary is not a child).
    private static bool PointInRect(double px, double py, Rect r)
        => px >= r.X && px < r.X + r.Width
        && py >= r.Y && py < r.Y + r.Height;

    private List<LinkViewModel> SnapshotSelectedLinks()
    {
        var list = new List<LinkViewModel>();
        if (_vm is null) return list;
        foreach (var l in _vm.Links) if (l.IsSelected) list.Add(l);
        return list;
    }

    private List<FrameViewModel> SnapshotSelectedFrames()
    {
        var list = new List<FrameViewModel>();
        if (_vm is null) return list;
        foreach (var f in _vm.Frames) if (f.IsSelected) list.Add(f);
        return list;
    }

    private void ApplyLinkSelection(IReadOnlyList<LinkViewModel> selected)
    {
        if (_vm is null) return;
        //  Pre-fix this method ran an unconditional "every non-picked
        // wire → IsSelected = false" pre-loop AND then SetSelectedLinks, which
        // internally also clears every currently-selected wire's flag. For
        // wires that were neither in the prior nor new selection the pre-loop
        // wrote IsSelected=false to a wire whose flag was already false —
        // benign but per-frame at 120 Hz. SetSelectedLinks already covers the
        // "was selected, now isn't" transition because LogicCanvasViewModel
        // walks the existing SelectedLinks and clears each before refilling.
        // Drop the pre-loop entirely; the SetSelectedLinks path is the single
        // source of truth.
        _vm.SetSelectedLinks(selected);
    }

    private void ApplyFrameSelection(IReadOnlyList<FrameViewModel> selected)
    {
        if (_vm is null) return;
        //  Same rationale as ApplyLinkSelection — SetSelectedFrames
        // already clears the prior selection flags before refilling, so the
        // pre-loop is redundant per-frame work during a marquee drag.
        _vm.SetSelectedFrames(selected);
    }

    private bool HasAnyLinkOrFrameSelected()
    {
        if (_vm is null) return false;
        foreach (var l in _vm.Links)  if (l.IsSelected) return true;
        foreach (var f in _vm.Frames) if (f.IsSelected) return true;
        return false;
    }

    private static bool RectsIntersect(Rect a, Rect b)
        => a.X < b.X + b.Width && a.X + a.Width > b.X
        && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    /// <summary>
    ///  Drop the lazily-allocated marquee overlay Rectangle from
    /// OverlayLayer.Children on canvas tear-down. Pre-fix the overlay was
    /// added to OverlayLayer the first time BeginMarquee ran and never
    /// removed — only Collapsed. With 0.10.0 multi-window Architect each
    /// SubGraphWindow gets its own LogicCanvasView and would leak one
    /// Rectangle into its OverlayLayer's Children forever. Detach + null
    /// on Unloaded so the next Loaded re-allocates lazily on demand.
    /// </summary>
    private void DetachMarqueeOverlayOnUnload()
    {
        if (_marqueeOverlay is null) return;
        try { OverlayLayer.Children.Remove(_marqueeOverlay); }
        catch { /* layer already disposed — best effort */ }
        _marqueeOverlay = null;
    }

    private MarqueeMode ResolveMarqueeMode()
    {
        bool shift = ModifierDown(VirtualKey.Shift);
        bool ctrl  = ModifierDown(VirtualKey.Control);
        bool alt   = ModifierDown(VirtualKey.Menu);   // Alt
        if (alt)   return MarqueeMode.Remove;
        if (ctrl)  return MarqueeMode.Toggle;
        if (shift) return MarqueeMode.Add;
        return MarqueeMode.Replace;
    }

    private static bool ModifierDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    // ─── Group drag helpers ───────────────────────────────────────────────

    private void BeginGroupDragIfMulti(NodeViewModel pivot, Point canvasStart)
    {
        if (_vm is null) return;
        _groupDragStarts.Clear();
        if (_vm.SelectedNodes.Count > 1 && _vm.SelectedNodes.Contains(pivot))
        {
            foreach (var n in _vm.SelectedNodes)
                _groupDragStarts.Add((n, n.X, n.Y));
        }
    }

    private bool TryUpdateGroupDrag(NodeViewModel pivot, Point canvasStart, Point canvasNow)
    {
        if (_vm is null || _groupDragStarts.Count == 0) return false;
        double dx = canvasNow.X - canvasStart.X;
        double dy = canvasNow.Y - canvasStart.Y;
        foreach (var entry in _groupDragStarts)
        {
            double targetX = entry.X + dx;
            double targetY = entry.Y + dy;
            _vm.TranslateNode(entry.Node, targetX - entry.Node.X, targetY - entry.Node.Y);
        }
        return true;
    }

    // arch-perf P0-4 — group-drag coalesce (mirrors the frame-content-drag
    // RequestFrameContentDragUpdate / FlushFrameContentDragIfDirty pair). The
    // pointer-move + edge-pan handlers stash the latest cursor canvas-point and
    // flip the dirty flag instead of running the N-node TranslateNode loop
    // inline; OnRenderingTick drains it at most once per displayed frame.
    private void RequestGroupDragUpdate(Point canvasNow)
    {
        _groupDragLatestCanvas = canvasNow;
        _groupDragDirty = true;
    }

    // Drained from OnRenderingTick and flushed once more on PointerReleased
    // (before undo) so the group lands exactly under the cursor. Applies the
    // absolute delta from drag-start to the latest stashed point — identical
    // final positions to the old inline path, only temporally coalesced.
    internal void FlushGroupDragIfDirty()
    {
        if (!_groupDragDirty) return;
        _groupDragDirty = false;
        if (_dragNode is not null)
            TryUpdateGroupDrag(_dragNode, _nodeDragStartCanvas, _groupDragLatestCanvas);
    }

    private void EndGroupDrag()
    {
        _groupDragStarts.Clear();
        _groupDragDirty = false;
    }
}
