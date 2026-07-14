using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Foundation;
using Windows.UI;

using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// ─── Immediate-mode GPU canvas renderer ────────
//
// THE PERMANENT FIX for the large-graph death-freeze. The retained path (LOD +
// viewport cull + wire-virt, shipped 0.12.41–0.12.44) hit its honest ceiling:
// at readable editing zoom EVERY on-screen node is a full ~65–149-element
// WinUI NodeView, and the framework's synchronous measure/arrange/render walk
// over that tree IS the freeze. Retained-mode WinUI cannot make a readable,
// inline-socket node cheaper than ~100 elements, so the only way past the
// ceiling is to stop realizing a visual tree per node.
//
// This renderer draws the WHOLE visible graph — grid, wires, nodes, selection —
// as Direct2D primitives in a single CanvasControl.Draw pass. Cost becomes
// O(visible draws), flat, whether the graph is 50 nodes or 5,000. The pan/zoom
// transform is baked into the draw session (NOT a XAML RenderTransform) so
// DirectWrite text stays crisp at any zoom.
//
// STAGING (no silent regression):
//   • _useImmediateMode defaults OFF. The retained ViewSurface path is the
//     shipping default until Majo confirms run-app parity.
//   • Ctrl+Alt+F7 toggles it (mirrors the Ctrl+Alt+F6 virtualization kill-
//     switch idiom). ON hides the retained paint layers + shows ImmediateCanvas.
//   • Reads the SAME view-models + NodeGeometry the retained path renders from,
//     so the two never diverge on layout/anchor math.
//
// PHASE 0 (this file) = the read-only render path: prove the freeze is gone and
// the graph reads correctly. Interaction in immediate mode (model-space hit-
// test, the live-edit overlay) is the next phase; today, toggling immediate
// mode is a view that still pans/zooms (HostRoot handlers + _vm.Zoom/Pan drive
// the draw) but defers per-node editing to the retained path.
//
// This paint code lives in Architect.WinUI and is never lifted to Shared —
// Visualist would get its own copy if/when its WidgetGraphCanvas needs the
// same treatment.
public sealed partial class LogicCanvasView
{
    // DEFAULT — the GPU canvas IS the Architect canvas. The retained NodeView path
    // survives only as an automatic crash-fallback (ActivateImmediateModeDefault
    // flips this to false if Win2D can't initialise on a given machine/build) — not
    // a user opt-in. Ctrl+Alt+F7 is an emergency escape hatch to the legacy path.
    private bool _useImmediateMode = true;
    private bool _immediateHooked;
    private bool _immediateActivated;

    // The one node currently materialized as a real, editable NodeView over
    // the GPU canvas (double-tap to enter). The renderer skips drawing it so the
    // live control isn't double-painted; all inline editing (pills, rename,
    // middle-attrs) is the real NodeView's own, verbatim.
    private NodeViewModel? _editNode;

    // Hover feedback. In the retained path wire/node hover came from XAML
    // pointer events on per-element Paths/NodeViews; immediate mode has none, so
    // the idle-move handler resolves the hovered node/wire from the model.
    private NodeViewModel? _hoverNode;
    private LinkViewModel? _hoverLink;

    // Idle-hover resolve cache. PointerMoved fires at mouse report rate while
    // the cursor rests over the canvas, and ResolveModelHit walks every node
    // plus a 25-sample bezier per link — re-resolving on sub-pixel jitter is
    // pure waste. The cached hit is reused until the cursor travels
    // IdleHoverRethreshScreenPx (screen-space, zoom-compensated) from the last
    // resolve; MarkSceneDirty invalidates it on any scene change so a hover
    // can't go stale under a still cursor after the scene shifts beneath it.
    private bool _idleHitValid;
    private Point _idleHitPoint;
    private object? _idleHit;
    private const double IdleHoverRethreshScreenPx = 1.5;

    // Scene-dirty gate for the per-frame GPU repaint. OnRenderingTick used to
    // call InvalidateImmediate unconditionally, so a fully idle canvas re-ran
    // the whole Draw pass at frame cadence forever. Every visual-affecting
    // source now calls MarkSceneDirty and the tick only invalidates while the
    // counter is live. The counter is a small grace window (a few frames past
    // the last change) so late-arriving layout/measure effects of a change
    // still paint — an extra frame is invisible, a missed repaint is a bug.
    // Volatile write + CAS decrement because callers may mark from off the UI
    // thread; the render tick is the only decrementer.
    private int _sceneDirtyFrames = SceneDirtyGraceFrames;
    private const int SceneDirtyGraceFrames = 3;

    internal void MarkSceneDirty()
    {
        System.Threading.Volatile.Write(ref _sceneDirtyFrames, SceneDirtyGraceFrames);
        // The scene shifted — a cached idle-hover hit may no longer match what
        // sits under the (possibly still) cursor; re-resolve on the next move.
        _idleHitValid = false;
        _idleHit = null;
    }

    /// <summary>
    /// True while the scene-dirty grace window is live; consumes one grace
    /// frame per call. Called exactly once per render tick.
    /// </summary>
    private bool ConsumeSceneDirtyFrame()
    {
        int frames = System.Threading.Volatile.Read(ref _sceneDirtyFrames);
        if (frames <= 0) return false;
        // CAS so a concurrent MarkSceneDirty refresh always wins over the decrement.
        System.Threading.Interlocked.CompareExchange(ref _sceneDirtyFrames, frames - 1, frames);
        return true;
    }

    // Lazily-built DirectWrite formats (created on first draw, rebuilt never —
    // font metrics are process-scoped, mirroring NodeGeometry's text cache).
    private CanvasTextFormat? _imTitleFormat;
    private CanvasTextFormat? _imDefinerFormat;
    private CanvasTextFormat? _imLabelFormat;
    private CanvasTextFormat? _imLabelRightFormat;
    private CanvasTextFormat? _imPillFormat;
    private CanvasTextFormat? _imPillFormatWrap; // multi-line wrapped value pills

