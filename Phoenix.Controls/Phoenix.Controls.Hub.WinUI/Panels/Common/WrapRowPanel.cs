using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Minimal left-to-right wrapping panel for <see cref="RoleCheckRow"/>.
///
/// WinUI 3's base SDK ships no WrapPanel (that one lives in the Community Toolkit,
/// which this suite does not reference), and <c>VariableSizedWrapGrid</c> only wraps
/// on a UNIFORM cell pitch — a pitch wide enough for "Broadcaster" wastes half a cell
/// on "VIP", and a narrower one clips. So: children keep their natural desired size
/// and a row breaks as soon as the next child would cross the available width.
///
/// This exists because the role gate is hosted at sites whose widths differ by a
/// factor of three — the Quotes permissions block splits the card into THREE star
/// columns, the Counters one into two, while Automod / Loyalty / Custom Commands each
/// give the row a full card. A fixed horizontal StackPanel silently clipped the
/// right-hand boxes in the narrow hosts; wrapping makes the control fit whatever it
/// is given instead of every host having to budget for the widest possible row.
///
/// ★ Under a finite measure width the panel reports the whole width it was OFFERED,
/// never the packed extent of the rows it just built. Reporting the extent is what
/// left the Automod and Loyalty role rows clipped: a shrink-to-fit host (MaxWidth +
/// HorizontalAlignment="Center" — what both of those section stacks are) sizes
/// itself to what its content desires, so the panel came back arranged at exactly
/// its own measured width and the strict <c>&gt;</c> break test had zero tolerance
/// for any width lost on the way back down. Under an INFINITE measure width the
/// panel still degenerates to a single row — see the invariant note in
/// <see cref="ArrangeOverride"/>. Row gaps come from the children's own bottom
/// margin, so the panel itself carries no spacing knobs.
/// </summary>
public sealed class WrapRowPanel : Panel
{
    /// <summary>
    /// The width the last measure pass broke rows against, so arrange can hold
    /// itself to it. Infinite until the first measure — and for any host that
    /// measures this panel at infinite width.
    /// </summary>
    private double _measureWidth = double.PositiveInfinity;

    /// <summary>
    /// Breaks children into rows against <paramref name="width"/> and, when
    /// <paramref name="arrange"/> is set, positions them. ONE implementation for
    /// both passes so the two can never disagree about where a row ends.
    /// </summary>
    private Size LayoutRows(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, widest = 0;

        foreach (var child in Children)
        {
            Size want = child.DesiredSize;

            // `x > 0` keeps a single over-wide child on its own row instead of
            // pushing an empty row ahead of it. With width infinite the
            // comparison is never true, so everything stays on one row.
            if (x > 0 && x + want.Width > width)
            {
                widest = Math.Max(widest, x);
                x = 0;
                y += lineHeight;
                lineHeight = 0;
            }

            if (arrange) child.Arrange(new Rect(x, y, want.Width, want.Height));

            x += want.Width;
            lineHeight = Math.Max(lineHeight, want.Height);
        }

        widest = Math.Max(widest, x);
        return new Size(widest, y + lineHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Children are always measured unconstrained: their natural width is what
        // decides where the break falls, and a CheckBox handed a too-small width
        // would trim its own label rather than move to the next row.
        var unconstrained = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children) child.Measure(unconstrained);

        _measureWidth = availableSize.Width;
        Size rows = LayoutRows(availableSize.Width, arrange: false);

        // Report the offered width and only the packed HEIGHT. Asking for `widest`
        // instead is what let a shrink-to-fit host collapse onto the row's exact
        // pixel width (see the class remark); reporting the full offer costs
        // nothing at a star-column host — it stretches the panel to that width
        // regardless — and gives the shrink-to-fit ones real slack. An infinite
        // offer carries no width to report back, so there the packed extent is
        // the only meaningful answer.
        return double.IsInfinity(availableSize.Width)
            ? rows
            : new Size(availableSize.Width, rows.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // ── Invariant: arrange must never break rows against a NARROWER width
        //    than measure did ──────────────────────────────────────────────────
        // Row breaks are monotone in the width they are computed against: widening
        // can only merge rows, never split one. So laying out at >= the measured
        // width reproduces measure's breaks or fewer of them, and the painted
        // height can never exceed the height measure reported. Below that width a
        // later child breaks early and adds a row nothing budgeted height for —
        // and since neither this panel nor its hosts set a Clip, the surplus row
        // is painted outside the slot the host reserved, where the next card is
        // then drawn over it. That is the reported clipping. Holding arrange to
        // Math.Max makes "painted height <= reported height" a property of this
        // type rather than of each host's markup; when a host genuinely arranges
        // narrower than it measured, the price is a trailing box overhanging to
        // the right instead of a row disappearing under the next card.
        LayoutRows(
            double.IsInfinity(_measureWidth) ? finalSize.Width : Math.Max(finalSize.Width, _measureWidth),
            arrange: true);

        // The one case the Math.Max cannot cover is a host that MEASURES at
        // infinite width (a horizontal StackPanel, an Auto column, a horizontally
        // scrolling host): measure degenerated to a single row and reported a
        // one-row height, and there is no finite measured width to hold arrange
        // to, so arrange falls back to the width it was handed and every row after
        // the first lands outside that height. If a future host needs an infinite
        // measure context, give this panel an explicit finite width instead.
        //
        // Arrange cannot repair such a disagreement by re-entering measure, which
        // is why the fix above is a remembered width and not a correction. A guard
        // here once tried: cache the measured height, and on a taller arrange with
        // a different width call InvalidateMeasure(). The re-measure runs with the
        // parent's UNCHANGED availableSize, so MeasureOverride recomputes the same
        // one-row height and the passes disagree again on the next arrange —
        // measure→arrange→invalidate repeating until WinUI throws
        // LayoutCycleException, a crash in place of one clipped row. It was removed
        // as that latent layout cycle. _measureWidth costs one field written during
        // measure and read during arrange: no invalidation, no extra layout pass.
        //
        // (Constraining the child measure would not help either: a CheckBox handed
        // a too-small width trims its own label, which just moves the truncation
        // one element inwards. The unconstrained measure stays.)
        return finalSize;
    }
}
