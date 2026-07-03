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
        /// `true` when VISUAL_COMPLETE returns within the queue's hard timeout, or `false`
        /// on timeout. Inactive layers (no live /hud/&lt;id&gt; clients) short-circuit to
        /// `true` immediately so scripts running in unattended mode don't block.
        /// </summary>
        public Task<bool> EnqueueTriggerAsync(string layerId, string widgetId, string triggerName,
            JsonElement eventData, string waitId = "")
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

            if (!layer.Widgets.Any(w => w.Id == widgetId))
            {
                return Task.FromResult(false);
            }

            // Inactive-layer short-circuit: nothing to render against, succeed immediately so
            // wait_for_visual doesn't stall scripts when OBS scenes are hidden.
            if (!Registry.IsLayerActive(layerId))
            {
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

            return queue.EnqueueAsync(triggerName, eventData, waitId);
        }

        /// <summary>
        /// Forwards an inbound VISUAL_COMPLETE to the matching widget queue and resolves any
        /// Bus pending wait keyed by waitId. Idempotent — multiple browser clients
        /// echoing the same completion are absorbed via TrySetResult.
        /// </summary>
        public void NotifyTriggerComplete(string layerId, string widgetId, string triggerName, string waitId)
        {
            WidgetTriggerQueue? queue;
            lock (_lock)
            {
                _queues.TryGetValue((layerId, widgetId), out queue);
            }
            queue?.NotifyComplete(triggerName, waitId);

            if (!string.IsNullOrEmpty(waitId))
                Bus.Instance.ResolveVisualWait(waitId);
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
