using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Services;

using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// ─── [Tranche-2b] Staged Level-of-Detail (LOD) node realization ─────────────
//
// WHY THIS EXISTS — the viewport node-cull (Tranche-2, RefreshNodeCull) only
// unmounts OFF-SCREEN nodes. The dominant freeze, proven from Majo's own
// 2026-06-12 capture, is the opposite case: loading a large graph and ZOOMING
// OUT to see the whole thing. Then every node is on-screen, the cull mounts all
// of them (the log line "206/206 nodes mounted"), and WinUI's synchronous
// measure/arrange/render walk over ~10-22k realized UIElements freezes the UI
// thread for seconds (a 71.8s pass was captured pre-fix). Culling cannot help
// when everything is visible.
//
// THE FIX — give each node a cheap representation when it is small on screen:
//
//   • None  — off the cull band            → nothing in the tree (the old cull).
//   • Proxy — on-screen but zoomed out      → ONE lightweight Border (header-
//             colour block; optional title TextBlock above the label threshold).
//             ~1-2 UIElements vs the full NodeView's ~48-149.
//   • Full  — on-screen and zoomed in       → the real NodeView (lazily built).
//
// A 206-node graph viewed zoomed-out is now ~206-412 elements (proxies) instead
// of ~22,000 — the framework walk drops from "permanent freeze" to nothing. At
// "thousands" the far-zoom proxies are bare boxes (no text measure), so it
// stays cheap. Full NodeViews are built LAZILY (only when a node is first
// realized Full) — a graph you only ever view zoomed-out never pays the
// per-node construction cost at all, which the prior cull did NOT avoid (it
// `new`'d a NodeView for every node up front).
//
// All of this is gated behind _enableNodeVirtualization (Ctrl+Alt+F6 kill-
// switch). OFF == the legacy "every node is a full NodeView, always mounted"
// path, byte-identical to pre-Tranche-2 behaviour.
public sealed partial class LogicCanvasView
{
    // Proxy element per node (lightweight Border). Lazily created, kept alive on
    // cull/regime transitions (same keep-alive policy as _nodeViews) so a
    // zoom-in/out cycle reuses the object instead of rebuilding it.
    private readonly Dictionary<NodeViewModel, Border> _nodeProxies = new();
    // Subset of _nodeProxies currently mounted in NodeLayer.Children.
    private readonly HashSet<NodeViewModel> _realizedProxy = new();
    // Every node whose PropertyChanged we've hooked (X/Y reposition + proxy
    // selection). Tracked independently of _nodeViews because a node can live as
    // a proxy and never get a full NodeView — so the old "iterate _nodeViews to
    // unsubscribe" path would leak proxy-only nodes' subscriptions.
    private readonly HashSet<NodeViewModel> _subscribedNodes = new();

    // LOD zoom thresholds. Hysteresis (Enter > Exit) stops a zoom hovering at the
    // boundary from thrashing the whole graph proxy<->full every settle. Below
    // the label threshold a proxy is a bare box (no TextBlock = no text measure),
    // which is what keeps "thousands of nodes" cheap on a zoomed-out load.
    // 2026-06-21 (Majo: "LOD enables way too early") — thresholds lowered so full,
    // readable NodeViews persist much further into a zoom-out. Proxies now only
    // engage below 0.25 (heavy zoom-out / whole-graph-fit), where node text is
    // unreadable anyway, so the normal editing range (~0.3–1.0) always shows full
    // nodes. The zoomed-OUT-to-see-everything case stays cheap because fit-all zoom
    // on a large graph sits well below 0.25; only the early proxy swap at moderate
    // zoom is removed. One-line tunable if it's still too early or refreezes on fit-all.
    private const double LodFullEnter = 0.33;   // zoom >= this  → full NodeViews
    private const double LodFullExit  = 0.25;   // zoom <  this  → proxies
    private const double LodLabelZoom = 0.16;   // proxies show their title at/above this zoom
    private bool _lodFullRegime = true;         // start "full" so a fresh / zoomed-in canvas behaves like before
    private bool _lodLabelOn    = true;         // whether proxies currently carry a title

    private enum NodeReal { None, Proxy, Full }

