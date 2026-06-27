using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

// Microsoft.UI.Xaml.Shapes.Path collides with System.IO.Path inside this
// partial (the canvas alias in LogicCanvasView.xaml.cs already imports the
// IO Path indirectly via System usings on sibling partials). Alias both to
// keep call sites unambiguous and the file diff small.
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using LineSegment = Microsoft.UI.Xaml.Media.LineSegment;
using PathFigure = Microsoft.UI.Xaml.Media.PathFigure;
using PathGeometry = Microsoft.UI.Xaml.Media.PathGeometry;
using PenLineCap = Microsoft.UI.Xaml.Media.PenLineCap;
using PenLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Wire decoration partial — owns the per-link "flow chevron" + "dangling
// endpoint marker" overlays, plus hover wiring on the 12px hit-zone Path.
//
// The LinkLayer ItemsControl renders the two wire Paths (hit zone + visible
// stroke); this partial layers per-link decorations into sibling Canvases
// (FlowDecorLayer / DanglingMarkerLayer) so the marker geometry doesn't
// fight the wire's bezier rendering for layout space.
public sealed partial class LogicCanvasView
{
    // Per-link decoration overlays. Tracked here so a PropertyChanged on
    // PathData / IsFlow / IsDangling can refresh in place without
    // teardown / rebuild churn.
    private readonly Dictionary<LinkViewModel, ShapePath> _flowChevrons = new();
    private readonly Dictionary<LinkViewModel, FrameworkElement> _danglingMarkers = new();

    // ChevronGeometry cache retired with the flow chevrons (0.10.8 sweep #2).
    // The dictionary stays as an empty placeholder so RefreshFlowChevron's
    // _chevronGeoms.Remove(lvm) call still compiles — keeping the call site
    // unchanged means any previously-painted chevron from an older build is
    // still cleaned up by the next PropertyChanged tick.
    private readonly Dictionary<LinkViewModel, object> _chevronGeoms = new();

    //  Geometry-signature cache for the dangling marker. Reusing
    // the marker Grid/Canvas across PropertyChanged ticks saves a Grid +
    // Canvas + Rectangle*N + SolidColorBrush*2N allocation per tick.
    // The signature combines IsDangling + last-known From/To anchors so a
    // genuine geometry change (the dangling endpoint snapping to a fresh
    // last-known coord) still rebuilds; the common case (PathData /
    // EffectiveStrokeBrush ticking with no anchor change) hits the cache.
    private readonly Dictionary<LinkViewModel, DanglingSig> _danglingSignatures = new();

    /// <summary>
    /// [P3] Value-type dangling-marker signature. Pre-fix the signature was a
    /// string built (allocated) on every RefreshDanglingMarker call — including
    /// cache hits where the marker never rebuilds. Holding the rounded anchor
    /// coordinates + presence flags in a readonly struct lets the cache compare
    /// equality with zero heap allocation, so the string concatenation cost only
    /// existed on the path where it never mattered. Coords are rounded to whole
    /// pixels (the marker is 6px and won't visibly shift sub-pixel) so a node
    /// drag tracing fractional pixels still hits the cache.
    /// </summary>
    private readonly struct DanglingSig : System.IEquatable<DanglingSig>
    {
        public readonly bool HasFrom;
        public readonly bool HasTo;
        public readonly double Fx;
        public readonly double Fy;
        public readonly double Tx;
        public readonly double Ty;

        public DanglingSig((double X, double Y)? from, (double X, double Y)? to)
        {
            HasFrom = from is not null;
            HasTo   = to   is not null;
            Fx = HasFrom ? System.Math.Round(from!.Value.X) : 0;
            Fy = HasFrom ? System.Math.Round(from!.Value.Y) : 0;
            Tx = HasTo   ? System.Math.Round(to!.Value.X)   : 0;
            Ty = HasTo   ? System.Math.Round(to!.Value.Y)   : 0;
        }

        public bool Equals(DanglingSig other)
            => HasFrom == other.HasFrom && HasTo == other.HasTo
            && Fx == other.Fx && Fy == other.Fy
            && Tx == other.Tx && Ty == other.Ty;

        public override bool Equals(object? obj) => obj is DanglingSig o && Equals(o);

        public override int GetHashCode()
            => System.HashCode.Combine(HasFrom, HasTo, Fx, Fy, Tx, Ty);
    }

    // Subscribe to LinkViewModel PropertyChanged once per VM. Tracked so
    // OnLinksChangedForDecor can unhook on removal without iterating the
    // whole list.
    private readonly Dictionary<LinkViewModel, PropertyChangedEventHandler> _linkSubscriptions = new();

