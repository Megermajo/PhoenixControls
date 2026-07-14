using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Foundation;
using Windows.UI.Core;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Pointer-event partial — owns the input state machine. The state is one of:
//
//   * Idle       — no drag in progress
//   * Pan        — middle/right-mouse-drag panning the view
//   * NodeDrag   — left-drag on a node body, translating it
//   * WireDrop   — left-drag from a socket pin, in-flight wire
//
// Multi-pointer / pen / touch paths follow the same code path — WinUI 3
// PointerRoutedEventArgs abstracts the device.
public sealed partial class LogicCanvasView
{
    private enum DragState { Idle, Pan, NodeDrag, WireDrop, Marquee, FrameMove, FrameResize }
    private DragState _drag = DragState.Idle;

    // Pixel threshold a press must move before the drag commits an undo
    // snapshot. Mirrors WidgetGraphCanvas.DragDeadZonePx — keeps a
    // click-without-drag from polluting the undo history with a no-op step.
    private const double DragDeadZonePx = 2.0;

    // Node-drag deferred undo. We defer the undo push from
    // "first cross-deadzone move" to "PointerReleased AND positions actually
    // changed", so this flag tracks "this drag has crossed the deadzone"
    // without coupling it to the undo-stack write. Reset on every PointerPressed.
    // The frame paths now follow the same discipline
    // via _frameMutated below; the old _dragHasPushedUndo first-motion-push flag
    // is fully retired.
    private bool _dragMutated;

    // Frame-drag parity with the node path. Set true
    // once a FrameMove / FrameResize crosses the deadzone. The undo snapshot is
    // now deferred to PointerReleased (CommitFrameDragUndo) instead of pushed at
    // first motion, so an Esc-cancel mid-frame-drag reverts to the captured
    // pre-drag bounds without leaving a spurious undo step (and without the
    // extra Ctrl+Z the pre-fix frame path required). Reset on every frame press.
    private bool _frameMutated;

    // Space-as-pan-mode: while Space is physically held, LMB+drag pans the
    // view instead of starting a marquee / node drag. Mirrors Photoshop /
    // Blueprints / Figma idiom. Tracked here (not via the canvas
    // ModifierDown helper) so we can debounce repeated KeyDown events.
    private bool _spaceHeld;

    // Edge-pan timer + radius — during a NodeDrag / FrameMove / WireDrop /
    // FrameResize, when the cursor sits within EdgePanRadiusPx of the host
    // edge we auto-pan the view in that direction so the user can drag a
    // node past the visible viewport. DispatcherTimer at ~16ms so the pan
    // feels smooth without flooding the layout pass.
    private const double EdgePanRadiusPx = 32.0;
    private const double EdgePanSpeedPx  = 14.0; // per tick at full strength
    private Microsoft.UI.Xaml.DispatcherTimer? _edgePanTimer;

    private FrameViewModel? _dragFrame;
    private Point _frameDragStartCanvas;
    private double _frameStartX, _frameStartY, _frameStartW, _frameStartH;

    // Which edge / corner the in-flight FrameResize gesture
    // is anchored on. Captured at PointerPressed, drives ResizeByEdge on every
    // subsequent PointerMoved. Reset to None at gesture end so a stale value
    // can't bleed into the next press.
    private FrameResizeEdge _frameResizeEdge = FrameResizeEdge.None;

    // Multi-frame drag bookkeeping. When the user starts a
    // FrameMove on a frame that's part of a multi-selection, capture each
    // selected frame's start (X, Y) and translate them all together as the
    // pointer moves. Mirrors the node-side `_groupDragStarts` pattern in
    // LogicCanvasView.Marquee.cs.
    private System.Collections.Generic.List<(FrameViewModel Frame, double X, double Y)>
        _frameGroupDragStarts = new();

    // Parent-child drag linkage. When a FrameMove gesture
    // starts, snapshot every node whose bounds intersect the dragged frame's
    // bounds (innermost-frame-wins for cross-frame containment) so the frame
    // carries its contents like a UE-Blueprints comment box. Containment is
    // computed once at drag-start (NOT continuously) so nodes don't slip in /
    // out mid-drag — same semantics as Blueprints. The dictionary stores each
    // node's pre-drag canvas-space (X, Y); PointerMoved applies the frame's
    // (dx, dy) to every entry alongside the frame translation, and the existing
    // `PushUndo()` at first-motion captures the pre-drag graph state covering
    // both the frame AND its contained nodes in a SINGLE undo entry. Cleared in
    // `EndFrameContentDrag()` on PointerReleased / PointerCanceled / Esc-cancel.
    private System.Collections.Generic.Dictionary<NodeViewModel, Point>
        _frameContentDragStarts = new();

    // Frame-content drag coalesce. PointerMoved on a
    // FrameMove with N captured nodes pre-fix called TranslateNode N times
    // per event (120 Hz on touchpads → 120·N TranslateNode dispatches /
    // second, each firing PropertyChanged X+Y, each marking every wire
    // touching the node dirty). Now the move handler just stashes the
    // latest (dx, dy) + flag; OnRenderingTick drains the flag and calls
    // FlushFrameContentDrag exactly once per displayed frame, dropping
    // the work to 60·N TranslateNode dispatches / second on a 60 Hz tick.
    // Same dirty-flag idiom as `_snapGuidesDirty` / `_marqueeApplyDirty`.
    private bool _frameContentDragDirty;
    private double _frameContentDragLastDx;
    private double _frameContentDragLastDy;

    // Group-drag coalesce. The multi-node group drag was the
    // one gesture still applied inline per pointer-move: TryUpdateGroupDrag
    // loops every selected node calling TranslateNode (X+Y PropertyChanged
    // each), so a 250-node selection at 120 Hz fired ~60k position dispatches
    // / second on the UI thread. Now the move + edge-pan handlers stash the
    // latest cursor canvas-point + flag; OnRenderingTick drains it via
    // FlushGroupDragIfDirty once per displayed frame, and PointerReleased
    // flushes the final stash before committing undo so nodes land with zero
    // offset. Same idiom as the frame-content-drag coalescer above.
    private bool _groupDragDirty;
    private Point _groupDragLatestCanvas;

    // `_nodeStartX/Y` (declared above) doubles as the
    // pre-drag offset for the single-node Esc-cancel path: restoring to
    // those values reverts the drag without having to push undo. The
    // multi-node group path reuses the existing `_groupDragStarts` dictionary
    // from LogicCanvasView.Marquee.cs. Both store positions at PointerPressed.

    private Point _panStartHost;
    private double _panStartTx;
    private double _panStartTy;

    private NodeViewModel? _dragNode;
    private Point _nodeDragStartCanvas;
    private double _nodeStartX;
    private double _nodeStartY;

    private SocketViewModel? _wireFromSocket;
    private NodeViewModel? _wireFromNode;

    // Host-space press point of the in-flight wire drag. A press on a managed
    // "+" slot that RELEASES within PlaceholderClickDriftPx of this point is a
    // CLICK on the slot, no matter what the drop resolution says — without the
    // tolerance, a few pixels of mouse drift resolved the release into the
    // ADJACENT slot row's full-width drop band (or the node body / nothing)
    // and the click silently did nothing.
    private Point _wireDragPressHost;

    // Click-vs-drag tolerance for slot presses, host-space pixels. Twice the
    // 4px Windows drag threshold — a slot click is an aimed, deliberate
    // gesture, but pen/touch and fast mouse clicks drift a few pixels.
    private const double PlaceholderClickDriftPx = 8.0;

    private uint _capturedPointerId;
    private bool _hasCapture;

    // Last cursor position seen during Press/Move (host-space). Used as a
    // hit-test anchor on Release when OriginalSource resolves to the
    // captured HostRoot rather than the element under the cursor.
    private Point _lastHostPoint;

    // Deterministic pin-press hand-off. The socket pin's own
    // 24×24 hit-target raises PointerPressed (NodeView.Pins.cs) and stamps the
    // pressed socket here via NotePinPress. Because PointerPressed BUBBLES, the
    // pin's handler (a descendant) runs BEFORE OnHostPointerPressed on HostRoot
    // (the ancestor) in the same event pass — so by the time the canvas handler
    // reads this field, it holds the exact socket the user pressed. This
    // replaces the geometry probe (ResolvePinUnderCursor /
    // FindElementsInHostCoordinates) as the PRIMARY source of the wire-drag
    // socket: the spatial query resolved a genuine pin only ~1-in-10 presses
    // (Majo: "pipe preview not visible / only spawns 1 in 10"), because
    // FindElementsInHostCoordinates is unreliable for the small, overhanging
    // pin targets under the canvas RenderTransform. Routed-event delivery to the
    // actually-pressed element is authoritative. ResolvePinUnderCursor stays as
    // a best-effort fallback for the (now rare) case the note didn't arrive.
    private SocketViewModel? _pendingPinPress;

    /// <summary>
    /// Called from a socket pin's hit-target PointerPressed
    /// (NodeView.Pins.cs) to stamp the pressed socket for the imminent
    /// OnHostPointerPressed. See <see cref="_pendingPinPress"/>.
    /// </summary>
    internal void NotePinPress(SocketViewModel sock) => _pendingPinPress = sock;

    /// <summary>
    /// Read-and-clear the pin stamped by the most recent pin-target press.
    /// Always clears so a stamp can never leak into a later, unrelated press
    /// (e.g. a press that never reaches a pin handler).
    /// </summary>
    private SocketViewModel? ConsumePendingPinPress()
    {
        var sock = _pendingPinPress;
        _pendingPinPress = null;
        return sock;
    }

    private void OnHostPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Breadcrumb the click entry point. Majo reports the
        // canvas "always freezes while panning around or clicking around"; the
        // existing watchdog breadcrumb only ever showed the per-frame render
        // tick's tail (misleading). If a click synchronously wedges the UI
        // thread (selection cascade, var-chain highlight, a flyout/layout
        // re-entrancy), this scope is the last-traced activity and names the
        // click path instead. Cheap (the render tick already uses the same
        // tracer every frame). The pan/drag MOVE path's heavy work is already
        // traced inside OnRenderingTick's drains.
        using var _trace = Phoenix.Controls.Shared.Services.UiActivityTrace
            .Begin("Architect.PointerPressed");
        Focus(FocusState.Programmatic);
        var pp = e.GetCurrentPoint(HostRoot);
        var point = pp.Position;
        _lastHostPoint = point;

        // Any press dismisses an open hover tip so it
        // can't float over a drag / pan / wire-drop gesture. Dwell-only arming
        // (NodeView.Pins.StartTooltipTimer) already keeps tips from appearing while
        // the cursor is moving; this clears one that was already up when the press
        // landed. Idempotent + safe when no tip is showing.
        Phoenix.Controls.Architect.WinUI.Controls.TooltipPopup.Hide();

        bool left   = pp.Properties.IsLeftButtonPressed;
        bool right  = pp.Properties.IsRightButtonPressed;
        bool middle = pp.Properties.IsMiddleButtonPressed;

        // In immediate mode there are no per-node
        // visual elements, so resolve "what's under the cursor" from the model.
        // e.OriginalSource is the bare CanvasControl, which makes the visual-tree
        // affordance/interactive/button checks below all return false — so the
        // entire downstream press pipeline (select/drag/marquee/wire/pan) is reused
        // unchanged on the model hit.
        var hit = _useImmediateMode
            ? ResolveModelHit(HostToCanvas(point))
            : HitTagFrom(e.OriginalSource);

        // Resolve the pressed socket pin. PRIMARY source is the
        // deterministic note the pin's own hit-target stamped via NotePinPress
        // (routed PointerPressed reaches the pin descendant before this ancestor
        // handler). FALLBACK is the geometry probe — kept for the rare case the
        // note didn't arrive, but it was the sole resolver before and only
        // succeeded ~1-in-10 presses, which is exactly the "pipe preview not
        // visible" report. ConsumePendingPinPress always clears the stamp so a
        // non-pin press can't inherit a stale value.
        var notedPin = ConsumePendingPinPress();  // always clear any stale stamp
        // PRESS-time placeholder resolution is narrowed to the drawn slot
        // chrome (fullPlaceholderRowBand:false). The generous full-row band is
        // a DROP affordance; at press time it hijacked every body press in a
        // slot row's horizontal stripe into a placeholder wire-drag.
        var pinSock = left
            ? (_useImmediateMode
                ? ResolveSocketAtCanvasPoint(HostToCanvas(point), WireDropModelHitRadius,
                    fullPlaceholderRowBand: false)
                : (notedPin ?? ResolvePinUnderCursor(e)))
            : null;

        // A press that ISN'T on the edited node's
        // inline affordance leaves edit mode (the affordance press returns early
        // below via IsPressOnEditAffordance, keeping the editor live). OriginalSource
        // is a real element only for the one materialized NodeView, so this never
        // fires for GPU-drawn nodes.
        if (_useImmediateMode && _editNode is not null && !IsPressOnEditAffordance(e.OriginalSource, e))
            ExitImmediateEdit();
        if (_useImmediateMode) ClearImmediateHover();   // a press starting a drag clears stale hover