    // ── Static proxy brushes (resolved once from the theme, fallback-coded).
    // Per-pillar paint per feedback_visualist_architect_chrome_independence; these
    // mirror NodeView's brush keys so a proxy reads as the same node zoomed out.
    // Mutable (not readonly) so a runtime OS theme / high-contrast switch can
    // re-resolve them — RefreshProxyThemeBrushes(), wired from the canvas'
    // ActualThemeChanged handler, mirrors NodeView's own brush refresh so a
    // zoomed-out proxy doesn't keep stale-theme colours while full nodes repaint.
    private static SolidColorBrush s_proxySelectionBrush =
        ResolveLodBrush("EmberPrimaryBrush", 0xFF, 0xE5, 0xA2, 0x4E);
    private static SolidColorBrush s_proxyDividerBrush =
        ResolveLodBrush("CoalDividerBrush", 0xFF, 0x3A, 0x31, 0x27);
    private static SolidColorBrush s_proxyFallbackBg =
        ResolveLodBrush("CoalRaisedBrush", 0xFF, 0x2A, 0x26, 0x22);

    // [theme] Re-resolve the proxy brushes after a runtime theme / high-contrast
    // change and repaint the mounted proxies. Called from OnCanvasActualThemeChanged
    // (LogicCanvasView.xaml.cs) alongside the per-link / per-NodeView refresh, so
    // every realized representation flips theme together. Cheap + infrequent.
    private void RefreshProxyThemeBrushes()
    {
        s_proxySelectionBrush = ResolveLodBrush("EmberPrimaryBrush", 0xFF, 0xE5, 0xA2, 0x4E);
        s_proxyDividerBrush   = ResolveLodBrush("CoalDividerBrush", 0xFF, 0x3A, 0x31, 0x27);
        s_proxyFallbackBg     = ResolveLodBrush("CoalRaisedBrush", 0xFF, 0x2A, 0x26, 0x22);
        foreach (var vm in _realizedProxy)
            if (_nodeProxies.TryGetValue(vm, out var proxy))
                ConfigureProxy(proxy, vm);   // repaint bg / border / selection with the new brushes
    }

