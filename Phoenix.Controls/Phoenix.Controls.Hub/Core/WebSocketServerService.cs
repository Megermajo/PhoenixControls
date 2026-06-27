using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    ///  — external WebSocket listener that fires
    /// <c>on_websocket("name")</c> handler blocks when a client posts a message
    /// to <c>/ws/&lt;name&gt;</c>. Sister surface to <see cref="HUDServer"/>
    /// (overlay browser sources) and <see cref="RemoteBridgeServer"/>
    /// (Viewer-roadmap remote control), but for raw external WS clients —
    /// streamer-authored panels / dashboards / bridges that don't use the
    /// pairing flow.
    ///
    /// Authentication: shared-secret token via <c>?token=</c> query parameter
    /// (QC41-02). The token lives in <see cref="AppConfig.WebSocketServerToken"/>
    /// and is auto-generated on first launch via <see cref="RandomNumberGenerator"/>.
    /// Mismatch → WebSocket close 1008 (PolicyViolation) without ever dispatching
    /// to a script. Loopback bind by default (see
    /// <see cref="AppConfig.WebSocketServerBindHost"/>); LAN bind requires an
    /// explicit <see cref="AppConfig.WebSocketServerLanModeEnabled"/> opt-in flag
    /// so a quick host-edit during a sound check doesn't accidentally expose the
    /// listener.
    ///
    /// Lifecycle: <see cref="StartAsync"/> binds the HttpListener, the accept
    /// loop runs until cancelled. Each WebSocket connection runs a per-socket
    /// receive loop; each text frame fires
    /// <see cref="ScriptManager.ExecuteOnWebSocketScriptsAsync"/> with the
    /// payload as event data — guarded by a
    /// <c>MaxConcurrentWebsocketScripts</c> semaphore so a chatty bridge can't
    /// spawn unlimited engine invocations. Connections that send binary frames
    /// or malformed JSON still trigger the script (raw text in
    /// <c>{event.payload}</c>) so user-friendly contracts can be authored
    /// without a JSON wrapper.
    ///
    /// Decisions made for  (per the WebSocket Server TODO note):
    ///   * Port — <see cref="AppConfig.WebSocketServerPort"/> default 18083.
    ///   * Auth — QC41-02 introduces required token; pre-fix sprint shipped
    ///     "loopback is the auth", which silently exposed script execution to
    ///     anyone who could reach the bind host.
    ///   * Routing — per-path (one <c>on_websocket("name"):</c> block per
    ///     <c>/ws/&lt;name&gt;</c>) mirroring HTTP.WebhookListener's
    ///     <c>on_webhook("name"):</c> shape.
    ///   * Multi-handler dispatch — QC36-12 flipped the contract from
    ///     first-match-wins to all-match fan-out (now consistent with
    ///     on_chat / on_event / on_clipboard). Duplicate names are warned
    ///     about by ScriptRegistry's generalized header-collision detector
    ///     (QC36-08 — DetectDuplicateHeaderNames), so an operator who
    ///     wanted exclusive dispatch sees a Communication-tier log line
    ///     and can rename one of the colliding scripts.
    /// </summary>
    public sealed class WebSocketServerService : IAsyncDisposable, IDisposable
    {
        private readonly ScriptManager _scriptManager;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private string? _activePrefix;

        private readonly ConcurrentDictionary<Guid, WebSocket> _sessions = new();

        // QC41-02 — concurrent-script cap. SemaphoreSlim sized from
        // AppConfig.MaxConcurrentWebsocketScripts at StartAsync so a chatty
        // bridge spamming /ws/<name> can't spawn unlimited engine invocations.
        // 0 in config means "unlimited" — we model that with int.MaxValue so
        // the lock path stays uniform.
        private SemaphoreSlim? _wsScriptSem;

        // QC41-02 / QC36-11 — fallback cap on the cumulative bytes a single
        // message may aggregate across continuation frames. 1MB matches
        // HUDServer's QC35-02 default. Now configurable via
        // AppConfig.WebSocketMaxMessageBytes (read at receive-loop time so a
        // mid-flight config change is picked up by the next message); this
        // constant is the clamp when the config value is missing / non-positive.
        // MUST be tracked in BYTES (not StringBuilder chars) — an emoji-laden
        // frame can slip a char-based cap by ratios up to 4 bytes/char. The
        // receive loop accumulates raw UTF-8 in a MemoryStream and only decodes
        // once EndOfMessage so partial multi-byte sequences can never corrupt
        // the decode.
        private const int DefaultMaxAggregatedFrameBytes = 1024 * 1024;

        public bool IsRunning => _listener is { IsListening: true };
        public string? ActivePrefix => _activePrefix;

        // B44 (audit/winui-regressions-2026-05-24) — live status accessors for
        // the Hub StatusStrip badge. IsListening mirrors IsRunning (kept as a
        // separate property so the badge's semantic ("the listener is bound
        // and accepting upgrades") is decoupled from internal naming);
        // ConnectedClientCount reads the per-session dictionary's Count so the
        // strip can render "WS: 3 / :18083" once per second without paying
        // for an allocation. Both are cheap O(1) reads with no locking — the
        // ConcurrentDictionary's Count is a snapshot read and the listener's
        // IsListening flag is a plain bool field on HttpListener.
        public bool IsListening => _listener is { IsListening: true };
        public int ConnectedClientCount => _sessions.Count;

        public WebSocketServerService(ScriptManager scriptManager)
        {
            _scriptManager = scriptManager ?? throw new ArgumentNullException(nameof(scriptManager));
        }

        public Task StartAsync()
        {
            if (IsRunning) return Task.CompletedTask;

            // QC36-02 — defense-in-depth gate. The primary gate lives in
            // HubBootstrapper.StartOptInServices: when AppConfig.WebSocketServerEnabled
            // is false, the service is never constructed at all (lazier resource use
            // — no HttpListener bind, no accept loop). This check is the belt-and-
            // braces: if a future call site constructs the service and reaches
            // StartAsync with the flag off, we log an error (which signals a
            // bootstrap-side bug) and refuse to bind. Without this, the "Settings
            // checkbox toggled off but server still running" failure mode in QC36-02
            // could re-emerge under a bootstrap regression.
            if (!ConfigManager.Current.WebSocketServerEnabled)
            {
                GlobalLogger.Log(
                    "WebSocketServerService.StartAsync called while AppConfig.WebSocketServerEnabled=false; refusing to bind. "
                    + "This indicates a bootstrap-side wiring bug — the service should not have been constructed.",
                    "WebSocketServer", LogLevel.CriticalError);
                return Task.CompletedTask;
            }

            // QC41-02 / [S38] — ensure a strong shared-secret token exists.
            // Primary mint path runs at ConfigManager.Load (S38) so this is
            // belt-and-braces for tests / hot-reload paths that bypass Load.
            // Idempotent when the token is already valid.
            EnsureWebSocketTokenProvisioned();

            string host = string.IsNullOrWhiteSpace(ConfigManager.Current.WebSocketServerBindHost)
                            ? "127.0.0.1"
                            : ConfigManager.Current.WebSocketServerBindHost;
            int port = ConfigManager.Current.WebSocketServerPort > 0
                            ? ConfigManager.Current.WebSocketServerPort
                            : 18083;

            // QC41-02 — LAN-bind hardening. A non-loopback host requires an
            // explicit AppConfig.WebSocketServerLanModeEnabled opt-in; without
            // it we downgrade to loopback and log a CriticalError so the
            // operator sees why the bind doesn't match config. Empty / short
            // tokens combined with LAN exposure would be a script-execution
            // wide-open footgun, so we refuse the downgrade too if no token
            // ever got provisioned (paranoia — EnsureWebSocketTokenProvisioned
            // should have already filled this in).
            bool isLoopback = IsLoopbackHost(host);
            if (!isLoopback && !ConfigManager.Current.WebSocketServerLanModeEnabled)
            {
                GlobalLogger.Log(
                    $"WebSocketServerService: WebSocketServerBindHost='{host}' is non-loopback but " +
                    "AppConfig.WebSocketServerLanModeEnabled=false. Downgrading bind to 127.0.0.1 — " +
                    "tick the LAN-mode opt-in in Settings to expose the listener on the LAN.",
                    "WebSocketServer", LogLevel.CriticalError);
                host = "127.0.0.1";
            }

            int cap = ConfigManager.Current.MaxConcurrentWebsocketScripts;
            int effectiveCap = cap > 0 ? cap : int.MaxValue;
            _wsScriptSem = new SemaphoreSlim(effectiveCap, effectiveCap);

            string prefix = $"http://{host}:{port}/";

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                GlobalLogger.Log(
                    $"WebSocketServerService: failed to bind '{prefix}': {ex.Message}. " +
                    "Try a different WebSocketServerBindHost / WebSocketServerPort, or run `netsh http add urlacl` for the URL.",
                    "WebSocketServer", LogLevel.CriticalError);
                _listener = null;
                _cts.Dispose();
                _cts = null;
                try { _wsScriptSem.Dispose(); } catch { }
                _wsScriptSem = null;
                return Task.CompletedTask;
            }
            _activePrefix = prefix;
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            GlobalLogger.Log(
                $"WebSocketServerService listening on {prefix} (concurrent-script cap={cap}).",
                "WebSocketServer", LogLevel.System);
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_cts != null) try { _cts.Cancel(); } catch { }
            // Politely close active sockets so client-side close handshakes complete.
            foreach (var kv in _sessions)
            {
                try { await kv.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "server stopping", CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            _sessions.Clear();
            if (_listener != null)
            {
                try { _listener.Stop();  } catch { }
                try { _listener.Close(); } catch { }
                _listener = null;
            }
            if (_acceptTask != null)
            {
                try { await _acceptTask.ConfigureAwait(false); } catch { }
                _acceptTask = null;
            }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            try { _wsScriptSem?.Dispose(); } catch { }
            _wsScriptSem = null;
            _activePrefix = null;
        }

        /// <summary>
        /// QC41-04 — DisposeAsync is the supported teardown for WinUI / STA
        /// callers. <see cref="StopAsync"/> awaits the accept loop and a
        /// per-socket close handshake drain; calling
        /// <c>.GetAwaiter().GetResult()</c> on the UI thread schedules
        /// continuations on the captured SynchronizationContext and deadlocks
        /// the dispatch loop. Mirrors <see cref="RemoteBridgeServer.DisposeAsync"/>
        /// and the Bus.Stop pattern from commit 6b713988.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch { /* shutdown is best-effort */ }
        }

        /// <summary>
        /// QC41-04 — synchronous <see cref="IDisposable.Dispose"/> compatibility
        /// shim. The pre-fix implementation was
        /// <c>StopAsync().GetAwaiter().GetResult()</c>, a WinUI deadlock surface
        /// whenever the call originated on the UI thread (Hub shutdown via the
        /// pillar-launcher path does exactly that). Mirrors Bus.Stop (commit
        /// 6b713988) by hopping to a worker thread before blocking so the
        /// caller's SyncContext can keep pumping while StopAsync drains.
        /// Production callers should prefer <see cref="DisposeAsync"/>.
        /// </summary>
        public void Dispose() => Task.Run(StopAsync).GetAwaiter().GetResult();

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener is { IsListening: true })
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    GlobalLogger.Error("WebSocketServer", "accept loop exception", ex);
                    continue;
                }

                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => HandleContextAsync(ctx, ct),
                    "WebSocketServer", "request dispatch");
            }
        }

        private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            // Only WS upgrades on /ws/<name> paths. Everything else 404s.
            if (!ctx.Request.IsWebSocketRequest || !path.StartsWith("/ws/", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            string name = path.Substring("/ws/".Length).Trim('/');
            if (string.IsNullOrEmpty(name) || name.Contains('/'))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            // QC41-02 — shared-secret token gate. Reject BEFORE the WebSocket
            // upgrade so a probe doesn't even see the WS handshake succeed;
            // they get a plain 401 and we never spend ScriptManager cycles on
            // them. CryptographicEquals (FixedTimeEquals on byte arrays) keeps
            // the comparison resistant to timing-side-channel leaks.
            string? supplied = ctx.Request.QueryString["token"];
            string expected  = ConfigManager.Current.WebSocketServerToken ?? "";
            if (!IsTokenAcceptable(supplied, expected))
            {
                GlobalLogger.Log(
                    $"WebSocketServer: rejected /ws/{name} upgrade from " +
                    $"{ctx.Request.RemoteEndPoint?.Address} — missing or mismatched token.",
                    "WebSocketServer", LogLevel.CriticalError);
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
                return;
            }

            HttpListenerWebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("WebSocketServer", "WS accept failed", ex);
                // [P1 swarm-audit 2026-05-29] The 401/400/404 paths above all Close()
                // the response; this AcceptWebSocketAsync-failure path left it open,
                // leaking the HttpListenerContext's response stream/connection.
                try { ctx.Response.Close(); } catch { }
                return;
            }

            var sessionId = Guid.NewGuid();
            var socket    = wsCtx.WebSocket;
            _sessions[sessionId] = socket;

            var buffer = new byte[16 * 1024];
            // QC36-11 — pull the byte cap from AppConfig at session start so an
            // operator who tunes WebSocketMaxMessageBytes via Settings doesn't
            // have to reconnect every WS client to pick it up on NEW sessions.
            // We snapshot once per connection rather than per-fragment so a
            // mid-message config flip can't make the cap inconsistent within a
            // single aggregated frame. Non-positive config values fall back to
            // the 1MB default.
            int cfgCap = ConfigManager.Current?.WebSocketMaxMessageBytes ?? DefaultMaxAggregatedFrameBytes;
            int maxBytes = cfgCap > 0 ? cfgCap : DefaultMaxAggregatedFrameBytes;
            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    // QC41-02 — accumulate raw UTF-8 bytes in a MemoryStream
                    // rather than decoding fragments into a StringBuilder. Two
                    // wins: (1) the size cap is honest (byte count, not UTF-16
                    // char count which lies by up to 4x on emoji-heavy
                    // payloads), and (2) partial multi-byte UTF-8 sequences
                    // can never straddle a fragment boundary and corrupt the
                    // decode — we only call GetString once EndOfMessage is
                    // observed.
                    using var aggBuf = new MemoryStream();
                    WebSocketReceiveResult result;
                    bool oversized = false;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (aggBuf.Length + result.Count > maxBytes)
                        {
                            oversized = true;
                            break;
                        }
                        aggBuf.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (oversized)
                    {
                        // QC36-11 — log at Communication tier (not CriticalError) so the
                        // operator sees abused/buggy clients without it pinging the
                        // critical-alert UI. The original implementation logged at
                        // CriticalError; this matches the WS-Server tier for client
                        // misbehaviour vs server faults.
                        GlobalLogger.Log(
                            $"WebSocketServer: aborting /ws/{name} — aggregated frame exceeded {maxBytes} bytes (close 1009 MessageTooBig)",
                            "WebSocketServer", LogLevel.Communication);
                        try
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig,
                                "frame too large", CancellationToken.None).ConfigureAwait(false);
                        }
                        catch { /* best-effort */ }
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    string raw = Encoding.UTF8.GetString(aggBuf.GetBuffer(), 0, (int)aggBuf.Length);

                    // Build the JsonElement payload that ExecuteOnWebSocketScriptsAsync
                    // hands to the engine. If the message parses cleanly we pass the
                    // parsed shape (so {event.<field>} tokens resolve like webhook
                    // payloads); otherwise we wrap the raw text in {"payload":...}
                    // so {event.payload} still works.
                    JsonElement payload;
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        payload = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { payload = raw }));
                        payload = doc.RootElement.Clone();
                    }

                    // QC41-02 — gate script dispatch on the concurrent-script
                    // semaphore. WaitAsync (not TryEnter) so a brief burst
                    // queues rather than dropping invocations; the per-script
                    // timeout in ScriptManager bounds the worst-case stall.
                    var sem = _wsScriptSem;
                    if (sem is null) continue;
                    try
                    {
                        await sem.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { break; }
                    try
                    {
                        await _scriptManager.ExecuteOnWebSocketScriptsAsync(name, payload).ConfigureAwait(false);
                    }
                    finally
                    {
                        try { sem.Release(); } catch (ObjectDisposedException) { /* shutdown raced */ }
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                GlobalLogger.Error("WebSocketServer", $"receive loop error on /ws/{name}", ex);
            }
            finally
            {
                _sessions.TryRemove(sessionId, out _);
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closing", CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                socket.Dispose();
            }
        }

        // ── QC41-02 token / host helpers ──────────────────────────────────

        /// <summary>
        /// QC41-02 / [S38] — belt-and-braces token provisioning. The primary
        /// mint path now lives in <see cref="ConfigManager.Load"/> (S38) so a
        /// fresh-install Hub always has a credible token in
        /// <c>config.json</c> before the WebSocketServer ever binds. We still
        /// invoke <see cref="ConfigManager.EnsureWebSocketServerToken"/> here
        /// for the corner case where the config was loaded by something
        /// other than <see cref="ConfigManager.Load"/> (tests, future
        /// hot-reload paths) — it's idempotent when the token is already
        /// valid.
        /// </summary>
        private static void EnsureWebSocketTokenProvisioned()
        {
            try
            {
                if (ConfigManager.EnsureWebSocketServerToken())
                {
                    ConfigManager.Save(Phoenix.Controls.Shared.Core.Paths.AppConfigJson);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WebSocketServer", "failed to persist generated token", ex);
            }
        }

        private static bool IsTokenAcceptable(string? supplied, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return false;
            if (string.IsNullOrEmpty(supplied)) return false;
            // FixedTimeEquals to dodge a length-leak side-channel; pad the
            // shorter side to the longer side's length with a sentinel byte
            // so equal-length comparison is always exercised.
            byte[] a = Encoding.UTF8.GetBytes(supplied);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            if (a.Length != b.Length) return false;
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static bool IsLoopbackHost(string host) =>
            string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.Ordinal);
    }
}