    /// <summary>
    /// Hook into the LinkLayer's ItemsSource and seed every existing link's
    /// decoration. Called from DataContextChanged after _vm is set.
    /// </summary>
    private void HookWireDecorations()
    {
        if (_vm is null) return;
        _vm.Links.CollectionChanged += OnLinksChangedForDecor;
        foreach (var lvm in _vm.Links) AttachDecorations(lvm);
    }

    /// <summary>Tear-down counterpart used on DataContext swap.</summary>
    private void UnhookWireDecorations()
    {
        if (_vm is not null) _vm.Links.CollectionChanged -= OnLinksChangedForDecor;
        foreach (var (lvm, handler) in _linkSubscriptions) lvm.PropertyChanged -= handler;
        _linkSubscriptions.Clear();
        foreach (var p in _flowChevrons.Values) FlowDecorLayer.Children.Remove(p);
        _flowChevrons.Clear();
        _chevronGeoms.Clear();
        foreach (var m in _danglingMarkers.Values) DanglingMarkerLayer.Children.Remove(m);
        _danglingMarkers.Clear();
        _danglingSignatures.Clear();
    }

    private void OnLinksChangedForDecor(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                    foreach (LinkViewModel lvm in e.NewItems) AttachDecorations(lvm);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                    foreach (LinkViewModel lvm in e.OldItems) DetachDecorations(lvm);
                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (var lvm in new List<LinkViewModel>(_linkSubscriptions.Keys))
                    DetachDecorations(lvm);
                if (_vm is not null)
                    foreach (var lvm in _vm.Links) AttachDecorations(lvm);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null)
                    foreach (LinkViewModel lvm in e.OldItems) DetachDecorations(lvm);
                if (e.NewItems is not null)
                    foreach (LinkViewModel lvm in e.NewItems) AttachDecorations(lvm);
                break;
        }
    }

    private void AttachDecorations(LinkViewModel lvm)
    {
        if (_linkSubscriptions.ContainsKey(lvm)) return;
        PropertyChangedEventHandler handler = (s, ev) => OnLinkPropertyChanged(lvm, ev);
        _linkSubscriptions[lvm] = handler;
        lvm.PropertyChanged += handler;
        RefreshFlowChevron(lvm);
        RefreshDanglingMarker(lvm);
    }

    private void DetachDecorations(LinkViewModel lvm)
    {
        if (_linkSubscriptions.Remove(lvm, out var handler)) lvm.PropertyChanged -= handler;
        if (_flowChevrons.Remove(lvm, out var chev)) FlowDecorLayer.Children.Remove(chev);
        _chevronGeoms.Remove(lvm);
        if (_danglingMarkers.Remove(lvm, out var mark)) DanglingMarkerLayer.Children.Remove(mark);
        _danglingSignatures.Remove(lvm);
    }

    private void OnLinkPropertyChanged(LinkViewModel lvm, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LinkViewModel.PathData):
            case nameof(LinkViewModel.IsFlow):
            case nameof(LinkViewModel.EffectiveStrokeBrush):
                // Dangling-marker rebuilds are intentionally NOT triggered here.
                // PathData fires at 60 Hz during node drags; a dangling wire's
                // lost endpoint sits at a fixed last-known coordinate that does
                // not move with the drag, so rebuilding the marker every frame
                // (even with the early-return guard inside RefreshDanglingMarker)
                // is pure overhead on graphs with many wires. The marker is only
                // ever rebuilt on an actual IsDangling state flip below.
                RefreshFlowChevron(lvm);
                break;
            case nameof(LinkViewModel.IsDangling):
                RefreshDanglingMarker(lvm);
                RefreshFlowChevron(lvm);
                break;
        }
    }

    /// <summary>
    /// Clear any previously-painted mid-pipe flow chevron. Per Majo's
    /// 0.10.8 readability sweep the chevron decoration is retired — wires
    /// communicate direction via their bezier curvature alone (input pins
    /// always sit on the left side of a node body, output pins on the right,
    /// so the wire's start/end orientation conveys direction without an
    /// extra glyph in the middle). The method is kept as a no-op so the
    /// existing OnLinkPropertyChanged / AttachDecorations call sites keep
    /// compiling, AND so any chevron painted by an older build (or a
    /// half-attached state from a DataContext rebind) gets cleaned up the
    /// next time the link fires PropertyChanged.
    /// </summary>
    private void RefreshFlowChevron(LinkViewModel lvm)
    {
        if (_flowChevrons.Remove(lvm, out var existing))
            FlowDecorLayer.Children.Remove(existing);
        _chevronGeoms.Remove(lvm);
    }

    /// <summary>
    /// Build / update / clear the "lost socket" marker for a dangling wire.
    /// Renders a 6px hollow square at the last-known coordinate of whichever
    /// endpoint vanished — both endpoints get a marker when both are lost.
    /// </summary>
    private void RefreshDanglingMarker(LinkViewModel lvm)
    {
        if (!lvm.IsDangling)
        {
            if (_danglingMarkers.Remove(lvm, out var existing)) DanglingMarkerLayer.Children.Remove(existing);
            _danglingSignatures.Remove(lvm);
            return;
        }

        var from = lvm.LastFromAnchor;
        var to   = lvm.LastToAnchor;
        if (from is null && to is null)
        {
            if (_danglingMarkers.Remove(lvm, out var existing)) DanglingMarkerLayer.Children.Remove(existing);
            _danglingSignatures.Remove(lvm);
            return;
        }

        //  Geometry-signature cache. Compose the signature from the
        // pair of last-known anchors (rounded to whole pixels — sub-pixel
        // change wouldn't shift the 6px marker visibly anyway). On a
        // cache hit, leave the existing marker in place; on a miss, rebuild.
        // [P3] The signature is a value-type struct — no heap allocation on the
        // (common) cache-hit path where the marker never rebuilds.
        var sig = new DanglingSig(from, to);
        if (_danglingSignatures.TryGetValue(lvm, out var prevSig) && prevSig.Equals(sig))
            return;

        var grp = new Grid { IsHitTestVisible = false };
        var marker = new Microsoft.UI.Xaml.Controls.Canvas { IsHitTestVisible = false };
        if (from is not null)
        {
            var sq = MakeDanglingSquare(from.Value.X, from.Value.Y);
            marker.Children.Add(sq);
        }
        if (to is not null)
        {
            var sq = MakeDanglingSquare(to.Value.X, to.Value.Y);
            marker.Children.Add(sq);
        }
        grp.Children.Add(marker);

        if (_danglingMarkers.TryGetValue(lvm, out var prior))
        {
            DanglingMarkerLayer.Children.Remove(prior);
        }
        _danglingMarkers[lvm] = grp;
        _danglingSignatures[lvm] = sig;
        DanglingMarkerLayer.Children.Add(grp);
    }

    //  Shared stroke + half-alpha-fill brushes for the dangling
    // marker — pre-fix every Rectangle build allocated its own SolidColorBrush
    // pair off the same ARGB literals (0xFF/0x80 alpha on rust-red 0xCB/4D/3F).
    // Brushes are sharable across multiple Shape consumers (the canvas
    // never mutates these) so a process-wide pair is safe.
    private static readonly SolidColorBrush s_danglingStroke =
        new(ArchitectCanvasPalette.ErrRustRed);
    private static readonly SolidColorBrush s_danglingFill =
        new(Color.FromArgb(0x80,
            ArchitectCanvasPalette.ErrRustRed.R,
            ArchitectCanvasPalette.ErrRustRed.G,
            ArchitectCanvasPalette.ErrRustRed.B));

    private static Rectangle MakeDanglingSquare(double cx, double cy)
    {
        const double size = 6.0;
        var sq = new Rectangle
        {
            Width = size,
            Height = size,
            StrokeThickness = 1.5,
            Stroke = s_danglingStroke,
            Fill   = s_danglingFill,
            IsHitTestVisible = false,
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(sq, cx - size / 2);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop (sq, cy - size / 2);
        return sq;
    }

    /// <summary>
    /// PointerEntered on the 12px transparent hit-zone Path of a wire —
    /// flip the wire VM's IsHovered so EffectiveStrokeBrush /
    /// EffectiveStrokeThickness lift to the hover treatment.
    /// </summary>
    private void OnLinkHitZonePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LinkViewModel lvm)
            lvm.IsHovered = true;
    }

    /// <summary>PointerExited counterpart — clear the hover flag.</summary>
    private void OnLinkHitZonePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LinkViewModel lvm)
            lvm.IsHovered = false;
    }

    /// <summary>
    ///  Clear every link VM's IsHovered flag. WinUI doesn't always
    /// fire PointerExited on a wire's hit-zone when a node drag (or other
    /// gesture) starts on top of it — the "ghost hover" stays painted after
    /// the gesture completes, producing a brighter wire that the user isn't
    /// actually pointing at. Called from OnHostPointerPressed (NodeDrag /
    /// FrameMove / Marquee branches) and AbortInFlightGesture so a sticky
    /// hover can't survive any gesture that steals capture from the hit-zone.
    /// </summary>
    private void ClearAllLinkHoverFlags()
    {
        if (_vm is null) return;
        foreach (var l in _vm.Links)
            if (l.IsHovered) l.IsHovered = false;
    }
}
