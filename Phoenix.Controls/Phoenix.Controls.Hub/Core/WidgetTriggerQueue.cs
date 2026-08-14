using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Dispatcher invoked when a queued trigger is ready to be sent to a browser client.
    /// HubBootstrapper wires this to HUDServer.SendToLayerAsync at startup; tests inject a mock recorder.
    /// </summary>
    public delegate Task LayerDispatchDelegate(string layerId, object payload, CancellationToken ct);

    /// <summary>
    /// Clock + delay seam for the idle-loop watchdog.
    ///
    /// The watchdog (<see cref="WidgetTriggerQueue.IdleLoopWatchdogAsync"/>) polls
    /// for inactivity on a background task. In production that polling is driven by
    /// real wall-clock time and a real <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// — but that makes the re-enqueue path non-deterministic to test (the cycle
    /// fires "eventually", at the mercy of Task.Run scheduling and 500 ms timer
    /// granularity), which is exactly why the Phase 2 idle-loop test was skipped.
    ///
    /// Injecting this abstraction lets a test supply a fake that controls
    /// <see cref="UtcNow"/> manually and yields immediately from <see cref="DelayAsync"/>,
    /// and observes <see cref="NotifyCycleCompleted"/> to block until the watchdog
    /// has finished exactly one poll cycle. Production binds
    /// <see cref="SystemIdleLoopClock"/> by default, so existing callers are
    /// unaffected and behaviour is identical.
    /// </summary>
    public interface IIdleLoopClock
    {
        /// <summary>Current UTC time as observed by the idle-loop watchdog.</summary>
        DateTime UtcNow { get; }

        /// <summary>Awaitable inter-poll delay. Production = <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
        Task DelayAsync(TimeSpan delay, CancellationToken ct);

        /// <summary>
        /// Invoked by the watchdog once per completed poll cycle (after the idle
        /// check runs, whether or not a re-enqueue occurred). Production ignores
        /// it; tests use it to advance deterministically. Implementations MUST be
        /// fast / non-blocking — a slow handler delays the next poll.
        /// </summary>
        void NotifyCycleCompleted();
    }

    /// <summary>
    /// Production <see cref="IIdleLoopClock"/>: real wall-clock
    /// time and a real <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// <see cref="NotifyCycleCompleted"/> is a no-op — nothing in production
    /// observes idle-loop poll cycles. The deterministic observer lives only in
    /// the test project (FakeIdleLoopClock), so no test-only mutator leaks onto
    /// production code.
    /// </summary>
    public sealed class SystemIdleLoopClock : IIdleLoopClock
    {
        public DateTime UtcNow => DateTime.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

        public void NotifyCycleCompleted() { /* no-op in production */ }
    }

    /// <summary>
    /// WidgetTriggerQueue — per-widget FIFO of trigger invocations.
    /// Dispatches RUN_TRIGGER to the layer's WebSocket via the injected dispatcher and gates the
    /// next pump on VISUAL_COMPLETE arriving back from the browser. A hard timeout (default 10 s)
    /// frees the queue if the browser hangs or disconnects mid-trigger so successive triggers
    /// don't pile up indefinitely.
    ///
    /// B4 — the hard timeout is now per-INVOCATION, not per-queue. The browser holds a
    /// trigger for its authored timeline duration plus two dip-to-blank transitions before
    /// it acks, and an authored duration may legally reach 600 000 ms, so a single flat
    /// per-queue constant fired this queue's timeout on every timeline longer than ~10 s —
    /// logging WIDGET_TIMEOUT at System tier, broadcasting it to the debug panels, tearing
    /// down Bus's pending visual wait early and advancing the pump — while the widget was in
    /// fact rendering perfectly. The queue is per-widget and long-lived (LayerRuntime keeps one
    /// per (layer, widget) key), so a constructor value cannot vary per trigger —
    /// <see cref="EnqueueAsync"/> therefore accepts an optional override that travels with the
    /// invocation and is read at the timeout site.
    /// <see cref="LayerRuntime.ComputeCompletionTimeoutMs"/> derives it from the addressed
    /// trigger's timeline; every other caller keeps the queue default.
    ///
    /// B3 — this timeout is NOT the script's wait budget. `Async.WaitForVisual`'s TimeoutMS is
    /// a second, independent, author-owned budget that still defaults to a flat 10 000 ms
    /// (node template default, the `wait_for_visual` fallback, and
    /// Bus.TriggerVisualAndWaitAsync's default parameter), so a 20 s timeline left on that
    /// default still returns false at 10 s and takes the node's Timeout branch even though
    /// nothing here times out. Raising it is a hand edit;
    /// LayerRuntime.WarnIfScriptWaitBudgetIsShort makes the mismatch visible.
    /// </summary>
    public sealed class WidgetTriggerQueue
    {
        /// <summary>Default hard-timeout for a single trigger's VISUAL_COMPLETE return path (ms).</summary>
        public const int DefaultCompletionTimeoutMs = 10000;

        /// <summary>
        /// Upper bound on a per-invocation completion-timeout override (ms).
        ///
        /// Sanity guard, not a tuning knob: compositor.js clamps its own on-screen hold to
        /// 60 000 ms and each of its two dip-to-blank transitions to 1 000 ms, so no real
        /// render can occupy the browser for two minutes. Without the clamp a caller passing
        /// (say) int.MaxValue would wedge this widget's FIFO for ~24 days — precisely the
        /// pile-up the hard timeout exists to prevent. The only production caller
        /// (<see cref="LayerRuntime.ComputeCompletionTimeoutMs"/>) is bounded well below this,
        /// so the clamp never fires live; it fires only on a programming error, which is why
        /// it is logged once per queue rather than per trigger.
        ///
        /// B2 — COUPLING, stated here as well as at
        /// <see cref="LayerRuntime.CompletionGraceMs"/> so the two files cannot drift: that
        /// caller's worst case is the 60 000 ms hold ceiling + 2 × the 1 000 ms transition clamp
        /// + LayerRuntime.CompletionGraceMs (10 000, itself derived from compositor.js's 5 000 ms
        /// per-render stall tolerance × 2 plausibly-cold renders) = 72 000 ms. That leaves ~48 s
        /// of headroom under this ceiling. Anyone raising the grace, the hold ceiling or the
        /// transition clamp must re-check that sum against this constant — if the derived worst
        /// case ever reaches it, the clamp starts silently truncating real budgets and B4's
        /// defect returns for the longest timelines only.
        /// </summary>
        public const int MaxCompletionTimeoutMs = 120000;

        /// <summary>
        /// Phase 2 — default inactivity window before the idle-loop watchdog re-enqueues
        /// the widget's onStartup trigger. Tunable per-instance via <see cref="IdleLoopSeconds"/>.
        /// </summary>
        private const int DefaultIdleLoopSeconds = 30;

        /// <summary>
        /// Bounded-channel capacity for queued trigger invocations. Single source
        /// of truth — the channel construction, the pre-emptive head-pop watermark
        /// in <see cref="EnqueueAsync"/>, and the eviction log line all read this so
        /// they can never silently diverge if the cap is retuned.
        /// </summary>
        private const int ChannelCapacity = 256;

        private readonly string _layerId;
        private readonly string _widgetId;
        private readonly LayerDispatchDelegate _dispatch;
        private readonly LayerRegistry? _registry;
        private readonly Channel<TriggerInvocation> _channel;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private readonly int _completionTimeoutMs;

        // Clock seam for the idle-loop watchdog. Defaults to
        // the real system clock (SystemIdleLoopClock); tests inject a fake to make
        // the watchdog's poll cycle deterministic. Only the idle-loop path reads
        // this — the enqueue/evict path (cluster C) is untouched.
        private readonly IIdleLoopClock _idleLoopClock;

        // Currently in-flight invocation. The pump sets this before dispatch and clears it after
        // VISUAL_COMPLETE (or timeout). NotifyComplete reads it to resolve the right TCS.
        private TriggerInvocation? _inFlight;
        private readonly object _inFlightLock = new();

        // Serializes the pre-emptive head-pop + TryWrite sequence in
        // EnqueueAsync. Without it, two concurrent producers observing Count >= 256
        // can each TryRead a different head, then both TryWrite — the second write
        // triggers DropOldest, silently evicting an invocation whose awaiter we
        // never resolve (it hangs for the full 10s hard-timeout). Holding this
        // monitor across the pop+write makes the eviction-or-write decision atomic.
        private readonly object _enqueueLock = new();

        // B4 — 0/1 latch (Interlocked) so the MaxCompletionTimeoutMs clamp logs at most
        // once per queue instead of once per enqueue. See NormalizeTimeoutOverride.
        private int _timeoutClampLogged;

        // ──────────────────────────────────────────────────────────────────────────
        // Phase 2 — idle-loop watchdog
        //
        // When the widget queue empties and stays empty for `IdleLoopSeconds`, the
        // watchdog re-enqueues an `onStartup` trigger so the widget's idle animation
        // / state can re-bootstrap (e.g. a chat ticker that needs to keep ticking
        // even when no logic event has fired in a while).
        //
        // OPT-IN by design — defaults to FALSE so existing widgets that do NOT
        // tolerate spontaneous onStartup re-fires (one-shot intros, etc.) are not
        // surprised. <see cref="EnableIdleLoop"/> is the only switch.
        //
        // D12 — the truth about that switch, because this comment used to claim a
        // capability the product does not have: there is NO widget-preset field and
        // NO `.phxlayer` flag that turns this on (no `idleLoop` key exists anywhere
        // in Shared's models or serializer), and `EnableIdleLoop` has no production
        // caller at all — the only callers live in the test project. So in every
        // shipped build this watchdog is permanently dormant.
        //
        // It is kept because the mechanism is sound and cheap, but nothing depends
        // on it: the Hub-side revert-to-idle behaviour authors actually see is
        // implemented BROWSER-side, in compositor.js's handleRunTrigger, which after
        // holding a non-onStartup trigger for its timeline duration re-renders the
        // widget's `onStartup` trigger before acking VISUAL_COMPLETE. If a
        // .phxlayer-driven opt-in is ever wanted, wiring it is new work — do not
        // assume it already exists because this block once said so.
        // ──────────────────────────────────────────────────────────────────────────
        private volatile bool _idleLoopEnabled = false;
        private int _idleLoopSeconds = DefaultIdleLoopSeconds;
        // Plain DateTime writes are not guaranteed atomic per ECMA-335
        // (8-byte struct on a 32-bit runtime can tear). Store the ticks as a long
        // and gate writes/reads through Interlocked so the idle-loop watchdog never
        // observes a torn timestamp.
        private long _lastActivityUtcTicks = DateTime.UtcNow.Ticks;
        private Task? _idleWatchdog;

        /// <summary>True when the idle-loop watchdog is currently armed for this widget.</summary>
        public bool IsIdleLoopEnabled => _idleLoopEnabled;

        /// <summary>Inactivity window (seconds) the idle-loop waits before re-enqueueing onStartup.</summary>
        public int IdleLoopSeconds => _idleLoopSeconds;

        public WidgetTriggerQueue(string layerId, string widgetId, LayerDispatchDelegate dispatch,
            int completionTimeoutMs = DefaultCompletionTimeoutMs,
            IIdleLoopClock? idleLoopClock = null)
            : this(layerId, widgetId, dispatch, registry: null, completionTimeoutMs, idleLoopClock) { }

        public WidgetTriggerQueue(string layerId, string widgetId, LayerDispatchDelegate dispatch,
            LayerRegistry? registry,
            int completionTimeoutMs = DefaultCompletionTimeoutMs,
            IIdleLoopClock? idleLoopClock = null)
        {
            _layerId  = layerId;
            _widgetId = widgetId;
            _dispatch = dispatch;
            _registry = registry;
            // Guard against zero/negative completion timeouts — a negative
            // value flows into Task.Delay(_completionTimeoutMs, ...) in PumpAsync
            // and throws ArgumentOutOfRangeException, faulting the trigger instead
            // of timing out gracefully as the contract promises.
            if (completionTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(completionTimeoutMs), "Completion timeout must be positive.");
            _completionTimeoutMs = completionTimeoutMs;
            // Default to the real system clock; tests inject a fake.
            _idleLoopClock = idleLoopClock ?? new SystemIdleLoopClock();
            // Bounded channel so a hung dispatcher can't grow the buffer
            // indefinitely under chat-rate triggers. Wait mode means TryWrite returns
            // false on overflow; the existing fail path in EnqueueAsync surfaces an
            // InvalidOperationException to the caller. Cap chosen as a generous safety
            // net (chat is ~10/s peak; 256 buffers ~25s of queued work before
            // back-pressure surfaces).
            // Pair FullMode with the writer mode. The pump uses
            // TryWrite (a sync writer), so a Wait full-mode produces no
            // back-pressure: TryWrite returns false on overflow and the
            // previous error path threw "WidgetTriggerQueue is shut down."
            // even though the queue wasn't shut down at all — just full.
            // DropOldest is the right policy for a UI event queue: a chat
            // alert that's been queued 25 seconds behind is stale anyway,
            // so trimming the head to keep the most recent invocations in
            // flight is correct. The drop is observable via a GlobalLogger
            // line so operators can see when their triggers fall behind.
            _channel  = Channel.CreateBounded<TriggerInvocation>(
                new BoundedChannelOptions(capacity: ChannelCapacity)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });
            _pump = Task.Run(PumpAsync);
        }

        /// <summary>
        /// Opt-in the Phase 2 idle-loop watchdog for this widget. When the queue
        /// has been empty for <paramref name="inactivitySeconds"/>, an `onStartup`
        /// trigger is re-enqueued automatically. Pass any value &lt;= 0 to disable.
        /// Safe to call multiple times — re-arms with the new window.
        ///
        /// D12 — currently called from the test project only; see the Phase 2 comment
        /// block above for why no production path reaches it.
        /// </summary>
        public void EnableIdleLoop(int inactivitySeconds = DefaultIdleLoopSeconds)
        {
            if (inactivitySeconds <= 0)
            {
                _idleLoopEnabled = false;
                return;
            }
            _idleLoopSeconds  = inactivitySeconds;
            // Seed the inactivity window through the clock seam
            // so a fake clock fully controls the watchdog's notion of "now".
            Interlocked.Exchange(ref _lastActivityUtcTicks, _idleLoopClock.UtcNow.Ticks);
            _idleLoopEnabled  = true;

            // Lazy-start the watchdog pump on first opt-in. After that the same
            // task observes _idleLoopEnabled / _idleLoopSeconds via volatile reads.
            if (_idleWatchdog is null)
                _idleWatchdog = Task.Run(IdleLoopWatchdogAsync);
        }

        /// <summary>
        /// Enqueues a trigger invocation. Returns a Task that completes when VISUAL_COMPLETE
        /// arrives for that invocation (or the hard-timeout fires). The returned Task's result
        /// is `true` on real completion and `false` on timeout.
        ///
        /// Under DropOldest, TryWrite always returns true while the channel
        /// is open and silently evicts the head invocation when the buffer is full.
        /// We log the eviction via GlobalLogger so operators can see when triggers
        /// fall behind, and complete the evicted invocation's Done TCS with
        /// <c>false</c> so its awaiter sees a timeout-equivalent outcome instead of
        /// hanging. The only TryWrite-false path now is "writer completed" (Stop()
        /// has been called); in that case we fault the awaiter with a clear
        /// InvalidOperationException.
        ///
        /// B4 — <paramref name="completionTimeoutMsOverride"/> replaces the queue's own
        /// completion timeout for THIS invocation only. Pass null (the default) to keep the
        /// per-queue value, which makes every pre-existing call site byte-identical to before
        /// the override existed. A non-positive override is ignored rather than thrown on:
        /// the constructor throws for a bad timeout because that is a wiring-time programming
        /// error surfaced once, whereas an enqueue happens per chat message on a live stream,
        /// and dropping a trigger because a computed number came out wrong is strictly worse
        /// than falling back to the queue default. Values above
        /// <see cref="MaxCompletionTimeoutMs"/> are clamped.
        /// </summary>
        public Task<bool> EnqueueAsync(string triggerName, JsonElement eventData, string waitId = "",
            int? completionTimeoutMsOverride = null)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var invocation = new TriggerInvocation(
                triggerName, eventData, waitId, tcs,
                NormalizeTimeoutOverride(completionTimeoutMsOverride));

            // When the channel is at capacity, DropOldest would
            // silently evict the head invocation on the next TryWrite — but
            // Channel<T> doesn't expose the evicted item to a callback, so its
            // awaiter would hang for the full 10-second hard-timeout. Pre-fix
            // the docstring claimed "the pump's hard-timeout path catches the
            // most common case" but in fact the dropped invocation was NEVER
            // in-flight (it was sitting in the channel buffer waiting to be
            // dequeued), so the pump's timeout never fires for it.
            //
            // Fix: pop+fail the head ourselves BEFORE TryWrite when at
            // capacity. The TryRead is conditional on the Count check so we
            // only intervene when an eviction would actually happen. Resolving
            // the evicted invocation with `false` matches the timeout
            // semantics WidgetTriggerQueue's awaiters already handle
            // (TriggerVisualAndWaitAsync collapses false → return false → the
            // script's visual.trigger reports `completed=false`).
            //
            // Serialise the pop+TryWrite pair under _enqueueLock so
            // concurrent producers can't race past the cap check and have
            // DropOldest silently evict a *different* invocation between our pop
            // and our write. Inside the monitor only one producer at a time
            // observes Count, conditionally pops the head, and writes the new
            // entry — the eviction-or-write decision is now atomic per queue.
            lock (_enqueueLock)
            {
                if (_channel.Reader.Count >= ChannelCapacity && _channel.Reader.TryRead(out var evicted))
                {
                    GlobalLogger.Log(
                        $"WidgetTriggerQueue: trigger queue for {_layerId}/{_widgetId} at capacity ({ChannelCapacity}) — popping+failing head ('{evicted.TriggerName}') to make room for '{triggerName}'.",
                        "WidgetTriggerQueue", LogLevel.System);
                    evicted.Done.TrySetResult(false);
                }

                if (!_channel.Writer.TryWrite(invocation))
                {
                    // TryWrite-false under DropOldest only happens when the channel
                    // is closed (TryComplete() called). Stop() is the only caller
                    // that completes the writer.
                    tcs.TrySetException(new InvalidOperationException("WidgetTriggerQueue is shut down."));
                    return tcs.Task;
                }
            }

            Interlocked.Exchange(ref _lastActivityUtcTicks, DateTime.UtcNow.Ticks); // Phase 2 — reset idle window on each enqueue.
            return tcs.Task;
        }

        /// <summary>
        /// B4 — validates a per-invocation completion-timeout override at the ENQUEUE boundary
        /// so the pump's timeout site can trust the value it reads. Non-positive overrides
        /// collapse to null ("use the queue default"); oversized ones clamp to
        /// <see cref="MaxCompletionTimeoutMs"/> and log once per queue, because a repeat
        /// offender firing at chat rate must not turn a diagnostic into a log flood.
        /// </summary>
        private int? NormalizeTimeoutOverride(int? requested)
        {
            if (requested is not int ms) return null;
            if (ms <= 0) return null;
            if (ms <= MaxCompletionTimeoutMs) return ms;

            if (Interlocked.Exchange(ref _timeoutClampLogged, 1) == 0)
            {
                GlobalLogger.Log(
                    $"WidgetTriggerQueue: completion-timeout override {ms} ms for {_layerId}/{_widgetId} " +
                    $"exceeds the {MaxCompletionTimeoutMs} ms ceiling — clamping (logged once per queue).",
                    "WidgetTriggerQueue", LogLevel.System);
            }
            return MaxCompletionTimeoutMs;
        }

        /// <summary>
        /// Resolves the in-flight invocation if its (triggerName, waitId) matches. Idempotent —
        /// repeated calls (e.g. multiple browsers echoing the same VISUAL_COMPLETE) are no-ops.
        ///
        /// <para>V13 §8.1 — this method takes NO completion payload, on purpose, and the
        /// once-per-waitId "later ack dropped" report does NOT live here. Two reasons, and the
        /// second is the load-bearing one:</para>
        /// <list type="number">
        /// <item>The queue arbitrates the INVOCATION (may the widget's FIFO advance?), not the
        /// script's wait. It has no consumer for a payload, so accepting one would be an unused
        /// parameter on the hottest completion path.</item>
        /// <item>The report cannot be anchored to `_inFlight`. The pump's `finally` clears
        /// `_inFlight` asynchronously the moment the FIRST ack resolves the invocation's TCS, so
        /// a second socket's ack arriving microseconds later usually hits the `current is null`
        /// early-return below and is indistinguishable from an ack for a trigger that was never
        /// queued. A fan-out of eight would then log 0 or 1 lines depending purely on
        /// thread-pool scheduling. The per-waitId ledger in <c>Bus.ResolveVisualWait</c> is the
        /// one first-wins record that outlives the invocation — see the
        /// <c>_resolvedVisualWaits</c> comment in Bus.cs. Do not "consolidate" it back here.</item>
        /// </list>
        /// </summary>
        public bool NotifyComplete(string triggerName, string waitId)
        {
            TriggerInvocation? current;
            lock (_inFlightLock) current = _inFlight;
            if (current is null) return false;

            // waitId is the strong correlation token. When it's present, both
            // sides MUST match it; when it's absent, the triggerName fallback applies
            // ONLY if the in-flight invocation also has no waitId. Previously the
            // empty-waitId path was matching on triggerName alone, which routed
            // rapid identical triggers to the wrong invocation.
            if (!string.IsNullOrEmpty(waitId))
            {
                if (!string.Equals(current.WaitId, waitId, StringComparison.Ordinal))
                    return false;
            }
            else
            {
                if (!string.IsNullOrEmpty(current.WaitId)) return false;
                if (!string.Equals(current.TriggerName, triggerName, StringComparison.Ordinal))
                    return false;
            }

            return current.Done.TrySetResult(true);
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var inv in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    // Publish the invocation as in-flight BEFORE the
                    // IsLayerActive check. Previously, the check + TrySetResult(true)
                    // fast-success path ran with _inFlight still null, so a real
                    // VISUAL_COMPLETE arriving in the same window (NotifyComplete-
                    // routed) found nothing to match and was lost. With _inFlight
                    // set first, the normal matcher path can race the fast-success
                    // — and because both paths call TrySetResult on the same TCS,
                    // whichever wins, the loser is a no-op. The browser-side echo
                    // therefore wins by default whenever it's tied with the local
                    // fast-success, exactly as the sprint brief specifies.
                    lock (_inFlightLock) _inFlight = inv;

                    try
                    {
                        // H50 — re-check the registry between dequeues. A trigger that
                        // was enqueued while the layer was active can find that the
                        // layer went inactive (OBS scene flip) before its turn — fast-
                        // succeed instead of waiting the full hard timeout.
                        //
                        // Mirrors LayerRuntime.EnqueueTriggerAsync's design-time carve-out:
                        // a FIRE-AND-FORGET trigger on a layer that still has a Visualist
                        // preview attached must be dispatched, not fast-succeeded, or a
                        // queued "Test Run" is dropped here instead of at the entry gate.
                        // A trigger carrying a WaitId keeps the original behaviour — an
                        // editor socket cannot ack it (HUDServer refuses VISUAL_COMPLETE
                        // from editor clients), so dispatching it would stall the waiter.
                        bool previewOnlyFireAndForget =
                            string.IsNullOrEmpty(inv.WaitId)
                            && _registry is not null
                            && _registry.HasAnyConnection(_layerId);
                        if (_registry is not null && !_registry.IsLayerActive(_layerId) && !previewOnlyFireAndForget)
                        {
                            // Route through the same NotifyComplete
                            // matcher a real browser-side echo would use, so any
                            // tied real completion sharing this invocation's TCS
                            // wins idempotently. NotifyComplete's TrySetResult is
                            // the single commit point for both paths.
                            NotifyComplete(inv.TriggerName, inv.WaitId);
                            continue;
                        }

                        var payload = new
                        {
                            type        = "RUN_TRIGGER",
                            widgetId    = _widgetId,
                            triggerName = inv.TriggerName,
                            eventData   = inv.EventData,
                            waitId      = inv.WaitId,
                        };
                        await _dispatch(_layerId, payload, _cts.Token).ConfigureAwait(false);

                        // B4 — the invocation's own budget wins over the per-queue default.
                        // Resolved ONCE here and reused by the Task.Delay, the log line and the
                        // WIDGET_TIMEOUT payload, so those three can never disagree about which
                        // limit was actually applied (pre-fix the log and the payload both
                        // reported the flat per-queue constant regardless).
                        int effectiveTimeoutMs = inv.CompletionTimeoutMsOverride ?? _completionTimeoutMs;

                        // L63 — Task.Delay timeout disposed promptly when the wait completes
                        // by a different path. Without the linked CTS the timer ran to
                        // completion even after VISUAL_COMPLETE arrived.
                        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        var delayTask = Task.Delay(effectiveTimeoutMs, delayCts.Token);
                        var completed = await Task.WhenAny(inv.Done.Task, delayTask).ConfigureAwait(false);
                        try { delayCts.Cancel(); } catch { }
                        if (completed != inv.Done.Task)
                        {
                            // L60 — when the hard-timeout fires the previous behaviour was a
                            // single Communication-tier log line, which was easy to miss in
                            // the busy log feed. Surface this as System-tier (so it lights
                            // the dashboard error bar) AND broadcast a structured
                            // WIDGET_TIMEOUT bus message so Architect/Visualist debug panels
                            // can flag the offending widget. Both signals are best-effort —
                            // failures here MUST NOT prevent the pump from advancing.
                            GlobalLogger.Log(
                                $"WidgetTriggerQueue: trigger '{inv.TriggerName}' on {_layerId}/{_widgetId} timed out after {effectiveTimeoutMs} ms — advancing queue.",
                                "WidgetTriggerQueue", LogLevel.System);

                            try
                            {
                                var timeoutPayload = new
                                {
                                    layerId     = _layerId,
                                    widgetId    = _widgetId,
                                    triggerName = inv.TriggerName,
                                    timeoutMs   = effectiveTimeoutMs,
                                };
                                // Route through AsyncErrorBoundary — the
                                // bare _ = Bus.Instance.BroadcastAsync(...) outlier was
                                // the only WIDGET_TIMEOUT broadcast that didn't fan
                                // its faults into the standard log surface (BroadcastAsync
                                // can throw ObjectDisposedException during Stop, or a
                                // peer-send fault under reconnect churn). Peer routes
                                // in Bus.cs at LOGIC_RELOAD_ACK / MACRO_SYNC /
                                // MACRO_REMOVE / MACRO_REQUEST already wrap correctly.
                                _ = AsyncErrorBoundary.SafeRunAsync(
                                    () => Bus.Instance.BroadcastAsync(new BusMessage
                                    {
                                        Type    = "WIDGET_TIMEOUT",
                                        Source  = "Hub",
                                        Target  = "*",
                                        Payload = JsonSerializer.Serialize(timeoutPayload),
                                    }),
                                    "WidgetTriggerQueue",
                                    $"WIDGET_TIMEOUT broadcast for {_layerId}/{_widgetId}");
                            }
                            catch (Exception broadcastEx)
                            {
                                // Tests / early-startup paths may not have an initialised bus —
                                // Bus.Instance accessor itself can throw on partial init.
                                System.Diagnostics.Debug.WriteLine(
                                    $"[WidgetTriggerQueue] WIDGET_TIMEOUT broadcast failed: {broadcastEx.Message}");
                            }

                            // Reciprocal cleanup so Bus._pendingWaits doesn't leak the
                            // entry when the browser silently disconnects. Bus's WaitForVisualAsync
                            // also TryRemoves on its WhenAny exit, but if the originating call site
                            // discarded the queueTask or the broadcast threw before the WhenAny was
                            // wired up, only this hook frees the slot.
                            if (!string.IsNullOrEmpty(inv.WaitId))
                            {
                                try { Bus.Instance.CancelPendingVisualWait(inv.WaitId); }
                                catch { /* bus may not be initialised in tests */ }
                            }

                            inv.Done.TrySetResult(false);
                        }
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        inv.Done.TrySetCanceled();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("WidgetTriggerQueue", $"dispatch failed for {_layerId}/{_widgetId}/{inv.TriggerName}", ex);
                        inv.Done.TrySetException(ex);
                    }
                    finally
                    {
                        lock (_inFlightLock) _inFlight = null;
                        Interlocked.Exchange(ref _lastActivityUtcTicks, DateTime.UtcNow.Ticks); // Phase 2 — fresh idle window after each completion.
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }

        /// <summary>
        /// Phase 2 — idle-loop watchdog. Polls inactivity and re-enqueues
        /// `onStartup` once the queue has been quiet for <see cref="IdleLoopSeconds"/>.
        /// Started lazily on first <see cref="EnableIdleLoop"/> call; gated by the
        /// volatile <see cref="_idleLoopEnabled"/> flag so opt-out is immediate.
        /// </summary>
        private async Task IdleLoopWatchdogAsync()
        {
            // Poll at a coarse cadence — the watchdog only needs to react within
            // a second or two of the deadline, not millisecond-precise. Tests can
            // use a small IdleLoopSeconds to drive the path quickly.
            //
            // All time reads and the inter-poll wait go through
            // _idleLoopClock so a fake clock can drive the cycle deterministically.
            // The production clock is the real system clock, so behaviour is
            // identical to before this seam was introduced.
            var pollInterval = TimeSpan.FromMilliseconds(500);
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _idleLoopClock.DelayAsync(pollInterval, _cts.Token).ConfigureAwait(false);

                    // Run one poll cycle, then signal completion exactly once. The
                    // try/finally guarantees NotifyCycleCompleted fires on EVERY
                    // cycle regardless of which short-circuit (`continue`) the body
                    // takes — that's the deterministic release point a fake clock's
                    // WaitForNextCycleAsync subscribes to. Production's notifier is
                    // a no-op, so this costs nothing live.
                    try
                    {
                        if (!_idleLoopEnabled) continue;

                        // Skip if there's anything in flight or queued — the queue is
                        // not idle and the per-completion timestamp will refresh.
                        if (_channel.Reader.Count > 0) continue;
                        bool busy;
                        lock (_inFlightLock) busy = _inFlight is not null;
                        if (busy) continue;

                        var lastUtc = new DateTime(Interlocked.Read(ref _lastActivityUtcTicks), DateTimeKind.Utc);
                        var idleFor = _idleLoopClock.UtcNow - lastUtc;
                        if (idleFor.TotalSeconds < _idleLoopSeconds) continue;

                        // Quiet for long enough — re-arm the timestamp BEFORE enqueueing
                        // so we don't double-fire while the trigger is still in flight.
                        Interlocked.Exchange(ref _lastActivityUtcTicks, _idleLoopClock.UtcNow.Ticks);

                        try
                        {
                            var emptyEvent = JsonDocument.Parse("{}").RootElement.Clone();
                            // Wrap so a faulted enqueue (channel full / closed) lands
                            // in GlobalLogger via the boundary instead of disappearing.
                            _ = AsyncErrorBoundary.SafeRunAsync(
                                () => EnqueueAsync("onStartup", emptyEvent),
                                "WidgetTriggerQueue", $"idle-loop re-enqueue {_layerId}/{_widgetId}");
                        }
                        catch (Exception ex)
                        {
                            GlobalLogger.Error("WidgetTriggerQueue",
                                $"idle-loop re-enqueue failed for {_layerId}/{_widgetId}", ex);
                        }
                    }
                    finally
                    {
                        // Signal end-of-cycle. Production: no-op. Tests: releases the
                        // next WaitForNextCycleAsync awaiter so they advance in lockstep.
                        _idleLoopClock.NotifyCycleCompleted();
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }

        public void Stop()
        {
            _idleLoopEnabled = false; // Phase 2 — silence the watchdog before tearing down.
            try { _cts.Cancel(); } catch { }
            _channel.Writer.TryComplete();

            // Fault any still-pending invocations so awaiters don't hang forever.
            while (_channel.Reader.TryRead(out var inv))
                inv.Done.TrySetCanceled();
            lock (_inFlightLock) _inFlight?.Done.TrySetCanceled();

            // The _pump / _idleWatchdog tasks capture
            // _cts.Token. Previously Stop() disposed _cts immediately after Cancel()
            // without waiting for those tasks to observe cancellation and exit — a
            // use-after-free race where a still-running task touches the disposed CTS.
            // Stop() is sync void (callers rely on that), so we do a best-effort bounded
            // wait: each Wait is capped at 2s so a wedged task can't hang shutdown, and
            // the try/catch swallows the AggregateException a cancelled/faulted task
            // surfaces. After this the tasks have unwound before we dispose the CTS.
            try { _pump?.Wait(2000); }         catch { /* cancelled/faulted — expected */ }
            try { _idleWatchdog?.Wait(2000); } catch { /* cancelled/faulted — expected */ }

            // Dispose the CTS we own. Without this the linked source +
            // registration handle leak across the queue's lifetime.
            try { _cts.Dispose(); } catch { }
        }

        /// <summary>
        /// B4 — <see cref="CompletionTimeoutMsOverride"/> is the per-invocation completion
        /// budget in ms, or null to use the queue's own <c>_completionTimeoutMs</c>. Already
        /// validated + clamped by <see cref="NormalizeTimeoutOverride"/> at enqueue time, so
        /// the pump can hand it straight to Task.Delay. The record is private + nested, so
        /// widening it is an internal change with no public surface.
        /// </summary>
        private sealed record TriggerInvocation(
            string TriggerName,
            JsonElement EventData,
            string WaitId,
            TaskCompletionSource<bool> Done,
            int? CompletionTimeoutMsOverride = null);
    }
}
