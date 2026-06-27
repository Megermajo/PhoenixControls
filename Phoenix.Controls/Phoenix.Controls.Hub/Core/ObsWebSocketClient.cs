using System;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core;

/// <summary>
/// B38 (audit/winui-regressions-2026-05-24) — direct OBS WebSocket v5+
/// client. Pre-B38 OBS integration was REST-only via Streamer.bot DoAction
/// (13 commands: scene / recording / streaming / replay / source-visibility /
/// etc.). Scripts could <em>trigger</em> OBS but could not <em>react</em> to
/// OBS state changes because no inbound event subscription existed. This
/// class closes that gap by subscribing to the OBS WS v5 EventSubscription
/// bitmask and fanning matching events through <see cref="EventReceived"/>,
/// which Hub's <c>ScriptManager.DispatchObsEvent</c> converts into
/// <c>on_obs("&lt;EventType&gt;")</c> handler triggers and which Hub's
/// <c>HubBootstrapper</c> mirrors as an <c>OBS_EVENT</c> bus broadcast for
/// Architect's debug-trace + future panels.
///
/// Protocol reference:
///   https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md
///
/// Handshake (sender → us):
///   OpCode 0 Hello       — server announces protocol + optional auth
///                          (auth.challenge + auth.salt).
///   OpCode 1 Identify    — we respond with rpcVersion +
///                          eventSubscriptions bitmask + (when auth was
///                          announced) authResponse =
///                          base64(SHA256(base64(SHA256(password+salt)) +
///                          challenge)).
///   OpCode 2 Identified  — server accepts; <see cref="IsConnected"/>
///                          flips true.
///   OpCode 5 Event       — every subscribed event arrives as
///                          { eventType, eventData } and gets routed to
///                          <see cref="EventReceived"/>.
///
/// Reconnect is driven by <c>WebsocketClient.ReconnectionHappened</c>:
/// every (re-)connect re-runs the handshake. <c>ErrorReconnectTimeout</c>
/// is updated with exponential backoff + jitter on each disconnect so
/// repeated failures don't thunder against the OBS host.
/// </summary>
public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;
    private readonly int _eventSubscriptions;

    private WebsocketClient? _client;
    private IDisposable? _reconnectionSub;
    private IDisposable? _disconnectionSub;
    private IDisposable? _messageReceivedSub;

    // Backoff state — mirrors WS.cs's Streamer.bot client. Incremented per
    // DisconnectionHappened, reset on a successful ReconnectionHappened.
    // The library reads ErrorReconnectTimeout when scheduling the next retry,
    // so updating it inside the disconnection handler applies to the upcoming
    // attempt.
    private int _consecutiveDisconnects;
    private static readonly Random _backoffJitter = new();
    private const double BackoffBaseSeconds = 5.0;
    private const double BackoffMaxSeconds = 60.0;

    // Guards _client lifecycle against concurrent send / dispose. Same
    // pattern as WS._clientLock.
    private readonly object _clientLock = new();

    private int _disposed;

    /// <summary>
    /// Fires for every inbound OBS event with the raw event type
    /// (<c>CurrentProgramSceneChanged</c>, <c>RecordStateChanged</c>, …)
    /// and the JSON payload string of the OBS <c>eventData</c> object
    /// (or "{}" when the event carries no data). Subscribers should
    /// marshal back to their own dispatcher; this event fires from the
    /// <c>Websocket.Client</c> message pump.
    /// </summary>
    public event Action<string, string>? EventReceived;

    /// <summary>True after a successful Identified handshake.</summary>
    // [P1 swarm-audit 2026-05-29] Written from three unsynchronized callback contexts
    // (DisconnectionHappened sub, HandleIdentified, DisconnectAsync). Back it with a
    // volatile field so a writer on one thread is visible to readers on another without
    // taking _clientLock for a single bool.
    private volatile bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => _isConnected = value; }

    public ObsWebSocketClient(string host, int port, string password, int eventSubscriptions = 1023)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _password = password ?? string.Empty;
        // OBS WS v5 EventSubscription bitmask. 1023 (0x3FF) = General +
        // Config + Scenes + Inputs + Transitions + Filters + Outputs +
        // SceneItems + MediaInputs + Vendors + Ui. Surfaced as a ctor
        // parameter (defaulted to 1023) so AppConfig.ObsEventSubscriptionMask
        // can scope it down for streamers who only care about a slice.
        _eventSubscriptions = eventSubscriptions;
    }

    /// <summary>
    /// Establishes the OBS WebSocket v5 connection and starts the
    /// reconnect/keepalive loop. Returns once the underlying
    /// <c>WebsocketClient.Start</c> call has completed; the Identified
    /// handshake itself may still be in flight when this returns — check
    /// <see cref="IsConnected"/> to confirm. Idempotent: a second call
    /// after a prior connect is a no-op.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_client != null) return;

        var url = new Uri($"ws://{_host}:{_port}/");

        // Match WS.cs's keepalive posture — explicit 30s so a future framework
        // default change doesn't silently move our reconnect cadence.
        var factory = new Func<ClientWebSocket>(() =>
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            return ws;
        });

        WebsocketClient client;
        lock (_clientLock)
        {
            client = new WebsocketClient(url, factory)
            {
                // Disable Websocket.Client's idle-based forced reconnect —
                // OBS does not push traffic during quiet periods, so a finite
                // ReconnectTimeout would tear the socket down every <window>
                // seconds even when the link is healthy. TCP keepalive +
                // ErrorReconnectTimeout still cover genuine disconnects.
                ReconnectTimeout = null,
                ErrorReconnectTimeout = TimeSpan.FromSeconds(BackoffBaseSeconds),
            };
            _client = client;

            // [P1 swarm-audit 2026-05-29] Assign the Rx subscription fields under the
            // SAME _clientLock that guards _client. Previously they were assigned outside
            // the lock while DisconnectAsync disposed+nulled them under the lock — an
            // unsynchronized race that could dispose a half-assigned set or leak a sub.
            _reconnectionSub = client.ReconnectionHappened.Subscribe(info =>
            {
                // ReconnectionHappened fires once per (re-)connect. Reset the
                // backoff counter so a fresh outage starts at BackoffBaseSeconds
                // again. IsConnected stays false here — the handshake (Hello →
                // Identify → Identified, OpCode 2) flips it from MessageReceived.
                Interlocked.Exchange(ref _consecutiveDisconnects, 0);
                GlobalLogger.Log(
                    $"OBS WebSocket socket (re)connected to {url} — awaiting Hello.",
                    "ObsWebSocketClient", LogLevel.Communication);
            });

            _disconnectionSub = client.DisconnectionHappened.Subscribe(info =>
            {
                IsConnected = false;
                int n = Interlocked.Increment(ref _consecutiveDisconnects);
                if (n == 1)
                {
                    GlobalLogger.Log(
                        "OBS WebSocket disconnected — attempting reconnect.",
                        "ObsWebSocketClient", LogLevel.Communication);
                }

                // Exponential backoff + ±25% jitter, capped at BackoffMaxSeconds.
                // Matches WS.cs's curve so the operator sees consistent reconnect
                // cadence across the two long-lived WS clients.
                double baseSeconds = Math.Min(
                    BackoffBaseSeconds * Math.Pow(2.0, Math.Min(n - 1, 4)),
                    BackoffMaxSeconds);
                double jitterPct;
                lock (_backoffJitter) jitterPct = _backoffJitter.NextDouble() * 0.5 - 0.25;
                double delay = Math.Max(BackoffBaseSeconds, baseSeconds * (1.0 + jitterPct));
                try { client.ErrorReconnectTimeout = TimeSpan.FromSeconds(delay); } catch { }
            });

            _messageReceivedSub = client.MessageReceived.Subscribe(msg =>
            {
                if (msg.Text is null)
                {
                    // OBS WS v5 only sends text frames — a binary frame here is
                    // an unexpected protocol violation. Log + drop, don't throw.
                    GlobalLogger.Log(
                        "OBS WebSocket dropped a non-text frame (protocol expects JSON text).",
                        "ObsWebSocketClient", LogLevel.Communication);
                    return;
                }
                try { HandleInboundMessage(msg.Text); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("ObsWebSocketClient",
                        "Inbound message parse / dispatch failed", ex);
                }
            });
        }

        try
        {
            await client.Start().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ObsWebSocketClient",
                $"Start() failed against {url}", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void HandleInboundMessage(string text)
    {
        // OBS WS v5 envelope: { "op": <int>, "d": <obj> }
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.Number)
        {
            GlobalLogger.Log(
                $"OBS WS frame missing numeric 'op' — dropping: {Truncate(text, 200)}",
                "ObsWebSocketClient", LogLevel.Communication);
            return;
        }
        int op = opEl.GetInt32();
        JsonElement d = root.TryGetProperty("d", out var dEl) ? dEl : default;

        switch (op)
        {
            case 0: // Hello
                HandleHello(d);
                break;
            case 2: // Identified
                HandleIdentified(d);
                break;
            case 5: // Event
                HandleEvent(d);
                break;
            // OpCode 6 (Request) / 7 (RequestResponse) / 9 (RequestBatchResponse)
            // are not implemented in this sprint — Request plumbing is the
            // follow-up sprint after the event-subscription work in B38.
            // Drop quietly so a future enable doesn't surface noise here.
            default:
                GlobalLogger.Log(
                    $"OBS WS unhandled OpCode={op} (length={text.Length}) — ignored.",
                    "ObsWebSocketClient", LogLevel.Debug);
                break;
        }
    }

    private void HandleHello(JsonElement d)
    {
        string obsVersion = d.TryGetProperty("obsWebSocketVersion", out var v) ? (v.GetString() ?? "?") : "?";
        int rpcVersion = d.TryGetProperty("rpcVersion", out var rpc) && rpc.ValueKind == JsonValueKind.Number
            ? rpc.GetInt32()
            : 1;

        // Optional authentication block: { challenge, salt }. When OBS has
        // no password configured the block is absent and we send the
        // Identify response without an authResponse field.
        string? authResponse = null;
        if (d.TryGetProperty("authentication", out var authEl)
            && authEl.ValueKind == JsonValueKind.Object)
        {
            string challenge = authEl.TryGetProperty("challenge", out var c) ? (c.GetString() ?? "") : "";
            string salt      = authEl.TryGetProperty("salt",      out var s) ? (s.GetString() ?? "") : "";

            if (string.IsNullOrEmpty(_password))
            {
                GlobalLogger.Log(
                    "OBS WebSocket announced an auth challenge but no password is configured (AppConfig.ObsWebSocketPassword is empty). Identify will be sent without an authResponse — OBS will close the socket.",
                    "ObsWebSocketClient", LogLevel.CriticalError);
            }
            else
            {
                authResponse = ComputeAuthResponse(_password, salt, challenge);
            }
        }

        // Build + send Identify (OpCode 1). rpcVersion is echoed from Hello;
        // eventSubscriptions is our requested bitmask.
        string payload = BuildIdentifyEnvelope(rpcVersion, _eventSubscriptions, authResponse);
        TrySend(payload);

        GlobalLogger.Log(
            $"OBS WS Hello received (obsWebSocketVersion={obsVersion}, rpcVersion={rpcVersion}, auth={(authResponse != null ? "yes" : "no")}) — Identify sent.",
            "ObsWebSocketClient", LogLevel.Communication);
    }

    private void HandleIdentified(JsonElement d)
    {
        int negotiatedRpc = d.TryGetProperty("negotiatedRpcVersion", out var n) && n.ValueKind == JsonValueKind.Number
            ? n.GetInt32()
            : 1;
        IsConnected = true;
        GlobalLogger.Log(
            $"OBS WebSocket Identified — negotiatedRpcVersion={negotiatedRpc}, eventSubscriptions=0x{_eventSubscriptions:X}.",
            "ObsWebSocketClient", LogLevel.Communication);
    }

    private void HandleEvent(JsonElement d)
    {
        string eventType = d.TryGetProperty("eventType", out var t) ? (t.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(eventType)) return;

        // eventData is optional in the v5 envelope (some events carry only
        // an eventIntent). Serialise the raw JSON so subscribers can parse
        // the shape themselves; pass "{}" when the field is missing rather
        // than null so handlers never have to null-guard.
        string payload = d.TryGetProperty("eventData", out var data)
            ? data.GetRawText()
            : "{}";

        Raise(eventType, payload);
    }

    /// <summary>
    /// Forwards an inbound OBS event to <see cref="EventReceived"/>
    /// subscribers, swallowing per-subscriber faults so one bad handler
    /// can't poison the rest. Internal so the message-pump path can call
    /// it without exposing the event raise to external callers.
    /// </summary>
    internal void Raise(string eventType, string payload)
    {
        try { EventReceived?.Invoke(eventType, payload); }
        catch (Exception ex)
        {
            GlobalLogger.Error("ObsWebSocketClient",
                $"EventReceived subscriber threw on '{eventType}'", ex);
        }
    }

    /// <summary>
    /// OBS WS v5 auth-response formula:
    ///   secret       = base64(SHA256(password + salt))
    ///   authResponse = base64(SHA256(secret + challenge))
    /// Both SHA256 inputs are UTF-8 byte sequences of the concatenated
    /// strings; both base64 encodings use the standard +/= alphabet.
    /// </summary>
    private static string ComputeAuthResponse(string password, string salt, string challenge)
    {
        using var sha = SHA256.Create();
        byte[] step1 = sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt));
        string secret = Convert.ToBase64String(step1);
        byte[] step2 = sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge));
        return Convert.ToBase64String(step2);
    }

    private static string BuildIdentifyEnvelope(int rpcVersion, int eventSubscriptions, string? authResponse)
    {
        // Hand-roll the JSON rather than anonymous-object the optional
        // authResponse — adding a property to an anonymous object at runtime
        // would require either a Dictionary or two distinct shapes. The
        // resulting JSON is tiny + well-formed; STJ-serialised strings are
        // safe because authResponse is base64 (no quotes / backslashes).
        var sb = new StringBuilder();
        sb.Append("{\"op\":1,\"d\":{");
        sb.Append("\"rpcVersion\":").Append(rpcVersion);
        sb.Append(",\"eventSubscriptions\":").Append(eventSubscriptions);
        if (!string.IsNullOrEmpty(authResponse))
        {
            sb.Append(",\"authentication\":\"").Append(authResponse).Append('\"');
        }
        sb.Append("}}");
        return sb.ToString();
    }

    private void TrySend(string payload)
    {
        WebsocketClient? snapshot;
        lock (_clientLock) snapshot = _client;
        if (snapshot is null) return;
        try { snapshot.Send(payload); }
        catch (Exception ex)
        {
            GlobalLogger.Error("ObsWebSocketClient",
                "Send failed (will retry on next reconnect)", ex);
        }
    }

    /// <summary>
    /// Cleanly closes the OBS WebSocket and disposes the underlying
    /// <c>WebsocketClient</c>. Sends a Normal Closure (1000) status when
    /// the socket is still open; tolerates the server having closed first.
    /// Idempotent — a second call is a no-op.
    /// </summary>
    public async Task DisconnectAsync()
    {
        WebsocketClient? snapshot;
        // [P1 swarm-audit 2026-05-29] Capture+null the Rx subscription fields under
        // the SAME _clientLock that ConnectAsync uses to assign them, so this disposal
        // can't race a concurrent (re)connect. Dispose the captured locals after the
        // lock is released.
        IDisposable? reconnectionSubSnapshot;
        IDisposable? disconnectionSubSnapshot;
        IDisposable? messageReceivedSubSnapshot;
        lock (_clientLock)
        {
            snapshot = _client;
            _client = null;
            reconnectionSubSnapshot = _reconnectionSub;
            _reconnectionSub = null;
            disconnectionSubSnapshot = _disconnectionSub;
            _disconnectionSub = null;
            messageReceivedSubSnapshot = _messageReceivedSub;
            _messageReceivedSub = null;
        }
        IsConnected = false;

        if (snapshot is null) return;

        try
        {
            // Stop() sends a close frame (1000 Normal Closure) and tears
            // down the reconnect loop. We DON'T await Start() / Stop() race
            // detection — the lib handles half-open states internally.
            await snapshot.Stop(WebSocketCloseStatus.NormalClosure, "Hub shutdown")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ObsWebSocketClient", "Stop() failed during disconnect", ex);
        }

        // Dispose Rx subscriptions then the client itself. Run on a thread-
        // pool task so a UI-thread caller (HubBootstrapper.Shutdown) doesn't
        // block on the lib's join — same pattern as WS.StopAsync.
        await Task.Run(() =>
        {
            // [P1 swarm-audit 2026-05-29] Dispose the snapshots captured under the lock
            // above; the fields were already nulled there.
            try { reconnectionSubSnapshot?.Dispose(); }    catch { }
            try { disconnectionSubSnapshot?.Dispose(); }   catch { }
            try { messageReceivedSubSnapshot?.Dispose(); } catch { }

            try { snapshot.Dispose(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("ObsWebSocketClient", "client dispose error", ex);
            }
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            GlobalLogger.Error("ObsWebSocketClient", "DisposeAsync.Disconnect failed", ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ObsWebSocketClient));
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";
}