        // Pan: middle-button anywhere; right-button on bare canvas; or LMB
        // while Space is held (drawing-app idiom). Right-press on a node /
        // wire / frame still falls through to OnHostRightTapped so the
        // context menu fires — pan is for empty-canvas right-drag only.
        bool panRequested =
               middle
            || (right && hit is null)
            || (left  && _spaceHeld);
        if (panRequested)
        {
            _drag = DragState.Pan;
            SetTransientHotkeyContext(HotkeyContext.Panning);
            _panStartHost = point;
            _panStartTx   = _vm?.PanX ?? 0;
            _panStartTy   = _vm?.PanY ?? 0;
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        // Right-click on a target (node / wire / frame) → routed via
        // OnHostRightTapped — let it fall through to the context-menu path.
        if (right && !left && !middle) return;

        // A left-press that lands on an inline
        // edit affordance (value pill, socket-label rename, middle-attr pill /
        // bool toggle, DB-picker chevron, or an active inline TextBox / ComboBox)
        // must fall through to that element's own Tapped / DoubleTapped / Click
        // WITHOUT the canvas capturing the pointer. The capture in the wire-drop
        // / node-drag branches below is exactly what made every in-body
        // interaction dead (Majo: "any interaction on the node bodies are
        // dead") — a captured pointer never delivers the child gesture. We do
        // NOT set e.Handled: leaving the press unhandled lets the child's
        // gesture recogniser complete. The socket PIN hit-targets (Ellipses) and
        // the node header / title are not affordances, so wire-drag + node-drag
        // still start on a press there.
        // A genuine pin press (pinSock) is never an edit
        // affordance — skip the veto so the wire-drag below always starts.
        if (left && pinSock is null && IsPressOnEditAffordance(e.OriginalSource, e))
            return;

        // Alt+left on a socket pin → sever every wire on that pin (single
        // undo entry). Pre-T15 OnMouseDown alt-disconnect path. Done before
        // the wire-drop branch so a held Alt suppresses the drag.
        bool altPressed = ModifierDown(Windows.System.VirtualKey.Menu);
        var altTargetSock = pinSock ?? (hit as SocketViewModel);
        if (left && altPressed && altTargetSock is not null && _vm is not null)
        {
            SeverAllWiresOnSocket(altTargetSock);
            e.Handled = true;
            return;
        }

        // Alt+left on a wire → delete that wire. Mirror of pre-T15 alt-on-link.
        if (left && altPressed && hit is LinkViewModel altLink)
        {
            RemoveLink(altLink);
            e.Handled = true;
            return;
        }

        // Flow.Reroute split hitbox. The knot is a 20×20 diamond whose In/Out
        // pins sit on its W/E midpoints — every press inside the body is within
        // the 14px pin radius of BOTH pins, so pre-fix the wire-drop branch
        // swallowed every press and the knot had no move hitbox at all. Split at
        // the vertical centre line: LEFT half moves the node (pinSock cleared so
        // the press falls through to the node-drag branch), RIGHT half always
        // starts an OUTPUT wire. Presses outside the body (near-pin slack) keep
        // the plain nearest-pin behavior, and wire DROPS onto the knot are
        // unaffected (drop resolution happens in HandlePointerEnd, not here).
        if (left && !altPressed && hit is NodeViewModel rerouteNode && rerouteNode.IsReroute)
        {
            var reroutePt = HostToCanvas(point);
            if (reroutePt.X < rerouteNode.X + rerouteNode.Width / 2.0)
                pinSock = null;
            else
                pinSock = rerouteNode.Outputs.Count > 0 ? rerouteNode.Outputs[0] : pinSock;
        }

        // Direct inline-pill editing. A left-press
        // squarely on an editable value pill of a GPU-drawn node enters edit mode
        // in ONE click — TryBeginInlineEditAtCanvasPoint materializes the node's real
        // NodeView over the GPU canvas and opens that pill's TextBox. Pre-fix the only
        // way to reach a pill was to double-tap the node first to materialize it (M3),
        // then click the pill. A pill sits mid-body and pins straddle the node edges,
        // so pinSock is null on a pill hit and this never competes with a wire-drag.
        if (_useImmediateMode && left && !altPressed && pinSock is null
            && hit is NodeViewModel pillNode
            && TryBeginInlineEditAtCanvasPoint(pillNode, HostToCanvas(point)))
        {
            e.Handled = true;
            return;
        }

        // Wire-drop: left-press on a socket pin.
        // 0.11.x followup — bail out if the press lands inside a Button
        // child of the socket row. The chevron picker on DB.* nodes lives
        // inside the input row's Grid (Column 2), which means HitTagFrom
        // walks up and finds the row's Tag=SocketViewModel — pre-fix the
        // wire-drop branch then captured the pointer and the Button's
        // Click never fired (Majo: "chevron doesn't react to clicks").
        // Same guard preserves the wire-drop on bare pin / label hits.
        // Wire-drop start. A geometry-resolved pin press (pinSock) is
        // authoritative and bypasses the IsInsideInteractiveChild gate that
        // would otherwise veto it when OriginalSource resolved to the row
        // container behind the pin ("no pipe on some sockets"). The legacy
        // OriginalSource-walk hit is kept as the fallback for presses geometry
        // didn't classify as a pin.
        var wireSock = pinSock ?? (hit as SocketViewModel);
        if (left && wireSock is not null && !IsInsideButton(e.OriginalSource)
                 && (pinSock is not null || !IsInsideInteractiveChild(e.OriginalSource)))
        {
            var parentNode = FindNodeOwning(wireSock);
            if (parentNode is not null)
            {
                bool ctrlHeld = ModifierDown(Windows.System.VirtualKey.Control);

                // Ctrl+left on a socket already carrying a connection →
                // detach the existing wire and redrag from the OTHER end's
                // source-side socket so the user can re-aim it. Pre-T15
                // OnMouseDown ctrl-rewire branch.
                if (ctrlHeld && _vm is not null && TryRewireExistingLink(wireSock, point))
                {
                    e.Handled = true;
                    return;
                }

                _drag = DragState.WireDrop;
                SetTransientHotkeyContext(HotkeyContext.DraggingWire);
                _wireFromSocket = wireSock;
                _wireFromNode   = parentNode;
                _wireDragPressHost = point;
                StartWirePreview(parentNode, wireSock, HostToCanvas(point));
                // Light the green/red drop-target
                // halos on every socket for the duration of the wire drag.
                // Pre-fix BeginDropHinting/EndDropHinting had zero callers, so
                // the XAML halo bindings + NodeView pin listeners were dead —
                // users saw only the in-flight wire colour, never which sockets
                // would accept the drop. Cleared at every wire-drag exit
                // (HandlePointerEnd / capture-loss salvage / Esc-abort).
                _vm?.BeginDropHinting(wireSock);
                CapturePointer(e);
                EnsureEdgePanTimerRunning();
                e.Handled = true;
                return;
            }
        }

        // Node drag / select: left-press on a node body. Modifier-aware:
        //   plain   → if not in selection, replace; start drag
        //   ctrl    → toggle node in/out of multi-selection (no drag)
        //   shift   → add node to multi-selection (no drag)
        if (left && hit is NodeViewModel n && _vm is not null
                 && !IsInsideInteractiveChild(e.OriginalSource))
        {
            // 0.11.x polish — clear the pin-selection ring when the
            // press lands on a node body (i.e. NOT on a pin). Without
            // this the gold ring from a previously-clicked pin stays
            // visible across unrelated node selects.
            NodeView.ClearPinSelection();
            bool ctrl  = ModifierDown(Windows.System.VirtualKey.Control);
            bool shift = ModifierDown(Windows.System.VirtualKey.Shift);

            if (ctrl)
            {
                var next = new System.Collections.Generic.List<NodeViewModel>(_vm.SelectedNodes);
                if (next.Contains(n)) next.Remove(n); else next.Add(n);
                _vm.SetMultiSelection(next);
                e.Handled = true;
                return;
            }
            if (shift)
            {
                if (!_vm.SelectedNodes.Contains(n))
                {
                    var next = new System.Collections.Generic.List<NodeViewModel>(_vm.SelectedNodes) { n };
                    _vm.SetMultiSelection(next);
                }
                e.Handled = true;
                return;
            }

            // Plain click — if the clicked node isn't already in the multi-set,
            // replace with single-node selection. If it is, keep the multi-set
            // intact so the user can drag the whole group.
            if (!_vm.SelectedNodes.Contains(n)) _vm.Selection = n;

            // Drag starts on top of a wire hit-zone don't reliably
            // fire PointerExited on the wire — clear any sticky hover up
            // front so the bright hover-brushed wire doesn't survive the
            // drag's lifetime as a ghost halo.
            ClearAllLinkHoverFlags();

            // Defer the undo snapshot until PointerReleased
            // (not first cross-deadzone move) so Esc can restore positions
            // from the captured _nodeStartX / _nodeStartY (single node) or
            // _groupDragStarts (multi node) without ever pushing undo. The
            // _dragMutated flag tracks "the drag did cross the deadzone" so
            // PointerReleased knows whether to push undo or not.
            _dragMutated = false;
            _drag = DragState.NodeDrag;
            _dragNode = n;
            _nodeDragStartCanvas = HostToCanvas(point);
            _nodeStartX = n.X;
            _nodeStartY = n.Y;
            BeginGroupDragIfMulti(n, _nodeDragStartCanvas);
            CapturePointer(e);
            EnsureEdgePanTimerRunning();
            e.Handled = true;
            return;
        }

        // Click on a wire: select it.
        if (left && hit is LinkViewModel l)
        {
            if (_vm is not null) _vm.Selection = l;
            // 0.11.x polish — clearing the pin-selection ring on a
            // wire click mirrors the node-body branch above; a wire
            // is "non-pin", so the gold ring should drop with it.
            NodeView.ClearPinSelection();
            e.Handled = true;
            return;
        }

        // Frame: drag body to move; drag corner / edge handle to resize.
        // Also honour Ctrl/Shift modifiers on plain body
        // hits so frames participate in the same multi-select idiom as nodes.
        // IsFrameResizeHandle returns a FrameResizeEdge
        // enum (None / Left / Right / Top / Bottom / corners) so the per-
        // edge transform in OnHostPointerMoved can branch precisely.
        if (left && hit is FrameViewModel fr && _vm is not null)
        {
            // The GPU (immediate-mode) canvas has no tagged Border children, so
            // the visual-tree ResolveFrameResizeEdge / IsFrameHeaderHit always
            // returned None there — which silently routed every frame press into
            // a marquee, breaking both move and resize. Resolve them in canvas
            // space instead; the retained path keeps its visual-tree resolution.
            var framePt = HostToCanvas(point);
            var edge = _useImmediateMode
                ? FrameEdgeAt(fr, framePt.X, framePt.Y, FrameResizeBand())
                : ResolveFrameResizeEdge(e.OriginalSource);
            bool ctrl  = ModifierDown(Windows.System.VirtualKey.Control);
            bool shift = ModifierDown(Windows.System.VirtualKey.Shift);

            // Ctrl/Shift on a frame body (not a resize handle) → modifier-
            // aware selection without starting a drag. Resize-handle hits
            // always proceed to the resize branch — previously the only
            // way to size a frame was through the handle, so Ctrl/Shift on
            // a handle never had a coherent meaning, and forcing the resize
            // path through is the safer / less-surprising choice.
            if (edge == FrameResizeEdge.None && (ctrl || shift))
            {
                if (ctrl)
                {
                    var next = new System.Collections.Generic.List<FrameViewModel>(_vm.SelectedFrames);
                    if (next.Contains(fr)) next.Remove(fr); else next.Add(fr);
                    _vm.SetSelectedFrames(next);
                }
                else // shift
                {
                    if (!_vm.SelectedFrames.Contains(fr))
                    {
                        var next = new System.Collections.Generic.List<FrameViewModel>(_vm.SelectedFrames) { fr };
                        _vm.SetSelectedFrames(next);
                    }
                }
                e.Handled = true;
                return;
            }

            // Frames only move
            // when the press lands on the header strip (Tag="header") or the
            // label TextBlock (Tag="label"). Resize-handle hits (edge != None)
            // proceed into the resize branch unchanged.
            bool isHeaderHit = edge == FrameResizeEdge.None
                               && (_useImmediateMode
                                   ? IsFrameHeaderHitWin2D(fr, framePt.X, framePt.Y)
                                   : IsFrameHeaderHit(e.OriginalSource));

            // Body bare-click (no modifier, no header, no
            // resize-handle) behaves as a click on bare canvas: drag past the
            // dead-zone grows a marquee starting at the press point, and a
            // zero-distance release clears the current selection. Frame
            // selection is reachable through the header click below, through
            // marquee intersection, or through right-click context menu.
            // Pre-fix this branch was a "selection-only" early return that
            // ate left-clicks: marquee could never start on a frame, and
            // clicking inside a frame couldn't deselect (the body silently
            // single-selected the frame and returned Handled).
            if (edge == FrameResizeEdge.None && !isHeaderHit)
            {
                if (TryQuickKeySpawn(point))
                {
                    e.Handled = true;
                    return;
                }
                _drag = DragState.Marquee;
                BeginMarquee(point);
                CapturePointer(e);
                e.Handled = true;
                return;
            }

            // Header / resize-handle path: if the clicked frame isn't already
            // in the multi-selection, swap to a single-frame selection. If it
            // IS in the multi-set, keep the multi-set intact so the whole
            // group moves together.
            if (edge == FrameResizeEdge.None && !_vm.SelectedFrames.Contains(fr))
            {
                _vm.SetSelectedFrames(new[] { fr });
            }

            // Defer undo snapshot until release — same dead-zone + deferred-
            // undo discipline as the node-drag branch above.
            _frameMutated = false;
            _dragFrame = fr;
            _frameDragStartCanvas = HostToCanvas(point);
            _frameStartX = fr.X; _frameStartY = fr.Y;
            _frameStartW = fr.Width; _frameStartH = fr.Height;
            _frameResizeEdge = edge;
            _drag = edge != FrameResizeEdge.None ? DragState.FrameResize : DragState.FrameMove;

            // Capture every selected frame's start position
            // so a body-drag on a multi-selected frame moves the whole group.
            // Resize gestures don't make sense as group-resize so they only
            // size the directly-clicked frame.
            BeginFrameGroupDragIfMulti(fr);

            // Capture contained nodes so a frame move
            // carries its contents (UE-Blueprints comment-box semantics).
            // Only fires on FrameMove (not FrameResize) — resizing a comment
            // box shouldn't drag its members. Containment is snapshotted
            // ONCE here; nodes don't slip in / out mid-drag.
            if (_drag == DragState.FrameMove)
                BeginFrameContentDrag(fr);

            CapturePointer(e);
            EnsureEdgePanTimerRunning();
            e.Handled = true;
            return;
        }

        // Left-press on bare canvas → quick-key spawn (if armed) or marquee select.
        if (left && hit is null && _vm is not null)
        {
            if (TryQuickKeySpawn(point))
            {
                e.Handled = true;
                return;
            }
            // 0.11.x polish — clear the canvas-wide pin-selection ring
            // when the user clicks empty canvas. Pre-fix the gold ring
            // around a previously-clicked pin stuck around forever
            // (Majo: "small square, barely noticeable, never disappears");
            // an empty-canvas click is the natural "clear" gesture.
            NodeView.ClearPinSelection();
            _drag = DragState.Marquee;
            BeginMarquee(point);
            CapturePointer(e);
            e.Handled = true;
            return;
        }
    }

