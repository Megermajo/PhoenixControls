using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Hub.Core.Translation;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// HUDServer — HTTP + WebSocket server for the OBS browser source overlay.
    /// Port 18080 by default. Serves static files from data/overlay/ and local media from the
    /// configured asset directory.
    ///
    /// Phase 2: per-layer routes added.
    ///   GET  /layer/&lt;id&gt;     — overlay HTML, browser reads ?layer=&lt;id&gt; query
    ///   GET  /api/layer/&lt;id&gt; — Layer JSON
    ///   WS   /hud/&lt;id&gt;       — per-layer WebSocket; presence tracked in LayerRegistry
    ///   WS   /hud            — legacy broadcast endpoint (no composition handlers in Phase 2)
    /// </summary>
    public class HUDServer : IDisposable
    {
        private HttpListener _listener;
        private readonly string _overlayPath;
        // The port this instance listens on — kept so the WebSocket origin gate can
        // require a same-authority Origin without re-deriving it from the prefixes.
        private readonly int _listenPort;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly LayerRegistry _layerRegistry;
        // TTL pulled from AppConfig.UrlImageCacheTtlHours so
        // operators can tune the URL-image cache freshness from settings.json without
        // a recompile. Initialized in the ctor body because field-initializer ordering
        // can't reach ConfigManager.Current safely if the field were inline-`new`'d.
        private readonly UrlImageCache _urlCache;

        // Per-socket send pumps. Each connected WebSocket gets one PerSocketSender
        // holding a bounded Channel<byte[]> (256 frames, DropOldest) drained by a
        // single-reader pump that owns the actual ws.SendAsync calls. Replaces the
        // previous _layerSendLocks SemaphoreSlim approach:
        //
        //   • Send-serialization contract preserved — only the pump's reader calls
        //     SendAsync, so concurrent producers never interleave frames on the same socket.
        //   • New: bounded buffer with drop-oldest policy. Producers no longer
        //     queue behind a slow client indefinitely — old frames fall off the
        //     back of the channel when the cap is reached, and the producer
        //     returns immediately. The drop count is logged at Communication
        //     tier so a slow OBS browser source surfaces in the syslog without
        //     stalling the script engine that owns the trigger.
        private readonly ConcurrentDictionary<WebSocket, PerSocketSender> _layerSenders = new();

        // ─── V13 §8.3 — per-socket TRUST classification ─────────────────────
        //
        // Membership = the socket presented this layer's current connect token on its
        // /hud/<id> upgrade, i.e. it is running a page THIS Hub process served.
        // Absence = Untrusted.
        //
        // PRESENCE-MEANS-TRUSTED is the deliberate direction, and it is the opposite of
        // _editorSockets in LayerRegistry (where absence means the common case). Here the
        // safe answer for an unknown socket is "declined the privileged upward paths", so
        // the exceptional case has to be the one that gets recorded. It is race-free
        // because the entry is written immediately after TryRegisterConnection succeeds
        // and BEFORE the receive loop that could deliver the socket's first frame — so a
        // legitimately trusted connection can never be observed un-recorded.
        //
        // Lifetime is exactly _layerSenders': written on registration, removed by the
        // grace-window teardown and swept in Stop(). A leaked entry would pin a dead
        // WebSocket object, which is why both removal sites matter.
        private readonly ConcurrentDictionary<WebSocket, byte> _trustedSockets = new();

        // V13 §8.3 — per-socket latch for the ONE self-heal reload offer (see
        // OfferSelfHealReloadOnce). Needed because the trigger — a LIVE_HELLO from an untrusted
        // socket — legitimately repeats on the same socket after every author save
        // (softReloadLayer re-sends the hello). Shares _layerSenders' / _trustedSockets' lifetime:
        // written on the first offer, removed by the grace teardown, swept in Stop().
        private readonly ConcurrentDictionary<WebSocket, byte> _selfHealOfferedSockets = new();

        // V13 §8.3 — one-shot latch per layer for the untrusted-connection notice, so an
        // OBS scene that cycles a stale-cached source doesn't repeat the same paragraph.
        //
        // Cleared ONLY in Stop(). Deliberately NOT pruned by OnLayerRemoved: LayerWatcher fires
        // Remove+Register on every `.phxlayer` SAVE, so pruning there would re-arm a "once per
        // layer" notice on every author save. Same reasoning LayerRegistry already records next to
        // the connect token it likewise refuses to drop on RemoveLayer. Bounded by the layer count.
        private readonly ConcurrentDictionary<string, byte> _untrustedLayerLogged = new();

        // Per-socket disconnect grace window. When OBS hides a browser source (or the
        // visibility flickers), the socket closes; we used to immediately drop the layer's
        // presence and any queued one-shot alerts. Instead, schedule a cancellable teardown
        // and let a fast reconnect restore presence without re-routing.
        //
        // Keyed by (LayerId, Socket), matching _layerSenders' per-socket keying. The
        // dict used to key by layerId alone, so a reconnect to the same layer within
        // the grace window cancelled the OLD socket's teardown and orphaned its
        // PerSocketSender (dict entry + Channel + parked pump Task + linked CTS) in
        // _layerSenders until Stop(). With the composite key each closed socket's
        // teardown fires independently; presence stays continuous across a reconnect
        // because LayerRegistry.UnregisterConnection only marks a layer inactive once
        // its connection set empties, and the new socket registers before the old
        // socket's grace window elapses.
        private const int LAYER_DISCONNECT_GRACE_SECONDS = 3;
        private readonly ConcurrentDictionary<(string LayerId, WebSocket Socket), CancellationTokenSource> _pendingTeardowns = new();

        // Recently-reloaded layer ids. PushLayerReloadedAsync stamps each broadcast
        // so /api/layer/<id> can wait for the writer to settle before serving JSON. Five-
        // second TTL is an arbitrary "long enough that the writer is done, short enough
        // that a static layer doesn't permanently pay the wait" window.
        //
        // Entries used to be removed only on the next /api/layer/<id> read,
        // so a layer that gets reloaded without any browser following up would pin its
        // stamp forever. A periodic Timer sweep (see _recentlyReloadedSweeper, started
        // from StartAsync and disposed in Stop) evicts entries older than the TTL.
        private readonly ConcurrentDictionary<string, DateTime> _recentlyReloaded = new();
        private System.Threading.Timer? _recentlyReloadedSweeper;
        private const int RECENT_RELOAD_SWEEP_INTERVAL_SECONDS = 60;

        // Per-layer PRODUCTION WebSocket connection cap. OBS only opens a small fixed
        // number of browser sources per scene (Preview + Studio = 2; one or two spares
        // for multi-monitor layouts), so 4 is generous. Without this cap an attacker
        // could fuzz /hud/<layerId> until the HashSet inside LayerRegistry exhausted
        // RAM; with it, the 5th concurrent connection per layer gets closed with code
        // 1013 (Try Again Later) and the receive loop never starts.
        //
        // V6 — this budget now counts PRODUCTION sockets only (LayerClientKind.Production).
        // It used to be the single shared budget, which was a live bug: Visualist can open
        // FOUR design surfaces on one layer (layer-canvas capture host, embedded
        // single-widget pane, layer popout, widget popout) and 4 was also the cap, so an
        // author with previews open could lock a real OBS Browser Source out of its own
        // layer. The symptom was "my overlay just stopped working" plus a 1013 close the
        // author never sees. Splitting the budgets means an OBS source is only ever
        // rejected by four OTHER OBS sources.
        private const int MAX_CONNECTIONS_PER_LAYER = 4;

        // V6 — per-layer EDITOR WebSocket budget, disjoint from the production one above.
        //
        // Why a bound at all rather than an exemption: ?client=editor is attacker-supplied
        // (anything that can reach /hud/<id> can append it), so an exempt kind would just
        // move the unbounded-growth hole behind a query param. Two bounded budgets keep the
        // amplification ceiling at MAX_CONNECTIONS_PER_LAYER + MAX_EDITOR_CONNECTIONS_PER_LAYER
        // per layer while making it impossible for either kind to starve the other.
        //
        // Sized 6 = the 4 design surfaces Visualist can actually open on one layer, plus 2
        // for the reconnect window. The dead-socket prune in TryRegisterConnection only
        // reclaims sockets that have LEFT WebSocketState.Open; a WebView2 torn down without
        // a close handshake leaves a half-open socket that still reads Open until a send
        // fails, so a preview pane that re-navigates can transiently hold two slots.
        // Under-sizing this would reject a preview pane, which is a visible authoring
        // failure, so the headroom is deliberate.
        private const int MAX_EDITOR_CONNECTIONS_PER_LAYER = 6;

        // Per-layer counter of "registered but no live socket"
        // RUN_TRIGGER drops. Used to rate-limit the diagnostic log so a tight
        // script firing into an inactive layer doesn't saturate the syslog.
        private readonly ConcurrentDictionary<string, long> _layerDropCounts = new();
        private const int RECENT_RELOAD_TTL_SECONDS = 5;

        // Per-webhook-name fixed-window rate limiter. Counts only successful (200) posts.
        // _webhookHits is keyed by attacker-controlled path content. Cap the
        // dict at WebhookHitsMaxKeys; when full we prune the oldest entries (LRU by
        // windowStart) before inserting. Combined with the new IsValidWebhookName
        // guard below, an unauthenticated attacker can no longer trigger unbounded
        // memory growth via crafted /webhook/<name> calls.
        private static readonly ConcurrentDictionary<string, (DateTime windowStart, int count, DateTime lastSeen)> _webhookHits = new();
        private const int WebhookRateLimitPerMin = 60;
        private const int WebhookHitsMaxKeys = 256;
        // Webhook names must satisfy the same identifier shape as layer
        // ids. 64-char cap is generous for any human-written name; tighter than
        // typical filesystem identifiers and well inside what the rate-limiter
        // dict can afford.
        private const int WebhookNameMaxLength = 64;

        // Single-shot warning when WebhookSecret is unset and a request comes in.
        // Demoted from `static` to instance field. The previous
        // process-static scope meant the operator-security warning fired once per
        // process even across HUDServer restarts (settings-reload, future hot-restart
        // surfaces), at which point a fresh-but-still-unset webhook secret would
        // not re-warn. Instance-scoped means each HUDServer instance gets its own
        // one-shot warning. Production has one HUDServer per Hub run so behaviour
        // is equivalent for the happy path; matters only for restart / test paths.
        private int _warnedNoSecret;

        /// <summary>Configurable root directory for local media assets served at /assets/.</summary>
        public string AssetDirectory { get; set; } = "";

        /// <summary>Configurable root directory for the user-imported media library served at /media/.</summary>
        public string MediaDirectory { get; set; } = "";

        public static bool IsStarted { get; private set; } = false;
        // Raised when the accept loop dies without an orderly Stop() / host
        // cancellation — the overlay server is down and only a Hub restart
        // brings it back. Static to match IsStarted: the status aggregator
        // observes the class-level lifecycle, not a specific instance.
        // Per-request faults never raise this; they continue the loop.
        public static event Action? OnFatalError;
        // Per-layer WebSocket connections live in `_layerRegistry`. ClientCount reads
        // from there; the legacy single-broadcast `_clients` list was retired when
        // every send path moved onto the registry, and the last untargeted senders
        // (BroadcastAsync / BroadcastRawAsync) went with the inert per-widget mutation
        // commands in V4 part C.
        // Test-only-reachable (same bucket as GetLastFps / CancelPendingTeardown):
        // no production caller reads this — LayerRouteTests asserts the fan-out
        // counts the registry, not a private client list. The status bar does NOT
        // subscribe or read here; it polls its own service counters on a timer.
        public int ClientCount => _layerRegistry.GetTotalConnectionCount();

        /// <summary>
        /// Webhook activity event.
        /// Fires once per accepted /webhook/&lt;name&gt; POST that survives the
        /// validation guards (name shape, body size, secret check, rate
        /// limit). The Hub WebhookPanel subscribes through HubHost.HUD to
        /// surface the last N posts as a tail panel.
        /// <para/>
        /// The event fires AFTER the 200 response is queued so a slow
        /// subscriber can't add latency to the webhook caller's round-trip.
        /// Subscribers MUST be fast + allocation-light; per
        /// <see cref="AsyncErrorBoundary"/> conventions we don't await the
        /// invocation so a faulting handler can't poison the dispatch path.
        /// </summary>
        public event Action<WebhookActivity>? OnWebhookFired;

        /// <summary>
        /// Rolling-average broadcast FPS surfaced
        /// for the StatusStrip's center-zone readout. The compositor sends
        /// one FPS sample per layer per second (see the WS receive loop's
        /// "FPS" case); each sample is stored on
        /// <see cref="LayerRegistry"/> via <c>RecordFps</c>. This getter
        /// averages the most-recent FPS sample across every active layer
        /// (TTL-gated to 5 s by <see cref="LayerRegistry.GetSnapshot"/>);
        /// the "rolling 60 frame" framing is folded
        /// into the compositor's own 1 s windowed counter, so a separate
        /// 60-sample ring buffer in the send loop would be redundant —
        /// and the HUDServer guardrail forbids altering the
        /// HTTP / WS handlers, which is where a sender-side ring would
        /// have to plug in. Returns 0.0 when no active layer has a
        /// recent FPS sample. Property is read-only and cheap: one
        /// snapshot allocation, one foreach.
        /// </summary>
        public double CurrentBroadcastFps
        {
            get
            {
                try
                {
                    var snap = _layerRegistry.GetSnapshot();
                    int sum = 0;
                    int n   = 0;
                    foreach (var entry in snap)
                    {
                        if (entry.Fps is int fps)
                        {
                            sum += fps;
                            n   += 1;
                        }
                    }
                    return n == 0 ? 0.0 : (double)sum / n;
                }
                catch
                {
                    // Snapshot can race a registry mutation in pathological
                    // shutdown windows; surface 0 rather than throw to the
                    // dispatcher's unhandled path.
                    return 0.0;
                }
            }
        }

        public HUDServer(int port = 18080, LayerRegistry? registry = null)
        {
            _layerRegistry = registry ?? LayerRegistry.Instance;
            // Prune the per-layer drop-count diagnostic dict when a
            // layer is removed from the registry (Deleted/Renamed-away in
            // LayerWatcher). Without this hook the dict grew across the Hub
            // process lifetime — bounded by attacker-controlled layer ids? no,
            // but bounded by every layer the user ever loaded, which adds up
            // for streamers iterating layouts. The subscriber is unhooked in
            // Stop() so a recreated HUDServer doesn't accumulate them on the
            // singleton registry.
            _layerRegistry.LayerRemoved += OnLayerRemoved;
            // HUDServer owns the LayerReloaded → /hud broadcast forwarding
            // itself (unhooked in Stop) — an externally-wired anonymous lambda
            // on the process-lifetime registry pinned every disposed HUDServer
            // for the rest of the session.
            _layerRegistry.LayerReloaded += OnLayerReloadedRegistry;
            _overlayPath = ResolveOverlayPath();
            _listenPort  = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add($"http://localhost:{port}/");

            // Wire UrlImageCache TTL from AppConfig. Negative/zero values fall
            // back to UrlImageCache's internal 24h default (its ctor rejects non-positive).
            _urlCache = new UrlImageCache(
                ttl: TimeSpan.FromHours(ConfigManager.Current.UrlImageCacheTtlHours));

            // Default asset / media directories. Production deploys put the
            // folders next to the exe; dev runs from bin/Debug/... need to
            // walk up to the Phoenix.Controls.Hub source tree because the
            // csproj doesn't copy data/ into bin (intentional — content is
            // hot-reloaded from the source). The walk-up matches what
            // ResolveOverlayPath / TryResolveLayerFilePath already do for the
            // overlay HTML and layer JSON; without it, /media/<rel> 404s on
            // every dev-run request even though the file exists.
            AssetDirectory = ResolveDataSubfolder("assets", Paths.AppAssets);
            MediaDirectory = ResolveMediaRoot();
            try { Directory.CreateDirectory(MediaDirectory); } catch { /* best-effort */ }
        }

        /// <summary>
        /// The media-library root <c>/media/&lt;rel&gt;</c> serves from, resolved WITHOUT
        /// constructing a server. Design-time surfaces that need to offer the streamer a
        /// clip (the Soundboard tool's picker) must enumerate the same folder the overlay
        /// will later fetch from, and the two resolutions differ in a real corner case:
        /// <see cref="Paths.HubMedia"/> alone points into the source tree whenever
        /// <c>data/</c> exists, even if <c>data/media</c> has never been created, whereas
        /// this falls back to the AppBase copy the ctor then creates. A picker listing one
        /// folder while the overlay reads another produces a clip that plays in no OBS —
        /// so there is exactly one resolver and both callers use it.
        /// </summary>
        public static string ResolveMediaRoot() => ResolveDataSubfolder("media", Paths.AppMedia);

        /// <summary>
        /// Resolve a Hub <c>data/&lt;leaf&gt;</c> directory via the solution-anchored
        /// resolver in <see cref="Paths"/>. In dev this points at the source tree;
        /// in production it falls back to <see cref="Paths.AppData"/> next to the .exe.
        /// Returns <paramref name="productionFallback"/> only if the resolved folder
        /// doesn't exist (first-run, before the user has created any media/assets).
        /// </summary>
        private static string ResolveDataSubfolder(string leaf, string productionFallback)
        {
            string resolved = Paths.HubData(leaf);
            return Directory.Exists(resolved) ? resolved : productionFallback;
        }

        /// <summary>
        /// Resolve the path to the overlay HTML/JS directory via the solution-anchored
        /// resolver. Falls back to <see cref="Paths.AppOverlay"/> when the resolved
        /// folder lacks <c>index.html</c> (e.g., first-run before content is committed).
        /// </summary>
        private static string ResolveOverlayPath()
        {
            string baseGuess = Paths.AppOverlay;
            if (File.Exists(Path.Combine(baseGuess, "index.html"))) return baseGuess;

            string resolved = Paths.HubOverlay;
            if (File.Exists(Path.Combine(resolved, "index.html"))) return resolved;

            return baseGuess;
        }

        /// <summary>
        /// Clear this layer's drop-count slot so the per-layer
        /// diagnostic dict can't grow without bound as layers come and go.
        /// Wired in the ctor; unhooked in <see cref="Stop"/>.
        /// </summary>
        private void OnLayerRemoved(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            _layerDropCounts.TryRemove(layerId, out _);
            // V6 — same prune for the refused-editor-inbound counters. Keyed
            // "<layerId>|<messageType>", so the whole layer's slots go together.
            _editorRefusalCounts.TryRemove($"{layerId}|VISUAL_COMPLETE", out _);
            _editorRefusalCounts.TryRemove($"{layerId}|FPS", out _);
            // V13 — the production-refusal COUNTER is pruned (it is a rate limiter keyed
            // "<layerId>|<messageType>"; re-arming it only means the next hundred-line window
            // starts over).
            //
            // ★ _untrustedLayerLogged is deliberately NOT pruned here, for exactly the reason
            // LayerRegistry does not prune the connect token on RemoveLayer: LayerWatcher fires
            // Remove+Register on every `.phxlayer` SAVE, so pruning would re-arm a latch that is
            // documented — and tested — as firing ONCE per layer, and an author iterating on a
            // layer would re-read the same six-line paragraph after every save. The latch keys the
            // layer ID, not the layer CONTENT, so surviving a reload is the correct behaviour. It
            // stays bounded by the real layer count (same bound as the token map) and is cleared in
            // Stop().
            _productionRefusalCounts.TryRemove($"{layerId}|DEBUG_WIDGET_NODE", out _);
            // Symmetric prune for the reload-stamp dict so a layer
            // removed mid-grace-window doesn't leak its entry until the next
            // periodic sweep.
            _recentlyReloaded.TryRemove(layerId, out _);
            // Overlay Live Channel — a removed layer can never receive another
            // frame, so its subscription + dirty set go with the diagnostic slots
            // above. The socket-close path only clears when a browser actually
            // disconnects; a .phxlayer deleted while OBS still holds the source
            // open would otherwise keep the layer dirtying the pump against an id
            // that no longer resolves.
            OverlayLiveStore.Instance.ClearLayer(layerId);
        }

        /// <summary>
        /// Forwards LayerRegistry.LayerReloaded to every connected /hud/&lt;id&gt;
        /// browser (OBS sources + Visualist's capture page reload via
        /// compositor.js's LAYER_RELOADED handler). Wired in the ctor; unhooked
        /// in <see cref="Stop"/> so a disposed HUDServer can't be re-entered by
        /// a later registry fire.
        /// </summary>
        private void OnLayerReloadedRegistry(string layerId)
        {
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => PushLayerReloadedAsync(layerId),
                "HUDServer", "PushLayerReloadedAsync");
        }

        /// <summary>
        /// Drop <see cref="_recentlyReloaded"/> entries older than the
        /// TTL. Runs on the Timer wired in <see cref="StartAsync"/>; the cost is
        /// O(N) over a dict that's bounded by the active layer count, so a
        /// 60-second cadence is more than fast enough.
        /// </summary>
        internal void SweepRecentlyReloaded()
        {
            try
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(RECENT_RELOAD_TTL_SECONDS);
                foreach (var kv in _recentlyReloaded)
                {
                    if (kv.Value < cutoff)
                    {
                        _recentlyReloaded.TryRemove(kv.Key, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HUDServer", "_recentlyReloaded sweep faulted", ex);
            }
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            // Accept a caller CT and link it to the internal _cts so a host
            // shutdown signal (HubBootstrapper / app exit) tears the accept loop down
            // alongside an explicit Stop(). Bus.StartAsync already takes a CT; this
            // brings HUDServer to parity. The linked token is used for the accept
            // loop's wait observation; long-lived per-request work continues to use
            // _cts.Token directly so Stop() alone still cancels everything in flight.
            using var linkedCts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct)
                : null;
            var loopToken = linkedCts?.Token ?? _cts.Token;

            _listener.Start();
            IsStarted = true;
            GlobalLogger.Log($"HUD Server started: {_listener.Prefixes.First()}", "HUDServer", LogLevel.System);
            // Surface the resolved data roots so dev runs can confirm they're
            // pointing at the source tree (the bin/Debug/... auto-created
            // folder used to be a silent 404 source for /media/ requests).
            GlobalLogger.Log($"HUD Server media root: {MediaDirectory}", "HUDServer", LogLevel.System);
            GlobalLogger.Log($"HUD Server asset root: {AssetDirectory}", "HUDServer", LogLevel.System);
            GlobalLogger.Log($"HUD Server overlay root: {_overlayPath}", "HUDServer", LogLevel.System);

            // Periodic sweep of _recentlyReloaded stale entries.
            // ServeLayerJsonAsync only removes the stamp on a hit, so a reload
            // without a follow-up /api/layer/<id> request used to pin the entry
            // forever. Sweep every 60s and drop entries older than the TTL.
            _recentlyReloadedSweeper = new System.Threading.Timer(
                _ => SweepRecentlyReloaded(),
                state:    null,
                dueTime:  TimeSpan.FromSeconds(RECENT_RELOAD_SWEEP_INTERVAL_SECONDS),
                period:   TimeSpan.FromSeconds(RECENT_RELOAD_SWEEP_INTERVAL_SECONDS));

            while (_listener.IsListening && !loopToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    if (context.Request.IsWebSocketRequest)
                        _ = AsyncErrorBoundary.SafeRunAsync(() => ProcessWebSocketRequestAsync(context), "HUDServer", "WS handler");
                    else
                        _ = AsyncErrorBoundary.SafeRunAsync(() => ProcessHttpRequestAsync(context), "HUDServer", "HTTP handler");
                }
                catch (HttpListenerException) when (!_listener.IsListening) { break; }
                catch (ObjectDisposedException) when (!_listener.IsListening) { break; }
                catch (OperationCanceledException) when (loopToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Per-request accept fault — logged and the loop continues;
                    // this path never counts as a fatal exit.
                    GlobalLogger.Error("HUDServer", "accept error", ex);
                }
            }

            // Distinguish an orderly shutdown (Stop() latched _stopped, or the
            // host / linked CT cancelled) from the accept loop dying on its
            // own — e.g. HttpListener dropping out of IsListening after an
            // unrecoverable OS-level fault. Only the abnormal exit is
            // announced: IsStarted flips false so pollers stop reporting a
            // healthy overlay server, and OnFatalError lets the status
            // aggregator mark the HUD channel Errored immediately.
            if (IsStarted && Volatile.Read(ref _stopped) == 0 && !loopToken.IsCancellationRequested)
            {
                IsStarted = false;
                GlobalLogger.Log(
                    "HUD Server accept loop exited unexpectedly — overlay serving is down until Hub restarts.",
                    "HUDServer", LogLevel.CriticalError);
                // Per-handler isolation, mirroring the Bus / WS fan-out
                // pattern — one faulting subscriber must not skip the rest.
                var handlers = OnFatalError?.GetInvocationList();
                if (handlers is not null)
                {
                    foreach (var d in handlers)
                    {
                        try { ((Action)d)(); }
                        catch (Exception ex)
                        {
                            GlobalLogger.Error("HUDServer", "OnFatalError subscriber threw", ex);
                        }
                    }
                }
            }
        }

        private int _stopped;

        public void Dispose() => Stop();

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

            // Unhook the registry subscriptions so a recreated HUDServer doesn't
            // accumulate stale handlers on the singleton LayerRegistry.
            try { _layerRegistry.LayerRemoved -= OnLayerRemoved; } catch { }
            // Symmetric unhook for the LayerReloaded forwarder (omitting it leaked
            // one disposed HUDServer reference per session and re-entered it on
            // later reloads).
            try { _layerRegistry.LayerReloaded -= OnLayerReloadedRegistry; } catch { }

            try { _cts.Cancel(); } catch { }

            // Stop the recently-reloaded sweep before the dict is
            // torn down so a late timer fire can't race the clear below.
            try { _recentlyReloadedSweeper?.Dispose(); } catch { }
            _recentlyReloadedSweeper = null;
            _recentlyReloaded.Clear();

            try { _listener.Stop(); }    catch (ObjectDisposedException) { } catch (Exception ex) { GlobalLogger.Error("HUDServer", "stop error", ex); }
            try { _listener.Close(); }   catch { }

            // Cancel any pending grace-window teardowns BEFORE we
            // dispose the per-socket senders. The previous order disposed the
            // _layerSenders first, leaving the teardown callbacks (still
            // queued through AsyncErrorBoundary) racing against a cleared
            // dict — they would TryRemove against a sender that no longer
            // existed and silently no-op, but the UnregisterConnection /
            // sender.Dispose calls inside those callbacks could fault on a
            // disposed socket. Cancelling here makes the callbacks return
            // immediately on Task.Delay observation, after which the sweep
            // below evicts any senders the receive-loops didn't unwind.
            foreach (var kv in _pendingTeardowns)
            {
                try { kv.Value.Cancel(); } catch { }
                try { kv.Value.Dispose(); } catch { }
            }
            _pendingTeardowns.Clear();

            // Unregister every per-layer connection from the LayerRegistry so
            // presence reflects "no browsers attached" after Stop(). Without this the
            // next StartAsync() reuses a registry that still reports the previous
            // session's layers as active, which breaks LayerRuntime's "skip dispatch
            // when nobody is listening" fast-path (queued triggers leak into the
            // restarted server thinking a phantom client is there). Mirrors the
            // close-on-disconnect path that the receive-loop's finally normally runs
            // — just triggered for everything during a hard Stop.
            //
            // V6 — GetLayerIdsWithAnyConnection, NOT GetActiveLayerIds. Presence is now
            // production-only, so a layer whose sole client is a Visualist preview is no
            // longer "active" — and iterating the presence accessor here would skip it,
            // leaking its sockets in _connections across a Stop→StartAsync and resurrecting
            // exactly the stale-presence bug this sweep exists to fix.
            try
            {
                foreach (var layerId in _layerRegistry.GetLayerIdsWithAnyConnection())
                {
                    foreach (var ws in _layerRegistry.GetConnections(layerId))
                    {
                        try { _layerRegistry.UnregisterConnection(layerId, ws); } catch { }
                    }
                    // Overlay Live Channel — drop the subscription with the
                    // presence. Neither of the two normal clear paths can cover a
                    // hard Stop(): the LayerRemoved unhook above already fired, and
                    // the grace-window teardowns (which hold the last-socket clear)
                    // are cancelled a few lines below, so their callbacks return
                    // without running. Without this, a Stop→StartAsync restart —
                    // the case the unregister sweep itself exists for — would leave
                    // the pump dirtying layers whose browsers are gone, and the next
                    // browser's LIVE_HELLO is the only thing that would ever
                    // overwrite the stale key set.
                    try { OverlayLiveStore.Instance.ClearLayer(layerId); } catch { }
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HUDServer", "layer unregister on stop", ex);
            }

            // Drain any per-socket send pumps still alive. Disposing the
            // PerSocketSender completes its channel writer and cancels the
            // pump CT; any queued frames are silently dropped. Receive-loop
            // finally + grace-teardown normally evict the sender first, but
            // a hard Stop() may abort sockets before they unwind so we sweep
            // the remainder here.
            //
            // Runs AFTER _pendingTeardowns are cancelled above so a
            // late grace callback can't race the dict mutation below.
            foreach (var kv in _layerSenders)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _layerSenders.Clear();

            // V13 §8.3 — sweep the per-socket trust records and the per-layer notice
            // latches with the senders they shadow. A hard Stop() can abort sockets before
            // their grace teardowns run, so without this sweep the dict would hold dead
            // WebSocket references across a Stop→StartAsync (same leak class the sender
            // sweep above exists for), and every layer would stay permanently "already
            // warned" for the next session.
            _trustedSockets.Clear();
            _selfHealOfferedSockets.Clear();
            _untrustedLayerLogged.Clear();

            // Wipe the per-layer drop-count dict on Stop too. Even
            // with the LayerRemoved hook, a Hub shutdown that occurs while
            // layers are still registered would leave entries pinned for the
            // next instance.
            _layerDropCounts.Clear();

            // _listener (HttpListener) and _urlCache
            // (UrlImageCache, IDisposable) were Stop()/Close()'d / never disposed,
            // leaking the listener's OS handle and the cache's HttpClient/timer
            // across a Stop→Start restart. Dispose both here, after the per-socket
            // pumps are drained, before the CTS goes away.
            try { (_listener as IDisposable)?.Dispose(); } catch { }
            try { _urlCache?.Dispose(); } catch { }

            try { _cts.Dispose(); } catch { }

            IsStarted = false;
        }

        // ──────────────────────────────────────────────────────────────────
        //  HTTP REQUEST HANDLING
        // ──────────────────────────────────────────────────────────────────

        private async Task ProcessHttpRequestAsync(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod ?? "";

            try
            {
                // Every route below assumes GET (or POST for /webhook).
                // Verbs other than the allowed set used to fall through to
                // ServeStaticFileAsync, which happily served a 200 for DELETE /index.html
                // or PUT /assets/foo.png. None of the handlers honor side-effecting
                // verbs, but accepting them is a contract violation that confuses
                // proxies and security scanners. Reject mismatched verbs up front
                // with the right 405 + Allow header for each route family.
                //
                // OPTIONS handling: the only POST-accepting route is /webhook/<name>,
                // so a CORS preflight there gets a 204 with the right Allow / CORS
                // headers. Every other route is GET-only; preflights against those
                // fall through to the 405 path with Allow: GET, HEAD.
                if (path.StartsWith("/webhook/", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    SendCorsPreflight(context, "POST, OPTIONS", "Content-Type, X-PhoenixControls-Secret");
                    return;
                }
                if (path.StartsWith("/webhook/", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    SendMethodNotAllowed(context, "POST, OPTIONS");
                    return;
                }
                if ((path.StartsWith("/layer/",     StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/layer/", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/assets/",    StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/media/",     StringComparison.OrdinalIgnoreCase) ||
                     path.Equals    ("/api/media",  StringComparison.OrdinalIgnoreCase) ||
                     path.Equals    ("/asset/url",  StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(method, "GET",  StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    SendMethodNotAllowed(context, "GET, HEAD");
                    return;
                }

                // /webhook/{name} — inbound POST relay; fires on_webhook(name) in Hub scripts
                if (path.StartsWith("/webhook/", StringComparison.OrdinalIgnoreCase)
                    && context.Request.HttpMethod == "POST")
                {
                    string name = path.Substring("/webhook/".Length).Trim('/');
                    string remote = context.Request.RemoteEndPoint?.ToString() ?? "?";

                    // Same identifier shape as layer ids ([A-Za-z0-9_-]+),
                    // capped at 64 chars. Without this, attacker-supplied names
                    // (path segments, embedded slashes, multi-MB strings) ended up
                    // as static dict keys in _webhookHits — unbounded RAM growth
                    // keyed by attacker-controlled content. Reject before any work.
                    if (!IsValidWebhookName(name))
                    {
                        GlobalLogger.Log($"Webhook rejected: invalid name '{TruncateForLog(name, 32)}' (from {remote})", "HUDServer", LogLevel.Communication);
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    // Body size cap. Reject before reading the body.
                    int maxBody = ConfigManager.Current.MaxWebhookBodyBytes > 0
                        ? ConfigManager.Current.MaxWebhookBodyBytes
                        : 1024 * 1024;
                    long advertised = context.Request.ContentLength64;
                    if (advertised > maxBody)
                    {
                        GlobalLogger.Log($"Webhook /{name} rejected: Content-Length {advertised} > {maxBody} (from {remote})", "HUDServer", LogLevel.Communication);
                        context.Response.StatusCode = 413; // Payload Too Large
                        context.Response.Close();
                        return;
                    }

                    // Auth via X-PhoenixControls-Secret header (constant-time compare).
                    // Per-webhook secret
                    // override. WebhookSecrets[<name>] takes precedence when set;
                    // the legacy global WebhookSecret stays as the fallback so
                    // existing single-secret integrations keep working unchanged.
                    // If BOTH are empty, the request is rejected with 401 — no
                    // silent allow (the legacy "accept-all + warn-once" branch
                    // is gone; an unconfigured webhook is an explicit reject).
                    var cfg = ConfigManager.Current;
                    string configuredSecret = "";
                    if (cfg.WebhookSecrets is { } perEndpoint
                        && perEndpoint.TryGetValue(name, out var perSecret)
                        && !string.IsNullOrEmpty(perSecret))
                    {
                        configuredSecret = perSecret;
                    }
                    else
                    {
                        configuredSecret = cfg.WebhookSecret ?? "";
                    }

                    if (string.IsNullOrEmpty(configuredSecret))
                    {
                        // Both per-endpoint and global secrets are empty — refuse
                        // the request. Single-shot CriticalError on first hit so
                        // the operator sees the misconfiguration once instead of
                        // a flood, but the response itself stays 401 every time.
                        if (Interlocked.Exchange(ref _warnedNoSecret, 1) == 0)
                        {
                            GlobalLogger.Log(
                                "WebhookSecret + WebhookSecrets are both unset — /webhook/{name} requests are rejected with 401. Configure AppConfig.WebhookSecret (global fallback) or AppConfig.WebhookSecrets[<name>] (per-endpoint override) in Hub Settings.",
                                "HUDServer", LogLevel.CriticalError);
                        }
                        GlobalLogger.Log($"Webhook /{name} rejected: no secret configured (from {remote})", "HUDServer", LogLevel.Communication);
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        return;
                    }

                    {
                        string presented = context.Request.Headers["X-PhoenixControls-Secret"] ?? "";
                        byte[] a = Encoding.UTF8.GetBytes(configuredSecret);
                        byte[] b = Encoding.UTF8.GetBytes(presented);
                        bool match = a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
                        if (!match)
                        {
                            GlobalLogger.Log($"Webhook /{name} rejected: bad/missing X-PhoenixControls-Secret (from {remote})", "HUDServer", LogLevel.Communication);
                            context.Response.StatusCode = 401;
                            context.Response.Close();
                            return;
                        }
                    }

                    // Read body with bounded buffer (handles chunked encoding too).
                    string body;
                    try
                    {
                        body = await ReadBodyWithCapAsync(context.Request.InputStream, maxBody, _cts.Token).ConfigureAwait(false);
                    }
                    catch (InvalidDataException)
                    {
                        GlobalLogger.Log($"Webhook /{name} rejected: body exceeds {maxBody} bytes during read (from {remote})", "HUDServer", LogLevel.Communication);
                        context.Response.StatusCode = 413;
                        context.Response.Close();
                        return;
                    }

                    // Rate-limit successful posts only — bump AFTER auth+size pass.
                    if (!TryRecordWebhookHit(name))
                    {
                        GlobalLogger.Log($"Webhook /{name} rate-limited (>{WebhookRateLimitPerMin}/min, from {remote})", "HUDServer", LogLevel.Communication);
                        context.Response.StatusCode = 429;
                        context.Response.Headers["Retry-After"] = "60";
                        context.Response.Close();
                        return;
                    }

                    string safeBody = string.IsNullOrWhiteSpace(body) ? "{}" : body;
                    // The previous `using var doc = JsonDocument.Parse(...)`
                    // disposed the document at the end of this `try` block, but the
                    // fire-and-forget SafeRunAsync continuation reads `doc.RootElement`
                    // AFTER that point — racing the synchronous return of this method
                    // and producing intermittent ObjectDisposedException under load.
                    // Clone the RootElement so the captured value is independent of
                    // the disposed document.
                    JsonElement payloadElement;
                    try
                    {
                        using var doc = JsonDocument.Parse(safeBody);
                        payloadElement = doc.RootElement.Clone();
                    }
                    catch
                    {
                        // Non-JSON body — pass as plain text under a "body" key
                        using var doc = JsonDocument.Parse($"{{\"body\":{JsonSerializer.Serialize(safeBody)}}}");
                        payloadElement = doc.RootElement.Clone();
                    }

                    var capturedName    = name;
                    var capturedPayload = payloadElement;
                    // Forward the raw body + path so the webhook node's
                    // Body/Method/Path outputs resolve (method is always POST here).
                    var capturedBody    = safeBody;
                    var capturedPath    = path;
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        () => ScriptManager.Instance.ExecuteOnWebhookScriptsAsync(capturedName, capturedPayload, capturedBody, "POST", capturedPath),
                        "HUDServer", $"webhook {capturedName}");
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                    GlobalLogger.Log($"Webhook: /{name}", "HUDServer", LogLevel.StreamEvent);

                    // Fan the activity
                    // out to the WebhookPanel via the OnWebhookFired event. The
                    // event fires AFTER the response is queued so a slow
                    // subscriber can't add latency to the caller round-trip;
                    // any subscriber exception is swallowed inline so a bad
                    // listener can never poison the dispatch path. PayloadSize
                    // captures the body length we just accepted (the cap is
                    // enforced above; the actual bytes-read are the final
                    // length of safeBody before we wrapped JSON parsing).
                    var fired = OnWebhookFired;
                    if (fired is not null)
                    {
                        try
                        {
                            var activity = new WebhookActivity(
                                FiredAtUtc:    DateTime.UtcNow,
                                Endpoint:      $"/webhook/{name}",
                                RemoteAddress: remote,
                                PayloadBytes:  Encoding.UTF8.GetByteCount(safeBody));
                            foreach (var sub in fired.GetInvocationList())
                            {
                                try { ((Action<WebhookActivity>)sub).Invoke(activity); }
                                catch (Exception ex)
                                {
                                    GlobalLogger.Error("HUDServer",
                                        "WebhookActivity subscriber threw", ex);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Defensive — the activity construction itself
                            // should never throw, but we log + continue rather
                            // than let an activity-side bug fault the request.
                            GlobalLogger.Error("HUDServer",
                                "WebhookActivity fan-out threw", ex);
                        }
                    }
                    return;
                }

                // /assets/ route: serve local media files from AssetDirectory
                if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeAssetAsync(context, path.Substring("/assets/".Length));
                    return;
                }

                // /media/ route: serve user-imported media library from MediaDirectory.
                // Mirrors /assets/ but rooted at data/media/ instead of data/assets/.
                if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeMediaAsync(context, path.Substring("/media/".Length));
                    return;
                }

                // /api/media — JSON listing of every file under MediaDirectory.
                if (path.Equals("/api/media", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeMediaListingAsync(context);
                    return;
                }

                // /asset/url?u=<encoded>[&_ts=<bucket>] — Phase 7 URL-image cache proxy.
                if (path.Equals("/asset/url", StringComparison.OrdinalIgnoreCase))
                {
                    string? remote = context.Request.QueryString["u"];
                    // `_ts` is compositor.js's cache-busting bucket for
                    // WebSource.RefreshSeconds — it flips value once per refresh
                    // window. Forward it to the cache as an opaque freshness
                    // token so a value we haven't fetched under yet revalidates
                    // against the origin. Dropping it (as this route used to)
                    // pinned every WebSource to the 24h UrlImageCacheTtlHours
                    // default, so a live scoreboard painted one frame at the
                    // start of the stream and then never changed again.
                    string? bucket = context.Request.QueryString["_ts"];
                    await ServeCachedUrlAsync(context, remote, bucket);
                    return;
                }

                // /api/layer/<id> — return Layer JSON
                if (path.StartsWith("/api/layer/", StringComparison.OrdinalIgnoreCase))
                {
                    string id = path.Substring("/api/layer/".Length).Trim('/');
                    if (!IsValidLayerId(id))
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }
                    await ServeLayerJsonAsync(context, id);
                    return;
                }

                // /layer/<id> — serve overlay HTML (browser reads ?layer=<id> from URL)
                if (path.StartsWith("/layer/", StringComparison.OrdinalIgnoreCase))
                {
                    // Accept only [A-Za-z0-9_-]+ ids and reject paths with extra
                    // segments like `/layer/foo/bar` to keep the route surface tight.
                    string id = path.Substring("/layer/".Length).Trim('/');
                    if (!IsValidLayerId(id))
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }
                    await ServeLayerHtmlAsync(context, id);
                    return;
                }

                // Overlay static files — only GET / HEAD accepted.
                if (!string.Equals(method, "GET",  StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    SendMethodNotAllowed(context, "GET, HEAD");
                    return;
                }
                // V13 §8.3 — `/`, `/overlay` and `/index.html` are a SUPPORTED overlay
                // configuration, not a legacy accident: compositor.js still resolves its layer from
                // `params.get('layer')` on purpose and does not care what path served the page, so
                // `/?layer=main` and `/index.html?layer=main` are both valid OBS Browser Source
                // URLs. They used to rewrite straight to /index.html and fall through to
                // ServeStaticFileAsync, which bypassed ServeLayerHtmlAsync — and therefore the
                // connect-token injection, the CSP header AND the ?v= cache-bust. A source
                // configured this way could never become Trusted, and the notice it triggered named
                // a cache refresh that could not possibly help, because the served page had no
                // token to pick up no matter how often it was re-fetched.
                //
                // So resolve the id the same way the page will (query, else compositor's own
                // 'main' fallback) and serve it through the one HTML path.
                //
                // ★ /index.html is spelled OrdinalIgnoreCase deliberately. The other two spellings
                // are exact because a mis-cased `/OVERLAY` never reached the page at all (no such
                // file, so File.Exists said no and the request 404'd) — but `/INDEX.HTML` DID serve
                // the page, because Windows' file system is case-insensitive and File.Exists
                // matched index.html. Matching case-sensitively here would have left that exact
                // spelling as a live bypass of the very injection this arm exists to guarantee.
                //
                // The resolve-first guard keeps this strictly non-regressive: an id that does not
                // resolve to a layer falls back to the ORIGINAL static /index.html behaviour rather
                // than turning a previously-served page into a 404. ServeLayerHtmlAsync would 404
                // on its own resolve, so the pre-check is what preserves the old surface. It costs
                // one registry lookup on a route OBS hits once per source.
                if (path == "/" || path == "/overlay" ||
                    path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    string rootId = (context.Request.QueryString["layer"] ?? "").Trim();
                    if (rootId.Length == 0) rootId = DefaultRootLayerId;
                    if (IsValidLayerId(rootId) && await GetOrLoadLayerResilientAsync(rootId) is not null)
                    {
                        await ServeLayerHtmlAsync(context, rootId);
                        return;
                    }
                    path = "/index.html";
                }

                // Pipe the /overlay static-file fallback through the same path-
                // traversal guard /assets/ and /media/ enforce. Previously HttpListener's
                // URL canonicalization was the sole defense; a malicious overlay HTML or
                // a misconfigured browser source crafting `/..\..\foo` could escape
                // _overlayPath. We canonicalize via Path.GetFullPath then assert the
                // result still sits under the overlay root.
                string overlayRoot = Path.GetFullPath(_overlayPath);
                string fullPath    = Path.GetFullPath(Path.Combine(_overlayPath, path.TrimStart('/')));
                if (!fullPath.StartsWith(overlayRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !fullPath.Equals(overlayRoot, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                if (File.Exists(fullPath))
                {
                    await ServeStaticFileAsync(context, fullPath, path);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HUDServer", "HTTP error", ex);
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }

        private Task ServeAssetAsync(HttpListenerContext context, string relativePath) =>
            ServeFileFromRootAsync(context, relativePath, AssetDirectory);

        private Task ServeMediaAsync(HttpListenerContext context, string relativePath) =>
            ServeFileFromRootAsync(context, relativePath, MediaDirectory);

        private async Task ServeFileFromRootAsync(HttpListenerContext context, string relativePath, string rootDir)
        {
            // Sanitize path to prevent directory traversal
            relativePath = relativePath.TrimStart('/');
            string fullPath = Path.GetFullPath(Path.Combine(rootDir, relativePath));

            // Reject any path that escapes the asset/media directory
            string assetRoot = Path.GetFullPath(rootDir);
            if (!fullPath.StartsWith(assetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.Equals(assetRoot, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            if (!File.Exists(fullPath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            string mime = GetMimeType(fullPath);
            long fileSize = new FileInfo(fullPath).Length;
            string? rangeHeader = context.Request.Headers["Range"];

            context.Response.ContentType = mime;
            context.Response.Headers["Accept-Ranges"] = "bytes";

            // Delegate range parsing to the shared TryParseRange helper. The
            // previous inline parser threw on inverted (`bytes=100-50`) and out-of-range
            // headers — every malformed Range hit a 500 instead of a 416. The helper
            // already handles suffix, open-ended, multi-range, OOR, and inverted forms
            // and returns false on anything unsatisfiable so we can fall through to the
            // 200 full-file response.
            if (TryParseRange(rangeHeader, fileSize, out long start, out long end))
            {
                long length = end - start + 1;

                context.Response.StatusCode = 206;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{fileSize}";
                context.Response.ContentLength64 = length;

                using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(start, SeekOrigin.Begin);
                int bufSize = (int)Math.Min(length, 65536);
                var buffer = new byte[bufSize];
                long remaining = length;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    // Thread _cts.Token through the disk read and response write.
                    // ServeStaticFileAsync (line 812-814) already does this; ServeFileFromRootAsync
                    // was missed and would hang shutdown on a slow client.
                    int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), _cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            else
            {
                // Full file
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = fileSize;
                using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // Supply _cts.Token on the full-file copy too.
                await fs.CopyToAsync(context.Response.OutputStream, bufferSize: 65536, _cts.Token).ConfigureAwait(false);
            }
            context.Response.Close();
        }

        // Serves a flat JSON list of every file under MediaDirectory keyed by
        // relative path. Used by the Visualist Media Library window and (in the
        // future) browser-side picker. Response shape:
        //   [{ "rel": "images/welcome.png", "kind": "image", "sizeBytes": 1234, "mtime": "2026-04-29T..." }, ...]
        private static readonly byte[] EmptyJsonArray = System.Text.Encoding.UTF8.GetBytes("[]");
        private async Task ServeMediaListingAsync(HttpListenerContext context)
        {
            try
            {
                string rootFull = Path.GetFullPath(MediaDirectory);
                if (!Directory.Exists(rootFull))
                {
                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode = 200;
                    var empty = EmptyJsonArray;
                    context.Response.ContentLength64 = empty.Length;
                    // Thread _cts.Token through the OutputStream write so the
                    // empty-list response can be torn down on Stop(). Sister methods at
                    // ServeFileFromRootAsync / ServeStaticFileAsync already do this.
                    await context.Response.OutputStream.WriteAsync(empty.AsMemory(0, empty.Length), _cts.Token).ConfigureAwait(false);
                    context.Response.Close();
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.Append('[');
                bool first = true;
                foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
                    string kind = MediaKindForExtension(Path.GetExtension(file));
                    if (kind == "other") continue; // skip unknown extensions
                    var info = new FileInfo(file);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{')
                      .Append("\"rel\":").Append(JsonSerializer.Serialize(rel)).Append(',')
                      .Append("\"kind\":\"").Append(kind).Append("\",")
                      .Append("\"sizeBytes\":").Append(info.Length).Append(',')
                      .Append("\"mtime\":\"").Append(info.LastWriteTimeUtc.ToString("o")).Append('"')
                      .Append('}');
                }
                sb.Append(']');
                var body = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = body.Length;
                // Supply _cts.Token on the full-listing write so Stop() can
                // abort a slow client mid-flush. Matches the CT-bearing writes at
                // lines 703 / 714 / 1011.
                await context.Response.OutputStream.WriteAsync(body.AsMemory(0, body.Length), _cts.Token).ConfigureAwait(false);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HUDServer", "/api/media listing failed", ex);
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }

        /// <summary>Coarse media classification from a file extension —
        /// "image" / "video" / "audio" / "other". Public because it is the one
        /// definition of what the media library considers playable: the
        /// <c>/api/media</c> listing filters on it, and so does the Soundboard tool's
        /// clip picker, which must offer exactly the files the listing would name.</summary>
        public static string MediaKindForExtension(string ext) => ext.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" => "image",
            ".mp4" or ".webm" or ".mov" or ".m4v"                       => "video",
            ".mp3" or ".wav" or ".ogg" or ".m4a" or ".flac"             => "audio",
            _                                                            => "other",
        };

        private async Task ServeCachedUrlAsync(HttpListenerContext context, string? remoteUrl, string? freshnessToken = null)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            // Cheap pre-reject before touching the cache directory.
            // UrlImageCache validates again as defense-in-depth.
            var (ok, reason) = await _urlCache.ValidateUrlForFetchAsync(remoteUrl, _cts.Token).ConfigureAwait(false);
            if (!ok)
            {
                GlobalLogger.Log($"/asset/url rejected '{remoteUrl}' — {reason}", "HUDServer", LogLevel.Communication);
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            string? cached = await _urlCache.GetCachedPathAsync(remoteUrl, _cts.Token, freshnessToken);
            if (cached is null || !File.Exists(cached))
            {
                context.Response.StatusCode = 502;
                context.Response.Close();
                return;
            }

            // Label the body with the MIME UrlImageCache actually VALIDATED — its
            // cache filename carries the canonical extension for the accepted image
            // type — never a type re-derived from the remote URL's path. A `…/x.html`
            // URL whose origin served a magic-byte-valid PNG used to come back as
            // text/html on the overlay origin, which is a script-execution sink one
            // <img>/<iframe> away from any page the streamer visits: script running on
            // http://127.0.0.1:18080 can read /layer/<id>, lift the phx-hud-token
            // meta and open a Trusted /hud/<id> socket.
            string? mime = UrlImageCache.MimeForCachedFile(cached);
            if (mime is null)
            {
                GlobalLogger.Log(
                    $"/asset/url refusing to serve '{Path.GetFileName(cached)}' — not a recognised validated-image cache entry",
                    "HUDServer", LogLevel.CriticalError);
                context.Response.StatusCode = 502;
                context.Response.Close();
                return;
            }

            // Stream the cached file rather than buffering the whole thing into a
            // single byte[]. Multi-MB PNGs/GIFs would land on the LOH and stall GC; a
            // 64KB streaming copy stays well under the 85KB threshold. Mirrors the
            // ServeStaticFileAsync success path.
            context.Response.ContentType     = mime;
            // nosniff — the declared type is the validated one, so never let a
            // browser content-sniff its way to a different (scriptable) type on a
            // polyglot payload.
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.StatusCode      = 200;
            await StreamFileToResponseAsync(cached, context.Response, _cts.Token).ConfigureAwait(false);
            context.Response.Close();
        }

        // ──────────────────────────────────────────────────────────────────
        //  STREAMING FILE COPY  (testable)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Streams the file at <paramref name="path"/> to <paramref name="response"/>'s
        /// output stream using a 64KB buffer (well under the LOH threshold). Sets
        /// <c>ContentLength64</c> from the file's length on disk. The caller is
        /// responsible for setting <c>ContentType</c> and <c>StatusCode</c>, and for
        /// closing the response after this returns.
        /// </summary>
        /// <remarks>
        /// Internal+static so unit tests can validate the streaming behavior without
        /// having to spin a full HUDServer instance and an HTTP client around it.
        /// </remarks>
        internal static async Task StreamFileToResponseAsync(
            string path,
            HttpListenerResponse response,
            CancellationToken ct = default)
        {
            var info = new FileInfo(path);
            response.ContentLength64 = info.Length;

            using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            // CopyToAsync uses an internal pooled buffer; pin it at 64KB so we don't
            // drift to a larger default in some future framework version that would
            // push us back over the LOH threshold.
            await fs.CopyToAsync(response.OutputStream, bufferSize: 65536, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Same as the response overload but writes to an arbitrary <see cref="Stream"/>.
        /// Exists purely so unit tests can validate streaming behavior without standing
        /// up an <see cref="HttpListenerResponse"/> (which is internally constructed and
        /// not freely instantiable).
        /// </summary>
        internal static async Task<long> StreamFileToStreamAsync(
            string path,
            Stream destination,
            CancellationToken ct = default)
        {
            var info = new FileInfo(path);
            using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await fs.CopyToAsync(destination, bufferSize: 65536, ct).ConfigureAwait(false);
            return info.Length;
        }

        /// <summary>
        /// Returns the registered <see cref="Layer"/> for <paramref name="id"/>,
        /// lazily reading and registering it from disk when the registry doesn't
        /// have it yet. Closes the race where a freshly-saved (or freshly-created)
        /// <c>.phxlayer</c> is requested over HTTP before LayerWatcher's debounced
        /// reload has registered it: the file exists on disk, so we register it on
        /// demand instead of returning a hard 404. That 404 is unrecoverable for
        /// the requester — its body carries no <c>compositor.js</c>, so the page
        /// can never re-fetch itself on the next LAYER_RELOADED. Both the OBS
        /// browser source and Visualist's live canvas preview (a hidden WebView2
        /// pointed at <c>/layer/&lt;id&gt;?capture=1</c>) navigate here, so the
        /// hard 404 left the preview stuck on Hub's error page. Returns null only
        /// when no matching <c>.phxlayer</c> exists on disk.
        /// </summary>
        private Layer? GetOrLoadLayer(string id)
        {
            var layer = _layerRegistry.GetLayer(id);
            if (layer is not null) return layer;

            string? path = TryResolveLayerFilePath(id);
            if (path is null) return null;
            try
            {
                var loaded = LayerSerializer.Read(path);
                // RegisterLayer fires LayerReloaded → PushLayerReloadedAsync, which
                // is a no-op here (no /hud clients are connected to a layer being
                // loaded for the first time) and self-corrects subsequent fetches
                // onto the registry fast path.
                _layerRegistry.RegisterLayer(id, loaded);
                return loaded;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HUDServer", $"lazy-load of layer '{id}' from '{path}' failed", ex);
                return null;
            }
        }

        // The atomic save (File.Replace) renames the live .phxlayer away for a
        // sub-millisecond window during the swap; a preview fetch landing in that
        // gap saw File.Exists==false and got a hard 404 the bodyless error page
        // can't self-recover from (no compositor.js to re-fetch itself). Retry the
        // lookup a few times with a short delay so a request racing the swap
        // resolves once the new file lands. A layer that genuinely doesn't exist
        // still 404s after the budget (~3×40ms).
        private async Task<Layer?> GetOrLoadLayerResilientAsync(string id)
        {
            var layer = GetOrLoadLayer(id);
            for (int i = 0; i < 3 && layer is null; i++)
            {
                try { await Task.Delay(40, _cts.Token).ConfigureAwait(false); }
                catch { break; }
                layer = GetOrLoadLayer(id);
            }
            return layer;
        }

        private async Task ServeLayerHtmlAsync(HttpListenerContext context, string id)
        {
            if (await GetOrLoadLayerResilientAsync(id) is null)
            {
                GlobalLogger.Log(
                    $"/layer/{id} -> 404: no '{id}.phxlayer' in AppLayers ('{Paths.AppLayers}') or HubLayers ('{Paths.HubLayers}'), and not in the live registry.",
                    "HUDServer", Phoenix.Controls.Shared.Models.LogLevel.System);
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            string indexPath = Path.Combine(_overlayPath, "index.html");
            if (!File.Exists(indexPath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            // Content-Security-Policy hardens the OBS browser source
            // page against third-party script injection / external script-src.
            // The directive permits 'self' (same-origin static files served
            // from /overlay) for everything; script-src additionally allows
            // 'unsafe-eval' because compositor.js may dynamically evaluate
            // widget logic at runtime. If a later audit confirms the
            // compositor no longer needs eval / Function(), the directive can
            // be tightened by dropping 'unsafe-eval'. img-src and media-src
            // allow data: and blob: so embedded base64 thumbnails and
            // generated media (from Image nodes) keep rendering.
            //
            // ── V15 — frame-src, and why it is exactly two hosts ────────────────
            //
            // The Player.Embed widget sink mounts a third-party <iframe> on the
            // DOM-overlay track. `frame-src` falls back to `child-src` and then to
            // `default-src 'self'`, so before this directive existed EVERY such frame was
            // blocked outright — there was no partial behaviour to preserve.
            //
            // The allowlist is enumerated rather than wildcarded because a frame is the
            // one thing on this page that runs code we did not write, inside our origin's
            // window tree. `https:` — or even `https://*.youtube.com` — would let any
            // author-typed URL that survives a graph edit load a stranger's document into
            // the overlay a streamer is broadcasting. The two entries are precisely the
            // two feeds Player.Embed can build a URL for, and the browser-side builder
            // hard-codes the same two origins, so a URL this policy would reject is one
            // the compositor never constructs:
            //
            //   https://www.youtube-nocookie.com  — the song-request player. The
            //     -nocookie host rather than www.youtube.com: it is YouTube's own
            //     no-tracking-cookie variant of the same embed, it serves the identical
            //     player and postMessage API, and it keeps the overlay from writing
            //     third-party ad cookies into the streamer's OBS browser profile.
            //   https://clips.twitch.tv           — the clip shoutout. The clip embed
            //     lives on this host only; twitch.tv/<channel>/clip/<slug> is a WEB page,
            //     not an embed, and the compositor rewrites that form to this host.
            //
            // ★ Nothing else widens with it, deliberately. No img-src entry for
            // i.ytimg.com (so no poster thumbnails — the idle card is CSS), no connect-src
            // entry (the page never fetches either host), no script-src entry (the YouTube
            // IFrame API JS is NOT loaded; the compositor speaks the player's postMessage
            // protocol directly, which is what keeps script-src at 'self').
            //
            // ★ UNPROVEN AT THIS TIP: nobody has yet confirmed that a youtube-nocookie
            // frame loads AND autoplays inside a real OBS Browser Source. If that
            // pre-flight comes back negative this line is the whole of the CSP change to
            // revert — see PlayerEmbedSinkNode for the rest of the blast radius.
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob:; " +
                "media-src 'self' data: blob:; " +
                "connect-src 'self' ws: wss:; " +
                "frame-src https://www.youtube-nocookie.com https://clips.twitch.tv; " +
                "font-src 'self' data:";

            // Cache-bust the two hot static refs. HUDServer already sends
            // no-cache on .js/.css, but OBS's embedded CEF browser can still
            // serve compositor.js from a warm in-memory cache across a
            // LAYER_RELOADED (location.reload) — so a shipped compositor fix
            // silently doesn't reach the overlay until the source is manually
            // recreated. Appending ?v=<token> makes the URL itself change the
            // instant EITHER file changes (token = the two files' mtime⊕length),
            // which defeats every cache layer unconditionally while staying stable
            // within a deploy. AbsolutePath routing strips the query, so /compositor.js
            // still resolves. The index is ~1KB, so read+rewrite per hit is
            // negligible; a quoting change in the HTML just no-ops the replace
            // (falls back to the un-versioned ref, still served no-cache).
            string html = await File.ReadAllTextAsync(indexPath, _cts.Token).ConfigureAwait(false);
            string v    = OverlayAssetVersion();
            html = html
                .Replace("src=\"/compositor.js\"", $"src=\"/compositor.js?v={v}\"")
                .Replace("href=\"/overlay.css\"",  $"href=\"/overlay.css?v={v}\"");
            html = InjectConnectToken(html, id);
            await ServeHtmlStringAsync(context, html);
        }

        /// <summary>
        /// V13 §8.3 — stamps this layer's connect token into the served page so the
        /// <c>/hud/&lt;id&gt;</c> socket the page opens can present it back
        /// (<c>?token=&lt;t&gt;</c>) and be classified Trusted.
        ///
        /// <para>The carrier is a <c>&lt;meta&gt;</c> tag rather than an inline
        /// <c>&lt;script&gt;</c> assignment because the overlay's CSP is
        /// <c>default-src 'self'; script-src 'self' 'unsafe-eval'</c> with no
        /// <c>'unsafe-inline'</c> — an inline script would be blocked by the very header
        /// this method's caller sets three statements earlier. A meta tag is markup, not
        /// script, so it is unaffected. The value is lower-case hex by construction
        /// (<c>LayerRegistry.GetOrCreateConnectToken</c>), so it needs no attribute
        /// escaping and cannot break out of the quotes.</para>
        ///
        /// <para>Injection is idempotent-by-position: <c>&lt;/head&gt;</c> appears once in
        /// the overlay's <c>index.html</c>. If a future index has no <c>&lt;/head&gt;</c>,
        /// the page is served UNSTAMPED rather than mangled — which lands its socket in the
        /// grace path (renders, declines the upward paths, logs once), never dark. That is
        /// the same failure direction as a stale cached page, which is the whole posture of
        /// §8.3.</para>
        /// </summary>
        private string InjectConnectToken(string html, string layerId)
        {
            string token = _layerRegistry.GetOrCreateConnectToken(layerId);
            if (string.IsNullOrEmpty(token)) return html;

            int idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return html;

            return html.Insert(idx,
                $"    <meta name=\"{ConnectTokenMetaName}\" content=\"{token}\">\n");
        }

        /// <summary>
        /// V13 §8.3 — the <c>&lt;meta name&gt;</c> compositor.js reads the connect token from.
        /// The contract FIXES this literal at <c>phx-hud-token</c>; it is not a free choice.
        ///
        /// <para>★ THIS STRING IS A CROSS-FILE CONTRACT with <c>compositor.js</c>'s
        /// <c>CONNECT_TOKEN</c> reader (<c>document.querySelector('meta[name="phx-hud-token"]')</c>),
        /// and a silent disagreement has no error path at all: every socket simply classifies
        /// Untrusted, the overlay keeps rendering, and the only symptom is "my Visual.Complete
        /// payload is always empty and node flashes never fire".</para>
        ///
        /// <para>★ THAT IS NOT HYPOTHETICAL — it is what V13's first attempt shipped. Hub stamped
        /// <c>phx-hud-token</c> while the browser queried <c>phx-layer-token</c>, so BOTH new
        /// capabilities were dead on arrival, and the test that appeared to guard it asserted this
        /// very constant against markup built from this very constant — self-agreement, which
        /// passes for any name. The guard now asserts the LITERAL on both sides
        /// (<c>V13PayloadAndTokenTests.A1_ConnectTokenMetaName_IsTheSameLiteralOnBothSidesOfTheWire</c>
        /// re-reads the name out of compositor.js source text), so a one-sided rename fails.
        /// Never re-write that test to compare a constant with output derived from it.</para>
        /// </summary>
        internal const string ConnectTokenMetaName  = "phx-hud-token";

        /// <summary>V13 §8.3 — query-string key on the <c>/hud/&lt;id&gt;</c> upgrade.</summary>
        internal const string ConnectTokenQueryKey  = "token";

        /// <summary>
        /// V13 §8.3 belt-and-braces — the downward frame that asks ONE untrusted page to
        /// hard-reload itself so it re-fetches <c>/layer/&lt;id&gt;</c> and picks up the token.
        ///
        /// <para>★ CROSS-FILE LITERAL with compositor.js's <c>msg.type === 'HUD_RELOAD'</c> arm.
        /// Same failure shape as <see cref="ConnectTokenMetaName"/>: a one-sided rename has no
        /// error path — Hub would keep sending a frame nothing handles, and the page would degrade
        /// silently forever. <c>V13PayloadAndTokenTests</c> asserts the LITERAL on both sides.</para>
        /// </summary>
        internal const string SelfHealReloadFrameType = "HUD_RELOAD";

        /// <summary>
        /// V13 §8.3 — the layer id <c>/</c> and <c>/overlay</c> resolve to when the request carries
        /// no <c>?layer=</c>.
        ///
        /// <para>★ CROSS-FILE LITERAL with compositor.js's own fallback chain
        /// (<c>pathMatch || params.get('layer') || 'main'</c>). If these two disagreed, a bare
        /// <c>/</c> would be stamped with a token minted for a DIFFERENT layer than the one the
        /// page then binds to, and the socket would classify Untrusted with no clue why.</para>
        /// </summary>
        internal const string DefaultRootLayerId = "main";

        // Cache-bust token for the overlay's static <script>/<link> refs, keyed off
        // the (mtime,length) of BOTH files the token is stamped onto — compositor.js
        // and overlay.css — so it's stable within a deploy (no redundant re-fetch)
        // and flips the instant an update rewrites EITHER of them. Keying it off
        // compositor.js alone meant a CSS-only edit shipped under an unchanged token
        // and CEF happily kept serving the stale overlay.css, which is exactly the
        // failure this token exists to close. The two halves are emitted separately
        // rather than folded together so neither file can mask the other's change.
        private readonly object _assetVerGate = new();
        private string? _assetVerToken;
        private (long jsTicks, long jsLen, long cssTicks, long cssLen) _assetVerStamp;

        private string OverlayAssetVersion()
        {
            try
            {
                var js = new FileInfo(Path.Combine(_overlayPath, "compositor.js"));
                if (!js.Exists) return "0";
                // A missing overlay.css contributes zeros instead of voiding the whole
                // token — compositor.js is the load-bearing ref and must still bust.
                var css = new FileInfo(Path.Combine(_overlayPath, "overlay.css"));
                var stamp = (jsTicks:  js.LastWriteTimeUtc.Ticks,
                             jsLen:    js.Length,
                             cssTicks: css.Exists ? css.LastWriteTimeUtc.Ticks : 0L,
                             cssLen:   css.Exists ? css.Length : 0L);
                lock (_assetVerGate)
                {
                    if (_assetVerToken is not null && _assetVerStamp == stamp) return _assetVerToken;
                    _assetVerStamp = stamp;
                    _assetVerToken = $"{(stamp.jsTicks ^ stamp.jsLen):x}-{(stamp.cssTicks ^ stamp.cssLen):x}";
                    return _assetVerToken;
                }
            }
            catch { return "0"; }
        }

        // Serve an in-memory HTML string with the same no-cache posture as the
        // static .html path. The caller sets any page-specific headers (e.g. CSP)
        // before invoking; this only adds Cache-Control/Content-Type/length.
        private async Task ServeHtmlStringAsync(HttpListenerContext context, string html)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType     = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Pragma"]        = "no-cache";
            context.Response.Headers["Expires"]       = "0";
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length), _cts.Token).ConfigureAwait(false);
            context.Response.Close();
        }

        private async Task ServeLayerJsonAsync(HttpListenerContext context, string id)
        {
            // If this layer was just reloaded, the writer (Visualist's atomic save)
            // may not have fully flushed by the time the browser fetches us. Wait briefly
            // for the underlying .phxlayer file to stop growing before we serve. The wait
            // is a no-op when the file is already stable, so static layers don't pay it.
            if (_recentlyReloaded.TryGetValue(id, out var stamp)
                && DateTime.UtcNow - stamp < TimeSpan.FromSeconds(RECENT_RELOAD_TTL_SECONDS))
            {
                string? layerFile = TryResolveLayerFilePath(id);
                if (layerFile is not null)
                {
                    await WaitForFileStableAsync(layerFile, _cts.Token).ConfigureAwait(false);
                }
                // Drop the stamp once we've waited — subsequent hits get the fast path.
                // Use the value-matching overload so a concurrent PushLayerReloadedAsync that
                // wrote a *newer* stamp across the await above isn't discarded by this remove.
                _recentlyReloaded.TryRemove(new KeyValuePair<string, DateTime>(id, stamp));
            }

            var layer = await GetOrLoadLayerResilientAsync(id);
            if (layer is null)
            {
                GlobalLogger.Log(
                    $"/api/layer/{id} -> 404: no '{id}.phxlayer' on disk (AppLayers/HubLayers) or in the registry.",
                    "HUDServer", Phoenix.Controls.Shared.Models.LogLevel.System);
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            string json = LayerSerializer.Serialize(layer);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            // Emphatic no-cache so a browser hitting /api/layer/<id> after a
            // LAYER_RELOADED notice picks up the new JSON instead of serving a stale
            // intermediate-cached copy. Also relax CORS so an OBS browser source on a
            // different origin can fetch this without a flag (still loopback-only).
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Pragma"]        = "no-cache";
            context.Response.Headers["Expires"]       = "0";
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length), _cts.Token).ConfigureAwait(false);
            context.Response.Close();
        }

        private async Task ServeStaticFileAsync(HttpListenerContext context, string fullPath, string urlPath)
        {
            // Stream rather than buffer the whole file. Large URL-fetched images and
            // overlay assets used to allocate a single byte[] of the file size, blowing past
            // the LOH threshold for anything > 85KB. Now we stream through a 64KB buffer
            // and honor Range requests so video/audio scrubbing works without re-fetching
            // the whole asset on every seek.
            long fileSize  = new FileInfo(fullPath).Length;
            string mime    = GetMimeType(urlPath);
            string? range  = context.Request.Headers["Range"];

            context.Response.ContentType = mime;
            // Always advertise Range support on full responses too, so clients know they
            // can resume on a future request even if they didn't ask for a slice this time.
            context.Response.Headers["Accept-Ranges"] = "bytes";

            // Source-overlay text content (HTML/JS/CSS) is hot-edited during dev — without
            // explicit Cache-Control, browsers (Firefox especially) hold the cached copy
            // through Ctrl+Shift+R. Mirror the /api/layer/<id> no-cache pattern for these
            // mime types only; image/video/audio assets keep default heuristics so OBS
            // doesn't re-download a 50MB MP4 every render.
            string ext = Path.GetExtension(urlPath).ToLowerInvariant();
            if (ext is ".html" or ".htm" or ".js" or ".mjs" or ".css" or ".json")
            {
                context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                context.Response.Headers["Pragma"]        = "no-cache";
                context.Response.Headers["Expires"]       = "0";
            }

            using var fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            if (TryParseRange(range, fileSize, out long start, out long end))
            {
                long length = end - start + 1;
                context.Response.StatusCode      = 206;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{fileSize}";
                context.Response.ContentLength64 = length;

                fs.Seek(start, SeekOrigin.Begin);
                // Cap buffer at 64KB so we stay well under the LOH threshold no matter
                // how big the slice is.
                int bufSize = (int)Math.Min(length, 65536);
                var buffer  = new byte[bufSize];
                long remaining = length;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read   = await fs.ReadAsync(buffer.AsMemory(0, toRead), _cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            else
            {
                context.Response.StatusCode      = 200;
                context.Response.ContentLength64 = fileSize;
                // CopyToAsync uses an internal pooled buffer; supply 64KB explicitly so
                // we don't drift to a larger default in some future framework version.
                await fs.CopyToAsync(context.Response.OutputStream, bufferSize: 65536, _cts.Token).ConfigureAwait(false);
            }
            context.Response.Close();
        }

        // ──────────────────────────────────────────────────────────────────
        //  WEBSOCKET HANDLING
        // ──────────────────────────────────────────────────────────────────

        private async Task ProcessWebSocketRequestAsync(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";

            // Per-layer WebSocket route: /hud/<layerId>
            string? layerId = null;
            if (path.StartsWith("/hud/", StringComparison.OrdinalIgnoreCase))
            {
                layerId = path.Substring("/hud/".Length).Trim('/');
                if (string.IsNullOrEmpty(layerId) || !IsValidLayerId(layerId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }
            }
            else
            {
                // The legacy broadcast `/hud` endpoint with no layer
                // id was a no-op accepting OBS sources that never received triggers (Phase 2
                // dispatch is per-layer). Reject the route outright so misconfigured browser
                // sources get a clear 400 instead of a silently-stuck connection.
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            // V6 — classify the client BEFORE either cap gate, because the kind selects
            // which budget the connection is measured against.
            //
            // The route above parses layerId from Url.AbsolutePath, which STRIPS the query
            // (same idiom as the HTTP handler and the /compositor.js route) — so
            // ?client=editor cannot reach IsValidLayerId and the id parse needs no change.
            // The query itself is still on the request: HttpListener parses it into
            // Request.QueryString for us (precedent: the /assets/ route's QueryString["u"]
            // read), which is why the discriminator can be a pure read with no route work.
            var clientKind = ClassifyClientKind(context.Request.QueryString);
            bool isEditorClient = clientKind == LayerClientKind.Editor;
            int kindCap = isEditorClient ? MAX_EDITOR_CONNECTIONS_PER_LAYER : MAX_CONNECTIONS_PER_LAYER;

            // Per-layer connection cap. Check BEFORE the upgrade
            // handshake so a flood of /hud/<layerId> fuzzes doesn't burn handle
            // counts spinning up doomed sockets. OBS realistically opens 2
            // sources (Preview + Studio); 4 leaves headroom for multi-monitor
            // setups while bounding attacker amplification. Existing in-grace
            // teardowns count toward the cap, which is intentional — if 4
            // tabs are still inside the grace window, the 5th client is the
            // one that won't survive a real concurrency spike anyway.
            //
            // This pre-upgrade count check is now a cheap
            // best-effort fast-path; the authoritative gate is the atomic
            // TryRegisterConnection below. Pre-fix this lone check was
            // TOCTOU — five concurrent handshakes could all observe
            // count < cap, all upgrade, and all register past the limit
            // because GetConnections + RegisterConnection were not held
            // under the same lock. The double-gate pattern keeps the
            // pre-upgrade reject (no upgrade cost for the obvious-flood
            // case) AND ensures the post-upgrade registration is atomic.
            //
            // V6 — measured PER KIND. Counting all sockets here is what let four preview
            // panes pre-reject an OBS Browser Source before it ever reached the atomic gate.
            int currentCount = _layerRegistry.GetLiveConnectionCount(layerId!, clientKind);
            if (currentCount >= kindCap)
            {
                GlobalLogger.Log(
                    $"HUDServer: rejecting /hud/{layerId} — per-layer {clientKind} connection cap ({kindCap}) reached.",
                    "HUDServer", LogLevel.CriticalError);
                // Upgrade-then-close-with-1013 so the client sees a proper
                // WebSocket "Try Again Later" rather than a bare TCP reset.
                try
                {
                    var rejectCtx = await context.AcceptWebSocketAsync(null);
                    var rejectSocket = rejectCtx.WebSocket;
                    try
                    {
                        await rejectSocket.CloseAsync(
                            (WebSocketCloseStatus)1013, // Try Again Later
                            "per-layer connection cap reached",
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { /* best-effort */ }
                    finally { rejectSocket.Dispose(); }
                }
                catch
                {
                    // If the upgrade itself fails we can't help the client further;
                    // the cap already prevented the resource burn.
                }
                return;
            }

            // ─── Cross-site WebSocket-hijack gate ────────────────────────────────
            // The listener binds 127.0.0.1/localhost only, which stops LAN peers but
            // NOT browsers: a WebSocket handshake is exempt from CORS preflight, so
            // any http:// page the streamer happens to visit can open
            // ws://127.0.0.1:18080/hud/<id> from their machine and the upgrade used
            // to be unconditional. That page cannot become Trusted (the connect
            // token lives in the served HTML, which carries no
            // Access-Control-Allow-Origin, so a cross-origin fetch cannot read it) —
            // but the two upward frames that are deliberately NOT privileged were
            // reachable: LIVE_HELLO replaces a layer's ENTIRE live subscription
            // (freezing every live-bound widget on the real overlay until a
            // legitimate socket re-hellos), and VISUAL_COMPLETE resolves a script's
            // wait by waitId alone, first-ack-wins, so a forged ack beats the real
            // browser's and the script takes the Done branch for a widget that never
            // rendered. Both waitIds are handed to every socket on the layer in
            // RUN_TRIGGER, so the attacker is given what it needs to forge.
            //
            // Same-origin pages send Origin = our own scheme+host (compositor.js
            // dials window.location.host, so the real overlay always matches). A
            // request with NO Origin is allowed: non-browser clients (the test
            // harness, a native tool) omit it, and browsers always send it — so
            // absence cannot be a browser attacker while presence-and-mismatch
            // always is.
            if (!IsAllowedWebSocketOrigin(context.Request))
            {
                GlobalLogger.Log(
                    $"Refused a /hud WebSocket upgrade from a foreign origin '{context.Request.Headers["Origin"]}' " +
                    $"(layer '{layerId}'). A page in a browser tried to connect to the overlay socket; " +
                    "the overlay itself is unaffected.",
                    "HUDServer", LogLevel.CriticalError);
                context.Response.StatusCode = 403;
                try { context.Response.Close(); } catch { /* best-effort */ }
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(null);
            var socket = wsContext.WebSocket;

            // If a previous socket for this layer is still inside its grace window,
            // do NOT cancel its pending teardown. Teardowns are keyed per (layer, socket):
            // the old socket's teardown fires independently after the grace window and
            // disposes its PerSocketSender (cancelling it here orphaned that sender in
            // _layerSenders until Stop()). Presence stays continuous — the registration
            // below adds this socket before the old one unregisters, and
            // LayerRegistry.UnregisterConnection only marks the layer inactive once its
            // connection set empties. One-shot alerts queued during the gap still
            // dispatch as soon as the queue pump cycles.

            // Atomic gate. If five sockets all passed the
            // pre-upgrade fast-path within a microsecond of each other (their
            // GetConnections reads all returned the same value), this is the
            // step that authoritatively rejects the over-cap one. The fast-
            // path above already paid the upgrade cost for those sockets, so
            // the rejection cost is the same close-with-1013 + Dispose
            // pattern.
            // After the route-validation gate above, layerId is always non-null here.
            //
            // V6 — this is where the per-socket kind becomes authoritative: the registry
            // records it under the same lock that admits the socket, so every later
            // presence read and every inbound-frame classification agrees with the cap
            // decision made here. The budget handed over is this KIND's budget; the
            // registry counts only same-kind sockets against it.
            if (!_layerRegistry.TryRegisterConnection(layerId!, socket, kindCap, clientKind))
            {
                GlobalLogger.Log(
                    $"HUDServer: rejecting /hud/{layerId} at atomic gate — per-layer {clientKind} connection cap ({kindCap}) raced past the pre-upgrade fast-path.",
                    "HUDServer", LogLevel.CriticalError);
                try
                {
                    await socket.CloseAsync(
                        (WebSocketCloseStatus)1013,
                        "per-layer connection cap reached",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* best-effort */ }
                finally { socket.Dispose(); }
                return;
            }
            // ── V13 §8.3 — CLASSIFY, never refuse ───────────────────────────
            //
            // A socket that presents this layer's current connect token is Trusted; anything
            // else (no ?token=, a token from a previous Hub process, a token for another
            // layer) is Untrusted. Untrusted is NOT a rejection: the socket is already
            // registered above and receives every downward frame — LIVE_SNAPSHOT,
            // LIVE_PATCH, RUN_TRIGGER, LAYER_RELOADED — unchanged. Only the new UPWARD
            // paths are declined (see IsPrivilegedUpwardFrame and the VISUAL_COMPLETE arm).
            //
            // WHY NOT A HARD REJECT: every OBS Browser Source running a page cached from
            // before this change presents no token. Refusing them would black out the
            // streamer's whole overlay mid-stream, with the only clue in a log nobody is
            // reading. An overlay that renders but declines one new privileged path is
            // strictly better than one that disappears. Same posture as
            // IsInboundAllowedFromEditorClient: classify the client, degrade the specific
            // capability, keep the surface alive.
            bool isTrustedClient = _layerRegistry.IsConnectTokenValid(
                layerId!, context.Request.QueryString[ConnectTokenQueryKey]);
            if (isTrustedClient)
            {
                // Recorded BEFORE the receive loop below, so the socket's very first frame
                // already classifies correctly. See the _trustedSockets field comment for
                // why presence (not absence) is the recorded case.
                _trustedSockets[socket] = 1;
            }
            else
            {
                ReportUntrustedLayerClientOnce(layerId!, clientKind);
                // NOTE: the self-heal reload is NOT offered here. It is offered when this socket
                // sends its first LIVE_HELLO — see OfferSelfHealReloadOnce for why the offer has
                // to be earned rather than fired at classification time.
            }

            // Demote layer-presence chatter to
            // Communication tier. An OBS scene cycling through 5 browser-source
            // panels produced 10+ System-tier rows/sec in the syslog every time
            // the user toggled scenes, growing the SystemLog ring buffer with
            // noise. Communication tier still surfaces to the operator but the
            // default SystemLog filter (Info+Warn+Error) won't include it.
            // V6 — the kind is in the line so "Layers: 0" in the status strip while a
            // preview is open is self-explaining in the log rather than looking like a
            // presence bug.
            GlobalLogger.Log(
                $"Layer client connected ({clientKind}): /hud/{layerId}",
                "HUDServer", LogLevel.Communication);

            // Overlay Live Channel — arm the LIVE_HELLO watchdog. A compositor.js
            // old enough to predate the live channel (an OBS Browser Source cache
            // that never re-fetched the script) connects and renders normally but
            // never sends LIVE_HELLO, so the layer subscribes to nothing and every
            // live-key binding silently stays empty — a failure with no symptom
            // other than "the overlay shows old values forever". NoteConnection
            // records that we are now expecting a HELLO; the delayed check reports
            // the miss ONCE per layer, and only while the layer still has a live
            // socket (a source the user closed inside the window stays quiet).
            //
            // The server's own _cts.Token is both the delay token and the expected
            // token: a Hub shutdown inside the 5 s window cancels the watchdog
            // instead of logging a phantom diagnostic on the way out.
            OverlayLiveStore.Instance.NoteConnection(layerId!);
            _ = AsyncErrorBoundary.SafeRunAsync(
                async () =>
                {
                    await Task.Delay(OverlayLiveStore.HelloDeadlineMs, _cts.Token).ConfigureAwait(false);
                    OverlayLiveStore.Instance.ReportHelloDeadline(
                        layerId!, _layerRegistry.GetLiveConnectionCount(layerId!));
                },
                "HUDServer", "LIVE_HELLO deadline",
                _cts.Token);

            var buffer = new byte[16384];
            var inboundBuilder = new StringBuilder();
            // Cap the per-WS reassembly buffer at 1 MiB. Without this a malicious
            // overlay HTML loaded into OBS Browser Source could ship a fragmented WebSocket
            // frame that never sets EndOfMessage, growing inboundBuilder until the process
            // OOMs. Local-only listener limits the threat model but the sandboxed browser
            // page is not trusted (it loads user-provided HTML / external scripts).
            //
            // The cap MUST be tracked in *bytes*, not StringBuilder
            // chars. The previous code compared `inboundBuilder.Length` (UTF-16
            // code units) against `MaxInboundFrameBytes`, letting an emoji-laden
            // frame slip the cap by ratios up to 4 bytes per char. Track the
            // cumulative byte count on each ReceiveAsync result; the byte
            // length of the incoming fragment is `result.Count` (the raw buffer
            // segment we filled).
            const int MaxInboundFrameBytes = 1 * 1024 * 1024;
            int inboundByteCount = 0;
            try
            {
                while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    if (inboundByteCount + result.Count > MaxInboundFrameBytes)
                    {
                        GlobalLogger.Log(
                            $"HUDServer: inbound frame on /hud/{layerId} exceeds {MaxInboundFrameBytes} bytes — closing socket.",
                            "HUDServer", LogLevel.CriticalError);
                        try { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too big", CancellationToken.None); } catch { }
                        break;
                    }

                    inboundByteCount += result.Count;
                    inboundBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    string json = inboundBuilder.ToString();
                    inboundBuilder.Clear();
                    inboundByteCount = 0;
                    // HandleInboundFromBrowser was inside the
                    // bare catch below, which hid any throw as "client disconnected" and
                    // tore down the socket. Wrap it so cancellation still propagates but
                    // other faults surface in GlobalLogger and keep the receive loop alive.
                    try
                    {
                        HandleInboundFromBrowser(layerId, json, socket);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("HUDServer", $"inbound handler threw on /hud/{layerId ?? "(legacy)"}", ex);
                    }
                }
            }
            catch (OperationCanceledException) { /* server shutting down */ }
            catch { /* client disconnected */ }
            finally
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None); } catch { }
                }
                socket.Dispose();

                // DEFER the per-socket SemaphoreSlim cleanup until after the
                // registry no longer references this socket. Previously the receive-loop
                // finally TryRemove'd the lock immediately while the socket was still in
                // _layerRegistry's connection set; broadcast paths racing the teardown
                // would `GetOrAdd` a fresh SemaphoreSlim against a dead socket, leaking
                // it forever (one per disconnect-vs-broadcast race). Moving the cleanup
                // into the grace-teardown callback ensures UnregisterConnection runs
                // first, so by the time we dispose, no broadcast can re-add a lock.
                //
                // DON'T immediately unregister the connection. Defer the
                // unregister until the grace window elapses; if a new client connects
                // to the same layer in the meantime it registers its own presence
                // first, so this teardown's UnregisterConnection can't empty the
                // layer's connection set and queued one-shot alerts aren't dropped on
                // a one-frame OBS visibility flicker. The teardown is keyed per
                // (layer, socket) and always fires — it must, because it's the only
                // path that disposes THIS socket's PerSocketSender.
                var capturedLayerId = layerId!;
                var capturedSocket  = socket;
                ScheduleGraceTeardown(
                    (capturedLayerId, capturedSocket),
                    _pendingTeardowns,
                    LAYER_DISCONNECT_GRACE_SECONDS * 1000,
                    () =>
                    {
                        _layerRegistry.UnregisterConnection(capturedLayerId, capturedSocket);
                        if (_layerSenders.TryRemove(capturedSocket, out var sender))
                        {
                            try { sender.Dispose(); } catch { }
                        }
                        // V13 §8.3 — the trust record and the self-heal-offer latch share
                        // _layerSenders' lifetime exactly; dropping them here is what keeps the
                        // dicts from pinning dead WebSocket objects for the process lifetime (one
                        // per reconnect).
                        _trustedSockets.TryRemove(capturedSocket, out _);
                        _selfHealOfferedSockets.TryRemove(capturedSocket, out _);
                        // Overlay Live Channel — drop this layer's subscription
                        // once its LAST socket is gone, so the pump stops building
                        // patch frames for a layer nobody is listening to. Gated on
                        // the post-unregister live count rather than fired
                        // unconditionally, because subscriptions are stored per
                        // LAYER, not per socket: a second OBS browser source on the
                        // same layer (or a reconnect that registered inside this
                        // grace window) is served by the SAME subscription this
                        // socket was, and every socket on a layer announces the
                        // identical layer-wide key set (contract S6 — the client
                        // never narrows its LIVE_HELLO by widget). So the zero-count
                        // gate is exactly the right condition: while any connection
                        // survives, the one shared subscription must survive with
                        // it, otherwise that browser is bound to nothing until the
                        // next page reload.
                        //
                        // V6 — the ALL-KINDS count, deliberately. An editor preview pane is
                        // a real live-channel consumer; gating on production presence here
                        // would drop the subscription the moment OBS closed and leave every
                        // open preview bound to nothing, which is the authoring surface that
                        // needs the live data most.
                        if (_layerRegistry.GetLiveConnectionCount(capturedLayerId) == 0)
                        {
                            OverlayLiveStore.Instance.ClearLayer(capturedLayerId);
                        }
                        // Communication tier for the symmetric
                        // disconnect line — see connect-side comment above.
                        GlobalLogger.Log(
                            $"Layer client disconnected (after {LAYER_DISCONNECT_GRACE_SECONDS}s grace): /hud/{capturedLayerId}",
                            "HUDServer", LogLevel.Communication);
                    });
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  DISCONNECT GRACE HELPERS  (testable)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Schedules <paramref name="onTeardown"/> to fire after <paramref name="graceMs"/>
        /// unless a subsequent <see cref="CancelPendingTeardown"/> for the same key races
        /// it. Replaces any existing pending teardown for the same key (newer wins).
        /// </summary>
        /// <remarks>
        /// Internal+static so unit tests can validate cancel-before-fire and fire-after-delay
        /// without spinning a HUDServer / WebSocket pair. Generic over the key type: the
        /// production dict keys by (LayerId, Socket) so reconnects don't cancel the old
        /// socket's teardown, while existing tests exercise the same logic with plain
        /// string keys.
        /// </remarks>
        internal static CancellationTokenSource ScheduleGraceTeardown<TKey>(
            TKey key,
            ConcurrentDictionary<TKey, CancellationTokenSource> pending,
            int graceMs,
            Action onTeardown)
            where TKey : notnull
        {
            // Replace any previous pending teardown for this key — only the newest disconnect
            // gets to fire (earlier ones are stale once another close has happened).
            if (pending.TryRemove(key, out var existing))
            {
                try { existing.Cancel(); } catch { }
                try { existing.Dispose(); } catch { }
            }

            var cts = new CancellationTokenSource();
            pending[key] = cts;

            // Wrap the grace-window teardown so an unexpected fault inside the
            // delay+post-handler chain surfaces in GlobalLogger instead of
            // disappearing as an unobserved task exception.
            _ = AsyncErrorBoundary.SafeRunAsync(async () =>
            {
                try
                {
                    await Task.Delay(graceMs, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // cancelled by CancelPendingTeardown, a same-key reschedule, or Stop()
                }

                // Only fire if we're still the registered teardown for this key. A reconnect
                // followed by another disconnect would have replaced our entry, in which case
                // a fresh timer owns the teardown and we should bow out silently.
                if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                {
                    pending.TryRemove(key, out _);
                    // onTeardown was in a bare catch that
                    // silently dropped its throws. Log non-cancellation faults so a
                    // failing UnregisterConnection/sender.Dispose isn't invisible.
                    try { onTeardown(); }
                    catch (Exception ex) { GlobalLogger.Error("HUDServer", "grace teardown threw", ex); }
                    try { cts.Dispose(); } catch { }
                }
            }, "HUDServer", $"grace-window teardown {key}");

            return cts;
        }

        /// <summary>
        /// Cancels and removes any pending teardown for <paramref name="key"/>. Returns
        /// true when a teardown was cancelled, false when no teardown was pending.
        /// No production caller remains — the connect path deliberately lets the old
        /// socket's teardown fire (it owns that socket's PerSocketSender disposal) —
        /// but the helper stays as the test seam for the cancel-before-fire contract.
        /// </summary>
        internal static bool CancelPendingTeardown<TKey>(
            TKey key,
            ConcurrentDictionary<TKey, CancellationTokenSource> pending)
            where TKey : notnull
        {
            if (pending.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
                return true;
            }
            return false;
        }

        // ──────────────────────────────────────────────────────────────────
        //  FILE STABILITY FENCE  (testable)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Polls <paramref name="path"/>'s length until two consecutive samples
        /// <paramref name="delayMs"/> apart match, or <paramref name="maxAttempts"/>
        /// is exhausted. Returns true when the file is stable, false if it kept
        /// growing or was missing past the cap. Uses the length-twice approach so
        /// it works for non-exclusive writers (Visualist's atomic save flushes
        /// through a temp file + rename, leaving no exclusive lock to acquire).
        ///
        /// Thin delegate over <see cref="DebouncedFileWatcher.WaitForSizeStableAsync"/>;
        /// the wrapper is preserved because BugFixSweep2_HubHUDServer_Tests calls
        /// it as a static helper.
        /// </summary>
        internal static Task<bool> WaitForFileStableAsync(
            string path,
            CancellationToken ct = default,
            int maxAttempts = 10,
            int delayMs = 50)
            => DebouncedFileWatcher.WaitForSizeStableAsync(path, ct, maxAttempts, delayMs);

        /// <summary>
        /// Resolves the on-disk path for `data/layers/&lt;id&gt;.phxlayer`. Mirrors
        /// LayerWatcher's walk-up search so we point at the live source folder both
        /// during dev (running from bin/Debug) and from a deployed install.
        /// Returns null when no candidate exists.
        /// </summary>
        private static string? TryResolveLayerFilePath(string id)
        {
            string fileName = id + ".phxlayer";

            // First: BaseDirectory-relative (production deploy path).
            string baseGuess = Path.Combine(Paths.AppLayers, fileName);
            if (File.Exists(baseGuess)) return baseGuess;

            // Then: solution-anchored Hub data/layers (dev-tree run).
            string srcGuess = Path.Combine(Paths.HubLayers, fileName);
            return File.Exists(srcGuess) ? srcGuess : null;
        }

        // ──────────────────────────────────────────────────────────────────
        //  RANGE HEADER PARSING  (testable)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses an HTTP Range header of the form `bytes=START-END`, `bytes=START-`,
        /// or `bytes=-SUFFIX`. Returns true on a satisfiable range and writes the
        /// resolved [start, end] pair (inclusive). Returns false on a missing,
        /// malformed, or unsatisfiable header — caller should fall through to a 200
        /// full-file response.
        /// </summary>
        internal static bool TryParseRange(string? rangeHeader, long fileSize, out long start, out long end)
        {
            start = 0;
            end   = 0;

            if (string.IsNullOrEmpty(rangeHeader)) return false;
            if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileSize <= 0) return false;

            string spec = rangeHeader.Substring("bytes=".Length);
            // Multipart ranges (`bytes=0-99,200-299`) aren't supported here — the file
            // streamer below dispatches one slice. Take the first range and ignore the
            // rest, which is RFC-compliant: a server MAY treat multi-range as single.
            int comma = spec.IndexOf(',');
            if (comma >= 0) spec = spec.Substring(0, comma);

            var parts = spec.Split('-');
            if (parts.Length != 2) return false;

            long s, e;
            if (string.IsNullOrEmpty(parts[0]))
            {
                // Suffix range: `bytes=-N` means the last N bytes.
                if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return false;
                s = Math.Max(0, fileSize - suffix);
                e = fileSize - 1;
            }
            else
            {
                if (!long.TryParse(parts[0], out s)) return false;
                if (string.IsNullOrEmpty(parts[1]))
                {
                    e = fileSize - 1;
                }
                else if (!long.TryParse(parts[1], out e))
                {
                    return false;
                }
            }

            // Clamp end to the file's last byte.
            if (e >= fileSize) e = fileSize - 1;

            // Reject inverted/empty/out-of-range slices.
            if (s < 0 || s >= fileSize) return false;
            if (e < s) return false;

            start = s;
            end   = e;
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        //  PER-LAYER DISPATCH
        //
        //  The untargeted pair that used to open this region —
        //  BroadcastAsync(object) and BroadcastRawAsync(string) — was deleted
        //  in V4 part C. Their only callers were the five inert per-widget
        //  mutation commands (chat.overlay.push / chat.overlay.clear /
        //  visual.set_text / set_visible / set_property), which went with the
        //  nodes that emitted them. Everything that survives is ADDRESSED:
        //  SendToLayerAsync for one layer, BroadcastToAllLayersAsync for the
        //  documented every-layer primitive. Do not reintroduce a raw-string
        //  broadcast — an untyped frame with no layer scope is how the
        //  chat-overlay path stayed silently broken for a whole release cycle.
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a JSON payload to all WebSocket clients connected to /hud/&lt;layerId&gt;.
        /// Used by WidgetTriggerQueue to deliver RUN_TRIGGER messages.
        /// </summary>
        public Task SendToLayerAsync(string layerId, object payload, CancellationToken ct = default)
        {
            // SerializeToUtf8Bytes — single pass into the
            // wire byte[] for RUN_TRIGGER + LAYER_RELOADED + ad-hoc frames.
            // The diagnostic preview path used to need the string form too,
            // so we keep a lazily-built preview only on the cold drop branch.
            byte[] buff = JsonSerializer.SerializeToUtf8Bytes(payload);
            var connections = _layerRegistry.GetConnections(layerId);

            // Diagnostic — when a layer is registered (a `.phxlayer` file is on
            // disk and LayerWatcher loaded it) but no browser is connected, the
            // foreach below is a silent no-op and the script never realises the
            // frame was dropped. The payload preview is included so the user
            // can tell RUN_TRIGGER drops apart from LAYER_RELOADED / CAPTION_UPDATE
            // drops.
            //
            // Demoted from System to Communication
            // tier and rate-limited. A script that fires a trigger every chat
            // message into an inactive layer was log-spamming syslog at chat
            // rate; we now emit the first miss + every 100th miss per (layerId)
            // so the operator still sees the problem without saturating the UI.
            if (connections.Count == 0 && _layerRegistry.GetLayer(layerId) != null)
            {
                long missCount = _layerDropCounts.AddOrUpdate(layerId, 1, (_, prev) => prev + 1);
                if (missCount == 1 || missCount % 100 == 0)
                {
                    int previewLen = Math.Min(buff.Length, 240);
                    string preview = Encoding.UTF8.GetString(buff, 0, previewLen);
                    GlobalLogger.Log(
                        $"HUDServer: layer '{layerId}' is registered but has 0 live WebSockets — frame dropped (total={missCount}). payload={preview}",
                        "HUDServer", LogLevel.Communication);
                }
            }

            return FanOutAsync(buff, connections, ct);
        }

        /// <summary>
        /// Sends a JSON payload to ONE socket, identified by the opaque token the receive
        /// loop handed out (which is the <see cref="WebSocket"/> itself).
        ///
        /// <para>The addressed counterpart to <see cref="SendToLayerAsync"/>, wired into
        /// <c>OverlayLiveStore.SocketDispatcher</c> so a <c>LIVE_HELLO</c> is answered by
        /// a <c>LIVE_SNAPSHOT</c> to the ASKER instead of a full-repaint frame fanned at
        /// every browser on the layer. Same pump, same bounded channel, same teardown
        /// ownership as every other frame — see <see cref="OfferSelfHealReloadOnce"/>,
        /// which uses the identical addressing.</para>
        ///
        /// <para>A token that is not a live open socket is a no-op rather than a fault:
        /// the socket can close between a HELLO arriving and its snapshot being built,
        /// and a closed browser is not an error condition.</para>
        /// </summary>
        public Task SendToSocketAsync(object socketToken, object payload, CancellationToken ct = default)
        {
            if (socketToken is not WebSocket ws) return Task.CompletedTask;
            if (ws.State != WebSocketState.Open) return Task.CompletedTask;

            byte[] buff = JsonSerializer.SerializeToUtf8Bytes(payload);

            // GetOrAdd, not TryGetValue: a socket that has been sent nothing yet has no
            // pump. The teardown path removes whatever is created here.
            var sender = _layerSenders.GetOrAdd(ws, static (s, token) => new PerSocketSender(s, token), _cts.Token);
            sender.TryEnqueue(buff);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Pushes a LAYER_RELOADED notification to every browser connected to /hud/&lt;layerId&gt;.
        /// Compositor.js handles it by calling location.reload() — the page reboots and re-fetches
        /// /api/layer/&lt;id&gt; on next load. HubBootstrapper wires LayerRegistry.LayerReloaded to this.
        /// </summary>
        public Task PushLayerReloadedAsync(string layerId, CancellationToken ct = default)
        {
            // Mark the id as recently-reloaded so the next /api/layer/<id> fetch
            // waits for the writer to settle before reading. We stamp BEFORE sending so
            // the browser's reactive reload can't beat us to the API call.
            _recentlyReloaded[layerId] = DateTime.UtcNow;
            return SendToLayerAsync(layerId, new { type = "LAYER_RELOADED", layerId }, ct);
        }

        /// <summary>
        /// Broadcasts a JSON payload to every active per-layer WebSocket.
        /// <para>
        /// Retained primitive with no production caller: it was
        /// LiveCaptionService's CAPTION_UPDATE path until V3 moved captions onto
        /// the Overlay Live Channel, which routes per subscribed key instead of
        /// fanning out. Kept because "one typed frame to every connected
        /// overlay" is a real, occasionally-needed shape and reimplementing it
        /// ad hoc is how untargeted raw broadcasts creep back in.
        /// <c>LayerRouteTests</c> exercises it so the traversal cannot rot
        /// while it waits for a caller. Prefer <see cref="SendToLayerAsync"/>
        /// whenever the frame belongs to one layer.
        /// </para>
        /// </summary>
        public Task BroadcastToAllLayersAsync(object payload, CancellationToken ct = default)
        {
            // SerializeToUtf8Bytes — same single-pass
            // allocation win as the SendToLayerAsync path.
            byte[] buff = JsonSerializer.SerializeToUtf8Bytes(payload);
            return FanOutAsync(buff, EnumerateAllLayerSockets(), ct);
        }

        /// <summary>
        /// Yields every WebSocket on every layer that holds one, once. Sole consumer is
        /// <see cref="BroadcastToAllLayersAsync"/>; kept as its own method so
        /// the registry traversal stays one readable unit. Snapshots the
        /// layer ids first because
        /// <see cref="LayerRegistry.GetConnections"/> can change concurrently.
        ///
        /// <para>V6 — <see cref="LayerRegistry.GetLayerIdsWithAnyConnection"/>, NOT
        /// <c>GetActiveLayerIds</c>. This is a FAN-OUT, so "which layers are live on
        /// stream?" is the wrong question: a layer whose only client is a Visualist preview
        /// must still receive every-layer frames, or the preview stops being a preview of
        /// what OBS would show.</para>
        /// </summary>
        private IEnumerable<WebSocket> EnumerateAllLayerSockets()
        {
            foreach (var layerId in _layerRegistry.GetLayerIdsWithAnyConnection())
            {
                foreach (var ws in _layerRegistry.GetConnections(layerId))
                    yield return ws;
            }
        }

        /// <summary>
        /// Shared per-socket send dispatch used by
        /// <see cref="SendToLayerAsync"/> and
        /// <see cref="BroadcastToAllLayersAsync"/>.
        ///
        /// <para>
        /// Backpressure design: enqueues the frame into each socket's
        /// <see cref="PerSocketSender"/> bounded channel (cap=256, DropOldest)
        /// rather than awaiting <c>ws.SendAsync</c> in-line. Each socket has
        /// exactly one pump task draining its channel, so:
        ///   • <see cref="WebSocket.SendAsync"/> still runs serialized per socket
        ///     (only the pump reads the channel) — the send-serialization contract is preserved.
        ///   • A slow client can no longer back-pressure the producer; the 257th
        ///     frame evicts the oldest (DropOldest) and TryWrite returns
        ///     immediately. The drop is counted and surfaced through
        ///     <see cref="GlobalLogger"/> at Communication tier.
        ///   • Across-socket fan-out is now naturally parallel — slow OBS browser
        ///     A no longer makes browser B miss its CAPTION_UPDATE.
        /// </para>
        ///
        /// <para>
        /// Observable semantics for callers: the returned <see cref="Task"/>
        /// resolves on enqueue completion, NOT on the underlying socket send.
        /// No caller in the suite depends on send-complete timing — trigger /
        /// caption / overlay broadcasts are fire-and-forget at the wire layer
        /// and any acks travel back via dedicated reply types (VISUAL_COMPLETE,
        /// TRANSLATE_RESPONSE, etc.).
        /// </para>
        /// </summary>
        private Task FanOutAsync(
            byte[] buff,
            IEnumerable<WebSocket> sockets,
            CancellationToken ct)
        {
            // ct here is the caller-supplied token. We still honor it as a
            // best-effort "stop enqueuing more frames" gate; the per-socket
            // pump uses its own server-linked CT for actually canceling sends.
            foreach (var ws in sockets)
            {
                if (ct.IsCancellationRequested) break;
                if (ws.State != WebSocketState.Open) continue;
                var sender = _layerSenders.GetOrAdd(ws, static (s, ct) => new PerSocketSender(s, ct), _cts.Token);
                sender.TryEnqueue(buff);
            }
            return Task.CompletedTask;
        }

        // ──────────────────────────────────────────────────────────────────
        //  INBOUND MESSAGE PARSING (browser → Hub)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Routes an inbound JSON message from a connected browser. Per-layer messages live on
        /// /hud/&lt;layerId&gt; sockets and have their layerId injected from the URL (so the browser
        /// doesn't need to repeat itself in the payload).
        /// </summary>
        private void HandleInboundFromBrowser(string? layerId, string json, WebSocket socket)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
                string type = typeProp.GetString() ?? "";

                // V6 — one gate, before the switch, for the frames a Visualist design-time
                // socket must not be allowed to act on. The arm-by-arm reasoning lives on
                // IsInboundAllowedFromEditorClient; the short version is that only the
                // frames which mutate shared Hub state on the layer's behalf
                // (VISUAL_COMPLETE, FPS, and since V15 MEDIA_ENDED) are refused, and the
                // benign / preview-correctness ones (LIVE_HELLO, TRANSLATE_REQUEST, the two
                // diagnostics) still pass.
                //
                // The registry is the authority on the kind — not a per-receive-loop bool —
                // because the socket reference is already in hand here and the registry
                // recorded the kind under the same lock that admitted the socket. That also
                // means an already-unregistered socket classifies as production, which is
                // the pre-V6 behaviour and the safe direction.
                bool isEditorClient = _layerRegistry.IsEditorSocket(socket);
                if (isEditorClient && !IsInboundAllowedFromEditorClient(type))
                {
                    ReportRefusedEditorInbound(layerId, type);
                    return;
                }

                // V13 §8.3 — the SECOND, orthogonal gate: is this socket running a page
                // THIS Hub served? The editor/production gate above answers "what kind of
                // surface is this?"; this one answers "did it prove provenance?". They are
                // independent — a Visualist preview is an editor client AND trusted, a
                // stale-cached OBS source is production AND untrusted.
                //
                // Untrusted sockets are declined the privileged UPWARD frames outright.
                // VISUAL_COMPLETE is deliberately NOT in that set: it is degraded rather
                // than dropped, inside its own arm, because dropping a completion would
                // hang a script that is waiting on it. Everything DOWNWARD is unaffected.
                bool isTrustedClient = _trustedSockets.ContainsKey(socket);

                // V13 §8.3 belt-and-braces — an untrusted page that speaks the CURRENT protocol
                // gets offered the one self-heal reload. LIVE_HELLO is the proof: only a
                // compositor.js new enough to have the HUD_RELOAD handler sends it, so the offer
                // reaches exactly the pages that can act on it and nothing else. See
                // OfferSelfHealReloadOnce.
                if (!isTrustedClient && type == "LIVE_HELLO")
                {
                    OfferSelfHealReloadOnce(socket, layerId);
                }

                if (!isTrustedClient && IsPrivilegedUpwardFrame(type))
                {
                    ReportUntrustedLayerClientOnce(layerId ?? "", isEditorClient
                        ? LayerClientKind.Editor : LayerClientKind.Production);
                    return;
                }

                switch (type)
                {
                    case "VISUAL_COMPLETE":
                    {
                        if (string.IsNullOrEmpty(layerId)) return;
                        string widgetId    = ReadString(doc.RootElement, "widgetId");
                        string triggerName = ReadString(doc.RootElement, "triggerName");
                        string waitId      = ReadString(doc.RootElement, "waitId");
                        // V13 §8.1 — the optional completion payload. ReadOptionalString
                        // (not ReadString) so an OMITTED property stays null and is
                        // distinguishable from an explicit empty string: `payload` is
                        // omitted exactly when the Visual.Complete Payload pin is unwired,
                        // which is the compatibility case that must behave as it did before
                        // V13 existed.
                        //
                        // V13 §8.3 — an Untrusted socket's completion still RESOLVES (the
                        // waiting script must not hang) but carries no data. This is the one
                        // degrade-rather-than-drop path; see the gate above.
                        string? payload = isTrustedClient
                            ? ReadOptionalString(doc.RootElement, "payload")
                            : null;
                        LayerRuntime.Instance.NotifyTriggerComplete(
                            layerId, widgetId, triggerName, waitId, payload);
                        break;
                    }
                    case "DEBUG_WIDGET_NODE":
                    {
                        // V13 §8.2 — ONE batched trace frame per trigger ACTIVATION, listing
                        // the node ids the widget graph visited. Design-time only.
                        if (string.IsNullOrWhiteSpace(layerId)) break;

                        // Suppressed on the RECEIVE side, not left to the browser to police
                        // itself: an OBS Browser Source that sends this (old cached script,
                        // hand-crafted page) must not be able to drive the author's canvas
                        // flash, and `renderWidgetTrigger` re-runs per animation frame, so a
                        // production sender would be a frame-rate firehose onto the bus.
                        if (!isEditorClient)
                        {
                            ReportRefusedProductionInbound(layerId, type);
                            break;
                        }

                        string widgetId    = ReadString(doc.RootElement, "widgetId");
                        string triggerName = ReadString(doc.RootElement, "triggerName");
                        var    nodeIds     = ReadStringArray(doc.RootElement, "nodeIds");
                        int    seq         = ReadInt(doc.RootElement, "seq");
                        ForwardWidgetNodeTrace(layerId, widgetId, triggerName, nodeIds, seq);
                        break;
                    }
                    case "MEDIA_ENDED":
                    {
                        // V15 — THE ONE UPWARD AMENDMENT. A Player.Embed widget playing the
                        // song-request queue reports that its media finished, so the queue can
                        // advance. This is deliberately NOT general widget→script eventing:
                        // the frame is consumed by exactly one owning Hub service and reaches
                        // no script, no bus and no other tool.
                        //
                        // Trust: nothing to add here. IsPrivilegedUpwardFrame already lists
                        // MEDIA_ENDED, so the gate above returned for an untrusted socket
                        // before this switch ran, and the Origin check ran at upgrade time.
                        //
                        // Editor: also already handled — IsInboundAllowedFromEditorClient
                        // refuses it, so a Visualist preview pane or a thumbnail-capture host
                        // cannot advance the live queue. Both gates are the reason this arm has
                        // no checks of its own beyond the layer guard.
                        //
                        // Whitespace-strict on layerId like LIVE_HELLO, not IsNullOrEmpty:
                        // the value is only used for attribution in the log line below, and a
                        // blank-but-present id would produce an unreadable one.
                        if (string.IsNullOrWhiteSpace(layerId)) break;

                        string widgetId = ReadString(doc.RootElement, "widgetId");
                        string videoId  = ReadString(doc.RootElement, "videoId");
                        // seq is the songrequest.play_token the widget was playing, NOT a
                        // browser-minted counter. That choice is the whole dedupe: every OBS
                        // source on the layer reads the same token off the same live channel,
                        // so N sources report the SAME (videoId, seq) pair and the service can
                        // collapse them. A per-page counter would be N different values and
                        // would double-advance. Long, because play_token is a long Hub-side.
                        long seq = ReadLong(doc.RootElement, "seq");

                        _ = AsyncErrorBoundary.SafeRunAsync(
                            () => HandleMediaEndedAsync(layerId!, widgetId, videoId, seq),
                            "HUDServer", "MEDIA_ENDED",
                            _cts.Token);
                        break;
                    }
                    case "TRIGGER_RECEIVED":
                    {
                        // Diagnostic — compositor.js acks every RUN_TRIGGER on receipt
                        // BEFORE attempting to render. This closes the question of "did
                        // the browser even see it?" separately from "did it render?".
                        if (string.IsNullOrEmpty(layerId)) return;
                        break;
                    }
                    case "TRIGGER_DIAGNOSTIC":
                    {
                        // Diagnostic — compositor.js emits this when handleRunTrigger
                        // takes any silent early-return path (unknown_widget,
                        // unknown_trigger, not_visible) or catches a render_error.
                        // Surfacing at System tier means the user sees the failure
                        // without needing OBS DevTools attached.
                        if (string.IsNullOrEmpty(layerId)) return;
                        string widgetId    = ReadString(doc.RootElement, "widgetId");
                        string triggerName = ReadString(doc.RootElement, "triggerName");
                        string reason      = ReadString(doc.RootElement, "reason");
                        string detail      = ReadString(doc.RootElement, "detail");
                        // V6 — attribute the client. A widget-filtered preview pane emits
                        // `not_visible` for every widget it isn't showing, which at System
                        // tier is indistinguishable from a real OBS source failing to
                        // render. Naming the kind makes preview noise triageable without
                        // suppressing a diagnostic the author actually wants.
                        GlobalLogger.Log(
                            $"HUDServer: browser TRIGGER_DIAGNOSTIC client={(isEditorClient ? "editor" : "production")} layer='{layerId}' widget='{widgetId}' trigger='{triggerName}' reason='{reason}' detail='{detail}'",
                            "HUDServer", LogLevel.System);
                        break;
                    }
                    case "LIVE_HELLO":
                    {
                        // Overlay Live Channel handshake — compositor.js sends this
                        // first thing in socket.onopen with the flat key/prefix set
                        // every widget graph on the layer reads. The store replaces
                        // the layer's whole subscription and answers with a
                        // LIVE_SNAPSHOT, so a reconnect (or a graph edit that
                        // changed the key set) needs no other bookkeeping than
                        // re-sending this frame.
                        //
                        // Stricter than the sibling arms' IsNullOrEmpty on purpose:
                        // a whitespace-only layerId would occupy a subscription slot
                        // no /hud/<id> route can ever match again, so it would sit
                        // in the store's dirty-tracking dict until Hub restarts.
                        if (string.IsNullOrWhiteSpace(layerId)) break;
                        int proto = ReadInt(doc.RootElement, "proto");
                        var keys  = ReadStringArray(doc.RootElement, "keys");
                        // Fire-and-forget: HandleHelloAsync builds and dispatches the
                        // snapshot frame through the per-socket pump, and the receive
                        // loop must not block behind a slow browser. proto is passed
                        // through verbatim — the store owns the version policy and
                        // its one-shot mismatch log; the transport doesn't second-
                        // guess it. _cts.Token as both operation and expected token
                        // so a HELLO racing shutdown cancels quietly.
                        // `socket` is passed as the reply token so the answering
                        // LIVE_SNAPSHOT — a full-repaint frame — goes to the browser
                        // that asked, not to every browser on the layer.
                        _ = AsyncErrorBoundary.SafeRunAsync(
                            () => OverlayLiveStore.Instance.HandleHelloAsync(layerId, proto, keys, socket, _cts.Token),
                            "HUDServer", "LIVE_HELLO",
                            _cts.Token);
                        break;
                    }
                    case "TRANSLATE_REQUEST":
                    {
                        string reqId  = ReadString(doc.RootElement, "reqId");
                        string text   = ReadString(doc.RootElement, "text");
                        string target = ReadString(doc.RootElement, "targetLang");
                        // Basic input validation. Empty reqId or text means the browser
                        // sent garbage; reject before scheduling translation work.
                        if (string.IsNullOrEmpty(reqId) || string.IsNullOrEmpty(text)) break;
                        // Fire-and-forget the translation; the reply travels back over the same socket.
                        _ = AsyncErrorBoundary.SafeRunAsync(
                            () => HandleTranslateRequestAsync(socket, reqId, text, target),
                            "HUDServer", "TRANSLATE_REQUEST");
                        break;
                    }
                    case "FPS":
                    {
                        // Phase 9 (a) — compositor.js sends one of these per second
                        // with the count of trigger renders that ran in the prior
                        // window. Stored on LayerRegistry; surfaced by the Hub
                        // status bar's tooltip.
                        if (string.IsNullOrEmpty(layerId)) break;
                        int fps = 0;
                        if (doc.RootElement.TryGetProperty("fps", out var fpsProp))
                        {
                            // GetInt32() throws on a JSON number
                            // out of Int32 range; TryGetInt32 fails closed to 0 instead.
                            if (fpsProp.ValueKind == JsonValueKind.Number && fpsProp.TryGetInt32(out fps)) { }
                            else if (fpsProp.ValueKind == JsonValueKind.String && int.TryParse(fpsProp.GetString(), out var parsed)) fps = parsed;
                            else fps = 0;
                        }
                        _layerRegistry.RecordFps(layerId, fps);
                        break;
                    }
                }
            }
            catch (JsonException ex)
            {
                GlobalLogger.Error("HUDServer", $"malformed inbound from /hud/{layerId ?? "(legacy)"}", ex);
            }
        }

        // V6 — refused-editor-inbound counter, keyed by "<layerId>|<messageType>".
        //
        // Rate-limited for the same reason SendToLayerAsync's drop diagnostic is: a preview
        // pane emits FPS at 1 Hz per socket and can emit VISUAL_COMPLETE once per rendered
        // trigger, so an unthrottled line per refusal would saturate the SystemLog ring
        // buffer during a normal authoring session. Key space is bounded by
        // (registered layers × the two refused types).
        private readonly ConcurrentDictionary<string, long> _editorRefusalCounts = new();

        /// <summary>
        /// V6 — records that a design-time socket's side-effecting frame was dropped.
        ///
        /// <para>Communication tier, first + every 100th: the refusal is CORRECT behaviour,
        /// not a fault, so it must not light the operator's error surfaces. It is logged at
        /// all because "my Test Run does nothing when only the preview is open" is the
        /// question this sprint's presence change provokes, and this line is the answer
        /// (the Test Target affordance in the widget editor is the fix).</para>
        /// </summary>
        private void ReportRefusedEditorInbound(string? layerId, string messageType)
        {
            string key = $"{layerId ?? "(none)"}|{messageType}";
            long n = _editorRefusalCounts.AddOrUpdate(key, 1, (_, prev) => prev + 1);
            if (n != 1 && n % 100 != 0) return;
            GlobalLogger.Log(
                $"HUDServer: refused {messageType} from an editor (design-time) client on /hud/{layerId ?? "(none)"} — " +
                $"a Visualist preview must not answer for a production overlay (total={n}).",
                "HUDServer", LogLevel.Communication);
        }

        // V13 §8.2 — mirror counter for the OPPOSITE direction: a design-time-only frame
        // arriving from a PRODUCTION socket. Rate-limited on the same first + every-100th
        // schedule and for the same reason as _editorRefusalCounts: the frames this guards
        // are emitted per trigger activation, and `renderWidgetTrigger` re-runs per
        // animation frame, so a misbehaving sender is a frame-rate source.
        private readonly ConcurrentDictionary<string, long> _productionRefusalCounts = new();

        /// <summary>
        /// V13 §8.2 — records that a production (OBS) socket's design-time-only frame was
        /// dropped. Communication tier: like its editor-side sibling this is CORRECT
        /// behaviour, not a fault, so it must not light the operator's error surfaces — but
        /// it is logged because "my node flashes never fire" needs an answer, and the answer
        /// is "that socket is an OBS source, open the widget editor instead".
        /// </summary>
        private void ReportRefusedProductionInbound(string? layerId, string messageType)
        {
            string key = $"{layerId ?? "(none)"}|{messageType}";
            long n = _productionRefusalCounts.AddOrUpdate(key, 1, (_, prev) => prev + 1);
            if (n != 1 && n % 100 != 0) return;
            GlobalLogger.Log(
                $"HUDServer: refused {messageType} from a production (OBS) client on /hud/{layerId ?? "(none)"} — " +
                $"the widget trace is design-time only and is never driven by a live overlay (total={n}).",
                "HUDServer", LogLevel.Communication);
        }

        /// <summary>
        /// V13 §8.3 — the one-shot per-layer notice that a client connected without a valid
        /// connect token, naming the remedy.
        ///
        /// <para>System tier (unlike the two refusal counters, which are Communication):
        /// this one is actionable by the streamer and has a concrete fix. The usual cause is
        /// an OBS Browser Source still running a <c>compositor.js</c> cached from before the
        /// token existed, and the fix is a browser-source cache refresh.</para>
        ///
        /// <para>ONCE per layer, latched, because the trigger is a connect — an OBS scene
        /// cycling a stale source would otherwise repeat this paragraph on every flip. The latch
        /// survives a layer remove+register (i.e. every author save, see
        /// <see cref="_untrustedLayerLogged"/>) and is cleared only by <see cref="Stop"/>.</para>
        ///
        /// <para>The caller that classifies an upgrade also offers that one socket the self-heal
        /// reload (<see cref="RequestSelfHealReload"/>); this method only ever logs, so the
        /// re-report from the inbound trust gate cannot cause a second reload.</para>
        ///
        /// <para>The token value is NEVER named here, nor is the value the client presented.
        /// A log line carrying either would hand a capability to anything that can read the
        /// SystemLog ring buffer — which includes the Hub UI, the pop-out panels and every
        /// exported log file.</para>
        /// </summary>
        private void ReportUntrustedLayerClientOnce(string layerId, LayerClientKind clientKind)
        {
            string key = string.IsNullOrEmpty(layerId) ? "(none)" : layerId;
            if (!_untrustedLayerLogged.TryAdd(key, 1)) return;

            GlobalLogger.Log(
                $"HUDServer: a {clientKind} client on /hud/{key} connected WITHOUT this Hub's " +
                $"per-layer connect token. It still renders — every LIVE_SNAPSHOT / LIVE_PATCH / " +
                $"RUN_TRIGGER frame is unaffected — but it cannot use the design-time trace or " +
                $"return a Visual.Complete payload. The usual cause is an OBS Browser Source " +
                $"running a page cached from before this Hub started: right-click the source → " +
                $"Refresh cache of current page (or Interact → reload) and the token is picked up. " +
                $"(Logged once per layer.)",
                "HUDServer", LogLevel.System);
        }

        /// <summary>
        /// V13 §8.3 belt-and-braces — asks ONE untrusted page to reload itself so it re-fetches
        /// <c>/layer/&lt;id&gt;</c> and picks up the connect token, instead of degrading silently
        /// for the rest of its life. (Nothing else would ever reload it: compositor.js's
        /// <c>socket.onclose</c> reconnects the socket only, and <c>LAYER_RELOADED</c> prefers the
        /// soft reload, which re-fetches <c>/api/layer/&lt;id&gt;</c> and not the page.)
        ///
        /// <para><b>WHY IT IS TRIGGERED BY LIVE_HELLO AND NOT BY THE CLASSIFICATION ITSELF.</b>
        /// A page that cannot handle <c>HUD_RELOAD</c> must not be sent one, and "untrusted" alone
        /// does not distinguish a current page holding a stale token (which self-heals) from a page
        /// so old it predates the whole live channel, or from something that is not a compositor at
        /// all. <c>LIVE_HELLO</c> is the proof of protocol level: only a compositor.js that has the
        /// <c>HUD_RELOAD</c> arm sends it. Firing at classification time instead put an unsolicited
        /// frame on EVERY tokenless socket, which is also why it is wrong in principle — Hub would
        /// be shouting an instruction at clients that provably cannot obey it.</para>
        ///
        /// <para><b>Per socket, exactly once</b>, via <see cref="_selfHealOfferedSockets"/> — and
        /// the latch is required, not decorative: <c>sendLiveHello</c> is re-sent after every
        /// <c>softReloadLayer</c>, i.e. after every author save, on the SAME socket.</para>
        ///
        /// <para>The reload STORM §8.3 forbids is prevented on the BROWSER side, and it has to be:
        /// a reload closes this socket, so any latch kept here dies with it and the fresh page's
        /// socket would be instructed again, forever. compositor.js's <c>_selfHealHardReload</c>
        /// therefore latches in <c>sessionStorage</c>, which survives the reload it guards.</para>
        ///
        /// <para>Targeted at the ONE socket, never <see cref="SendToLayerAsync"/>: the other
        /// sockets on this layer may be perfectly trusted, and reloading them would be a
        /// self-inflicted flash on a live overlay. <c>GetOrAdd</c> (not <c>TryGetValue</c>) because
        /// the socket may not have been sent anything yet, so its pump may not exist; the teardown
        /// path removes whatever we create here.</para>
        ///
        /// <para>The frame carries a reason and NEVER the token — it is going to a page that just
        /// failed to prove provenance.</para>
        /// </summary>
        private void OfferSelfHealReloadOnce(WebSocket socket, string? layerId)
        {
            if (!_selfHealOfferedSockets.TryAdd(socket, 1)) return;
            try
            {
                if (socket.State != WebSocketState.Open) return;
                byte[] buff = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type   = SelfHealReloadFrameType,
                    reason = "untrusted",
                });
                var sender = _layerSenders.GetOrAdd(socket, static (s, ct) => new PerSocketSender(s, ct), _cts.Token);
                sender.TryEnqueue(buff);
                GlobalLogger.Log(
                    $"HUDServer: asking the untrusted overlay page on /hud/{layerId ?? "(none)"} to reload " +
                    $"itself ONCE so it re-fetches the page and picks up this layer's connect token. " +
                    $"If it comes back untrusted again it is left on the grace path — the page latches " +
                    $"the attempt itself, so this never loops.",
                    "HUDServer", LogLevel.Communication);
            }
            catch (Exception ex)
            {
                // Never fatal: failing to offer the self-heal just leaves the socket on the grace
                // path it is already on.
                GlobalLogger.Error("HUDServer", "self-heal reload instruction could not be enqueued", ex);
            }
        }

        /// <summary>
        /// V13 §8.3 — the inbound frame types an Untrusted socket may not act on AT ALL.
        ///
        /// <para><c>VISUAL_COMPLETE</c> is deliberately absent: it is DEGRADED (payload
        /// stripped) rather than dropped, inside its own switch arm, because dropping a
        /// completion would hang whatever script is waiting on it — strictly worse than
        /// accepting a payload-less ack from a page whose provenance we cannot prove.</para>
        ///
        /// <para><c>MEDIA_ENDED</c> was listed here ahead of the V15 sprint that introduced
        /// it, on purpose: §8.3 names it as one of the three privileged upward paths, and a
        /// gate that has to be remembered later is a gate that will be forgotten. V15 has
        /// since landed the switch arm, so this entry is now live rather than reserved — an
        /// untrusted page cannot advance the song-request queue. (It is also refused from an
        /// EDITOR socket, by the separate and orthogonal
        /// <see cref="IsInboundAllowedFromEditorClient"/>; the two gates answer different
        /// questions and a preview pane fails the second while passing this one.)</para>
        ///
        /// <para>Everything else returns false, so the default posture for a frame nobody has
        /// classified is UNCHANGED behaviour. This predicate must stay a deny-list of
        /// privileged paths, never become an allow-list of permitted ones — the latter would
        /// silently break the next frame type somebody adds.</para>
        /// </summary>
        internal static bool IsPrivilegedUpwardFrame(string messageType)
            => messageType switch
            {
                "DEBUG_WIDGET_NODE" => true,
                "MEDIA_ENDED"       => true,
                _                   => false,
            };

        /// <summary>
        /// Whether a /hud WebSocket upgrade may proceed, judged on the request's
        /// <c>Origin</c> header. See the call site in ProcessWebSocketRequestAsync for
        /// why a loopback bind is not sufficient on its own.
        ///
        /// <para>ABSENT Origin ⇒ allowed. Browsers always send it on a WebSocket
        /// handshake; non-browser clients (the test harness, any native tool, a future
        /// desktop shell) do not. So absence cannot be the browser attacker this gate
        /// exists for, while treating it as hostile would break every legitimate
        /// non-browser consumer.</para>
        ///
        /// <para>PRESENT Origin ⇒ must be http(s) on a LOOPBACK host at THIS server's
        /// port. compositor.js dials <c>window.location.host</c>, so the real overlay —
        /// and the Visualist WebView2 preview, which navigates the same URL — always
        /// matches whichever spelling (127.0.0.1 / localhost / [::1]) the page was
        /// served from. A page on any other host, or on another local port, does
        /// not.</para>
        /// </summary>
        internal static bool IsAllowedWebSocketOrigin(string? origin, int listenPort)
        {
            if (string.IsNullOrWhiteSpace(origin)) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (parsed.Port != listenPort) return false;

            string host = parsed.Host;
            // IsLoopback covers 127.0.0.0/8 and ::1; "localhost" is matched by name
            // because it is a host STRING here, not a resolved address.
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        }

        private bool IsAllowedWebSocketOrigin(HttpListenerRequest request)
            => IsAllowedWebSocketOrigin(request.Headers["Origin"], _listenPort);

        /// <summary>
        /// V13 §8.2 — forwards one widget-graph trace frame to Visualist on the TARGETED
        /// route: <c>Target = "Visualist"</c>, which <c>Bus.BroadcastToRemoteAsync</c> filters
        /// down to that single peer (<c>if (target != "*" &amp;&amp; target != id) continue;</c>).
        ///
        /// <para><b>Never <c>Target = "*"</c>.</b> A broadcast trace frame would be a real
        /// product bug rather than a style problem: Hub→Hub local routing HAS landed —
        /// <c>Bus.BroadcastAsync</c> delivers <c>Target "*" / "Hub"</c> frames to Hub's own
        /// consumers (OnMessageReceived, pending waits, the <c>on_bus</c> fan-out) — so a
        /// broadcast envelope WOULD run every <c>on_bus("DEBUG_WIDGET_NODE")</c> script once
        /// per trigger activation. Targeting one peer is what keeps the frame off that path:
        /// the local-delivery Target gate in <c>BroadcastAsync</c> skips any frame whose
        /// Target is neither <c>"*"</c> nor <c>"Hub"</c>. That guard is active, not
        /// hypothetical — keep this send targeted.</para>
        ///
        /// <para>This is the SAME route <c>DEBUG_NODE_EXEC</c> takes to Architect
        /// (ScriptManager.cs) — <c>BroadcastAsync</c> with an explicit non-wildcard
        /// <c>Target</c>, wrapped in <c>AsyncErrorBoundary.SafeRunAsync</c> so a bus fault
        /// during reconnect churn lands in the log instead of on the unobserved-task pump.</para>
        ///
        /// <para>Unlike <c>DEBUG_NODE_EXEC</c> there is NO connected-peer pre-gate
        /// (<c>DEBUG_NODE_EXEC</c> checks <c>Bus.IsArchitectConnected</c> before emitting).
        /// That gate exists on the Architect path because it fires once per executed NODE
        /// (~2500/sec on a busy chat) and pays serialisation each time. This frame is one
        /// message per trigger ACTIVATION and only ever arrives from an editor socket — which
        /// means Visualist is the thing that sent it — and <c>BroadcastToRemoteAsync</c>
        /// already early-outs on <c>_clients.IsEmpty</c> before serialising anything. Adding
        /// the gate would buy nothing and would make the forward unobservable to the in-proc
        /// bridge.</para>
        /// </summary>
        private void ForwardWidgetNodeTrace(
            string layerId, string widgetId, string triggerName, List<string> nodeIds, int seq)
        {
            try
            {
                string payload = JsonSerializer.Serialize(new
                {
                    layerId,
                    widgetId,
                    triggerName,
                    nodeIds,
                    seq,
                });
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => Bus.Instance.BroadcastAsync(new BusMessage
                    {
                        Type    = "DEBUG_WIDGET_NODE",
                        Source  = "Hub",
                        Target  = "Visualist",
                        Payload = payload,
                    }, _cts.Token),
                    "HUDServer",
                    $"DEBUG_WIDGET_NODE forward for {layerId}/{widgetId}/{triggerName}",
                    _cts.Token);
            }
            catch (Exception ex)
            {
                // Bus.Instance's own construction can throw on partial init (the same guard
                // WidgetTriggerQueue's WIDGET_TIMEOUT broadcast carries), and a trace frame
                // must never take down the receive loop that delivered it.
                GlobalLogger.Error("HUDServer",
                    $"DEBUG_WIDGET_NODE forward failed for {layerId}/{widgetId}", ex);
            }
        }

        /// <summary>
        /// V15 — hands one <c>MEDIA_ENDED</c> frame to the ONLY service allowed to consume
        /// it. The whole upward amendment ends here: nothing else in the Hub sees this
        /// frame, no script is raised, nothing goes on the bus.
        ///
        /// <para><b>The result is read, not discarded, and that is the point.</b>
        /// <c>NotifyMediaEndedAsync</c> returns false for every ignore path — the tool is
        /// off, nothing is playing, the reporting widget was on a different track, a
        /// SECOND OBS source on the same layer reported the same
        /// <c>(videoId, play_token)</c> pair the first one already spent, or a mod's
        /// <c>!skip</c> moved the queue between the claim and the advance. All of those are
        /// normal, so none of them is an error; but when a streamer's queue does not
        /// advance, this Debug line is the only evidence anyone will have of which one
        /// happened. Swallowing the bool would make a stuck queue undiagnosable — which is
        /// also why the service's late bails (the tool switched off mid-flight, the skip
        /// race) report false rather than a true that moved nothing.</para>
        ///
        /// <para>Debug tier rather than System: on a two-source layer exactly one refusal
        /// is expected per track, by design. A System-tier line would train the streamer to
        /// ignore their own log.</para>
        /// </summary>
        private static async Task HandleMediaEndedAsync(string layerId, string widgetId, string videoId, long seq)
        {
            bool advanced = await SongRequestService.Instance
                .NotifyMediaEndedAsync(videoId, seq)
                .ConfigureAwait(false);
            if (advanced) return;

            // Capped only on the way into the log, at the same 240 the frame-drop preview
            // above uses and the same the browser's own diagnostic sender applies to its
            // detail: both of these are strings a PAGE supplied, and a stale or hand-crafted
            // source could otherwise push an unbounded value straight into the streamer's log.
            // Capped here rather than at the read, because the service compares the full
            // videoId — truncating before that would change what the queue matches on.
            static string ForLog(string s) => s.Length <= 240 ? s : s.Substring(0, 240) + "…";

            GlobalLogger.Log(
                $"HUDServer: MEDIA_ENDED layer='{ForLog(layerId)}' widget='{ForLog(widgetId)}' video='{ForLog(videoId)}' token={seq} did not advance the song queue — "
                + "the tool is off, nothing is playing, another browser source already reported "
                + "this track, or a skip moved the queue first.",
                "HUDServer", LogLevel.Debug);
        }

        private async Task HandleTranslateRequestAsync(WebSocket socket, string reqId, string text, string targetLang)
        {
            string translated;
            try
            {
                translated = await TranslationService.Instance.TranslateAsync(text, targetLang, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("HUDServer", "TRANSLATE_REQUEST failed", ex);
                translated = text;
            }
            try
            {
                if (socket.State != WebSocketState.Open) return;
                // SerializeToUtf8Bytes, same one-pass rationale as the
                // fan-out paths above.
                byte[] buff = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type       = "TRANSLATE_RESPONSE",
                    reqId,
                    translated,
                    targetLang,
                });
                // Route through the per-socket sender so TRANSLATE_RESPONSE
                // doesn't race CAPTION_UPDATE / RUN_TRIGGER frames on the same socket
                // (the pump's single reader serializes all sends naturally).
                // TryGetValue (not GetOrAdd) so a translate
                // request that arrived after the receive loop began tearing down doesn't
                // resurrect a fresh sender against a dead socket. If no sender exists,
                // the socket is unreachable and the translate response would have nowhere
                // to land.
                if (!_layerSenders.TryGetValue(socket, out var sender)) return;
                sender.TryEnqueue(buff);
            }
            catch { /* socket dropped — browser will retry on the next request */ }
        }

        private static string ReadString(JsonElement obj, string key)
        {
            if (obj.ValueKind != JsonValueKind.Object) return "";
            if (!obj.TryGetProperty(key, out var prop)) return "";
            return prop.ValueKind == JsonValueKind.String ? (prop.GetString() ?? "") : "";
        }

        /// <summary>
        /// V13 §8.1 — presence-preserving sibling of <see cref="ReadString"/>: returns
        /// <c>null</c> when the property is ABSENT (or not a JSON string) and the string's
        /// own value otherwise, so an explicit <c>"payload": ""</c> is distinguishable from
        /// an omitted <c>payload</c>.
        ///
        /// <para>That distinction is the compatibility gate: the browser omits the property
        /// exactly when the <c>Visual.Complete → Payload</c> pin is unwired, and an omitted
        /// payload has to reach <c>Bus.ResolveVisualWait</c> as <c>null</c> so nothing
        /// downstream can tell V13 happened. Collapsing both to <c>""</c> here would work by
        /// accident today and stop working the moment any consumer distinguishes them.</para>
        ///
        /// <para>A non-string <c>payload</c> (number, object, array — a hostile or buggy
        /// sender) is treated as absent rather than stringified: the wire contract says
        /// String, and coercing an object into the script's <c>global._wait_payload</c> would
        /// be inventing data.</para>
        /// </summary>
        private static string? ReadOptionalString(JsonElement obj, string key)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(key, out var prop)) return null;
            if (prop.ValueKind != JsonValueKind.String) return null;
            return prop.GetString();
        }

        /// <summary>
        /// Integer sibling of <see cref="ReadString"/>. Fails closed to 0 for a
        /// missing / non-numeric / out-of-Int32-range property — <c>GetInt32()</c>
        /// throws on the last case, which would abort the whole inbound switch, so
        /// <c>TryGetInt32</c> is the only safe read for browser-supplied numbers
        /// (same rationale as the inline FPS parse above).
        /// </summary>
        private static int ReadInt(JsonElement obj, string key)
        {
            if (obj.ValueKind != JsonValueKind.Object) return 0;
            if (!obj.TryGetProperty(key, out var prop)) return 0;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int n)) return n;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out int parsed)) return parsed;
            return 0;
        }

        /// <summary>
        /// 64-bit sibling of <see cref="ReadInt"/>, for <c>MEDIA_ENDED</c>'s
        /// <c>seq</c> — the song-request <c>play_token</c>, which is a <c>long</c> on the
        /// Hub side and must not be silently truncated into an <c>int</c>.
        ///
        /// <para>Same fail-closed-to-0 posture and the same <c>TryGetInt64</c> reason as
        /// its sibling: <c>GetInt64()</c> throws on an out-of-range property, which would
        /// abort the whole inbound switch on a malformed frame. 0 is a safe failure here
        /// because <c>play_token</c> starts at 0 and the first selection makes it 1, so a
        /// zero can never match a playing track — a garbage <c>seq</c> is refused rather
        /// than being read as some other track's token.</para>
        ///
        /// <para>A browser JSON number is a double, so a value large enough to have lost
        /// integer precision fails <c>TryGetInt64</c>'s exactness and reads 0. That is the
        /// correct direction: 2^53 selections in one session is not reachable, and a
        /// fractional token is by definition not one this Hub minted.</para>
        /// </summary>
        private static long ReadLong(JsonElement obj, string key)
        {
            if (obj.ValueKind != JsonValueKind.Object) return 0;
            if (!obj.TryGetProperty(key, out var prop)) return 0;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long n)) return n;
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out long parsed)) return parsed;
            return 0;
        }

        /// <summary>
        /// Reads a JSON string array (LIVE_HELLO's <c>keys</c>), skipping non-string
        /// and blank elements. Returns an empty list for a missing or non-array
        /// property — same fail-closed posture as <see cref="ReadString"/>: a
        /// malformed handshake subscribes to nothing instead of throwing out of the
        /// receive loop. Caps are the store's business, not the transport's.
        /// </summary>
        private static List<string> ReadStringArray(JsonElement obj, string key)
        {
            if (obj.ValueKind != JsonValueKind.Object) return new List<string>();
            if (!obj.TryGetProperty(key, out var prop)) return new List<string>();
            if (prop.ValueKind != JsonValueKind.Array) return new List<string>();
            // Pre-size from the declared length but clamp it: the element count is
            // browser-supplied and bounded only by the 1 MiB inbound frame cap, so a
            // hostile HELLO must not get to name its own allocation size.
            var list = new List<string>(Math.Min(prop.GetArrayLength(), 1024));
            foreach (var el in prop.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                string s = el.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(s)) continue;
                list.Add(s);
            }
            return list;
        }

        // ──────────────────────────────────────────────────────────────────
        //  WEBHOOK HARDENING HELPERS
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the request body into a UTF-8 string, capping at <paramref name="maxBytes"/>.
        /// Throws <see cref="InvalidDataException"/> if the body exceeds the cap (handles
        /// chunked transfer where Content-Length is unknown).
        /// </summary>
        private static async Task<string> ReadBodyWithCapAsync(Stream input, int maxBytes, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            byte[] buf = new byte[8192];
            int total = 0;
            int read;
            // Observe ct on every ReadAsync so server shutdown unblocks even
            // when the webhook caller is slow-loris'ing the body. ServeStaticFileAsync
            // already threads its CT; this site was missed.
            while ((read = await input.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new InvalidDataException("body exceeds cap");
                ms.Write(buf, 0, read);
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        /// <summary>
        /// Fixed-window 60s rate limiter, keyed by webhook name. Returns false if this hit
        /// would exceed <see cref="WebhookRateLimitPerMin"/>. Counts only successful posts
        /// (auth + size already verified before we call this).
        ///
        /// Caps the dict at <see cref="WebhookHitsMaxKeys"/> entries; on
        /// overflow we evict the least-recently-seen entries first. Combined with
        /// the IsValidWebhookName guard at the route entry, _webhookHits can no
        /// longer grow unboundedly.
        /// </summary>
        private static bool TryRecordWebhookHit(string name)
        {
            DateTime now = DateTime.UtcNow;
            bool allowed = true;

            // Prune *before* the insert so a flood of unique names can't push
            // the dict past the cap during the AddOrUpdate.
            if (_webhookHits.Count >= WebhookHitsMaxKeys && !_webhookHits.ContainsKey(name))
            {
                PruneWebhookHitsLru(targetCount: WebhookHitsMaxKeys - 16);
            }

            _webhookHits.AddOrUpdate(
                name,
                _ => (now, 1, now),
                (_, prev) =>
                {
                    if (now - prev.windowStart > TimeSpan.FromMinutes(1))
                        return (now, 1, now);
                    int next = prev.count + 1;
                    if (next > WebhookRateLimitPerMin)
                    {
                        allowed = false;
                        // Touch lastSeen even when rate-limited — the requester
                        // is active, just rate-shaped, and we don't want their
                        // entry pruned out from under them.
                        return (prev.windowStart, prev.count, now);
                    }
                    return (prev.windowStart, next, now);
                });
            return allowed;
        }

        /// <summary>
        /// Evict entries from <see cref="_webhookHits"/> down to
        /// <paramref name="targetCount"/>, oldest <c>lastSeen</c> first. Called
        /// from <see cref="TryRecordWebhookHit"/> when the dict is about to
        /// overflow. Exposed via the field's <c>internal</c> visibility for tests.
        /// </summary>
        private static void PruneWebhookHitsLru(int targetCount)
        {
            if (targetCount < 0) targetCount = 0;
            // Snapshot once — the dict is concurrent so the count can drift, but
            // we just need a stable view to pick the LRU candidates.
            var snapshot = _webhookHits.ToArray();
            if (snapshot.Length <= targetCount) return;
            // Sort by lastSeen ascending — oldest first.
            Array.Sort(snapshot, (a, b) => a.Value.lastSeen.CompareTo(b.Value.lastSeen));
            int toRemove = snapshot.Length - targetCount;
            for (int i = 0; i < toRemove; i++)
            {
                _webhookHits.TryRemove(snapshot[i].Key, out _);
            }
        }

        /// <summary>
        /// Webhook names must match <c>[A-Za-z0-9_-]+</c> and be
        /// shorter than <see cref="WebhookNameMaxLength"/>. Mirrors
        /// <see cref="IsValidLayerId"/> but lives behind its own predicate so
        /// the two routes can diverge if they need to in the future.
        /// </summary>
        private static bool IsValidWebhookName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length > WebhookNameMaxLength) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'A' && c <= 'Z')
                       || (c >= 'a' && c <= 'z')
                       || (c >= '0' && c <= '9')
                       || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Truncate a possibly-attacker-controlled string for safe inclusion in
        /// a log line. Replaces control chars with '?' and caps length so a
        /// rejected webhook name can't be used to flood the log ring with one
        /// massive line.
        /// </summary>
        private static string TruncateForLog(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(Math.Min(s.Length, maxLen));
            int upto = Math.Min(s.Length, maxLen);
            for (int i = 0; i < upto; i++)
            {
                char c = s[i];
                sb.Append(c < 0x20 || c == 0x7F ? '?' : c);
            }
            if (s.Length > maxLen) sb.Append("…");
            return sb.ToString();
        }

        /// <summary>
        /// Write a 405 Method Not Allowed response with the right
        /// <c>Allow</c> header. Keeps the dispatcher entry-points readable and
        /// makes proxies / scanners happy.
        /// </summary>
        private static void SendMethodNotAllowed(HttpListenerContext context, string allow)
        {
            try
            {
                context.Response.StatusCode = 405;
                context.Response.Headers["Allow"] = allow;
                context.Response.Close();
            }
            catch
            {
                // Best-effort — the listener may have been disposed between the
                // check and here. The catch matches the rest of this file's
                // shutdown-tolerant style.
            }
        }

        /// <summary>
        /// Emit a CORS preflight response for the /webhook/ POST route.
        /// 204 No Content + Access-Control-Allow-{Origin,Methods,Headers} so a
        /// browser issuing a CORS POST (e.g. dashboard panel calling /webhook/
        /// from a different origin) gets the green light. Other routes are
        /// GET-only and don't need a preflight branch.
        /// </summary>
        private static void SendCorsPreflight(HttpListenerContext context, string allowMethods, string allowHeaders)
        {
            try
            {
                context.Response.StatusCode = 204;
                context.Response.Headers["Allow"] = allowMethods;
                context.Response.Headers["Access-Control-Allow-Origin"]  = "*";
                context.Response.Headers["Access-Control-Allow-Methods"] = allowMethods;
                context.Response.Headers["Access-Control-Allow-Headers"] = allowHeaders;
                context.Response.Headers["Access-Control-Max-Age"]       = "600";
                context.Response.Close();
            }
            catch
            {
                // Shutdown-tolerant — same rationale as SendMethodNotAllowed above.
            }
        }

        /// <summary>
        /// Only accept layer ids matching `[A-Za-z0-9_-]+`. Anything else means
        /// the route was passed extra path segments or unsafe characters.
        /// </summary>
        private static bool IsValidLayerId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'A' && c <= 'Z')
                       || (c >= 'a' && c <= 'z')
                       || (c >= '0' && c <= '9')
                       || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        //  V6 — EDITOR-vs-PRODUCTION CLIENT CLASSIFICATION
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Classifies a <c>/hud/&lt;id&gt;</c> WebSocket upgrade as a Visualist design-time
        /// surface or a production OBS Browser Source, from the upgrade request's query.
        ///
        /// <para>THE INVARIANT THIS ENFORCES: the server's classification must agree with
        /// compositor.js's own page-side <c>IS_DESIGN_TIME</c>. If they ever disagreed, a
        /// surface would render design-time mocks (fake leaderboard names, PreviewText) while
        /// counting as production presence — the worst of both halves. compositor.js derives
        /// IS_DESIGN_TIME from three page params: <c>?widget=&lt;id&gt;</c>, <c>?capture=1</c>
        /// and <c>?client=editor</c>. This method therefore honours the same three, even
        /// though every Visualist page URL now also carries <c>client=editor</c> explicitly:
        /// the redundancy is what makes the two halves impossible to drift apart if a future
        /// surface forwards only its own marker.</para>
        ///
        /// <para>Anything else — including a bare query and an unrecognised <c>client</c>
        /// value — is Production. Failing toward production is deliberate: it is the pre-V6
        /// behaviour, so a misclassification degrades to "presence is too generous" (a
        /// cosmetic regression) rather than "a real OBS source is invisible to every
        /// dispatch path" (a broken stream). An OBS Browser Source never sets any of these
        /// params, so it can never be misread as an editor surface by accident.</para>
        ///
        /// <para>Takes the parsed collection rather than an <c>HttpListenerRequest</c> so the
        /// classification is unit-testable without an HttpListener; the caller passes
        /// <c>context.Request.QueryString</c>, which HttpListener has already parsed.</para>
        /// </summary>
        internal static LayerClientKind ClassifyClientKind(System.Collections.Specialized.NameValueCollection? query)
        {
            if (query is null) return LayerClientKind.Production;

            if (string.Equals(query["client"], "editor", StringComparison.OrdinalIgnoreCase))
                return LayerClientKind.Editor;

            // The per-widget preview pane / popout (?widget=<id>) and the hidden
            // thumbnail-capture host (?capture=1) are design-time by compositor.js's own
            // rule. Presence of the key is the signal, matching the page side's
            // `widgetFilterId !== null` / `params.has('capture')`.
            if (query["widget"] is not null)  return LayerClientKind.Editor;
            if (query["capture"] is not null) return LayerClientKind.Editor;

            return LayerClientKind.Production;
        }

        /// <summary>
        /// V6 — whether an inbound browser frame of <paramref name="messageType"/> may be
        /// acted on when it arrives from a Visualist design-time socket.
        ///
        /// <para>The classification, arm by arm (see the switch in
        /// <c>HandleInboundFromBrowser</c> for the bodies):</para>
        /// <list type="bullet">
        /// <item><c>VISUAL_COMPLETE</c> — REFUSED. Side-effecting, and the whole reason V6
        /// exists: it resolves the WidgetTriggerQueue TCS and the bus visual-wait, so a
        /// preview pane acking a widget it never drew is what completed a script's
        /// <c>wait_for_visual</c> against a pane.</item>
        /// <item><c>FPS</c> — REFUSED. Side-effecting: it writes the layer-KEYED
        /// <c>LayerRegistry._fps</c> map that <c>IsLayerActive</c>'s fallback arm reads, so
        /// one preview render would keep the layer reading ACTIVE for the 5 s TTL and would
        /// average preview renders into the Hub's FPS readout. Refusing here is what let
        /// the parked signal-divergence item stay parked (no per-socket re-key needed).</item>
        /// <item><c>LIVE_HELLO</c> — ALLOWED. Side-effecting, but refusing it starves the
        /// preview of live-channel data, and the subscription is layer-scoped and identical
        /// across every socket on the layer by contract (the client never narrows its key
        /// set by widget), so an editor hello asks for nothing a production hello wouldn't.</item>
        /// <item><c>TRANSLATE_REQUEST</c> — ALLOWED. Side-effecting off-box (translation
        /// API), but it touches no presence and no trigger state, the reply is per-socket,
        /// and the client caches by (text, lang). A preview that couldn't translate would
        /// render different text than OBS, which defeats the point of the preview.</item>
        /// <item><c>TRIGGER_RECEIVED</c> — ALLOWED. Benign: the handler body is a no-op
        /// diagnostic sink.</item>
        /// <item><c>TRIGGER_DIAGNOSTIC</c> — ALLOWED. Benign (log only). Kept for editor
        /// sockets on purpose: a preview pane's silent early-returns are exactly what an
        /// author needs to see. The log line carries the client kind so preview-sourced
        /// <c>not_visible</c> noise is attributable.</item>
        /// <item>V15 <c>MEDIA_ENDED</c> — REFUSED. Side-effecting on SHARED state, and the
        /// most consequential kind: it advances the live song-request queue. A Visualist
        /// preview pane and the hidden thumbnail-capture host are both classified Editor AND
        /// both classify as Trusted, so the V13 provenance gate does not touch them — without
        /// an arm here, opening a widget preview while the stream is running would skip the
        /// streamer's track. Refused rather than degraded (the VISUAL_COMPLETE treatment)
        /// because nothing waits on it: a dropped MEDIA_ENDED costs a queue that stops
        /// advancing until the next report, while an accepted one costs a track nobody got to
        /// hear.</item>
        /// <item>V13 <c>DEBUG_WIDGET_NODE</c> — ALLOWED, and in fact editor-ONLY: it is the
        /// design-time trace, so its arm inverts this predicate's usual direction and refuses
        /// the PRODUCTION case itself (see <c>ReportRefusedProductionInbound</c>). It reaches
        /// here through the <c>_ =&gt; true</c> default rather than an explicit arm, because
        /// this predicate's contract is "which frames must an EDITOR be stopped from acting
        /// on" and the answer for the trace frame is "none".</item>
        /// </list>
        ///
        /// <para>Unknown types return true — this predicate must not become a silent
        /// allowlist that swallows a message arm somebody adds later; the switch already
        /// ignores types it doesn't know.</para>
        /// </summary>
        internal static bool IsInboundAllowedFromEditorClient(string messageType)
            => messageType switch
            {
                "VISUAL_COMPLETE" => false,
                "FPS"             => false,
                "MEDIA_ENDED"     => false,
                _                 => true,
            };

        // ──────────────────────────────────────────────────────────────────
        //  MIME TYPE DETECTION
        // ──────────────────────────────────────────────────────────────────

        private static string GetMimeType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".html" or ".htm" => "text/html",
                ".css"            => "text/css",
                ".js"             => "application/javascript",
                ".json"           => "application/json",
                ".png"            => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif"            => "image/gif",
                ".svg"            => "image/svg+xml",
                ".webp"           => "image/webp",
                ".mp4"            => "video/mp4",
                ".webm"           => "video/webm",
                ".mkv"            => "video/x-matroska",
                ".avi"            => "video/x-msvideo",
                ".mov"            => "video/quicktime",
                ".mp3"            => "audio/mpeg",
                ".ogg"            => "audio/ogg",
                ".wav"            => "audio/wav",
                ".flac"           => "audio/flac",
                ".aac"            => "audio/aac",
                ".woff"           => "font/woff",
                ".woff2"          => "font/woff2",
                ".ttf"            => "font/ttf",
                // Fill in the common modern types we were silently
                // serving as application/octet-stream. Browsers refuse to use
                // .wasm if not labeled application/wasm; modern bundlers ship
                // .map files with content-type sourcemap.
                ".ico"            => "image/x-icon",
                ".map"            => "application/json",
                ".mjs"            => "application/javascript",
                ".wasm"           => "application/wasm",
                ".vtt"            => "text/vtt",
                _                 => "application/octet-stream"
            };
        }

        // ──────────────────────────────────────────────────────────────────
        //  PER-SOCKET SEND PUMP  (backpressure)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Per-WebSocket outbound pump. Owns a bounded <see cref="Channel{T}"/>
        /// of pre-serialized frames and a single-reader task that drains the
        /// channel into <c>ws.SendAsync</c>. Producers (<see cref="FanOutAsync"/>,
        /// <see cref="HandleTranslateRequestAsync"/>) call <see cref="TryEnqueue"/>
        /// which is non-blocking and returns immediately even when the pump is
        /// slow.
        ///
        /// <para>
        /// Backpressure: <see cref="MaxBufferedFrames"/> cap with
        /// <see cref="BoundedChannelFullMode.DropOldest"/> policy. When a client
        /// can't keep up, the oldest queued frame falls off the back rather than
        /// the producer blocking — picking "frame currency" over "frame
        /// completeness" because RUN_TRIGGER / CAPTION_UPDATE / VISUAL_TRIGGER
        /// frames are all "latest wins" by design (a stale chat overlay or
        /// caption is worse than a missed frame). Drops are counted and
        /// surfaced through <see cref="GlobalLogger"/> at Communication tier
        /// every <see cref="DropReportInterval"/>th drop so a slow OBS browser
        /// source is visible to the operator without saturating the syslog.
        /// </para>
        ///
        /// <para>
        /// Thread-safety: <see cref="WebSocket.SendAsync"/> is not thread-safe;
        /// the pump is the single reader of the channel and the only caller
        /// of SendAsync on its socket, so concurrent producers never interleave
        /// frames on the same socket (the send-serialization contract is preserved).
        /// </para>
        /// </summary>
        private sealed class PerSocketSender : IDisposable
        {
            // Cap roughly matches a few hundred ms of caption/trigger traffic at
            // a brisk pace. Tunable if a future feature surfaces sustained
            // pressure that should buffer further before dropping.
            private const int MaxBufferedFrames  = 256;
            private const int DropReportInterval = 100;

            private readonly WebSocket _socket;
            private readonly Channel<byte[]> _channel;
            private readonly Task _pumpTask;
            private readonly CancellationTokenSource _cts;
            private long _droppedFrames;
            private long _lastReportedDrops;
            private int _disposed;

            public PerSocketSender(WebSocket socket, CancellationToken serverCt)
            {
                _socket = socket;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
                _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxBufferedFrames)
                {
                    FullMode     = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
                _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
            }

            /// <summary>
            /// Enqueue a frame for sending. Non-blocking. Returns false only when
            /// the channel writer has been completed (post-Dispose); a "queue
            /// full" condition returns true because DropOldest evicted an older
            /// frame to make room.
            /// </summary>
            public bool TryEnqueue(byte[] frame)
            {
                if (!_channel.Writer.TryWrite(frame))
                {
                    // Writer completed — the pump has shut down; the caller's
                    // frame has nowhere to land.
                    return false;
                }
                // DropOldest detection. After TryWrite, depth == cap means
                // either (a) we just filled the last slot, or (b) we evicted
                // the oldest. We can't distinguish from a depth check alone,
                // but sustained over-cap pressure manifests as repeated
                // max-depth observations — accumulating false positives at the
                // exact-fill boundary is acceptable for a telemetry counter.
                if (_channel.Reader.Count >= MaxBufferedFrames)
                {
                    long total = Interlocked.Increment(ref _droppedFrames);
                    ReportDropIfNeeded(total);
                }
                return true;
            }

            private void ReportDropIfNeeded(long currentDropCount)
            {
                long last = Interlocked.Read(ref _lastReportedDrops);
                if (currentDropCount - last < DropReportInterval) return;
                // CompareExchange so a concurrent producer doesn't double-log
                // around the threshold crossing.
                if (Interlocked.CompareExchange(ref _lastReportedDrops, currentDropCount, last) != last)
                    return;
                GlobalLogger.Log(
                    $"HUDServer: per-socket send buffer dropped ~{currentDropCount} frames (cap={MaxBufferedFrames}). " +
                    "Client is consuming slower than the producer; oldest frames are evicted by DropOldest policy.",
                    "HUDServer", LogLevel.Communication);
            }

            private async Task PumpAsync(CancellationToken ct)
            {
                try
                {
                    var reader = _channel.Reader;
                    while (!ct.IsCancellationRequested)
                    {
                        bool readable;
                        try
                        {
                            readable = await reader.WaitToReadAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        if (!readable) break;

                        while (reader.TryRead(out var frame) && frame != null)
                        {
                            if (_socket.State != WebSocketState.Open) continue;
                            try
                            {
                                await _socket.SendAsync(
                                    new ArraySegment<byte>(frame),
                                    WebSocketMessageType.Text,
                                    true,
                                    ct).ConfigureAwait(false);
                            }
                            // A misbehaving socket aborts its own
                            // pump but doesn't affect other sockets. The receive-loop
                            // finally + grace-teardown will dispose this sender when the
                            // disconnect is observed.
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* socket abort; continue draining */ }
                            catch (OperationCanceledException) { return; }
                            catch { /* drop on error; teardown owned by receive-loop finally */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("HUDServer", "PerSocketSender pump faulted", ex);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { _channel.Writer.TryComplete(); } catch { }
                try { _cts.Cancel(); } catch { }
                // We deliberately do NOT await _pumpTask — Dispose runs from the
                // grace-teardown callback or HUDServer.Stop which is sync. The
                // pump observes the cancelled CT on its next iteration and
                // exits. The Task is unobserved by design (any fault is already
                // logged inside PumpAsync).
                try { _cts.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Payload for the
    /// <see cref="HUDServer.OnWebhookFired"/> event. One instance per accepted
    /// /webhook/&lt;name&gt; POST; the Hub WebhookPanel keeps the last N
    /// instances in a rolling buffer for its tail UI.
    /// </summary>
    /// <param name="FiredAtUtc">UTC timestamp at which the webhook completed
    /// its validation gauntlet and the 200 response was queued.</param>
    /// <param name="Endpoint">The full request path including the leading
    /// <c>/webhook/</c> prefix (e.g. <c>/webhook/alerts</c>).</param>
    /// <param name="RemoteAddress">The caller's remote endpoint as captured
    /// from <c>HttpListenerRequest.RemoteEndPoint</c> — typically an
    /// <c>IP:port</c> pair.</param>
    /// <param name="PayloadBytes">UTF-8 byte length of the post body after
    /// the size + secret guards passed. Counts the body that the script
    /// dispatch saw, not the raw HTTP frame.</param>
    public sealed record WebhookActivity(
        DateTime FiredAtUtc,
        string   Endpoint,
        string   RemoteAddress,
        int      PayloadBytes);
}
