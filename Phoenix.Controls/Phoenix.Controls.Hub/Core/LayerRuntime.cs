using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// LayerRuntime — orchestrates per-widget trigger queues for all loaded layers.
    /// Receives VISUAL_TRIGGER messages from Bus and routes them into
    /// the correct WidgetTriggerQueue, which in turn dispatches RUN_TRIGGER to
    /// the browser via HUDServer. The browser reports back via VISUAL_COMPLETE,
    /// which HUDServer parses and forwards to <see cref="NotifyTriggerComplete"/>.
    /// </summary>
    public sealed class LayerRuntime
    {
        // Same systemic ??= race that plagues ScriptManager / DB /
        // LayerRegistry. LayerRuntime first-touch happens from HubBootstrapper wiring
        // AND from VISUAL_TRIGGER bus handlers that fire before the bootstrapper has
        // finished; concurrent paths must serialize.
        private static LayerRuntime? _instance;
        private static readonly object _instanceLock = new();
        public static LayerRuntime Instance
        {
            get
            {
                var inst = _instance;
                if (inst != null) return inst;
                lock (_instanceLock)
                {
                    return _instance ??= new LayerRuntime();
                }
            }
        }

        private readonly Dictionary<(string LayerId, string WidgetId), WidgetTriggerQueue> _queues = new();
        private readonly object _lock = new();

        /// <summary>
        /// Per-trigger dispatcher. HubBootstrapper sets this to HUDServer.SendToLayerAsync after
        /// the HUDServer instance is constructed. Tests inject a recording mock.
        ///
        /// L62 — setter is `internal` so production code can't accidentally swap the dispatcher
        /// at runtime; tests reach it through `[InternalsVisibleTo("Phoenix.Controls.Tests")]`.
        /// </summary>
        public LayerDispatchDelegate? Dispatcher { get; internal set; }

        /// <summary>
        /// Underlying registry instance — defaults to the singleton; tests can swap.
        /// L62 — setter is `internal` for the same reason as <see cref="Dispatcher"/>.
        /// </summary>
        public LayerRegistry Registry { get; internal set; } = LayerRegistry.Instance;

        public LayerRuntime() { }

        /// <summary>
        /// L62 — test seam. Resets the singleton instance and clears the per-runtime
        /// state so test classes that exercise the singleton (rare — most tests construct
        /// their own LayerRuntime) start from a clean slate. Production code must never
        /// call this.
        /// </summary>
        internal static void ResetForTesting()
        {
            var existing = _instance;
            if (existing is not null)
            {
                try { existing.Stop(); } catch { }
                existing.Dispatcher = null;
                existing.Registry   = LayerRegistry.Instance;
            }
            _instance = null;
        }

        /// <summary>
        /// Enqueues a trigger for the addressed widget. Returns a Task whose result is
        /// `true` when VISUAL_COMPLETE returns within this invocation's hard timeout, or `false`
        /// on timeout. Inactive layers (no live /hud/&lt;id&gt; clients) short-circuit to
        /// `true` immediately so scripts running in unattended mode don't block.
        ///
        /// B4 — the hard timeout is derived per trigger by
        /// <see cref="ComputeCompletionTimeoutMs"/> from the addressed trigger's timeline, so a
        /// long authored timeline is no longer mis-reported as a WIDGET_TIMEOUT.
        ///
        /// B3 — <paramref name="scriptWaitTimeoutMs"/> is the caller's OWN wait budget: the
        /// author's `Async.WaitForVisual` TimeoutMS, which `wait_for_visual` passes to
        /// Bus.TriggerVisualAndWaitAsync as its `timeoutMs` and which that method forwards HERE.
        /// It does not change any behaviour; it exists so
        /// <see cref="WarnIfScriptWaitBudgetIsShort"/> can report the case where the author's
        /// budget is shorter than the time this trigger provably needs.
        ///
        /// Null (the default) skips the check. The two fire-and-forget dispatch paths in Bus
        /// (DispatchVisualTrigger and the VISUAL_TRIGGER bus handler) leave it null on purpose —
        /// nobody is waiting on those, so there is no budget to compare against. The waiting path
        /// is the one that passes it, which is what makes this diagnostic reachable in production
        /// and not only from tests.
        /// </summary>
        public Task<bool> EnqueueTriggerAsync(string layerId, string widgetId, string triggerName,
            JsonElement eventData, string waitId = "", int? scriptWaitTimeoutMs = null)
        {
            // P1-6 — Debug-build assertion that the dispatcher was wired before any
            // VISUAL_TRIGGER routes hit the runtime. Production keeps the
            // log+return below so a regression doesn't crash a live stream, but in
            // Debug builds we fail loudly so the bring-up bug (where Bus.StartAsync
            // ran before HubBootstrapper assigned the dispatcher) cannot regress
            // silently.
#if DEBUG
            if (Dispatcher is null)
            {
                throw new InvalidOperationException(
                    $"LayerRuntime.Dispatcher is null at EnqueueTriggerAsync entry for layer '{layerId}'/widget '{widgetId}' — bootstrap order regression.");
            }
#endif

            var layer = Registry.GetLayer(layerId);
            if (layer is null)
            {
                return Task.FromResult(false);
            }

            // B4 — capture the widget rather than a found/not-found bool. This walk was
            // already here; the LayerWidget it discards is exactly what the per-invocation
            // completion-timeout derivation needs (its TransitionMs, and the addressed
            // trigger's timeline), so there is no second lookup.
            LayerWidget? widget = null;
            foreach (var w in layer.Widgets) { if (w.Id == widgetId) { widget = w; break; } }
            if (widget is null)
            {
                return Task.FromResult(false);
            }

            // Inactive-layer short-circuit: nothing to render against, succeed immediately so
            // wait_for_visual doesn't stall scripts when OBS scenes are hidden.
            //
            // ── Design-time carve-out: a preview pane IS a render target ──────────────
            // "Active" is PRODUCTION presence (V6), so a layer with only a Visualist preview
            // attached is inactive and every design-time "Test Run" was silently discarded —
            // and because this short-circuit is a fast-SUCCEED, Visualist still reported
            // "Test Run sent". Admit an editor-only layer, but ONLY for a fire-and-forget
            // trigger.
            //
            // ★ The waitId is the discriminator, NOT the socket kind. HUDServer refuses
            // VISUAL_COMPLETE from editor sockets, so a trigger carrying a waitId dispatched
            // into a preview-only layer could never be acked: wait_for_visual would stall to
            // its timeout instead of returning instantly here. That would be strictly worse
            // than the bug being fixed, so a waiting trigger keeps the original behaviour.
            if (!Registry.IsLayerActive(layerId))
            {
                bool previewOnlyFireAndForget =
                    string.IsNullOrEmpty(waitId) && Registry.HasAnyConnection(layerId);
                if (!previewOnlyFireAndForget)
                    return Task.FromResult(true);
            }

            // P2-TOCTOU — cache the dispatcher once so the null-check below and the
            // queue construction inside the lock operate on the *same* reference.
            // Reading Dispatcher twice (here and at queue creation) lets a concurrent
            // ResetForTesting / re-wire null it between check and use, which would
            // hand the new WidgetTriggerQueue a null dispatcher that crashes on
            // first invocation.
            LayerDispatchDelegate? dispatcher = Dispatcher;
            if (dispatcher is null)
            {
                GlobalLogger.Log(
                    $"LayerRuntime: dispatcher not configured; trigger '{triggerName}' for {layerId}/{widgetId} dropped.",
                    "LayerRuntime", LogLevel.CriticalError);
                return Task.FromResult(false);
            }

            WidgetTriggerQueue queue;
            lock (_lock)
            {
                var key = (layerId, widgetId);
                if (!_queues.TryGetValue(key, out queue!))
                {
                    // H50 — queue carries the LayerRegistry reference so the pump can
                    // re-check IsLayerActive between dequeues and fast-fail any backlog
                    // accumulated while the layer was visible but is now hidden.
                    queue = new WidgetTriggerQueue(layerId, widgetId, dispatcher, Registry);
                    _queues[key] = queue;
                }
            }

            int? completionTimeoutMs = ComputeCompletionTimeoutMs(widget, triggerName);

            // B3 — compare the author's own wait budget against what this trigger actually needs
            // while both numbers are in hand. Placed AFTER the inactive-layer short-circuit on
            // purpose: with no browser attached nothing renders and nothing is mis-timed, so a
            // hidden OBS scene must not generate log noise.
            if (scriptWaitTimeoutMs is int scriptMs)
                WarnIfScriptWaitBudgetIsShort(layerId, widgetId, triggerName, scriptMs, completionTimeoutMs);

            return queue.EnqueueAsync(triggerName, eventData, waitId, completionTimeoutMs);
        }

        // B3 — one-shot latch for the script-wait-budget mismatch diagnostic, keyed by
        // (layer, widget, trigger). Bounded by construction: a key is only ever recorded after
        // ComputeCompletionTimeoutMs has RESOLVED the trigger on a registered widget of a
        // registered layer, so the key space is exactly the authored (layer, widget, trigger)
        // set — the same order of magnitude as _queues — and an arbitrary script-supplied
        // trigger name can never add an entry. Guarded by _lock, which already serialises
        // _queues, so no extra lock ordering is introduced.
        private readonly HashSet<(string LayerId, string WidgetId, string TriggerName)> _shortWaitBudgetWarned = new();

        /// <summary>
        /// B3 — reports, once per (layer, widget, trigger), that the SCRIPT's visual-wait budget
        /// is shorter than the completion timeout this trigger's timeline actually needs.
        ///
        /// Why a diagnostic and NOT a floor: `Async.WaitForVisual`'s TimeoutMS is the author's
        /// number. "Give up after 3 s even though the animation runs for 20" is a legitimate
        /// thing to author, and silently raising it to the derived value would override an
        /// explicit intent with no trace — the worse failure of the two. So this only makes the
        /// mismatch discoverable; the author still owns the fix (raise TimeoutMS by hand).
        ///
        /// Latched per key because the alternative is a log line per chat message on a live
        /// stream — the same reason WidgetTriggerQueue latches its clamp warning.
        /// </summary>
        private void WarnIfScriptWaitBudgetIsShort(string layerId, string widgetId, string triggerName,
            int scriptWaitTimeoutMs, int? completionTimeoutMs)
        {
            // No derived budget means the trigger didn't resolve, and compositor.js answers that
            // with an immediate ack — there is nothing for the author's wait to out-live.
            if (completionTimeoutMs is not int needed) return;
            // A non-positive budget is not an author intent this can meaningfully compare
            // against; ScriptManager's own parse fallback already substitutes a default there.
            if (scriptWaitTimeoutMs <= 0) return;
            if (scriptWaitTimeoutMs >= needed) return;

            lock (_lock)
            {
                if (!_shortWaitBudgetWarned.Add((layerId, widgetId, triggerName))) return;
            }

            GlobalLogger.Log(
                $"LayerRuntime: '{triggerName}' on {layerId}/{widgetId} needs about {needed} ms to finish " +
                $"(authored timeline + transitions + render slack), but the script's visual wait is set to " +
                $"{scriptWaitTimeoutMs} ms. The wait will report a timeout and take the Timeout branch while the " +
                $"widget renders correctly — raise TimeoutMS on the Async.WaitForVisual node to at least {needed}. " +
                $"(Logged once per layer/widget/trigger.)",
                "LayerRuntime", LogLevel.System);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // B4 — per-invocation completion timeout
        //
        // WidgetTriggerQueue's hard timeout used to be a flat 10 000 ms per queue, but the
        // browser keeps a trigger in flight for far longer than that on purpose: after the
        // render it holds the frame on screen for the trigger's AUTHORED timeline duration
        // (Visualist clamps that field to [0, 600 000] ms) and only then reverts to
        // `onStartup` and acks VISUAL_COMPLETE. So every timeline beyond ~10 s tripped the
        // QUEUE's hard timeout: a System-tier WIDGET_TIMEOUT log line, a WIDGET_TIMEOUT bus
        // broadcast to the debug panels, an early teardown of Bus's pending visual wait, and
        // the pump advancing to the next trigger while the browser was still holding this one
        // — all while the widget rendered perfectly. The numbers below mirror compositor.js's
        // own hold arithmetic so the two halves of the handshake agree instead of the Hub half
        // guessing.
        //
        // B3 — SCOPE, because the two budgets are easy to conflate and only ONE of them is
        // derived here. What this derivation governs is the queue's hard timeout and the
        // WIDGET_TIMEOUT signal, nothing else. `Async.WaitForVisual`'s `TimeoutMS` is a
        // SECOND, independent, author-owned budget that still defaults to a flat 10 000 ms in
        // three places — the node template default (NodeRegistry.Templates.LogicAsync.cs), the
        // `wait_for_visual` command's fallback (ScriptManager.Wait.cs), and
        // Bus.TriggerVisualAndWaitAsync's default parameter. A 20 s timeline left on that
        // default therefore STILL returns false at 10 s, takes the node's Timeout branch and
        // logs a CriticalError while the widget renders correctly. Raising it is a hand edit
        // the author must make; <see cref="WarnIfScriptWaitBudgetIsShort"/> exists so the
        // mismatch is at least visible instead of silent.
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Browser's per-render stall tolerance (ms) — mirrors compositor.js's
        /// `LIVE_PASS_WIDGET_TIMEOUT_MS`. That is the point at which the browser stops WAITING
        /// on one widget's render and calls it stalled; below it, a slow render is simply a
        /// slow render and the browser expects to finish it.
        /// </summary>
        internal const int BrowserRenderStallToleranceMs = 5000;

        /// <summary>
        /// How many of the renders that happen OUTSIDE the measured hold may plausibly be cold.
        ///
        /// compositor.js's RUN_TRIGGER path performs FOUR `renderWidgetTrigger` calls that the
        /// hold arithmetic does not cover: `renderWithTransition` paints the new content and
        /// then does a second "final live render" after the dip, and `handleRunTrigger` runs
        /// that whole pair twice (once swapping the trigger in, once reverting to `onStartup`).
        /// Two of the four can genuinely await a cold image/video decode — the trigger's own
        /// first paint and `onStartup`'s first paint after the revert. The other two re-render
        /// content that is already decoded, so they are not budgeted as cold.
        /// </summary>
        internal const int ColdRenderAllowance = 2;

        /// <summary>
        /// Slack added on top of the browser's own hold, in ms — DERIVED, not guessed:
        /// <see cref="ColdRenderAllowance"/> × <see cref="BrowserRenderStallToleranceMs"/>.
        ///
        /// The previous 2 000 ms was smaller than the browser's own tolerance for a SINGLE slow
        /// render, so the defect B4 set out to remove survived in a narrower form: a 20 s
        /// timeline with a 3 s cold decode still timed out. Sizing the grace off the same
        /// 5 000 ms constant compositor.js uses means the Hub half cannot be stricter than the
        /// browser half about what counts as "still working".
        ///
        /// This is pure headroom on a timeout whose only job is unwedging a DEAD browser, so
        /// erring long costs a wedged widget a later unblock and nothing else, while erring
        /// short is the bug. The worst case remains bounded: 60 000 ms hold ceiling + 2 × the
        /// 1 000 ms transition clamp + this grace = 72 000 ms, comfortably inside
        /// <see cref="WidgetTriggerQueue.MaxCompletionTimeoutMs"/> (120 000). If any of those
        /// three is retuned, re-check that sum — the ceiling comment in WidgetTriggerQueue.cs
        /// carries the same arithmetic so the two files cannot drift apart unnoticed.
        /// </summary>
        internal const int CompletionGraceMs = ColdRenderAllowance * BrowserRenderStallToleranceMs;

        /// <summary>Browser's substitute hold for an unset/zero/non-finite duration (ms). Mirrors compositor.js.</summary>
        internal const double BrowserMinHoldMs = 2000;

        /// <summary>Browser's upper clamp on the on-screen hold (ms). Mirrors compositor.js.</summary>
        internal const double BrowserMaxHoldMs = 60000;

        /// <summary>
        /// Derives the completion-timeout override for one invocation, or null to keep
        /// <see cref="WidgetTriggerQueue.DefaultCompletionTimeoutMs"/>.
        ///
        /// Returns null when the addressed trigger does not exist on the widget: compositor.js
        /// answers that case with an immediate `unknown_trigger` diagnostic + ack, so there is
        /// no authored duration to measure and nothing to be gained by moving the budget.
        /// Keeping null there is also what makes every existing caller's behaviour unchanged.
        ///
        /// The result is floored at the historical flat default. The defect this fixes is only
        /// ever "the budget was too SMALL"; handing some trigger a tighter budget than it had
        /// yesterday would trade a fixed bug for a fresh one (a spurious WIDGET_TIMEOUT on a
        /// slow first decode), so the derivation may only ever add headroom.
        ///
        /// B2 — with the grace now derived from the browser's own per-render stall tolerance,
        /// that floor no longer BINDS for any reachable input: the smallest possible sum is the
        /// 2 000 ms minimum hold + 0 transition + 10 000 ms grace = 12 000 ms. It is kept
        /// deliberately as an invariant rather than deleted as dead code — it is what makes
        /// "never tighter than before B4" true by construction no matter how the three
        /// constants above are later retuned, and it costs one comparison per trigger.
        /// </summary>
        internal static int? ComputeCompletionTimeoutMs(LayerWidget widget, string triggerName)
        {
            var trigger = FindTrigger(widget, triggerName);
            if (trigger is null) return null;

            var timeline = trigger.Timeline;

            // DurationMs is the AUTHORED duration and is NOT derived from the keyframes.
            // Only save-time guards exist for it, so a hand-edited / corrupted .phxlayer can
            // carry NaN or Infinity — guard before arithmetic, exactly as compositor.js does
            // with its isFinite check.
            double effectiveMs = timeline is null ? 0 : timeline.DurationMs;
            if (!double.IsFinite(effectiveMs)) effectiveMs = 0;

            // A keyframe past the authored duration still animates (V5 part A makes the
            // production clock advance), so it extends what "finished" means. Read the cached,
            // self-invalidating SortedKeyframes projection — never Keyframes.Max(...), which
            // would re-sort on every trigger and would not filter NaN/Infinity times.
            if (timeline is not null)
            {
                var sorted = timeline.SortedKeyframes;
                if (sorted.Count > 0)
                {
                    // SortedKeyframes is already NaN/Infinity-filtered, so the last element's
                    // time is finite by construction; the guard is belt-and-braces against a
                    // future change to that projection.
                    double lastKeyframeMs = sorted[sorted.Count - 1].TimeMs;
                    if (double.IsFinite(lastKeyframeMs) && lastKeyframeMs > effectiveMs)
                        effectiveMs = lastKeyframeMs;
                }
            }

            // Mirror compositor.js: a non-finite / non-positive duration becomes the minimum
            // hold, and anything larger than the ceiling is clamped there.
            double holdMs = effectiveMs;
            if (!(holdMs > 0))            holdMs = BrowserMinHoldMs;
            if (holdMs > BrowserMaxHoldMs) holdMs = BrowserMaxHoldMs;

            // TransitionMs counts TWICE because the browser dips twice per non-onStartup
            // trigger: once swapping the trigger's content in, once reverting to onStartup.
            // The property's setter clamps to [0, 1000], so it is finite here by construction.
            //
            // onStartup itself pays only one dip and no hold, so this figure is generous for
            // it. That is deliberate: one derivation, and erring long only costs a wedged
            // widget a slightly later timeout, whereas erring short is the bug being fixed.
            double totalMs = holdMs + (2d * widget.TransitionMs) + CompletionGraceMs;

            int computed = totalMs >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(totalMs);
            return computed < WidgetTriggerQueue.DefaultCompletionTimeoutMs
                ? WidgetTriggerQueue.DefaultCompletionTimeoutMs
                : computed;
        }

        /// <summary>
        /// Resolves the trigger the browser will actually run, mirroring compositor.js's
        /// `findTrigger`: exact name across the whole list first, then the `onTrigger:`-prefixed
        /// form. Resolving it any other way would budget for a different timeline than the one
        /// that renders.
        ///
        /// B1 — both passes skip null ELEMENTS, not just a null list. `List&lt;WidgetTrigger&gt;`
        /// is declared non-nullable but `triggers: [null]` in a hand-edited / corrupted
        /// `.phxlayer` deserialises to a real hole, and LayerSerializer.Deserialize's own
        /// migration loop HEALS AROUND such an element (`if (trigger is null) continue;`) rather
        /// than removing it — so a layer legitimately reaches this runtime carrying one. Without
        /// the guard, `t.Name` throws, and on the TriggerVisualAndWaitAsync path
        /// <see cref="EnqueueTriggerAsync"/> is not async, so the NRE would propagate
        /// SYNCHRONOUSLY out through the `wait_for_visual` command and abort the whole script on
        /// every run — a crash HEAD did not have, since HEAD never walked the trigger list here.
        /// Same skip LayerSerializer already uses; no behaviour change for well-formed layers.
        /// </summary>
        private static WidgetTrigger? FindTrigger(LayerWidget widget, string triggerName)
        {
            var triggers = widget.Triggers;
            if (triggers is null || triggers.Count == 0 || string.IsNullOrEmpty(triggerName))
                return null;

            foreach (var t in triggers)
                if (t is not null && string.Equals(t.Name, triggerName, StringComparison.Ordinal)) return t;

            string prefixed = "onTrigger:" + triggerName;
            foreach (var t in triggers)
                if (t is not null && string.Equals(t.Name, prefixed, StringComparison.Ordinal)) return t;

            return null;
        }

        /// <summary>
        /// Forwards an inbound VISUAL_COMPLETE to the matching widget queue and resolves any
        /// Bus pending wait keyed by waitId. Idempotent — multiple browser clients
        /// echoing the same completion are absorbed via TrySetResult.
        ///
        /// <para>V13 §8.1 — <paramref name="payload"/> is the browser's optional
        /// <c>Visual.Complete → Payload</c> string, already stripped to <c>null</c> by
        /// HUDServer when the acking socket is Untrusted (§8.3). It is forwarded ONLY to
        /// <see cref="Bus.ResolveVisualWait"/>: that is where the pending wait lives, and its
        /// atomic <c>TryRemove</c> is the first-ack-wins decision that selects which of N
        /// browser sources' payloads the script sees.</para>
        ///
        /// <para><b>Why the payload does not ride the queue.</b>
        /// <see cref="WidgetTriggerQueue.NotifyComplete"/> arbitrates the INVOCATION (does this
        /// ack free the widget's FIFO?), not the WAIT, and it has no consumer for a payload —
        /// passing one in would be an unused parameter on the hottest completion path. Its own
        /// idempotency is unchanged and still the gate for the queue side; see the
        /// <c>_resolvedVisualWaits</c> comment in Bus.cs for why the one-per-waitId collision
        /// report is anchored at the wait rather than at the invocation.</para>
        /// </summary>
        public void NotifyTriggerComplete(string layerId, string widgetId, string triggerName, string waitId,
            string? payload = null)
        {
            WidgetTriggerQueue? queue;
            lock (_lock)
            {
                _queues.TryGetValue((layerId, widgetId), out queue);
            }

            // ★ V13 — ORDER IS LOAD-BEARING: resolve the WAIT before advancing the QUEUE.
            //
            // Bus.TriggerVisualAndWaitWithPayloadAsync awaits
            // Task.WhenAny(waitTcs, queueTask, delayTask), and one browser ack completes BOTH
            // of the first two — the wait through ResolveVisualWait, the queue through
            // NotifyComplete's TrySetResult. Only the wait's envelope carries the completion
            // payload. With the pre-V13 order (queue first) WhenAny's winner was whichever
            // continuation the thread pool happened to run first, biased toward the
            // payload-LESS queueTask because its continuation was queued first — so the
            // payload would have been dropped most of the time, nondeterministically, and a
            // test for it would have been flaky rather than red.
            //
            // Resolving the wait first makes the waiter's rule airtight instead of likely:
            // if WhenAny returned at all, and a browser ack was the cause, then the wait's
            // TCS is ALREADY completed by program order on this thread — so the waiter can
            // treat a completed wait TCS as authoritative no matter which task WhenAny
            // picked. That check lives in Bus and names this ordering; the two must move
            // together.
            //
            // Nothing else depends on the old order: both resolutions are idempotent
            // TrySetResult calls, and the queue's pump advances a dictionary-lookup later.
            if (!string.IsNullOrEmpty(waitId))
                Bus.Instance.ResolveVisualWait(waitId, payload);

            queue?.NotifyComplete(triggerName, waitId);
        }

        public void Stop()
        {
            // L56 — collect the layer IDs we know about *before* clearing so we can
            // unregister their browser-connection presence from the LayerRegistry.
            // Without this, restarting Hub leaves stale `IsLayerActive == true` for
            // every layer that had a queue, and the inactive-layer fast-succeed in
            // EnqueueTriggerAsync mis-fires forever after.
            HashSet<string> layerIds;
            List<WidgetTriggerQueue> queuesToStop;
            lock (_lock)
            {
                // P1-30 — avoid the LINQ Select + ToHashSet allocation chain
                // (delegate + iterator + intermediate enumerable). Stop() runs
                // during shutdown but also from ResetForTesting; either way the
                // queue dictionary may be large enough that the savings matter,
                // and an explicit foreach is just as readable.
                layerIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in _queues.Keys) layerIds.Add(key.LayerId);
                // L57 — collect the queues and clear the dictionary while holding
                // the lock, but call Stop() OUTSIDE the lock below. Each Stop()
                // blocks up to ~4s on task waits; holding _lock across that span
                // would stall every concurrent EnqueueTriggerAsync (which also
                // takes _lock), cascading a freeze through trigger dispatch during
                // shutdown. Mirrors the DiscardLayer pattern.
                queuesToStop = new List<WidgetTriggerQueue>(_queues.Values);
                _queues.Clear();
                // B3 — drop the diagnostic latches with the queues they describe, so a Hub
                // restart (and ResetForTesting, which routes through here) starts from a clean
                // slate rather than inheriting "already warned" state for a layer set that is
                // about to be reloaded from disk.
                _shortWaitBudgetWarned.Clear();
            }

            foreach (var q in queuesToStop) try { q.Stop(); } catch { }

            // Stop is best-effort — registry mutation must not throw out of teardown.
            try
            {
                foreach (var layerId in layerIds)
                {
                    try
                    {
                        foreach (var ws in Registry.GetConnections(layerId))
                        {
                            try { Registry.UnregisterConnection(layerId, ws); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// H49 — explicit teardown for a widget that's been removed from a layer.
        /// LayerWatcher / Visualist edits invoke this when a widget id disappears
        /// from a `.phxlayer`. Without it, the per-widget channel pump remained
        /// blocked on ReadAllAsync forever.
        /// </summary>
        public void DiscardWidget(string layerId, string widgetId)
        {
            WidgetTriggerQueue? toStop = null;
            lock (_lock)
            {
                if (_queues.Remove((layerId, widgetId), out var q)) toStop = q;
                // B3 — a widget that disappears and comes back has been re-authored, so its
                // timeline may no longer out-run the script's wait (or may now out-run it by
                // more). Forget the latch so the next enqueue re-evaluates instead of staying
                // silent about a budget that changed.
                _shortWaitBudgetWarned.RemoveWhere(
                    k => k.LayerId == layerId && k.WidgetId == widgetId);
            }
            try { toStop?.Stop(); } catch { }
        }

        /// <summary>
        /// Drops every queue for a given layer (e.g. when a `.phxlayer` is deleted).
        /// Without this, queued triggers for a now-gone layer kept their
        /// channel pumps alive.
        /// </summary>
        public void DiscardLayer(string layerId)
        {
            List<WidgetTriggerQueue> toStop = new();
            lock (_lock)
            {
                var keys = new List<(string, string)>();
                foreach (var kv in _queues)
                    if (kv.Key.LayerId == layerId)
                    {
                        keys.Add(kv.Key);
                        toStop.Add(kv.Value);
                    }
                foreach (var k in keys) _queues.Remove(k);
                // B3 — same reasoning as DiscardWidget: LayerWatcher calls this on every
                // `.phxlayer` change, so keeping the latch would permanently silence the
                // diagnostic for a layer whose timelines the author is actively editing.
                _shortWaitBudgetWarned.RemoveWhere(k => k.LayerId == layerId);
            }
            foreach (var q in toStop) try { q.Stop(); } catch { }
        }

        /// <summary>L56 — diagnostic snapshot of live (layer, widget) keys.</summary>
        public IReadOnlyCollection<(string LayerId, string WidgetId)> ActiveQueues()
        {
            // P1-30 — replace ToList() (which sizes via the underlying ICollection
            // counter but still allocates a List<T> wrapper + delegate chain) with
            // a pre-sized List the foreach pushes into directly. The lock is held
            // for the snapshot duration either way.
            lock (_lock)
            {
                var snapshot = new List<(string, string)>(_queues.Count);
                foreach (var key in _queues.Keys) snapshot.Add(key);
                return snapshot;
            }
        }
    }
}
