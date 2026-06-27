using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.UI;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// View-model for a single wire on the canvas. Resolves its endpoints from
// the backing Link + the canvas's node lookup, recomputes the bezier path
// string, and exposes the brush + dash + thickness the wire renders at.
//
// Lazy-resolved: the link doesn't cache anchors, because nodes can be
// dragged. Bind RecomputePath() into the canvas's translate handler and
// call it whenever a node at either endpoint moves.
//
// Effective-paint composition (LogicCanvasView.xaml binds these):
//   EffectiveStrokeBrush      — gold (#FFD700) when selected; type colour otherwise
//   EffectiveStrokeThickness  — 2.0 baseline; 2.75 on hover; 3.5 on selection
//   EffectiveStrokeDashArray  — empty by default; dashed when IsInvalid (type
//                               mismatch surfaced by a wildcard cascade) OR
//                               IsDangling (an endpoint socket vanished out from
//                               under the link, e.g. template rename).
//   IsFlow                    — true when the source socket is a Flow pin;
//                               LogicCanvasView paints flow wires with a
//                               directional chevron mid-path to disambiguate
//                               execution flow from data wires (task 7).
public sealed class LinkViewModel : ObservableObject
{
    private readonly Link _link;
    private readonly Graph _graph;
    private string _pathData = string.Empty;
    private string _strokeColorHex = SocketPalette.HexFor(SocketDataType.Any); //  route via SocketPalette.HexFor(Any) instead of literal #A89683
    private bool _isSelected;
    private bool _isHovered;
    private bool _isInvalid;
    private bool _isFlow;
    private SocketDataType _resolvedKind = SocketDataType.Any;

    // One-shot guard so a dangling-endpoint warning logs once per link
    // (vs. on every node-translate that rebuilds the path).
    private bool _danglingLogged;

    // Dangling-endpoint persistence — when an endpoint socket vanishes
    // (template rename, MigrateNodes back-fill), the last successfully-
    // resolved anchor lets the canvas keep rendering the wire as a dashed
    // red bezier to a "lost socket" marker at its last-known coordinates
    // (task 9). Without this the wire silently disappeared and the user
    // had no visible cue that the link was orphaned.
    private double _lastFromX, _lastFromY, _lastToX, _lastToY;
    private bool _haveLastFromAnchor, _haveLastToAnchor;
    private bool _isDangling;

    // Brush cache — composing EffectiveStrokeBrush off two cached
    // SolidColorBrushes keeps the binding allocation-free during node drags
    // (RecomputePath fires per moved frame). The selection-side brush is
    // resolved once from the theme dictionary; the type-side brush refreshes
    // when the resolved socket type changes.
    private static SolidColorBrush? s_selectionBrushCached;
    private SolidColorBrush _typeBrush = HexBrush(SocketPalette.HexFor(SocketDataType.Any)); //  route via SocketPalette.HexFor(Any) instead of literal 0xA89683

    // Hover brush cache — the +20% brightness lift used to live inline in
    // the EffectiveStrokeBrush getter, allocating a fresh SolidColorBrush
    // every binding read (the canvas's HoveredLink toggle fires the getter
    // multiple times per hover). Caching the brush + invalidating on the
    // narrow set of inputs (hover entered, type colour changed) keeps the
    // hot path allocation-free. Null = needs rebuild.
    private SolidColorBrush? _hoverBrushCached;

    // 0.10.0 (arch-perf P1) — dirty flag for the frame-coalesced wire-recompute
    // pipeline. Pointer-move handlers that translate nodes call MarkPathDirty
    // instead of RecomputePath; the canvas's CompositionTarget.Rendering tick
    // calls RecomputeIfDirty exactly once per frame so a pointer burst at
    // ~120 Hz collapses into one bezier rebuild per displayed frame.
    private bool _pathDirty;

    // [wire-virt] Viewport-cull flag. False when this wire's bounding box is
    // entirely outside the canvas cull band — the LinkLayer item Visibility binds
    // to this so an off-screen wire collapses (skipped by WinUI's measure/arrange/
    // render walk). Default true; the canvas settle-pass cull
    // (LogicCanvasView.Lod.RefreshLinkRealization) drives it, and the kill-switch
    // (Ctrl+Alt+F6) forces every link visible. The bezier PathData still recomputes
    // while collapsed (cheap math, time-sliced) so the wire is correct the instant
    // it re-enters the band.
    private bool _isViewportVisible = true;

