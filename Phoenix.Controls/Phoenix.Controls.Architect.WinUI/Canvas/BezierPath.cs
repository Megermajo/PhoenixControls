using System.Globalization;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Cubic-bezier wire path math, derived from the UE-Blueprints idiom: the
// control points sit horizontally inset from each endpoint at the same y
// as the endpoint, so the curve leaves and re-enters horizontally —
// readable as "this wire goes left-to-right" regardless of vertical drift.
//
// Tangent floor + vertical-distance term:
//   The naive formula `dx = |to.x - from.x| * 0.5` collapses to zero on
//   "back-routing" feedback loops (e.g. Macro.Exit → Macro.Entry where
//   the source x sits to the RIGHT of the target x and the curve has to
//   loop back). That made the loop flatten into a near-straight line
//   diving through the source node body. We now compose the inset from:
//     * a vertical-distance term so taller loops bow further outward,
//     * a minimum-tangent floor so even short horizontal runs leave the
//       endpoint with a visible curve,
//     * a back-routing kicker for negative dx so reverse-direction wires
//       always have enough lateral slack to clear the source.
//
// Pure C# string emit so this can sit in a non-WinUI library; XAML's
// <Path Data="{...}"/> mini-language accepts SVG-style cubic-bezier
// "M x y C cx1 cy1, cx2 cy2, ex ey".
public static class BezierPath
{
    /// <summary>
    /// Absolute minimum tangent length on every wire — keeps the bezier
    /// from collapsing into a near-straight line on short horizontal runs
    /// and stops the back-routing loop case from diving through the source
    /// node body. 40 px matches the canvas grid step so wires feel
    /// "snapped" against the grid spacing.
    /// </summary>
    public const double MinTangent = 40.0;

    /// <summary>
    /// Self-loop detection epsilon: when both axes' deltas are smaller than
    /// this in canvas-space, the wire is treated as a self-loop and routed
    /// via an upward-arcing path so the loop is visually discoverable
    /// instead of collapsing into a ~40 px tight arc behind the node body
    /// (QC48-39). 0.5 px tolerates pixel-rounding noise from
    /// NodeGeometry.SocketAnchor without false-positiving on real wires.
    /// </summary>
    private const double SelfLoopEpsilon = 0.5;

    /// <summary>
    /// Vertical reach of the self-loop arc above the endpoint row, in
    /// canvas-space pixels. Matches the typical node body height so the
    /// loop visibly clears the node chrome on either side.
    /// </summary>
    private const double SelfLoopArcHeight = 80.0;

    /// <summary>
    /// Build the cubic bezier path string from (<paramref name="fromX"/>,
    /// <paramref name="fromY"/>) to (<paramref name="toX"/>,
    /// <paramref name="toY"/>) in the `M x y C cx1 cy1, cx2 cy2, ex ey`
    /// form WinUI 3 PathGeometry / SVG both accept.
    ///  Self-loop case (both endpoints coincident in canvas-space)
    /// arcs upward and around the source so the wire is visible.
    /// </summary>
    public static string Build(double fromX, double fromY, double toX, double toY)
    {
        var c = CultureInfo.InvariantCulture;

        //  Self-loop: endpoints coincide in canvas-space. Without
        // this branch, ControlDistance returns MinTangent (40 px) and the
        // bezier collapses into a tight arc that sits inside the node body
        // — visually invisible even though hit-testing still works.
        // Routing the curve upward and outward through a pair of control
        // points 60-100 px above each endpoint gives a small overhead loop
        // that arcs around the source node, mirroring UE Blueprints'
        // self-connection rendering.
        if (System.Math.Abs(toX - fromX) < SelfLoopEpsilon
            && System.Math.Abs(toY - fromY) < SelfLoopEpsilon)
        {
            double cx1 = fromX + MinTangent;
            double cx2 = toX   - MinTangent;
            double cy1 = fromY - SelfLoopArcHeight;
            double cy2 = toY   - SelfLoopArcHeight;
            return string.Create(c,
                $"M {fromX} {fromY} C {cx1} {cy1}, {cx2} {cy2}, {toX} {toY}");
        }

        double dx = ControlDistance(fromX, fromY, toX, toY);
        // Force invariant culture to keep "12.5" not "12,5" on locales like de-DE.
        return string.Create(c,
            $"M {fromX} {fromY} C {fromX + dx} {fromY}, {toX - dx} {toY}, {toX} {toY}");
    }

    /// <summary>
    /// Resolve the horizontal control-point distance for a wire spanning
    /// (from → to). Combines:
    ///   * a horizontal half-distance (the original UE Blueprints term),
    ///   * a vertical fraction so steep loops bow further outward,
    ///   * a back-routing kicker (only when the wire runs right-to-left)
    ///     so reverse-direction wires always have lateral slack to clear
    ///     the source,
    /// clamped to <see cref="MinTangent"/> on the low end so even short
    /// runs leave the endpoint with a visible curve.
    /// </summary>
    public static double ControlDistance(double fromX, double fromY, double toX, double toY)
    {
        double horizontal = System.Math.Abs(toX - fromX) * 0.5;
        double vertical   = System.Math.Abs(toY - fromY) * 0.35;
        // Back-routing kicker — when toX < fromX the wire has to loop back
        // past the source node; a flat half-distance is then exactly half of
        // a (potentially small) negative span and the curve flattens. Add a
        // chunk of the vertical span to push the control points outward so
        // the loop arcs visibly around the source.
        double backKick = (toX < fromX) ? System.Math.Abs(toY - fromY) * 0.45 : 0.0;
        return System.Math.Max(MinTangent, horizontal + vertical + backKick);
    }

    /// <summary>
    /// Sample a point on the cubic bezier at parameter t ∈ [0,1].
    /// Used by hit-testing, mid-path chevron orientation on flow wires,
    /// and the (deferred) live-trace pulse animation.
    ///  Mirrors <see cref="Build"/>'s self-loop branch so
    /// hit-testing / chevron orientation on a self-loop traces the same
    /// upward arc the rendered path follows.
    /// </summary>
    public static (double X, double Y) Sample(double fromX, double fromY, double toX, double toY, double t)
    {
        double cx1, cy1, cx2, cy2;
        if (System.Math.Abs(toX - fromX) < SelfLoopEpsilon
            && System.Math.Abs(toY - fromY) < SelfLoopEpsilon)
        {
            cx1 = fromX + MinTangent;
            cx2 = toX   - MinTangent;
            cy1 = fromY - SelfLoopArcHeight;
            cy2 = toY   - SelfLoopArcHeight;
        }
        else
        {
            double dx = ControlDistance(fromX, fromY, toX, toY);
            cx1 = fromX + dx; cy1 = fromY;
            cx2 = toX   - dx; cy2 = toY;
        }

        double mt = 1.0 - t;
        double mt2 = mt * mt, mt3 = mt2 * mt;
        double t2 = t * t,    t3 = t2 * t;

        double x = mt3 * fromX + 3 * mt2 * t * cx1 + 3 * mt * t2 * cx2 + t3 * toX;
        double y = mt3 * fromY + 3 * mt2 * t * cy1 + 3 * mt * t2 * cy2 + t3 * toY;
        return (x, y);
    }
}
