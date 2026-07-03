using System.Collections.Generic;
using System.Text.Json.Serialization;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Shared.Models
{
    /// <summary>
    /// Per-trigger animation track. <see cref="Keyframes"/> is the authored
    /// (unordered) list, mutated freely by Visualist's editor and the
    /// AnimatedPinRegistry pin sync. Hot path consumers (KeyframeInterpolation,
    /// compositor.js mirror tests) want a sorted+NaN-filtered view; <see
    /// cref="SortedKeyframes"/> lazily caches that projection so we stop
    /// re-sorting on every sample call.
    ///
    /// Cache invalidation is automatic on the common mutation paths (list
    /// reference change, count delta, or any in-place TimeMs edit) via a
    /// cheap (count, time-fingerprint, instance-id) signature; callers can
    /// also bump <see cref="InvalidateSortCache"/> explicitly when they've
    /// done something exotic. The cache filters NaN/Infinity TimeMs via
    /// <see cref="NumericGuards.FilterFiniteKeyframes"/> so the sort+filter
    /// behaviour introduced in KeyframeInterpolation is preserved.
    /// </summary>
    public sealed class WidgetTimeline
    {
        [JsonPropertyName("duration")]  public double         DurationMs { get; set; }
        [JsonPropertyName("keyframes")] public List<Keyframe> Keyframes  { get; set; } = new();

        // ── Sort cache ───────────────────────────────────────────────────
        // The cache is keyed on a (reference, count, time-fingerprint)
        // signature so direct mutations of the underlying list (Add /
        // Insert / Remove / Clear / property reassignment / in-place
        // TimeMs edits) all invalidate without requiring callers to
        // explicitly call InvalidateSortCache. The fingerprint scan is
        // O(n) over a value-type accumulator; rebuilding the sort is the
        // expensive O(n log n) step we are saving, so the trade is net
        // positive once a track is sampled more than once between edits.
        [JsonIgnore] private List<Keyframe>? _sortedCache;
        [JsonIgnore] private object?         _sortedSourceRef;
        [JsonIgnore] private int             _sortedSourceCount;
        [JsonIgnore] private long            _sortedSourceFingerprint;

        /// <summary>
        /// Sorted-by-TimeMs, NaN/Infinity-filtered view of <see cref="Keyframes"/>.
        /// Cached between calls; rebuilt only when the underlying list
        /// reference, count, or any keyframe's TimeMs has changed since the
        /// previous access. Returned list is treated as read-only — callers
        /// must mutate the canonical <see cref="Keyframes"/> list, not this
        /// projection.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<Keyframe> SortedKeyframes
        {
            get
            {
                var src = Keyframes;
                if (src is null || src.Count == 0)
                {
                    _sortedCache             = null;
                    _sortedSourceRef         = null;
                    _sortedSourceCount       = 0;
                    _sortedSourceFingerprint = 0;
                    return System.Array.Empty<Keyframe>();
                }

                long fingerprint = ComputeTimeFingerprint(src);
                if (_sortedCache is not null
                    && ReferenceEquals(_sortedSourceRef, src)
                    && _sortedSourceCount == src.Count
                    && _sortedSourceFingerprint == fingerprint)
                {
                    return _sortedCache;
                }

                // Filter NaN/Infinity TimeMs (moved here so
                // KeyframeInterpolation.SampleScalar no longer carries
                // both the filter and the sort), then sort. List<>.Sort is
                // in-place on a freshly allocated copy so the caller's
                // authoring list keeps its insertion order.
                var filtered = NumericGuards.FilterFiniteKeyframes(src, "WidgetTimeline");
                filtered.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

                _sortedCache             = filtered;
                _sortedSourceRef         = src;
                _sortedSourceCount       = src.Count;
                _sortedSourceFingerprint = fingerprint;
                return _sortedCache;
            }
        }

        /// <summary>
        /// Force the next <see cref="SortedKeyframes"/> access to rebuild.
        /// The cache signature already covers count-changing and TimeMs-
        /// changing edits, so most callers don't need this — it exists for
        /// exotic mutations (e.g. swapping keyframe instances at the same
        /// time / index) and for unit tests asserting cache behaviour.
        /// </summary>
        public void InvalidateSortCache()
        {
            _sortedCache             = null;
            _sortedSourceRef         = null;
            _sortedSourceCount       = 0;
            _sortedSourceFingerprint = 0;
        }

        private static long ComputeTimeFingerprint(List<Keyframe> kfs)
        {
            // Mix each keyframe's TimeMs bits into a rolling 64-bit hash.
            // Cheap, deterministic, and sensitive to any single TimeMs
            // change — which is the only field SortedKeyframes orders by,
            // so it's the only field whose in-place edit must invalidate.
            unchecked
            {
                long hash = 1469598103934665603L;
                for (int i = 0; i < kfs.Count; i++)
                {
                    var k = kfs[i];
                    long bits = k is null ? 0L : System.BitConverter.DoubleToInt64Bits(k.TimeMs);
                    hash = (hash ^ bits) * 1099511628211L;
                }
                return hash;
            }
        }
    }
}