    public LinkViewModel(Link link, Graph graph)
    {
        _link = link;
        _graph = graph;
        RecomputePath();
    }

    public Link Model => _link;

    public string Id => _link.Id;

    /// <summary>SVG-style path data string ready for &lt;Path Data=...&gt; binding.</summary>
    public string PathData
    {
        get => _pathData;
        private set => SetField(ref _pathData, value);
    }

    /// <summary>
    /// [wire-virt] False when the wire's bounding box is entirely outside the
    /// canvas cull band, so the bound LinkLayer item collapses and WinUI skips
    /// measuring / arranging / rendering its two Paths. Default true; driven by
    /// the canvas settle-pass cull. Forced true when node virtualization is off.
    /// </summary>
    public bool IsViewportVisible
    {
        get => _isViewportVisible;
        set => SetField(ref _isViewportVisible, value);
    }

    /// <summary>
    /// Stroke colour hex resolved against the type of the wire's source
    /// socket. The canvas binds to <see cref="EffectiveStrokeBrush"/>
    /// (NOT this) so selection / invalid / dangling paint can override
    /// without RecomputePath stomping it on the next node drag.
    /// </summary>
    public string StrokeColorHex
    {
        get => _strokeColorHex;
        private set
        {
            if (SetField(ref _strokeColorHex, value))
            {
                _typeBrush = HexBrush(value);
                // Type colour drives the hover brush — invalidate so the
                // next EffectiveStrokeBrush read rebuilds against the new
                // type colour instead of returning a stale hover brush.
                _hoverBrushCached = null;
                OnPropertyChanged(nameof(EffectiveStrokeBrush));
            }
        }
    }

    /// <summary>The SocketDataType inherited from the source socket — useful for filtering / inspector.</summary>
    public SocketDataType Kind => _resolvedKind;