    private static SolidColorBrush ResolveLodBrush(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var v) && v is SolidColorBrush scb)
                return scb;
        }
        catch { /* theme not ready — fall through to the literal */ }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
    }

    // Hysteresis-gated zoom regime. Returns true when the regime OR the label
    // band changed, i.e. the realization pass has work even if the viewport rect
    // did not leave the cull band (a pure zoom-in-place across a threshold).
    private bool UpdateLodRegime()
    {
        double zoom = _vm?.Zoom ?? 1.0;
        bool changed = false;
        if (_lodFullRegime && zoom < LodFullExit)        { _lodFullRegime = false; changed = true; }
        else if (!_lodFullRegime && zoom >= LodFullEnter) { _lodFullRegime = true;  changed = true; }
        bool labelOn = zoom >= LodLabelZoom;
        if (labelOn != _lodLabelOn) { _lodLabelOn = labelOn; changed = true; }
        return changed;
    }

    // A node's X/Y changed (drag / undo / paste / programmatic move). Schedule a
    // realization pass on the next settle so a node moved ON-screen from an
    // off-screen (None) state gets mounted — and one moved off-screen gets culled.
    // Resetting the settle counter (same idiom as ApplyViewTransform for pan/zoom)
    // defers the pass until the move STOPS, so a continuous group-drag never culls
    // mid-gesture. No-op when virtualization is off (legacy mounts everything).
    private void MarkNodeMovedForCull()
    {
        if (!_enableNodeVirtualization) return;
        _cullDirty = true;
        _cullSettleFrames = 0;
    }

    private NodeReal ComputeNodeReal(NodeViewModel vm)
    {
        if (!_enableNodeVirtualization) return NodeReal.Full;     // kill-switch → legacy mount-all
        if (!NodeIntersectsCullBand(vm)) return NodeReal.None;    // off-screen → nothing
        return _lodFullRegime ? NodeReal.Full : NodeReal.Proxy;
    }

    // Realize a single node at the current LOD. Used by AddNodeView (single
    // spawn / bulk load) for instant display without waiting for a settle tick.
    private void RealizeNode(NodeViewModel vm) => ApplyNodeReal(vm, ComputeNodeReal(vm));

    // Transition a node from whatever it currently is to the target
    // representation. Idempotent — mount/unmount helpers no-op when already in
    // the requested state, so re-running the pass costs only dict lookups.
    private void ApplyNodeReal(NodeViewModel vm, NodeReal target)
    {
        switch (target)
        {
            case NodeReal.Full:
                if (_realizedProxy.Contains(vm)) UnmountProxy(vm);
                MountNodeView(vm, EnsureFullView(vm));
                break;

            case NodeReal.Proxy:
                if (_realizedNodes.Contains(vm) && _nodeViews.TryGetValue(vm, out var fvP))
                    CullUnmountNodeView(vm, fvP);
                MountProxy(vm);
                break;

            case NodeReal.None:
                if (_realizedNodes.Contains(vm) && _nodeViews.TryGetValue(vm, out var fvN))
                    CullUnmountNodeView(vm, fvN);
                if (_realizedProxy.Contains(vm)) UnmountProxy(vm);
                break;
        }
    }

    // Lazily construct the full NodeView. THIS is the load-time win: a node that
    // is only ever a proxy never gets its ~48-149-element tree built.
    private NodeView EnsureFullView(NodeViewModel vm)
    {
        if (_nodeViews.TryGetValue(vm, out var view)) return view;
        view = new NodeView { DataContext = vm };
        _nodeViews[vm] = view;
        return view;
    }

    private void MountProxy(NodeViewModel vm)
    {
        var proxy = EnsureProxy(vm);
        ConfigureProxy(proxy, vm);              // refresh size / label / selection on every (re)realize
        if (_realizedProxy.Add(vm))
        {
            XamlCanvas.SetLeft(proxy, vm.X);
            XamlCanvas.SetTop (proxy, vm.Y);
            NodeLayer.Children.Add(proxy);
        }
    }

    private void UnmountProxy(NodeViewModel vm)
    {
        if (!_realizedProxy.Remove(vm)) return;
        if (_nodeProxies.TryGetValue(vm, out var proxy))
            NodeLayer.Children.Remove(proxy);
    }

    private Border EnsureProxy(NodeViewModel vm)
    {
        if (_nodeProxies.TryGetValue(vm, out var b)) return b;
        b = new Border
        {
            CornerRadius    = new CornerRadius(6),
            UseLayoutRounding = true,
            // Tag = the NodeViewModel so LogicCanvasView.Pointer's HitTagFrom
            // resolves a proxy click to the node → select / drag / group-drag
            // parity with the full NodeView, zoomed out.
            Tag             = vm,
            IsHitTestVisible = true,
        };
        _nodeProxies[vm] = b;
        return b;
    }

    // Paint the proxy from the model: header-colour body, size, selection ring,
    // and the title (only when zoomed in enough to read it — below LodLabelZoom
    // the proxy is a bare box with NO TextBlock, so a thousands-of-nodes
    // zoomed-out load measures zero text).
    private void ConfigureProxy(Border proxy, NodeViewModel vm)
    {
        proxy.Background = vm.HeaderGradientBrush ?? s_proxyFallbackBg;
        proxy.Width  = vm.Width  > 0 ? vm.Width  : 160.0;
        proxy.Height = vm.Height > 0 ? vm.Height : 60.0;
        ApplyProxySelection(proxy, vm);

        if (_lodLabelOn)
        {
            if (proxy.Child is not TextBlock tb)
            {
                tb = new TextBlock
                {
                    FontFamily   = ResolveLodFont(),
                    FontSize     = 13,
                    FontWeight   = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground   = ResolveLodBrush("CoalPaperBrush", 0xFF, 0xEA, 0xE2, 0xD6),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    Margin       = new Thickness(10, 6, 10, 6),
                    VerticalAlignment   = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsHitTestVisible    = false,
                };
                proxy.Child = tb;
            }
            if (!string.Equals(tb.Text, vm.Title, System.StringComparison.Ordinal))
                tb.Text = vm.Title;
        }
        else if (proxy.Child is not null)
        {
            proxy.Child = null;   // drop the TextBlock → no text measure at far zoom
        }
    }

    private static void ApplyProxySelection(Border proxy, NodeViewModel vm)
    {
        proxy.BorderBrush     = vm.IsSelected ? s_proxySelectionBrush : s_proxyDividerBrush;
        proxy.BorderThickness = new Thickness(vm.IsSelected ? 2 : 1);
    }

    private static FontFamily ResolveLodFont()
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue("SansFont", out var v) && v is FontFamily ff)
                return ff;
        }
        catch { /* fall through */ }
        return new FontFamily("Segoe UI");
    }

    // The realization pass — replaces RefreshNodeCull. Runs on a SETTLE tick
    // (never mid-gesture) when virtualization marked the viewport / a node move
    // dirty. Refreshes the cull band + LOD regime, then diffs EVERY node's current
    // representation against the target for the settled viewport + zoom.
    //
    // No early-return on "band+regime unchanged": a node moved on-screen (undo /
    // paste / select-all drag) marks _cullDirty without changing the viewport, and
    // a delayed first-measure can leave the fallback band looking "unchanged" —
    // both must still re-realize. The full pass is O(N) cheap arithmetic
    // (rect-test + dict lookups); ApplyNodeReal is idempotent, so nodes already in
    // the right state cost nothing. The expensive element work happens ONLY for
    // nodes that actually change representation. (The audit's cost is element
    // realization, never an N-iteration arithmetic loop, so always iterating is
    // safe even at thousands of nodes.)
    private void RefreshNodeRealization()
    {
        // [perf/win2d-immediate-canvas] immediate mode renders nodes on the GPU
        // canvas — skip the retained cull/LOD realization entirely.
        if (_vm is null || !_enableNodeVirtualization || _useImmediateMode) return;
        UpdateCullBand();    // refresh the hysteresis band if the viewport moved
        UpdateLodRegime();   // refresh the proxy/full regime + label state
        using (UiActivityTrace.Begin("Architect.NodeRealize"))
        {
            foreach (var vm in _vm.Nodes)
                ApplyNodeReal(vm, ComputeNodeReal(vm));
        }
        RefreshLinkRealization();   // [wire-virt] cull off-screen wires on the same settle
    }

    // Force every node to the representation correct for the current viewport +
    // zoom, ignoring the band/regime "changed" short-circuit. Used on the
    // kill-switch toggle and any path that must reconcile from scratch.
    private void ForceFullRealization()
    {
        if (_vm is null) return;
        UpdateCullBand();
        UpdateLodRegime();
        foreach (var vm in _vm.Nodes)
            ApplyNodeReal(vm, ComputeNodeReal(vm));
        RefreshLinkRealization();   // [wire-virt]
    }

    // ─── [wire-virt] Wire-layer virtualization ──────────────────────────────
    //
    // The LinkLayer is a non-virtualizing ItemsControl, so every wire's two Paths
    // stay realized regardless of zoom/pan — a constant tax on every layout/render
    // walk and a per-frame cost during a frame/group drag. This collapses off-band
    // wires via the LinkViewModel.IsViewportVisible binding so WinUI skips their
    // measure/arrange/render entirely; the bezier PathData still recomputes (cheap,
    // time-sliced) so a wire is correct the instant it scrolls back into view.
    //
    // Runs on the SAME settle tick as the node cull (called from
    // RefreshNodeRealization), reusing the freshly-updated _cullBand. O(L) cheap
    // arithmetic; SetField no-ops when a wire's visibility is unchanged, so a
    // settled graph costs nothing.
    private void RefreshLinkRealization()
    {
        if (_vm is null || !_enableNodeVirtualization) return;
        foreach (var lvm in _vm.Links)
            lvm.IsViewportVisible = LinkIntersectsCullBand(lvm);
    }

    // Model-based bbox test: the wire spans its two last-resolved anchors, inflated
    // by a generous margin so a backward / bulging bezier whose control points
    // overshoot the endpoint box isn't culled at the band edge. Errs toward visible;
    // a dangling / not-yet-resolved wire (null anchor) always stays visible.
    private bool LinkIntersectsCullBand(LinkViewModel lvm)
    {
        if (_cullBand.Width <= 0) return true;            // no band yet → keep visible
        var from = lvm.LastFromAnchor;
        var to   = lvm.LastToAnchor;
        if (from is null || to is null) return true;      // dangling / unresolved → keep visible
        const double m = 300.0;                            // bezier-bulge + safety margin (canvas space)
        double minX = System.Math.Min(from.Value.X, to.Value.X) - m;
        double maxX = System.Math.Max(from.Value.X, to.Value.X) + m;
        double minY = System.Math.Min(from.Value.Y, to.Value.Y) - m;
        double maxY = System.Math.Max(from.Value.Y, to.Value.Y) + m;
        return minX < _cullBand.X + _cullBand.Width
            && maxX > _cullBand.X
            && minY < _cullBand.Y + _cullBand.Height
            && maxY > _cullBand.Y;
    }
}
