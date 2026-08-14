using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.ViewerServer.Internal;

namespace Phoenix.Controls.ViewerServer;

/// <summary>
/// HTTP + WebSocket server hosted by Hub on port 18090 (default), serving
/// the Phoenix Controls remote viewer per the v2 design package. Pure
/// read-only: emits the <see cref="ViewerSnapshot"/> bootstrap and a live
/// <see cref="ViewerEvent"/> push channel; rejects every command surface
/// with HTTP 405.
///
/// Routes:
/// <list type="bullet">
///   <item><c>GET  /v/{channel}</c> — static HTML shell (web bundle).</item>
///   <item><c>GET  /v/{channel}/assets/*</c> — static asset.</item>
///   <item><c>POST /v/api/pair/begin</c> — mints a pairing handle.</item>
///   <item><c>POST /v/api/pair/complete</c> — claims the PIN, returns bearer token.</item>
///   <item><c>GET  /v/api/snapshot</c> (auth) — bootstrap payload.</item>
///   <item><c>WS   /v/ws</c> (auth via <c>Sec-WebSocket-Protocol: bearer.&lt;token&gt;</c>) — push channel.</item>
/// </list>
/// </summary>
public sealed class ViewerServer : IAsyncDisposable
{
    private readonly ViewerServerOptions _options;
    private readonly IHubReadModel _hub;
    private readonly PinManager _pin;
    private readonly PairedDeviceStore _devices;
    private readonly PairingRateLimiter _pairLimiter = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private IDisposable? _liveSubscription;

    // The IHubReadModel.Subscribe handler is invoked on whatever
    // thread the source publishes on (typically the single Hub UI/dispatcher
    // thread) and is contractually required to be "cheap and non-blocking".
    // Serialising the event + fanning out inline blocked that thread, freezing
    // the whole suite on busy event streams. OnHubEvent now only enqueues onto
    // this unbounded channel; the EventPumpAsync loop does the (heavier)
    // serialise-and-send work off the publishing thread.
    private Channel<ViewerEvent>? _eventQueue;
    private Task? _eventPump;

    private readonly ConcurrentDictionary<Guid, WebSocketSession> _sessions = new();
    // Track every outstanding HTTP / WS handler task so
    // StopAsync can await drain before returning. Without this, callers
    // like Hub-shutdown would race the handlers and tear down DI / file
    // handles while requests were still serializing.
    private readonly ConcurrentDictionary<Guid, Task> _handlers = new();
    private int _started;

    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(5);

    public ViewerServer(ViewerServerOptions options, IHubReadModel hub)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _pin = new PinManager(options.PinTtl);