    /// <summary>
    /// True when the source socket is a Flow pin. The canvas uses this flag
    /// to paint a small directional chevron mid-path on flow wires so
    /// execution flow reads visually distinct from data wires. Set inside
    /// RecomputePath from the resolved source socket type.
    /// </summary>
    public bool IsFlow
    {
        get => _isFlow;
        private set
        {
            if (SetField(ref _isFlow, value))
            {
                // EffectiveStrokeBrush and EffectiveStrokeThickness both
                // branch on IsFlow (neutral grey + 2.5 px per §5.3), so a
                // flow-flag flip must repaint the wire.
                OnPropertyChanged(nameof(EffectiveStrokeBrush));
                OnPropertyChanged(nameof(EffectiveStrokeThickness));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(EffectiveStrokeBrush));
                OnPropertyChanged(nameof(EffectiveStrokeThickness));
            }
        }
    }

    /// <summary>
    /// True while the cursor is inside the wire's 12px transparent hit-zone
    /// path. LogicCanvasView toggles this on its hover detection so the
    /// stroke bumps to <see cref="HoveredStrokeThickness"/> + a brightness
    /// nudge, mirroring UE Blueprints' hover affordance. Selection wins over
    /// hover so a selected wire stays at the selection thickness even when
    /// the cursor leaves the hit-zone.
    /// </summary>
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (SetField(ref _isHovered, value))
            {
                // No cache reset needed — the hover brush lazily builds on
                // first hover and stays valid until the type colour changes
                // (StrokeColorHex setter nulls _hoverBrushCached). When the
                // pointer leaves, the getter just routes back to _typeBrush.
                OnPropertyChanged(nameof(EffectiveStrokeBrush));
                OnPropertyChanged(nameof(EffectiveStrokeThickness));
            }
        }
    }

    /// <summary>
    /// True when the wire's endpoints have types the registry's compatibility
    /// rules reject (e.g. a wildcard cascade resolved one end into a type the
    /// other end can no longer accept). The canvas paints the wire dashed in
    /// the error colour so the regression is visible at a glance instead of
    /// silently producing a script that will fail at runtime.
    /// </summary>
    public bool IsInvalid
    {
        get => _isInvalid;
        private set
        {
            if (SetField(ref _isInvalid, value))
            {
                OnPropertyChanged(nameof(EffectiveStrokeBrush));
                OnPropertyChanged(nameof(EffectiveStrokeDashArray));
            }
        }
    }

    /// <summary>
    /// True when either endpoint failed to resolve in the graph and the wire
    /// is being rendered against its last-known coords. Surfaces an obvious
    /// dashed-red bezier (vs. the older silently-vanish behaviour) so the
    /// user can spot a corrupt or out-of-date .phxg in the canvas itself
    /// rather than only in the System Log.
    /// </summary>
    public bool IsDangling
    {
        get => _isDangling;
        private set
        {
            if (SetField(ref _isDangling, value))
            {
                OnPropertyChanged(nameof(EffectiveStrokeBrush));
                OnPropertyChanged(nameof(EffectiveStrokeDashArray));
            }
        }
    }

    /// <summary>
    /// The brush the canvas actually paints. Priority order:
    ///   selected → gold (SelectionBrush, #FFD700, per Design_Orders)
    ///   invalid / dangling → ErrBrush (rust red)
    ///   flow → neutral grey (200,200,200) per Design_Orders §5.3
    ///   hovered → type colour with ~20% brightness lift
    ///   default → type colour
    /// Composed at the getter (not the setter) so node drags + wildcard
    /// cascades that re-snapshot StrokeColorHex don't stomp the selection
    /// paint. Flow wires intentionally bypass the SocketPalette colour
    /// so execution flow reads visually distinct from typed data wires —
    /// the spec calls a neutral grey here, not the coal-paper Flow socket
    /// hue. The 20 % hover lift still applies (lifted from the grey base).
    /// </summary>
    public Brush EffectiveStrokeBrush
    {
        get
        {
            if (_isSelected) return SelectionBrush();
            if (_isInvalid || _isDangling) return ErrorBrush();
            if (_isFlow)
            {
                if (_isHovered)
                {
                    if (_flowHoverBrushCached is null)
                    {
                        var c = s_flowBrush.Color;
                        byte r = (byte)System.Math.Min(255, (int)(c.R * 1.20));
                        byte g = (byte)System.Math.Min(255, (int)(c.G * 1.20));
                        byte b = (byte)System.Math.Min(255, (int)(c.B * 1.20));
                        _flowHoverBrushCached = new SolidColorBrush(Color.FromArgb(c.A, r, g, b));
                    }
                    return _flowHoverBrushCached;
                }
                return s_flowBrush;
            }
            if (_isHovered)
            {
                // 20% brightness lift for the hover state. Cheap channel
                // scale — keeps the type identity readable while making
                // the active wire pop. Cached: the canvas's HoveredLink
                // toggle fires this getter multiple times per hover, and
                // pre-cache every read allocated a fresh SolidColorBrush.
                // Invalidated on hover-toggle and on type-colour change
                // (the only inputs feeding this lift).
                if (_hoverBrushCached is null)
                {
                    var c = _typeBrush.Color;
                    byte r = (byte)System.Math.Min(255, (int)(c.R * 1.20));
                    byte g = (byte)System.Math.Min(255, (int)(c.G * 1.20));
                    byte b = (byte)System.Math.Min(255, (int)(c.B * 1.20));
                    _hoverBrushCached = new SolidColorBrush(Color.FromArgb(c.A, r, g, b));
                }
                return _hoverBrushCached;
            }
            return _typeBrush;
        }
    }

    // 0.10.8 readability sweep #4 — flow wires restored to the pre-WinUI
    // white (matching the white Flow chevron pin). The earlier "Design_Orders
    // §5.3 — execution wires are neutral grey (200,200,200), not the
    // SocketPalette Flow socket colour" decision diverged from pre-T15
    // behaviour; Majo's 0.10.6 audit called the wire-coloring fork as one
    // of the items to mirror back to the pre-WinUI palette. Now the flow
    // wire's stroke shares its colour with the socket it springs from,
    // making the visual identity "flow wires look like flow sockets stretched
    // out into pipes" — same convention typed-data wires already follow.
    private static readonly SolidColorBrush s_flowBrush =
        new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private SolidColorBrush? _flowHoverBrushCached;

    /// <summary>
    /// Dash pattern applied to the visible wire stroke. Empty for a normal
    /// solid wire; non-empty (4,2) when <see cref="IsInvalid"/> or
    /// <see cref="IsDangling"/> so the broken state is unambiguous.
    /// WinUI's <see cref="Microsoft.UI.Xaml.Shapes.Shape.StrokeDashArray"/>
    /// is the standard <see cref="DoubleCollection"/> binding target.
    /// Both collections are static singletons so every binding read shares
    /// the same instance — DoubleCollection is sharable across StrokeDashArray
    /// consumers, so this is safe and saves a per-read allocation that
    /// pre-fix fired on every wire bind / invalidation tick.
    /// </summary>
    public DoubleCollection EffectiveStrokeDashArray
        => (_isInvalid || _isDangling) ? s_dashedStroke : s_solidStroke;

    private static readonly DoubleCollection s_dashedStroke = new() { 4, 2 };
    private static readonly DoubleCollection s_solidStroke  = new();

    /// <summary>
    /// Stroke thickness the canvas actually paints. Selection wins over
    /// hover so a clicked-then-hovered wire renders at the selected weight,
    /// not a mid-state. Hovered wires get a small bump (2.75 px) for the
    /// UE-Blueprints "I can click this" affordance; selected wires get the
    /// stronger 3.5 px bump so the gold tint alone isn't the only signal.
    /// </summary>
    public double EffectiveStrokeThickness
    {
        get
        {
            if (_isSelected) return SelectedStrokeThickness;
            if (_isHovered)  return HoveredStrokeThickness;
            // Flow wires render at 2.5 px per Design_Orders §5.3 so execution
            // flow reads visually heavier than typed data wires (2.0 px).
            if (_isFlow)     return FlowStrokeThickness;
            return DefaultStrokeThickness;
        }
    }

    public const double DefaultStrokeThickness  = 2.0;
    public const double FlowStrokeThickness     = 2.5;
    public const double HoveredStrokeThickness  = 2.75;
    public const double SelectedStrokeThickness = 3.5;

    /// <summary>
    /// Last successfully-resolved source-anchor (canvas-space). Used by
    /// LogicCanvasView to paint a "lost socket" marker on a dangling wire
    /// at the coordinates the source socket last lived at.
    /// </summary>
    public (double X, double Y)? LastFromAnchor
        => _haveLastFromAnchor ? (_lastFromX, _lastFromY) : null;

    /// <summary>Last successfully-resolved target-anchor counterpart.</summary>
    public (double X, double Y)? LastToAnchor
        => _haveLastToAnchor ? (_lastToX, _lastToY) : null;

    /// <summary>
    /// 0.10.0 (arch-perf P1) — flag this wire for recompute on the next
    /// CompositionTarget.Rendering tick. Cheap; safe to call multiple times
    /// per frame from pointer-move handlers. The actual bezier rebuild
    /// happens in <see cref="RecomputeIfDirty"/> at frame-render cadence
    /// instead of per pointer event.
    /// </summary>
    public void MarkPathDirty() => _pathDirty = true;

    /// <summary>
    /// 0.10.0 (arch-perf P1) — paired with <see cref="MarkPathDirty"/>. The
    /// canvas's CompositionTarget.Rendering tick walks every link and calls
    /// this; clean links no-op so the per-frame cost is bounded by O(L)
    /// regardless of how many node moves have been queued.
    /// </summary>
    /// <returns>
    /// true if this link was dirty and got recomputed; false if it was clean
    /// (a bool no-op).  The canvas's render-tick drain uses the
    /// return to count actual recomputes so it can time-slice the pass —
    /// clean links cost nothing toward the per-tick budget.
    /// </returns>
    public bool RecomputeIfDirty()
    {
        if (!_pathDirty) return false;
        _pathDirty = false;
        RecomputePath();
        return true;
    }

    /// <summary>
    /// Re-resolve endpoints from the graph and rebuild the bezier path
    /// string. Cheap (two dictionary lookups + a few math ops) — call
    /// from any node-translate handler. If either endpoint is missing
    /// (e.g. a dangling link from an in-progress edit), we fall back to
    /// the last-known anchor coordinates so the wire renders as a dashed
    /// red bezier to a "lost socket" marker rather than silently vanishing.
    /// </summary>
    public void RecomputePath()
    {
        // Clear the dirty flag here too so a direct caller (constructor,
        // wildcard cascade, test) doesn't leave the flag set and cause a
        // duplicate rebuild on the next Rendering tick.
        _pathDirty = false;
        var fromNode   = _graph.FindNodeById(_link.FromNodeId);
        var toNode     = _graph.FindNodeById(_link.ToNodeId);
        var fromSocket = _graph.FindSocketById(_link.FromSocketId);
        var toSocket   = _graph.FindSocketById(_link.ToSocketId);

        bool fromResolved = fromNode is not null && fromSocket is not null;
        bool toResolved   = toNode   is not null && toSocket   is not null;

        // Compute current source/target anchors when each end resolves; fall
        // back to the last-known anchor when not. Persist successful anchors
        // so the next call (after a real disappearance) has coordinates to
        // render against.
        double fx, fy, tx, ty;
        if (fromResolved)
        {
            int fromRow = SocketRowIndex(fromNode!, fromSocket!);
            var (afx, afy) = NodeGeometry.SocketAnchor(fromNode!, fromRow, fromSocket!.Type);
            // 0.11.5 canvas-polish r3 — prefer the NodeView-measured row-
            // centre Y over NodeGeometry.RowCenterY's static estimate when
            // available. Fixes multi-line pill drift (Template/Script/
            // Payload pills that grow the row past SocketRowHeight=24).
            double? measuredFy = SocketRenderState.TryGetMeasuredRowCenterY(fromSocket.Id);
            fx = afx + fromNode!.Location.X;
            fy = (measuredFy ?? afy) + fromNode.Location.Y;
            _lastFromX = fx; _lastFromY = fy; _haveLastFromAnchor = true;
        }
        else if (_haveLastFromAnchor)
        {
            fx = _lastFromX; fy = _lastFromY;
        }
        else
        {
            // First resolve attempt and the source is already gone — log
            // once and bail so PathData stays empty rather than rendering
            // a garbage line through (0,0).
            LogDanglingOnce();
            IsDangling = true;
            return;
        }

        if (toResolved)
        {
            int toRow = SocketRowIndex(toNode!, toSocket!);
            var (atx, aty) = NodeGeometry.SocketAnchor(toNode!, toRow, toSocket!.Type);
            double? measuredTy = SocketRenderState.TryGetMeasuredRowCenterY(toSocket.Id);
            tx = atx + toNode!.Location.X;
            ty = (measuredTy ?? aty) + toNode.Location.Y;
            _lastToX = tx; _lastToY = ty; _haveLastToAnchor = true;
        }
        else if (_haveLastToAnchor)
        {
            tx = _lastToX; ty = _lastToY;
        }
        else
        {
            LogDanglingOnce();
            IsDangling = true;
            return;
        }

        PathData = BezierPath.Build(fx, fy, tx, ty);

        // Resolve type-derived presentation. When the source socket is gone
        // we keep the prior cached values so the dangling wire still has
        // a colour assignment to render against.
        if (fromResolved)
        {
            _resolvedKind = fromSocket!.DataType;
            StrokeColorHex = SocketPalette.HexFor(_resolvedKind);
            IsFlow = SocketTypeHelper.IsFlowPin(fromSocket);
            OnPropertyChanged(nameof(Kind));
        }

        // Mark dangling state.
        bool nowDangling = !fromResolved || !toResolved;
        if (nowDangling) LogDanglingOnce();
        else
        {
            //  Both endpoints resolved — reset the once-per-link
            // dangling-log guard so a future re-dangle (template re-add then
            // re-delete, undo of a delete then re-delete) re-logs once
            // instead of staying silent for the rest of the session.
            _danglingLogged = false;
        }
        IsDangling = nowDangling;

        // Type-compatibility check — even with both ends resolved, a
        // wildcard cascade can leave the source and target on incompatible
        // types (e.g. one side resolved to Int, the other resolved to
        // String through a different chain). Surface that as IsInvalid so
        // the wire renders dashed red until the user fixes it.
        if (fromResolved && toResolved)
        {
            var (src, dst) = fromSocket!.Type == SocketType.Output
                ? (fromSocket, toSocket!)
                : (toSocket!, fromSocket);
            IsInvalid = !NodeRegistry.AreCompatible(src.DataType, dst.DataType);
        }
        else
        {
            // Treat a dangling link as invalid so its dashed-red treatment
            // doesn't depend on IsInvalid alone — IsDangling drives the
            // same brush + dash via EffectiveStrokeBrush /
            // EffectiveStrokeDashArray, so this stays a no-op here.
            IsInvalid = false;
        }
    }

    private void LogDanglingOnce()
    {
        if (_danglingLogged) return;
        _danglingLogged = true;
        string detail =
            $"from={_link.FromNodeId}/{_link.FromSocketId} " +
            $"to={_link.ToNodeId}/{_link.ToSocketId}";
        GlobalLogger.Log(
            $"Link skipped — endpoint(s) missing in graph: {detail}",
            "Architect.LinkViewModel",
            LogLevel.System);
    }

    private static int SocketRowIndex(Node node, Socket socket)
    {
        // Same logic NodeViewModel uses to assign rows — keep them in lockstep.
        int row = 0;
        foreach (var s in node.Sockets)
        {
            if (s.Type != socket.Type) continue;
            if (s.Id == socket.Id) return row;
            row++;
        }
        return 0;
    }

    //  Drop the shared selection-brush cache so it
    // re-resolves from the (theme-/HC-current) resource dictionary on the next
    // EffectiveStrokeBrush read. Called from the node-view theme-change handler.
    internal static void InvalidateThemeBrushCache() => s_selectionBrushCached = null;

    /// <summary>
    ///  Repaint this wire's stroke in the current
    /// theme: drop the per-instance hover caches and re-fire EffectiveStrokeBrush
    /// so a selected / hovered wire picks up the refreshed theme brushes. The
    /// canvas calls this for every link on a runtime theme / high-contrast switch.
    /// </summary>
    public void RefreshThemeBrushes()
    {
        _hoverBrushCached     = null;
        _flowHoverBrushCached = null;
        OnPropertyChanged(nameof(EffectiveStrokeBrush));
    }

    private static SolidColorBrush SelectionBrush()
    {
        // Cached resource lookup — every selection toggle would otherwise
        // round-trip through Application.Current.Resources. The brush is a
        // SolidColorBrush in the theme dictionary so reference-sharing is fine.
        if (s_selectionBrushCached is not null) return s_selectionBrushCached;
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue("SelectionBrush", out var found)
                && found is SolidColorBrush b)
            {
                s_selectionBrushCached = b;
                return b;
            }
        }
        catch { /* designer / pre-app */ }
        s_selectionBrushCached = new SolidColorBrush(ArchitectCanvasPalette.SelectionGold); //  route via ArchitectCanvasPalette.SelectionGold instead of literal 0xFFD700
        return s_selectionBrushCached;
    }

    private static SolidColorBrush ErrorBrush()
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue("ErrBrush", out var found)
                && found is SolidColorBrush b)
                return b;
        }
        catch { /* designer / pre-app */ }
        return new SolidColorBrush(ArchitectCanvasPalette.ErrRustRed); //  route via ArchitectCanvasPalette.ErrRustRed instead of literal 0xCB4D3F
    }

    private static SolidColorBrush HexBrush(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#')
            return new SolidColorBrush(Color.FromArgb(0xFF, 0xA8, 0x96, 0x83));
        try
        {
            string s = hex.Substring(1);
            byte a = 0xFF, r, g, b;
            if (s.Length == 8)
            {
                a = System.Convert.ToByte(s.Substring(0, 2), 16);
                r = System.Convert.ToByte(s.Substring(2, 2), 16);
                g = System.Convert.ToByte(s.Substring(4, 2), 16);
                b = System.Convert.ToByte(s.Substring(6, 2), 16);
            }
            else if (s.Length == 6)
            {
                r = System.Convert.ToByte(s.Substring(0, 2), 16);
                g = System.Convert.ToByte(s.Substring(2, 2), 16);
                b = System.Convert.ToByte(s.Substring(4, 2), 16);
            }
            else return new SolidColorBrush(Color.FromArgb(0xFF, 0xA8, 0x96, 0x83));
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        catch { return new SolidColorBrush(Color.FromArgb(0xFF, 0xA8, 0x96, 0x83)); }
    }
}