    private void OnHostPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(HostRoot);
        var point = pp.Position;
        _lastHostPoint = point;

        // Frame edge-resize
        // discoverability. While no drag is in flight, mirror the visual-tree
        // hit-test under the cursor onto the host's ProtectedCursor so the
        // eight (otherwise-invisible) resize zones around every frame surface
        // a directional cursor on hover. The active-drag branches below own
        // the cursor themselves (Pan / NodeDrag / Marquee / Wire / FrameMove
        // all leave the default arrow), so we only repaint when Idle.
        if (_drag == DragState.Idle)
        {
            // Model-driven frame-edge cursor + hover highlight (the retained
            // path's per-element pointer events don't exist on the GPU canvas).
            // One host→canvas projection and ONE (cached) model hit-test feed
            // both — each previously ran its own full scene walk per move.
            if (_useImmediateMode) UpdateIdleHoverWin2D(HostToCanvas(point));
            else UpdateFrameEdgeHoverCursor(e.OriginalSource);
        }

        switch (_drag)
        {
            case DragState.Pan:
                if (_vm is not null)
                {
                    // Queue the target pan for the
                    // next CompositionTarget.Rendering tick instead of
                    // poking _vm.PanX/Y + ApplyViewTransform on every
                    // pointer-move. A 120 Hz pan burst collapses to one
                    // transform write per displayed frame.
                    QueuePan(
                        _panStartTx + (point.X - _panStartHost.X),
                        _panStartTy + (point.Y - _panStartHost.Y));
                }
                e.Handled = true;
                break;

            case DragState.NodeDrag:
                if (_vm is not null && _dragNode is not null)
                {
                    var canvasPoint = HostToCanvas(point);
                    double mdx = canvasPoint.X - _nodeDragStartCanvas.X;
                    double mdy = canvasPoint.Y - _nodeDragStartCanvas.Y;
                    if (Math.Abs(mdx) + Math.Abs(mdy) < DragDeadZonePx
                        && !_dragMutated)
                    {
                        // Inside the dead zone — keep the press alive but don't
                        // move the node yet. Also defer the
                        // undo push (now happens on release, not on first
                        // cross-deadzone move) so Esc can roll back without
                        // requiring a second Ctrl+Z.
                        e.Handled = true;
                        break;
                    }
                    _dragMutated = true;

                    // A multi-node group applied inline per
                    // pointer-move fires ~2 PropertyChanged dispatches per node
                    // per event; coalesce the group translate to the render tick
                    // (same idiom as frame-content drag). A single-node drag has
                    // no fan-out, so it stays inline (immediate).
                    if (_groupDragStarts.Count > 0)
                    {
                        RequestGroupDragUpdate(canvasPoint);
                    }
                    else
                    {
                        double targetX = _nodeStartX + mdx;
                        double targetY = _nodeStartY + mdy;
                        _vm.TranslateNode(_dragNode, targetX - _dragNode.X, targetY - _dragNode.Y);
                    }

                    // Recompute alignment guides + collision
                    // overlay against every visible non-selection node and
                    // repaint the transient overlay layer. Cleared on
                    // PointerReleased / AbortInFlightGesture so the hints
                    // never outlive the drag.
                    //
                    // 2026-05-22 — the rebuild is now coalesced via
                    // _snapGuidesDirty; the next CompositionTarget.Rendering
                    // tick runs UpdateSnapGuidesForDrag exactly once per
                    // displayed frame. Mark dirty here instead of rebuilding
                    // inline so a 120 Hz pointer burst doesn't churn the
                    // overlay-layer Children collection 120 times per second.
                    RequestSnapGuidesUpdate();
                }
                e.Handled = true;
                break;

            case DragState.Marquee:
                UpdateMarquee(point);
                e.Handled = true;
                break;

            case DragState.FrameMove:
                if (_dragFrame is not null)
                {
                    var p = HostToCanvas(point);
                    double dx = p.X - _frameDragStartCanvas.X;
                    double dy = p.Y - _frameDragStartCanvas.Y;
                    // Deadzone + deferred-undo parity
                    // with the node path: no PushUndo at first motion (the
                    // snapshot is committed on release via CommitFrameDragUndo),
                    // so a sub-deadzone twitch never lands a spurious undo step
                    // and Esc reverts cleanly.
                    if (!_frameMutated
                        && Math.Abs(dx) + Math.Abs(dy) < DragDeadZonePx)
                    {
                        e.Handled = true;
                        break;
                    }
                    _frameMutated = true;

                    // Drag every frame in the multi-set
                    // together when a group drag is in flight; fall back to
                    // single-pivot translate otherwise.
                    if (!TryUpdateFrameGroupDrag(dx, dy))
                    {
                        double targetX = _frameStartX + dx;
                        double targetY = _frameStartY + dy;
                        _dragFrame.Translate(targetX - _dragFrame.X, targetY - _dragFrame.Y);
                    }

                    // Ripple the same delta into every
                    // node captured at drag-start (contents follow the frame).
                    // Stash the latest delta + dirty flag;
                    // the OnRenderingTick drain runs FlushFrameContentDrag at
                    // displayed-frame cadence so a 120 Hz pointer burst
                    // collapses to one N-node translate pass per displayed
                    // frame instead of one per pointer event.
                    RequestFrameContentDragUpdate(dx, dy);
                }
                e.Handled = true;
                break;

            case DragState.FrameResize:
                if (_dragFrame is not null && _frameResizeEdge != FrameResizeEdge.None)
                {
                    var p = HostToCanvas(point);
                    double rdx = p.X - _frameDragStartCanvas.X;
                    double rdy = p.Y - _frameDragStartCanvas.Y;
                    // Deferred-undo parity (see FrameMove).
                    if (!_frameMutated
                        && Math.Abs(rdx) + Math.Abs(rdy) < DragDeadZonePx)
                    {
                        e.Handled = true;
                        break;
                    }
                    _frameMutated = true;

                    // Apply the per-edge transform from the
                    // captured pre-drag bounds. ResizeByEdge handles minimum-
                    // size clamping (40×40) including the origin-shifting
                    // top / left edges.
                    _dragFrame.ResizeByEdge(
                        _frameResizeEdge,
                        rdx, rdy,
                        _frameStartX, _frameStartY,
                        _frameStartW, _frameStartH);
                }
                e.Handled = true;
                break;

            case DragState.WireDrop:
                if (_wireFromSocket is not null && _wireFromNode is not null)
                {
                    // 0.11.x polish (wire-drag lag) — split the per-move work
                    // into the cheap path (geometry mutation) and the expensive
                    // path (hit-test + brush color resolution). The geometry
                    // update is 4 Point writes on a cached PathGeometry — runs
                    // inline so the wire stays glued to the cursor at gaming-
                    // mouse 1000 Hz. The hit-test calls
                    // VisualTreeHelper.FindElementsInHostCoordinates which
                    // walks the visible subtree; coalesce it to the next
                    // CompositionTarget.Rendering tick so a high-Hz pointer
                    // burst can't pin the UI thread re-doing it per event.
                    // Keep the LAST resolved stroke until the tick refreshes
                    // it so the wire never goes invisible mid-drag.
                    UpdateWirePreview(_wireFromNode, _wireFromSocket,
                                      HostToCanvas(point),
                                      WirePreview.Stroke
                                        ?? ResolveWirePreviewColor(_wireFromSocket, null));
                    RequestWireDropHitTestUpdate();
                }
                e.Handled = true;
                break;
        }
    }

    private void OnHostPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Breadcrumb the release path — HandlePointerEnd runs the wire-drop
        // resolution / link creation / gesture settle synchronously on the UI
        // thread, none of which the PointerPressed or render-tick scopes cover.
        using var _trace = Phoenix.Controls.Shared.Services.UiActivityTrace
            .Begin("Architect.PointerReleased");
        // Refresh the cursor anchor with the actual release position. The
        // Move handler updates _lastHostPoint on every Moved, but if the user
        // releases without moving (a quick click-drop on a tiny socket), the
        // anchor would otherwise be stale. ResolveHitFromEvent falls back to
        // _lastHostPoint when OriginalSource is the captured HostRoot, so a
        // fresh value here directly improves the wire-drop hit-test.
        try { _lastHostPoint = e.GetCurrentPoint(HostRoot).Position; } catch { }
        HandlePointerEnd(e, cancelled: false);
    }

    /// <summary>
    /// PointerCanceled / PointerCaptureLost handler — the gesture was aborted
    /// (capture stolen by a flyout, touch lifted off-screen, etc). Tear down
    /// any in-flight drag/wire state but DO NOT commit it (no TryCreateLink,
    /// no implicit click). The cursor target at this moment is unreliable,
    /// so treating these events as "release here" can spawn wires the user
    /// never aimed at.
    /// </summary>
    /// <remarks>
    /// PointerCaptureLost is a routed event that bubbles. When PointerPressed
    /// on a child socket-pin element implicitly captures the pointer to that
    /// child, then this control's press handler steals capture to HostRoot,
    /// the child's capture-loss bubbles up here — that's the handoff
    /// completing normally, not a cancellation. Filter on
    /// e.OriginalSource == HostRoot so only HostRoot's own capture loss
    /// (real abort: flyout, touch lift, etc.) tears down the gesture.
    /// Without this guard, every wire-drop is aborted before the user has
    /// even moved the mouse and TryCreateLink never runs.
    ///
    /// Wire-drop salvage: when a capture-loss arrives WHILE _drag is still
    /// WireDrop (the OS released capture out-of-order on a normal release
    /// — e.g. PointerCaptureLost beats PointerReleased on some hardware /
    /// driver combos), tear-down would silently swallow the user's wire even
    /// though the cursor is sitting on a perfectly valid target socket. So
    /// we hit-test once at the last cursor anchor: if it lands on a Socket,
    /// treat the capture-loss as a normal release and commit the wire. Any
    /// other target — empty canvas, node body, nothing — falls through to
    /// the original cancel path so we don't synthesise drops the user
    /// didn't aim at.
    /// </remarks>
    private void OnHostPointerCancelled(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, HostRoot)) return;

        if (_drag == DragState.WireDrop && _wireFromSocket is not null)
        {
            var salvaged = ResolveHitFromEvent(e);
            if (salvaged is SocketViewModel target)
            {
                // TryCreateLink owns the undo snapshot for the wire
                // creation (see LogicCanvasView.xaml.cs PushUndo() before
                // AddLink). Do NOT add a PushUndo() here — that would push a
                // duplicate snapshot for the same user action. If a future
                // refactor extracts the undo logic out of TryCreateLink, this
                // salvage branch must regain its own PushUndo() call so the
                // capture-loss-salvaged wire remains undoable.
                TryCreateLink(_wireFromSocket, target);
                EndWirePreview();
                _vm?.EndDropHinting(); // clear halos
                _wireFromSocket = null;
                _wireFromNode   = null;
                _drag = DragState.Idle;
                ClearTransientHotkeyContext();
                ReleasePointer(e);
                e.Handled = true;
                return;
            }
        }

        HandlePointerEnd(e, cancelled: true);
    }

    private void HandlePointerEnd(PointerRoutedEventArgs e, bool cancelled)
    {
        var hit = cancelled ? null : ResolveHitFromEvent(e);

        // Off-screen wire-drop: when virtualization has culled the target
        // node out of the visual tree (it scrolled into view via edge-pan mid-drag),
        // the visual hit-test above misses. Resolve the target socket model-side so
        // the wire still connects to the intended (unmounted) socket.
        if (hit is null && !cancelled && _drag == DragState.WireDrop && _wireFromSocket is not null
            && (_enableNodeVirtualization || _useImmediateMode))
        {
            var dropCanvas = HostToCanvas(ResolveLastCursorHostPoint(e));
            hit = ResolveSocketAtCanvasPoint(dropCanvas, WireDropModelHitRadius);
            // Node-body drop parity — when the drop
            // isn't on a specific pin, fall back to the node under the cursor so the
            // "auto-connect to first compatible input" branch below still fires.
            if (hit is null && _useImmediateMode)
                hit = ResolveModelHit(dropCanvas) as NodeViewModel;
        }

        switch (_drag)
        {
            case DragState.WireDrop when _wireFromSocket is not null:
                BubbleDiag($"drop: from \"{_wireFromSocket.Label}\" ({_wireFromSocket.DataType}) → " +
                    (hit is SocketViewModel ht
                        ? $"socket \"{ht.Label}\"{(ht.IsPlaceholder ? " [PLACEHOLDER]" : "")} on \"{ht.ParentNode?.Title}\""
                        : hit is NodeViewModel bn ? $"body \"{bn.Title}\"" : "nothing"));
                // Click-drift tolerance: a press on a managed "+" slot that
                // travelled only a few host pixels is a CLICK on that slot,
                // regardless of what the release resolved to — the adjacent
                // slot row's full-width drop band, the node body, or nothing
                // (each of which used to eat the click silently). Activate the
                // PRESSED slot and skip the drop interpretation entirely.
                if (!cancelled && _wireFromNode is not null
                    && PlaceholderActivator.IsManagedPlaceholder(_wireFromNode.Model, _wireFromSocket.Model))
                {
                    var endHost = ResolveLastCursorHostPoint(e);
                    double cdx = endHost.X - _wireDragPressHost.X;
                    double cdy = endHost.Y - _wireDragPressHost.Y;
                    if ((cdx * cdx) + (cdy * cdy)
                        <= PlaceholderClickDriftPx * PlaceholderClickDriftPx)
                    {
                        BubbleDiag($"click-drift: release within {PlaceholderClickDriftPx}px of the " +
                            $"\"{_wireFromSocket.Label}\" press → activate the slot");
                        TryActivatePlaceholderOnClick(_wireFromSocket);
                        EndWirePreview();
                        _vm?.EndDropHinting(); // clear halos
                        _wireFromSocket = null;
                        _wireFromNode   = null;
                        break;
                    }
                }
                if (hit is SocketViewModel target)
                {
                    TryCreateLink(_wireFromSocket, target);
                }
                else if (hit is NodeViewModel bodyNode && bodyNode != _wireFromNode)
                {
                    // Drop on a node body (not a specific socket) →
                    // auto-connect to the first compatible socket on that node.
                    // Pre-T15 OnMouseUp wire-drop path.
                    var compat = FindFirstCompatibleInput(_wireFromSocket, bodyNode);
                    if (compat is not null) TryCreateLink(_wireFromSocket, compat);
                    else
                        // Surface the no-op so a
                        // body-drop onto a node with no compatible socket isn't
                        // silent — consistent with TryCreateLink's own rejection
                        // logging (no modal, per the repeatable-rejection rule).
                        GlobalLogger.Log(
                            $"Wire not connected: \"{bodyNode.Title}\" has no socket compatible with {_wireFromSocket.DataType}",
                            "Architect.Canvas", LogLevel.System);
                }
                else if (hit is null && !cancelled)
                {
                    // Drop on empty canvas → filtered spawn menu. The menu lets
                    // the user pick a template whose first compatible input gets
                    // auto-connected to the source socket. Pre-T15
                    // ShowFilteredSpawnMenu. Captures source socket so the post-
                    // spawn click-handler can wire it up.
                    var dropPoint = ResolveLastCursorHostPoint(e);
                    ShowFilteredSpawnPaletteForWireDrop(_wireFromSocket, dropPoint);
                }
                EndWirePreview();
                _vm?.EndDropHinting(); // clear halos
                _wireFromSocket = null;
                _wireFromNode   = null;
                break;

            case DragState.NodeDrag:
                // Apply any group-drag delta still pending from
                // the last PointerMoved BEFORE committing undo / clearing the
                // group, so the nodes land exactly under the cursor (zero offset)
                // and the undo snapshot captures the final positions, not a
                // frame-stale pose. No-op for a single-node drag (nothing stashed).
                FlushGroupDragIfDirty();
                // Push undo on release (not on first move),
                // and only when the drag actually mutated positions. The undo
                // controller snapshots the CURRENT graph state at PushUndo
                // time, so we briefly restore the pre-drag positions,
                // snapshot, and re-apply the drag — that way Ctrl+Z lands on
                // the pre-drag pose and an Esc-cancel mid-drag never touches
                // the stack. A no-mutation drag (click below deadzone) skips
                // the snapshot entirely.
                if (_dragMutated && _dragNode is not null && _vm is not null)
                {
                    CommitNodeDragUndo(_dragNode);
                }
                _dragNode = null;
                _dragMutated = false;
                EndGroupDrag();
                break;

            case DragState.Marquee:
                EndMarquee();
                break;

            case DragState.FrameMove:
            case DragState.FrameResize:
                // Push undo on release (not on first
                // motion), and only when the gesture actually mutated the
                // frame. CommitFrameDragUndo briefly reverts to the captured
                // pre-drag bounds, snapshots, then re-applies the end-state —
                // so Ctrl+Z lands on the pre-drag pose and an Esc-cancel never
                // touches the stack. wasResize must be read before the state
                // reset below.
                if (_frameMutated && _dragFrame is not null && _vm is not null)
                {
                    CommitFrameDragUndo(wasResize: _drag == DragState.FrameResize);
                }
                _dragFrame = null;
                _frameMutated = false;
                _frameResizeEdge = FrameResizeEdge.None;
                EndFrameGroupDrag();
                // Release the captured contained-node set.
                EndFrameContentDrag();
                break;

            case DragState.Pan:
                break;
        }

        // Clear any in-flight snap-guide / collision
        // overlay so the hints never outlive the drag they were drawn for.
        ClearSnapGuideOverlay();

        _drag = DragState.Idle;
        ClearTransientHotkeyContext();
        StopEdgePanTimer();
        ReleasePointer(e);
        e.Handled = true;
    }

    private void OnHostPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null) return;
        var pp = e.GetCurrentPoint(HostRoot);
        int delta = pp.Properties.MouseWheelDelta;
        if (delta == 0) return;

        // Breadcrumb the zoom/pan input edge — the 2026-07-14 freeze hit while
        // panning/zooming, and input handlers dispatch in the uninstrumented
        // window between frames. Cheap (flag writes below; the heavy flushes
        // run inside the traced render tick).
        using var _trace = Phoenix.Controls.Shared.Services.UiActivityTrace
            .Begin("Architect.PointerWheel");

        bool ctrl  = ModifierDown(Windows.System.VirtualKey.Control);
        bool shift = ModifierDown(Windows.System.VirtualKey.Shift);

        // Wheel zoom + wheel pan used to write PanX/Y/Zoom +
        // ApplyViewTransform synchronously on every detent — bypassing the
        // CompositionTarget.Rendering coalescer the drag-pan path uses.
        // Touchpad inertia / smooth-scroll wheels fire up to ~120 events/s
        // which produced visibly more transform writes than the display
        // could consume. Route through QueuePan / QueueZoom so a wheel burst
        // collapses into one transform write per displayed frame, matching
        // the drag-pan pipeline.
        //
        // Ctrl-gated wheel zoom — Math.Pow(1.1, delta / 120.0) gives a
        // smooth per-detent step (each notch ≈ ±10%) with cursor-anchored
        // hold so the canvas point under the cursor stays put through the
        // zoom. Plain wheel (no Ctrl) becomes vertical pan; Shift+wheel
        // (no Ctrl) becomes horizontal pan — UE Blueprints / Figma idiom.
        // Ctrl+Shift+wheel switches the per-detent step from 1.1 (±10%)
        // to 1.025 (±2.5%) for fine-grained zoom control near targets like
        // 100%, 150%, 200% — 0.10.0 UX P2 "Shift-fine modifier on the 1.1
        // zoom step".
        if (ctrl)
        {
            // QueueZoom resolves the target zoom/pan tuple against the LIVE
            // _vm.Zoom/PanX/PanY when the rendering tick fires, not the
            // pre-burst values — so a fast 5-detent zoom anchors at the
            // cursor position the user actually sees on screen.
            double stepBase = shift ? 1.025 : 1.1;
            QueueWheelZoom(delta, stepBase, pp.Position);
            e.Handled = true;
            return;
        }

        // Pan magnitude: scale the wheel delta into pixels of pan. 120 is
        // the standard mouse-wheel detent unit; tune the multiplier so a
        // detent feels roughly 60 px of pan, matching the canvas grid step
        // ×1.5 (the body-of-a-typical-node distance).
        double pixels = (delta / 120.0) * 60.0;
        if (shift) QueueWheelPanDelta(pixels, 0);
        else       QueueWheelPanDelta(0, pixels);
        e.Handled = true;
    }

    /// <summary>
    /// Start the edge-pan timer if it isn't already running. The timer ticks
    /// at ~60 Hz; on each tick we check whether the cursor sits within
    /// <see cref="EdgePanRadiusPx"/> of any host edge and, if so, advance the
    /// pan in that direction. Cheap when no drag is active because we exit
    /// early if <see cref="_drag"/> isn't one of the qualifying states.
    /// </summary>
    private void EnsureEdgePanTimerRunning()
    {
        if (_edgePanTimer is not null) return;
        _edgePanTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _edgePanTimer.Tick += OnEdgePanTick;
        _edgePanTimer.Start();
    }

    private void StopEdgePanTimer()
    {
        if (_edgePanTimer is null) return;
        try { _edgePanTimer.Stop(); _edgePanTimer.Tick -= OnEdgePanTick; }
        catch { /* shutdown best-effort */ }
        _edgePanTimer = null;
    }

    private void OnEdgePanTick(object? sender, object e)
    {
        if (_vm is null) { StopEdgePanTimer(); return; }
        // Only auto-pan while a drag that benefits from edge-extension is
        // in flight. Pan / Idle / Marquee are excluded (pan already drives
        // PanX/Y itself; idle / marquee don't need to extend the viewport).
        bool eligible = _drag is DragState.NodeDrag
                                or DragState.FrameMove
                                or DragState.FrameResize
                                or DragState.WireDrop;
        if (!eligible) { StopEdgePanTimer(); return; }

        // Compute axis-wise pan velocity from cursor distance to each edge.
        double w = HostRoot.ActualWidth;
        double h = HostRoot.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double cx = _lastHostPoint.X;
        double cy = _lastHostPoint.Y;

        double vx = 0, vy = 0;
        if (cx < EdgePanRadiusPx)
            vx = EdgePanSpeedPx * (1.0 - cx / EdgePanRadiusPx);
        else if (cx > w - EdgePanRadiusPx)
            vx = -EdgePanSpeedPx * (1.0 - (w - cx) / EdgePanRadiusPx);
        if (cy < EdgePanRadiusPx)
            vy = EdgePanSpeedPx * (1.0 - cy / EdgePanRadiusPx);
        else if (cy > h - EdgePanRadiusPx)
            vy = -EdgePanSpeedPx * (1.0 - (h - cy) / EdgePanRadiusPx);

        if (vx == 0 && vy == 0) return;

        _vm.PanX += vx;
        _vm.PanY += vy;
        ApplyViewTransform();

        // Ripple the auto-pan into any in-flight drag so the dragged node
        // doesn't appear "stuck" while the canvas slides under it. We do
        // this by re-running the Move handler logic with the same anchor
        // point — pan changes the canvas-space coords under the same host
        // pixel, so the drag's delta naturally widens.
        // The node-drag path now gates on _dragMutated (the "we've crossed
        // the deadzone" flag) instead of _dragHasPushedUndo, since undo is
        // deferred to release on this path.
        if (_drag == DragState.NodeDrag && _dragNode is not null && _vm is not null)
        {
            var canvasPoint = HostToCanvas(_lastHostPoint);
            double mdx = canvasPoint.X - _nodeDragStartCanvas.X;
            double mdy = canvasPoint.Y - _nodeDragStartCanvas.Y;
            if (_dragMutated)
            {
                // Route the edge-pan ripple through the SAME
                // coalescer as the pointer-move path. Applying it inline here
                // while the move path coalesces would double-move the group on
                // any frame where both fire; both now just stash the latest
                // point and the one render-tick drain applies it once.
                if (_groupDragStarts.Count > 0)
                {
                    RequestGroupDragUpdate(canvasPoint);
                }
                else
                {
                    double targetX = _nodeStartX + mdx;
                    double targetY = _nodeStartY + mdy;
                    _vm.TranslateNode(_dragNode, targetX - _dragNode.X, targetY - _dragNode.Y);
                }
            }
        }
        else if (_drag == DragState.FrameMove && _dragFrame is not null)
        {
            var p = HostToCanvas(_lastHostPoint);
            double dx = p.X - _frameDragStartCanvas.X;
            double dy = p.Y - _frameDragStartCanvas.Y;
            // Gate the edge-pan ripple on _frameMutated
            // (the "crossed deadzone" flag) instead of _dragHasPushedUndo, since
            // the frame undo push is now deferred to release.
            if (_frameMutated)
            {
                if (!TryUpdateFrameGroupDrag(dx, dy))
                {
                    double targetX = _frameStartX + dx;
                    double targetY = _frameStartY + dy;
                    _dragFrame.Translate(targetX - _dragFrame.X, targetY - _dragFrame.Y);
                }
                // Edge-pan ripple needs to carry the
                // frame's contents too, otherwise the auto-pan would visually
                // strand contained nodes behind the moving frame.
                // Route through the dirty-flag drain so
                // the edge-pan tick + the pointer-move tick collapse into a
                // single TranslateNode pass per displayed frame.
                RequestFrameContentDragUpdate(dx, dy);
            }
        }
    }

    /// <summary>
    /// Cancel whatever drag is in flight without committing it (no
    /// TryCreateLink, no marquee selection, no node-translate). Mirrors the
    /// state-cleanup half of HandlePointerEnd minus the event-bound parts
    /// (no pointer release, no event Handled flag) — used by the Escape-key
    /// path which doesn't have a PointerRoutedEventArgs to release against.
    /// Releases pointer capture if we hold it.
    /// </summary>
    private void AbortInFlightGesture()
    {
        switch (_drag)
        {
            case DragState.WireDrop:
                EndWirePreview();
                _vm?.EndDropHinting(); // clear halos
                _wireFromSocket = null;
                _wireFromNode   = null;
                break;

            case DragState.NodeDrag:
                // Restore pre-drag positions on Esc-cancel
                // without ever pushing undo. The captured _nodeStartX/Y holds
                // the single-node start; _groupDragStarts holds every member
                // of a multi-node group drag. Previously the undo push
                // happened at first-motion, so larger drags required a
                // second Ctrl+Z after Esc to fully unwind — now Esc is the
                // single source of truth for cancellation.
                if (_vm is not null && _dragNode is not null && _dragMutated)
                {
                    RevertNodeDragToStart();
                }
                _dragNode = null;
                _dragMutated = false;
                EndGroupDrag();
                break;

            case DragState.Marquee:
                EndMarquee();
                break;

            case DragState.FrameMove:
            case DragState.FrameResize:
                // Esc-cancel now reverts the frame
                // (and, for a move, its group + carried contents) to the
                // captured pre-drag pose WITHOUT pushing undo — parity with the
                // node-drag Esc path. Pre-fix the frame stayed where the gesture
                // left it and a spurious undo step (pushed at first motion) sat
                // on the stack, so Esc needed a follow-up Ctrl+Z to unwind.
                if (_frameMutated && _dragFrame is not null && _vm is not null)
                {
                    RevertFrameDragToStart(wasResize: _drag == DragState.FrameResize);
                }
                _dragFrame = null;
                _frameMutated = false;
                _frameResizeEdge = FrameResizeEdge.None;
                EndFrameGroupDrag();
                EndFrameContentDrag();
                break;
        }

        // Esc-cancel + capture-loss tear-down: scrub any sticky
        // wire-hover halo so an aborted gesture doesn't leave a wire
        // highlighted as if the cursor were still on its hit-zone.
        ClearAllLinkHoverFlags();

        // Clear any in-flight snap-guide / collision
        // overlay on cancel so the guides don't outlive the gesture.
        ClearSnapGuideOverlay();

        _drag = DragState.Idle;
        ClearTransientHotkeyContext();
        StopEdgePanTimer();

        // Best-effort capture release. The canonical ReleasePointer call
        // requires a PointerRoutedEventArgs we don't have here; reach into
        // HostRoot.PointerCaptures and release every captured pointer so a
        // mid-drag Escape doesn't leave the host hostage to the gesture.
        if (_hasCapture)
        {
            try
            {
                var captures = HostRoot.PointerCaptures;
                if (captures is not null)
                {
                    foreach (var p in System.Linq.Enumerable.ToArray(captures))
                        HostRoot.ReleasePointerCapture(p);
                }
            }
            catch { /* shutdown best-effort */ }
            _hasCapture = false;
        }
    }

    /// <summary>
    /// Resolve a hit-tagged object from a pointer event. First tries the
    /// event's OriginalSource (works for hover during a drag). If that
    /// returns null — typically on Release while the pointer is captured to
    /// HostRoot, where OriginalSource reports the captured element rather
    /// than the under-cursor child — falls back to an explicit hit-test at
    /// the last known cursor position. The fallback is what makes
    /// wire-drop reliably commit when the user releases on a target socket.
    /// </summary>
    private object? ResolveHitFromEvent(PointerRoutedEventArgs e)
    {
        var byOriginalSource = HitTagFrom(e.OriginalSource);
        if (byOriginalSource is not null) return byOriginalSource;

        // Pointer-position fallback. FindElementsInHostCoordinates returns the
        // descendants under the host point in topmost-first order; walk each
        // one upward via HitTagFrom until we find a tagged element.
        try
        {
            var elements = VisualTreeHelper.FindElementsInHostCoordinates(_lastHostPoint, this);
            foreach (var el in elements)
            {
                var tag = HitTagFrom(el);
                if (tag is not null) return tag;
            }
        }
        catch { /* hit-test best-effort — never throw from a release handler */ }
        return null;
    }

    // ─── Pointer capture helpers ─────────────────────────────────────────

    private void CapturePointer(PointerRoutedEventArgs e)
    {
        // PointerRoutedEventArgs.Pointer can be null in rare pre-contact /
        // cancelled-before-init scenarios (the WinForms baseline never saw this
        // because MouseEventArgs always carried a valid Location/Button).
        if (e?.Pointer is not null && HostRoot.CapturePointer(e.Pointer))
        {
            _capturedPointerId = e.Pointer.PointerId;
            _hasCapture = true;
        }
    }

    private void ReleasePointer(PointerRoutedEventArgs e)
    {
        if (e?.Pointer is null || !_hasCapture) return;
        try { HostRoot.ReleasePointerCapture(e.Pointer); }
        catch { /* pointer already released — fine */ }
        _hasCapture = false;
    }

    // ─── Wire preview ────────────────────────────────────────────────────
    //
    // PERF: previously UpdateWirePreview
    // allocated a fresh PathGeometry + PathFigure + BezierSegment on every
    // PointerMoved during a wire drag (60+ Hz). Now we allocate the geometry
    // once and mutate StartPoint / Point1-3 in place — PathGeometry & friends
    // are DependencyObjects so property writes flow through the standard
    // change-tracking path. Same visual output, ~zero allocations per frame.
    private PathGeometry? _previewGeometry;
    private PathFigure? _previewFigure;
    private BezierSegment? _previewSegment;

    private void EnsurePreviewGeometry()
    {
        if (_previewGeometry is not null) return;
        _previewSegment  = new BezierSegment();
        _previewFigure   = new PathFigure { IsClosed = false };
        _previewFigure.Segments.Add(_previewSegment);
        _previewGeometry = new PathGeometry();
        _previewGeometry.Figures.Add(_previewFigure);
    }

    private void StartWirePreview(NodeViewModel node, SocketViewModel sock, Point canvasCursor)
    {
        EnsurePreviewGeometry();
        WirePreview.Data = _previewGeometry;
        WirePreview.Visibility = Visibility.Visible;
        UpdateWirePreview(node, sock, canvasCursor, ResolveWirePreviewColor(sock, null));
    }

    private void UpdateWirePreview(NodeViewModel node, SocketViewModel sock, Point canvasCursor, Brush stroke)
    {
        EnsurePreviewGeometry();
        WirePreview.Stroke = stroke;

        var (ax, ay) = sock.Anchor();
        double fx = ax + node.X;
        double fy = ay + node.Y;
        double tx = canvasCursor.X;
        double ty = canvasCursor.Y;
        // Route through BezierPath.ControlDistance so the preview wire
        // matches the committed wire — including the tangent floor + the
        // vertical-distance term that keeps back-routing previews from
        // flattening through the source node.
        double dx = BezierPath.ControlDistance(fx, fy, tx, ty);

        _previewFigure!.StartPoint = new Point(fx, fy);
        _previewSegment!.Point1    = new Point(fx + dx, fy);
        _previewSegment!.Point2    = new Point(tx - dx, ty);
        _previewSegment!.Point3    = new Point(tx, ty);
    }

    private void EndWirePreview()
    {
        WirePreview.Visibility = Visibility.Collapsed;
        // Keep _previewGeometry hot — wire drags are repeatable and the cost
        // of a stale geometry held by an invisible Path is trivial vs.
        // re-allocating per drag.
    }

    /// <summary>
    /// 0.11.x polish — drop a Flow.Reroute at the cursor mid-wire-drag.
    /// Routes the in-flight wire through the new reroute and re-arms the
    /// drag from the reroute's output so the user keeps dragging toward
    /// the next destination (Blueprints "click again to insert another
    /// knot" idiom). Wired from the Architect Keyboard handler on
    /// <c>VirtualKey.Y</c> while <c>_drag == DragState.WireDrop</c>.
    /// </summary>
    /// <returns>True when a reroute was inserted; false on no-op
    /// (no active drag, missing source, or registry refusal).</returns>
    private bool TryInsertRerouteUnderCursorDuringWireDrag()
    {
        if (_drag != DragState.WireDrop) return false;
        if (_wireFromSocket is null || _wireFromNode is null) return false;
        if (_vm is null) return false;

        // Snapshot the upstream source — after LoadGraph below rebuilds
        // every VM in the canvas, the existing SocketViewModel references
        // would dangle, so we lock onto the underlying Node / Socket Ids
        // first and re-resolve the VMs afterward.
        var fromNodeId   = _wireFromNode.Model.Id;
        var fromSocketId = _wireFromSocket.Model.Id;
        var fromSocketType = _wireFromSocket.Model.Type;
        var sourceDataType = _wireFromSocket.DataType;
        var sourceColor    = _wireFromSocket.Model.Color;

        // Reroute centred at the cursor in canvas space. The pin overlay
        // paints the diamond at the node origin; offset by half its
        // 20×20 footprint so the diamond's centre lands on the cursor.
        var canvasPos = HostToCanvas(_lastHostPoint);
        int spawnX = (int)System.Math.Round(canvasPos.X - 10);
        int spawnY = (int)System.Math.Round(canvasPos.Y - 10);
        var reroute = NodeRegistry.CreateNode(
            "Flow.Reroute", new System.Drawing.Point(spawnX, spawnY));
        if (reroute is null) return false;

        var graph = _vm.Graph;
        var rerouteIn  = reroute.Sockets.FirstOrDefault(s => s.Type == SocketType.Input);
        var rerouteOut = reroute.Sockets.FirstOrDefault(s => s.Type == SocketType.Output);
        if (rerouteIn is null || rerouteOut is null) return false;
        rerouteIn.DataType  = sourceDataType;
        rerouteIn.Color     = sourceColor;
        rerouteOut.DataType = sourceDataType;
        rerouteOut.Color    = sourceColor;

        PushUndo();
        graph.Nodes.Add(reroute);
        // The wire from the original source side into the reroute's
        // matching pin. fromSocketType tells us which way to point the
        // edge — an Output source flows out → reroute.in, while an
        // Input source (Ctrl+left-on-input rewire path) is reading from
        // the reroute's OUTPUT side so the link reverses.
        var newLink = fromSocketType == SocketType.Output
            ? new Link
            {
                FromNodeId = fromNodeId, FromSocketId = fromSocketId,
                ToNodeId   = reroute.Id, ToSocketId   = rerouteIn.Id,
            }
            : new Link
            {
                FromNodeId = reroute.Id, FromSocketId = rerouteOut.Id,
                ToNodeId   = fromNodeId, ToSocketId   = fromSocketId,
            };
        graph.Links.Add(newLink);

        // Incremental insert (one node + one link) instead of
        // LoadGraph rebuilding every VM + OnGraphMutated walking every socket
        // twice — the "big lag when placing reroute via hotkey" on large graphs.
        // Returns the freshly-built reroute VM; the drag continues from its
        // "free" side (out for an Output-originated drag, in for an
        // Input-originated rewire).
        var rerouteVm = _vm.ApplyIncrementalReroute(reroute, new[] { newLink });
        if (rerouteVm is null) return false;
        SocketViewModel? nextSource = null;
        var nextPool = fromSocketType == SocketType.Output ? rerouteVm.Outputs : rerouteVm.Inputs;
        foreach (var svm in nextPool)
        {
            if (svm.Model.Id == (fromSocketType == SocketType.Output ? rerouteOut.Id : rerouteIn.Id))
            { nextSource = svm; break; }
        }
        if (nextSource is null) return false;

        _wireFromNode   = rerouteVm;
        _wireFromSocket = nextSource;
        // The drag now sources from the reroute's
        // free pin (a different type/direction than the original source), so
        // re-evaluate the drop-target halos against the new source socket.
        _vm?.BeginDropHinting(nextSource);
        // Repaint the live wire preview from the reroute's free pin so
        // the user sees the drag continuing from the new knot, not still
        // sourcing from the original upstream node.
        UpdateWirePreview(
            rerouteVm, nextSource,
            HostToCanvas(_lastHostPoint),
            ResolveWirePreviewColor(nextSource, null));
        return true;
    }

    // Cached theme + palette brushes — pre-fix ResolveWirePreviewColor
    // ran Application.Current.Resources["Ember200Brush"] on every pointer-move
    // and fell through to `new SolidColorBrush(Microsoft.UI.Colors.Gold)` /
    // palette equivalents on every move where the theme key was missing. Both
    // sit in a 60-120 Hz hot loop. Mirror the s_selectionBrushCached pattern
    // on LinkViewModel: cache the theme brush on first hit, and pre-allocate
    // a palette-fallback brush for the no-resource case so the missing-key
    // path is also alloc-free.
    private static Brush? s_emberPreviewBrushCached;
    private static Brush? s_okBrushCached;
    private static Brush? s_errBrushCached;
    private static readonly SolidColorBrush s_emberPreviewFallback =
        new(ArchitectCanvasPalette.EmberDefaultPreview);
    private static readonly SolidColorBrush s_okFallback =
        new(ArchitectCanvasPalette.OkSageGreen);
    private static readonly SolidColorBrush s_errFallback =
        new(ArchitectCanvasPalette.ErrRustRed);

    /// <summary>
    /// Resolve the in-flight wire stroke based on the hovered target socket:
    ///   * compatible   — OkBrush (sage green)
    ///   * incompatible — ErrBrush (rust red)
    ///   * no socket under cursor — Ember (the default "drag-in-progress" colour)
    /// </summary>
    private Brush ResolveWirePreviewColor(SocketViewModel src, SocketViewModel? hover)
    {
        // Fallback colours come from the per-pillar palette, not raw
        // Microsoft.UI.Colors — the dictionary's Ember200/Ok/Err brushes are
        // a warm earthy palette and a missing-resource fallback to e.g.
        // Microsoft.UI.Colors.LightGreen would jar against the rest of the
        // canvas. The Resource() lookup wins when the dictionary has the
        // brush (the normal path); these fallbacks only fire on a designer
        // / pre-app start where Application.Current.Resources isn't loaded.
        Brush fallback = ResolveEmberPreviewBrush();
        if (hover is null) return fallback;
        if (ReferenceEquals(hover.Model, src.Model))      return fallback;
        if (hover.Direction == src.Direction)             return ResolveErrBrush();

        var (a, b) = src.Direction == SocketType.Output ? (src, hover) : (hover, src);
        return NodeRegistry.AreCompatible(a.DataType, b.DataType)
            ? ResolveOkBrush()
            : ResolveErrBrush();
    }

    private static Brush ResolveEmberPreviewBrush()
        => s_emberPreviewBrushCached
           ??= (Application.Current?.Resources["Ember200Brush"] as Brush)
               ?? s_emberPreviewFallback;

    private static Brush ResolveOkBrush()
        => s_okBrushCached
           ??= (Application.Current?.Resources["OkBrush"] as Brush)
               ?? s_okFallback;

    private static Brush ResolveErrBrush()
        => s_errBrushCached
           ??= (Application.Current?.Resources["ErrBrush"] as Brush)
               ?? s_errFallback;

    // ─── Misc helpers ────────────────────────────────────────────────────

    private NodeViewModel? FindNodeOwning(SocketViewModel s)
    {
        if (_vm is null) return null;
        foreach (var n in _vm.Nodes)
            if (n.FindSocket(s.Id) is not null) return n;
        return null;
    }

    /// <summary>
    /// Severs every wire that touches <paramref name="sock"/> in a single
    /// undo entry. Used by the Alt+click-on-pin path. Mirrors pre-T15
    /// OnMouseDown alt-disconnect behaviour.
    /// </summary>
    private void SeverAllWiresOnSocket(SocketViewModel sock)
    {
        if (_vm is null) return;
        var doomed = new System.Collections.Generic.List<LinkViewModel>();
        foreach (var lk in _vm.Links)
        {
            if (lk.Model.FromSocketId == sock.Id || lk.Model.ToSocketId == sock.Id)
                doomed.Add(lk);
        }
        if (doomed.Count == 0) return;
        PushUndo();
        foreach (var lk in doomed) RemoveLink(lk, pushUndo: false);
    }

    /// <summary>
    /// Ctrl+left on a socket already carrying a connection: detach the
    /// existing link and start a fresh wire-drop from the OTHER end's source-
    /// side socket so the user can re-aim it. Returns true when a rewire
    /// gesture was taken (caller stops further processing).
    /// </summary>
    private bool TryRewireExistingLink(SocketViewModel sock, Point hostPoint)
    {
        if (_vm is null) return false;
        LinkViewModel? hit = null;
        foreach (var lk in _vm.Links)
        {
            if (lk.Model.FromSocketId == sock.Id || lk.Model.ToSocketId == sock.Id) { hit = lk; break; }
        }
        if (hit is null) return false;

        // Walk through both endpoints to find the source side (output socket).
        SocketViewModel? sourceSide = null;
        NodeViewModel? sourceNode = null;
        foreach (var n in _vm.Nodes)
        {
            var fromS = n.FindSocket(hit.Model.FromSocketId);
            if (fromS is not null) { sourceSide = fromS; sourceNode = n; break; }
        }
        if (sourceSide is null || sourceNode is null) return false;

        PushUndo();
        RemoveLink(hit);

        _drag = DragState.WireDrop;
        SetTransientHotkeyContext(HotkeyContext.DraggingWire);
        _wireFromSocket = sourceSide;
        _wireFromNode   = sourceNode;
        _wireDragPressHost = hostPoint;
        StartWirePreview(sourceNode, sourceSide, HostToCanvas(hostPoint));
        // Ctrl-rewire re-aims an existing wire from
        // the opposite end — hint valid drop targets for that re-drag too.
        _vm?.BeginDropHinting(sourceSide);
        return true;
    }

    /// <summary>
    /// Walk the body's input sockets and return the first whose data-type is
    /// compatible with <paramref name="src"/>. Used by the drop-on-body
    /// fallback in <see cref="HandlePointerEnd"/>.
    /// </summary>
    // Direction-aware target-socket selector
    // for body-drops / post-spawn auto-wire. Wires connect Output → Input, so
    // the only valid target on a node body is the OPPOSITE direction to the
    // drag source: an Output source picks the first compatible Input, an Input
    // source picks the first compatible Output. Pre-fix this always scanned
    // Inputs, so dragging FROM an input and dropping on a body returned an
    // input — which TryCreateLink then rejected on the same-direction gate,
    // producing a confusing near-silent no-op. Selecting the opposite pool
    // means the pair this returns always survives TryCreateLink's gates
    // (type already checked here; self-loop excluded by the caller).
    private static SocketViewModel? FindFirstCompatibleInput(SocketViewModel src, NodeViewModel target)
    {
        var pool = src.Direction == SocketType.Output ? target.Inputs : target.Outputs;
        // Prefer a real, type-compatible socket.
        foreach (var s in pool)
        {
            if (s.IsPlaceholder) continue;
            if (NodeRegistry.AreCompatible(src.DataType, s.DataType))
                return s;
        }
        // No real socket matched — fall back to a managed dynamic
        // placeholder ("+ variable" / "+ input" / "+ output" / "+ return") so a body-drop
        // on a node whose only viable target is its add-slot (e.g. a fresh Macro.Entry /
        // Process.Entry / Event.Trigger carrying only Flow + the placeholder) GROWS the
        // bubble instead of no-op'ing. Managed placeholders are always DATA slots and
        // ADOPT the wire's type on activation (TryCreateLink's
        // owns the type check), so the only thing to exclude is a flow source — a flow
        // wire wants the node's real Flow input, already handled by the loop above.
        if (src.DataType != SocketDataType.Flow)
        {
            foreach (var s in pool)
                if (s.IsDynamicPlaceholder)
                {
                    BubbleDiag($"body-drop fallback → placeholder \"{s.Label}\" on \"{target.Title}\"");
                    return s;
                }
        }
        return null;
    }

    /// <summary>
    /// Drop on empty canvas — open the standard spawn palette anchored at the
    /// drop point. Picking a template spawns it at the click point and runs
    /// <see cref="FindFirstCompatibleInput"/> from the source socket so the
    /// new node auto-wires its first compatible input. (Pre-T15
    /// ShowFilteredSpawnMenu — the WinUI port reuses the existing spawn
    /// palette and adds the post-spawn auto-wire on the click handler.)
    /// </summary>
    private void ShowFilteredSpawnPaletteForWireDrop(SocketViewModel src, Point hostPoint)
    {
        _pendingWireDropSource = src;
        _pendingWireDropCanvas = HostToCanvas(hostPoint);
        // Hand the source type AND direction to the palette so the COMPATIBLE
        // section pre-filters templates to those whose first compatible socket
        // accepts this socket. Pre-fix the direction was omitted, so
        // a wire dropped from an Input socket walked the wrong pin pool (Inputs
        // instead of Outputs) and the post-spawn auto-wire failed silently
        // unless the user happened to pick a type-matching template.
        ShowSpawnPalette(hostPoint, compatibilityFilter: src.DataType, sourceDirection: src.Direction);
    }

    private SocketViewModel? _pendingWireDropSource;
    private Point _pendingWireDropCanvas;

    private Point ResolveLastCursorHostPoint(PointerRoutedEventArgs e)
    {
        try { return e.GetCurrentPoint(HostRoot).Position; }
        catch { return _lastHostPoint; }
    }

    // ─── Multi-frame group drag ────────────────────────

    private void BeginFrameGroupDragIfMulti(FrameViewModel pivot)
    {
        if (_vm is null) return;
        _frameGroupDragStarts.Clear();
        if (_vm.SelectedFrames.Count > 1 && _vm.SelectedFrames.Contains(pivot))
        {
            foreach (var f in _vm.SelectedFrames)
                _frameGroupDragStarts.Add((f, f.X, f.Y));
        }
    }

    private bool TryUpdateFrameGroupDrag(double dx, double dy)
    {
        if (_vm is null || _frameGroupDragStarts.Count == 0) return false;
        foreach (var entry in _frameGroupDragStarts)
        {
            double targetX = entry.X + dx;
            double targetY = entry.Y + dy;
            entry.Frame.Translate(targetX - entry.Frame.X, targetY - entry.Frame.Y);
        }
        return true;
    }

    private void EndFrameGroupDrag() => _frameGroupDragStarts.Clear();

    // ─── Frame parent-child drag linkage ──────────────
    //
    // When a FrameMove gesture begins, snapshot every node whose canvas-space
    // bounds intersect the drag frame's bounds so the frame carries those
    // nodes with it (UE-Blueprints "comment box" idiom). Cross-frame
    // containment uses smallest-area-frame-wins: a node sitting inside two
    // nested frames belongs to the inner one — only THAT frame carries it,
    // even if the outer frame is the one being dragged. This matches
    // Blueprints' visual-hierarchy intuition (the tightest enclosing
    // annotation is "the parent").
    //
    // Multi-frame group drag: when several frames are selected and dragged
    // together, the carry set is computed across the WHOLE drag group at
    // drag-start — a node whose innermost frame is ANY of the dragged frames
    // gets captured. All captured nodes translate by the single shared
    // (dx, dy), so the relative geometry inside each frame is preserved.
    //
    // Undo: the existing `PushUndo()` at first-motion (the frame path's
    // prior discipline) snapshots the pre-drag graph state which
    // covers BOTH the frame's pre-drag bounds AND every contained node's
    // pre-drag location. A single Ctrl+Z rolls back the whole gesture as one
    // undo entry.

    /// <summary>
    /// Snapshot the contained-node set for a FrameMove gesture. Nodes whose
    /// canvas-space bounds intersect the pivot frame's bounds are captured
    /// with their pre-drag (X, Y) so <see cref="UpdateFrameContentDrag"/> can
    /// translate them in lockstep with the frame.
    ///
    /// Cross-frame containment: smallest-area-frame-wins. Walks every frame
    /// in the graph and picks the smallest-area frame containing each node's
    /// rect; only captures the node if that innermost frame is either the
    /// pivot OR a member of the in-flight multi-frame drag group.
    /// </summary>
    // ────────────────────────────────────────────────────────────────────
    // Frame nesting visual
    // semantics: INTENTIONAL FLAT-FRAME BEHAVIOUR, NOT A REGRESSION.
    // ────────────────────────────────────────────────────────────────────
    //
    // What happens today: dragging an outer frame moves its CONTAINED
    // NODES (identified by the innermost-frame lookup below) but does NOT
    // move inner frames whose rects sit visually inside the outer frame.
    // The inner frame's own rect stays put.
    //
    // Why this is intentional:
    //   • The graph model (`GraphFrame` in
    //     Phoenix.Controls.Shared/Models/GraphModels.cs) has NO parent
    //     pointer. Frames are flat visual groupings — a list, not a tree.
    //     The exporter (`ScriptExporter`) and serializer
    //     (`GraphSerializer`) both treat frames as overlays with no
    //     containment relationship between them.
    //   • Re-parenting inner frames on outer-drag would IMPLY a hierarchy
    //     the model does not store. The next save/load round-trip would
    //     drop the implied parent because there's no field to persist it
    //     into, breaking user expectation between sessions.
    //   • Pre-T15 (the WinForms `FrameManager` era) shipped the exact
    //     same flat-drag semantics — confirmed by audit Unit 96. This
    //     is NOT a WinUI migration regression; it is the long-standing
    //     contract.
    //
    // If a future sprint wants nested-frame drag semantics:
    //   1. Add a nullable `Guid? ParentFrameId` field to `GraphFrame`
    //      (Phoenix.Controls.Shared/Models/GraphModels.cs).
    //   2. Re-parent on outer-frame drop: walk every other frame whose
    //      pre-drag rect was fully contained by the pivot's pre-drag
    //      rect and set their `ParentFrameId` to the pivot.
    //   3. Extend `BeginFrameContentDrag` below to enumerate child
    //      frames (via `ParentFrameId == pivot.Id`) and translate their
    //      rects in lockstep alongside the captured nodes.
    //   4. Update `GraphSerializer.Save` / `Load` to round-trip the new
    //      field, and `MigrateNodes` to default it to null on legacy
    //      `.phxg` loads.
    //
    // Until that sprint lands, the contained-nodes-follow / inner-frames-
    // stay-put behaviour is the documented, by-design outcome. Documented
    // here so a future cleanup pass doesn't file this as a bug.
    private void BeginFrameContentDrag(FrameViewModel pivot)
    {
        if (_vm is null) return;
        _frameContentDragStarts.Clear();

        // The set of frames currently being dragged. For a multi-frame drag,
        // `_frameGroupDragStarts` is already populated by
        // `BeginFrameGroupDragIfMulti` — reuse it. For a single-frame drag
        // the pivot is the only member.
        var draggedFrames = new System.Collections.Generic.HashSet<FrameViewModel>();
        if (_frameGroupDragStarts.Count > 0)
        {
            foreach (var entry in _frameGroupDragStarts) draggedFrames.Add(entry.Frame);
        }
        else
        {
            draggedFrames.Add(pivot);
        }

        // Snapshot of all frame bounds for the innermost-frame-wins lookup.
        // Reading these once avoids re-querying through the VM N×M times in
        // the inner loop.
        var allFrames = _vm.Frames;

        foreach (var node in _vm.Nodes)
        {
            double nx = node.X, ny = node.Y;
            double nw = node.Width  <= 0 ? 1 : node.Width;
            double nh = node.Height <= 0 ? 1 : node.Height;
            double nr = nx + nw, nb = ny + nh;

            // Find the smallest-area frame whose bounds intersect this node.
            // Ties broken by first-encountered (stable enumeration order from
            // _vm.Frames) — matters only when two frames have identical area,
            // which is a vanishingly rare authoring case.
            FrameViewModel? innermost = null;
            long innermostArea = long.MaxValue;
            foreach (var f in allFrames)
            {
                double fx = f.X, fy = f.Y, fw = f.Width, fh = f.Height;
                bool overlap = nx < fx + fw && nr > fx
                            && ny < fy + fh && nb > fy;
                if (!overlap) continue;
                long area = (long)fw * (long)fh;
                if (area < innermostArea)
                {
                    innermostArea = area;
                    innermost = f;
                }
            }

            // Only capture when the node's innermost frame is one of the
            // dragged frames — that's the parent-child link.
            if (innermost is not null && draggedFrames.Contains(innermost))
            {
                _frameContentDragStarts[node] = new Point(nx, ny);
            }
        }
    }

    /// <summary>
    /// Translate every captured contained node by the same (dx, dy) the frame
    /// drag has accumulated. Uses the canonical <see cref="LogicCanvasViewModel.TranslateNode"/>
    /// so wire endpoints (the bezier path on every link touching a moved node)
    /// recompute through the same coalesced rendering tick the node-drag path
    /// uses — contents follow visually with their wires intact.
    /// </summary>
    private void UpdateFrameContentDrag(double dx, double dy)
    {
        if (_vm is null || _frameContentDragStarts.Count == 0) return;
        foreach (var entry in _frameContentDragStarts)
        {
            double targetX = entry.Value.X + dx;
            double targetY = entry.Value.Y + dy;
            _vm.TranslateNode(entry.Key, targetX - entry.Key.X, targetY - entry.Key.Y);
        }
    }

    /// <summary>
    /// Stash the latest cumulative (dx, dy) for the
    /// in-flight FrameMove and flip the dirty flag so the next
    /// CompositionTarget.Rendering tick drains it via
    /// <see cref="FlushFrameContentDragIfDirty"/>. Caller-side (the
    /// FrameMove branch + edge-pan ripple) used to call
    /// <see cref="UpdateFrameContentDrag"/> directly on every PointerMoved /
    /// edge-pan tick — for a frame with N contained nodes that's N
    /// TranslateNode calls per pointer event at 120 Hz on touchpads. Now
    /// the work runs at most once per displayed frame (~60 Hz) regardless
    /// of pointer rate.
    /// </summary>
    internal void RequestFrameContentDragUpdate(double dx, double dy)
    {
        _frameContentDragLastDx = dx;
        _frameContentDragLastDy = dy;
        _frameContentDragDirty  = true;
    }

    /// <summary>
    /// Drain the frame-content drag dirty flag from
    /// <see cref="OnRenderingTick"/>. Cheap when nothing is dirty (one bool
    /// check). On the drain path, calls <see cref="UpdateFrameContentDrag"/>
    /// with the latest stashed delta so the contained-node positions catch
    /// up with the frame in a single displayed-frame cycle.
    /// </summary>
    internal void FlushFrameContentDragIfDirty()
    {
        if (!_frameContentDragDirty) return;
        _frameContentDragDirty = false;
        UpdateFrameContentDrag(_frameContentDragLastDx, _frameContentDragLastDy);
    }

    private void EndFrameContentDrag()
    {
        _frameContentDragStarts.Clear();
        _frameContentDragDirty = false;
        _frameContentDragLastDx = 0;
        _frameContentDragLastDy = 0;
    }

    // ─── Deferred undo + Esc-cancel for node drag ──────

    /// <summary>
    /// Push undo for a node drag that just completed. The undo controller
    /// snapshots whatever the graph CURRENTLY is, so we briefly revert the
    /// dragged nodes to their captured pre-drag positions, snapshot, then
    /// re-apply the drag end-state. The visual flicker is bounded by one
    /// synchronous frame so the user never sees the revert; the layout pass
    /// applies the final positions before the next render tick.
    /// </summary>
    private void CommitNodeDragUndo(NodeViewModel pivot)
    {
        if (_vm is null) return;

        // Capture end-state for every dragged node so we can re-apply.
        // Multi-node drag: _groupDragStarts has every node we touched. Single
        // node drag: just the pivot. Either way we collect (Node, endX, endY)
        // for the re-apply step.
        var end = new System.Collections.Generic.List<(NodeViewModel Node, double X, double Y)>();
        bool isGroup = _groupDragStarts.Count > 0;
        if (isGroup)
        {
            foreach (var entry in _groupDragStarts)
                end.Add((entry.Node, entry.Node.X, entry.Node.Y));
        }
        else
        {
            end.Add((pivot, pivot.X, pivot.Y));
        }

        // Revert each node to its captured pre-drag start.
        if (isGroup)
        {
            foreach (var entry in _groupDragStarts)
                _vm.TranslateNode(entry.Node, entry.X - entry.Node.X, entry.Y - entry.Node.Y);
        }
        else
        {
            _vm.TranslateNode(pivot, _nodeStartX - pivot.X, _nodeStartY - pivot.Y);
        }

        // Snapshot the pre-drag graph state.
        PushUndo();

        // Re-apply the end-state so the user sees the drag as committed.
        foreach (var endEntry in end)
        {
            _vm.TranslateNode(endEntry.Node, endEntry.X - endEntry.Node.X, endEntry.Y - endEntry.Node.Y);
        }
    }

    /// <summary>
    /// Esc-cancel mid-drag: revert every dragged node to its captured
    /// pre-drag start. Does NOT push undo — the gesture is being cancelled
    /// so the undo stack stays untouched.
    /// </summary>
    private void RevertNodeDragToStart()
    {
        if (_vm is null || _dragNode is null) return;
        if (_groupDragStarts.Count > 0)
        {
            foreach (var entry in _groupDragStarts)
                _vm.TranslateNode(entry.Node, entry.X - entry.Node.X, entry.Y - entry.Node.Y);
        }
        else
        {
            _vm.TranslateNode(_dragNode, _nodeStartX - _dragNode.X, _nodeStartY - _dragNode.Y);
        }
    }

    // ─── ARCH-P1-UNDO-FRAME-DEFER — deferred undo + Esc-cancel for frame drag ──
    //
    // Parity with the node-drag path (CommitNodeDragUndo / RevertNodeDragToStart
    // above). The frame path used to PushUndo at first motion; now the snapshot
    // is deferred to release and Esc reverts cleanly without touching the stack.

    /// <summary>
    /// Restore the in-flight frame gesture to its captured pre-drag pose. For a
    /// resize that's the pivot frame's pre-drag bounds; for a move it's the
    /// frame(s) + every carried contained node back to their start positions.
    /// Used by both the deferred-undo commit and the Esc-cancel revert.
    /// </summary>
    private void RevertFrameDragGeometry(bool wasResize)
    {
        if (_vm is null || _dragFrame is null) return;
        if (wasResize)
        {
            _dragFrame.SetBounds(_frameStartX, _frameStartY, _frameStartW, _frameStartH);
            return;
        }
        if (_frameGroupDragStarts.Count > 0)
        {
            foreach (var e in _frameGroupDragStarts)
                e.Frame.Translate(e.X - e.Frame.X, e.Y - e.Frame.Y);
        }
        else
        {
            _dragFrame.Translate(_frameStartX - _dragFrame.X, _frameStartY - _dragFrame.Y);
        }
        foreach (var kv in _frameContentDragStarts)
            _vm.TranslateNode(kv.Key, kv.Value.X - kv.Key.X, kv.Value.Y - kv.Key.Y);
    }

    /// <summary>
    /// Esc-cancel mid-frame-drag: revert to the pre-drag pose. Does NOT push
    /// undo — the gesture is being cancelled so the stack stays untouched.
    /// </summary>
    private void RevertFrameDragToStart(bool wasResize) => RevertFrameDragGeometry(wasResize);

    /// <summary>
    /// Push undo for a completed frame drag. Mirrors CommitNodeDragUndo: capture
    /// the end-state, briefly revert to the pre-drag pose, snapshot, then
    /// re-apply the end-state — so Ctrl+Z lands on the pre-drag bounds and an
    /// Esc-cancel earlier never touched the stack. The visual revert is bounded
    /// by one synchronous frame so the user never sees it.
    /// </summary>
    private void CommitFrameDragUndo(bool wasResize)
    {
        if (_vm is null || _dragFrame is null) return;

        // Capture end-state before reverting.
        double endX = _dragFrame.X, endY = _dragFrame.Y, endW = _dragFrame.Width, endH = _dragFrame.Height;
        System.Collections.Generic.List<(FrameViewModel F, double X, double Y)>? frameEnds = null;
        System.Collections.Generic.List<(NodeViewModel N, double X, double Y)>? nodeEnds = null;
        if (!wasResize)
        {
            if (_frameGroupDragStarts.Count > 0)
            {
                frameEnds = new();
                foreach (var e in _frameGroupDragStarts) frameEnds.Add((e.Frame, e.Frame.X, e.Frame.Y));
            }
            if (_frameContentDragStarts.Count > 0)
            {
                nodeEnds = new();
                foreach (var kv in _frameContentDragStarts) nodeEnds.Add((kv.Key, kv.Key.X, kv.Key.Y));
            }
        }

        // Revert to pre-drag, snapshot, re-apply end-state.
        RevertFrameDragGeometry(wasResize);
        PushUndo();

        if (wasResize)
        {
            _dragFrame.SetBounds(endX, endY, endW, endH);
        }
        else
        {
            if (frameEnds is not null)
                foreach (var fe in frameEnds) fe.F.Translate(fe.X - fe.F.X, fe.Y - fe.F.Y);
            else
                _dragFrame.Translate(endX - _dragFrame.X, endY - _dragFrame.Y);
            if (nodeEnds is not null)
                foreach (var ne in nodeEnds) _vm.TranslateNode(ne.N, ne.X - ne.N.X, ne.Y - ne.N.Y);
        }
    }

    // ─── Snap guides + collision overlay ──────────────
    //
    // Drawn into OverlayLayer (host-space Canvas above the panned ViewSurface)
    // so the lines / collision tints sit above every node + wire and don't
    // get clipped by the ViewSurface CompositeTransform. We convert each
    // canvas-space coordinate to host-space (cx * Zoom + PanX, cy * Zoom + PanY)
    // at paint time so the guides track pan / zoom changes inside the drag.
    //
    // Strokes use SelectionBrush so the gold-ember selection palette flows
    // through (Design_Orders.md §2). Collision tint uses ErrBrush — same
    // brush the wire-preview uses for incompatible drops so the canvas
    // chrome stays consistent. Paint code lives in Architect (NOT Shared).

    private const double SnapEdgeThresholdPx = 4.0;

    private readonly System.Collections.Generic.List<Microsoft.UI.Xaml.Shapes.Shape> _snapGuideShapes = new();

    // 2026-05-22 — snap-guide rebuild coalesced to the next
    // CompositionTarget.Rendering tick (drag glitched on high-Hz pointers
    // because the per-PointerMoved rebuild allocated Shapes + Brushes and
    // mutated OverlayLayer.Children at ~120 Hz). PointerMoved sets the dirty
    // flag; OnRenderingTick reads-and-clears it and calls UpdateSnapGuidesForDrag
    // at most once per displayed frame. Mirrors the pan / wheel coalescer pattern.
    private bool _snapGuidesDirty;
    private void RequestSnapGuidesUpdate() => _snapGuidesDirty = true;

    // 0.11.x polish (wire-drag lag) — wire-drop hit-test coalescer. The
    // PointerMoved branch above flips the dirty bit instead of hit-testing
    // inline; OnRenderingTick reads-and-clears it and resolves the hover
    // socket + preview brush at most once per displayed frame. Cheap when
    // no drag is active (one bool check). Same idiom as _snapGuidesDirty.
    private bool _wireDropHitTestDirty;
    private void RequestWireDropHitTestUpdate() => _wireDropHitTestDirty = true;

    /// <summary>
    /// 0.11.x polish — drain the coalesced wire-drop hit-test. Runs the
    /// FindElementsInHostCoordinates walk against the cursor's last host
    /// point and updates the preview wire's stroke brush. Idempotent and
    /// cheap when no wire drag is in flight.
    /// </summary>
    internal void DrainWireDropHitTest()
    {
        if (!_wireDropHitTestDirty) return;
        _wireDropHitTestDirty = false;
        if (_drag != DragState.WireDrop) return;
        if (_wireFromSocket is null) return;

        SocketViewModel? hover = null;
        try
        {
            var elements = Microsoft.UI.Xaml.Media.VisualTreeHelper
                .FindElementsInHostCoordinates(_lastHostPoint, this);
            foreach (var el in elements)
            {
                if (HitTagFrom(el) is SocketViewModel sock) { hover = sock; break; }
            }
        }
        catch { /* hit-test best-effort */ }

        // Model-side fallback: a culled (off-screen) hover target won't be
        // in the visual tree; resolve it model-side so the compatibility preview
        // (wire colour) still reflects the unmounted socket under the cursor.
        if (hover is null && _enableNodeVirtualization)
            hover = ResolveSocketAtCanvasPoint(HostToCanvas(_lastHostPoint), WireDropModelHitRadius);

        WirePreview.Stroke = ResolveWirePreviewColor(_wireFromSocket, hover);
    }

    /// <summary>
    /// Recompute the snap-guide line set against the current drag target. For
    /// each visible non-selected node whose left / right / centerX matches
    /// the dragged node's left / right / centerX (within SnapEdgeThresholdPx
    /// in canvas-space), paint a vertical line spanning the host viewport;
    /// horizontal guides mirror the same logic for top / bottom / centerY.
    /// Collision targets (dragged node fully overlaps a sibling's body bbox)
    /// get a translucent red fill rectangle drawn at the sibling's bounds.
    /// </summary>
    private void UpdateSnapGuidesForDrag()
    {
        if (_vm is null || _dragNode is null)
        {
            ClearSnapGuideOverlay();
            return;
        }

        // Build the set of selected nodes (excluded from the alignment test
        // so a multi-node drag doesn't draw guides against its own members).
        var dragSet = new System.Collections.Generic.HashSet<NodeViewModel>();
        if (_groupDragStarts.Count > 0)
        {
            foreach (var entry in _groupDragStarts) dragSet.Add(entry.Node);
        }
        else
        {
            dragSet.Add(_dragNode);
        }

        ClearSnapGuideOverlay();

        // Resolve theme brushes lazily; cache pattern mirrors LinkViewModel /
        // the wire-preview brush cache above.
        var selectionBrush = (Application.Current?.Resources["SelectionBrush"] as Brush)
            ?? new SolidColorBrush(ArchitectCanvasPalette.SelectionGold);
        var errBrush = ResolveErrBrush();

        // Dragged node bounds (canvas-space). Fall back to 0×0 when Width /
        // Height haven't been measured yet (the node was just spawned and
        // the layout pass hasn't run), so the guides at least snap to the
        // origin instead of NaN-propagating through the comparison.
        double dragX = _dragNode.X;
        double dragY = _dragNode.Y;
        double dragW = _dragNode.Width  <= 0 ? 1 : _dragNode.Width;
        double dragH = _dragNode.Height <= 0 ? 1 : _dragNode.Height;
        double dragRight = dragX + dragW;
        double dragBottom = dragY + dragH;
        double dragCX = dragX + dragW / 2;
        double dragCY = dragY + dragH / 2;

        double zoom = System.Math.Max(_vm.Zoom, 0.0001);

        foreach (var n in _vm.Nodes)
        {
            if (dragSet.Contains(n)) continue;
            double nx = n.X, ny = n.Y;
            double nw = n.Width  <= 0 ? 1 : n.Width;
            double nh = n.Height <= 0 ? 1 : n.Height;
            double nr = nx + nw;
            double nb = ny + nh;
            double ncx = nx + nw / 2;
            double ncy = ny + nh / 2;

            // Collision: the dragged bbox fully overlaps this node's bbox
            // (centre-overlaps-centre threshold). Paint a translucent red
            // rectangle over the sibling so the user can see "if you let go
            // here, you'll stack onto this node".
            bool overlap = dragX < nr && dragRight > nx
                        && dragY < nb && dragBottom > ny;
            if (overlap)
            {
                double hx = nx * zoom + _vm.PanX;
                double hy = ny * zoom + _vm.PanY;
                double hw = nw * zoom;
                double hh = nh * zoom;
                var collisionRect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width  = hw,
                    Height = hh,
                    Fill   = errBrush,
                    Opacity = 0.25,
                    IsHitTestVisible = false,
                };
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(collisionRect, hx);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop (collisionRect, hy);
                OverlayLayer.Children.Add(collisionRect);
                _snapGuideShapes.Add(collisionRect);
            }

            // Vertical alignment lines (left / centerX / right of dragged
            // matches left / centerX / right of sibling).
            TryAddVerticalGuide(dragX,    nx,  selectionBrush);
            TryAddVerticalGuide(dragX,    ncx, selectionBrush);
            TryAddVerticalGuide(dragX,    nr,  selectionBrush);
            TryAddVerticalGuide(dragCX,   nx,  selectionBrush);
            TryAddVerticalGuide(dragCX,   ncx, selectionBrush);
            TryAddVerticalGuide(dragCX,   nr,  selectionBrush);
            TryAddVerticalGuide(dragRight, nx, selectionBrush);
            TryAddVerticalGuide(dragRight, ncx,selectionBrush);
            TryAddVerticalGuide(dragRight, nr, selectionBrush);

            // Horizontal alignment lines (top / centerY / bottom).
            TryAddHorizontalGuide(dragY,      ny,  selectionBrush);
            TryAddHorizontalGuide(dragY,      ncy, selectionBrush);
            TryAddHorizontalGuide(dragY,      nb,  selectionBrush);
            TryAddHorizontalGuide(dragCY,     ny,  selectionBrush);
            TryAddHorizontalGuide(dragCY,     ncy, selectionBrush);
            TryAddHorizontalGuide(dragCY,     nb,  selectionBrush);
            TryAddHorizontalGuide(dragBottom, ny,  selectionBrush);
            TryAddHorizontalGuide(dragBottom, ncy, selectionBrush);
            TryAddHorizontalGuide(dragBottom, nb,  selectionBrush);
        }
    }

    /// <summary>
    /// Add a vertical guide line spanning the host viewport when
    /// |dragCanvasX - siblingCanvasX| is within the snap threshold. The line
    /// is drawn in host-space at the siblingCanvasX projected through the
    /// current view transform so the guide visually anchors on the sibling's
    /// edge (the line the user is snapping TO).
    /// </summary>
    private void TryAddVerticalGuide(double dragCanvasX, double siblingCanvasX, Brush stroke)
    {
        if (_vm is null) return;
        if (System.Math.Abs(dragCanvasX - siblingCanvasX) > SnapEdgeThresholdPx) return;
        double hostX = siblingCanvasX * _vm.Zoom + _vm.PanX;
        double h = HostRoot.ActualHeight;
        if (h <= 0) return;
        var line = new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = hostX, Y1 = 0,
            X2 = hostX, Y2 = h,
            Stroke = stroke,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            IsHitTestVisible = false,
            Opacity = 0.85,
        };
        OverlayLayer.Children.Add(line);
        _snapGuideShapes.Add(line);
    }

    private void TryAddHorizontalGuide(double dragCanvasY, double siblingCanvasY, Brush stroke)
    {
        if (_vm is null) return;
        if (System.Math.Abs(dragCanvasY - siblingCanvasY) > SnapEdgeThresholdPx) return;
        double hostY = siblingCanvasY * _vm.Zoom + _vm.PanY;
        double w = HostRoot.ActualWidth;
        if (w <= 0) return;
        var line = new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = 0, Y1 = hostY,
            X2 = w, Y2 = hostY,
            Stroke = stroke,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            IsHitTestVisible = false,
            Opacity = 0.85,
        };
        OverlayLayer.Children.Add(line);
        _snapGuideShapes.Add(line);
    }

    private void ClearSnapGuideOverlay()
    {
        // 2026-05-22 — clear the coalesced-rebuild dirty flag too so a tear-down
        // mid-drag (Esc-cancel, PointerCanceled, drop on socket) doesn't leave
        // a pending rebuild that would repaint guides after the gesture ended.
        _snapGuidesDirty = false;
        if (_snapGuideShapes.Count == 0) return;
        foreach (var s in _snapGuideShapes)
        {
            try { OverlayLayer.Children.Remove(s); }
            catch { /* layer torn down — best effort */ }
        }
        _snapGuideShapes.Clear();
    }
}