        string devicesPath = options.PairedDevicesPath
            ?? Path.Combine(Paths.LocalAppData("Hub"), "viewer_devices.json");
        _devices = new PairedDeviceStore(devicesPath);
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _listener = new HttpListener();
        foreach (var prefix in BuildPrefixes()) _listener.Prefixes.Add(prefix);

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // LAN mode requires a urlacl reservation or admin. Surface a
            // clear log line rather than a stack-trace explosion.
            GlobalLogger.Log(
                $"ViewerServer failed to bind ({ex.Message}). LAN mode? You may need: " +
                $"netsh http add urlacl url=http://+:{_options.Port}/ user=Everyone",
                "ViewerServer", LogLevel.CriticalError);
            throw;
        }

        GlobalLogger.Log(
            $"ViewerServer listening on {_listener.Prefixes.First()} (channel='{_options.Channel}', LAN={_options.LanModeEnabled})",
            "ViewerServer", LogLevel.System);

        // Start the background serialise/fanout pump before
        // subscribing so no enqueued event is dropped on a race.
        _eventQueue = Channel.CreateUnbounded<ViewerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _eventPump = Task.Run(() => EventPumpAsync(_cts.Token));

        _liveSubscription = _hub.Subscribe(OnHubEvent);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_started == 0) return;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _liveSubscription?.Dispose(); } catch { }

        // Signal the event pump to finish (no more events arrive once
        // the subscription is disposed) and await its exit so serialisation
        // doesn't outlive shutdown.
        try { _eventQueue?.Writer.TryComplete(); } catch { }
        if (_eventPump != null)
        {
            try { await _eventPump.ConfigureAwait(false); } catch { }
        }

        // Close all WS sessions so the accept loop's inner task drains.
        foreach (var session in _sessions.Values)
        {
            try { await session.CloseAsync().ConfigureAwait(false); } catch { }
        }
        _sessions.Clear();

        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }

        // Drain outstanding HTTP / WS handlers (bounded) so a
        // caller calling DisposeAsync immediately after StopAsync
        // doesn't tear down resources while in-flight requests are
        // still serializing. Bounded by StopDrainTimeout so a wedged
        // handler can't block shutdown indefinitely. The caller's CT
        // can shorten the wait further when shutdown urgency demands it.
        var pending = _handlers.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                var drain = Task.WhenAll(pending);
                var timeout = Task.Delay(StopDrainTimeout, ct);
                var winner = await Task.WhenAny(drain, timeout).ConfigureAwait(false);
                if (winner == timeout)
                {
                    GlobalLogger.Log(
                        $"ViewerServer StopAsync drain timed out with {_handlers.Count} handler(s) still in flight.",
                        "ViewerServer", LogLevel.System);
                }
            }
            catch (OperationCanceledException) { /* caller cancelled; bail */ }
            catch { /* individual handler faults are not interesting at shutdown */ }
        }

        try { _listener?.Close(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    private IEnumerable<string> BuildPrefixes()
    {
        // HttpListener prefixes: http://host:port/ (the server is http-only).
        // Honour BindAddress instead of always defaulting to loopback
        // regardless of caller intent:
        //   • LAN off → loopback only (127.0.0.1 + localhost), no matter
        //     what BindAddress says. Defence-in-depth so a stale option
        //     can't accidentally expose the surface.
        //   • LAN on, BindAddress = IPAddress.Any (or IPv6Any) → wildcard
        //     "+" prefix so the LAN can reach it (needs urlacl/admin).
        //   • LAN on, BindAddress = specific NIC address → bind exactly
        //     that interface; still keep loopback so the viewer page stays
        //     reachable from the streamer's own machine.
        const string scheme = "http";

        if (!_options.LanModeEnabled)
        {
            yield return $"{scheme}://127.0.0.1:{_options.Port}/";
            yield return $"{scheme}://localhost:{_options.Port}/";
            yield break;
        }

        if (Equals(_options.BindAddress, IPAddress.Any) ||
            Equals(_options.BindAddress, IPAddress.IPv6Any))
        {
            yield return $"{scheme}://+:{_options.Port}/";
        }
        else
        {
            // Specific interface: HttpListener prefixes use the literal
            // host. Keep loopback alongside so local access keeps working
            // when LAN mode also points at a specific NIC.
            yield return $"{scheme}://{_options.BindAddress}:{_options.Port}/";
            yield return $"{scheme}://127.0.0.1:{_options.Port}/";
            yield return $"{scheme}://localhost:{_options.Port}/";
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (_listener!.IsListening && !ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (!_listener.IsListening) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                GlobalLogger.Error("ViewerServer", "accept error", ex);
                continue;
            }

            // Register the handler task so StopAsync can
            // drain it. The Guid key lets the handler self-deregister
            // on completion without us needing to chase task identity.
            var handlerId = Guid.NewGuid();
            var handlerTask = Task.Run(async () =>
            {
                try { await HandleAsync(context, ct).ConfigureAwait(false); }
                finally { _handlers.TryRemove(handlerId, out _); }
            }, ct);
            _handlers[handlerId] = handlerTask;
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            if (context.Request.IsWebSocketRequest && IsWsRoute(path))
            {
                await HandleWebSocketAsync(context, ct).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/v/api/pair/begin")
            {
                await HandlePairBeginAsync(context).ConfigureAwait(false);
                return;
            }
            if (method == "POST" && path == "/v/api/pair/complete")
            {
                await HandlePairCompleteAsync(context).ConfigureAwait(false);
                return;
            }
            if (method == "GET" && path == "/v/api/snapshot")
            {
                // LAN-reachable: paired-device flow needs this from non-loopback origins. PIN polling stays loopback-only.
                await HandleSnapshotAsync(context, ct).ConfigureAwait(false);
                return;
            }
            // Static: /v/{channel} → index.html ; /v/{channel}/assets/* → bundle file
            if (method == "GET" && TryMatchStaticRoute(path, out string staticRel))
            {
                await StaticFileResponder.ServeAsync(context.Response, _options.AssetsRoot, staticRel).ConfigureAwait(false);
                return;
            }

            // Every command-shaped URL beyond the pure-read surface gets
            // 405 — the viewer is read-only.
            if (path.StartsWith("/v/api/", StringComparison.Ordinal))
            {
                await StaticFileResponder.WriteStatusAsync(context.Response, HttpStatusCode.MethodNotAllowed,
                    "viewer is read-only").ConfigureAwait(false);
                return;
            }

            await StaticFileResponder.WriteStatusAsync(context.Response, HttpStatusCode.NotFound, "not found").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ViewerServer", $"handler {context.Request.Url?.AbsolutePath ?? "<unknown>"}", ex);
            try { context.Response.StatusCode = (int)HttpStatusCode.InternalServerError; } catch { }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static bool IsWsRoute(string path) =>
        string.Equals(path, "/v/ws", StringComparison.OrdinalIgnoreCase);

    private bool TryMatchStaticRoute(string path, out string relative)
    {
        relative = "";
        // "/v/{channel}" → index.html (the SPA shell loads pair vs connected client-side).
        // "/v/{channel}/assets/foo" → "assets/foo"
        // "/v/{channel}/styles/foo" → "styles/foo"
        // "/v/{channel}/scripts/foo" → "scripts/foo"
        if (!path.StartsWith("/v/", StringComparison.Ordinal)) return false;
        string trimmed = path.Substring(3);
        int slash = trimmed.IndexOf('/');
        string channel = slash < 0 ? trimmed : trimmed.Substring(0, slash);
        if (string.IsNullOrEmpty(channel)) return false;

        if (slash < 0)
        {
            relative = "index.html";
            return true;
        }

        string tail = trimmed.Substring(slash + 1);
        if (tail.StartsWith("assets/", StringComparison.Ordinal) ||
            tail.StartsWith("styles/", StringComparison.Ordinal) ||
            tail.StartsWith("scripts/", StringComparison.Ordinal))
        {
            relative = tail;
            return true;
        }
        return false;
    }

    // ── pairing ────────────────────────────────────────────────────────
    private async Task HandlePairBeginAsync(HttpListenerContext ctx)
    {
        var ip = ctx.Request.RemoteEndPoint?.Address;
        if (!_pairLimiter.TryRegisterBegin(ip))
        {
            // Brute-force defense: per-IP cap on pair/begin (5/min) plus a
            // shared lockout state with pair/complete failures. Loopback
            // callers (Hub UI tests) are typically inside the cap; LAN
            // attackers spamming pair/begin to mint handles hit this.
            GlobalLogger.Log(
                $"ViewerServer: /pair/begin rate-limited for {ip?.ToString() ?? "<unknown>"}",
                "ViewerServer", LogLevel.Communication);
            ctx.Response.Headers["Retry-After"] = "60";
            await StaticFileResponder.WriteStatusAsync(ctx.Response,
                (HttpStatusCode)429, "too many pairing requests").ConfigureAwait(false);
            return;
        }

        Dictionary<string, string> body;
        try { body = await ReadJsonAsync(ctx.Request).ConfigureAwait(false); }
        catch (PayloadTooLargeException ex)
        {
            GlobalLogger.Log(
                $"ViewerServer: /pair/begin body rejected for {ip?.ToString() ?? "<unknown>"} — {ex.Message}",
                "ViewerServer", LogLevel.Communication);
            await StaticFileResponder.WriteStatusAsync(ctx.Response,
                HttpStatusCode.RequestEntityTooLarge, "body too large").ConfigureAwait(false);
            return;
        }
        catch
        {
            await StaticFileResponder.WriteStatusAsync(ctx.Response, HttpStatusCode.BadRequest,
                "invalid body").ConfigureAwait(false);
            return;
        }

        string label = body.TryGetValue("deviceLabel", out var l) ? l : "Unnamed device";
        string pairingId;
        DateTimeOffset expiresAt;
        try
        {
            (pairingId, expiresAt) = _pin.BeginPairing(label);
        }
        catch (InvalidOperationException)
        {
            // Pending store full — second memory bound after the per-IP cap.
            ctx.Response.Headers["Retry-After"] = "60";
            await StaticFileResponder.WriteStatusAsync(ctx.Response,
                (HttpStatusCode)429, "pairing handle store at capacity").ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(ctx.Response, new
        {
            pairingId,
            expiresAt = expiresAt.ToUnixTimeSeconds(),
        }).ConfigureAwait(false);
    }

    private async Task HandlePairCompleteAsync(HttpListenerContext ctx)
    {
        var ip = ctx.Request.RemoteEndPoint?.Address;
        if (!_pairLimiter.TryRegisterCompleteAttempt(ip))
        {
            GlobalLogger.Log(
                $"ViewerServer: /pair/complete locked out for {ip?.ToString() ?? "<unknown>"}",
                "ViewerServer", LogLevel.Communication);
            ctx.Response.Headers["Retry-After"] = "300";
            await StaticFileResponder.WriteStatusAsync(ctx.Response,
                (HttpStatusCode)429, "too many failed pairing attempts").ConfigureAwait(false);
            return;
        }

        Dictionary<string, string> body;
        try { body = await ReadJsonAsync(ctx.Request).ConfigureAwait(false); }
        catch (PayloadTooLargeException ex)
        {
            // An over-cap body on the unauthenticated /pair/complete route is
            // a failed pairing attempt like any other: count it so a flood
            // walks the IP into the same lockout a wrong-PIN flood does.
            // (Malformed-but-small bodies already reach TryClaim, fail, and
            // get counted there, so this keeps the two paths consistent.)
            _pairLimiter.NoteCompleteFailure(ip);
            GlobalLogger.Log(
                $"ViewerServer: /pair/complete body rejected for {ip?.ToString() ?? "<unknown>"} — {ex.Message}",
                "ViewerServer", LogLevel.Communication);
            await StaticFileResponder.WriteStatusAsync(ctx.Response,
                HttpStatusCode.RequestEntityTooLarge, "body too large").ConfigureAwait(false);
            return;
        }

        string pairingId   = body.GetValueOrDefault("pairingId", "");
        string code        = body.GetValueOrDefault("code", "");
        string devicePubId = body.GetValueOrDefault("devicePubId", "");

        if (!_pin.TryClaim(pairingId, code, devicePubId, out string label))
        {
            // Record the failure: enough of these inside the sliding window
            // triggers a per-IP lockout (see PairingRateLimiter.LockoutDuration).
            _pairLimiter.NoteCompleteFailure(ip);
            await StaticFileResponder.WriteStatusAsync(ctx.Response, HttpStatusCode.Unauthorized,
                "invalid or expired pairing").ConfigureAwait(false);
            return;
        }

        _pairLimiter.ClearOnSuccess(ip);

        var device = _devices.Issue(label, out string token);
        GlobalLogger.Log($"ViewerServer paired device '{device.Label}' ({device.DeviceId})", "ViewerServer", LogLevel.System);

        await WriteJsonAsync(ctx.Response, new
        {
            deviceId = device.DeviceId,
            token,
            channel  = _options.Channel,
        }).ConfigureAwait(false);
    }

    // ── snapshot ───────────────────────────────────────────────────────
    private async Task HandleSnapshotAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var device = AuthenticateBearer(ctx.Request);
        if (device == null)
        {
            await StaticFileResponder.WriteStatusAsync(ctx.Response, HttpStatusCode.Unauthorized,
                "missing or invalid token").ConfigureAwait(false);
            return;
        }

        var snapshot = await _hub.GetSnapshotAsync(ct).ConfigureAwait(false);
        await WriteJsonAsync(ctx.Response, snapshot).ConfigureAwait(false);
    }

    // ── websocket ──────────────────────────────────────────────────────
    private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        // Auth through the WebSocket subprotocol header: bearer.<token>.
        // Browsers can't set arbitrary Authorization headers on WS, so the
        // subprotocol slot is the canonical place per the original
        // Viewer_Roadmap.md design.
        string? selectedProtocol = null;
        string? token = null;
        var protocols = ctx.Request.Headers.GetValues("Sec-WebSocket-Protocol") ?? Array.Empty<string>();
        foreach (var raw in protocols)
        {
            foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (entry.StartsWith("bearer.", StringComparison.Ordinal))
                {
                    token = entry.Substring("bearer.".Length);
                    selectedProtocol = entry;
                    break;
                }
            }
            if (token != null) break;
        }

        if (string.IsNullOrEmpty(token))
        {
            await StaticFileResponder.WriteStatusAsync(ctx.Response, HttpStatusCode.Unauthorized,
                "missing bearer subprotocol").ConfigureAwait(false);
            return;
        }
        var device = _devices.Verify(token);
        if (device == null)
        {
            await StaticFileResponder.WriteStatusAsync(ctx.Response, HttpStatusCode.Unauthorized,
                "invalid token").ConfigureAwait(false);
            return;
        }

        HttpListenerWebSocketContext wsCtx;
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(selectedProtocol).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ViewerServer", "WS handshake failed", ex);
            return;
        }

        var session = new WebSocketSession(wsCtx.WebSocket, device, ct);
        _sessions[session.Id] = session;
        GlobalLogger.Log($"ViewerServer WS open: device='{device.Label}' ({device.DeviceId})", "ViewerServer", LogLevel.System);

        try
        {
            await PumpAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            try { session.WebSocket.Dispose(); } catch { }
            // dispose the session itself so its send pump
            // (channel + drain task) shuts down instead of leaking per
            // session teardown.
            try { session.Dispose(); } catch { }
        }
    }

    // Cap inbound WS message size at 4 KB total
    // (cumulative across continuation frames). An authenticated peer that
    // streamed unbounded frames could otherwise DoS-flood the pump. The
    // viewer is read-only, so legitimate clients send only tiny control
    // frames — anything larger is malformed or malicious.
    private const int MaxInboundMessageBytes = 4 * 1024;

    private async Task PumpAsync(WebSocketSession session, CancellationToken ct)
    {
        // Viewer is read-only: the single client → server message is the
        // "PING" latency probe, answered with a VIEWER_PONG echo so the
        // browser can measure round-trip time on its own clock. Every
        // other inbound frame is drained purely to keep the socket healthy
        // and to notice when the peer goes away.
        var buf = new byte[4 * 1024];
        var message = new MemoryStream();
        int cumulative = 0;
        while (session.WebSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var result = await session.WebSocket.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await session.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
                    return;
                }

                // Track cumulative bytes across continuation frames so a
                // fragmented message can't sneak past a per-frame check.
                cumulative += result.Count;
                if (cumulative > MaxInboundMessageBytes)
                {
                    GlobalLogger.Log(
                        $"ViewerServer WS frame too large ({cumulative}B > {MaxInboundMessageBytes}B cap); closing.",
                        "ViewerServer",
                        LogLevel.System);
                    await session.WebSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large", CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // Buffer text frames (bounded by the cap above) so a PING
                // split across continuation frames still parses. Binary has
                // no defined meaning on this wire and is dropped unread.
                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    message.Write(buf, 0, result.Count);

                if (result.EndOfMessage)
                {
                    // Reset cumulative tally when the peer finishes a message.
                    cumulative = 0;
                    if (message.Length > 0)
                    {
                        TryAnswerPing(session, message);
                        message.SetLength(0);
                    }
                }
            }
            catch (WebSocketException) { return; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                GlobalLogger.Error("ViewerServer", "WS pump", ex);
                return;
            }
        }
    }

    // The one message a client may send: {"type":"PING","t":<Date.now()>}.
    // Reply with a VIEWER_PONG whose payload is the client's own timestamp
    // echoed verbatim — the browser computes RTT as Date.now() - payload
    // entirely on its local clock, so server/client clock skew never enters
    // the measurement. Anything that is not a well-formed PING (bad JSON
    // included) is silently discarded: the viewer surface stays read-only
    // and gains no command channel here.
    private static void TryAnswerPing(WebSocketSession session, MemoryStream message)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                new ReadOnlyMemory<byte>(message.GetBuffer(), 0, (int)message.Length));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "PING", StringComparison.Ordinal))
                return;
            if (!doc.RootElement.TryGetProperty("t", out var t)) return;

            var envelope = new BusMessage
            {
                Type    = "VIEWER_PONG",
                Source  = "Hub",
                Target  = "*",
                Payload = t.GetRawText(),
                SentAt  = DateTime.UtcNow,
            };
            // Ride the session's bounded send channel like every other
            // frame — the pong shares the fanout path's backpressure rules.
            session.Send(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOpts)));
        }
        catch (JsonException) { /* not JSON — drop, same as any non-PING */ }
    }

    // ── live event fanout ─────────────────────────────────────────────
    // Subscribe handler: must be cheap and non-blocking (it runs on
    // the publisher's thread — typically the single Hub UI/dispatcher thread).
    // Only enqueue here; the heavier serialise + fanout work happens in
    // EventPumpAsync on a background thread.
    private void OnHubEvent(ViewerEvent evt)
    {
        if (_sessions.IsEmpty) return;
        _eventQueue?.Writer.TryWrite(evt);
    }

    // Background pump: drains queued ViewerEvents and performs the
    // serialise + fanout off the publishing thread so a busy event stream
    // can't freeze the Hub UI thread.
    private async Task EventPumpAsync(CancellationToken ct)
    {
        var reader = _eventQueue!.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    if (_sessions.IsEmpty) continue;

                    // Wrap in the existing BusMessage envelope so consumers
                    // (browser-side compositor or Architect inspector) can reuse
                    // the type discriminator pattern they already understand.
                    var envelope = new BusMessage
                    {
                        Type    = evt.Kind,
                        Source  = "Hub",
                        Target  = "*",
                        Payload = JsonSerializer.Serialize<object>(evt, JsonOpts),
                        SentAt  = DateTime.UtcNow,
                    };
                    byte[] frame = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOpts));

                    // Enumerate the dictionary directly: ConcurrentDictionary's
                    // enumerator is safe under concurrent add/remove, while both
                    // .Values and .ToArray() take a full locked snapshot copy —
                    // an avoidable allocation per broadcast on the event pump.
                    foreach (var kv in _sessions)
                    {
                        kv.Value.Send(frame);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            GlobalLogger.Error("ViewerServer", "event pump", ex);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────
    private PairedDevice? AuthenticateBearer(HttpListenerRequest req)
    {
        string? auth = req.Headers["Authorization"];
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        string token = auth.Substring("Bearer ".Length).Trim();
        return _devices.Verify(token);
    }

    // Hard cap on the request body of the two UNAUTHENTICATED pairing routes.
    // Their payloads are a handful of short JSON fields (deviceLabel /
    // pairingId / code / devicePubId) — well under 1 KB — so 8 KB is generous
    // headroom. Before this cap, ReadJsonAsync did an uncapped
    // StreamReader.ReadToEndAsync into a single string, so any LAN peer could
    // POST a multi-hundred-MB body to /v/api/pair/complete (read BEFORE the
    // PIN check) and force a LOH allocation — or an OutOfMemoryException —
    // inside the Hub process that drives Streamer.bot and the OBS overlay.
    // Mirrors the inbound-WS MaxInboundMessageBytes cap above.
    internal const int MaxPairingBodyBytes = 8 * 1024;

    /// <summary>
    /// Thrown by <see cref="ReadJsonAsync"/> when the request body exceeds
    /// <see cref="MaxPairingBodyBytes"/>. Callers answer 413 rather than
    /// letting the generic handler catch turn it into a 500.
    /// </summary>
    private sealed class PayloadTooLargeException : Exception
    {
        public PayloadTooLargeException(string message) : base(message) { }
    }

    private static async Task<Dictionary<string, string>> ReadJsonAsync(HttpListenerRequest req)
    {
        // ContentLength64 is -1 for a chunked request, which declares no
        // length at all; as before, such requests are treated as body-less
        // rather than read (no legitimate pairing client sends chunked).
        if (req.ContentLength64 <= 0) return new();

        // Cheap reject on the declared length: a hostile peer announcing a
        // multi-hundred-MB body never gets a single byte buffered.
        if (req.ContentLength64 > MaxPairingBodyBytes)
            throw new PayloadTooLargeException(
                $"declared body of {req.ContentLength64} bytes exceeds the {MaxPairingBodyBytes}-byte pairing cap");

        string text = await ReadCappedBodyAsync(req).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                    dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.GetRawText();
            }
            return dict;
        }
        catch { return new(); }
    }

    /// <summary>
    /// Reads the request body into a string, never buffering more than
    /// <see cref="MaxPairingBodyBytes"/> + 1 bytes. The declared-length check
    /// in <see cref="ReadJsonAsync"/> is the cheap first gate; this loop is
    /// what actually bounds memory, so a Content-Length that under-declares
    /// what the peer really sends still cannot escape the cap. Bytes are
    /// counted on the wire — before any decode allocation — and the one extra
    /// byte is how an over-cap body is detected without buffering it.
    /// </summary>
    private static async Task<string> ReadCappedBodyAsync(HttpListenerRequest req)
    {
        // Dispose the input stream on the way out, matching the StreamReader
        // ownership this replaced.
        using var input = req.InputStream;

        byte[] buffer = new byte[MaxPairingBodyBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await input
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total))
                .ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
        }

        if (total > MaxPairingBodyBytes)
            throw new PayloadTooLargeException(
                $"body exceeds the {MaxPairingBodyBytes}-byte pairing cap");

        return (req.ContentEncoding ?? Encoding.UTF8).GetString(buffer, 0, total);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, object body)
    {
        resp.StatusCode = (int)HttpStatusCode.OK;
        resp.ContentType = "application/json; charset=utf-8";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // IDisposable so the per-session send pump (bounded channel + drain
    // task) shuts down when a session is removed (HandleWebSocketAsync
    // finally / server StopAsync).
    private sealed class WebSocketSession : IDisposable
    {
        // A stalled viewer (locked phone, suspended tab, half-open Wi-Fi)
        // stops reading; its TCP send buffer fills and WebSocket.SendAsync
        // never completes. Serialising sends behind a semaphore parked one
        // Task + pinned byte[] per event for the life of the stall —
        // unbounded growth. A bounded DropOldest channel drained by one
        // pump caps memory instead: producers return immediately and stale
        // frames drop (the viewer feed is latest-wins), mirroring
        // HUDServer.PerSocketSender.
        private const int MaxBufferedFrames  = 256;
        private const int DropReportInterval = 100;
        // The viewer stream is append-style (chat/feed/log entries), so an
        // evicted frame is a permanent gap for this connection. A session
        // that accumulates this many drops without ever draining to empty is
        // pathologically behind: abort it so the client reconnects and
        // re-bootstraps from a fresh ViewerSnapshot, healing every gap
        // (mirrors RemoteBridgeServer's abort-on-slowness precedent).
        private const int MaxDropsBeforeAbort = 256;

        private readonly Channel<byte[]> _sendQueue;
        private readonly CancellationTokenSource _cts;
        private readonly Task _sendPump;
        private long _droppedFrames;
        private long _lastReportedDrops;
        private int _abortedForBacklog;
        private int _disposed;

        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket WebSocket { get; }
        public PairedDevice Device { get; }
        public string DeviceId => Device.DeviceId;

        public WebSocketSession(WebSocket ws, PairedDevice device, CancellationToken serverCt)
        {
            WebSocket = ws;
            Device = device;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxBufferedFrames)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _sendPump = Task.Run(() => SendPumpAsync(_cts.Token));
        }

        // Non-blocking enqueue. A full queue evicts its oldest frame
        // (DropOldest) rather than stalling the fanout loop; TryWrite only
        // fails once the writer is completed (post-Dispose), at which point
        // the frame has nowhere to land anyway.
        public void Send(byte[] frame)
        {
            if (!_sendQueue.Writer.TryWrite(frame)) return;
            // Depth == cap after a write means either an exact fill or an
            // eviction — indistinguishable from a depth check alone, but
            // sustained over-cap pressure shows as repeated max-depth
            // observations, which is good enough for a telemetry counter.
            if (_sendQueue.Reader.Count >= MaxBufferedFrames)
            {
                long total = Interlocked.Increment(ref _droppedFrames);
                ReportDropIfNeeded(total);
                if (total >= MaxDropsBeforeAbort)
                    AbortForBacklog(total);
            }
        }

        // One-shot per session. Abort (not CloseAsync) because the peer's TCP
        // send buffer is already full — a graceful close handshake could stall
        // the same way the data frames did. Abort faults the pending
        // ReceiveAsync in the server's receive pump, whose handler finally
        // runs the existing removal/dispose cleanup.
        private void AbortForBacklog(long totalDrops)
        {
            if (Interlocked.CompareExchange(ref _abortedForBacklog, 1, 0) != 0) return;
            GlobalLogger.Log(
                $"ViewerServer: device '{Device.Label}' ({DeviceId}) fell {totalDrops} frames behind without recovering; " +
                "aborting the connection so the client reconnects and re-snapshots.",
                "ViewerServer", LogLevel.System);
            try { WebSocket.Abort(); } catch { }
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
                $"ViewerServer: send buffer for device '{Device.Label}' dropped ~{currentDropCount} frames (cap={MaxBufferedFrames}). " +
                "Viewer is consuming slower than the event stream; oldest frames are evicted.",
                "ViewerServer", LogLevel.Communication);
        }

        // Single reader of the channel and the only caller of SendAsync on
        // this socket — WebSocket.SendAsync is not thread-safe, so the pump
        // preserves the send-serialisation contract the semaphore provided.
        private async Task SendPumpAsync(CancellationToken ct)
        {
            try
            {
                var reader = _sendQueue.Reader;
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var frame))
                    {
                        if (WebSocket.State != WebSocketState.Open) continue;
                        try
                        {
                            await WebSocket.SendAsync(new ArraySegment<byte>(frame),
                                WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                        }
                        catch (WebSocketException) { /* peer gone — drop quietly */ }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex) { GlobalLogger.Error("ViewerServer", "WS send", ex); }
                    }

                    // Queue fully drained — the viewer caught up, so the
                    // eviction streak (if any) is over. Reset the counters:
                    // only MaxDropsBeforeAbort drops without a single full
                    // drain force the reconnect.
                    Interlocked.Exchange(ref _droppedFrames, 0);
                    Interlocked.Exchange(ref _lastReportedDrops, 0);
                }
            }
            catch (OperationCanceledException) { /* session teardown */ }
            catch (Exception ex)
            {
                GlobalLogger.Error("ViewerServer", "WS send pump", ex);
            }
        }

        public Task CloseAsync() => CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");

        public async Task CloseAsync(WebSocketCloseStatus status, string description)
        {
            try
            {
                if (WebSocket.State == WebSocketState.Open)
                    await WebSocket.CloseAsync(status, description, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* swallow — half-closed sockets are fine to abandon */ }
        }

        // Completes the send queue and cancels the pump. Idempotent / safe
        // to call after the socket is gone. Deliberately does NOT await
        // _sendPump — Dispose runs from the WS handler finally; the pump
        // observes the cancelled CT (or the completed writer) and exits, and
        // any fault is already logged inside SendPumpAsync.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _sendQueue.Writer.TryComplete(); } catch { }
            try { _cts.Cancel(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
