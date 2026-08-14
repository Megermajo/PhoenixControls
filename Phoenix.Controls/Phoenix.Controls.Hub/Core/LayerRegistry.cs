using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// V6 — what a <c>/hud/&lt;layerId&gt;</c> WebSocket actually IS.
    ///
    /// <para><see cref="Production"/> is an OBS Browser Source: a surface the audience
    /// sees. Its presence is what "this layer is live" means, and its
    /// <c>VISUAL_COMPLETE</c> is the only honest answer to "did the overlay render?".</para>
    ///
    /// <para><see cref="Editor"/> is a Visualist design-time surface — the layer-canvas
    /// thumbnail capture host, the embedded single-widget pane, the layer popout, the
    /// widget popout. Before V6 these were byte-identical to an OBS source on the wire,
    /// so a layer read ACTIVE while only an author's preview pane was attached and that
    /// pane acked visuals for widgets it never drew: a script's <c>wait_for_visual</c>
    /// completed against a preview. The kind travels from the page URL
    /// (<c>?client=editor</c>) through the WS upgrade query into
    /// <see cref="LayerRegistry.TryRegisterConnection(string, WebSocket, int, LayerClientKind)"/>.</para>
    /// </summary>
    public enum LayerClientKind
    {
        /// <summary>An OBS Browser Source (or anything else that is not a declared editor surface).</summary>
        Production = 0,

        /// <summary>A Visualist design-time preview surface (declared <c>?client=editor</c>).</summary>
        Editor = 1,
    }

    /// <summary>
    /// LayerRegistry — in-memory map of loaded `.phxlayer` files plus per-layer browser-connection presence.
    /// Layer ID is the filename stem (e.g., "main.phxlayer" → "main").
    /// Singleton lifetime; populated by LayerWatcher from disk and mutated by HUDServer as browsers connect/disconnect.
    ///
    /// <para>V6 — "presence" means PRODUCTION presence. Every arm that answers "is this
    /// layer live?" (<see cref="IsLayerActive"/>, <see cref="GetSnapshot"/>,
    /// <see cref="GetActiveLayerIds"/>, <see cref="ActiveLayerCount"/>) counts only
    /// <see cref="LayerClientKind.Production"/> sockets — including IsLayerActive, whose
    /// widening was tried and reverted (it made a script's wait_for_visual stall against a
    /// pane that cannot ack). The design-time Test Run repair is a fire-and-forget-only
    /// carve-out in <c>LayerRuntime</c> built on <see cref="HasAnyConnection"/> instead.
    /// The arms that answer "does any
    /// browser need this data / this handle?" (<see cref="GetConnections"/>,
    /// <see cref="GetLiveConnectionCount(string)"/>,
    /// <see cref="GetLayerIdsWithAnyConnection"/>,
    /// <see cref="GetTotalConnectionCount"/>) deliberately count BOTH kinds — an editor
    /// pane is a real socket that must still receive live frames and must still be
    /// unregistered on shutdown. Mixing those two questions up is how this file
    /// produced both halves of the V6 bug.</para>
    /// </summary>
    public sealed class LayerRegistry
    {
        // Concurrent first-touch from LayerWatcher.ScanAll and HUDServer's
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

        // V6 — the per-socket client kind, as a SIDE-SET rather than by widening
        // _connections to Dictionary<WebSocket, LayerClientKind>.
        //
        // WHY a side-set: GetConnections' contract is "every socket on this layer",
        // and it is the fan-out path (SendToLayerAsync → RUN_TRIGGER / LAYER_RELOADED /
        // LIVE_SNAPSHOT). Widening the value type would have forced a change to that
        // return type at the exact site where an accidental "only production" filter
        // would silently stop every preview pane from rendering. A side-set leaves
        // every existing read of _connections meaning what it already meant, and makes
        // "does this layer have PRODUCTION presence?" the only new question asked.
        //
        // Membership is EDITOR-ONLY (absence == Production), so the common case costs
        // nothing and a missed insert fails toward the pre-V6 behaviour rather than
        // toward "a real OBS source is invisible". Guarded by the same _lock as
        // _connections; every path that removes a socket from a connection set must
        // remove it here too or the set leaks for the process lifetime (see
        // PruneDeadUnlocked and UnregisterConnection).
        private readonly HashSet<WebSocket> _editorSockets = new();

        /// <summary>
        /// Raised whenever a layer is registered or replaced (i.e. on every LayerWatcher reload).
        /// HubBootstrapper subscribes to push LAYER_RELOADED to connected browsers — they call
        /// location.reload() and re-fetch /api/layer/&lt;id&gt;.
        /// </summary>
        public event Action<string>? LayerReloaded;

        /// <summary>
        /// Raised whenever a layer is removed from the registry (i.e. on
        /// every LayerWatcher Deleted / Renamed-away event). HUDServer subscribes
        /// to prune <c>_layerDropCounts</c> so the diagnostic counter doesn't
        /// accumulate forever once a layer is gone. Symmetric counterpart to
        /// <see cref="LayerReloaded"/>.
        /// </summary>
        public event Action<string>? LayerRemoved;

        /// <summary>
        /// Fires when a single layer's active state crosses (became active /
        /// became inactive). Active = at least one <see cref="LayerClientKind.Production"/>
        /// WebSocket connection registered for the layer. Visualist's LayerRail subscribes
        /// through IHubServices.Layers to flip the per-row dot on/off without polling.
        /// (string layerId, bool isActive)
        ///
        /// <para>V6 — an <see cref="LayerClientKind.Editor"/> socket connecting or
        /// disconnecting fires NOTHING. Otherwise opening a preview pane lit the author's
        /// own rail dot green, i.e. the UI told them the layer was live on stream because
        /// they were looking at it. The transition is computed from the production count
        /// on both sides of the mutation, so a layer with a production source plus three
        /// preview panes stays "active" until the PRODUCTION socket goes.</para>
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

        /// <summary>
        /// Uncapped registration. Kind defaults to <see cref="LayerClientKind.Production"/>
        /// so every pre-V6 caller (and every test that registers a stand-in socket to make
        /// a layer read live) keeps its exact previous meaning.
        /// </summary>
        public void RegisterConnection(string layerId, WebSocket ws)
            => RegisterConnection(layerId, ws, LayerClientKind.Production);

        /// <summary>Uncapped registration with an explicit client kind (V6).</summary>
        public void RegisterConnection(string layerId, WebSocket ws, LayerClientKind kind)
        {
            bool becameActive = false;
            lock (_lock)
            {
                _connections.TryGetValue(layerId, out var set);
                // V6 — "had any" must mean "had any PRODUCTION", so a production source
                // joining a layer that already carries preview panes still announces the
                // 0→1 production transition.
                bool hadProduction = HasProductionSocketUnlocked(set);
                if (set is null)
                    _connections[layerId] = set = new HashSet<WebSocket>();
                set.Add(ws);
                if (kind == LayerClientKind.Editor) _editorSockets.Add(ws);
                becameActive = !hadProduction && HasProductionSocketUnlocked(set);
            }
            if (becameActive) FireLiveLayerChanged(layerId, true);
        }

        /// <summary>
        /// Atomic cap-and-register. Returns <c>true</c> when the new
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
            => TryRegisterConnection(layerId, ws, maxConnections, LayerClientKind.Production);

        /// <summary>
        /// V6 — atomic cap-and-register with a PER-KIND budget.
        ///
        /// <para><paramref name="maxConnections"/> is the budget for
        /// <paramref name="kind"/> ALONE: only sockets of the same kind on the same layer
        /// count against it. That is the fix for a live bug — Visualist can open FOUR
        /// design surfaces on one layer (layer-canvas capture host, embedded single-widget
        /// pane, layer popout, widget popout) and the single shared cap was also 4, so an
        /// author with previews open could lock a real OBS Browser Source out of its own
        /// layer. It presented as "my overlay just stopped working", with a 1013
        /// Try-Again-Later the author never saw.</para>
        ///
        /// <para>Two disjoint budgets, not an exemption: an editor socket is still
        /// attacker-reachable (anyone who can hit <c>/hud/&lt;id&gt;</c> can append
        /// <c>?client=editor</c>), so it must stay bounded. Splitting the budgets means a
        /// flood of either kind can only ever exhaust its own, and a production upgrade is
        /// measured against production sockets only — so an OBS source is rejected only by
        /// four OTHER production sockets, exactly as before V6. Visualist can never be one
        /// of those four because every Visualist page URL declares <c>?client=editor</c>.</para>
        /// </summary>
        public bool TryRegisterConnection(string layerId, WebSocket ws, int maxConnections, LayerClientKind kind)
        {
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
                PruneDeadUnlocked(set);
                int sameKindCount = CountKindUnlocked(set, kind);
                if (sameKindCount >= maxConnections) return false;

                bool hadProduction = HasProductionSocketUnlocked(set);
                if (set is null)
                    _connections[layerId] = set = new HashSet<WebSocket>();
                set.Add(ws);
                if (kind == LayerClientKind.Editor) _editorSockets.Add(ws);
                becameActive = !hadProduction && HasProductionSocketUnlocked(set);
            }
            if (becameActive) FireLiveLayerChanged(layerId, true);
            return true;
        }

        // ─── V6 kind-scoped counting primitives (all _lock-held) ────────────

        /// <summary>
        /// Drops sockets that have left <see cref="WebSocketState.Open"/> from
        /// <paramref name="set"/> AND from <see cref="_editorSockets"/>.
        ///
        /// <para>The side-set prune is not optional: the pre-V6 body was a bare
        /// <c>set.RemoveWhere</c>, and swept editor sockets would have stayed in
        /// <c>_editorSockets</c> for the whole Hub process — one leaked reference per
        /// preview reload, and a recycled reference (the runtime is free to hand the same
        /// object identity back) would classify a fresh PRODUCTION socket as an editor
        /// one, making a real OBS source invisible to presence.</para>
        /// </summary>
        private void PruneDeadUnlocked(HashSet<WebSocket>? set)
        {
            if (set is null) return;
            set.RemoveWhere(s =>
            {
                if (s.State == WebSocketState.Open) return false;
                _editorSockets.Remove(s);
                return true;
            });
        }

        /// <summary>True when <paramref name="set"/> holds at least one socket that is NOT
        /// a declared editor surface. This is the single definition of "this layer has
        /// production presence" that all four presence arms share.</summary>
        private bool HasProductionSocketUnlocked(HashSet<WebSocket>? set)
        {
            if (set is null || set.Count == 0) return false;
            // Fast path: no editor sockets anywhere → any member is production.
            if (_editorSockets.Count == 0) return true;
            foreach (var ws in set)
            {
                if (!_editorSockets.Contains(ws)) return true;
            }
            return false;
        }

        /// <summary>Count of sockets in <paramref name="set"/> matching <paramref name="kind"/>.</summary>
        private int CountKindUnlocked(HashSet<WebSocket>? set, LayerClientKind kind)
        {
            if (set is null || set.Count == 0) return 0;
            if (kind == LayerClientKind.Production && _editorSockets.Count == 0) return set.Count;
            int n = 0;
            foreach (var ws in set)
            {
                bool isEditor = _editorSockets.Contains(ws);
                if (isEditor == (kind == LayerClientKind.Editor)) n++;
            }
            return n;
        }

        /// <summary>
        /// Live (WebSocketState.Open) connection count for a layer, pruning any dead
        /// sockets it sweeps. Used by HUDServer's pre-upgrade cap fast-path so a pile
        /// of closed sockets (the H48 cancelled-teardown leak — see
        /// <see cref="TryRegisterConnection(string, WebSocket, int)"/>) can't wrongly
        /// reject a fresh reconnect before the request ever reaches the authoritative
        /// atomic gate.
        ///
        /// <para>V6 — counts BOTH kinds, deliberately. Its two other consumers are the
        /// <c>OverlayLiveStore.ClearLayer</c> zero-gate (an editor pane still needs the
        /// layer's live-channel subscription, so dropping it while a preview is attached
        /// would starve the preview) and the LIVE_HELLO deadline watchdog (an editor pane
        /// is a real client that really should have said hello). Neither question is
        /// "is this layer live on stream?", which is what the presence arms answer.</para>
        /// </summary>
        public int GetLiveConnectionCount(string layerId)
        {
            lock (_lock)
            {
                if (!_connections.TryGetValue(layerId, out var set) || set is null) return 0;
                PruneDeadUnlocked(set);
                return set.Count;
            }
        }

        /// <summary>
        /// V6 — live connection count for one <paramref name="kind"/> on a layer. This is
        /// what HUDServer's pre-upgrade cap fast-path measures, so a pile of preview panes
        /// cannot pre-reject an OBS Browser Source before it reaches the atomic gate.
        /// </summary>
        public int GetLiveConnectionCount(string layerId, LayerClientKind kind)
        {
            lock (_lock)
            {
                if (!_connections.TryGetValue(layerId, out var set) || set is null) return 0;
                PruneDeadUnlocked(set);
                return CountKindUnlocked(set, kind);
            }
        }

        /// <summary>
        /// V6 — true when this socket was registered as a Visualist design-time surface.
        /// HUDServer's inbound dispatch calls this to refuse the side-effecting frames
        /// (<c>VISUAL_COMPLETE</c>, <c>FPS</c>) an editor pane must not be allowed to
        /// produce. Returns false for an unknown / already-unregistered socket, which is
        /// the safe direction: an unclassifiable socket is treated as production, i.e.
        /// exactly the pre-V6 behaviour.
        /// </summary>
        public bool IsEditorSocket(WebSocket ws)
        {
            if (ws is null) return false;
            lock (_lock) return _editorSockets.Contains(ws);
        }

        // ─── V13 §8.3 — the per-layer /hud connect token ────────────────────
        //
        // WHAT IT IS: a 128-bit random value minted lazily per layer and PERSISTED, so the same
        // layer keeps the same token across Hub restarts.
        //
        // ★ IT USED TO BE PER-INSTANCE, i.e. rotated per Hub start, and that was a recurring
        // steady-state failure rather than a migration edge case (§8.3 as amended 2026-08-02).
        // Nothing makes an already-open page re-fetch the /layer/<id> HTML: compositor.js's
        // socket.onclose reconnects the SOCKET only, and LAYER_RELOADED deliberately prefers
        // softReloadLayer(), which re-fetches /api/layer/<id> and not the page. So every OBS
        // Browser Source open across a restart reconnected presenting the previous process's dead
        // token and lost the payload path for the rest of that page's life — and Hub restarts are
        // routine: EVERY AUTO-UPDATE IS ONE.
        //
        // Persisting costs nothing security-wise. The token proves "Hub served this page", not
        // "this session is fresh"; it is a capability marker, not a session secret, and there is
        // no threat model in which per-start rotation helps. Compare: an OBS source that keeps
        // rendering for weeks is the NORMAL case this has to survive.
        //
        // WHAT IT IS NOT: authentication. It is a capability marker that says "this
        // page was served by THIS Hub process", which is what distinguishes an
        // overlay Hub itself handed out from an arbitrary local page that guessed a
        // layer id. HUDServer classifies a tokenless / stale-token socket
        // Untrusted and still serves it every downward frame (V13 §8.3's grace
        // path); only the new UPWARD paths are declined. So a miss here is never a
        // blackout — see HUDServer.IsPrivilegedUpwardFrame for the exact list.
        //
        // NO TOKEN MINTED YET ⇒ INVALID, deliberately. A socket can only arrive
        // after GET /layer/&lt;id&gt; served the page that opens it, and that route mints
        // the token. A /hud/&lt;id&gt; upgrade for a layer whose HTML this process never
        // served is therefore either a stale cached page or something that never
        // asked Hub for a page at all — both are exactly the Untrusted case.
        //
        // NOT DROPPED BY RemoveLayer, deliberately. LayerWatcher fires Remove+Register on
        // every `.phxlayer` SAVE, so pruning the token there would demote every
        // already-open OBS Browser Source to Untrusted each time the author hits save — a
        // page Hub really did serve, silently losing its payload path, with the only remedy
        // being a cache refresh nobody has a reason to suspect. The token keys the layer
        // ID, not the layer CONTENT, so surviving a reload is the correct behaviour.
        //
        // The dictionary is bounded by the real layer count rather than by traffic: the
        // only minting caller is HUDServer.ServeLayerHtmlAsync, and it runs only AFTER the
        // layer resolved on disk or in the registry (a 404 returns first), so a fuzzer
        // hitting /layer/<random> can never add an entry. Validation reads never mint.
        // The persisted file inherits that same bound — one entry per layer id whose page Hub
        // has actually served, i.e. a handful per install, so it needs no pruning policy.
        private const int ConnectTokenBytes = 16;
        private readonly Dictionary<string, string> _connectTokens = new(StringComparer.Ordinal);

        // Loaded-from-disk latch. Per INSTANCE (not static) so a test that builds a second
        // registry models a second Hub process honestly: it re-reads the store rather than
        // inheriting this instance's memory.
        private bool _connectTokensLoaded;

        // ★ WHERE IT LIVES: %AppData%/PhoenixControls/hud-connect-tokens.json — roaming AppData,
        // NOT {AppBase}/data. An auto-update replaces the install tree, and the whole point of
        // persisting is to survive exactly that (the P0 "update wipes user data" class). Same root
        // the DB and config already use.
        private const string ConnectTokenStoreFileName = "hud-connect-tokens.json";

        // Serializes the read-merge-write on the store file. STATIC because in production the file
        // is shared by every LayerRegistry instance in the process (production has one; tests
        // build several), and _lock is per-instance so it cannot order two instances' writes. Save
        // re-reads and MERGES before writing, so two instances minting different layers cannot
        // clobber each other's entries. A test that re-points the directory (below) then shares
        // this lock with unrelated files, which merely over-serializes — the safe direction.
        private static readonly object _connectTokenIoLock = new();

        // ★ TEST SEAM — the DIRECTORY the store file lives in. `null`, which is the only value
        // production ever has, resolves to exactly the roaming path described above: the default
        // is the same expression it has always been, so production cannot drift by accident.
        //
        // WHY IT EXISTS: without it a test had no way to opt out of the real roaming folder — the
        // folder that also holds the streamer's live DB and config. The suite that proves "a
        // corrupt store re-mints and rewrites itself" therefore had to corrupt the REAL file and
        // restore a backup in a `finally`. This test suite has a documented history of hangs and
        // its orphan testhosts get killed, so that `finally` is not guaranteed to run, and the
        // failure mode was the developer's own live overlay tokens left as `{ this is not json`.
        // A test reaching into live user data is a defect in the seam, not in the test.
        //
        // Assigned once, right after construction. Re-pointing later is still coherent (see the
        // setter) but there is no reason to.
        private string? _connectTokenStoreDirectoryOverride;

        internal string? ConnectTokenStoreDirectoryOverride
        {
            get { lock (_lock) return _connectTokenStoreDirectoryOverride; }
            set
            {
                lock (_lock)
                {
                    _connectTokenStoreDirectoryOverride = value;
                    // Re-pointing the store invalidates everything already read out of the old
                    // one, so drop the load latch and the cached map: the next call reads the new
                    // file instead of answering from the previous file's memory.
                    _connectTokens.Clear();
                    _connectTokensLoaded = false;
                }
            }
        }

        // Instance-scoped (was static) purely so the override above can reach it. Every read
        // happens under _lock, which is where both callers already are.
        private string ConnectTokenStorePath =>
            Path.Combine(
                _connectTokenStoreDirectoryOverride ?? Phoenix.Controls.Shared.Core.Paths.RoamingAppData(),
                ConnectTokenStoreFileName);

        /// <summary>The persisted shape. Versioned so a future format change is detectable.</summary>
        private sealed class ConnectTokenStore
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, string> Tokens { get; set; } = new(StringComparer.Ordinal);
        }

        // Naming policy stated EXPLICITLY rather than left to the default: the file is
        // user-visible state under %AppData% and `{ "version": 1, "tokens": { … } }` is the shape
        // the rest of this product's JSON uses. Case-insensitive on read so a hand-edited file
        // (the realistic reason anyone opens this) still loads instead of silently re-minting every
        // token — which would demote every open OBS source to Untrusted at once.
        private static readonly JsonSerializerOptions _connectTokenJson = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// V13 — returns this layer's connect token, minting it on first use and reusing the
        /// persisted value on every later Hub start. Called by <c>HUDServer.ServeLayerHtmlAsync</c>
        /// so the served page carries the token that its own <c>/hud/&lt;id&gt;</c> socket will
        /// present back.
        ///
        /// <para>Lower-case hex, so it is safe to embed in an HTML attribute and in a
        /// query string with no escaping — the validator and the embedder therefore
        /// cannot disagree about encoding.</para>
        ///
        /// <para>File IO runs under <see cref="_lock"/>, which is deliberate and bounded: the load
        /// happens at most once per process and a save only when a layer id is minted for the
        /// first time ever (i.e. when Hub serves a page for a layer it has never served). The
        /// alternative — releasing the lock across the IO — would allow two callers to mint two
        /// tokens for one layer, and the SECOND one would invalidate a page already served with
        /// the first. A sub-millisecond stall on a page-serve beats handing an overlay a token
        /// that stops working.</para>
        /// </summary>
        public string GetOrCreateConnectToken(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return "";
            lock (_lock)
            {
                EnsureConnectTokensLoadedUnlocked();
                // A well-formed persisted value is reused; a malformed one (hand-edited file,
                // truncated write) is treated as MISSING and re-minted, per §8.3's "regenerate
                // only when missing or unreadable". Trusting a garbage value would classify every
                // page Untrusted with a token present, which is the confusing failure.
                if (_connectTokens.TryGetValue(layerId, out var existing) && IsWellFormedToken(existing))
                    return existing;

                // Cryptographic RNG, not Guid.NewGuid: the value is a capability
                // marker, and a predictable one would let a local page skip the
                // grace path it is supposed to land in.
                string minted = Convert.ToHexString(RandomNumberGenerator.GetBytes(ConnectTokenBytes))
                                       .ToLowerInvariant();
                _connectTokens[layerId] = minted;
                PersistConnectToken(layerId, minted);
                return minted;
            }
        }

        /// <summary>
        /// 32 lower-case hex chars — the exact shape <see cref="GetOrCreateConnectToken"/> mints.
        /// Used to reject anything the store hands back that this code did not write, so a
        /// hand-edited file degrades to "mint a fresh one" instead of to a permanently Untrusted
        /// overlay.
        /// </summary>
        private static bool IsWellFormedToken(string? token)
        {
            if (token is null || token.Length != ConnectTokenBytes * 2) return false;
            foreach (char c in token)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Seeds <see cref="_connectTokens"/> from the persisted store, once. Any failure —
        /// missing file, unparseable JSON, denied read — leaves the map empty, which means every
        /// layer mints fresh and the store is rewritten. That is the documented degradation and it
        /// is never fatal: a fresh token only costs the still-open pages one self-heal reload
        /// (HUDServer's HUD_RELOAD) or, failing that, the grace path they were already in.
        ///
        /// <para>The token value is never logged here or anywhere else; a failure is reported
        /// without it.</para>
        /// </summary>
        private void EnsureConnectTokensLoadedUnlocked()
        {
            if (_connectTokensLoaded) return;
            _connectTokensLoaded = true;   // set FIRST: a throwing load must not retry per call

            try
            {
                string path = ConnectTokenStorePath;
                string json;
                lock (_connectTokenIoLock)
                {
                    if (!File.Exists(path)) return;
                    json = File.ReadAllText(path);
                }
                var store = JsonSerializer.Deserialize<ConnectTokenStore>(json, _connectTokenJson);
                if (store?.Tokens is null) return;
                foreach (var kv in store.Tokens)
                {
                    if (string.IsNullOrEmpty(kv.Key) || !IsWellFormedToken(kv.Value)) continue;
                    _connectTokens[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"LayerRegistry: the /hud connect-token store could not be read ({ex.GetType().Name}) — " +
                    $"tokens will be re-minted, so any overlay page still open from before this Hub start " +
                    $"self-heals with one reload (or keeps rendering on the grace path).",
                    "LayerRegistry", LogLevel.System);
            }
        }

        /// <summary>
        /// Writes one minted token into the persisted store, merging with whatever is on disk so a
        /// second registry instance (or a Hub that ran between our load and now) does not lose its
        /// entries. Best-effort: a failed write only means the next Hub start re-mints.
        ///
        /// <para>Atomic replace via a temp file + <see cref="File.Move(string, string, bool)"/> so
        /// a crash mid-write cannot leave a truncated store — which would be read back as
        /// unreadable and silently re-mint every token.</para>
        ///
        /// <para>Instance method (it was static) because the store directory is now an instance
        /// seam; the caller already holds <see cref="_lock"/>, which is where
        /// <see cref="ConnectTokenStorePath"/> must be read.</para>
        /// </summary>
        private void PersistConnectToken(string layerId, string token)
        {
            try
            {
                string path = ConnectTokenStorePath;
                lock (_connectTokenIoLock)
                {
                    var store = new ConnectTokenStore();
                    if (File.Exists(path))
                    {
                        try
                        {
                            var existing = JsonSerializer.Deserialize<ConnectTokenStore>(File.ReadAllText(path), _connectTokenJson);
                            if (existing?.Tokens is not null) store.Tokens = existing.Tokens;
                        }
                        catch { /* unreadable ⇒ start a fresh store, same rule as the loader */ }
                    }
                    store.Tokens[layerId] = token;

                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(store, _connectTokenJson));
                    File.Move(tmp, path, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"LayerRegistry: the /hud connect-token store could not be written " +
                    $"({ex.GetType().Name}) — this layer's token works for this Hub session but will " +
                    $"be re-minted on the next start.",
                    "LayerRegistry", LogLevel.System);
            }
        }

        /// <summary>
        /// V13 — whether <paramref name="presented"/> is this layer's connect token. False for an
        /// empty/absent token and for a layer no Hub run has ever served (see the block comment
        /// above for why that is the right answer).
        ///
        /// <para>★ THIS PATH MUST LOAD THE PERSISTED STORE, and it is the reason persistence
        /// works at all. After a Hub restart the FIRST thing that happens is an already-open OBS
        /// source reconnecting its <c>/hud/&lt;id&gt;</c> socket — no page fetch, so
        /// <c>GetOrCreateConnectToken</c> has not run in this process yet. Validating against an
        /// unloaded map would classify that socket Untrusted and re-create exactly the defect the
        /// amendment fixes.</para>
        ///
        /// <para>The comparison runs through <see cref="CryptographicOperations.FixedTimeEquals"/>
        /// so a caller cannot narrow the token byte-by-byte from timing. The token
        /// value is NEVER logged, here or by any caller — a log line naming it would
        /// hand it to anything that can read the SystemLog ring buffer.</para>
        /// </summary>
        public bool IsConnectTokenValid(string layerId, string? presented)
        {
            if (string.IsNullOrEmpty(layerId) || string.IsNullOrEmpty(presented)) return false;

            string? expected;
            lock (_lock)
            {
                EnsureConnectTokensLoadedUnlocked();
                if (!_connectTokens.TryGetValue(layerId, out expected)) return false;
            }
            if (string.IsNullOrEmpty(expected)) return false;

            // Length first, before either allocation. FixedTimeEquals would also return
            // false for a length mismatch, but only after we had encoded an
            // attacker-supplied query value of arbitrary length into a byte[]. Rejecting
            // on length leaks nothing that matters — the token's length is a compile-time
            // constant anybody can read off this file — while everything that IS secret
            // still goes through the constant-time compare below.
            if (presented.Length != expected.Length) return false;

            var a = Encoding.ASCII.GetBytes(expected);
            var b = Encoding.ASCII.GetBytes(presented);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        public void UnregisterConnection(string layerId, WebSocket ws)
        {
            bool becameInactive = false;
            lock (_lock)
            {
                if (_connections.TryGetValue(layerId, out var set))
                {
                    // V6 — the transition is PRODUCTION 1→0, not set-emptied. A layer whose
                    // OBS source disconnects while three preview panes stay open must go
                    // inactive at that moment; waiting for the set to empty would keep the
                    // Hub status strip, the Visualist rail dot and LayerRuntime's dispatch
                    // gate all claiming the overlay is live because the author still has a
                    // preview open.
                    bool hadProduction = HasProductionSocketUnlocked(set);
                    set.Remove(ws);
                    if (set.Count == 0) _connections.Remove(layerId);
                    becameInactive = hadProduction && !HasProductionSocketUnlocked(set);
                }
                // Unconditional: the kind side-set must never outlive the socket, even if
                // the (layerId, ws) pair no longer matched a connection set — the Stop()
                // sweep and the grace teardown can both reach here after a prune already
                // removed the socket.
                _editorSockets.Remove(ws);
            }
            if (becameInactive) FireLiveLayerChanged(layerId, false);
        }

        /// <summary>
        /// Sum of WebSocket connections across every registered layer.
        /// V6 — counts BOTH kinds. This feeds <c>HUDServer.ClientCount</c>, whose
        /// question is "how many browser handles does this server hold?" — a preview
        /// pane is one. The layer-presence question is
        /// <see cref="ActiveLayerCount"/>, which is production-only.
        /// </summary>
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

        /// <summary>
        /// Presence arm 1 — socket ∪ FPS-heartbeat ∪ test-override. Consumed by
        /// <c>LayerRuntime.EnqueueTriggerAsync</c>'s inactive-layer fast-succeed and
        /// <c>WidgetTriggerQueue.PumpAsync</c>'s between-dequeue re-check, i.e. this is the
        /// arm that decides whether a script's visual actually gets dispatched.
        ///
        /// <para>★ SCOPE GUARD — DO NOT "FINISH" THIS METHOD. The socket arm below is
        /// production-scoped (V6); the FPS fallback arm is still keyed by layerId ALONE and
        /// is NOT reconciled with it. That divergence — <c>IsLayerActive</c> = socket ∪ FPS
        /// ∪ override, <c>GetSnapshot</c> = socket ∪ override, <c>GetActiveLayerIds</c> /
        /// <c>ActiveLayerCount</c> = socket only — is a SEPARATELY PARKED item (TODO.md,
        /// "Layer state-machine signal divergence"), and the window in which the arms
        /// disagree stays exactly as wide as it is today: <see cref="FpsTtl"/> = 5 s (the
        /// TODO text's "0-3s" is wrong — 3 s is HUDServer's disconnect grace, a different
        /// mechanism). V6 deliberately added the discriminator to each arm INDEPENDENTLY so
        /// every arm kept its own signal mix. Unifying them is a behaviour change with its
        /// own blast radius and needs its own decision about which signal is canonical.</para>
        ///
        /// <para>What V6 did instead of re-keying <c>_fps</c> per socket: HUDServer REFUSES
        /// <c>FPS</c> frames from editor sockets (see its inbound dispatch). So the
        /// layer-keyed fallback can no longer be poisoned by a preview render — the arm is
        /// honest without touching its structure, which is what let the parked item stay
        /// parked.</para>
        /// </summary>
        /// <summary>
        /// Kind-BLIND socket presence — "is ANY browser surface attached to this layer,
        /// production or editor?". Deliberately NOT a presence arm: it answers only
        /// "is there something that could render this", and exists for exactly one caller,
        /// <c>LayerRuntime.EnqueueTriggerAsync</c>'s fire-and-forget carve-out, so a
        /// Visualist design-time "Test Run" reaches the preview pane when no OBS Browser
        /// Source is attached.
        ///
        /// <para>★ DO NOT use this to answer "is this layer live". A trigger that carries a
        /// waitId must NEVER be admitted on the strength of an editor socket: HUDServer
        /// refuses VISUAL_COMPLETE from editor clients, so the wait could not be resolved
        /// and <c>wait_for_visual</c> would stall to its timeout instead of taking
        /// LayerRuntime's instant inactive-layer fast-succeed. That is strictly worse than
        /// the discarded-Test-Run bug this carve-out repairs.</para>
        /// </summary>
        public bool HasAnyConnection(string layerId)
        {
            lock (_lock)
            {
                return _connections.TryGetValue(layerId, out var set) && set is not null && set.Count > 0;
            }
        }

        public bool IsLayerActive(string layerId)
        {
            lock (_lock)
            {
                if (_testActiveOverrides.Contains(layerId)) return true;

                // Primary signal: at least one live PRODUCTION WebSocket.
                // Mirrors the pre-fix behaviour exactly when the socket
                // closes cleanly (FIN exchanged → UnregisterConnection
                // ran → _connections has no entry).
                //
                // V6 — production-scoped. A Visualist preview pane used to satisfy this
                // arm, so a script's wait_for_visual dispatched into (and was acked by)
                // an editor pane that may not even render the addressed widget.
                //
                // ★ This stayed production-scoped on purpose. The design-time "Test Run"
                // repair does NOT widen this method — widening it would make a script's
                // wait_for_visual dispatch into a preview-only layer that can never ack it
                // (HUDServer refuses VISUAL_COMPLETE from editor sockets), turning an
                // instant fast-succeed into a stall to timeout. The repair instead lives in
                // LayerRuntime.EnqueueTriggerAsync, which admits an editor-only layer ONLY
                // for a fire-and-forget trigger — see HasAnyConnection below.
                if (_connections.TryGetValue(layerId, out var set) && HasProductionSocketUnlocked(set))
                    return true;

                // Secondary signal: a recent compositor FPS
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

        /// <summary>
        /// Every socket on a layer, BOTH kinds. This is the fan-out list
        /// (<c>SendToLayerAsync</c> → RUN_TRIGGER / LAYER_RELOADED / LIVE_SNAPSHOT /
        /// live patches), so it must stay kind-blind: a preview pane that stopped
        /// receiving frames would stop being a preview.
        /// </summary>
        public IReadOnlyList<WebSocket> GetConnections(string layerId)
        {
            lock (_lock)
            {
                return _connections.TryGetValue(layerId, out var set)
                    ? set.ToList()
                    : Array.Empty<WebSocket>();
            }
        }

        /// <summary>
        /// Presence arm 2a — layers with at least one PRODUCTION socket. No FPS fallback,
        /// no test-override (see the scope guard on <see cref="IsLayerActive"/>).
        /// <para>If you want "every layer holding any socket" — shutdown sweeps, every-layer
        /// fan-out — use <see cref="GetLayerIdsWithAnyConnection"/>. Using this method for
        /// those two would leak editor sockets across a Stop→Start and silently drop
        /// editor-only layers out of broadcasts.</para>
        /// </summary>
        public IReadOnlyList<string> GetActiveLayerIds()
        {
            lock (_lock)
            {
                return _connections
                    .Where(kv => HasProductionSocketUnlocked(kv.Value))
                    .Select(kv => kv.Key)
                    .ToList();
            }
        }

        /// <summary>
        /// V6 — every layer that currently holds at least one registered socket, REGARDLESS
        /// of client kind. Deliberately not a presence signal.
        ///
        /// <para>Exists because two callers ask "which layers hold sockets I must act on?",
        /// not "which layers are live?": <c>HUDServer.Stop</c>'s unregister sweep (missing an
        /// editor-only layer there leaves its sockets in <c>_connections</c> across a
        /// Stop→StartAsync, resurrecting the stale-presence bug that sweep exists to fix) and
        /// <c>HUDServer.EnumerateAllLayerSockets</c> (the every-layer fan-out; skipping
        /// editor-only layers would silently starve preview panes of broadcast frames).</para>
        /// </summary>
        public IReadOnlyList<string> GetLayerIdsWithAnyConnection()
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
        /// Presence arm 2b — count of layers with at least one PRODUCTION socket.
        /// Allocation-free sibling of <see cref="GetActiveLayerIds"/> (the StatusStrip polls
        /// this every second), and equivalent to <c>GetActiveLayerIds().Count</c>.
        ///
        /// <para>NOT equivalent to counting layers where <see cref="IsLayerActive"/> is true:
        /// this arm consults neither <c>_fps</c> nor <c>_testActiveOverrides</c>. That is the
        /// parked divergence documented on <see cref="IsLayerActive"/> — do not "fix" the
        /// code to match a tidier-sounding description.</para>
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
                        if (HasProductionSocketUnlocked(kv.Value)) count++;
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
        //
        // V6 — keyed by layerId ONLY, so a sample carries no attribution and an editor
        // pane's renders would be indistinguishable from an OBS source's. Rather than
        // re-key this map per socket (which would drag the parked signal-divergence item
        // into scope), HUDServer refuses FPS frames from editor sockets, so RecordFps is
        // only ever reached by production renders. If a future change lets an editor
        // socket in here, IsLayerActive's fallback arm goes dishonest again for FpsTtl.
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
        /// Presence arm 2c — one-shot snapshot for status-bar rendering. Returns ordered
        /// tuples of (layerId, isActive, fps?) for every registered layer, with stale FPS
        /// entries elided. Holds the lock for the duration so the caller sees a
        /// consistent picture across the three underlying maps.
        ///
        /// <para>V6 — <c>IsActive</c> is production-only. The <c>Fps</c> column is NOT
        /// filtered by kind here because it doesn't need to be: HUDServer refuses
        /// <c>FPS</c> frames from editor sockets, so nothing a preview renders ever reaches
        /// <c>_fps</c> in the first place. Keeps <c>HUDServer.CurrentBroadcastFps</c>
        /// (which reads this column and ignores <c>IsActive</c>) honest for free.</para>
        /// </summary>
        public IReadOnlyList<(string LayerId, bool IsActive, int? Fps)> GetSnapshot()
        {
            var result = new List<(string, bool, int?)>();
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                foreach (var layerId in _layers.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    bool active = (_connections.TryGetValue(layerId, out var set) && HasProductionSocketUnlocked(set))
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
