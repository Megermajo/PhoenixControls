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

        // Per-layer WebSocket connection cap. OBS only opens a small fixed
        // number of browser sources per scene (Preview + Studio = 2; one or two spares
        // for multi-monitor layouts), so 4 is generous. Without this cap an attacker
        // could fuzz /hud/<layerId> until the HashSet inside LayerRegistry exhausted
        // RAM; with it, the 5th concurrent connection per layer gets closed with code
        // 1013 (Try Again Later) and the receive loop never starts.
        private const int MAX_CONNECTIONS_PER_LAYER = 4;

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
        // Per-layer WebSocket connections live in `_layerRegistry`. ClientCount and
        // OnClientCountChanged read from there; the legacy single-broadcast `_clients`
        // list was retired alongside the rewrite of BroadcastRawAsync to fan out via
        // the registry.
        public int ClientCount => _layerRegistry.GetTotalConnectionCount();
        public event Action<int>? OnClientCountChanged;

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
            // Re-emit LayerRegistry's connection-count changes as our own
            // OnClientCountChanged event so existing subscribers (status bar)
            // don't need to know about the registry.
            _layerRegistry.OnConnectionsChanged += FireClientCountChanged;
            // Prune the per-layer drop-count diagnostic dict when a
            // layer is removed from the registry (Deleted/Renamed-away in
            // LayerWatcher). Without this hook the dict grew across the Hub
            // process lifetime — bounded by attacker-controlled layer ids? no,
            // but bounded by every layer the user ever loaded, which adds up
            // for streamers iterating layouts. The subscriber is unhooked in
            // Stop() so a recreated HUDServer doesn't accumulate them on the
            // singleton registry.
            _layerRegistry.LayerRemoved += OnLayerRemoved;
            _overlayPath = ResolveOverlayPath();
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
            MediaDirectory = ResolveDataSubfolder("media",  Paths.AppMedia);
            try { Directory.CreateDirectory(MediaDirectory); } catch { /* best-effort */ }
        }

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

        private void FireClientCountChanged(int total)
        {
            try { OnClientCountChanged?.Invoke(total); }
            catch (Exception ex)
            {
                GlobalLogger.Error("HUDServer", "OnClientCountChanged subscriber threw", ex);
            }
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
            // Symmetric prune for the reload-stamp dict so a layer
            // removed mid-grace-window doesn't leak its entry until the next
            // periodic sweep.
            _recentlyReloaded.TryRemove(layerId, out _);
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

            // Unhook the registry forwarder so a recreated HUDServer doesn't
            // accumulate stale subscriptions on the singleton LayerRegistry.
            try { _layerRegistry.OnConnectionsChanged -= FireClientCountChanged; } catch { }
            // Symmetric unhook for the LayerRemoved subscription.
            try { _layerRegistry.LayerRemoved -= OnLayerRemoved; } catch { }

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
            try
            {
                foreach (var layerId in _layerRegistry.GetActiveLayerIds())
                {
                    foreach (var ws in _layerRegistry.GetConnections(layerId))
                    {
                        try { _layerRegistry.UnregisterConnection(layerId, ws); } catch { }
                    }
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

                // /asset/url?u=<encoded> — Phase 7 URL-image cache proxy.
                if (path.Equals("/asset/url", StringComparison.OrdinalIgnoreCase))
                {
                    string? remote = context.Request.QueryString["u"];
                    await ServeCachedUrlAsync(context, remote);
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
                if (path == "/" || path == "/overlay") path = "/index.html";

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
        private async Task ServeMediaListingAsync(HttpListenerContext context)
        {
            try
            {
                string rootFull = Path.GetFullPath(MediaDirectory);
                if (!Directory.Exists(rootFull))
                {
                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode = 200;
                    var empty = System.Text.Encoding.UTF8.GetBytes("[]");
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

        private static string MediaKindForExtension(string ext) => ext.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" => "image",
            ".mp4" or ".webm" or ".mov" or ".m4v"                       => "video",
            ".mp3" or ".wav" or ".ogg" or ".m4a" or ".flac"             => "audio",
            _                                                            => "other",
        };

        private async Task ServeCachedUrlAsync(HttpListenerContext context, string? remoteUrl)
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

            string? cached = await _urlCache.GetCachedPathAsync(remoteUrl, _cts.Token);
            if (cached is null || !File.Exists(cached))
            {
                context.Response.StatusCode = 502;
                context.Response.Close();
                return;
            }

            // Stream the cached file rather than buffering the whole thing into a
            // single byte[]. Multi-MB PNGs/GIFs would land on the LOH and stall GC; a
            // 64KB streaming copy stays well under the 85KB threshold. Mirrors the
            // ServeStaticFileAsync success path.
            context.Response.ContentType     = GetMimeType(cached);
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
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob:; " +
                "media-src 'self' data: blob:; " +
                "connect-src 'self' ws: wss:; " +
                "font-src 'self' data:";
            await ServeStaticFileAsync(context, indexPath, "/index.html");
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
            int currentCount = _layerRegistry.GetLiveConnectionCount(layerId!);
            if (currentCount >= MAX_CONNECTIONS_PER_LAYER)
            {
                GlobalLogger.Log(
                    $"HUDServer: rejecting /hud/{layerId} — per-layer connection cap ({MAX_CONNECTIONS_PER_LAYER}) reached.",
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
            if (!_layerRegistry.TryRegisterConnection(layerId!, socket, MAX_CONNECTIONS_PER_LAYER))
            {
                GlobalLogger.Log(
                    $"HUDServer: rejecting /hud/{layerId} at atomic gate — per-layer connection cap ({MAX_CONNECTIONS_PER_LAYER}) raced past the pre-upgrade fast-path.",
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
            // Demote layer-presence chatter to
            // Communication tier. An OBS scene cycling through 5 browser-source
            // panels produced 10+ System-tier rows/sec in the syslog every time
            // the user toggled scenes, growing the SystemLog ring buffer with
            // noise. Communication tier still surfaces to the operator but the
            // default SystemLog filter (Info+Warn+Error) won't include it.
            GlobalLogger.Log($"Layer client connected: /hud/{layerId}", "HUDServer", LogLevel.Communication);

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
        //  BROADCAST
        // ──────────────────────────────────────────────────────────────────

        public Task BroadcastAsync(object message)
        {
            // SerializeToUtf8Bytes folds the
            // Serialize → string → GetBytes(string) double-allocation into a
            // single pass. Hot fan-out path (CAPTION_UPDATE / WIDGET_UPDATE /
            // VISUAL_SET_PROPERTY all flow through here), so the saved string
            // allocation matters under sustained traffic.
            byte[] buff = JsonSerializer.SerializeToUtf8Bytes(message);
            return FanOutAsync(buff, EnumerateAllLayerSockets(), _cts.Token);
        }

        // Broadcast now fans out across every
        // connected per-layer WebSocket via `_layerRegistry`. Previously this iterated a
        // legacy `_clients` list that was never populated, so chat.overlay.push,
        // chat.overlay.clear, SET_TEXT, VISUAL_SET_VISIBLE and VISUAL_SET_PROPERTY all
        // succeeded silently with nothing reaching the browser. Mirrors the per-socket
        // sender pump pattern used by BroadcastToAllLayersAsync / SendToLayerAsync.
        public Task BroadcastRawAsync(string json)
        {
            byte[] buff = Encoding.UTF8.GetBytes(json);
            return FanOutAsync(buff, EnumerateAllLayerSockets(), _cts.Token);
        }

        // ──────────────────────────────────────────────────────────────────
        //  PER-LAYER DISPATCH
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
        /// Broadcasts a JSON payload to every active per-layer WebSocket. Used by
        /// LiveCaptionService to push CAPTION_UPDATE to all connected browsers.
        /// </summary>
        public Task BroadcastToAllLayersAsync(object payload, CancellationToken ct = default)
        {
            // SerializeToUtf8Bytes — same single-pass
            // allocation win as the SendToLayerAsync / BroadcastAsync paths.
            byte[] buff = JsonSerializer.SerializeToUtf8Bytes(payload);
            return FanOutAsync(buff, EnumerateAllLayerSockets(), ct);
        }

        /// <summary>
        /// Yields every WebSocket on every active layer once. Used by both the
        /// broad-fan-out methods (<see cref="BroadcastRawAsync"/> /
        /// <see cref="BroadcastToAllLayersAsync"/>) so the registry traversal
        /// is shared. Snapshots the active layer ids first because
        /// <see cref="LayerRegistry.GetConnections"/> can change concurrently.
        /// </summary>
        private IEnumerable<WebSocket> EnumerateAllLayerSockets()
        {
            foreach (var layerId in _layerRegistry.GetActiveLayerIds())
            {
                foreach (var ws in _layerRegistry.GetConnections(layerId))
                    yield return ws;
            }
        }

        /// <summary>
        /// Shared per-socket send dispatch used by
        /// <see cref="BroadcastRawAsync"/>, <see cref="SendToLayerAsync"/>, and
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
                var sender = _layerSenders.GetOrAdd(ws, s => new PerSocketSender(s, _cts.Token));
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

                switch (type)
                {
                    case "VISUAL_COMPLETE":
                    {
                        if (string.IsNullOrEmpty(layerId)) return;
                        string widgetId    = ReadString(doc.RootElement, "widgetId");
                        string triggerName = ReadString(doc.RootElement, "triggerName");
                        string waitId      = ReadString(doc.RootElement, "waitId");
                        LayerRuntime.Instance.NotifyTriggerComplete(layerId, widgetId, triggerName, waitId);
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
                        GlobalLogger.Log(
                            $"HUDServer: browser TRIGGER_DIAGNOSTIC layer='{layerId}' widget='{widgetId}' trigger='{triggerName}' reason='{reason}' detail='{detail}'",
                            "HUDServer", LogLevel.System);
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

            public long DroppedFrameCount => Interlocked.Read(ref _droppedFrames);

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
