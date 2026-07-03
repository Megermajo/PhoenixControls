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
using Phoenix.Controls.Architect.WinUI.Controls;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Core;
using Windows.Foundation;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// 0.11.5 — pin overlay. The pin chrome that used to live
// inside InputRowTemplate / OutputRowTemplate (NodeView.xaml) is now built
// in code and parented to NodeView.PinOverlay, which is a SIBLING of
// NodeRoot in NodeView's outer Grid. The sibling parenting is the load-
// bearing change — the NodeRoot Border applies a rounded-rect clip to its
// content, so any element inside NodeRoot at x < 0 / x > width would be
// truncated. Moving the pin out of that clip lets us render the 14×14 pin
// straddling the body's outer edge (centre on x=0 / x=width, half outside,
// half inside) — Majo's "bubbles on the border of the node (50/50 in/out)"
// feedback from the 0.11.5 audit.
//
// Layout per pin (matches the pre-r3 InputRowTemplate / OutputRowTemplate
// chrome, just rendered into an overlay Canvas instead of a row column):
//   • RequiredHalo Ellipse  — yellow ring on unwired required INPUT pins
//   • DropHalo     Ellipse  — green Valid / red Invalid during a wire-drag
//   • Pin          Path     — the typed pin glyph (chevron / circle /
//                              triangle / diamond) via PinPathGeometry
//   • Wildcard     Ellipse  — dashed Any-type outline overlay
//   • Arity        TextBlock — "2"/"3"/"4" for Vector2/3/4
//   • HitTarget    Ellipse  — 24×24 transparent, Tag=SocketViewModel
//
// Tag wiring keeps LogicCanvasView.Pointer's HitTagFrom + the existing
// wire-drop / drop-state-hinting paths walking unchanged — they look up
// SocketViewModel via the Tag, regardless of where in the visual tree the
// tagged element lives.
public sealed partial class NodeView
{
    // PinElements: per-socket bundle of element references we need to mutate
    // imperatively when the SocketViewModel raises PropertyChanged (no
    // bindings inside PinOverlay — we drive the visuals from the code-behind
    // so we don't pay the binding cost on every drag-frame).
    //
    // 0.11.7 — the Pin field used to be a Path+Viewbox composite; it's now a
    // single Shape (Ellipse / Polygon / Rectangle) so the per-socket update
    // path can swap Stroke/Fill without poking through a Viewbox child.
    //
    // Three new layers
    // sit alongside the existing pin chrome:
    //   • SelectionRing — gold focus halo when this socket is the selected one.
    //   • AnimatedBadge — small yellow dot when the destination var has a
    //     keyframe in the Visualist timeline registry (currently a stub; see
    //     IsPinAnimatedExternally below).
    // The Pin shape itself gains a connected-vs-unconnected differentiation
    // via Fill opacity so unconnected pins read as outlines and
    // connected pins read as filled glyphs.
    private sealed class PinElements
    {
        public Grid       Container = null!;
        public Ellipse?   RequiredHalo;
        public Ellipse    DropHalo  = null!;
        public Microsoft.UI.Xaml.Shapes.Shape Pin = null!;
        public Ellipse    Wildcard  = null!;
        public TextBlock  Arity     = null!;
        public Ellipse?   SelectionRing;
        public Ellipse?   AnimatedBadge;
        public Ellipse    HitTarget = null!;
        public int        RowIndex;
        public SocketType Direction;
        public SocketPinKind PinKind;
        // Last-applied dynamic-placeholder state, so the same-kind
        // property-change branch only re-allocates a DoubleCollection
        // (StrokeDashArray) when the dashed-vs-solid state actually flipped,
        // not on every ColorHex / Fill bump.
        public bool       IsDashed;
    }

    private readonly Dictionary<SocketViewModel, PinElements> _pinElements = new();
    private NodeViewModel? _pinBoundVm;
    private bool _nodeRootSizeHooked;

    // Same brushes the InputRowTemplate / OutputRowTemplate used as
    // StaticResource lookups. Resolved lazily so designer-time / pre-app
    // construction doesn't crash on the resource lookup.
    private static SolidColorBrush? s_pinRequiredHaloBrush;
    private static SolidColorBrush PinRequiredHaloBrush => s_pinRequiredHaloBrush ??= ResolveBrush(
        "PinRequiredHaloBrush", 0xFF, 0xE0, 0xA2, 0x3A);

    // Drop-state brushes. The Valid (compatible) halo restores the
    // baseline's semi-transparent green glow (#C878DC78 — alpha 0xC8/200,
    // RGB 0x78,0xDC,0x78) so it reads as a reachability AFFORDANCE rather than
    // a fully-opaque selection marker. The Invalid halo stays the saturated
    // red so it reads as a hard "no". Paired with the 20×20 DropHalo size +
    // 2.0 stroke below.
    private static readonly SolidColorBrush s_dropValidBrush   = new(Color.FromArgb(0xC8, 0x78, 0xDC, 0x78));
    private static readonly SolidColorBrush s_dropInvalidBrush = new(Color.FromArgb(0xFF, 0xCB, 0x4D, 0x3F));

    // Shared transparent brush + a hex→brush cache so
    // the pin-rebuild path (BuildPinShape calls HexBrush 2x per pin, 6+ per
    // socket per rebuild) and DropStateBrushFor's None arm stop allocating a
    // fresh SolidColorBrush every call. UI-thread only, so a plain Dictionary is
    // safe. Brushes are read-only after creation (set as Stroke/Fill, never
    // mutated), so sharing one instance per hex across all pins is correct.
    private static readonly SolidColorBrush s_transparentBrush = new(Colors.Transparent);
    private static readonly System.Collections.Generic.Dictionary<string, SolidColorBrush> s_hexBrushCache =
        new(System.StringComparer.Ordinal);

    // Selection-ring brush. Reuses
    // the canvas's selection gold (EmberPrimaryBrush) so the per-socket halo
    // matches the per-node selection halo painted by NodeView.xaml.cs.
    private static SolidColorBrush? s_pinSelectionBrush;
    private static SolidColorBrush PinSelectionBrush => s_pinSelectionBrush ??= ResolveBrush(
        "EmberPrimaryBrush", 0xFF, 0xE5, 0xA2, 0x4E);

