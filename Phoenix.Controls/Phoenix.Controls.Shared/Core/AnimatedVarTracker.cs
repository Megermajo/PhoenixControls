using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Core;

/// <summary>
/// B42 (audit/winui-regressions-2026-05-24) — cross-pillar registry that
/// records which variable names currently have keyframe animation associated
/// with them in Visualist. Architect reads this registry to light the
/// per-pin animated-pin badge on Var.Set / Var.Get / Public.Set / Public.Get
/// nodes so authors can see at a glance which destinations are animated.
///
/// Shape (mirroring <c>Visualist.Core.AnimatedPinRegistry</c>, which is the
/// per-document source of truth):
///   • Visualist mirrors its per-document keyframe state into this tracker
///     whenever a keyframe is added / removed (see
///     <c>AnimatedPinRegistry.NotifyMirror*</c>).
///   • Architect's pin overlay queries <see cref="IsAnimated"/> per socket
///     and subscribes to <see cref="AnimationStateChanged"/> for live repaint.
///
/// Thread-safety: backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// because Visualist mutations can fire from the dispatcher while Architect
/// reads from the canvas-paint tick. The event handler list itself is
/// guarded by lock-snapshot dispatch so a subscriber's exception in one
/// pillar can't poison the registry for the other.
///
/// Per <c>feedback_visualist_architect_chrome_independence.md</c>, the
/// pillar-specific paint code stays per-pillar — only the var-name → bool
/// flag lives in shared. Visualist's <c>AnimatedPinRegistry</c> still owns
/// the timeline / keyframe model.
/// </summary>
public static class AnimatedVarTracker
{
    // Value is the int reference-count of animated keyframe groups currently
    // bound to the var name. Reference-counted (rather than bool) so a single
    // var name animated by two distinct widgets / layers stays "animated"
    // until both call MarkNotAnimated. The counter is bounded by Visualist's
    // own author flow — a key never goes negative under correct mirroring.
    private static readonly ConcurrentDictionary<string, int> s_counts =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Fires whenever a variable's animated state flips (false → true or
    /// true → false). Subscribers receive the variable name plus the new
    /// state. The event invokes synchronously on the mirroring caller's
    /// thread — Architect's NodeView.Pins.cs marshals the repaint back to
    /// the UI thread via DispatcherQueue.
    /// </summary>
    public static event Action<string, bool>? AnimationStateChanged;

    /// <summary>
    /// True when at least one keyframe group is currently bound to
    /// <paramref name="varName"/>. Returns false for empty / null / unknown
    /// names. Case-sensitive — Visualist mirroring is expected to use the
    /// exact attribute string the Architect side reads.
    /// </summary>
    public static bool IsAnimated(string? varName)
    {
        if (string.IsNullOrEmpty(varName)) return false;
        return s_counts.TryGetValue(varName, out var n) && n > 0;
    }

    /// <summary>
    /// Mark <paramref name="varName"/> as animated (increments the
    /// reference count). Fires <see cref="AnimationStateChanged"/> with
    /// <c>true</c> on the first transition (0 → 1). Subsequent calls only
    /// bump the count.
    /// </summary>
    public static void MarkAnimated(string? varName)
    {
        if (string.IsNullOrEmpty(varName)) return;
        bool flipped = false;
        s_counts.AddOrUpdate(
            varName!,
            _ => { flipped = true; return 1; },
            (_, prev) => prev + 1);
        if (flipped) RaiseChanged(varName!, true);
    }

    /// <summary>
    /// Mark <paramref name="varName"/> as no longer animated (decrements
    /// the reference count). Fires <see cref="AnimationStateChanged"/>
    /// with <c>false</c> on the final transition (1 → 0). A name not
    /// currently in the registry is a no-op.
    /// </summary>
    public static void MarkNotAnimated(string? varName)
    {
        if (string.IsNullOrEmpty(varName)) return;
        bool flipped = false;
        s_counts.AddOrUpdate(
            varName!,
            _ => 0, // missing → stays missing (idempotent on no-op call)
            (_, prev) =>
            {
                int next = prev - 1;
                if (next <= 0)
                {
                    flipped = true;
                    return 0;
                }
                return next;
            });
        // Drop zero entries so the dictionary doesn't accumulate ghosts.
        if (s_counts.TryGetValue(varName!, out var n) && n == 0)
            s_counts.TryRemove(varName!, out _);
        if (flipped) RaiseChanged(varName!, false);
    }

    /// <summary>
    /// Clear the registry. Used by tests and by Visualist document close /
    /// reopen to flush stale entries before re-mirroring a fresh document.
    /// Fires <see cref="AnimationStateChanged"/> with <c>false</c> for
    /// every name that was previously animated so subscribers can re-paint.
    /// </summary>
    public static void Clear()
    {
        // Snapshot keys first — direct enumeration of ConcurrentDictionary
        // is allowed but mutating during the loop is not safe.
        var names = new List<string>();
        foreach (var kv in s_counts)
        {
            if (kv.Value > 0) names.Add(kv.Key);
        }
        s_counts.Clear();
        foreach (var n in names) RaiseChanged(n, false);
    }

    private static void RaiseChanged(string varName, bool isAnimated)
    {
        // Snapshot delegate list so a subscriber removing itself mid-fire
        // doesn't ConcurrentModificationException — Delegate.GetInvocationList
        // returns a copy. Per-handler try/catch so one bad subscriber
        // doesn't poison the rest.
        var handler = AnimationStateChanged;
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<string, bool>)d)(varName, isAnimated); }
            catch
            {
                // Subscribers (Architect canvas paint) own their own logging
                // path; swallow here so the tracker's broadcast contract is
                // best-effort and doesn't crash Visualist on Architect bugs.
            }
        }
    }
}