    // Palette resolved once from the active theme dictionary (re-resolved on a
    // runtime theme switch via RefreshImmediateThemeColors). Fallbacks mirror
    // the Coal/Ember tokens so a pre-app-resources draw still reads correctly.
    private Color _imBodyFill     = Color.FromArgb(0xFF, 0x26, 0x22, 0x1E);
    private Color _imHeaderFallbk = Color.FromArgb(0xFF, 0x2A, 0x26, 0x22);
    private Color _imBorder       = Color.FromArgb(0xFF, 0x3A, 0x31, 0x27);
    private Color _imSelection    = Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E);
    private Color _imError        = Color.FromArgb(0xFF, 0xCB, 0x4D, 0x3F);
    private Color _imText         = Color.FromArgb(0xFF, 0xEA, 0xE2, 0xD6);
    private Color _imSubText      = Color.FromArgb(0xFF, 0xA8, 0x96, 0x83);
    private Color _imGridDot      = Color.FromArgb(0x80, 0x3A, 0x31, 0x27);
    // Node-state border tints (mirror NodeView.ApplyBorder's priority cascade).
    private Color _imFlash        = Color.FromArgb(0xFF, 0xFF, 0xD2, 0x4A); // live-trace execution pulse
    private Color _imVarWriter    = Color.FromArgb(0xFF, 0x4E, 0xC9, 0xE5); // var-chain writer (cyan)
    private Color _imVarReader    = Color.FromArgb(0xFF, 0xE5, 0xC2, 0x6A); // var-chain reader (amber)
    private Color _imPillFill     = Color.FromArgb(0xFF, 0x1E, 0x1B, 0x17); // inline value-pill background
    private bool _imColorsResolved;

    // ── Default activation (called once from OnLoaded) ──
    // The GPU canvas is the default. If Win2D can't initialise on this machine /
    // build (e.g. an AnyCPU build with no native DLL, or no usable GPU and WARP
    // unavailable), auto-fall back to the retained path so Architect still works
    // — degraded, logged, never a black canvas.
    private void ActivateImmediateModeDefault()
    {
        if (_immediateActivated) return;
        _immediateActivated = true;
        if (!_useImmediateMode) return;
        try
        {
            ApplyImmediateModeState();
            // The GPU canvas draws nodes + sockets as
            // pixels, so the per-pin TooltipPopup wiring the retained NodeView path
            // uses (NodeView.Pins) never existed here — hover tooltips silently
            // stopped working once the Win2D canvas became the default. Attach ONE
            // dynamic resolver to HostRoot (the pointer-handling surface whose
            // coordinate space HostToCanvas expects); it resolves the socket / node
            // under the cursor and drives the EXISTING TooltipPopup dwell/show/hide
            // machinery. Auto-detaches on HostRoot.Unloaded.
            if (HostRoot is not null)
                Phoenix.Controls.Architect.WinUI.Controls.TooltipPopup.AttachDynamic(
                    HostRoot, ResolveImmediateTooltip);
        }
        catch (Exception ex)
        {
            _useImmediateMode = false;
            try { ApplyImmediateModeState(); } catch { /* retained path is the baseline */ }
            GlobalLogger.Error("Architect.LogicCanvasView.Win2D",
                "Immediate-mode canvas failed to initialise — fell back to the retained canvas", ex);
        }
    }

    // ── Emergency escape hatch (Ctrl+Alt+F7, LogicCanvasView.Keyboard.cs) ──
    // NOT how you get the GPU canvas — that's the default. This toggles to the
    // legacy retained path if something ever goes wrong, and back.
    internal void ToggleImmediateMode() => SetImmediateMode(!_useImmediateMode);

    private void SetImmediateMode(bool on)
    {
        _useImmediateMode = on;
        ApplyImmediateModeState();
    }

    // Apply the visual-tree + render state for the CURRENT _useImmediateMode.
    // Idempotent; safe at startup and on every toggle.
    private void ApplyImmediateModeState()
    {
        bool on = _useImmediateMode;

        // The retained paint layers live inside ViewSurface; collapse just those
        // five so the host-space overlays (marquee, drop hint, minimap, welcome)
        // AND the HostRoot pointer handlers (pan / zoom) keep working untouched.
        var retained = on ? Visibility.Collapsed : Visibility.Visible;
        if (GridLayer is not null)           GridLayer.Visibility           = retained;
        if (FrameLayer is not null)          FrameLayer.Visibility          = retained;
        if (LinkLayer is not null)           LinkLayer.Visibility           = retained;
        if (FlowDecorLayer is not null)      FlowDecorLayer.Visibility      = retained;
        if (DanglingMarkerLayer is not null) DanglingMarkerLayer.Visibility = retained;
        // NodeLayer stays VISIBLE — in immediate mode it hosts only the single
        // materialized NodeView for the node being edited; the GPU canvas
        // draws every other node.

        if (ImmediateCanvas is not null)
            ImmediateCanvas.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            EnsureImmediateHooked();
            ResolveImmediateThemeColors();
            ClearRetainedNodeMounts();   // empty NodeLayer — the GPU path renders nodes now
            MarkSceneDirty();
            ImmediateCanvas?.Invalidate();
        }
        else
        {
            ClearImmediateHover();
            ExitImmediateEdit();
            ForceFullRealization();      // remount NodeViews for the retained path
        }
    }

    private void EnsureImmediateHooked()
    {
        if (_immediateHooked || ImmediateCanvas is null) return;
        ImmediateCanvas.Draw += OnImmediateDraw;
        // Cached CanvasGeometry must never survive a device (re)create —
        // drop every geometry cache whenever Win2D rebuilds its resources
        // (device-lost recovery included); they rebuild lazily on next draw.
        ImmediateCanvas.CreateResources += OnImmediateCreateResources;
        _immediateHooked = true;
    }

    private void OnImmediateCreateResources(
        CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        => ReleaseDeviceGeometryCaches();

    // ─── Inline-edit overlay ────────────────────────────────────────────
    //
    // Materialize ONE real, fully-interactive NodeView over the GPU canvas for the
    // node being edited (double-tap to enter). It's mounted into NodeLayer (which
    // shares the pan/zoom transform) so it sits exactly over its GPU-drawn
    // counterpart; the renderer skips that node. All inline editing — click-to-
    // edit pills, socket rename, middle-attr toggles — is the NodeView's own, so
    // the Blueprints UX is preserved verbatim. A press anywhere that isn't an
    // inline affordance exits edit mode (OnHostPointerPressed).
    private void EnterImmediateEdit(NodeViewModel? vm)
    {
        if (!_useImmediateMode || vm is null) return;
        // Same-node early-return ONLY when the edit view is genuinely mounted.
        // Several paths empty NodeLayer without exiting edit mode explicitly —
        // a pillar-tab / window re-attach re-runs ApplyImmediateModeState →
        // ClearRetainedNodeMounts, a graph reset / DataContext rebind runs
        // ClearNodeViews — and a flag-only check then poisoned this method:
        // the view was never re-mounted, the renderer kept skipping _editNode,
        // and the (detached) editor still reported focus success, so the FIRST
        // pill edit after re-entering a window made the node vanish with no
        // editor until a click-away reset _editNode (user report: "the first text
        // edit of a pill in a window always causes the node to disappear").
        // Those clear paths now exit edit mode themselves; this state check is
        // the by-construction backstop for any future clear path.
        if (ReferenceEquals(_editNode, vm)
            && _nodeViews.TryGetValue(vm, out var mounted)
            && NodeLayer.Children.Contains(mounted))
            return;
        ExitImmediateEdit();
        _editNode = vm;
        // The GPU renderer bakes the live VM pan/zoom into every frame, but the
        // mounted NodeView renders through ViewSurface's CompositeTransform. Force
        // the two into agreement at the mount — with a stale transform (a same-VM
        // graph load wrote PanX/PanY/Zoom and no gesture ran since) the view
        // materializes away from its GPU-drawn twin while the renderer stops
        // drawing the node, i.e. the node teleports or vanishes on the first edit
        // after opening a file. Idempotent when already in sync.
        ApplyViewTransform();
        var view = EnsureFullView(vm);          // reuse the LOD lazy-builder + cache
        view._isCulling = false;
        // Stamp AFTER EnsureFullView — a fresh view's DataContext set clears the
        // cached-canvas slot. Guarantees the inline editors' commit tail (undo +
        // EventName adopt/pair-sync + label cross-file sync) can reach this canvas
        // even when the commit fires AFTER ExitImmediateEdit detached the view
        // (click-away on the GPU canvas = LostFocus on a severed visual chain).
        view.StampOwnerCanvas(this);
        XamlCanvas.SetLeft(view, vm.X);
        XamlCanvas.SetTop(view, vm.Y);
        if (!NodeLayer.Children.Contains(view)) NodeLayer.Children.Add(view);
        _realizedNodes.Add(vm);                 // track so it's removed consistently
        MarkSceneDirty();                       // grace frames cover the mount's follow-on measure
        ImmediateCanvas?.Invalidate();          // renderer now skips _editNode
    }

    private void ExitImmediateEdit()
    {
        if (_editNode is null) return;
        var vm = _editNode;
        _editNode = null;
        if (_nodeViews.TryGetValue(vm, out var view))
        {
            view._isCulling = true;             // suppress the unconditional Unloaded teardown
            NodeLayer.Children.Remove(view);
        }
        _realizedNodes.Remove(vm);
        _nodeRowHeightsCache.Remove(vm);        // sockets/pills may have been edited while mounted
        NodeGeometry.MarkGeometryDirty(vm.Id);  // static geometry gate — same reason
        MarkSceneDirty();
        ImmediateCanvas?.Invalidate();          // GPU path draws the node again
    }

    // ─── Direct single-click inline-pill edit ──
    //
    // The GPU canvas draws every node's inline value pills as Direct2D primitives,
    // so there is no per-pill XAML element to receive a click. Pre-fix the only way
    // to edit a pill was: double-tap the NODE to materialize its real
    // NodeView, THEN click the pill — Majo: "you first have to double-click the node
    // to even get the option to edit those pills". This resolves the editable pill
    // under the cursor straight from the model (mirroring the EXACT row/pill geometry
    // DrawNodeBody / DrawValuePill / DrawMiddleAttributes paint), then materializes the
    // node's NodeView AND opens that pill's editor in a SINGLE click — the pill's
    // TextBox is the NodeView's own, so the Blueprints edit chrome is preserved
    // verbatim. The editor is realized + focused SYNCHRONOUSLY in this pointer event
    // (BeginInlinePillEdit → NodeView.FocusInlineEditorFor), because a freshly-
    // materialized NodeView hasn't laid out yet and merely flipping IsEditing left the
    // box neither shown nor focused on click one (Majo: "still have to double click to
    // see the editing part"). Returns true when a pill was hit + opened.
    //
    // Called from OnHostPointerPressed before the wire-drop / node-drag branches; a
    // pill hit sits mid-body (pins straddle the node edges) so it never competes with
    // a genuine wire-drag. Value pills (input rows), TEXT middle-attribute pills, and
    // BOOL middle-attribute checkmark rows (`☑ Key` — toggled in place, no NodeView
    // materialization needed) are handled; socket-label rename stays on the
    // double-tap path.
    private bool TryBeginInlineEditAtCanvasPoint(NodeViewModel node, Point canvasPoint)
    {
        if (_vm is null || node.IsReroute || node.HasCompactSymbol) return false;
        double nx = node.X, ny = node.Y, nw = node.Width;
        double x = canvasPoint.X, y = canvasPoint.Y;

        // Hit rects are computed by the SAME helpers the renderer uses
        // (ComputeInputPillRect + the cached RowCenterForSocket path), so the
        // clickable target always matches the painted pill — including confined
        // (non-overlapping) and multi-line grown rows.
        var rowHeights = GetRowHeightsCached(node);
        double RowHeightAt(int idx) => (idx >= 0 && idx < rowHeights.Length) ? rowHeights[idx] : NodeGeometry.SocketRowHeight;

        // 1) Input value pills.
        foreach (var s in node.Inputs)
        {
            if (!s.IsPillVisible || !s.IsEditablePill) continue;
            double rowCY = ny + RowCenterForSocket(node, s);
            var (px, py, pw, ph, _) = ComputeInputPillRect(nx, nw, rowCY, RowHeightAt(s.RowIndex), s, node);
            if (x >= px && x <= px + pw && y >= py - 2 && y <= py + ph + 2)
            {
                BeginInlinePillEdit(node, s.BeginValuePillEdit, s);
                return true;
            }
        }

        // 2) Middle-attribute rows — same area math as DrawMiddleAttributes
        //    (dynamic socket-rows offset). TEXT pills (Key …… [pill]) open the
        //    inline editor; BOOL checkmark rows (`☑ Key`) toggle in place.
        if (node.HasMiddleAttributes)
        {
            double socketRowsTotal = 0;
            foreach (var h in rowHeights) socketRowsTotal += h;
            double yTop = ny + NodeGeometry.FirstSocketRowY(node.Model)
                        + socketRowsTotal
                        + NodeGeometry.MiddleAttrAreaTopPad
                        + NodeGeometry.MiddleAttrDividerH;
            double mpX = nx + nw * 0.42;
            double mpW = Math.Max(10.0, nx + nw - 8 - mpX);
            foreach (var m in node.MiddleAttributes)
            {
                // Per-row height mirrors DrawMiddleAttributes so a grown (wrapped)
                // Template / Script / Payload row stays clickable across its full
                // height and the rows below it aren't offset.
                double rowH  = NodeGeometry.MiddleAttrRowHeightFor(node.Model, m.Key, m.Value);
                double rowCY = yTop + rowH / 2.0;
                double half  = Math.Max(11.0, rowH / 2.0);
                if (m.IsBool)
                {
                    // Bool checkmark row (`☑ Key`, left-aligned) — toggled DIRECTLY
                    // in canvas-space, one click, no NodeView materialization: a
                    // toggle has no editor session to realize, so the pill-edit
                    // machinery would be pure overhead. Hit rect mirrors the
                    // painted glyph+label run in DrawMiddleAttributes (8 left pad
                    // + 18 glyph + 6 gap + label) with a little slack on the right
                    // so the tap target isn't pixel-tight. Same commit tail as
                    // the retained NodeView.OnMiddleAttrBoolTapped: toggle →
                    // undo push → OnGraphMutated (the Chat.Message platform
                    // checkmarks change the exported `on_chat` header, so the
                    // graph must flag dirty) → GPU redraw.
                    double labelW = NodeGeometry.EstimateTextWidth(m.Key, 12.0);
                    double bLeft  = nx + 8;
                    double bRight = nx + 8 + 18 + 6 + labelW + 12;
                    if (x >= bLeft && x <= bRight && y >= rowCY - half && y <= rowCY + half)
                    {
                        if (m.ToggleBool())
                        {
                            PushUndoForInlineEdit();
                            _vm.OnGraphMutated();
                            ImmediateCanvas?.Invalidate();
                        }
                        return true;
                    }
                }
                else if (x >= mpX && x <= mpX + mpW && y >= rowCY - half && y <= rowCY + half)
                {
                    BeginInlinePillEdit(node, m.BeginEdit, m);
                    return true;
                }
                yTop += rowH;
            }
        }

        return false;
    }

    // Materialize the node's NodeView, flip the target into edit mode, then —
    // the step the first single-click fix was missing — SYNCHRONOUSLY realize +
    // focus the editor TextBox in THIS pointer event (NodeView.FocusInlineEditorFor =
    // UpdateLayout + Focus + SelectAll), mirroring the retained OnPillTapped path.
    // Without it, the just-materialized NodeView hadn't laid out, so flipping IsEditing
    // showed nothing and the user had to click again to actually edit.
    private void BeginInlinePillEdit(NodeViewModel node, System.Action beginEdit, object editTarget)
    {
        EnterImmediateEdit(node);          // materialize the real NodeView over the GPU canvas
        beginEdit();                        // IsEditing=true → editor TextBox Visibility flips Visible
        bool focused = _nodeViews.TryGetValue(node, out var view) && view.FocusInlineEditorFor(editTarget);
        ImmediateCanvas?.Invalidate();

        //  (temporary, Debug) — discriminates the residual
        // "node vanishes on first pill edit" reports at the paint level. The
        // prior PILL-EDIT-DIAG proved materialize+focus SUCCEED in the failing
        // sessions, so what's left is where/whether the mounted view renders:
        // in-tree state, actual size after the synchronous UpdateLayout, the
        // ViewSurface transform vs the live VM pose (stale-transform check),
        // and the layer visibilities. One line per pill edit; strip once the
        // symptom is confirmed dead in-app.
        if (view is not null)
        {
            double dx = Math.Abs(ViewTransform.TranslateX - (_vm?.PanX ?? 0));
            double dy = Math.Abs(ViewTransform.TranslateY - (_vm?.PanY ?? 0));
            double dz = Math.Abs(ViewTransform.ScaleX - (_vm?.Zoom ?? 1));
            GlobalLogger.Log(
                $" '{node.Title}' mounted={NodeLayer.Children.Contains(view)} "
                + $"layerChildren={NodeLayer.Children.Count} focused={focused} "
                + $"viewSize={view.ActualWidth:0}x{view.ActualHeight:0} vis={view.Visibility}/{ViewSurface.Visibility} "
                + $"xfDrift={dx:0.#},{dy:0.#},{dz:0.###} pos=({XamlCanvas.GetLeft(view):0},{XamlCanvas.GetTop(view):0}) vm=({node.X:0},{node.Y:0})",
                "Architect.LogicCanvasView.Win2D", LogLevel.Debug);
        }
    }

    // Remove every cull/proxy-mounted NodeView from NodeLayer when immediate mode
    // takes over (the GPU path renders nodes), keeping the cached views + the VM
    // PropertyChanged subscriptions intact so a toggle back re-realizes cleanly.
    private void ClearRetainedNodeMounts()
    {
        // An open edit session cannot survive a mount reset. On the CURRENT
        // call paths (startup activation via ActivateImmediateModeDefault —
        // gated by _immediateActivated — and the Ctrl+Alt+F7 toggle-on, whose
        // toggle-off already exited) _editNode is always null here, so this is
        // a no-op invariant assert; it exists so any FUTURE caller that resets
        // the mounts mid-edit can't leave _editNode dangling (the renderer
        // would keep skipping the node while its view is gone — the "node
        // vanishes on the first pill edit" class).
        ExitImmediateEdit();
        if (_realizedNodes.Count > 0)
        {
            var views = new System.Collections.Generic.List<NodeView>(_realizedNodes.Count);
            foreach (var vm in _realizedNodes)
                if (_nodeViews.TryGetValue(vm, out var v)) { v._isCulling = true; views.Add(v); }
            foreach (var v in views) NodeLayer.Children.Remove(v);
            _realizedNodes.Clear();
        }
        if (_realizedProxy.Count > 0)
        {
            var proxies = new System.Collections.Generic.List<Border>(_realizedProxy.Count);
            foreach (var vm in _realizedProxy)
                if (_nodeProxies.TryGetValue(vm, out var p)) proxies.Add(p);
            foreach (var p in proxies) NodeLayer.Children.Remove(p);
            _realizedProxy.Clear();
        }
    }

    // One idle-move scene resolve shared by the frame-edge cursor and the
    // hover highlight. Pre-merge each ran its own full ResolveModelHit walk
    // per PointerMoved; both consume the SAME hit for the SAME canvas point,
    // so a single (cached) resolve is behavior-identical.
    private void UpdateIdleHoverWin2D(Point canvasPoint)
    {
        var hit = ResolveModelHitCached(canvasPoint);
        // FrameEdgeAt stays fresh per move — only the scene walk is cached —
        // so the resize cursor still tracks the exact edge band under the cursor.
        var edge = hit is FrameViewModel f
            ? FrameEdgeAt(f, canvasPoint.X, canvasPoint.Y, FrameResizeBand())
            : FrameResizeEdge.None;
        ApplyFrameEdgeCursor(edge);
        ApplyImmediateHover(hit);
    }

    // ResolveModelHit with the idle-move short-circuit: while the cursor stays
    // within a ~1.5 screen-px radius of the last resolve (and the scene hasn't
    // changed — MarkSceneDirty invalidates), the previous hit is reused.
    private object? ResolveModelHitCached(Point canvasPoint)
    {
        if (_idleHitValid)
        {
            double zoom = Math.Max(_vm?.Zoom ?? 1.0, 0.0001);
            double thr = IdleHoverRethreshScreenPx / zoom; // keep the radius ~constant on screen
            double dx = canvasPoint.X - _idleHitPoint.X;
            double dy = canvasPoint.Y - _idleHitPoint.Y;
            if (dx * dx + dy * dy < thr * thr) return _idleHit;
        }
        _idleHit = ResolveModelHit(canvasPoint);
        _idleHitPoint = canvasPoint;
        _idleHitValid = true;
        return _idleHit;
    }

    // Apply the resolved hover target (node/wire highlight) — split from the
    // resolve so the idle path can share one hit with the frame-edge cursor.
    private void ApplyImmediateHover(object? hit)
    {
        if (!_useImmediateMode || _vm is null) return;
        var newNode = hit as NodeViewModel;
        var newLink = hit as LinkViewModel;
        bool dirty = false;
        if (!ReferenceEquals(newNode, _hoverNode)) { _hoverNode = newNode; dirty = true; }
        if (!ReferenceEquals(newLink, _hoverLink))
        {
            if (_hoverLink is not null) _hoverLink.IsHovered = false;
            _hoverLink = newLink;
            if (_hoverLink is not null) _hoverLink.IsHovered = true;
            dirty = true;
        }
        if (dirty) ImmediateCanvas?.Invalidate();
    }

    private void ClearImmediateHover()
    {
        _idleHitValid = false;
        _idleHit = null;
        bool dirty = _hoverNode is not null || _hoverLink is not null;
        _hoverNode = null;
        if (_hoverLink is not null) { _hoverLink.IsHovered = false; _hoverLink = null; }
        if (dirty) ImmediateCanvas?.Invalidate();
    }

    /// <summary>
    /// Request an immediate-canvas repaint. Cheap no-op when immediate mode is
    /// off. Called from OnRenderingTick while the scene-dirty grace window is
    /// live (MarkSceneDirty sources + the live gesture/coalescer flags), so
    /// pan / zoom / drag repaint at frame cadence while a fully idle canvas
    /// skips the Draw pass entirely; CanvasControl.Invalidate internally
    /// coalesces multiple calls into one Draw per frame.
    /// </summary>
    private void InvalidateImmediate()
    {
        if (_useImmediateMode) ImmediateCanvas?.Invalidate();
    }

    /// <summary>
    /// Resolve the rich hover tooltip (title / body) for
    /// whatever socket or node sits under the cursor on the immediate GPU canvas.
    /// Socket pins take priority over the node body; bare canvas returns an empty
    /// title which suppresses the tip. <paramref name="hostPoint"/> is HostRoot-
    /// local (the element the resolver is attached to via TooltipPopup.AttachDynamic),
    /// so it maps to canvas space through the same HostToCanvas the pointer pipeline
    /// uses. Mirrors the socket/node tooltip content the retained NodeView path shows.
    /// </summary>
    private (string title, string? body, string? glyph) ResolveImmediateTooltip(Windows.Foundation.Point hostPoint)
    {
        if (!_useImmediateMode || _vm is null) return (string.Empty, null, null);
        var canvasPoint = HostToCanvas(hostPoint);

        // Socket pin first — the smaller, more specific target.
        var sock = ResolveSocketAtCanvasPoint(canvasPoint, WireDropModelHitRadius);
        if (sock is not null) return SplitTooltip(sock.Tooltip, sock.Label);

        // Node body otherwise.
        if (ResolveModelHit(canvasPoint) is NodeViewModel node)
            return SplitTooltip(node.NodeTooltip, node.Title);

        return (string.Empty, null, null); // bare canvas → suppress
    }

    // Split a "Title\nBody…" tooltip string into the TooltipPopup (title, body)
    // pair (no glyph). Empty input falls back to <paramref name="fallbackTitle"/>.
    private static (string title, string? body, string? glyph) SplitTooltip(string? text, string? fallbackTitle)
    {
        string s = text ?? string.Empty;
        int nl = s.IndexOf('\n');
        string title = nl >= 0 ? s.Substring(0, nl) : s;
        string? body = nl >= 0 ? s.Substring(nl + 1) : null;
        if (string.IsNullOrEmpty(title)) title = fallbackTitle ?? string.Empty;
        return (title, body, null);
    }

    /// <summary>
    /// Re-resolve the immediate palette + repaint after a runtime OS theme /
    /// high-contrast switch. Called from OnCanvasActualThemeChanged alongside the
    /// retained per-link / proxy refresh so every render path flips theme together.
    /// </summary>
    private void RefreshImmediateThemeColors()
    {
        if (!_useImmediateMode) return;
        _imColorsResolved = false;
        ResolveImmediateThemeColors();
        MarkSceneDirty();
        ImmediateCanvas?.Invalidate();
    }

    private void ResolveImmediateThemeColors()
    {
        if (_imColorsResolved) return;
        _imBodyFill     = ResolveThemeColor("CoalRaisedBrush",         _imBodyFill);
        _imHeaderFallbk = ResolveThemeColor("CoalRaisedBrush",         _imHeaderFallbk);
        _imBorder       = ResolveThemeColor("CoalDividerBrush",        _imBorder);
        _imSelection    = ResolveThemeColor("EmberPrimaryBrush",       _imSelection);
        _imError        = ResolveThemeColor("ErrBrush",                _imError);
        _imText         = ResolveThemeColor("CoalPaperBrush",          _imText);
        _imSubText      = ResolveThemeColor("CoalSecondaryTextBrush",  _imSubText);
        var grid        = ResolveThemeColor("CoalDividerBrush",        _imGridDot);
        _imGridDot      = Color.FromArgb(0x80, grid.R, grid.G, grid.B); // 50% like the retained GridLayer Opacity=0.5
        _imPillFill     = ResolveThemeColor("CoalBaseBrush",           _imPillFill);
        _imColorsResolved = true;
    }

    private static Color ResolveThemeColor(string brushKey, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(brushKey, out var v)
                && v is SolidColorBrush scb)
                return scb.Color;
        }
        catch { /* theme not ready — use the literal fallback */ }
        return fallback;
    }

    // ─── The single Draw pass ────────────────────────────────────────────────
    private void OnImmediateDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_useImmediateMode || _vm is null) return;

        try
        {
            ResolveImmediateThemeColors();
            var ds = args.DrawingSession;
            EnsureTextFormats(ds);

            // DPI flip (monitor move / scale change) → drop the cached
            // geometry so nothing device-scaled survives; every cache
            // rebuilds lazily within this same pass.
            float dpi = sender.Dpi;
            if (dpi != _imLastDpi)
            {
                _imLastDpi = dpi;
                ReleaseDeviceGeometryCaches();
            }

            double zoom = _vm.Zoom <= 0 ? 1.0 : _vm.Zoom;
            double panX = _vm.PanX;
            double panY = _vm.PanY;

            // Bake the canvas pan/zoom into the draw session: screenPt = contentPt*zoom + pan.
            ds.Transform = Matrix3x2.CreateScale((float)zoom)
                         * Matrix3x2.CreateTranslation((float)panX, (float)panY);

            // Visible content rect (inverse transform of the control bounds) +
            // a margin so wires/nodes straddling the edge still draw.
            double w = sender.ActualWidth, h = sender.ActualHeight;
            const double margin = 240.0;
            double vl = (0 - panX) / zoom - margin;
            double vt = (0 - panY) / zoom - margin;
            double vr = (w - panX) / zoom + margin;
            double vb = (h - panY) / zoom + margin;
            var visible = new Rect(vl, vt, Math.Max(0, vr - vl), Math.Max(0, vb - vt));

            DrawGrid(ds, vl, vt, vr, vb, zoom);

            // Paint order mirrors the retained layers: frames < wires < nodes.
            foreach (var frame in _vm.Frames)
                DrawFrame(ds, frame, visible);

            foreach (var link in _vm.Links)
                DrawWire(sender, ds, link, visible);

            foreach (var node in _vm.Nodes)
            {
                if (ReferenceEquals(node, _editNode)) continue;   // drawn by its live NodeView
                if (!RectsOverlap(node.X, node.Y, node.Width, node.Height, visible)) continue;
                DrawNode(ds, node);
            }

            // In-flight wire-drop preview (dashed, on top of everything) — the
            // retained WirePreview Path lives in the collapsed ViewSurface, so the
            // immediate path draws it from the live drag state instead.
            DrawWirePreview(sender, ds);
        }
        catch (Exception ex)
        {
            // Never let a draw crash the shared UI thread. Log ONCE (a per-frame
            // failure would otherwise flood the log) with the exception so the
            // cause is captured.
            if (!_imErrLogged)
            {
                _imErrLogged = true;
                GlobalLogger.Error("Architect.LogicCanvasView.Win2D",
                    "Immediate-mode draw failed (logged once)", ex);
            }
        }
    }

    private bool _imErrLogged;

    private static bool RectsOverlap(double x, double y, double w, double h, Rect r)
        => x < r.X + r.Width && x + w > r.X && y < r.Y + r.Height && y + h > r.Y;

    // ── Background dot grid (mirrors the retained GridLayer's 40px lattice) ──
    private void DrawGrid(CanvasDrawingSession ds, double left, double top, double right, double bottom, double zoom)
    {
        const double step = 40.0;
        // Skip when dots would be denser than ~6 on-screen px apart (far zoom-out)
        // — the same readability floor the retained dot grid effectively had, and
        // it caps the loop count so a fit-all view never draws a million dots.
        if (step * zoom < 6.0) return;

        double radius = 1.4; // content-space; ~1.4*zoom on screen
        double startX = Math.Floor(left / step) * step;
        double startY = Math.Floor(top / step) * step;
        for (double x = startX; x <= right; x += step)
            for (double y = startY; y <= bottom; y += step)
                ds.FillCircle((float)x, (float)y, (float)radius, _imGridDot);
    }

    // ── One wire ──
    //
    // Per-wire bezier geometry cache. Building a CanvasPathBuilder +
    // CanvasGeometry per wire per frame is finalizable RCW churn at draw
    // cadence; the path depends only on the two endpoints (ControlDistance is
    // a pure function of them), so the geometry is reused until an endpoint
    // moves. Entries are released when the link leaves the graph
    // (DetachDecorations → ReleaseWireGeometry) and the whole cache drops on
    // device recreate / DPI change (ReleaseDeviceGeometryCaches).
    private sealed class WireGeometryCacheEntry
    {
        public CanvasGeometry? Geometry;
        public double Fx, Fy, Tx, Ty;
    }

    private readonly System.Collections.Generic.Dictionary<LinkViewModel, WireGeometryCacheEntry>
        _wireGeometryCache = new();

    private void ReleaseWireGeometry(LinkViewModel link)
    {
        if (_wireGeometryCache.Remove(link, out var entry))
            entry.Geometry?.Dispose();
    }

    private void ClearWireGeometryCache()
    {
        foreach (var entry in _wireGeometryCache.Values) entry.Geometry?.Dispose();
        _wireGeometryCache.Clear();
    }

    private void DrawWire(CanvasControl sender, CanvasDrawingSession ds, LinkViewModel link, Rect visible)
    {
        var from = link.LastFromAnchor;
        var to = link.LastToAnchor;
        if (from is null || to is null) return;

        double fx = from.Value.X, fy = from.Value.Y, tx = to.Value.X, ty = to.Value.Y;

        // Cheap bbox cull (matches the retained wire-virt band logic).
        double minX = Math.Min(fx, tx) - 300, maxX = Math.Max(fx, tx) + 300;
        double minY = Math.Min(fy, ty) - 300, maxY = Math.Max(fy, ty) + 300;
        if (maxX < visible.X || minX > visible.X + visible.Width
            || maxY < visible.Y || minY > visible.Y + visible.Height)
            return;

        if (!_wireGeometryCache.TryGetValue(link, out var cached))
        {
            cached = new WireGeometryCacheEntry();
            _wireGeometryCache[link] = cached;
        }
        if (cached.Geometry is null
            || cached.Fx != fx || cached.Fy != fy || cached.Tx != tx || cached.Ty != ty)
        {
            cached.Geometry?.Dispose();
            double dx = BezierPath.ControlDistance(fx, fy, tx, ty);
            using var pb = new CanvasPathBuilder(sender);
            pb.BeginFigure(new Vector2((float)fx, (float)fy));
            pb.AddCubicBezier(
                new Vector2((float)(fx + dx), (float)fy),
                new Vector2((float)(tx - dx), (float)ty),
                new Vector2((float)tx, (float)ty));
            pb.EndFigure(CanvasFigureLoop.Open);
            cached.Geometry = CanvasGeometry.CreatePath(pb);
            cached.Fx = fx; cached.Fy = fy; cached.Tx = tx; cached.Ty = ty;
        }
        var geom = cached.Geometry!;

        Color stroke = WireColor(link);
        float thickness = (float)link.EffectiveStrokeThickness;
        if (link.IsDangling || link.IsInvalid)
            ds.DrawGeometry(geom, stroke, thickness, s_imDashedStroke);
        else
            ds.DrawGeometry(geom, stroke, thickness);
    }

    private Color WireColor(LinkViewModel link)
    {
        if (link.IsSelected) return _imSelection;
        if (link.IsDangling || link.IsInvalid) return _imError;
        Color baseColor = link.IsFlow
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)   // flow = white (matches retained s_flowBrush)
            : ParseHexColor(link.StrokeColorHex, _imSubText);
        // hover lift — +20% brightness, matching the retained EffectiveStrokeBrush.
        if (link.IsHovered) baseColor = Lift(baseColor, 1.20);
        return baseColor;
    }

    private static Color Lift(Color c, double f) => Color.FromArgb(
        c.A,
        (byte)Math.Min(255, (int)(c.R * f)),
        (byte)Math.Min(255, (int)(c.G * f)),
        (byte)Math.Min(255, (int)(c.B * f)));

    private static readonly CanvasStrokeStyle s_imDashedStroke =
        new() { DashStyle = CanvasDashStyle.Dash };

    // ── One node ──
    private void DrawNode(CanvasDrawingSession ds, NodeViewModel node)
    {
        // Disabled nodes render dimmed (mirrors NodeView's reduced-opacity inert
        // look). CreateLayer applies the opacity to everything drawn inside it.
        if (node.IsDisabled)
        {
            using (ds.CreateLayer(0.45f))
                DrawNodeBody(ds, node);
        }
        else
        {
            DrawNodeBody(ds, node);
        }
    }

    private void DrawNodeBody(CanvasDrawingSession ds, NodeViewModel node)
    {
        double nx = node.X, ny = node.Y, nw = node.Width, nh = node.Height;

        if (node.IsReroute)
        {
            // Diamond wire-knot — fixed neutral grey body, selection-driven
            // stroke. Shape is a constant; the cached origin-centred geometry
            // is drawn at the knot's offset instead of rebuilding a path per
            // reroute per frame.
            float cx = (float)(nx + 10), cy = (float)(ny + 10);
            var dgeom = _rerouteDiamondGeom ??= BuildClosedPolygon(ImmediateCanvas, s_rerouteDiamondPts);
            var doffset = new Vector2(cx, cy);
            ds.FillGeometry(dgeom, doffset, Color.FromArgb(0xFF, 0x3C, 0x3C, 0x41));
            ds.DrawGeometry(dgeom, doffset, node.IsSelected ? _imSelection : Color.FromArgb(0xFF, 0x69, 0x69, 0x69), 1.5f);
            return;
        }

        var bodyRect = new Rect(nx, ny, nw, nh);
        ds.FillRoundedRectangle(bodyRect, 6, 6, _imBodyFill);

        // Header band — node's own header colour. Rounded all four corners; the
        // bottom rounding tucks under the body fill (cosmetic, imperceptible).
        double headerH = node.HeaderSectionHeight;
        Color headerColor = ToWinColor(node.Model.HeaderColor, _imHeaderFallbk);
        ds.FillRoundedRectangle(new Rect(nx, ny, nw, headerH), 6, 6, headerColor);

        // Title + optional italic definer subtitle.
        if (!string.IsNullOrEmpty(node.Title))
        {
            double titleH = node.HasDefiner ? headerH * 0.58 : headerH;
            ds.DrawText(node.Title, new Rect(nx + 10, ny, Math.Max(0, nw - 20), titleH), _imText, _imTitleFormat);
            if (node.HasDefiner && !string.IsNullOrEmpty(node.Definer))
                ds.DrawText(node.Definer, new Rect(nx + 10, ny + titleH, Math.Max(0, nw - 20), headerH - titleH), _imSubText, _imDefinerFormat);
        }

        if (node.HasCompactSymbol)
        {
            // Compact mode hides the socket labels behind one large centred
            // operator glyph (mirrors NodeView's compact affordance).
            using var compactFmt = new CanvasTextFormat
            {
                FontFamily = "Segoe UI", FontSize = (float)Math.Max(14, headerH),
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };
            ds.DrawText(node.CompactSymbol, new Rect(nx, ny + headerH, nw, Math.Max(0, nh - headerH)), _imText, compactFmt);
        }
        else
        {
            // Socket rows — label (+ value pill). PINS are drawn LAST (after the
            // border) so the outline can't cut through the edge-straddling bubbles.
            var rowHeights = GetRowHeightsCached(node);
            double RowHeightAt(int idx) => (idx >= 0 && idx < rowHeights.Length) ? rowHeights[idx] : NodeGeometry.SocketRowHeight;

            foreach (var s in node.Inputs)
            {
                double rowCY = ny + RowCenterForSocket(node, s);
                if (s.IsPillVisible)
                {
                    var (px, py, pw, ph, ml) = ComputeInputPillRect(nx, nw, rowCY, RowHeightAt(s.RowIndex), s, node);
                    // Label is confined to the space LEFT of the pill so it never
                    // paints under it (pill starts at px = label end + gap).
                    double lblW = Math.Max(0, px - (nx + 14) - 4);
                    ds.DrawText(s.Label ?? string.Empty, new Rect(nx + 14, rowCY - 9, lblW, 18), _imText, _imLabelFormat);
                    DrawValuePill(ds, px, py, pw, ph, s.PillDisplayText, ml);
                }
                else
                {
                    // No pill on this row — the label may use the input column but,
                    // like the pill, must stop before the output-label column.
                    double lblW = Math.Max(0, InputRightBoundary(nx, nw, node) - (nx + 14));
                    ds.DrawText(s.Label ?? string.Empty,
                        new Rect(nx + 14, rowCY - 9, lblW, 18), _imText, _imLabelFormat);
                }
            }
            foreach (var s in node.Outputs)
            {
                double rowCY = ny + RowCenterForSocket(node, s);
                // Right-align to nx+nw-16 (OutputRowTemplate right pad), mirroring the
                // retained Auto output column — no longer a fixed % band that overlapped
                // the input pills.
                double outLblW = NodeGeometry.EstimateTextWidth(s.Label, 12.0);
                ds.DrawText(s.Label ?? string.Empty,
                    new Rect(nx + nw - 16 - outLblW, rowCY - 9, Math.Max(0, outLblW + 2), 18), _imText, _imLabelRightFormat);
            }

            // Middle-attribute rows (Key …… [pill] / ☑/☐) below the socket grid.
            DrawMiddleAttributes(ds, node, nx, ny, nw);
        }

        // Border — paint-only state cascade (mirrors NodeView.ApplyBorder priority:
        // flash > error > var-writer > var-reader > selection > default).
        (Color borderColor, float borderThickness) = NodeStateBorder(node);
        ds.DrawRoundedRectangle(bodyRect, 6, 6, borderColor, borderThickness);

        // Pins LAST — ABOVE the border. The pins straddle the node's outer edge
        // (SocketColumnInset=0), so drawing them after the outline keeps the bubble
        // whole instead of letting the border line slice through it. This mirrors
        // the retained path, where pins live in a PinOverlay rendered as a sibling
        // ON TOP of NodeRoot (the body + border).
        foreach (var s in node.Inputs)  DrawPin(ds, nx,      ny + RowCenterForSocket(node, s), s);
        foreach (var s in node.Outputs) DrawPin(ds, nx + nw, ny + RowCenterForSocket(node, s), s);
    }

    private (Color, float) NodeStateBorder(NodeViewModel node)
    {
        if (node.IsExecutingFlash) return (_imFlash, 2f);
        if (node.IsErrorState)     return (_imError, 2f);
        if (node.IsVarChainWriter) return (_imVarWriter, 2f);
        if (node.IsVarChainReader) return (_imVarReader, 2f);
        if (node.IsSelected)       return (_imSelection, 2f);
        if (ReferenceEquals(node, _hoverNode)) return (Lift(_imBorder, 1.8), 1.5f); // hover
        return (_imBorder, 1f);
    }

    // Inline value pill. The caller passes the EXACT rect (computed by
    // ComputeInputPillRect for socket rows, or the middle-attr path) so the painted
    // pill and the click hit-test share one geometry source and can't drift. Single-
    // line pills paint NoWrap at 18px; multi-line pills paint wrapped into the (taller)
    // row. SYNC: the rect MUST come from the same helper the hit-test uses.
    private void DrawValuePill(CanvasDrawingSession ds, double pillX, double pillY, double pillW, double pillH, string? text, bool multiline)
    {
        if (string.IsNullOrEmpty(text) || pillW < 10) return;
        var pillRect = new Rect(pillX, pillY, pillW, pillH);
        ds.FillRoundedRectangle(pillRect, 4, 4, _imPillFill);
        var fmt = (multiline ? _imPillFormatWrap : _imPillFormat) ?? _imPillFormat;
        ds.DrawText(text, new Rect(pillX + 5, pillY + 2, Math.Max(0, pillW - 10), Math.Max(0, pillH - 4)), _imText, fmt);
    }

    // Exact rect of an INPUT socket's value pill — the SINGLE source
    // shared by DrawNodeBody and the click hit-test so the painted pill and the
    // clickable target can never diverge. Horizontal: the pill sits right after its
    // label and ends before the right-aligned output-label column (mirrors the
    // retained NodeView */Auto grid) so it never paints over the outputs — the bug
    // both DB and Text.Builder nodes showed. Vertical: single-line pills are 18px
    // centred on rowCY; multi-line pills fill the grown row and wrap at the SAME
    // PillWrapWidth the body-height budget measured with (so render and reserved
    // height match). DB pills (TableName/Column) stay single-line and just need the
    // node — already widened by IntrinsicWidth — to give them room, which the
    // confinement preserves.
    // X where the input column must END — the left edge of the
    // right-aligned output-label column (mirrors the retained NodeView Auto output
    // column). Input pills AND no-pill input labels stay left of this so they never
    // paint over the outputs. No outputs → input content runs to the right padding.
    // The output column width is the WIDEST output label (uniform column, exactly like
    // the retained grid), so every input row shares this one boundary.
    private double InputRightBoundary(double nx, double nw, NodeViewModel node)
    {
        const double rowRightPad = 8.0;  // input row right pad (no outputs case)
        const double outRightPad = 16.0; // OutputRowTemplate Padding right = 16
        const double interCol = 8.0;     // OutputRowTemplate Padding left — visual gap before the output column
        double maxOutW = 0;
        if (node.Outputs != null)
            foreach (var o in node.Outputs)
                maxOutW = Math.Max(maxOutW, NodeGeometry.EstimateTextWidth(o.Label, 12.0));
        return maxOutW > 0 ? (nx + nw - outRightPad - maxOutW - interCol) : (nx + nw - rowRightPad);
    }

    private (double x, double y, double w, double h, bool multiline) ComputeInputPillRect(
        double nx, double nw, double rowCY, double rowH, SocketViewModel s, NodeViewModel node)
    {
        const double labelX = 14.0;   // input row left pad (pin overhang) — matches DrawNodeBody label X
        const double labelGap = 6.0;  // gap between label and pill

        double labelW = NodeGeometry.EstimateTextWidth(s.Label, 12.0);
        double pillX = nx + labelX + labelW + labelGap;
        double availW = Math.Max(10.0, InputRightBoundary(nx, nw, node) - pillX);

        string text = s.PillDisplayText ?? string.Empty;
        bool isDb = NodeGeometry.IsDbPill(node.Model, s.Model);
        // A pill wraps (multi-line) when it is a
        // DECLARED multi-line editor (Template / Script / Payload / newline) OR
        // when a plain pill's single-line text is wider than the row's pill-wrap
        // budget. NodeGeometry.SocketRowHeights ALREADY measured + reserved the
        // wrapped height for every non-DB pill at this SAME PillWrapWidth, so the
        // grown row exists — the renderer previously drew plain over-wide pills
        // single-line NoWrap (CharacterEllipsis) into an 18px slot, so the text
        // was clipped while the reserved height sat empty (Majo: "pill text gets
        // cut off even though there's room"). Pass the REAL IsMultilinePill flag
        // to PillWrapWidth so the wrap width here == the width the row-height was
        // measured at (240 for a declared editor, 480 for a plain value); a
        // mismatch would wrap into more/fewer lines than reserved → vertical clip.
        // DB pills never wrap — they widen single-line so the whole identifier shows.
        double lead = NodeGeometry.SocketPillLead(s.Model?.Name ?? string.Empty);
        bool isMultiline = !isDb && NodeGeometry.IsMultilinePill(s.Model?.Name, text);
        double wrapW = NodeGeometry.PillWrapWidth(node.Model, lead, isMultiline, false);
        double textW = NodeGeometry.EstimateTextWidth(text, 11.0, mono: true);
        // A plain pill may only take the WRAP branch when the row-height budget
        // actually RESERVED a grown row (rowH > the single-line row). The width
        // predicate alone can disagree with the height budget at the wrap
        // boundary — EstimateTextWidth is the MAX of the XAML and Win2D
        // engines, while SocketRowHeights decides line count with a XAML
        // wrapped measure — and wrapping into a row that stayed single-line
        // would clip the second line vertically. The reserved height is the
        // one fact both sides share, so it gates the branch. Declared
        // multi-line editors always get a grown row from the budget, so this
        // gate never blocks them.
        bool rowGrown = rowH > NodeGeometry.SocketRowHeight + 0.5;
        bool wraps = !isDb && (isMultiline || (textW > wrapW && rowGrown));
        if (wraps)
        {
            double w = Math.Max(10.0, Math.Min(availW, wrapW));
            double h = Math.Max(18.0, rowH - 4.0);
            return (pillX, rowCY - h / 2.0, w, h, true);
        }
        // Single-line (incl. DB): size to the text + a few px of slack so a
        // measure-vs-draw font drift (NodeGeometry.EstimateTextWidth vs the Win2D
        // CanvasTextFormat face) can't ellipsis-trim the last glyph. The body
        // already reserved this width via SocketRowContentWidth, so the slack
        // comes out of headroom, not the output column.
        double sw = Math.Max(10.0, Math.Min(availW, textW + 18.0));
        return (pillX, rowCY - 9.0, sw, 18.0, false);
    }

    // Middle-attribute rows — DefaultProperties keys with no matching socket,
    // rendered as `Key …… [value-pill]` (text) or a left-aligned `☑ Key`
    // checkmark row (bool), beneath the socket grid.
    private void DrawMiddleAttributes(CanvasDrawingSession ds, NodeViewModel node, double nx, double ny, double nw)
    {
        if (!node.HasMiddleAttributes) return;
        // Socket area can now be taller than rows*24 (wrapped pills),
        // so the middle-attr block starts below the ACTUAL summed socket-row heights.
        double socketRowsTotal = 0;
        foreach (var h in GetRowHeightsCached(node)) socketRowsTotal += h;
        double y = ny + NodeGeometry.FirstSocketRowY(node.Model)
                 + socketRowsTotal
                 + NodeGeometry.MiddleAttrAreaTopPad;

        // Dotted divider above the area.
        ds.DrawLine((float)(nx + 8), (float)y, (float)(nx + nw - 8), (float)y, _imBorder, 1f, s_imDashedStroke);
        y += NodeGeometry.MiddleAttrDividerH;

        foreach (var m in node.MiddleAttributes)
        {
            // Per-row height from the SAME formula the body-height budget reserves
            // (NodeGeometry.MiddleAttrRowHeightFor). Pre-fix every row was a fixed
            // 22px and the value pill drew single-line NoWrap, so a long Template /
            // Script / Payload overran the node body over the "Result" output — the
            // "dismembered" pill Majo reported. Now the row grows and the pill wraps
            // to match, exactly like the socket-pill path (ComputeInputPillRect).
            double rowH  = NodeGeometry.MiddleAttrRowHeightFor(node.Model, m.Key, m.Value);
            double rowCY = y + rowH / 2.0;
            if (m.IsBool)
            {
                // Bool checkmark row — LEFT-aligned `☑ Key` (glyph + label),
                // mirroring the retained MiddleAttributeRowTemplate's bool
                // StackPanel (Chat.Message's Twitch /
                // YouTube / Kick list). Checked rows read bright (_imText),
                // unchecked dim (_imSubText) so the ON/OFF state is visible
                // straight off the GPU canvas without materializing the
                // NodeView. Geometry (8 left pad + 18 glyph + 6 gap + label)
                // is mirrored by the bool-row width budget in
                // NodeGeometry.ComputeIntrinsicWidth AND the single-click
                // toggle hit-test in TryBeginInlineEditAtCanvasPoint — keep
                // the three in lockstep.
                var boolColor = m.BoolValue ? _imText : _imSubText;
                ds.DrawText(m.BoolGlyph, new Rect(nx + 8, rowCY - 9, 18, 18), boolColor, _imLabelFormat);
                ds.DrawText(m.Key, new Rect(nx + 32, rowCY - 9, Math.Max(0, nw - 40), 18), boolColor, _imLabelFormat);
            }
            else
            {
                ds.DrawText(m.Key, new Rect(nx + 8, rowCY - 9, Math.Max(0, nw * 0.5), 18), _imSubText, _imLabelFormat);
                // Middle-attr rows have no output column on their line, so the pill
                // uses the right portion of the full body width (Key …… [pill]).
                double mpX = nx + nw * 0.42;
                double mpAvailW = Math.Max(10.0, nx + nw - 8 - mpX);
                string val = m.DisplayText;
                // Wrap when the key is a declared multi-line editor OR the value is
                // wider than the row's wrap budget (the width the height was
                // measured at); cap the pill at that budget so the render fills the
                // reserved height instead of overflowing the body single-line.
                double wrapW = NodeGeometry.MiddleAttrPillWrapWidth(node.Model, m.Key, m.Value);
                double textW = NodeGeometry.EstimateTextWidth(val, 11.0, mono: true);
                // Same row-grown gate as ComputeInputPillRect: the width
                // predicate may only elect the wrap branch when the height
                // budget (MiddleAttrRowHeightFor) actually reserved a grown
                // row — wrapping into the fixed 22px row would clip line 2.
                bool rowGrown = rowH > NodeGeometry.MiddleAttrRowHeight + 0.5;
                bool wraps = NodeGeometry.IsMultilinePill(m.Key, m.Value) || (textW > wrapW && rowGrown);
                if (wraps)
                {
                    double w = Math.Max(10.0, Math.Min(mpAvailW, wrapW));
                    double h = Math.Max(18.0, rowH - 4.0);
                    DrawValuePill(ds, mpX, rowCY - h / 2.0, w, h, val, true);
                }
                else
                {
                    DrawValuePill(ds, mpX, rowCY - 9, Math.Min(mpAvailW, textW + 18.0), 18, val, false);
                }
            }
            y += rowH;
        }
    }

    // Per-node socket-row-height cache. NodeGeometry.SocketRowHeights already
    // memoizes by node id, but validating that entry recomputes an
    // O(sockets+attributes) content hash on EVERY call — and DrawNodeBody
    // consults the rows several times per node per draw (body, pills,
    // middle-attrs, every pin's row centre). This caches the resolved array
    // per node VM; any node-VM PropertyChanged, every committed graph
    // mutation, edit-mode exit, and graph load/unload drop the entry so
    // socket/attribute edits re-derive on the next draw.
    private readonly System.Collections.Generic.Dictionary<NodeViewModel, double[]>
        _nodeRowHeightsCache = new();

    private double[] GetRowHeightsCached(NodeViewModel node)
    {
        if (_nodeRowHeightsCache.TryGetValue(node, out var rows)) return rows;
        rows = NodeGeometry.SocketRowHeights(node.Model);
        _nodeRowHeightsCache[node] = rows;
        return rows;
    }

    // Prefer the MEASURED row-centre (set when a real NodeView was
    // mounted to edit this node) over the computed dynamic estimate — mirroring the
    // wire path (LinkViewModel / SocketViewModel.Anchor / RebuildPinOverlay) so the
    // GPU pins, the pill hit-test, and the wires all land on the SAME Y in every
    // state. Falls back to the dynamic estimate for nodes never mounted (the pure-GPU
    // case), where the wire path also falls back to the same dynamic estimate — so
    // both states stay in lockstep.
    private double RowCenterForSocket(NodeViewModel node, SocketViewModel s)
        => SocketRenderState.TryGetMeasuredRowCenterY(s.Id)
           ?? RowCenterFromCachedHeights(node, s.RowIndex);

    // NodeGeometry.NodeBorderInset + NodeGeometry.RowCenterYDynamic, computed
    // from the per-node cached row-height array so each row-centre lookup
    // doesn't re-trigger SocketRowHeights' content-hash validation. The
    // summation is kept in exact lockstep with RowCenterYDynamic; the
    // degenerate paths delegate to it verbatim.
    private double RowCenterFromCachedHeights(NodeViewModel node, int rowIndex)
    {
        if (rowIndex < 0)
            return NodeGeometry.NodeBorderInset + NodeGeometry.RowCenterYDynamic(node.Model, rowIndex);
        var heights = GetRowHeightsCached(node);
        if (heights.Length == 0)
            return NodeGeometry.NodeBorderInset + NodeGeometry.RowCenterYDynamic(node.Model, rowIndex);
        double y = NodeGeometry.FirstSocketRowY(node.Model);
        int upto = Math.Min(rowIndex, heights.Length);
        for (int r = 0; r < upto; r++) y += heights[r];
        double h = rowIndex < heights.Length ? heights[rowIndex] : NodeGeometry.SocketRowHeight;
        return NodeGeometry.NodeBorderInset + y + h / 2.0;
    }

    // ── Pin / knot shape templates ──
    // The pin outlines are shape CONSTANTS in the 14×14 pin box (origin-
    // relative); the reroute diamond is a 20×20 knot centred on the origin.
    // Each kind's CanvasGeometry is built once and drawn at a per-pin offset
    // instead of rebuilding a path per pin per frame. Dropped + lazily
    // rebuilt on device recreate / DPI change (ReleaseDeviceGeometryCaches).
    private static readonly Vector2[] s_pinChevronPts  = { new(2f, 2f), new(11f, 7f), new(2f, 12f) };
    private static readonly Vector2[] s_pinTrianglePts = { new(7f, 2f), new(12f, 12f), new(2f, 12f) };
    private static readonly Vector2[] s_pinDiamondPts  = { new(7f, 2f), new(12f, 7f), new(7f, 12f), new(2f, 7f) };
    private static readonly Vector2[] s_pinSquarePts   = { new(2f, 2f), new(12f, 2f), new(12f, 12f), new(2f, 12f) };
    private static readonly Vector2[] s_rerouteDiamondPts = { new(0f, -10f), new(10f, 0f), new(0f, 10f), new(-10f, 0f) };

    private CanvasGeometry? _pinChevronGeom;
    private CanvasGeometry? _pinTriangleGeom;
    private CanvasGeometry? _pinDiamondGeom;
    private CanvasGeometry? _pinSquareGeom;
    private CanvasGeometry? _rerouteDiamondGeom;

    // Last DPI the geometry caches were built under (0 = never built).
    private float _imLastDpi;

    private static CanvasGeometry BuildClosedPolygon(ICanvasResourceCreator rc, Vector2[] pts)
    {
        using var pb = new CanvasPathBuilder(rc);
        pb.BeginFigure(pts[0]);
        for (int i = 1; i < pts.Length; i++) pb.AddLine(pts[i]);
        pb.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(pb);
    }

    // Drop every cached CanvasGeometry (wire beziers + pin/knot templates).
    // Called from the CreateResources hook (device created / lost+recreated)
    // and the per-draw DPI check; everything rebuilds lazily on next use.
    private void ReleaseDeviceGeometryCaches()
    {
        ClearWireGeometryCache();
        _pinChevronGeom?.Dispose();     _pinChevronGeom = null;
        _pinTriangleGeom?.Dispose();    _pinTriangleGeom = null;
        _pinDiamondGeom?.Dispose();     _pinDiamondGeom = null;
        _pinSquareGeom?.Dispose();      _pinSquareGeom = null;
        _rerouteDiamondGeom?.Dispose(); _rerouteDiamondGeom = null;
    }

    // Draw the socket pin in its TYPE SHAPE (matches PinPathGeometry / the retained
    // path): Flow=chevron, Bool=triangle, Collection=rounded-square, Any/Vector=
    // diamond, Int=square, else circle. Filled when connected (s.PinFilled), else a
    // hollow outline. Shapes live in a 14×14 box centred on (cx,cy) — origin (ox,oy).
    private void DrawPin(CanvasDrawingSession ds, double cx, double cy, SocketViewModel s)
    {
        Color pinColor = ParseHexColor(s.ColorHex, _imSubText);
        double ox = cx - 7, oy = cy - 7;
        // Dynamic placeholders ("+ variable" / "+ input"
        // / "+ output" / "+ return") render their pin outline DASHED — matching the
        // retained NodeView's IsDynamicPlaceholder StrokeDashArray — so an add-slot
        // reads as a distinct "droppable" target rather than a faint solid pin (which
        // is why a "+ slot" looked absent on the GPU canvas). Placeholders are always
        // hollow (SocketViewModel.RebuildPinDerived forces PinFilled=false), so only
        // the outline (Draw*, not Fill*) branches take the dashed style. A null style
        // is the default solid stroke for every non-placeholder pin.
        CanvasStrokeStyle? stroke = s.IsDynamicPlaceholder ? s_imDashedStroke : null;

        switch (s.Kind)
        {
            case SocketPinKind.Circle:
            case SocketPinKind.Mismatch:
                if (s.PinFilled) ds.FillCircle((float)cx, (float)cy, 4.5f, pinColor);
                else             ds.DrawCircle((float)cx, (float)cy, 4.5f, pinColor, 1.5f, stroke);
                return;

            case SocketPinKind.RoundedSquare:
            {
                var rsq = new Rect(ox + 2, oy + 2, 10, 10);
                if (s.PinFilled) ds.FillRoundedRectangle(rsq, 2, 2, pinColor);
                else             ds.DrawRoundedRectangle(rsq, 2, 2, pinColor, 1.5f, stroke);
                return;
            }

            default:
            {
                CanvasGeometry? geom = s.Kind switch
                {
                    SocketPinKind.Chevron  => _pinChevronGeom  ??= BuildClosedPolygon(ImmediateCanvas, s_pinChevronPts),
                    SocketPinKind.Triangle => _pinTriangleGeom ??= BuildClosedPolygon(ImmediateCanvas, s_pinTrianglePts),
                    SocketPinKind.Diamond  => _pinDiamondGeom  ??= BuildClosedPolygon(ImmediateCanvas, s_pinDiamondPts),
                    SocketPinKind.Square   => _pinSquareGeom   ??= BuildClosedPolygon(ImmediateCanvas, s_pinSquarePts),
                    _                      => null,
                };
                if (geom is null) { ds.FillCircle((float)cx, (float)cy, 4.5f, pinColor); return; }
                var offset = new Vector2((float)ox, (float)oy);
                if (s.PinFilled) ds.FillGeometry(geom, offset, pinColor);
                else             ds.DrawGeometry(geom, offset, pinColor, 1.5f, stroke);
                return;
            }
        }
    }

    private void EnsureTextFormats(ICanvasResourceCreator rc)
    {
        if (_imTitleFormat is not null) return;
        // FONTS — the app's EXACT Inter / JetBrains Mono faces, resolved through
        // the shared Win2DCanvasFonts helper (file:// URI + "#Family"; Phoenix is
        // UNPACKAGED, so the packaged "ms-appx:///" form does NOT resolve and
        // DrawText THROWS on it — that bug left every node invisible). The helper
        // trial-loads the files and falls back to Segoe UI Variable Text ≈ Inter /
        // Consolas ≈ JetBrains Mono, so DrawText can never throw on an
        // unresolvable font. Sharing the resolver keeps these draw formats and the
        // NodeGeometry width oracle (Win2DTextMeasure) on the SAME faces — the
        // measure/draw agreement the pill sizing depends on.
        var (sans, mono, exact) = Win2DCanvasFonts.Resolve(rc);
        GlobalLogger.Log(
            exact ? "[Win2D] Loaded exact Inter / JetBrains Mono fonts."
                  : "[Win2D] Using system fonts (exact .ttf load unavailable).",
            "Architect.LogicCanvasView.Win2D", LogLevel.System);

        _imTitleFormat = new CanvasTextFormat
        {
            FontFamily = sans, FontSize = 13, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            VerticalAlignment = CanvasVerticalAlignment.Center, HorizontalAlignment = CanvasHorizontalAlignment.Left,
        };
        _imDefinerFormat = new CanvasTextFormat
        {
            FontFamily = sans, FontSize = 10, FontStyle = Windows.UI.Text.FontStyle.Italic,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            VerticalAlignment = CanvasVerticalAlignment.Center, HorizontalAlignment = CanvasHorizontalAlignment.Left,
        };
        _imLabelFormat = new CanvasTextFormat
        {
            FontFamily = sans, FontSize = 12,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            VerticalAlignment = CanvasVerticalAlignment.Center, HorizontalAlignment = CanvasHorizontalAlignment.Left,
        };
        _imLabelRightFormat = new CanvasTextFormat
        {
            FontFamily = sans, FontSize = 12,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            VerticalAlignment = CanvasVerticalAlignment.Center, HorizontalAlignment = CanvasHorizontalAlignment.Right,
        };
        _imPillFormat = new CanvasTextFormat
        {
            FontFamily = mono, FontSize = 11,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            VerticalAlignment = CanvasVerticalAlignment.Center, HorizontalAlignment = CanvasHorizontalAlignment.Left,
        };
        // Wrapped variant for multi-line pills (Template / Script /
        // Payload, or any value with a newline). Top-aligned so the wrapped text
        // fills the grown row from its top; the row height was measured at the same
        // PillWrapWidth this wraps at, so render and height-budget agree.
        _imPillFormatWrap = new CanvasTextFormat
        {
            FontFamily = mono, FontSize = 11,
            WordWrapping = CanvasWordWrapping.Wrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            VerticalAlignment = CanvasVerticalAlignment.Top, HorizontalAlignment = CanvasHorizontalAlignment.Left,
        };
    }


    // ── Frame (comment region) ──
    private void DrawFrame(CanvasDrawingSession ds, FrameViewModel frame, Rect visible)
    {
        if (!RectsOverlap(frame.X, frame.Y, frame.Width, frame.Height, visible)) return;
        Color fc = ParseHexColor(frame.FrameColorHex, _imSelection);
        var rect = new Rect(frame.X, frame.Y, frame.Width, frame.Height);
        ds.FillRoundedRectangle(rect, 8, 8, Color.FromArgb(0x1F, fc.R, fc.G, fc.B)); // translucent body
        ds.DrawRoundedRectangle(rect, 8, 8, frame.IsSelected ? _imSelection : fc, frame.IsSelected ? 2f : 1.5f);
        if (!string.IsNullOrEmpty(frame.Label))
            ds.DrawText(frame.Label, new Rect(frame.X + 10, frame.Y + 4, Math.Max(0, frame.Width - 20), 22), fc, _imTitleFormat);

        // Bottom-right resize affordance — a small swatch echoing the retained
        // path's visible handle so the (otherwise invisible) resize border is
        // discoverable; hover anywhere on the border swaps in a resize cursor.
        const float handle = 9f;
        if (frame.Width > handle * 2 && frame.Height > handle * 2)
        {
            var grip = new Rect(frame.X + frame.Width - handle, frame.Y + frame.Height - handle, handle, handle);
            ds.FillRectangle(grip, Color.FromArgb(0x99, fc.R, fc.G, fc.B));
        }
    }

    // ── In-flight wire-drop preview ──
    private void DrawWirePreview(CanvasControl sender, CanvasDrawingSession ds)
    {
        if (_drag != DragState.WireDrop || _wireFromSocket is null || _wireFromNode is null) return;
        var (ax, ay) = _wireFromSocket.Anchor();
        double fx = _wireFromNode.X + ax, fy = _wireFromNode.Y + ay;
        var cursor = HostToCanvas(_lastHostPoint);
        double tx = cursor.X, ty = cursor.Y;
        double dx = BezierPath.ControlDistance(fx, fy, tx, ty);
        using var pb = new CanvasPathBuilder(sender);
        pb.BeginFigure(new Vector2((float)fx, (float)fy));
        pb.AddCubicBezier(
            new Vector2((float)(fx + dx), (float)fy),
            new Vector2((float)(tx - dx), (float)ty),
            new Vector2((float)tx, (float)ty));
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geom = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geom, _imSelection, 2f, s_imDashedStroke);
    }

    // ── Model-space hit-test (immediate mode replacement for the visual-tree
    //    HitTagFrom). Topmost-first priority matches paint order: node > wire >
    //    frame-header. Returns the same VM types HitTagFrom does, so the entire
    //    downstream pointer pipeline (select / drag / marquee / wire / context
    //    menu) is reused unchanged.
    private object? ResolveModelHit(Point canvasPoint)
    {
        if (_vm is null) return null;
        double x = canvasPoint.X, y = canvasPoint.Y;

        // Nodes — reverse z (last drawn = topmost).
        for (int i = _vm.Nodes.Count - 1; i >= 0; i--)
        {
            var n = _vm.Nodes[i];
            if (x >= n.X && x <= n.X + n.Width && y >= n.Y && y <= n.Y + n.Height)
                return n;
        }

        // Wires — nearest bezier within a zoom-compensated threshold (so wires
        // stay clickable when zoomed out, matching the retained 12px hit-zone feel).
        double wireHit = Math.Max(6.0, 8.0 / Math.Max(_vm.Zoom, 0.05));
        double bestD2 = wireHit * wireHit;
        LinkViewModel? bestLink = null;
        foreach (var link in _vm.Links)
        {
            var f = link.LastFromAnchor; var t = link.LastToAnchor;
            if (f is null || t is null) continue;
            double minX = Math.Min(f.Value.X, t.Value.X) - 60, maxX = Math.Max(f.Value.X, t.Value.X) + 60;
            double minY = Math.Min(f.Value.Y, t.Value.Y) - 60, maxY = Math.Max(f.Value.Y, t.Value.Y) + 60;
            if (x < minX || x > maxX || y < minY || y > maxY) continue;
            for (int s = 0; s <= 24; s++)
            {
                var (px, py) = BezierPath.Sample(f.Value.X, f.Value.Y, t.Value.X, t.Value.Y, s / 24.0);
                double ddx = x - px, ddy = y - py; double d2 = ddx * ddx + ddy * ddy;
                if (d2 < bestD2) { bestD2 = d2; bestLink = link; }
            }
        }
        if (bestLink is not null) return bestLink;

        // Frames — grab the resize border (edges / corners) OR the header band;
        // an empty interior click still falls through to a marquee (matches the
        // editor convention). Resolution is canvas-space here because the GPU
        // canvas has no tagged Border children for the retained path's
        // visual-tree ResolveFrameResizeEdge / IsFrameHeaderHit to walk — that
        // mismatch is exactly why frame move + resize silently broke once the
        // Win2D canvas became the default (the press fell through to a marquee).
        double frameBand = FrameResizeBand();
        for (int i = _vm.Frames.Count - 1; i >= 0; i--)
        {
            var fr = _vm.Frames[i];
            if (FrameEdgeAt(fr, x, y, frameBand) != FrameResizeEdge.None
                || IsFrameHeaderHitWin2D(fr, x, y))
                return fr;
        }
        return null;
    }

    // ── Win2D (model-space) frame interaction helpers ──────────────────────
    // Canvas-space counterparts to the retained path's visual-tree helpers
    // (ResolveFrameResizeEdge / IsFrameHeaderHit). Shared by ResolveModelHit
    // (so an edge press registers as a frame hit at all), OnHostPointerPressed
    // (move-vs-resize routing) and the hover-cursor path.

    // Frame header strip height (canvas units) — the band the frame can be
    // grabbed by to MOVE. Matches the 26px label strip DrawFrame paints.
    private const double FrameHeaderBandPx = 26.0;

    // Edge/corner grab tolerance in canvas units, kept ~8 screen px across the
    // zoom range so the band feels identical zoomed in or out (mirrors the
    // wire hit-zone's zoom compensation above).
    private double FrameResizeBand()
        => _vm is null ? 8.0 : Math.Max(4.0, 8.0 / Math.Max(_vm.Zoom, 0.05));

    // Which resize edge / corner (if any) the canvas point sits on for this
    // frame, within `band`. None for the interior, the header, or points
    // outside the frame by more than `band`. Corners win over edges.
    private static FrameResizeEdge FrameEdgeAt(FrameViewModel fr, double x, double y, double band)
    {
        bool withinX = x >= fr.X - band && x <= fr.X + fr.Width + band;
        bool withinY = y >= fr.Y - band && y <= fr.Y + fr.Height + band;
        if (!withinX || !withinY) return FrameResizeEdge.None;

        bool nearLeft   = Math.Abs(x - fr.X) <= band;
        bool nearRight  = Math.Abs(x - (fr.X + fr.Width)) <= band;
        bool nearTop    = Math.Abs(y - fr.Y) <= band;
        bool nearBottom = Math.Abs(y - (fr.Y + fr.Height)) <= band;

        if (nearTop && nearLeft)     return FrameResizeEdge.TopLeft;
        if (nearTop && nearRight)    return FrameResizeEdge.TopRight;
        if (nearBottom && nearLeft)  return FrameResizeEdge.BottomLeft;
        if (nearBottom && nearRight) return FrameResizeEdge.BottomRight;
        if (nearLeft)   return FrameResizeEdge.Left;
        if (nearRight)  return FrameResizeEdge.Right;
        if (nearTop)    return FrameResizeEdge.Top;
        if (nearBottom) return FrameResizeEdge.Bottom;
        return FrameResizeEdge.None;
    }

    // True when the canvas point sits on the frame's header strip (move grab).
    private static bool IsFrameHeaderHitWin2D(FrameViewModel fr, double x, double y)
        => x >= fr.X && x <= fr.X + fr.Width && y >= fr.Y && y <= fr.Y + FrameHeaderBandPx;

    // ── colour helpers ──
    private static Color ToWinColor(System.Drawing.Color c, Color fallback)
        => c.A == 0 && c.R == 0 && c.G == 0 && c.B == 0 ? fallback : Color.FromArgb(0xFF, c.R, c.G, c.B);

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return fallback;
        try
        {
            string s = hex.Substring(1);
            byte a = 0xFF, r, g, b;
            if (s.Length == 8)
            {
                a = Convert.ToByte(s.Substring(0, 2), 16);
                r = Convert.ToByte(s.Substring(2, 2), 16);
                g = Convert.ToByte(s.Substring(4, 2), 16);
                b = Convert.ToByte(s.Substring(6, 2), 16);
            }
            else if (s.Length == 6)
            {
                r = Convert.ToByte(s.Substring(0, 2), 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
            }
            else return fallback;
            return Color.FromArgb(a, r, g, b);
        }
        catch { return fallback; }
    }
}
