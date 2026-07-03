using System;
using System.Collections.Generic;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// NumericGuards — centralised NaN / Infinity defence
    /// for the three places non-finite doubles cause silent corruption:
    ///
    ///  1. Math.* script commands — a NaN result becomes a string "NaN" that
    ///     poisons every downstream comparison (NaN != NaN), so we gate
    ///     numeric outputs through <see cref="Sanitize"/> and return null
    ///     when the value is non-finite. Null is the existing "branch goes
    ///     nowhere on bad input" semantic the math handlers already use.
    ///
    ///  2. KeyframeInterpolation — a keyframe with TimeMs == NaN sorts
    ///     unpredictably (every comparison vs NaN is false), so the binary
    ///     segment lookup in SampleScalar can land on the wrong pair. We
    ///     filter NaN/Infinity TimeMs out of the keyframe list before
    ///     sorting via <see cref="FilterFiniteKeyframes"/>.
    ///
    ///  3. LayerSerializer save — System.Text.Json throws ArgumentException
    ///     on a raw NaN/Infinity double by default, which surfaces as a
    ///     mid-save crash and loses the user's work. We pre-validate the
    ///     Layer via <see cref="ValidateLayerForSave"/> and refuse the save
    ///     with a user-visible GlobalLogger entry pointing at the offending
    ///     widget+keyframe (no modal).
    ///
    /// All three call sites share the same root cause but want different
    /// shapes of answer, so this file exposes three small helpers rather
    /// than one over-broad one.
    /// </summary>
    public static class NumericGuards
    {
        private const string LogSource = "Numeric";

        /// <summary>
        /// Gate a numeric command result. Returns the value as an invariant-
        /// culture string when finite; logs and returns null when NaN/Infinity.
        /// The string form matches what the math handlers already return so
        /// callers can replace `return v.ToString(...)` one-for-one.
        /// </summary>
        public static string? Sanitize(double value, string contextForLog)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                GlobalLogger.Log(
                    $"{contextForLog}: produced NaN/Infinity, returning null",
                    "Math",
                    LogLevel.LogicExecution);
                return null;
            }
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Plain finite check — no logging side-effect.</summary>
        public static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>
        /// Filter keyframes whose TimeMs is NaN or Infinity. Logs a warning
        /// per dropped keyframe (with index + ParameterPath when available)
        /// so the user can find the offender in Visualist. Returns a fresh
        /// list — caller's collection is not mutated.
        /// </summary>
        public static List<Keyframe> FilterFiniteKeyframes(
            IReadOnlyList<Keyframe> keyframes,
            string contextForLog = "keyframe")
        {
            var result = new List<Keyframe>(keyframes.Count);
            for (int i = 0; i < keyframes.Count; i++)
            {
                var k = keyframes[i];
                if (k is null) continue;
                if (double.IsNaN(k.TimeMs) || double.IsInfinity(k.TimeMs))
                {
                    GlobalLogger.Log(
                        $"{contextForLog}: dropping keyframe #{i} ({k.ParameterPath}) with non-finite TimeMs",
                        LogSource,
                        LogLevel.Warning);
                    continue;
                }
                result.Add(k);
            }
            return result;
        }

        /// <summary>
        /// Per-keyframe rejection report used by <see cref="ValidateLayerForSave"/>.
        /// Identifies where a non-finite numeric lives so the user can fix the
        /// offending widget without hunting.
        /// </summary>
        public readonly record struct KeyframeIssue(
            string WidgetId,
            string WidgetName,
            string TriggerName,
            int KeyframeIndex,
            string ParameterPath,
            string Field);

        /// <summary>
        /// Walk every keyframe in a Layer and report any non-finite numeric
        /// values that would crash System.Text.Json on save. Caller is
        /// expected to reject the save when the returned list is non-empty
        /// and surface the entries via GlobalLogger / panel UI.
        /// </summary>
        public static List<KeyframeIssue> ValidateLayerForSave(Layer layer)
        {
            var issues = new List<KeyframeIssue>();
            if (layer is null) return issues;

            foreach (var widget in layer.Widgets)
            {
                if (widget is null) continue;
                foreach (var trigger in widget.Triggers)
                {
                    if (trigger?.Timeline is null) continue;
                    var dur = trigger.Timeline.DurationMs;
                    if (double.IsNaN(dur) || double.IsInfinity(dur))
                    {
                        issues.Add(new KeyframeIssue(
                            widget.Id, widget.Name, trigger.Name,
                            KeyframeIndex: -1,
                            ParameterPath: "(timeline)",
                            Field: "DurationMs"));
                    }

                    var kfs = trigger.Timeline.Keyframes;
                    for (int i = 0; i < kfs.Count; i++)
                    {
                        var k = kfs[i];
                        if (k is null) continue;
                        if (double.IsNaN(k.TimeMs) || double.IsInfinity(k.TimeMs))
                            issues.Add(MakeIssue(widget, trigger, i, k.ParameterPath, nameof(Keyframe.TimeMs)));

                        if (k.BezierP1X is double p1x && !IsFinite(p1x))
                            issues.Add(MakeIssue(widget, trigger, i, k.ParameterPath, nameof(Keyframe.BezierP1X)));
                        if (k.BezierP1Y is double p1y && !IsFinite(p1y))
                            issues.Add(MakeIssue(widget, trigger, i, k.ParameterPath, nameof(Keyframe.BezierP1Y)));
                        if (k.BezierP2X is double p2x && !IsFinite(p2x))
                            issues.Add(MakeIssue(widget, trigger, i, k.ParameterPath, nameof(Keyframe.BezierP2X)));
                        if (k.BezierP2Y is double p2y && !IsFinite(p2y))
                            issues.Add(MakeIssue(widget, trigger, i, k.ParameterPath, nameof(Keyframe.BezierP2Y)));

                        // The keyframe Value JsonElement can hold a numeric
                        // payload — when it does, treat NaN there as a save
                        // blocker too, otherwise the next save attempt will
                        // re-explode at the JsonElement write site.
                        if (k.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            if (k.Value.TryGetDouble(out var dv) && !IsFinite(dv))
                                issues.Add(MakeIssue(widget, trigger, i, k.ParameterPath, "Value"));
                        }
                    }
                }
            }

            return issues;
        }

        private static KeyframeIssue MakeIssue(
            LayerWidget widget, WidgetTrigger trigger, int index, string parameterPath, string field)
            => new(widget.Id, widget.Name, trigger.Name, index, parameterPath, field);
    }

    /// <summary>
    /// Thrown by <see cref="Services.LayerSerializer"/> when a save would
    /// write NaN / Infinity. Carries the offending keyframe coordinates so
    /// the caller can drive UI / log output. Not a generic IOException — a
    /// dedicated type makes it easy to catch only the rejection without
    /// swallowing real disk failures.
    /// </summary>
    public sealed class LayerNumericValidationException : Exception
    {
        public IReadOnlyList<NumericGuards.KeyframeIssue> Issues { get; }

        public LayerNumericValidationException(IReadOnlyList<NumericGuards.KeyframeIssue> issues)
            : base($"Layer contains {issues.Count} non-finite numeric value(s); save refused.")
        {
            Issues = issues;
        }
    }
}
