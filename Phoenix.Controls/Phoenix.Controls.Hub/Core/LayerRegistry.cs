using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// LayerRegistry — in-memory map of loaded `.phxlayer` files plus per-layer browser-connection presence.
    /// Layer ID is the filename stem (e.g., "main.phxlayer" → "main").
    /// Singleton lifetime; populated by LayerWatcher from disk and mutated by HUDServer as browsers connect/disconnect.
    /// </summary>
    public sealed class LayerRegistry
    {
        // BH-018 — concurrent first-touch from LayerWatcher.ScanAll and HUDServer's
        // /api/layer route could each run the ctor. Double-checked locking; the
        // public ctor below is kept available for tests so we still allow `new`.
        private static LayerRegistry? _instance;
        private static readonly object _instanceLock = new();
        public static LayerRegistry Instance
        {
            get
            {
                var inst = _instance;
                if (inst != null) return inst;
                lock (_instanceLock)
                {
                    return _instance ??= new LayerRegistry();
                }
            }
        }

        private readonly Dictionary<string, Layer>        _layers      = new();
        private readonly Dictionary<string, HashSet<WebSocket>>    _connections = new();
        private readonly object _lock = new();

        /// <summary>
        /// Raised whenever a layer is registered or replaced (i.e. on every LayerWatcher reload).
        /// HubBootstrapper subscribes to push LAYER_RELOADED to connected browsers — they call
        /// location.reload() and re-fetch /api/layer/&lt;id&gt;.
        /// </summary>
        public event Action<string>? LayerReloaded;

        /// <summary>
        /// QC05-07 — raised whenever a layer is removed from the registry (i.e. on
        /// every LayerWatcher Deleted / Renamed-away event). HUDServer subscribes
        /// to prune <c>_layerDropCounts</c> so the diagnostic counter doesn't
        /// accumulate forever once a layer is gone. Symmetric counterpart to
        /// <see cref="LayerReloaded"/>.
        /// </summary>
        public event Action<string>? LayerRemoved;

        /// <summary>
        /// T5 — Fires when a per-layer WebSocket connects or disconnects, with the
        /// new total active connection count across all layers. Hub status-bar
        /// surfaces (e.g. <c>HUDServer.OnClientCountChanged</c>) re-emit this so
        /// the connection indicator updates without each window subscribing here
        /// directly.
        /// </summary>
        public event Action<int>? OnConnectionsChanged;

        /// <summary>
        /// Fires when a single layer's active state crosses (became active /
        /// became inactive). Active = at least one WebSocket connection registered
        /// for the layer. Visualist's LayerRail subscribes through
        /// IHubServices.Layers to flip the per-row dot on/off without polling.
        /// (string layerId, bool isActive)
        /// </summary>
        public event Action<string, bool>? LiveLayerChanged;

        public LayerRegistry() { }

        public void RegisterLayer(string layerId, Layer layer)
        {
            lock (_lock) _layers[layerId] = layer;
            try { LayerReloaded?.Invoke(layerId); }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "LayerRegistry", $"LayerReloaded subscriber threw for '{layerId}'", ex);
            }
        }

        public void RemoveLayer(string layerId)
        {
            bool removed;
            lock (_lock) removed = _layers.Remove(layerId);
            if (!removed) return;
            try { LayerRemoved?.Invoke(layerId); }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "LayerRegistry", $"LayerRemoved subscriber threw for '{layerId}'", ex);
            }
        }

        public Layer? GetLayer(string layerId)
        {
            lock (_lock) return _layers.TryGetValue(layerId, out var l) ? l : null;
        }

        public IReadOnlyList<string> GetRegisteredLayerIds()
        {
            lock (_lock) return _layers.Keys.ToList();
        }

        public void RegisterConnection(string layerId, WebSocket ws)
        {
            int total;
            bool becameActive = false;
            lock (_lock)
            {
                bool hadAny = _connections.TryGetValue(layerId, out var set) && set!.Count > 0;
                if (set is null)
                    _connections[layerId] = set = new HashSet<WebSocket>();
                set.Add(ws);
                becameActive = !hadAny && set.Count > 0;
                total = TotalConnectionCountUnlocked();
            }
            FireConnectionsChanged(total);
            if (becameActive) FireLiveLayerChanged(layerId, true);
        }

        /// <summary>
        /// [QC18-S2 P2] Atomic cap-and-register. Returns <c>true</c> when the new
        /// WebSocket was added (the layer's per-layer count was strictly below
        /// <paramref name="maxConnections"/> at the moment of the lock-protected
        /// check). Returns <c>false</c> when the cap was already hit, in which
        /// case the registry is untouched and the caller is responsible for
        /// closing the socket out with a 1013 Try-Again-Later.
        ///
        /// HUDServer's pre-upgrade <c>GetConnections(...).Count &lt;= cap</c>
        /// fast-path is a TOCTOU — five concurrent <c>/hud/&lt;id&gt;</c>
        /// handshakes can all observe count &lt; cap, all proceed past the
        /// gate, and all register past the limit. Pushing the cap check into
        /// this lock-held path collapses the gate and the registration into a
        /// single atomic step. The fast-path stays in HUDServer as a cheap
        /// pre-upgrade reject; this method is the authoritative gate.
        /// </summary>
        public bool TryRegisterConnection(string layerId, WebSocket ws, int maxConnections)
        {
            int total;
            bool becameActive = false;
            lock (_lock)
            {
                _connections.TryGetValue(layerId, out var set);
                // Sweep dead sockets before the cap check. When an OBS browser source
                // reloads on LAYER_RELOADED its old socket closes and schedules an H48
                // grace-teardown — but the replacing connection cancels that teardown
                // (CancelPendingTeardown), so the closed socket is NEVER
                // UnregisterConnection'd and lingers in the set. After MAX_CONNECTIONS
                // reloads the set fills with dead sockets and every fresh reconnect is
                // rejected, so OBS can never reconnect and live auto-refresh dies.
                // Pruning non-Open sockets here keeps the cap measuring LIVE connections.
                if (set is not null) set.RemoveWhere(s => s.State != WebSocketState.Open);
                int currentCount = set?.Count ?? 0;
                if (currentCount >= maxConnections) return false;

                bool hadAny = currentCount > 0;
                if (set is null)
                    _connections[layerId] = set = new HashSet<WebSocket>();
                set.Add(ws);
                becameActive = !hadAny && set.Count > 0;
                total = TotalConnectionCountUnlocked();
            }
            FireConnectionsChanged(total);
            if (becameActive) FireLiveLayerChanged(layerId, true);
            return true;
        }

        /// <summary>
        /// Live (WebSocketState.Open) connection count for a layer, pruning any dead
        /// sockets it sweeps. Used by HUDServer's pre-upgrade cap fast-path so a pile
        /// of closed sockets (the H48 cancelled-teardown leak — see
        /// <see cref="TryRegisterConnection"/>) can't wrongly reject a fresh reconnect
        /// before the request ever reaches the authoritative atomic gate.
        /// </summary>
        public int GetLiveConnectionCount(string layerId)
        {
            lock (_lock)
            {
                if (!_connections.TryGetValue(layerId, out var set) || set is null) return 0;
                set.RemoveWhere(s => s.State != WebSocketState.Open);
                return set.Count;
            }
        }

        public void UnregisterConnection(string layerId, WebSocket ws)
        {
            int total;
            bool changed = false;
            bool becameInactive = false;
            lock (_lock)
            {
                if (_connections.TryGetValue(layerId, out var set))
                {
                    int before = set.Count;
                    changed = set.Remove(ws);
                    if (set.Count == 0)
                    {
                        _connections.Remove(layerId);
                        if (before > 0) becameInactive = true;
                    }
                }
                total = TotalConnectionCountUnlocked();
            }
            if (changed) FireConnectionsChanged(total);
            if (becameInactive) FireLiveLayerChanged(layerId, false);
        }

        /// <summary>Sum of WebSocket connections across every registered layer.</summary>
        public int GetTotalConnectionCount()
        {
            lock (_lock) return TotalConnectionCountUnlocked();
        }

        private int TotalConnectionCountUnlocked()
        {
            int total = 0;
            foreach (var kv in _connections) total += kv.Value.Count;
            return total;
        }

        private void FireConnectionsChanged(int total)
        {
            try { OnConnectionsChanged?.Invoke(total); }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "LayerRegistry", "OnConnectionsChanged subscriber threw", ex);
            }
        }

        private void FireLiveLayerChanged(string layerId, bool isActive)
        {
            try { LiveLayerChanged?.Invoke(layerId, isActive); }
            catch (Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "LayerRegistry",
                    $"LiveLayerChanged subscriber threw for '{layerId}' (isActive={isActive})",
                    ex);
            }
        }

        public bool IsLayerActive(string layerId)
        {
            lock (_lock)
            {
                if (_testActiveOverrides.Contains(layerId)) return true;

                // QC58-05 — primary signal: at least one live WebSocket.
                // Mirrors the pre-fix behaviour exactly when the socket
                // closes cleanly (FIN exchanged → UnregisterConnection
                // ran → _connections has no entry).
                if (_connections.TryGetValue(layerId, out var set) && set.Count > 0)
                    return true;

                // QC58-05 — secondary signal: a recent compositor FPS
                // heartbeat (within FpsTtl=5s) means the browser was alive
                // at least that recently, even if the socket has been
                // recorded as "no connections" because of a half-open /
                // dirty disconnect (NIC drop, OBS hard-kill, etc.). Without
                // this fallback, a dirty disconnect kept the layer reading
                // as "active" forever through the _connections-only branch
                // — *or* "inactive" forever once the dict was cleaned up by
                // some other path. Using the FPS heartbeat as the floor
                // gives us a self-healing inactivity window: stale entries
                // age out automatically because FpsTtl drops them lazily on
                // read.
                if (_fps.TryGetValue(layerId, out var entry))
                {
                    if (DateTime.UtcNow - entry.At <= FpsTtl) return true;
                    // Lazy-drop stale entries so they don't linger forever
                    // after the browser actually disappeared.
                    _fps.Remove(layerId);
                }

                return false;
            }
        }

        // Test-only seam — unit tests that exercise LayerRuntime's dispatch path don't have
        // real WebSockets to register, so they call MarkActiveForTesting to convince the
        // inactive-layer fast-succeed in LayerRuntime.EnqueueTriggerAsync to dispatch normally.
        // Production code never touches this set. Visibility is `internal` so this seam is
        // only reachable from the Phoenix.Controls.Tests assembly (granted via
        // [InternalsVisibleTo] in Phoenix.Controls.Hub.csproj) and stays out of the public
        // production API surface.
        private readonly HashSet<string> _testActiveOverrides = new();
        internal void MarkActiveForTesting(string layerId)
        {
            lock (_lock) _testActiveOverrides.Add(layerId);
        }

        public IReadOnlyList<WebSocket> GetConnections(string layerId)
        {
            lock (_lock)
            {
                return _connections.TryGetValue(layerId, out var set)
                    ? set.ToList()
                    : Array.Empty<WebSocket>();
            }
        }

        public IReadOnlyList<string> GetActiveLayerIds()
        {
            lock (_lock)
            {
                return _connections
                    .Where(kv => kv.Value.Count > 0)
                    .Select(kv => kv.Key)
                    .ToList();
            }
        }

        /// <summary>
        /// B8 (audit 2026-05-24) — count of layers whose
        /// <see cref="IsLayerActive"/> returns true. Equivalent to
        /// <c>GetActiveLayerIds().Count</c> but skips the allocation
        /// (the StatusStrip polls this every second). Returns 0 when
        /// no live WebSockets are registered.
        /// </summary>
        public int ActiveLayerCount
        {
            get
            {
                int count = 0;
                lock (_lock)
                {
                    foreach (var kv in _connections)
                    {
                        if (kv.Value.Count > 0) count++;
                    }
                }
                return count;
            }
        }

        // ─── Phase 9 (a) — per-layer FPS readout ────────────────────────────
        //
        // The compositor (data/overlay/compositor.js) sends an FPS message every
        // ~1s with the count of trigger renders that ran in the prior window.
        // HUDServer's WS receive loop calls RecordFps; the status bar reads
        // GetLastFps to populate its tooltip. "FPS" is a slight misnomer — what's
        // actually reported is renders-per-second (the canvas only paints when a
        // RUN_TRIGGER fires; idle layers report 0 even though the underlying
        // browser frame rate stays at 60Hz).
        private readonly Dictionary<string, (int Fps, DateTime At)> _fps = new();
        private static readonly TimeSpan FpsTtl = TimeSpan.FromSeconds(5);

        public void RecordFps(string layerId, int fps)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            if (fps < 0) fps = 0;
            lock (_lock) _fps[layerId] = (fps, DateTime.UtcNow);
        }

        /// <summary>
        /// Returns the most-recently reported renders-per-second for a layer, or
        /// null if the report is stale (older than <see cref="FpsTtl"/>) or never
        /// recorded. Stale entries are dropped lazily on read so a disconnected
        /// browser doesn't keep flashing its last value forever.
        /// </summary>
        public int? GetLastFps(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return null;
            lock (_lock)
            {
                if (!_fps.TryGetValue(layerId, out var entry)) return null;
                if (DateTime.UtcNow - entry.At > FpsTtl)
                {
                    _fps.Remove(layerId);
                    return null;
                }
                return entry.Fps;
            }
        }

        /// <summary>
        /// One-shot snapshot for status-bar rendering. Returns ordered tuples of
        /// (layerId, isActive, fps?) for every registered layer, with stale FPS
        /// entries elided. Holds the lock for the duration so the caller sees a
        /// consistent picture across the three underlying maps.
        /// </summary>
        public IReadOnlyList<(string LayerId, bool IsActive, int? Fps)> GetSnapshot()
        {
            var result = new List<(string, bool, int?)>();
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                foreach (var layerId in _layers.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    bool active = (_connections.TryGetValue(layerId, out var set) && set.Count > 0)
                                  || _testActiveOverrides.Contains(layerId);
                    int? fps = null;
                    if (_fps.TryGetValue(layerId, out var entry))
                    {
                        if (now - entry.At <= FpsTtl) fps = entry.Fps;
                        else _fps.Remove(layerId);
                    }
                    result.Add((layerId, active, fps));
                }
            }
            return result;
        }
    }
}
