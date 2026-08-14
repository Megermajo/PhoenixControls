using System;
using System.Diagnostics;
using System.Threading;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// A fixed-window rate cap for the Hub→Architect debug-trace broadcasts
    /// (<c>DEBUG_NODE_EXEC</c> / <c>DEBUG_VAR_SET</c>), with an observable drop
    /// count instead of a silent one.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a cap and not a coalesce.</b> The pressure this bounds is
    /// inherent, not repetitive: a busy chat is sized in-code at roughly
    /// 10 msg/s × 5 matching scripts × ~50 nodes each ≈ <b>2,500 marker
    /// emissions per second</b>, and those are ~250 DISTINCT (node, script)
    /// pairs each firing ten times a second. De-duplicating by identity inside a
    /// window therefore removes almost nothing — inside any window short enough
    /// to keep the flash live, each pair appears at most once already. Batching
    /// them into one array message would work, but it changes the wire shape and
    /// so the Architect-side consumer contract, which is a cross-pillar change
    /// this fix deliberately does not make.</para>
    ///
    /// <para><b>Why capping is acceptable here.</b> The consumer is a node
    /// FLASH — a purely visual debug affordance. Past a couple of hundred
    /// markers a second a human sees a continuous glow, so the frames above the
    /// cap carry no information a viewer could act on. The cap is far above
    /// ordinary authoring traffic (single-digit markers per manual trigger), so
    /// a streamer debugging one graph never reaches it; only the flood case
    /// does.</para>
    ///
    /// <para><b>Why this exists at all.</b> The emitters were gated on
    /// <c>Bus.IsArchitectConnected</c>, which was WebSocket-only and therefore
    /// permanently false once Architect moved in-process — so the gate was
    /// suppressing 100% of the traffic and the flash was dead. Correcting the
    /// predicate re-admits that traffic to a hot per-node path that no test
    /// covers, which is exactly why the budget lands in the same change rather
    /// than being left to a run-app pass to discover.</para>
    ///
    /// <para>Thread-safe and allocation-free on the hot path: one interlocked
    /// increment per admitted marker, and a monotonic <see cref="Stopwatch"/>
    /// read — never wall clock, so an NTP step or a DST change cannot widen or
    /// collapse a window.</para>
    /// </remarks>
    internal sealed class DebugTraceBudget
    {
        /// <summary>
        /// Markers admitted per window. 200/s is ~10× a fast manual authoring
        /// session and ~1/12th of the measured flood, and it is well past the
        /// rate at which discrete flashes stop being distinguishable.
        /// </summary>
        internal const int DefaultPerWindow = 200;

        /// <summary>How often a dropped-marker summary is logged, at most.</summary>
        private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(30);

        private readonly int _perWindow;
        private readonly TimeSpan _window;
        private readonly string _label;
        private readonly Func<long> _nowMs;

        private readonly object _gate = new();
        private long _windowStartMs;
        private int _admittedThisWindow;
        private long _droppedSinceReport;
        private long _lastReportMs;

        internal DebugTraceBudget(string label,
                                  int perWindow = DefaultPerWindow,
                                  TimeSpan? window = null,
                                  Func<long>? nowMs = null)
        {
            _label = label;
            _perWindow = perWindow > 0 ? perWindow : int.MaxValue;
            _window = window ?? TimeSpan.FromSeconds(1);
            _nowMs = nowMs ?? (() => MonotonicMs());
            _windowStartMs = _nowMs();
            _lastReportMs = _windowStartMs;
        }

        private static readonly Stopwatch s_mono = Stopwatch.StartNew();
        private static long MonotonicMs() => s_mono.ElapsedMilliseconds;

        /// <summary>Total markers refused since construction. Diagnostics only.</summary>
        internal long TotalDropped => Interlocked.Read(ref _totalDropped);
        private long _totalDropped;

        /// <summary>
        /// True when this marker may be broadcast. False means the window's budget
        /// is spent and the marker is dropped — the caller does no work at all,
        /// which is the point: the serialize + encode + fan-out is what costs.
        /// </summary>
        internal bool TryAdmit()
        {
            long now = _nowMs();
            lock (_gate)
            {
                if (now - _windowStartMs >= (long)_window.TotalMilliseconds)
                {
                    _windowStartMs = now;
                    _admittedThisWindow = 0;
                }

                if (_admittedThisWindow < _perWindow)
                {
                    _admittedThisWindow++;
                    return true;
                }

                _droppedSinceReport++;
                Interlocked.Increment(ref _totalDropped);

                // Report at most twice a minute so the summary can never become
                // the flood it is describing. Composed inside the lock but logged
                // outside it would need a temp; the log call is cheap relative to
                // a 30-second cadence, so it stays here for simplicity.
                if (now - _lastReportMs >= (long)ReportEvery.TotalMilliseconds)
                {
                    long dropped = _droppedSinceReport;
                    _droppedSinceReport = 0;
                    _lastReportMs = now;
                    GlobalLogger.Log(
                        $"{_label}: {dropped} debug-trace markers dropped in the last " +
                        $"{ReportEvery.TotalSeconds:0}s (cap {_perWindow}/{_window.TotalSeconds:0}s). " +
                        "The Architect node flash stays live; only the excess is refused.",
                        "ScriptManager", LogLevel.Debug);
                }
                return false;
            }
        }
    }
}