    // Animated-pin badge brush.
    // Yellow dot mirrors the Visualist marker for an animated pin
    // (AnimatedPinRegistry indicates a destination variable has keyframes).
    // The Architect-side surface is conditional on a registry lookup that
    // currently falls back to false (see IsPinAnimatedExternally below) —
    // when the cross-pillar plumbing lands, the brush is already wired.
    private static readonly SolidColorBrush s_animatedBadgeBrush =
        new(Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E));

    // Single-source-of-truth selected-socket id. Shared statically
    // across every NodeView in the same process so sibling nodes can clear
    // their selection ring when this one's pin is clicked. The field stores
    // the Socket.Id so cross-NodeView lookup is by id (cheap string compare),
    // and the static event lets each NodeView re-evaluate without scanning
    // the canvas's full Nodes collection.
    //
    // Rationale for the static + event approach: SocketViewModel cannot carry
    // the IsSelected flag in this commit (parallel-agent ownership), so the
    // selection state lives on the view side. The static field is the
    // single source of truth; each NodeView listens to s_selectionChanged
    // and updates only its own subset of PinElements. Clearing is implicit:
    // any change to s_selectedSocketId causes every listener to re-paint,
    // and only the matching id renders the gold ring.
    private static string? s_selectedSocketId;
    private static event EventHandler? s_selectionChanged;

    /// <summary>
    /// Set the canvas-wide selected socket. Pass <c>null</c> to clear.
    /// Idempotent on no-op assignment so a pointer-press on the already-
    /// selected pin doesn't trigger a useless re-paint pass across every
    /// visible node.
    /// </summary>
    internal static void SetSelectedSocket(string? id)
    {
        if (string.Equals(s_selectedSocketId, id, StringComparison.Ordinal)) return;
        s_selectedSocketId = string.IsNullOrEmpty(id) ? null : id;
        SafeEvent.Raise(s_selectionChanged, null, EventArgs.Empty, "NodeView", "SelectionChanged");
    }

    /// <summary>
    /// 0.11.x polish — public accessor so the canvas-side pointer / keyboard
    /// handlers can clear the pin selection ring when the user clicks an
    /// empty area or presses Esc. Without this the gold ring around the
    /// last-clicked pin sticks around forever (Majo: "very small square,
    /// barely noticeable, but it never disappears nor is it necessary").
    /// </summary>
    internal static void ClearPinSelection() => SetSelectedSocket(null);

    /// <summary>
    /// Query the cross-pillar
    /// animated-var tracker for the variable name bound to this socket.
    /// Returns true when at least one of the candidate names this socket
    /// resolves to has an active keyframe registration on the Visualist side.
    ///
    /// Variable-name resolution order:
    ///   1. <c>VariableName</c> attribute on the parent node (Var.Set /
    ///      Var.Get carry this — see <c>VarSetHandler.Emit</c>).
    ///   2. <c>KeyName</c> attribute (Public.Set / Public.Get parity).
    ///   3. <c>Name</c> attribute (generic fallback — State.Set / State.Get etc.).
    ///   4. The socket's own name (Vector2/3/4 components like "TranslateX"
    ///      ride on Architect's socket name = Visualist's component name
    ///      convention so the cross-pillar match works without an explicit
    ///      attribute).
    ///
    /// The candidate list is checked first-hit-wins to keep the per-paint
    /// cost negligible — the tracker's IsAnimated is a single
    /// ConcurrentDictionary lookup per candidate.
    /// </summary>
    private static bool IsPinAnimatedExternally(SocketViewModel sock)
    {
        if (sock is null) return false;
        try
        {
            // Walk the candidate names in priority order. Each helper accepts
            // null/empty and returns false on miss so a node with none of the
            // canonical attributes still falls through to the socket-name
            // fallback (case: an Image.Scale on Architect's Var.Set whose
            // attribute slot is blank).
            var node = sock.ParentNode;
            if (node is { Attributes: { } attrs })
            {
                if (attrs.TryGetValue("VariableName", out var vn)
                    && Phoenix.Controls.Shared.Core.AnimatedVarTracker.IsAnimated(vn))
                    return true;
                if (attrs.TryGetValue("KeyName", out var kn)
                    && Phoenix.Controls.Shared.Core.AnimatedVarTracker.IsAnimated(kn))
                    return true;
                if (attrs.TryGetValue("Name", out var nn)
                    && Phoenix.Controls.Shared.Core.AnimatedVarTracker.IsAnimated(nn))
                    return true;
            }

            string socketName = sock.Label ?? string.Empty;
            if (!string.IsNullOrEmpty(socketName)
                && Phoenix.Controls.Shared.Core.AnimatedVarTracker.IsAnimated(socketName))
                return true;
        }
        catch
        {
            // Best-effort — registry / attribute reads are non-critical for
            // canvas paint; never let a bad attribute name break the overlay.
        }
        return false;
    }

    // WinUI 3 quirk: DoubleCollection is a DependencyObject and refuses to
    // be set as StrokeDashArray on more than one Shape. A shared static
    // instance throws "Value does not fall within the expected range" on the
    // second assignment, which then aborts the whole .phxg load (the first
    // pin lights up, the next blows the canvas). Hand each shape its own
    // collection.
    private static DoubleCollection NewPinDashArray() => new() { 2, 2 };

    private bool _selectionListenerHooked;
    private bool _animatedVarListenerHooked;

    private void HookPinOverlay(NodeViewModel vm)
    {
        if (ReferenceEquals(_pinBoundVm, vm)) return;
        UnhookPinOverlay();
        _pinBoundVm = vm;
        vm.Inputs.CollectionChanged  += OnPinSocketCollectionChanged;
        vm.Outputs.CollectionChanged += OnPinSocketCollectionChanged;
        vm.PropertyChanged           += OnPinHostingVmPropertyChanged;
        // Hook NodeRoot size once — output pin positions depend on
        // NodeRoot.ActualWidth which fluctuates as the body intrinsic-grows
        // for wider socket labels / longer pill content.
        if (!_nodeRootSizeHooked && NodeRoot is not null)
        {
            NodeRoot.SizeChanged += OnNodeRootSizeChanged;
            _nodeRootSizeHooked = true;
        }
        // Listen to canvas-wide socket-selection changes so this NodeView
        // can clear / set its own ring when a sibling NodeView's pin is clicked.
        if (!_selectionListenerHooked)
        {
            s_selectionChanged += OnGlobalSelectionChanged;
            _selectionListenerHooked = true;
        }
        // Listen for cross-pillar animated-var state changes so the
        // animated-pin badge re-paints live (without waiting for the next
        // pin overlay rebuild). Subscriber is per-NodeView so the static
        // tracker's invocation list grows linearly with visible nodes — a
        // bounded ceiling since unrealised NodeViews unhook on DataContext
        // change / Unloaded.
        if (!_animatedVarListenerHooked)
        {
            Phoenix.Controls.Shared.Core.AnimatedVarTracker.AnimationStateChanged
                += OnAnimatedVarStateChanged;
            _animatedVarListenerHooked = true;
        }
        RebuildPinOverlay();
    }

    private void UnhookPinOverlay()
    {
        // Tear down the per-NodeView tooltip DispatcherTimer so
        // its Tick closure (which captures this NodeView via the _tooltipPending*
        // fields) stops pinning the instance alive across view recycling
        // (palette hovers, virtualization, rapid graph loads). DispatcherTimer
        // has no Dispose() in WinUI 3, so Stop + detach Tick + null is the
        // correct teardown; the pre-fix code never released the timer, leaking
        // a NodeView per recycle.
        CleanupTooltipTimer();
        // Also clear the coalesced-reposition guard. A reposition can be
        // enqueued (flag set true) and the NodeView torn down before the dispatcher
        // pumps the callback that would reset it; left stuck true, the recycled
        // view's next RebuildPinOverlay → ScheduleCoalescedReposition early-returns
        // and never re-enqueues, leaving pins misaligned until another change fires.
        _repositionScheduled = false;
        // Same teardown guard for the coalesced REBUILD
        // flag: a deferred rebuild can be enqueued and the view torn down before
        // the pump fires; clear it BEFORE the early-return below so a recycled
        // view's next ScheduleCoalescedRebuild isn't stuck and re-enqueues.
        _rebuildScheduled = false;
        if (_pinBoundVm is null)
        {
            // Even with no VM bound, ensure the selection listener is
            // released — Unloaded → UnhookPinOverlay can fire after a
            // DataContext swap already cleared _pinBoundVm.
            if (_selectionListenerHooked)
            {
                s_selectionChanged -= OnGlobalSelectionChanged;
                _selectionListenerHooked = false;
            }
            if (_animatedVarListenerHooked)
            {
                Phoenix.Controls.Shared.Core.AnimatedVarTracker.AnimationStateChanged
                    -= OnAnimatedVarStateChanged;
                _animatedVarListenerHooked = false;
            }
            return;
        }
        _pinBoundVm.Inputs.CollectionChanged  -= OnPinSocketCollectionChanged;
        _pinBoundVm.Outputs.CollectionChanged -= OnPinSocketCollectionChanged;
        _pinBoundVm.PropertyChanged           -= OnPinHostingVmPropertyChanged;
        foreach (var (vm, _) in _pinElements)
        {
            vm.PropertyChanged -= OnPinSocketPropertyChanged;
            // Drop stale measurements so a future Architect load of the same
            // .phxg doesn't reuse the prior session's row offsets — sockets
            // are re-Guid'd on graph load, so collision is unlikely, but
            // keeping the map tight matters for long-lived sessions.
            SocketRenderState.Forget(vm.Id);
        }
        _pinElements.Clear();
        PinOverlay?.Children.Clear();
        if (_selectionListenerHooked)
        {
            s_selectionChanged -= OnGlobalSelectionChanged;
            _selectionListenerHooked = false;
        }
        if (_animatedVarListenerHooked)
        {
            Phoenix.Controls.Shared.Core.AnimatedVarTracker.AnimationStateChanged
                -= OnAnimatedVarStateChanged;
            _animatedVarListenerHooked = false;
        }
        _pinBoundVm = null;
    }

    /// <summary>
    /// React to a cross-pillar animated-var state flip. Re-evaluates
    /// IsPinAnimatedExternally for every pin on this NodeView and toggles
    /// the badge Visibility. Cheap: pin count per node is small (~8 typical)
    /// and we only touch Visibility on a single Ellipse per pin. Marshalled
    /// to the UI thread because the tracker can fire from any pillar's
    /// dispatcher — Visualist keyframe edits run on its own UI thread which
    /// is not Architect's.
    /// </summary>
    private void OnAnimatedVarStateChanged(string varName, bool isAnimated)
    {
        _ = varName; _ = isAnimated; // re-evaluate against IsPinAnimatedExternally
        // Match the dispatcher pattern used elsewhere in this file (the host
        // canvas's DispatcherQueue is the right one for UI-tree mutation).
        // Capture into a local: the tracker can fire from any pillar's
        // dispatcher, so the property could be cleared between the null-check
        // and the enqueue. Fall back to synchronous apply if the queue is
        // gone or the enqueue is rejected (full / shutting down).
        var dq = DispatcherQueue;
        if (dq is null || !dq.TryEnqueue(ApplyAnimatedBadgeVisibility))
        {
            ApplyAnimatedBadgeVisibility();
        }
    }

    private void ApplyAnimatedBadgeVisibility()
    {
        foreach (var (sock, ele) in _pinElements)
        {
            if (ele.AnimatedBadge is null) continue;
            ele.AnimatedBadge.Visibility = IsPinAnimatedExternally(sock)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// React to a canvas-wide socket-selection change. Walks the
    /// per-NodeView pin map and toggles SelectionRing visibility against
    /// the static <see cref="s_selectedSocketId"/>. Cheap: pin count per
    /// node is small (~8 typical) and we only touch Visibility.
    /// </summary>
    private void OnGlobalSelectionChanged(object? sender, EventArgs e)
    {
        foreach (var (sock, ele) in _pinElements)
        {
            if (ele.SelectionRing is null) continue;
            bool selected = !string.IsNullOrEmpty(s_selectedSocketId)
                            && string.Equals(s_selectedSocketId, sock.Id, StringComparison.Ordinal);
            ele.SelectionRing.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnPinHostingVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Width changes shift output pin X; HeaderSectionHeight changes shift
        // every pin Y because RowCenterY is measured from the header bottom.
        if (e.PropertyName == nameof(NodeViewModel.Width) ||
            e.PropertyName == nameof(NodeViewModel.Height) ||
            e.PropertyName == nameof(NodeViewModel.HeaderSectionHeight))
        {
            RepositionAllPins();
        }
    }

    private void OnNodeRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // NodeRoot.ActualWidth drives the OUTPUT pin's X (= width). Inputs
        // sit on x=0 regardless, but reposition them too so a single pass
        // refreshes the whole overlay.
        RepositionAllPins();
    }

    private void OnPinSocketCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Sockets can change shape via NodeViewModel.RebuildSockets (placeholder
        // activation, dynamic-event grow) — easiest correct path is a full
        // rebuild rather than incremental add/remove tracking. The pin count
        // per node is small (~8 typical, ~16 worst-case) so the rebuild cost
        // is negligible compared to the layout pass it triggers anyway.
        // Coalesce the rebuild: a RebuildSockets cycle
        // raises a BURST of CollectionChanged events (one per socket add/remove),
        // each of which used to do a full teardown+recreate of every pin element
        // synchronously. Defer + de-dupe so the whole burst collapses into ONE
        // rebuild on the next pump (mirrors ScheduleCoalescedReposition). The
        // first build from HookPinOverlay stays synchronous so a freshly bound
        // node has its pins present for the very next wire-drag hit-test.
        ScheduleCoalescedRebuild();
    }

    private void RebuildPinOverlay()
    {
        if (PinOverlay is null || _pinBoundVm is null) return;

        // 0.11.x polish — trace the pin overlay rebuild. Called from
        // OnPinSocketCollectionChanged on every Add/Remove during a
        // RebuildSockets cycle (placeholder activation, dynamic event-pair
        // grow); the per-element add is reasonably cheap but a node with
        // many sockets going through a full sequence-add can do an N×M
        // walk depending on the dispatcher load. Tracing here surfaces the
        // path in stall reports.
        using var _trace = Phoenix.Controls.Shared.Services.UiActivityTrace
            .Begin($"Architect.RebuildPinOverlay[{_pinBoundVm.Title}]");

        foreach (var (vm, _) in _pinElements)
            vm.PropertyChanged -= OnPinSocketPropertyChanged;
        _pinElements.Clear();
        PinOverlay.Children.Clear();

        foreach (var sock in _pinBoundVm.Inputs)  AddPinElement(sock);
        foreach (var sock in _pinBoundVm.Outputs) AddPinElement(sock);
        // Initial best-effort placement off the SocketAnchor estimate so the
        // pins aren't stacked at the overlay origin before rows realise.
        RepositionAllPins();

        // Pin overlay double-reposition jump.
        // The new ItemsControl rows haven't realised yet, so the synchronous
        // RepositionAllPins above reads STALE measured-Y. Pre-fix this file
        // ALSO eagerly deferred a second full RepositionAllPins to the next
        // pump AND each row's Loaded/SizeChanged repositioned its own pin
        // individually — so a pin could jump twice (estimate → row-trickle →
        // deferred) and the overlay visibly settled in two stages. The
        // REFRAMED fix coalesces all of that into a SINGLE deferred full
        // reposition that runs after the rows have fired Loaded/SizeChanged
        // and populated the measured-Y map: ScheduleCoalescedReposition()
        // de-dupes so the burst of row events + this rebuild collapse into one
        // RepositionAllPins pass instead of N. The per-row handlers no longer
        // poke Canvas positions directly — they only push the measured Y and
        // request the coalesced pass (see TryMeasureSocketRow).
        ScheduleCoalescedReposition();
    }

    // Coalesced full-overlay reposition.
    // Multiple triggers fire in one layout cycle (rebuild + each row's Loaded
    // + each row's SizeChanged); without coalescing each scheduled its own
    // full reposition and the overlay settled in stages (the "jump twice"
    // symptom). This guard ensures at most one deferred RepositionAllPins is
    // queued per pump cycle — the burst collapses into a single pass that runs
    // after all rows have measured.
    private bool _repositionScheduled;
    private void ScheduleCoalescedReposition()
    {
        if (_repositionScheduled) return;
        var queue = DispatcherQueue;
        if (queue is null)
        {
            // No dispatcher (designer-time / pre-realised) — reposition inline.
            RepositionAllPins();
            return;
        }
        _repositionScheduled = true;
        queue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _repositionScheduled = false;
                RepositionAllPins();
            });
    }

    // Coalesced full-overlay REBUILD, mirroring
    // ScheduleCoalescedReposition. OnPinSocketCollectionChanged fires once per
    // socket add/remove during a RebuildSockets cycle; without this, a node
    // growing K sockets did K full PinOverlay teardown+recreate passes back to
    // back. The guard collapses the burst into a single rebuild on the next Low-
    // priority pump. RebuildPinOverlay guards (PinOverlay/_pinBoundVm null), so a
    // callback that fires after teardown is a safe no-op.
    private bool _rebuildScheduled;
    private void ScheduleCoalescedRebuild()
    {
        if (_rebuildScheduled) return;
        var queue = DispatcherQueue;
        if (queue is null)
        {
            // No dispatcher (designer-time / pre-realised) — rebuild inline.
            RebuildPinOverlay();
            return;
        }
        _rebuildScheduled = true;
        queue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _rebuildScheduled = false;
                RebuildPinOverlay();
            });
    }

    private void AddPinElement(SocketViewModel sock)
    {
        if (PinOverlay is null) return;

        var pinChrome = new Grid
        {
            Width  = 14,
            Height = 14,
            Tag    = sock,
            // Tooltips are nice-to-have but the row label inside NodeRoot
            // already carries a tooltip; skip on the overlay pin to avoid
            // double tooltips when the cursor is over the pin AND the label.
        };

        // Layer 1 — required-empty halo (inputs only). Always present so a
        // later IsRequiredEmpty=true flip just toggles Visibility.
        Ellipse? required = null;
        if (sock.Direction == SocketType.Input)
        {
            required = new Ellipse
            {
                Width = 14, Height = 14,
                Stroke = PinRequiredHaloBrush,
                StrokeThickness = 1.5,
                Fill = s_transparentBrush,
                IsHitTestVisible = false,
                Visibility = sock.IsRequiredEmpty ? Visibility.Visible : Visibility.Collapsed,
            };
            pinChrome.Children.Add(required);
        }

        // Layer 2 — drop-state halo (Valid green glow / Invalid red during
        // drag). 20×20 (was 14×14) with a 2.0 stroke (was 1.75) so the
        // compatible-target glow ring reads as a reachability affordance, not a
        // tight selection marker. The 20px ring overhangs the 14×14 chrome,
        // which is fine — it's parented to the pin chrome Grid and centred.
        var drop = new Ellipse
        {
            Width = 20, Height = 20,
            Stroke = DropStateBrushFor(sock.DropState),
            StrokeThickness = 2.0,
            Fill = s_transparentBrush,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Visibility = sock.DropState == DropState.None ? Visibility.Collapsed : Visibility.Visible,
        };
        pinChrome.Children.Add(drop);

        // Layer 3 — the pin glyph itself, built as a native shape at the
        // final 11×11 footprint (no Viewbox / no Path-from-Geometry indirect).
        // The pre-0.11.7 Path-inside-Viewbox approach paints nothing inside
        // Architect.WinUI's library-hosted-by-Hub setup: a Path with only
        // Data (no Width/Height) reports a measured bound of (origin..max)
        // including the coordinate-space offset, the Viewbox scales that,
        // and the resulting render rectangle silently collapses to zero
        // under XAML-Islands hosting — every pin painted nothing despite
        // the 0.11.6 hotfix making Geometry construction direct.
        //
        // Building the shape directly at 11×11 sidesteps the issue entirely:
        // Ellipse for Circle; Polygon for Chevron/Triangle/Diamond/Square;
        // Rectangle with CornerRadius for RoundedSquare. Each shape carries
        // its own Stroke/Fill and is hit-tested through the 24×24 transparent
        // ellipse mounted last in the same pinChrome Grid.
        var pinShape = BuildPinShape(sock.Kind, sock.ColorHex, sock.FillColorHex);
        pinShape.Tag = sock;
        if (sock.IsDynamicPlaceholder) pinShape.StrokeDashArray = NewPinDashArray();
        // Unconnected pins paint as
        // outlines (Fill brush at 0.4 opacity), connected pins paint solid.
        // We touch only the Shape's Fill.Opacity — Stroke stays at full
        // opacity so the outline still reads.
        ApplyPinConnectedOpacity(pinShape, sock);
        pinChrome.Children.Add(pinShape);

        // Layer 4 — wildcard dashed overlay for unresolved Any-typed sockets.
        var wildcard = new Ellipse
        {
            Width = 11, Height = 11,
            Stroke = HexBrush(sock.ColorHex),
            StrokeThickness = 1.5,
            StrokeDashArray = NewPinDashArray(),
            Fill = s_transparentBrush,
            IsHitTestVisible = false,
            Visibility = sock.IsWildcard ? Visibility.Visible : Visibility.Collapsed,
        };
        pinChrome.Children.Add(wildcard);

        // Layer 5 — Vector arity glyph ("2"/"3"/"4").
        var arity = new TextBlock
        {
            Text = sock.HasVectorArity ? sock.VectorArity.ToString() : string.Empty,
            FontSize = 7,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ResolveBrush("CoalPaperBrush", 0xFF, 0xF5, 0xEF, 0xE3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = sock.HasVectorArity ? Visibility.Visible : Visibility.Collapsed,
        };
        pinChrome.Children.Add(arity);

        // Layer 6 — selection ring.
        // Painted as a 16×16 gold ellipse with Stroke=EmberPrimary, transparent
        // fill, sitting on top of the pin glyph but BELOW the hit target so
        // clicks still land on the HitTarget. Visibility tracks the static
        // s_selectedSocketId via OnGlobalSelectionChanged.
        // The per-socket selection ring is DEAD in
        // the current build: nothing calls SetSelectedSocket with a non-null id (pin
        // selection was removed; ClearPinSelection only ever passes null), so it is
        // never shown. Create it ONLY when the socket is selected right now (never,
        // in practice), saving one element per pin × every socket. If socket-
        // selection is re-enabled later, SetSelectedSocket must trigger a pin rebuild
        // so this layer is (re)created — the in-place toggle (OnGlobalSelectionChanged)
        // is already null-guarded and no-ops on a skipped ring.
        Ellipse? selectionRing = null;
        if (!string.IsNullOrEmpty(s_selectedSocketId)
            && string.Equals(s_selectedSocketId, sock.Id, StringComparison.Ordinal))
        {
            selectionRing = new Ellipse
            {
                Width = 16, Height = 16,
                Stroke = PinSelectionBrush,
                StrokeThickness = 2,
                Fill = s_transparentBrush,
                IsHitTestVisible = false,
                Visibility = Visibility.Visible,
            };
            pinChrome.Children.Add(selectionRing);
        }

        // Layer 7 — animated-pin badge.
        // A 6×6 yellow dot in the bottom-right of the pin chrome surfaces
        // "this socket's destination variable has Visualist keyframes".
        // The cross-pillar registry isn't wired yet (IsPinAnimatedExternally
        // is a TODO stub returning false), so the badge currently never
        // paints — but the layer is in place so the activation is a one-line
        // change inside that helper once Phoenix.Controls.Shared grows a
        // shared AnimatedVarTracker.
        // IsPinAnimatedExternally is a stub returning
        // false (the cross-pillar AnimatedVarTracker isn't wired), so this badge
        // never paints. Create it ONLY when the socket is actually animated (never,
        // currently), saving one element per pin × every socket. When the registry
        // is wired, the activation must (re)create this layer + rebuild; the in-place
        // toggles (the DropState pass / OnAnimatedVarStateChanged) are null-guarded.
        Ellipse? animatedBadge = null;
        if (IsPinAnimatedExternally(sock))
        {
            animatedBadge = new Ellipse
            {
                Width = 6, Height = 6,
                Fill = s_animatedBadgeBrush,
                Stroke = null,
                IsHitTestVisible = false,
                // Bottom-right corner of the 14×14 chrome.
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Visibility = Visibility.Visible,
            };
            pinChrome.Children.Add(animatedBadge);
        }

        // Layer 8 — 24×24 transparent WCAG hit-target. The 24-px-wide hit
        // box is centred via Margin=-5 over the 14×14 pin footprint, so the
        // pointer target is comfortably larger than the painted pin.
        var hit = new Ellipse
        {
            Width = 24, Height = 24,
            Margin = new Thickness(-5),
            Fill = s_transparentBrush,
            Tag = sock,
        };
        // Precise marker so the canvas pointer code can tell a
        // genuine socket-PIN press (this 24×24 hit-target) apart from the socket
        // ROW grid, which shares Tag=SocketViewModel. LogicCanvasView.ResolvePinUnderCursor
        // keys on this to start a wire-drag authoritatively — even when WinUI reports
        // e.OriginalSource as the row container behind the pin ("no pipe on some sockets").
        CanvasInteraction.SetIsSocketPin(hit, true);
        // Clicking a pin no longer paints a persistent selection ring on the
        // bubble. Majo flagged the click-to-select halo as an unwanted
        // "selection frame" on the socket — it also broke the UE-Blueprints
        // idiom the canvas models (pins are pure wire endpoints; only nodes
        // and wires carry selection). The press is left unhooked here so the
        // canvas's wire-drop path still starts a drag on the same press; the
        // SelectionRing layer below stays in the tree but is now only ever
        // shown if something explicitly calls SetSelectedSocket (nothing on
        // the plain-click path does anymore).
        // Hover-anchored
        // TooltipPopup on the pin's hit target. PointerEntered starts a
        // ~600 ms delay timer; expiry positions the shared TooltipPopup at
        // the cursor and shows it with the socket's title (Label · DataType)
        // and body (Description). PointerExited / PointerCanceled / a
        // captured PointerPressed cancels the timer + hides the popup so a
        // drag-start (which is also a press on this hit target) doesn't
        // leave a stale tip floating mid-canvas.
        hit.PointerEntered  += OnPinHitTargetPointerEnteredForTooltip;
        hit.PointerMoved    += OnPinHitTargetPointerMovedForTooltip;
        hit.PointerExited   += OnPinHitTargetPointerExitedForTooltip;
        hit.PointerCanceled += OnPinHitTargetPointerExitedForTooltip;
        hit.PointerCaptureLost += OnPinHitTargetPointerExitedForTooltip;
        // Stamp this pin on the canvas FIRST (so the bubbling
        // press reaches OnHostPointerPressed with the socket already known —
        // the deterministic replacement for the flaky geometry probe), THEN
        // cancel the hover-tip so it doesn't paint underneath the drag cursor.
        // Both run on the same press; neither marks it Handled, so it still
        // bubbles to HostRoot to start the wire-drop + capture the pointer.
        hit.PointerPressed  += OnPinHitTargetPointerPressedForDrag;
        hit.PointerPressed  += OnPinHitTargetPointerExitedForTooltip;
        pinChrome.Children.Add(hit);

        PinOverlay.Children.Add(pinChrome);
        var built = new PinElements
        {
            Container = pinChrome,
            RequiredHalo = required,
            DropHalo = drop,
            Pin = pinShape,
            Wildcard = wildcard,
            Arity = arity,
            SelectionRing = selectionRing,
            AnimatedBadge = animatedBadge,
            HitTarget = hit,
            RowIndex = sock.RowIndex,
            Direction = sock.Direction,
            PinKind = sock.Kind,
            IsDashed = sock.IsDynamicPlaceholder,
        };
        _pinElements[sock] = built;
        // Apply the wire-drag dimming for the socket's CURRENT
        // DropState so a pin rebuilt mid-drag (placeholder activation, dynamic
        // grow) immediately reflects compatibility instead of painting bright
        // until the next DropState change.
        ApplyWireDragDimming(built, sock);
        sock.PropertyChanged += OnPinSocketPropertyChanged;
    }

    // ─── Hover-anchored TooltipPopup wiring ─────────────────────────────
    // Pre-T15 Architect rendered a custom TooltipPopup (rich title + glyph +
    // body, mouse-anchored) on socket / pill hover; post-T15 fell back to
    // plain ToolTipService which lost the iconography + the cursor-following
    // placement Design_Orders §4.9 specified. The TooltipPopup primitive
    // exists in Phoenix.Controls.Architect.WinUI/Controls/TooltipPopup.xaml(.cs)
    // — this block hooks the per-pin hit target and the per-row socket-label
    // grids (PointerEntered → 600 ms delay → Show; PointerExited → cancel +
    // Hide). One shared DispatcherTimer per NodeView is enough — sockets on
    // the same node never have overlapping hover sessions.

    // Dwell-only show policy. A tip paints only after
    // a genuine InitialDelayMs (400ms) of the cursor RESTING on a socket/pill/title.
    // The earlier "smart re-show" (skip the delay when re-hovering within
    // ReshowDelayMs of the last hide) was removed: it fired Show()/Popup-open on
    // every socket crossed during a pan (incl. wheel-pan, where nodes slide under a
    // still cursor) or a fast hover-sweep, which (a) made an unwanted popup appear
    // mid-pan and (b) churned the XamlRoot layout via rapid Popup open/close — the
    // suspected editing-freeze trigger. Dwell-only means tips never appear while
    // moving/panning. See StartTooltipTimer.
    private const int InitialDelayMs      = 400;
    private DispatcherTimer? _tooltipDelayTimer;
    private SocketViewModel? _tooltipPendingSocket;
    private MiddleAttributeViewModel? _tooltipPendingMiddleAttr;
    // Generic title/body path — used by the node-title header hover so the
    // node description shows in the same dark, cursor-anchored popup as the
    // socket / pill tooltips (it used to be a plain default-style
    // ToolTipService.ToolTip="{Binding NodeTooltip}" binding).
    private string? _tooltipPendingTitleText;
    private string? _tooltipPendingBodyText;
    private FrameworkElement? _tooltipPendingAnchor;

    /// <summary>
    /// Pointer entered the per-pin hit target. Cache the socket +
    /// anchor and arm the delay timer. The Show call doesn't fire until
    /// <see cref="InitialDelayMs"/> ms elapses without a PointerExited,
    /// so cursor flickers across the canvas don't pop transient tips.
    /// </summary>
    private void OnPinHitTargetPointerEnteredForTooltip(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not SocketViewModel sock) return;
        SeedTooltipPointerPosition(fe, e);
        ArmTooltipDelay(sock, fe);
    }

    private void OnPinHitTargetPointerMovedForTooltip(object sender, PointerRoutedEventArgs e)
    {
        // Tracking the cursor while the timer is still armed keeps the
        // popup's first-paint position glued to wherever the mouse is at
        // expiry — pre-fix the popup snapped to whichever pixel the
        // PointerEntered grabbed, which could be anywhere along the 24×24
        // hit ellipse depending on entry edge.
        if (sender is FrameworkElement fe) SeedTooltipPointerPosition(fe, e);
    }

    private void OnPinHitTargetPointerExitedForTooltip(object sender, PointerRoutedEventArgs e)
    {
        CancelTooltipDelayAndHide();
    }

    /// <summary>
    /// Stamp the pressed socket on the owning canvas so its
    /// <c>OnHostPointerPressed</c> starts the wire-drag deterministically. This
    /// handler runs on the pin's own hit-target — i.e. WinUI already hit-tested
    /// the press onto THIS pin — so it is authoritative, unlike the canvas-level
    /// geometry probe that resolved a pin only ~1-in-10 presses ("pipe preview
    /// not visible"). We do NOT mark the event Handled: it must still bubble to
    /// HostRoot's OnHostPointerPressed, which reads the stamp, sets up the drag
    /// and captures the pointer. Left-button only — right-press on a pin keeps
    /// routing to the context menu; Alt/Ctrl modifiers are interpreted canvas-
    /// side from the same socket.
    /// </summary>
    private void OnPinHitTargetPointerPressedForDrag(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not SocketViewModel sock) return;
        if (!e.GetCurrentPoint(fe).Properties.IsLeftButtonPressed) return;
        GetCanvasCached(fe)?.NotePinPress(sock);
    }

    /// <summary>
    /// Push the current cursor position (XamlRoot-relative) into the
    /// shared <see cref="TooltipPopup"/> static so the popup's first paint
    /// anchors to it. Bare ResolvePointerPositionForAnchor would fall back
    /// to the anchor element's top-right corner; this keeps the popup
    /// "below-right of the cursor" feel Design_Orders §4.9 specifies.
    /// </summary>
    private static void SeedTooltipPointerPosition(FrameworkElement anchor, PointerRoutedEventArgs e)
    {
        try
        {
            // GetCurrentPoint(null) returns coordinates relative to the
            // anchor's XamlRoot content — same coord space the popup's
            // HorizontalOffset / VerticalOffset use.
            var pt = e.GetCurrentPoint(null).Position;
            TooltipPopup.UpdatePointerPosition(pt);
        }
        catch
        {
            // Designer-time / pre-realised tree — popup will fall back to
            // the anchor's top-right corner via ResolvePointerPositionForAnchor.
        }
    }

    private void ArmTooltipDelay(SocketViewModel sock, FrameworkElement anchor)
    {
        _tooltipPendingSocket = sock;
        _tooltipPendingMiddleAttr = null;
        _tooltipPendingTitleText = null;
        _tooltipPendingBodyText = null;
        _tooltipPendingAnchor = anchor;
        StartTooltipTimer();
    }

    /// <summary>
    /// Start (or restart) the shared tooltip delay timer with the dwell-only
    /// delay (<see cref="InitialDelayMs"/>). Re-entry from a sibling pin stops +
    /// restarts the timer so the delay restarts cleanly. Centralised so the pin /
    /// socket-row / node-title / middle-attr arm sites all share one delay
    /// policy. The tip therefore only paints after a genuine rest on a socket —
    /// see the note in the body for why the former
    /// 0ms fast-reshow was removed.
    /// </summary>
    private void StartTooltipTimer()
    {
        // Always require the full dwell delay — the
        // 0ms fast-reshow (skip the delay when re-hovering within ReshowDelayMs of
        // the last hide) is exactly what made tooltips POP DURING MOVEMENT: panning
        // (incl. wheel-pan, where the cursor is still and nodes slide under it) and
        // fast hover-sweeps cross socket after socket, each enter-within-200ms-of-the-
        // last-exit firing an immediate Show()/Popup-open → a default-looking popup
        // appears mid-pan AND the rapid Popup open/close churns the XamlRoot layout
        // (the suspected long-running editing-freeze trigger Majo flagged). Gating on
        // a genuine InitialDelayMs dwell means a tip only appears when the user
        // actually rests on a socket — never while moving/panning.
        int delayMs = InitialDelayMs;
        if (_tooltipDelayTimer is null)
        {
            _tooltipDelayTimer = new DispatcherTimer();
            _tooltipDelayTimer.Tick += OnTooltipDelayTick;
        }
        else
        {
            _tooltipDelayTimer.Stop();
        }
        // A zero interval still requires a pump cycle to fire Tick; that's the
        // "snappy re-show" feel (immediate on the next dispatch) without
        // re-entrantly showing from inside the PointerEntered handler.
        _tooltipDelayTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _tooltipDelayTimer.Start();
    }

    private void OnTooltipDelayTick(object? sender, object e)
    {
        _tooltipDelayTimer?.Stop();
        var anchor = _tooltipPendingAnchor;
        if (anchor is null) return;
        try
        {
            // Socket-pin path — title = "Label · DataType", body =
            // socket Description. Middle-attribute path — title = key,
            // body = current display text. Both feed the same shared popup.
            if (_tooltipPendingSocket is { } sock)
            {
                string title = $"{sock.Label} · {sock.DataType}";
                string? body = string.IsNullOrEmpty(sock.Description) ? null : sock.Description;
                TooltipPopup.Show(anchor, title, body, glyph: null);
            }
            else if (_tooltipPendingMiddleAttr is { } middle)
            {
                TooltipPopup.Show(anchor, middle.Key, middle.DisplayText, glyph: null);
            }
            else if (!string.IsNullOrEmpty(_tooltipPendingTitleText))
            {
                // Node-title header hover — title line + optional description
                // body, same dark popup as the socket / middle-attr paths.
                TooltipPopup.Show(anchor, _tooltipPendingTitleText!, _tooltipPendingBodyText, glyph: null);
            }
        }
        catch
        {
            // Best-effort — popup mounts into the anchor's XamlRoot, which
            // can be null briefly during tab-switch / re-parent. Quietly
            // skip rather than fault the tick.
        }
    }

    private void CancelTooltipDelayAndHide()
    {
        _tooltipDelayTimer?.Stop();
        _tooltipPendingSocket = null;
        _tooltipPendingMiddleAttr = null;
        _tooltipPendingTitleText = null;
        _tooltipPendingBodyText = null;
        _tooltipPendingAnchor = null;
        try { TooltipPopup.Hide(); }
        catch { /* shared popup may already be closed by a sibling exit */ }
    }

    /// <summary>
    /// Fully release the per-NodeView tooltip DispatcherTimer.
    /// Stop it, detach the Tick handler (whose closure captures this NodeView),
    /// null the reference, and clear the pending-tooltip fields so nothing
    /// keeps the instance alive after unload / DataContext swap. Idempotent.
    /// </summary>
    private void CleanupTooltipTimer()
    {
        if (_tooltipDelayTimer is not null)
        {
            try { _tooltipDelayTimer.Stop(); } catch { /* detached tree */ }
            _tooltipDelayTimer.Tick -= OnTooltipDelayTick;
            _tooltipDelayTimer = null;
        }
        _tooltipPendingSocket = null;
        _tooltipPendingMiddleAttr = null;
        _tooltipPendingTitleText = null;
        _tooltipPendingBodyText = null;
        _tooltipPendingAnchor = null;
    }

    // ─── Node-title header hover → dark TooltipPopup ─────────────────────
    // Hooked from NodeView.xaml's header Border. Mirrors the socket-row
    // handlers: PointerEntered arms the shared 600 ms delay, PointerMoved
    // keeps the popup glued to the cursor, PointerExited cancels + hides.
    // Replaces the old plain ToolTipService.ToolTip="{Binding NodeTooltip}"
    // binding that rendered in the bare Windows default style.
    private void OnNodeTitlePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (DataContext is not NodeViewModel vm) return;
        SeedTooltipPointerPosition(fe, e);
        _tooltipPendingSocket = null;
        _tooltipPendingMiddleAttr = null;
        _tooltipPendingTitleText = string.IsNullOrEmpty(vm.Title) ? vm.NodeTooltip : vm.Title;
        // Body = the descriptive NodeTooltip unless it just repeats the title.
        _tooltipPendingBodyText =
            (!string.IsNullOrEmpty(vm.NodeTooltip)
             && !string.Equals(vm.NodeTooltip, _tooltipPendingTitleText, StringComparison.Ordinal))
                ? vm.NodeTooltip
                : null;
        _tooltipPendingAnchor = fe;
        if (string.IsNullOrEmpty(_tooltipPendingTitleText)) return;
        StartTooltipTimer();
    }

    private void OnNodeTitlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) SeedTooltipPointerPosition(fe, e);
    }

    private void OnNodeTitlePointerExited(object sender, PointerRoutedEventArgs e)
    {
        CancelTooltipDelayAndHide();
    }

    /// <summary>
    /// DB-picker chevron tooltip. The chevron lives in the input row's
    /// Column 2, outside the Column 0 label grid that carries
    /// <see cref="OnSocketRowGridPointerEntered"/>, so it had no dark tooltip —
    /// only a bare <c>ToolTipService.ToolTip="Pick from databank"</c>, a SECOND
    /// (default, mouse-bound) WinUI tooltip coexisting with the custom
    /// <see cref="TooltipPopup"/> on the same node-hover surface (the "default
    /// mouse-bound tooltip still enabled, suspected of causing freezes" Majo
    /// flagged). Route the hint through the shared dark popup so the node body
    /// runs a single tooltip system. <see cref="TooltipPopup.Attach"/> owns the
    /// pointer enter/move/exit wiring + delay and auto-detaches on the
    /// element's Unloaded, so there's no symmetric Detach to wire here.
    /// </summary>
    private void OnDatabankPickerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            TooltipPopup.Attach(fe, "Pick from databank");
    }

    /// <summary>
    /// Invoked from <see cref="NodeView"/> XAML on the input-row /
    /// output-row label grids. Same shape as the per-pin hit-target handlers
    /// but the anchor is the row label's grid, so the popup paints next to
    /// the label as well as the pin.
    /// </summary>
    internal void OnSocketRowGridPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SocketViewModel sock) return;
        SeedTooltipPointerPosition(fe, e);
        ArmTooltipDelay(sock, fe);
    }

    internal void OnSocketRowGridPointerExited(object sender, PointerRoutedEventArgs e)
    {
        CancelTooltipDelayAndHide();
    }

    /// <summary>
    /// Middle-attribute row hover. Shows "{Key}" as title +
    /// "{DisplayText}" as body so the popup mirrors the inline pill content
    /// without leaning on the bare ToolTipService binding the row's grid
    /// also carries.
    /// </summary>
    internal void OnMiddleAttrRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MiddleAttributeViewModel m) return;
        SeedTooltipPointerPosition(fe, e);
        _tooltipPendingSocket = null;
        _tooltipPendingMiddleAttr = m;
        _tooltipPendingTitleText = null;
        _tooltipPendingBodyText = null;
        _tooltipPendingAnchor = fe;
        StartTooltipTimer();
    }

    internal void OnMiddleAttrRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        CancelTooltipDelayAndHide();
    }

    /// <summary>
    /// Keep the pin glyph's Fill at full opacity. Pre-fix this dimmed
    /// unconnected pins to 0.4 alpha to read connected-vs-unconnected, but
    /// that washed out every unwired pin and made fills look "missing".
    /// Per Majo's socket legend the fill now encodes required-vs-optional
    /// (solid vs hollow) at the brush level — SocketViewModel paints optional
    /// inputs with a transparent fill and required/output/flow pins with the
    /// type colour — so the opacity must stay 1.0 or the solid pins dim too.
    /// Retained as a no-op-ish hook (always 1.0) so the existing call sites
    /// in AddPinElement / OnPinSocketPropertyChanged don't need rewiring.
    /// No-op when the Shape has no SolidColorBrush fill.
    /// </summary>
    private static void ApplyPinConnectedOpacity(
        Microsoft.UI.Xaml.Shapes.Shape pinShape,
        SocketViewModel sock)
    {
        _ = sock;
        if (pinShape.Fill is SolidColorBrush sb)
        {
            sb.Opacity = 1.0;
        }
    }

    /// <summary>
    /// Wire-drag compatibility dimming. During a wire
    /// drag the canvas (LogicCanvasViewModel.BeginDropHinting) flips every
    /// candidate socket's <see cref="SocketViewModel.DropState"/> to
    /// Valid / Invalid (compat already evaluated via NodeRegistry.AreCompatible)
    /// or None (source / same-direction / same-node / no drag). Baseline dimmed
    /// INCOMPATIBLE input sockets to 0.3 alpha so they visually recede as
    /// non-targets while compatible inputs + the green glow ring stand out.
    /// We dim the pin glyph (ele.Pin.Opacity) so the incompatible target recedes
    /// while the red DropHalo stays full-opacity as the "no" signal. Restored to
    /// 1.0 on Valid / None so drag-end (EndDropHinting → DropState=None) un-dims
    /// everything.
    /// </summary>
    private static void ApplyWireDragDimming(PinElements ele, SocketViewModel sock)
    {
        bool dimIncompatibleInput =
            sock.Direction == SocketType.Input && sock.DropState == DropState.Invalid;
        ele.Pin.Opacity = dimIncompatibleInput ? 0.3 : 1.0;
    }

    private void OnPinSocketPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SocketViewModel sock) return;
        if (!_pinElements.TryGetValue(sock, out var ele)) return;
        switch (e.PropertyName)
        {
            case nameof(SocketViewModel.DropState):
                ele.DropHalo.Stroke = DropStateBrushFor(sock.DropState);
                ele.DropHalo.Visibility = sock.DropState == DropState.None
                    ? Visibility.Collapsed : Visibility.Visible;
                // Dim incompatible INPUT sockets to
                // 0.3 during the drag (canvas already computed compat into
                // DropState). Un-dims on Valid / None (drag end clears DropState).
                ApplyWireDragDimming(ele, sock);
                break;
            case nameof(SocketViewModel.IsRequiredEmpty):
                if (ele.RequiredHalo is not null)
                    ele.RequiredHalo.Visibility = sock.IsRequiredEmpty
                        ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(SocketViewModel.IsConnected):
                if (ele.RequiredHalo is not null)
                    ele.RequiredHalo.Visibility = sock.IsRequiredEmpty
                        ? Visibility.Visible : Visibility.Collapsed;
                // Connectivity flip → re-apply outline/solid opacity on
                // the pin Fill. SyncSocketConnectivity fires this whenever a
                // wire is added or removed, so the glyph picks up the
                // outline-vs-solid state without a full overlay rebuild.
                ApplyPinConnectedOpacity(ele.Pin, sock);
                // Animated-pin badge is currently gated on a stub that
                // returns false, but re-evaluate here so the badge flips
                // immediately once the cross-pillar tracker is wired and
                // IsPinAnimatedExternally starts returning true.
                if (ele.AnimatedBadge is not null)
                    ele.AnimatedBadge.Visibility = IsPinAnimatedExternally(sock)
                        ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(SocketViewModel.ColorHex):
            case nameof(SocketViewModel.PinPathData):
            case nameof(SocketViewModel.FillColorHex):
            case nameof(SocketViewModel.IsWildcard):
            case nameof(SocketViewModel.IsDynamicPlaceholder):
            case nameof(SocketViewModel.HasVectorArity):
            case nameof(SocketViewModel.VectorArity):
                // Kind change → swap the shape type (Ellipse ↔ Polygon ↔
                // Rectangle); same-kind change → mutate Stroke/Fill in place
                // so we don't rebuild the visual tree on every brush bump.
                if (ele.PinKind != sock.Kind)
                {
                    int idx = ele.Container.Children.IndexOf(ele.Pin);
                    if (idx >= 0)
                    {
                        var rebuilt = BuildPinShape(sock.Kind, sock.ColorHex, sock.FillColorHex);
                        rebuilt.Tag = sock;
                        // Fresh shape — always (re)apply the dash to match the
                        // current placeholder state, and record it.
                        if (sock.IsDynamicPlaceholder) rebuilt.StrokeDashArray = NewPinDashArray();
                        ele.IsDashed = sock.IsDynamicPlaceholder;
                        ele.Container.Children.RemoveAt(idx);
                        ele.Container.Children.Insert(idx, rebuilt);
                        ele.Pin = rebuilt;
                        ele.PinKind = sock.Kind;
                    }
                }
                else
                {
                    ele.Pin.Stroke = HexBrush(sock.ColorHex);
                    ele.Pin.Fill = HexBrush(sock.FillColorHex);
                    // Only re-allocate the DoubleCollection when the
                    // dashed-vs-solid state actually flipped. A bare ColorHex /
                    // Fill bump (the common cascade case) on an already-dashed
                    // (or already-solid) pin no longer churns a fresh
                    // DoubleCollection per change. WinUI requires a per-shape
                    // collection (DependencyObject single-parent rule), so the
                    // allocation is unavoidable on a genuine flip — but only then.
                    if (ele.IsDashed != sock.IsDynamicPlaceholder)
                    {
                        ele.Pin.StrokeDashArray = sock.IsDynamicPlaceholder ? NewPinDashArray() : null;
                        ele.IsDashed = sock.IsDynamicPlaceholder;
                    }
                }
                // Fill brush was just rebuilt above (new SolidColorBrush
                // from HexBrush); reapply the outline-vs-solid opacity so a
                // wildcard-cascade type flip doesn't accidentally restore the
                // pin to full opacity on an unconnected socket.
                ApplyPinConnectedOpacity(ele.Pin, sock);
                ele.Wildcard.Stroke = HexBrush(sock.ColorHex);
                ele.Wildcard.Visibility = sock.IsWildcard ? Visibility.Visible : Visibility.Collapsed;
                ele.Arity.Text = sock.HasVectorArity ? sock.VectorArity.ToString() : string.Empty;
                ele.Arity.Visibility = sock.HasVectorArity ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    private void RepositionAllPins()
    {
        if (PinOverlay is null || _pinBoundVm is null) return;
        double width = NodeRoot?.ActualWidth ?? 0;
        if (width <= 0)
        {
            // Flow.Reroute carve-out — for the diamond reroute NodeRoot is
            // Visibility=Collapsed (IsStandardNode=false), so ActualWidth
            // stays at 0 and OnNodeRootSizeChanged never fires. Without a
            // fallback both pins ended up at Canvas.SetLeft/Top=(0,0),
            // stacking the input and output chevrons at the diamond's top-
            // left corner instead of straddling its W/E tips at x=0,y=10 and
            // x=20,y=10 (the actual wire-anchor points). Fall back to the
            // VM's intrinsic Width — NodeGeometry.EffectiveWidth returns the
            // fixed 20 for reroute and the live shrink-to-fit width for
            // standard nodes, so this is also the right answer on a true
            // first-layout race (the bail-out below still catches the
            // pre-Width-computed case).
            width = _pinBoundVm.Width;
            if (width <= 0)
            {
                // First-layout race — defer until NodeRoot.SizeChanged fires.
                return;
            }
        }
        foreach (var (sock, ele) in _pinElements)
        {
            // Prefer the measured row-centre Y from SocketRenderState (set
            // by OnSocketRowLoaded / OnSocketRowSizeChanged) over the static
            // SocketAnchor estimate so multi-line pill rows stay glued to
            // the painted centre. Falls back to SocketAnchor.Y when the row
            // hasn't been measured yet (first layout pass).
            var (_, ay) = NodeGeometry.SocketAnchor(_pinBoundVm.Model, ele.RowIndex, ele.Direction);
            double yCentre = SocketRenderState.TryGetMeasuredRowCenterY(sock.Id) ?? ay;
            // Pin chrome is 14×14 so centre-to-top-left offset is -7.
            // Inputs sit on x=0 (outer-left edge); outputs sit on x=width
            // (outer-right edge, live from NodeRoot.ActualWidth so the body
            // intrinsic-grow path stays in lockstep).
            double centreX = ele.Direction == SocketType.Input ? 0.0 : width;
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(ele.Container, centreX - 7.0);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop (ele.Container, yCentre - 7.0);
        }
    }

    // ─── Row measure-pass (Issue 2: multi-line pill drift fix) ──────────
    //
    // Each socket row's outermost Grid (declared in NodeView.xaml's
    // InputRowTemplate / OutputRowTemplate) fires these handlers on Loaded
    // and SizeChanged. We compute the row's centre Y in NodeRoot's outer-top
    // coordinate space and push it into SocketRenderState so the pin
    // overlay AND the wire-anchor math (LinkViewModel.RecomputePath) can
    // read it. NodeGeometry.RowCenterY's static SocketRowHeight=24 estimate
    // is wrong as soon as a row contains a multi-line Template / Script /
    // Payload pill — the row grows past 24 px, every subsequent row's
    // painted centre shifts, and the wire endpoints drift off.

    internal void OnSocketRowLoaded(object sender, RoutedEventArgs e)
    {
        TryMeasureSocketRow(sender);
    }

    internal void OnSocketRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TryMeasureSocketRow(sender);
    }

    private void TryMeasureSocketRow(object sender)
    {
        if (NodeRoot is null) return;
        if (sender is not FrameworkElement row) return;
        if (row.Tag is not SocketViewModel sock) return;
        try
        {
            var transform = row.TransformToVisual(NodeRoot);
            var topLeft   = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double centreY = topLeft.Y + row.ActualHeight / 2.0;
            // Hysteresis gate. OnSocketRowSizeChanged re-fires
            // repeatedly while a node body settles (multi-line pill wrap,
            // theme/zoom relayout, first realization); pre-fix each fire ran
            // an unconditional pin reposition + a link nudge, so a settling
            // wave rang the expensive downstream many times per row across all
            // ~770 rows. Bail when the measured centre hasn't moved more than
            // 0.5px from the value already stored — the same threshold
            // SocketViewModel.SetMeasuredRowCenterY uses internally — so a
            // settled-but-unchanged row stops re-triggering the reposition +
            // wire-dirty fan-out. First measurement (prevY null) always passes.
            var prevY = SocketRenderState.TryGetMeasuredRowCenterY(sock.Id);
            if (prevY is { } p && System.Math.Abs(p - centreY) < 0.5)
                return;
            // SocketAnchor adds NodeBorderInset on the y axis; the
            // TransformToVisual measurement already includes the inset, so
            // we feed centreY straight through.
            // Own breadcrumb. This path runs from WinUI layout
            // callbacks (row Loaded/SizeChanged), NOT inside OnRenderingTick's
            // "Architect.RenderTick" scope, so a stall here previously inherited
            // the stale "RenderTick.WireRecompute" latch in the watchdog log.
            // Begin a scope so a row-measure-storm stall names itself instead.
            using (Phoenix.Controls.Shared.Services.UiActivityTrace.Begin("Architect.RowMeasure"))
            {
                sock.SetMeasuredRowCenterY(centreY);
                SocketRenderState.SetMeasuredRowCenterY(sock.Id, centreY);
                // Instead of poking THIS pin's Canvas position
                // directly (which made the overlay settle row-by-row, contributing
                // to the visible double-jump), request a single coalesced
                // full-overlay reposition. The measured-Y is already pushed above,
                // so the coalesced RepositionAllPins reads correct values for every
                // row once the burst settles.
                if (_pinElements.ContainsKey(sock))
                    ScheduleCoalescedReposition();
                // Nudge wires terminating on this socket: SocketViewModel raised
                // its MeasuredRowCenterY PropertyChanged in SetMeasuredRowCenterY
                // — wires don't listen to it directly (they only see model
                // mutations), so explicitly mark every link touching this
                // socket dirty so the canvas's Rendering tick recomputes them.
                NudgeLinksForSocket(sock);
            }
        }
        catch
        {
            // Designer-time / pre-realised tree — measurement can throw
            // when the row's not in the visual tree yet. Quietly skip.
        }
    }

    private void NudgeLinksForSocket(SocketViewModel sock)
    {
        // Reach the LogicCanvasViewModel through the same
        // cached-canvas hook the pill-edit pointer paths use, then mark only
        // the wires incident on this socket dirty via the per-socket index
        // (O(incident)). Pre-fix this full-scanned the entire Links collection
        // per call, and TryMeasureSocketRow calls it once per socket-row
        // SizeChanged/Loaded — O(rows × links) per relayout wave, the dominant
        // quadratic term behind the render-tick freeze. RecomputeIfDirty runs
        // on the CompositionTarget.Rendering tick, so flipping the dirty flag
        // is all that's needed here.
        GetCanvasCached(this)?.ViewModel?.MarkLinksDirtyForSocket(sock.Id);
    }

    private static SolidColorBrush DropStateBrushFor(DropState s) => s switch
    {
        DropState.Valid   => s_dropValidBrush,
        DropState.Invalid => s_dropInvalidBrush,
        // shared transparent (was a fresh alloc/call).
        _                 => s_transparentBrush,
    };

    // 0.11.7 — pin glyph as a native shape sized at 11×11 directly. The
    // pre-0.11.7 path was: XamlReader.Load(<Path Data='…'/>) (0.10.x) →
    // direct Geometry instances inside Path inside Viewbox (0.11.6) → both
    // approaches painted nothing under Architect.WinUI's library-hosted-by-
    // Hub XAML-Islands setup because the Viewbox couldn't reliably size a
    // Path with only Data. Native shapes carry their own measure (Ellipse
    // 11×11 has DesiredSize 11×11; Polygon's DesiredSize is the union of
    // its Points; Rectangle 11×11 with CornerRadius is trivially measured)
    // so no Viewbox is needed.
    //
    // Each builder produces a Shape whose painted footprint sits inside the
    // 14×14 pinChrome Grid centred via HorizontalAlignment/VerticalAlignment.
    // StrokeThickness is constant 1.5 across kinds for visual consistency.
    private const double PinSize           = 11.0;
    private const double PinStrokeThickness = 1.5;

    private static Microsoft.UI.Xaml.Shapes.Shape BuildPinShape(SocketPinKind kind, string colorHex, string fillHex)
    {
        var stroke = HexBrush(colorHex);
        var fill   = HexBrush(fillHex);
        return kind switch
        {
            SocketPinKind.Chevron       => BuildPolygon(stroke, fill, new Point(0, 0), new Point(PinSize, PinSize / 2.0), new Point(0, PinSize)),
            SocketPinKind.Triangle      => BuildPolygon(stroke, fill, new Point(PinSize / 2.0, 0), new Point(PinSize, PinSize), new Point(0, PinSize)),
            SocketPinKind.Diamond       => BuildPolygon(stroke, fill, new Point(PinSize / 2.0, 0), new Point(PinSize, PinSize / 2.0), new Point(PinSize / 2.0, PinSize), new Point(0, PinSize / 2.0)),
            SocketPinKind.Square        => BuildPolygon(stroke, fill, new Point(0, 0), new Point(PinSize, 0), new Point(PinSize, PinSize), new Point(0, PinSize)),
            SocketPinKind.RoundedSquare => BuildRoundedSquare(stroke, fill),
            SocketPinKind.Mismatch      => BuildCircle(stroke, fill),
            _                           => BuildCircle(stroke, fill),
        };
    }

    private static Microsoft.UI.Xaml.Shapes.Ellipse BuildCircle(Brush stroke, Brush fill) => new()
    {
        Width  = PinSize,
        Height = PinSize,
        Stroke = stroke,
        Fill   = fill,
        StrokeThickness = PinStrokeThickness,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment   = VerticalAlignment.Center,
    };

    private static Microsoft.UI.Xaml.Shapes.Polygon BuildPolygon(Brush stroke, Brush fill, params Point[] points)
    {
        var poly = new Microsoft.UI.Xaml.Shapes.Polygon
        {
            Stroke = stroke,
            Fill   = fill,
            StrokeThickness = PinStrokeThickness,
            StrokeLineJoin  = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        foreach (var p in points) poly.Points.Add(p);
        return poly;
    }

    private static Microsoft.UI.Xaml.Shapes.Rectangle BuildRoundedSquare(Brush stroke, Brush fill) => new()
    {
        Width  = PinSize,
        Height = PinSize,
        RadiusX = 2,
        RadiusY = 2,
        Stroke = stroke,
        Fill   = fill,
        StrokeThickness = PinStrokeThickness,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment   = VerticalAlignment.Center,
    };

    // Cached entry point — one SolidColorBrush per
    // distinct hex string, shared across every pin. BuildHexBrush does the parse.
    private static SolidColorBrush HexBrush(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return s_transparentBrush;
        if (s_hexBrushCache.TryGetValue(hex!, out var cached)) return cached;
        var brush = BuildHexBrush(hex!);
        s_hexBrushCache[hex!] = brush;
        return brush;
    }

    private static SolidColorBrush BuildHexBrush(string? hex)
    {
        // Mirrors HexStringToBrushConverter — accepts "#RGB" / "#RRGGBB" /
        // "#AARRGGBB" / 8-char "#ARGB"-ish; falls back to transparent.
        if (string.IsNullOrEmpty(hex)) return s_transparentBrush;
        try
        {
            var s = hex!.TrimStart('#');
            byte a = 0xFF, r, g, b;
            if (s.Length == 8)
            {
                a = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                r = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                g = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                b = byte.Parse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
            }
            else if (s.Length == 6)
            {
                r = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                g = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                b = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            }
            else
            {
                return s_transparentBrush;
            }
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        catch
        {
            return s_transparentBrush;
        }
    }
}
