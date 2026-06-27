using System;
using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// KeyframeInterpolation — Phase 6 curve math. Mirrors the easing functions the
    /// browser-side <c>compositor.js</c> uses when sampling animated parameters per frame.
    ///
    /// All curves operate on a normalised <c>t ∈ [0, 1]</c> within a keyframe segment.
    /// </summary>
    public static class KeyframeInterpolation
    {
        /// <summary>
        /// Sample a scalar parameter at <paramref name="timeMs"/> from a <see
        /// cref="WidgetTimeline"/>. Preferred overload — uses the timeline's
        /// cached sorted+NaN-filtered view so we don't re-sort on every
        /// sample call. The  NaN/Infinity filter still applies; it
        /// just lives on the timeline now (see <c>WidgetTimeline.SortedKeyframes</c>).
        /// </summary>
        public static double SampleScalar(WidgetTimeline timeline, double timeMs)
        {
            if (timeline is null) return 0;
            return SampleScalarCore(timeline.SortedKeyframes, timeMs);
        }

        /// <summary>
        /// Sample a scalar parameter at <paramref name="timeMs"/> from a raw
        /// keyframe list. Kept for tests / call sites that don't have a
        /// <see cref="WidgetTimeline"/> handy; pays the per-call sort cost
        /// each invocation. Production sampling should prefer the
        /// <see cref="WidgetTimeline"/> overload.
        /// </summary>
        public static double SampleScalar(IReadOnlyList<Keyframe> keyframes, double timeMs)
        {
            if (keyframes is null || keyframes.Count == 0) return 0;

            //  (P0-7) — strip keyframes whose TimeMs is NaN/Infinity
            // BEFORE OrderBy. NaN compares false against every other value,
            // so it lands at an arbitrary position and breaks the binary
            // segment scan below (the `timeMs >= a.TimeMs && timeMs <= b.TimeMs`
            // gate fails silently the whole way down and we fall through to
            // the last-keyframe clamp regardless of where timeMs actually
            // lies). FilterFiniteKeyframes logs the index+ParameterPath of
            // each dropped entry so the user can find the offender.
            var finite = NumericGuards.FilterFiniteKeyframes(keyframes, "KeyframeInterpolation");
            if (finite.Count == 0) return 0;

            // Sort defensively — caller may not have ordered them.
            var sorted = finite.OrderBy(k => k.TimeMs).ToList();
            return SampleScalarCore(sorted, timeMs);
        }

        /// <summary>
        /// Shared core — assumes <paramref name="sorted"/> is already ordered
        /// by TimeMs ascending and free of NaN/Infinity TimeMs entries.
        /// </summary>
        private static double SampleScalarCore(IReadOnlyList<Keyframe> sorted, double timeMs)
        {
            if (sorted is null || sorted.Count == 0) return 0;

            // Clamp to first/last.
            if (timeMs <= sorted[0].TimeMs) return ToDouble(sorted[0].Value);
            if (timeMs >= sorted[^1].TimeMs) return ToDouble(sorted[^1].Value);

            // Find the segment.
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var a = sorted[i];
                var b = sorted[i + 1];
                if (timeMs >= a.TimeMs && timeMs <= b.TimeMs)
                {
                    double span = b.TimeMs - a.TimeMs;
                    double t = span <= 0 ? 0 : (timeMs - a.TimeMs) / span;
                    // Sweep 21 — segment easing belongs to the START keyframe `a`.
                    // Use the keyframe-aware overload so custom bezier handles take
                    // effect when the user has dragged them in CurveEditor.
                    double eased = ApplyCurve(t, a);
                    double va = ToDouble(a.Value);
                    double vb = ToDouble(b.Value);
                    return va + (vb - va) * eased;
                }
            }

            return ToDouble(sorted[^1].Value);
        }

        /// <summary>Apply an easing curve to a normalised t ∈ [0,1].</summary>
        public static double ApplyCurve(double t, KeyframeCurve curve)
        {
            t = Math.Clamp(t, 0, 1);
            return curve switch
            {
                KeyframeCurve.Linear    => t,
                KeyframeCurve.EaseIn    => t * t,
                KeyframeCurve.EaseOut   => 1 - (1 - t) * (1 - t),
                KeyframeCurve.EaseInOut => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2,
                KeyframeCurve.Bezier    => CubicBezier(t, 0.25, 0.1, 0.25, 1.0), // ease-like default
                KeyframeCurve.Step      => 0,                                     // hold the start value
                _                       => t,
            };
        }

        /// <summary>
        /// Sweep 21 — keyframe-aware ApplyCurve overload. When <paramref name="kf"/> has
        /// custom bezier control points and Curve == Bezier, evaluates with those handles
        /// instead of the default. Other curve types ignore the handles and behave exactly
        /// like <see cref="ApplyCurve(double, KeyframeCurve)"/>.
        /// </summary>
        public static double ApplyCurve(double t, Keyframe kf)
        {
            if (kf.Curve == KeyframeCurve.Bezier && kf.HasCustomBezierHandles)
            {
                t = Math.Clamp(t, 0, 1);
                // [P1 swarm-audit 2026-05-29] guard nullable bezier handles instead of null-forgiving deref (defaults 0)
                return CubicBezier(t, kf.BezierP1X ?? 0, kf.BezierP1Y ?? 0, kf.BezierP2X ?? 0, kf.BezierP2Y ?? 0);
            }
            return ApplyCurve(t, kf.Curve);
        }

        /// <summary>
        /// Symmetric cubic Bezier evaluator (control points on the unit square).
        /// Uses a small Newton iteration to find x → t, then evaluates y(t).
        /// </summary>
        public static double CubicBezier(double x, double p1x, double p1y, double p2x, double p2y)
        {
            // Coefficients for the parametric x(t) = ax*t^3 + bx*t^2 + cx*t.
            double cx = 3 * p1x;
            double bx = 3 * (p2x - p1x) - cx;
            double ax = 1 - cx - bx;
            double cy = 3 * p1y;
            double by = 3 * (p2y - p1y) - cy;
            double ay = 1 - cy - by;

            double t = x; // initial guess
            for (int i = 0; i < 8; i++)
            {
                double xt = ((ax * t + bx) * t + cx) * t;
                double dx = (3 * ax * t + 2 * bx) * t + cx;
                if (Math.Abs(dx) < 1e-9) break;
                t -= (xt - x) / dx;
                t = Math.Clamp(t, 0, 1);
            }
            return ((ay * t + by) * t + cy) * t;
        }

        private static double ToDouble(System.Text.Json.JsonElement v)
        {
            try
            {
                return v.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => v.GetDouble(),
                    System.Text.Json.JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
                    _ => 0,
                };
            }
            catch { return 0; }
        }
    }
}
