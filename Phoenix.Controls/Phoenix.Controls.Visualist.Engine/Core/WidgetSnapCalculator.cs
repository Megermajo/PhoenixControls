using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using Phoenix.Controls.Shared.Models;

[assembly: InternalsVisibleTo("Phoenix.Controls.Tests")]

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// WidgetSnapCalculator — pure-data snapping helper used by LayerCanvas
    /// during drag and resize. Stateless; given a candidate rect plus the rest
    /// of the layer, returns the nearest snap-adjusted rect within
    /// <see cref="SnapThreshold"/> px and the guide segments that explain why.
    ///
    /// Public so tests can probe the geometry without instantiating WinForms.
    /// LayerCanvas is the only production caller.
    /// </summary>
    public static class WidgetSnapCalculator
    {
        public const int SnapThreshold = 6;

        // QC22-05 — minimum widget dimension enforced during resize. Below this
        // the widget becomes a visually unusable sliver (smaller than the
        // resize-handle hit zone). When a snap would collapse the moving edge
        // below this floor, the snap is dropped and the edge is pinned to the
        // floor in place instead of producing a 20-px sliver that simultaneously
        // satisfies the snap target and the clamp — the two are mutually
        // exclusive at that scale.
        public const int MinResizeDim = 20;

        /// <summary>
        /// One axis-aligned guide line drawn while a snap is active. Always a
        /// vertical line (X1 == X2) or a horizontal line (Y1 == Y2), spanning
        /// from the matching widget through the selected one so the author can
        /// see what the snap latched onto.
        /// </summary>
        public readonly struct Guide
        {
            public readonly int X1, Y1, X2, Y2;
            public Guide(int x1, int y1, int x2, int y2) { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
        }

        public readonly struct SnapResult
        {
            /// <summary>The rect adjusted by snapping (or the input rect if no snap engaged).</summary>
            public readonly Rectangle Rect;
            /// <summary>Guide segments to render this frame; may be empty.</summary>
            public readonly IReadOnlyList<Guide> Guides;
            public SnapResult(Rectangle rect, IReadOnlyList<Guide> guides) { Rect = rect; Guides = guides; }
        }

        /// <summary>
        /// Snap a candidate rect against the layer-canvas edges/center plus
        /// every other widget's edges/centers. Edges considered: left, center-x,
        /// right (X axis); top, center-y, bottom (Y axis). Adjacent-edge snaps
        /// (right-of-A meeting left-of-B) are also produced.
        ///
        /// Set <paramref name="bypass"/> = true (Alt held) to short-circuit
        /// without snapping; the input rect is returned unchanged with no
        /// guides. Set <paramref name="resizing"/> = true to lock the X/Y top-
        /// left corner and only snap the right + bottom edges (matches the
        /// LayerCanvas resize-handle gesture).
        /// </summary>
        public static SnapResult Snap(
            Rectangle candidate,
            int layerWidth,
            int layerHeight,
            IEnumerable<Rectangle> otherWidgetRects,
            bool bypass,
            bool resizing)
        {
            if (bypass) return new SnapResult(candidate, System.Array.Empty<Guide>());

            int snappedX = candidate.X;
            int snappedY = candidate.Y;
            int snappedW = candidate.Width;
            int snappedH = candidate.Height;

            // Score-based snap: pick the nearest target per axis (smallest
            // absolute delta within threshold). A score-tie isn't important —
            // we return the first hit deterministically.
            int? bestDxX = null;     // adjustment to X (move horizontal)
            int? bestRightDx = null; // adjustment to right edge (resize)
            int? bestDyY = null;
            int? bestBottomDy = null;
            var guides = new List<Guide>(4);
            int? guideXLeftAt = null;
            int? guideXRightAt = null;
            int? guideYTopAt = null;
            int? guideYBottomAt = null;

            int candLeft   = candidate.Left;
            int candCx     = candidate.X + candidate.Width  / 2;
            int candRight  = candidate.Right;
            int candTop    = candidate.Top;
            int candCy     = candidate.Y + candidate.Height / 2;
            int candBottom = candidate.Bottom;

            // Layer-canvas reference X targets.
            var xTargets = new List<int> { 0, layerWidth / 2, layerWidth };
            var yTargets = new List<int> { 0, layerHeight / 2, layerHeight };

            // Other widgets contribute their left / center / right edges.
            foreach (var ow in otherWidgetRects)
            {
                xTargets.Add(ow.Left);
                xTargets.Add(ow.X + ow.Width / 2);
                xTargets.Add(ow.Right);
                yTargets.Add(ow.Top);
                yTargets.Add(ow.Y + ow.Height / 2);
                yTargets.Add(ow.Bottom);
            }

            // ── X axis snapping ────────────────────────────────────────────
            foreach (int tx in xTargets)
            {
                if (!resizing)
                {
                    // Moving — try snapping each edge of the candidate to tx
                    // and keep the smallest non-zero adjustment within threshold.
                    TrySnap(candLeft,  tx, ref bestDxX, ref guideXLeftAt);
                    TrySnap(candCx,    tx, ref bestDxX, ref guideXLeftAt);
                    TrySnap(candRight, tx, ref bestDxX, ref guideXLeftAt);
                }
                else
                {
                    // Resizing from the bottom-right handle — only the right
                    // edge moves, so only it can snap.
                    TrySnap(candRight, tx, ref bestRightDx, ref guideXRightAt);
                }
            }

            // ── Y axis snapping ────────────────────────────────────────────
            foreach (int ty in yTargets)
            {
                if (!resizing)
                {
                    TrySnap(candTop,    ty, ref bestDyY, ref guideYTopAt);
                    TrySnap(candCy,     ty, ref bestDyY, ref guideYTopAt);
                    TrySnap(candBottom, ty, ref bestDyY, ref guideYTopAt);
                }
                else
                {
                    TrySnap(candBottom, ty, ref bestBottomDy, ref guideYBottomAt);
                }
            }

            if (!resizing)
            {
                if (bestDxX is int dxx) snappedX += dxx;
                if (bestDyY is int dyy) snappedY += dyy;
            }
            else
            {
                // QC22-05 — when the snap-adjusted right edge would push width
                // below MinResizeDim, drop the snap rather than honouring both
                // (which produces the visually-broken combination of "guide
                // visible at X, but widget right edge is actually at X+sliver
                // because of the min clamp"). Two clean outcomes:
                //   • snapped width ≥ MinResizeDim → apply snap + show guide.
                //   • snapped width < MinResizeDim → ignore snap, pin to floor,
                //     drop the guide so the UI is honest about what happened.
                // Same rule mirrored on the Y axis.
                if (bestRightDx is int rdx)
                {
                    int candidateW = snappedW + rdx;
                    if (candidateW >= MinResizeDim)
                    {
                        snappedW = candidateW;
                    }
                    else
                    {
                        snappedW = System.Math.Max(MinResizeDim, snappedW + rdx);
                        guideXRightAt = null; // honesty: snap was dropped.
                    }
                }
                else
                {
                    // No snap engaged on the right edge — still enforce the
                    // min-size floor so a raw user drag inward can't produce
                    // a sliver either.
                    snappedW = System.Math.Max(MinResizeDim, snappedW);
                }

                if (bestBottomDy is int bdy)
                {
                    int candidateH = snappedH + bdy;
                    if (candidateH >= MinResizeDim)
                    {
                        snappedH = candidateH;
                    }
                    else
                    {
                        snappedH = System.Math.Max(MinResizeDim, snappedH + bdy);
                        guideYBottomAt = null;
                    }
                }
                else
                {
                    snappedH = System.Math.Max(MinResizeDim, snappedH);
                }
            }

            // Collect guide segments for the engaged snaps. Vertical guide on
            // an X-axis snap, horizontal guide on a Y-axis snap.
            if (guideXLeftAt is int gxl)
                guides.Add(new Guide(gxl, 0, gxl, layerHeight));
            if (guideXRightAt is int gxr)
                guides.Add(new Guide(gxr, 0, gxr, layerHeight));
            if (guideYTopAt is int gyt)
                guides.Add(new Guide(0, gyt, layerWidth, gyt));
            if (guideYBottomAt is int gyb)
                guides.Add(new Guide(0, gyb, layerWidth, gyb));

            return new SnapResult(new Rectangle(snappedX, snappedY, snappedW, snappedH), guides);
        }

        private static void TrySnap(int candidateValue, int target, ref int? bestDelta, ref int? guideAt)
        {
            int delta = target - candidateValue;
            int abs   = delta < 0 ? -delta : delta;
            if (abs > SnapThreshold) return;
            int curBest = bestDelta is int b ? (b < 0 ? -b : b) : int.MaxValue;
            if (abs >= curBest) return;
            bestDelta = delta;
            guideAt   = target;
        }
    }
}
