using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Websocket.Client;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Hub.Core;


namespace Phoenix.Controls.Hub.Core
{
    public class WS
    {
        private static WS? _instance;
        public static WS Instance => _instance ??= new WS();

        private WebsocketClient _client;
        private string _url = "ws://127.0.0.1:8080/"; // Default
        public int HUDPort { get; private set; } = 18080;
        private string _configPath = Paths.AppConfigJson;

        public event Action<bool>? OnConnectionStatusChanged;
        public event Action<string>? OnMessageReceived;
        public event Action<ChatMessage>? OnChatMessage;

        public bool IsConnected { get; private set; } = false;

        // Bounded so a flood of upstream chat messages can't grow this queue
        // unboundedly and OOM the process. Producers must use TryAdd (not Add)
        // to honor the back-pressure: TryAdd returns false on full so we can
        // log + drop. Add() blocks indefinitely when full, which would freeze
        // the upstream message handler under a chat flood.
        //
        // Non-readonly so Initialize() can recreate it after a Stop().
        // Stop() Disposes the queue (BlockingCollection.Dispose marks it dead);
        // without replacement, Stop()-then-Initialize() would TryAdd onto a
        // disposed queue and throw. The Interlocked CAS in Initialize ensures
        // exactly one replacement per cycle.
        private const int ScriptQueueCapacity = 1_000;
        private BlockingCollection<ChatMessage> _scriptQueue = new BlockingCollection<ChatMessage>(ScriptQueueCapacity);

        // Diagnostics surface for queue overflow. Counter is monotonic for the
        // process lifetime; consumers (Diagnostics window) display it as
        // "messages dropped since startup".
        private long _droppedQueueMessages;
        public long DroppedQueueMessages => System.Threading.Interlocked.Read(ref _droppedQueueMessages);
        public event Action<long>? OnQueueOverflow;

        // Recent-chat ring buffer feeding Chat.PeekRecent. Independent of _scriptQueue
        // (which is a single-consumer queue drained by ProcessScriptQueueAsync) so the
        // peek doesn't compete with logic-execution dispatch. Bounded; oldest entries
        // drop on overflow so memory stays flat under sustained chat. _recentChatLock
        // guards both the deque and the size invariant.
        private const int RecentChatCapacity = 50;
        private readonly System.Collections.Generic.LinkedList<ChatMessage> _recentChatBuffer = new();
        private readonly object _recentChatLock = new();

        // EventLog audit coalescing. ParseBotMessage audit-logs every
        // non-chat, non-heartbeat SB event; firing one fire-and-forget
        // autocommit INSERT per event serialized an unbounded number of tasks
        // on the shared DB semaphore — during poll/hype-train bursts that is
        // many commits/sec contending with script-engine writes. Producers
        // TryWrite into this bounded channel and a single consumer flushes
        // batches (AuditBatchSize rows or AuditFlushMs, whichever first) in
        // ONE transaction via DB.LogEventsBatchAsync — identical rows/values,
        // only commit granularity changes. DropOldest bounds memory if the DB
        // stalls; EventLog is a diagnostic audit surface, not a durability
        // contract. StopAsync completes the writer and drains the pump so the
        // in-flight batch flushes on shutdown.
        private const int AuditBatchSize = 50;
        private const int AuditFlushMs = 250;
        private const int AuditQueueCapacity = 20_000;
        private Channel<AuditRow> _auditChannel = CreateAuditChannel();
        private Task? _auditPumpTask;
        private int _auditPumpStarted;   // 0 = not running, 1 = running (CAS-gated one-shot)
        private int _auditChannelCompleted; // 1 after StopAsync completed the writer
        private long _auditEnqueueSeq;   // monotonic writer stamp — see AuditRow
        private long _auditEvictedTotal; // running count of evicted audit rows (logged coalesced)

        // Seq rides along so the single reader can detect DropOldest
        // evictions: the channel evicts silently, but a gap between
        // consecutive Seq values is exactly the number of rows it dropped —
        // no silent cap.
        private readonly record struct AuditRow(
            long Seq, string Source, string Type, string User, string Payload);

        private static Channel<AuditRow> CreateAuditChannel() =>
            Channel.CreateBounded<AuditRow>(
                new BoundedChannelOptions(AuditQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

        // SB authentication state. When the configured Streamer.bot password
        // is non-empty we send the v0.2 Authenticate handshake on first connect.
        // If auth fails (server rejects), we suppress further reconnect attempts
        // rather than spinning forever against bad creds (the silent-infinite-
        // reconnect symptom this fix targets).
        private const string AuthRequestId = "phoenix-authenticate";
        // Volatile because the value is set on the WS message-receive thread (auth response)
        // and read on the disconnection-event thread; without it the disconnect handler
        // could observe a stale `false` after the auth-failure flip and let reconnects
        // continue spinning against bad creds.
        private volatile bool _authFailedFatal;

        // Rx subscription handles — disposed and replaced each Initialize() call so
        // reconnect attempts don't accumulate duplicate event handlers.
        private IDisposable? _reconnectionSub;
        private IDisposable? _disconnectionSub;
        private IDisposable? _messageReceivedSub;

        // Cached parse of `AppConfig.BotUsername` (comma-separated). Previously
        // IsBlockedAccount and IsBroadcasterActor each split-and-allocated a fresh
        // HashSet on every chat message, which produced significant GC pressure under
        // chat floods. The cache is keyed by the source string itself: the hot path
        // is a string-equality check, and we lazily rebuild only when the underlying
        // BotUsername actually changed (covers both LoadSettings updates AND test /
        // ad-hoc paths that mutate `ConfigManager.Current.BotUsername` directly).
        private HashSet<string> _blockedAccountsCache = new(StringComparer.OrdinalIgnoreCase);
        private string _blockedAccountsKey = string.Empty;
        private readonly object _blockedAccountsLock = new();

        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

        // Guards _client lifecycle against concurrent Send. Without this, Stop()/Initialize()
        // could call _client.Dispose() while another thread is mid-Send, throwing
        // ObjectDisposedException out of the public Send surface.
        private readonly object _clientLock = new();

        // Reconnect backoff state. Incremented on each DisconnectionHappened, reset on
        // each successful ReconnectionHappened. Drives the exponential+jitter timeout
        // assigned to _client.ErrorReconnectTimeout so we don't slam SB / the network
        // every 5s during sustained outages or auth loops.
        private int _consecutiveDisconnects;
        private static readonly Random _backoffJitter = new();
        private const double BackoffBaseSeconds = 5.0;
        private const double BackoffMaxSeconds = 60.0;

        private WS()
        {
            _instance = this; // Self-assignment for safety
            _client = null!;  // Initialized in Initialize()
        }

        // Guard against re-entrant Initialize. The previous call
        // launched a fresh ProcessScriptQueueAsync each time and leaked the old _client
        // when reconfiguring. Now we dispose the prior client and only start the
        // consumer once per process lifetime.
        private int _consumerStarted;

        // Handle on the consumer task so Stop() can await
        // its exit before disposing _scriptQueue. Without this, the consumer's
        // GetConsumingEnumerable foreach can be mid-iteration when the
        // BlockingCollection.Dispose() runs, surfacing ObjectDisposedException
        // from inside ScriptManager.ExecuteOnChatScriptsAsync.
        private Task? _consumerTask;

        public void Initialize()
        {
            LoadSettings();

            // Reset the Stop() latch so a Stop()-then-Initialize() cycle
            // (e.g. settings reload, manual reconnect, future hot-restart) actually
            // produces a working WS. Pre-fix, _stopped stayed pinned at 1 forever
            // after the first Stop, which turned every subsequent Stop into a no-op
            // and meant the new _client we install below would never be released on
            // the next round-trip — kernel-handle leak + zombie subscriptions.
            //
            // _consumerStarted is reset alongside; it gates the one-shot
            // ProcessScriptQueueAsync consumer, and on Initialize we always create
            // a fresh _scriptQueue (it was Disposed in Stop), so the consumer must
            // be allowed to spin up again. The Interlocked CAS below makes that
            // safe under a hypothetical concurrent Stop racing this Initialize.
            Interlocked.Exchange(ref _stopped, 0);
            Interlocked.Exchange(ref _consumerStarted, 0);
            // Re-arm the role-tag-map one-shot so a settings-reload
            // (which re-invokes Initialize) leaves a fresh breadcrumb in the log
            // tied to the new reconnect cycle.
            Interlocked.Exchange(ref _roleTagMapLogged, 0);

            // Clear the fatal-auth latch before each round-trip. Without this,
            // after Majo fixes the SB password and the UI re-invokes Initialize, the
            // next disconnect's handler (line ~127) still sees `_authFailedFatal=true`
            // and disables reconnect with the misleading "auth was rejected"
            // CriticalError, so the operator's correction silently fails.
            _authFailedFatal = false;

            // Dispose any previous client before overwriting the field. Without this
            // its background subscriptions remained alive and competed with the new
            // socket for inbound frames. Lock-protected against concurrent Send so
            // we can't dispose mid-frame.
            lock (_clientLock)
            {
                try { _client?.Dispose(); } catch { }

                // Pin ClientWebSocket.Options.KeepAliveInterval
                // explicitly. .NET's default is 30s which would normally do, but
                // relying on the implicit value means a future framework change
                // could silently shift our reconnect cadence. We also disable the
                // library's idle-reconnect (see ReconnectTimeout = null below),
                // so the WS-level keepalive is the only thing detecting a
                // genuinely-dead Streamer.bot socket during a quiet period.
                var factory = new Func<ClientWebSocket>(() =>
                {
                    var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    return ws;
                });
                _client = new WebsocketClient(new Uri(_url), factory);
            }

            // Disable Websocket.Client's idle-based forced reconnect. Streamer.bot
            // does not push traffic during quiet periods, so a finite ReconnectTimeout
            // makes the client tear the socket down every <window> seconds even when
            // the link is healthy. TCP keepalive + ErrorReconnectTimeout still cover
            // genuine disconnects.
            _client.ReconnectTimeout = null;
            // Initial value; the disconnection handler updates this dynamically with
            // exponential backoff + jitter so repeated failures don't thunder.
            _client.ErrorReconnectTimeout = TimeSpan.FromSeconds(BackoffBaseSeconds);

            // Dispose previous subscriptions before re-subscribing to avoid
            // duplicate handler accumulation if Initialize() is called more than once.
            _reconnectionSub?.Dispose();
            _reconnectionSub = _client.ReconnectionHappened.Subscribe(info => {
              // Outer guard. An unhandled throw from
              // EmitRoleTagMapDiagnosticOnce / TryBeginStreamerBotAuth /
              // SubscribeToEvents would terminate the Rx subscription thread and
              // silently kill auto-reconnect. Inner ops keep their own try/catch;
              // this is the net that stops one bad reconnect from severing the link.
              try {
                IsConnected = true;
                // Reset backoff on a successful (re)connect so the next outage starts
                // fresh at BackoffBaseSeconds rather than the elevated value.
                Interlocked.Exchange(ref _consecutiveDisconnects, 0);
                GlobalLogger.Log($"WebSocket Connected/Reconnected to {_url}", "WS", LogLevel.Communication);
                OnConnectionStatusChanged?.Invoke(true);

                // Replaces the per-chat [RoleDebug] spam with a single
                // role-tag-map sample on connect. Documents the expected role-int
                // ↔ flag mapping so an operator opening the SystemLog after a
                // restart can see what schema the engine is decoding against,
                // without buying it on every chat line.
                EmitRoleTagMapDiagnosticOnce();

                // Fire the SB auth handshake BEFORE any subscriptions if a
                // password is configured. SB v0.2 Authenticate is a simple
                // {request:"Authenticate", id, authentication} envelope; on failure
                // SB returns an error response we correlate by id (see ParseBotMessage).
                // If no password is configured (NIE check), behavior is unchanged
                // and we go straight to SubscribeToEvents.
                if (TryBeginStreamerBotAuth())
                {
                    // Subscriptions will fire from the auth-success path; in the
                    // unauthenticated branch SubscribeToEvents has been called below.
                    return;
                }
                SubscribeToEvents();
              }
              catch (Exception ex) { GlobalLogger.Error("WS", "reconnection handler threw", ex); }
            });

            _disconnectionSub?.Dispose();
            _disconnectionSub = _client.DisconnectionHappened.Subscribe(info => {
              // Outer guard — see reconnection handler.
              // OnConnectionStatusChanged?.Invoke(false) and the backoff math below
              // are otherwise unprotected; an unhandled throw kills the Rx thread.
              try {
                IsConnected = false;

                // Exponential backoff + ±25% jitter. Without this, a constant 5s retry
                // hammers SB during auth-loops and produces a thundering-herd retry on
                // network flap. Capped at BackoffMaxSeconds so very long outages still
                // recover within a minute of restoration. Lib reads ErrorReconnectTimeout
                // when scheduling the next attempt, so updating it here applies to the
                // upcoming retry.
                int n = Interlocked.Increment(ref _consecutiveDisconnects);

                // Log only the first disconnect of a sustained outage — _consecutiveDisconnects
                // resets to 0 on ReconnectionHappened, so n==1 is exactly the transition
                // from connected to a fresh outage. Without this the System Log filled with
                // a "WebSocket Disconnected." line every 5..60s during quiet periods when
                // Streamer.bot wasn't running, which buried genuine signals.
                if (n == 1)
                {
                    GlobalLogger.Log("WebSocket Disconnected — attempting reconnect.", "WS", LogLevel.Communication);
                }
                OnConnectionStatusChanged?.Invoke(false);
                double baseSeconds = Math.Min(BackoffBaseSeconds * Math.Pow(2.0, Math.Min(n - 1, 4)), BackoffMaxSeconds);
                double jitterPct;
                lock (_backoffJitter) jitterPct = _backoffJitter.NextDouble() * 0.5 - 0.25;
                double delay = Math.Max(baseSeconds, baseSeconds * (1.0 + jitterPct));
                try { _client.ErrorReconnectTimeout = TimeSpan.FromSeconds(delay); } catch { }

                // If auth has been declared fatally rejected, stop the
                // reconnect loop. Without this the Websocket.Client's automatic
                // reconnector would keep slamming SB with bad creds forever and
                // we'd silently appear "connecting" in the UI.
                if (_authFailedFatal)
                {
                    try { _client.IsReconnectionEnabled = false; } catch { }
                    GlobalLogger.Log("L64 — reconnect disabled because Streamer.bot auth was rejected. Fix the password in config and restart Hub.",
                        "WS", LogLevel.CriticalError);
                }
              }
              catch (Exception ex) { GlobalLogger.Error("WS", "disconnection handler threw", ex); }
            });

            _messageReceivedSub?.Dispose();
            // WebSocket fragmentation/coalescing assumption.
            //
            // Streamer.bot can split a large frame across multiple WebSocket
            // continuation frames at the wire level. We deliberately do NOT
            // implement our own fragment reassembler here: the Websocket.Client
            // library (Websocket.Client 5.3.0, see csproj) buffers continuation
            // frames internally and only raises MessageReceived once the full
            // logical message is assembled. That contract is what makes
            // ParseBotMessage(json) safe to call with the full JSON document.
            //
            // If the upstream library ever changes that behavior, the symptom
            // would be JsonDocument.Parse throwing on truncated payloads — those
            // already fall through to the catch in ParseBotMessage and surface
            // as a "WS parse error" log. The drop-on-null-Text guard below
            // covers the related case where a binary frame slips through this
            // text-only handler; we log it at Communication level so the drop
            // is visible without spamming on every frame.
            _messageReceivedSub = _client.MessageReceived.Subscribe(msg => {
                // The Rx subscriber runs on the
                // Websocket.Client receive thread. An unhandled throw from a
                // downstream subscriber (OnMessageReceived) or from ParseBotMessage
                // terminates that Rx subscription thread and silently severs the
                // chat receive pump — Hub keeps "connected" but no messages flow.
                // Wrap the whole body so one bad frame can never kill the pump.
                try
                {
                    if (msg.Text == null)
                    {
                        GlobalLogger.Log(
                            "WebSocket frame with null Text dropped (binary or empty frame; WS expects coalesced JSON text)",
                            "WS", LogLevel.Communication);
                        return;
                    }
                    // Per-subscriber try/catch isolation — mirror the OnChatMessage
                    // GetInvocationList pattern below. A throwing OnMessageReceived
                    // subscriber must not skip the remaining subscribers (nor abort
                    // ParseBotMessage); each is invoked independently and faults are
                    // logged per-subscriber.
                    var rawHandlers = OnMessageReceived?.GetInvocationList();
                    if (rawHandlers is not null)
                    {
                        for (int ri = 0; ri < rawHandlers.Length; ri++)
                        {
                            try { ((Action<string>)rawHandlers[ri])(msg.Text); }
                            catch (Exception subEx)
                            {
                                GlobalLogger.Error("WS", "OnMessageReceived subscriber threw", subEx);
                            }
                        }
                    }
                    ParseBotMessage(msg.Text);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("WS", "MessageReceived handler threw — frame dropped, pump kept alive", ex);
                }
            });

            // Replace the audit channel if a previous Stop() completed its
            // writer — a completed Channel cannot accept writes again. Gated
            // on the completion flag (NOT run unconditionally) so a repeat
            // Initialize without an intervening Stop leaves the live channel
            // and its pump alone.
            if (Interlocked.Exchange(ref _auditChannelCompleted, 0) == 1)
            {
                _auditChannel = CreateAuditChannel();
                _auditPumpTask = null;
                Interlocked.Exchange(ref _auditPumpStarted, 0);
            }

            // Replace the script queue if a previous Stop() disposed it.
            // BlockingCollection cannot be revived after Dispose; the
            // GetConsumingEnumerable loop in ProcessScriptQueueAsync also returns
            // immediately on a completed queue, so a fresh instance is required to
            // restart consumption. _consumerStarted was reset above so the new
            // queue's consumer task actually launches below.
            if (_scriptQueue.IsAddingCompleted)
            {
                try { _scriptQueue.Dispose(); } catch { }
                _scriptQueue = new BlockingCollection<ChatMessage>(ScriptQueueCapacity);
            }

            // Single-shot consumer guard — second Initialize() call must not spawn
            // a parallel ProcessScriptQueueAsync that would race the first. We
            // capture the task handle so Stop() can drain it before disposing
            // the BlockingCollection.
            if (System.Threading.Interlocked.Exchange(ref _consumerStarted, 1) == 0)
                _consumerTask = Task.Run(() => ProcessScriptQueueAsync());
        }

        private void LoadSettings()
        {
            ConfigManager.Load(_configPath);
            _url   = ConfigManager.Current.StreamerBotUrl;
            HUDPort = ConfigManager.Current.HUDServerPort;

            // Eagerly rebuild the cache here so the very first chat message
            // after settings reload doesn't pay the rebuild cost. IsBlockedAccount also
            // rebuilds lazily when it observes a BotUsername change, so mutations made
            // outside ConfigManager.Load (tests, future hot-reload paths) still see
            // consistent results.
            RebuildBlockedAccountsCache(ConfigManager.Current.BotUsername ?? string.Empty);
        }

        private void RebuildBlockedAccountsCache(string botUsername)
        {
            var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(botUsername))
            {
                foreach (var part in botUsername.Split(','))
                    if (!string.IsNullOrWhiteSpace(part)) fresh.Add(part.Trim());
            }
            lock (_blockedAccountsLock)
            {
                _blockedAccountsCache = fresh;
                _blockedAccountsKey   = botUsername;
            }
        }

        // The legacy reflection-then-switch TryGetConfig{String,Bool}
        // wrappers were retired: every call site now reads ConfigManager.Current
        // directly via the strongly-typed AppConfig property. Removing the
        // wrappers drops the per-chat-line property-name allocation + switch and
        // lets the JIT inline the property reads.

        // Kicks off the SB Authenticate handshake when (and only when) a
        // non-empty password is configured. Returns true if the handshake was
        // dispatched (so the reconnect callback should NOT also invoke
        // SubscribeToEvents — that runs from the auth-success path instead).
        // Direct AppConfig property read (was reflection-via-helper).
        private bool TryBeginStreamerBotAuth()
        {
            string password = ConfigManager.Current?.StreamerBotPassword ?? string.Empty;
            if (string.IsNullOrEmpty(password))
                return false;

            // Let JsonSerializer handle escaping. The hand-rolled
            // `\\` and `\"` replacements missed every other JSON control
            // character (newline, tab, lone backslash sequences, U+0000..U+001F,
            // unpaired surrogates), so a password with such a code point used
            // to produce malformed JSON that SB rejected silently with no
            // operator-visible reason. Anonymous object → JsonSerializer.Serialize
            // produces a spec-compliant payload regardless of password contents.
            string payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                request        = "Authenticate",
                id             = AuthRequestId,
                authentication = password,
            });
            try
            {
                _client.Send(payload);
                GlobalLogger.Log("L64 — Streamer.bot Authenticate handshake sent.", "WS", LogLevel.Communication);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WS", "L64 — Streamer.bot auth send failed", ex);
                return false;
            }
            return true;
        }

        // Resolves the Authenticate response. SB returns either
        //   {"id":"phoenix-authenticate","status":"ok", ...}
        // or
        //   {"id":"phoenix-authenticate","status":"error","error":"..."}.
        // On success we proceed to SubscribeToEvents like the no-auth path.
        // On failure we log a CriticalError and (only on hard rejection) set
        // _authFailedFatal so the disconnection handler can disable
        // reconnect (preventing the silent infinite reconnect).
        //
        // Only latch _authFailedFatal on an unmistakable
        // credential rejection — not on any non-"ok" status. A malformed
        // response, a parse-only error, or a transient SB-side fault would
        // otherwise wedge reconnect forever even when the configured
        // password is correct. The hard-rejection signature is the SB
        // error envelope explicitly naming a credential failure;
        // anything else stays transient and the existing reconnect
        // backoff handles it.
        private void HandleAuthenticateResponse(System.Text.Json.JsonElement root)
        {
            string status = root.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                GlobalLogger.Log("L64 — Streamer.bot authentication accepted.", "WS", LogLevel.Communication);
                SubscribeToEvents();
                return;
            }

            string err = root.TryGetProperty("error", out var e) ? (e.GetString() ?? status) : status;
            bool hardRejection = IsHardCredentialRejection(status, err);

            if (hardRejection)
            {
                _authFailedFatal = true;
                GlobalLogger.Log(
                    $"L64 — Streamer.bot authentication REJECTED ('{err}'). Reconnects will be suppressed.",
                    "WS", LogLevel.CriticalError);
                // Tear the active socket down so the DisconnectionHappened handler
                // can flip IsReconnectionEnabled = false. We can't toggle it from
                // here because the reconnection loop is driven off disconnect.
                // Route through AsyncErrorBoundary.SafeRunAsync to match the
                // sibling fire-forget Stop calls in this file (lines 293/396/401/433).
                AsyncErrorBoundary.SafeRunAsync(
                    () => _client.Stop(WebSocketCloseStatus.NormalClosure, "auth rejected"),
                    "WS", "auth-rejected client stop");
            }
            else
            {
                // Non-credential failure (malformed envelope / parse-only
                // error / transient SB fault). Don't latch fatal; the existing
                // disconnect backoff retries the handshake on the next reconnect.
                GlobalLogger.Log(
                    $"L64 — Streamer.bot Authenticate replied non-ok ('{status}' err='{err}') but no credential-rejection signature was found. " +
                    "Treating as transient; reconnect backoff will retry the handshake.",
                    "WS", LogLevel.Communication);
            }
        }

        // Recognise the credential-rejection wire signatures. SB uses a
        // few different phrasings across releases ("Invalid", "invalid
        // credentials", "Authentication failed", "Unauthorized"). We
        // deliberately match a small allowlist of substrings rather than
        // "anything that's not ok" — a parse-only error or a stale SB
        // version returning an unexpected error string should not poison
        // reconnect.
        private static bool IsHardCredentialRejection(string status, string error)
        {
            if (string.IsNullOrEmpty(status) && string.IsNullOrEmpty(error)) return false;
            string blob = $"{status} {error}";
            // Substring match is intentional — SB error envelopes wrap the
            // signature word in surrounding prose. Lowercase compare so case
            // changes across SB versions don't break the latch.
            string lc = blob.ToLowerInvariant();
            return lc.Contains("invalid credential")
                || lc.Contains("invalid password")
                || lc.Contains("authentication failed")
                || lc.Contains("unauthorized")
                || lc.Contains("bad credentials")
                || lc.Contains("auth failed")
                || lc.Contains("denied");
        }

        private void ParseBotMessage(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("event", out var ev) &&
                        ev.TryGetProperty("source", out var src) &&
                        ev.TryGetProperty("type", out var type))
                    {
                        string eventSource = src.GetString() ?? string.Empty;
                        string eventType = type.GetString() ?? string.Empty;
                        // Kick's "Kicks Gifted" event arrives with the wire name
                        // "sGifted" (Streamer.bot strips the "Kick" prefix off its
                        // internal KicksGifted id when exporting the event name).
                        // Normalize it once, here, so scripts, nodes, and monitors
                        // all see the readable Kick.KicksGifted.
                        if (eventSource.Equals("Kick", StringComparison.OrdinalIgnoreCase) &&
                            eventType.Equals("sGifted", StringComparison.OrdinalIgnoreCase))
                            eventType = "KicksGifted";
                        string fullEventType = $"{eventSource}.{eventType}";

                        // Chat is multi-platform: Twitch.ChatMessage, YouTube.Message and
                        // Kick.ChatMessage all flow through the SAME pipeline (Chat panel,
                        // bot self-trigger guard, bounded script queue, on_chat dispatch).
                        // ChatMessage.Platform carries which platform the line came from;
                        // ExecuteOnChatScriptsAsync sets the engine EventType from it so
                        // on_chat(platform, ...) headers can gate per platform. Resolved
                        // BEFORE the audit block so the chat firehose exclusion below
                        // covers every platform's chat, not just Twitch's.
                        string? chatPlatform =
                            eventSource == "Twitch"  && eventType == "ChatMessage" ? ChatPlatforms.Twitch :
                            eventSource == "YouTube" && eventType == "Message"     ? ChatPlatforms.YouTube :
                            eventSource == "Kick"    && eventType == "ChatMessage" ? ChatPlatforms.Kick : null;

                        // ── AUDIT LOG ──
                        // privacy + DB-load: chat messages (all platforms) are firehose,
                        // audited separately via chat history. The periodic viewer-count
                        // heartbeats are excluded for the same DB-load reason — they fire
                        // on a timer, carry no user action, and would grow EventLog by
                        // one full-payload row per tick 24/7.
                        bool isHeartbeatEvent =
                            (eventSource == "YouTube" && (eventType == "StatisticsUpdated" || eventType == "PresentViewers")) ||
                            (eventSource == "Kick"    && (eventType == "ViewerCountUpdate" || eventType == "PresentViewers"));
                        if (chatPlatform is null && !isHeartbeatEvent)
                        {
                            EnqueueAuditEvent(eventSource, eventType, "System", json);
                        }

                        // 1. Route to Process Interceptors (Dynamic Caching)
                        ProcessManager.Instance.RouteEventToInterceptors(fullEventType, json);

                        // 2. Handle chat-message logic for the UI and Script Queue.
                        if (chatPlatform is not null)
                        {
                            // A chat event with no usable payload was previously
                            // returned-early silently, which made it look like the message had
                            // been processed when in fact the chat handler never saw it. The
                            // safer behavior is to log + drop with an explicit reason so the
                            // operator can see why a chat event vanished. We do NOT attempt to
                            // synthesize an empty message because downstream `!command` parsing
                            // and OnChatMessage subscribers would treat an empty body as a
                            // legitimate (but malformed) chat line.
                            if (!root.TryGetProperty("data", out var chatDataRoot))
                            {
                                GlobalLogger.Log(
                                    $"{fullEventType} missing data — dropped",
                                    "WS", LogLevel.Communication);
                                return;
                            }

                            ChatMessage msg;
                            string chatLogin;
                            string chatUserId;
                            // Twitch's payload nests the line under data.message; kept for the
                            // Twitch-only broadcaster-actor check further down.
                            System.Text.Json.JsonElement twitchMsgEl = default;

                            if (chatPlatform == ChatPlatforms.Twitch)
                            {
                                if (!chatDataRoot.TryGetProperty("message", out var data))
                                {
                                    GlobalLogger.Log(
                                        "Twitch.ChatMessage missing data.message — dropped",
                                        "WS", LogLevel.Communication);
                                    return;
                                }
                                twitchMsgEl = data;

                                // Role-prefix-badge reliability fix.
                                // Streamer.bot inconsistently populates `data.moderator` / `data.subscriber`
                                // / `data.vip` vs the integer `data.role` (broadcaster=4, mod=3, vip=2,
                                // sub=1, viewer=0). Pre-fix, mods/VIPs whose SB payload used `role:3`
                                // without the booleans collapsed to "Viewer" in `ChatMessage.RoleBadge`
                                // and through the role-color resolver. The reliable behavior is the
                                // UNION: derive each flag from (role-int >= threshold) OR (legacy
                                // boolean field) so either schema variant produces the right badge.
                                // The correct badge for either payload shape is a load-bearing requirement.
                                var roleFlags = ResolveChatRoleFlags(data);
                                msg = new ChatMessage
                                {
                                    Platform      = ChatPlatforms.Twitch,
                                    Username      = data.TryGetProperty("displayName",  out var dn)  ? dn.GetString()  ?? "" : "",
                                    Message       = data.TryGetProperty("message",      out var txt) ? txt.GetString() ?? "" : "",
                                    ColorHex      = data.TryGetProperty("color",        out var col) ? (col.GetString() is { Length: > 0 } c ? c : "#FFFFFF") : "#FFFFFF",
                                    IsBroadcaster = roleFlags.IsBroadcaster,
                                    IsMod         = roleFlags.IsMod,
                                    IsSub         = roleFlags.IsSub,
                                    IsVip         = roleFlags.IsVip,
                                    // Streamer.bot has used at least three field names for the
                                    // cumulative subscription-months counter across releases:
                                    //   `cumMonths` (current SB schema)
                                    //   `cumulativeMonths` (older SB)
                                    //   `cumulative_months` (snake_case payloads from some forks)
                                    // First non-null integer wins. ResolveSubMonths logs a one-time
                                    // Debug warning if all three are missing on a sub gift event so
                                    // we notice payload-shape drift without spamming the log.
                                    SubMonths     = ResolveSubMonths(data, eventType),
                                };
                                // Per-chat [RoleDebug] log was retired. Under busy chat
                                // it burned every slot in the 2000-entry log ring within seconds,
                                // crowding out the System / Critical events Majo actually needs to
                                // see. The role-flag resolution is now exercised by
                                // BugFixSweep7_SovereignWS_Tests; for live diagnosis, log a single
                                // role-tag-map sample on connect (see EmitRoleTagMapDiagnosticOnce).

                                // Match on Twitch `login` + `userId` (immutable) instead
                                // of `displayName` alone. Twitch display names can be stylized or
                                // non-ASCII (e.g. uppercase, Japanese characters) while the login
                                // is always the lowercase ASCII handle. Comparing only displayName
                                // missed every non-ASCII bot account; the guard checks all
                                // three identifiers against the configured BotUsername list.
                                chatLogin = data.TryGetProperty("login", out var loginEl) && loginEl.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? (loginEl.GetString() ?? "")
                                    : (data.TryGetProperty("userLogin", out var ulEl) && ulEl.ValueKind == System.Text.Json.JsonValueKind.String
                                        ? (ulEl.GetString() ?? "")
                                        : "");
                                chatUserId = data.TryGetProperty("userId", out var uidEl) && uidEl.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? (uidEl.GetString() ?? "")
                                    : (data.TryGetProperty("user_id", out var uid2El) && uid2El.ValueKind == System.Text.Json.JsonValueKind.String
                                        ? (uid2El.GetString() ?? "")
                                        : "");
                            }
                            else if (chatPlatform == ChatPlatforms.YouTube)
                            {
                                if (!TryBuildYouTubeChatMessage(chatDataRoot, out msg, out chatLogin, out chatUserId))
                                {
                                    GlobalLogger.Log(
                                        "YouTube.Message carried no usable user/message fields — dropped",
                                        "WS", LogLevel.Communication);
                                    return;
                                }
                            }
                            else
                            {
                                if (!TryBuildKickChatMessage(chatDataRoot, out msg, out chatLogin, out chatUserId))
                                {
                                    GlobalLogger.Log(
                                        "Kick.ChatMessage carried no usable user/message fields — dropped",
                                        "WS", LogLevel.Communication);
                                    return;
                                }
                            }

                            // Bot self-trigger guard runs BEFORE OnChatMessage fires so
                            // the Chat panel doesn't render the bot's own line. The name
                            // list (AppConfig.BotUsername, comma-separated) is platform-
                            // agnostic; the numeric-id checks inside the guard only bind
                            // for platforms whose payload carried an id.
                            if (IsBlockedChatActor(msg.Username, chatLogin, chatUserId))
                            {
                                GlobalLogger.Log(
                                    $"Ignored self-message from bot account (user='{msg.Username}' login='{chatLogin}' userId='{chatUserId}' platform='{msg.Platform}')",
                                    "WS", LogLevel.Debug);
                                return;
                            }

                            // Broadcaster self-fire guard for chat. Off by default
                            // (chat-based testing — "type !command in my own chat to verify"
                            // is a common workflow), but when AppConfig.SuppressBroadcasterChat
                            // is on we route the chat actor through IsBroadcasterActor so a
                            // broadcaster message is dropped before reaching OnChatMessage
                            // subscribers or the script queue. Pre-fix the chat path only
                            // checked IsBlockedAccount against the bot list — broadcaster-as-
                            // actor was silently accepted, which is the right default but
                            // gave users no opt-in to mute it. Mirrors the non-chat
                            // guard's per-event-type toggle pattern.
                            // Twitch keeps the config-identity actor check (BroadcasterUsername /
                            // BroadcasterUserId); YouTube/Kick derive broadcaster-ness from the
                            // payload itself (isOwner / userId==broadcastUserId), set by their mapper.
                            // Config gate FIRST — IsBroadcasterActor allocates per call, and the
                            // feature is off by default, so the actor check must not run per chat
                            // line unless the operator opted in.
                            if (ConfigManager.Current?.SuppressBroadcasterChat == true
                                && (chatPlatform == ChatPlatforms.Twitch
                                        ? IsBroadcasterActor(twitchMsgEl)
                                        : msg.IsBroadcaster))
                            {
                                GlobalLogger.Log(
                                    $"QC36-07 — dropped self-fired {fullEventType} from broadcaster (user='{msg.Username}' login='{chatLogin}' userId='{chatUserId}')",
                                    "WS", LogLevel.Debug);
                                return;
                            }

                            // Per-handler try/catch.
                            // Previously a throwing subscriber severed the rest of the
                            // chain — UI panel, script wait_for_next, etc. — and the
                            // exception bubbled into the WS receive pump. Match Bus's
                            // GetInvocationList pattern so one bad subscriber doesn't
                            // break ingest for the others.
                            var chatHandlers = OnChatMessage?.GetInvocationList();
                            if (chatHandlers is not null)
                            {
                                for (int hi = 0; hi < chatHandlers.Length; hi++)
                                {
                                    try { ((Action<ChatMessage>)chatHandlers[hi])(msg); }
                                    catch (Exception subEx)
                                    {
                                        GlobalLogger.Error("WS", "OnChatMessage subscriber threw", subEx);
                                    }
                                }
                            }
                            RecordRecentChat(msg);

                            // Queue for logic execution to ensure persistence safety.
                            // Bump dropped-message counter visibly so the Diagnostics window
                            // (and the configurable ScriptQueueOverflow event) can surface it.
                            // Drops are still rate-limited via the bounded BlockingCollection.
                            //
                            // Rate-limit the drop log. Under
                            // sustained chat flood the prior code emitted one CriticalError
                            // GlobalLogger.Log per drop, amplifying back-pressure (each Log
                            // call took the GlobalLogger _historyLock + formatted a string,
                            // costing the receive thread budget). Log every 50th drop only;
                            // the dropped-count is still incremented for OnQueueOverflow
                            // subscribers and the counter accessor surfaces the real total.
                            // Shutdown-teardown guard — a chat message can race StopAsync's
                            // CompleteAdding()/Dispose on this receive thread. Without the gate
                            // the TryAdd throws InvalidOperationException ("collection marked as
                            // complete") / ObjectDisposedException, and that unhandled throw on
                            // the receive thread aborts the shutdown coordinator (no "Shutdown
                            // coordinator complete" line). The _stopped flag is the fast path; the
                            // try/catch closes the check→add window. Drop-on-shutdown, not an error.
                            if (System.Threading.Volatile.Read(ref _stopped) == 0 && !_scriptQueue.IsAddingCompleted)
                            {
                                try
                                {
                                    if (!_scriptQueue.TryAdd(msg, 100))
                                    {
                                        long dropped = System.Threading.Interlocked.Increment(ref _droppedQueueMessages);
                                        if (dropped == 1 || dropped % 50 == 0)
                                        {
                                            GlobalLogger.Log(
                                                $"WS: script queue full ({ScriptQueueCapacity}) — dropping chat message (total drops={dropped})",
                                                "WS", LogLevel.CriticalError);
                                        }
                                        OnQueueOverflow?.Invoke(dropped);
                                    }
                                }
                                catch (Exception qEx) when (qEx is InvalidOperationException or ObjectDisposedException)
                                {
                                    // Queue was completed/disposed between the guard and the TryAdd
                                    // (shutdown race) — drop silently; teardown is in progress.
                                }
                            }
                        }
                        // 3. OBS scene change → broadcast to Visualist via Bus
                        //    AND fan out to the script engine for .phx execution.
                        //
                        // ── Dual-routing rationale ────────────────────────────────────
                        // OBS.SceneChanged is the only event we intentionally route down two
                        // independent paths:
                        //
                        //   (a) Bus.BroadcastSceneChangedAsync — the bus path drives
                        //       Visualist overlays. Layers can react to scene changes (e.g.
                        //       fade their root group, swap a preset) without any user-authored
                        //       Script involvement. This is the "platform" channel.
                        //
                        //   (b) ScriptManager.ExecuteGenericEventAsync("OBS.SceneChanged", ...)
                        //       — the script path lets streamers hook scene transitions in
                        //       Script (e.g. trigger a sound, post a chat message, set
                        //       a global var). This is the "user automation" channel.
                        //
                        // We do NOT collapse these into a single bus broadcast because the two
                        // consumers have different lifetimes (Visualist may be offline while
                        // scripts are running) and different envelope shapes (the bus broadcast
                        // is a typed scene-change payload; the script path forwards the raw
                        // SB `data` element so user scripts can inspect any future fields).
                        // ───────────────────────────────────────────────────────────────────
                        else if (eventSource == "OBS" && eventType == "SceneChanged"
                                 && root.TryGetProperty("data", out var sceneData)
                                 && sceneData.TryGetProperty("sceneName", out var sceneName))
                        {
                            string scene = sceneName.GetString() ?? "";
                            _ = AsyncErrorBoundary.SafeRunAsync(
                                () => Bus.Instance.BroadcastSceneChangedAsync(scene),
                                "WS", "scene broadcast");
                            GlobalLogger.Log($"Scene → {scene}", "OBS", LogLevel.StreamEvent);
                            var sceneDataLocal = sceneData;
                            _ = AsyncErrorBoundary.SafeRunAsync(
                                () => ScriptManager.Instance.ExecuteGenericEventAsync("OBS.SceneChanged", sceneDataLocal),
                                "WS", "OBS.SceneChanged script");
                        }
                        // 4. Route all other events to the script engine for .phx execution
                        else if (root.TryGetProperty("data", out var eventData))
                        {
                            // Broadcaster-self-trigger guard for non-chat events.
                            // Before the script engine sees Twitch.Follow / Twitch.PointRedeem
                            // (and the SB-named alias Twitch.RewardRedemption), drop the event
                            // if the actor is the broadcaster account. Without this, an
                            // operator testing their own redeem or a refollow-self bug fires
                            // the script as if a viewer had triggered it.
                            //
                            // Suppression is configurable per event type; defaults to ON for
                            // Follow / PointRedeem because these are the documented self-fire
                            // pain points. Toggle via the AppConfig flags (read by reflection
                            // until they are added to AppConfig.cs):
                            //   SuppressBroadcasterFollow      — Twitch.Follow
                            //   SuppressBroadcasterRedeem      — Twitch.PointRedeem / RewardRedemption
                            if (ShouldSuppressBroadcasterEvent(fullEventType, eventData))
                            {
                                string actorDbg = TryExtractActor(eventData);
                                GlobalLogger.Log(
                                    $"M31 — dropped self-fired {fullEventType} from broadcaster (actor='{actorDbg}')",
                                    "WS", LogLevel.Debug);
                                return;
                            }

                            // Map Streamer.bot event names to Phoenix event types
                            string phoenixEvent = $"{eventSource}.{eventType}";

                            // Raid-viewer spoof warning. Twitch IRC USERNOTICE
                            // / EventSub forward the raider-supplied viewer count
                            // verbatim, and SB's "Test Trigger" UI lets the operator type
                            // any integer. Scripts that gate a reward on
                            // user.viewers > 100 are easily bypassed. We don't change the
                            // trust model (no Helix verification — that's a follow-up),
                            // but we log a Communication-tier warning when the claimed
                            // viewer count is implausibly large so the operator sees
                            // suspicious payloads in the log. Threshold: 10000 viewers is
                            // larger than the top-3 Twitch streamers' typical concurrent
                            // viewership, so it's a reasonable "clearly fake or top-tier
                            // celebrity" tell. A future revision could compare against
                            // configured follower-count instead.
                            if (string.Equals(phoenixEvent, "Twitch.Raid", StringComparison.OrdinalIgnoreCase))
                                WarnIfRaidViewerCountSuspicious(eventData);

                            var eventDataLocal = eventData;
                            _ = AsyncErrorBoundary.SafeRunAsync(
                                () => ScriptManager.Instance.ExecuteGenericEventAsync(phoenixEvent, eventDataLocal),
                                "WS", $"{phoenixEvent} script");
                            string actor = TryExtractActor(eventData);
                            string feedMsg = string.IsNullOrEmpty(actor) ? eventType : $"{eventType} by {actor}";
                            GlobalLogger.Log(feedMsg, eventSource, LogLevel.StreamEvent);
                        }
                        else
                        {
                             GlobalLogger.Log($"WS Message: No handler for event {fullEventType}", "WS", LogLevel.Debug);
                        }
                    }
                    else if (root.TryGetProperty("id", out var respId))
                    {
                        string reqId = respId.GetString() ?? "";
                        if (reqId == AuthRequestId)
                            HandleAuthenticateResponse(root);
                        else if (reqId == GetEventsRequestId)
                            HandleGetEventsResponse(root);
                        else if (_pendingRequests.TryRemove(reqId, out var tcs))
                            tcs.TrySetResult(json);
                    }
                    else
                    {
                        GlobalLogger.Log($"WS Message: Received non-event payload: {json.Substring(0, Math.Min(json.Length, 100))}", "WS", LogLevel.Debug);
                    }
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WS", "WS parse error", ex);
            }
        }

        // Shared bot/broadcaster name set used by chat self-guard
        // and the broadcaster-self-trigger guard. The bot username is
        // operator-configured via AppConfig.BotUsername (comma-separated list
        // for solo setups using both bot and broadcaster accounts).
        // Fast path is a string-equality check against the cache key;
        // when the underlying BotUsername changes (LoadSettings, tests, future
        // hot-reload), the cache is lazily rebuilt under lock.
        internal bool IsBlockedAccount(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            string current = ConfigManager.Current.BotUsername ?? string.Empty;
            HashSet<string> snapshot;
            if (!string.Equals(current, _blockedAccountsKey, StringComparison.Ordinal))
            {
                RebuildBlockedAccountsCache(current);
            }
            lock (_blockedAccountsLock) snapshot = _blockedAccountsCache;
            return snapshot.Contains(username);
        }

        /// <summary>
        /// Bot self-trigger guard for chat events. Twitch displayName is
        /// operator-styled (can be uppercase / non-ASCII), so comparing only that
        /// missed stylized bot accounts. The hardened guard runs a strength-ranked
        /// fallback chain:
        ///   1. STRONGEST: userId == AppConfig.BotUserId (immutable numeric id)
        ///   2. STRONG:    userId == AppConfig.BroadcasterUserId (solo-setup case)
        ///   3. STRONG:    login matches BotUsername list (lowercase ASCII handle)
        ///   4. WEAK:      displayName matches BotUsername list — logged at Debug
        ///                 so operators can see when the styled-name fallback was
        ///                 the only thing that fired (suggests BotUserId should be
        ///                 configured).
        /// Empty inputs are skipped (a payload that omits the field can't match).
        /// </summary>
        internal bool IsBlockedChatActor(string displayName, string login, string userId)
        {
            var cfg = ConfigManager.Current;

            // 1. STRONGEST — immutable bot userId.
            if (!string.IsNullOrWhiteSpace(userId))
            {
                string botId = cfg?.BotUserId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(botId)
                    && string.Equals(botId, userId, StringComparison.Ordinal))
                    return true;

                // 2. STRONG — broadcaster numeric id (solo-setup case).
                string bcastId = cfg?.BroadcasterUserId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(bcastId)
                    && string.Equals(bcastId, userId, StringComparison.Ordinal))
                    return true;
            }

            // 3. STRONG — login is the lowercase ASCII handle; safe equality.
            if (!string.IsNullOrWhiteSpace(login) && IsBlockedAccount(login))
                return true;

            // 4. WEAK — displayName fallback. Preserves the prior behavior so
            //    ASCII-name bots whose configured BotUsername equals the display
            //    name still match, but logs a Debug breadcrumb so the operator
            //    can see the weak match and add a BotUserId/login to upgrade it.
            if (!string.IsNullOrWhiteSpace(displayName) && IsBlockedAccount(displayName))
            {
                GlobalLogger.Log(
                    $"QC06-03 — bot guard matched on displayName='{displayName}' only " +
                    "(no login/userId match). Configure AppConfig.BotUserId for a stronger match.",
                    "WS", LogLevel.Debug);
                return true;
            }

            return false;
        }

        // ── Multi-platform chat mappers ─────────────────────────────────────
        // Streamer.bot's WS payload schemas for YouTube/Kick are unpublished, so
        // both mappers probe defensively — nested actor objects first (YouTube
        // nests the author under data.user; Kick has been observed flat), then
        // flat fallbacks — and return false only when neither a user nor a
        // message could be extracted. internal static for unit tests.

        /// <summary>
        /// YouTube.Message → ChatMessage. Official Streamer.bot client typing:
        /// { eventId, message, publishedAt, user: { id, name, profileImageUrl,
        ///   isModerator, isOwner, isSponsor, isVerified } }.
        /// </summary>
        internal static bool TryBuildYouTubeChatMessage(System.Text.Json.JsonElement data, out ChatMessage msg, out string login, out string userId)
        {
            msg = new ChatMessage { Platform = ChatPlatforms.YouTube };
            login = string.Empty;
            userId = string.Empty;
            if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

            bool hasUser = data.TryGetProperty("user", out var user)
                && user.ValueKind == System.Text.Json.JsonValueKind.Object;

            string name = hasUser ? JsonStringField(user, "name") : string.Empty;
            if (name.Length == 0)
                name = FirstNonEmpty(JsonStringField(data, "displayName"), JsonStringField(data, "user"), JsonStringField(data, "userName"));

            string text = JsonStringField(data, "message");
            if (name.Length == 0 && text.Length == 0) return false;

            msg.Username = name;
            msg.Message  = text;
            if (hasUser)
            {
                msg.IsMod         = JsonBoolField(user, "isModerator");
                msg.IsBroadcaster = JsonBoolField(user, "isOwner");
                msg.IsSub         = JsonBoolField(user, "isSponsor");
                userId            = JsonStringField(user, "id");
            }
            else
            {
                msg.IsMod         = JsonBoolField(data, "isModerator");
                msg.IsBroadcaster = JsonBoolField(data, "isOwner");
                msg.IsSub         = JsonBoolField(data, "isSponsor") || JsonBoolField(data, "userIsSponsor");
                userId            = JsonStringField(data, "userId");
            }
            // YouTube has no separate lowercase handle — the display name doubles
            // as the bot-guard login key.
            login = name;
            return true;
        }

        /// <summary>
        /// Kick.ChatMessage → ChatMessage. Documented trigger-variable surface is
        /// flat (user / userName / userId, message, color, isSubscribed,
        /// isModerator, broadcastUserId); a Twitch-style nested data.message
        /// object and a nested data.user object are tolerated as fallbacks.
        /// </summary>
        internal static bool TryBuildKickChatMessage(System.Text.Json.JsonElement data, out ChatMessage msg, out string login, out string userId)
        {
            msg = new ChatMessage { Platform = ChatPlatforms.Kick };
            login = string.Empty;
            userId = string.Empty;
            if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

            var body = data;
            string text = string.Empty;
            if (data.TryGetProperty("message", out var msgEl))
            {
                if (msgEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    body = msgEl;
                    text = FirstNonEmpty(JsonStringField(body, "message"), JsonStringField(body, "content"));
                }
                else if (msgEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    text = msgEl.GetString() ?? string.Empty;
                }
            }

            bool hasUser = (body.TryGetProperty("user", out var user)
                            && user.ValueKind == System.Text.Json.JsonValueKind.Object)
                        || (data.TryGetProperty("user", out user)
                            && user.ValueKind == System.Text.Json.JsonValueKind.Object);

            string name = hasUser
                ? FirstNonEmpty(JsonStringField(user, "name"), JsonStringField(user, "username"), JsonStringField(user, "displayName"))
                : string.Empty;
            if (name.Length == 0)
                name = FirstNonEmpty(
                    JsonStringField(body, "user"), JsonStringField(body, "userName"), JsonStringField(body, "displayName"),
                    JsonStringField(data, "user"), JsonStringField(data, "userName"), JsonStringField(data, "displayName"));
            if (name.Length == 0 && text.Length == 0) return false;

            login = FirstNonEmpty(
                hasUser ? JsonStringField(user, "login") : string.Empty,
                JsonStringField(body, "userName"), JsonStringField(data, "userName"), name);
            userId = FirstNonEmpty(
                hasUser ? JsonStringField(user, "id") : string.Empty,
                JsonStringField(body, "userId"), JsonStringField(data, "userId"));

            msg.Username = name;
            msg.Message  = text;
            msg.IsMod = JsonBoolField(body, "isModerator") || JsonBoolField(data, "isModerator")
                     || (hasUser && JsonBoolField(user, "isModerator"));
            msg.IsSub = JsonBoolField(body, "isSubscribed") || JsonBoolField(data, "isSubscribed")
                     || (hasUser && JsonBoolField(user, "isSubscribed"));
            string broadcasterId = FirstNonEmpty(JsonStringField(body, "broadcastUserId"), JsonStringField(data, "broadcastUserId"));
            msg.IsBroadcaster = userId.Length > 0 && string.Equals(userId, broadcasterId, StringComparison.Ordinal);
            string color = FirstNonEmpty(JsonStringField(body, "color"), JsonStringField(data, "color"));
            if (color.Length > 0) msg.ColorHex = color;
            return true;
        }

        private static string JsonStringField(System.Text.Json.JsonElement obj, string name)
        {
            if (obj.ValueKind != System.Text.Json.JsonValueKind.Object) return string.Empty;
            if (!obj.TryGetProperty(name, out var el)) return string.Empty;
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => el.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.Number => el.GetRawText(),
                _ => string.Empty,
            };
        }

        private static bool JsonBoolField(System.Text.Json.JsonElement obj, string name)
        {
            if (obj.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!obj.TryGetProperty(name, out var el)) return false;
            return el.ValueKind == System.Text.Json.JsonValueKind.True
                || (el.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(el.GetString(), out var b) && b);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v)) return v;
            return string.Empty;
        }

        // Actor-vs-broadcaster check for non-chat events.
        // The actor's name is read from the SB event payload (displayName /
        // userName / user_name / login / userLogin), and the actor's twitch id
        // from (userId / user_id). Either one matching the configured broadcaster
        // identity is enough.
        internal bool IsBroadcasterActor(System.Text.Json.JsonElement data)
        {
            // Names: collect everything we can find on the payload.
            var actorNames = new System.Collections.Generic.List<string>();
            void TryAddName(string key)
            {
                if (data.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) actorNames.Add(s!);
                }
            }
            TryAddName("displayName");
            TryAddName("userName");
            TryAddName("user_name");
            TryAddName("login");
            TryAddName("userLogin");

            // Id: SB's RewardRedemption / Follow payloads carry userId or user_id.
            string? actorId = null;
            if (data.TryGetProperty("userId", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String)
                actorId = idEl.GetString();
            else if (data.TryGetProperty("user_id", out var idEl2) && idEl2.ValueKind == System.Text.Json.JsonValueKind.String)
                actorId = idEl2.GetString();

            // Compare to configured broadcaster identity.
            // Direct AppConfig property reads (was reflection-via-helper).
            var cfg = ConfigManager.Current;
            string? bcastName = cfg?.BroadcasterUsername;
            string? bcastId   = cfg?.BroadcasterUserId;

            if (!string.IsNullOrWhiteSpace(bcastId) && !string.IsNullOrWhiteSpace(actorId)
                && string.Equals(bcastId, actorId, StringComparison.Ordinal))
                return true;

            foreach (var n in actorNames)
            {
                if (!string.IsNullOrWhiteSpace(bcastName)
                    && string.Equals(bcastName, n, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Reuse the chat self-guard so the BotUsername list (which the
                // operator may also use as the broadcaster acct in solo setups)
                // suppresses these events too.
                if (IsBlockedAccount(n))
                    return true;
            }
            return false;
        }

        // Surface a Communication-tier warning when an incoming
        // Twitch.Raid event claims an implausibly large viewer count. SB
        // forwards the count verbatim from Twitch's USERNOTICE /
        // EventSub envelope, which is broadcaster-controlled at the raider
        // side and trivially editable from SB's Test Trigger UI. We do NOT
        // change the trust model here (no Helix cross-check — deferred as a
        // follow-up). Threshold 10000 is "above any plausible non-celebrity
        // raid"; below that we stay silent to avoid log noise on a real big
        // raid. Errors are swallowed — the warning is best-effort observability.
        private const long RaidViewerSpoofWarnThreshold = 10000;
        private static void WarnIfRaidViewerCountSuspicious(System.Text.Json.JsonElement data)
        {
            try
            {
                if (!data.TryGetProperty("viewers", out var viewersEl)) return;
                long viewers;
                switch (viewersEl.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Number:
                        if (!viewersEl.TryGetInt64(out viewers)) return;
                        break;
                    case System.Text.Json.JsonValueKind.String:
                        if (!long.TryParse(viewersEl.GetString(), out viewers)) return;
                        break;
                    default:
                        return;
                }
                if (viewers <= RaidViewerSpoofWarnThreshold) return;
                GlobalLogger.Log(
                    $"QC36-06 — Twitch.Raid viewer count {viewers} exceeds plausibility threshold ({RaidViewerSpoofWarnThreshold}); " +
                    "viewer count is broadcaster-controlled and spoofable. Validate before granting raid-gated rewards.",
                    "WS", LogLevel.Communication);
            }
            catch { /* best-effort observability — never block dispatch */ }
        }

        // Gate: combines per-event-type config toggles with the
        // broadcaster-actor identity check. Defaults are ON for Follow /
        // PointRedeem / RewardRedemption per the brief.
        internal bool ShouldSuppressBroadcasterEvent(string fullEventType, System.Text.Json.JsonElement data)
        {
            // Direct AppConfig property reads (was reflection-via-helper).
            var cfg = ConfigManager.Current;
            bool eligible;
            switch (fullEventType)
            {
                case "Twitch.Follow":
                    eligible = cfg?.SuppressBroadcasterFollow ?? true;
                    break;
                case "Twitch.PointRedeem":
                case "Twitch.RewardRedemption":
                    eligible = cfg?.SuppressBroadcasterRedeem ?? true;
                    break;
                default:
                    return false;
            }
            if (!eligible) return false;
            return IsBroadcasterActor(data);
        }

        private static string TryExtractActor(System.Text.Json.JsonElement data)
        {
            if (data.TryGetProperty("user", out var u))
            {
                if (u.TryGetProperty("name", out var n) && n.GetString() is string name && name.Length > 0)
                    return name;
                if (u.TryGetProperty("login", out var l) && l.GetString() is string login && login.Length > 0)
                    return login;
            }
            // Fallback: SB flattens user fields onto data on most non-chat events.
            if (data.TryGetProperty("displayName", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = dn.GetString();
                if (!string.IsNullOrEmpty(s)) return s!;
            }
            if (data.TryGetProperty("userName", out var un) && un.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = un.GetString();
                if (!string.IsNullOrEmpty(s)) return s!;
            }
            return "";
        }

        // Append a message to the most-recent ring buffer feeding Chat.PeekRecent.
        // Capacity-bounded; oldest entries drop on overflow.
        private void RecordRecentChat(ChatMessage msg)
        {
            lock (_recentChatLock)
            {
                _recentChatBuffer.AddLast(msg);
                while (_recentChatBuffer.Count > RecentChatCapacity)
                    _recentChatBuffer.RemoveFirst();
            }
        }

        /// <summary>
        /// Snapshot of the most recent N chat messages, oldest-to-newest.
        /// N ≤ 0 returns the full ring (capped by RecentChatCapacity).
        /// Independent of the script-execution queue — peek does not consume.
        /// </summary>
        public IReadOnlyList<ChatMessage> PeekRecentChat(int n)
        {
            lock (_recentChatLock)
            {
                if (_recentChatBuffer.Count == 0) return System.Array.Empty<ChatMessage>();
                int take = (n <= 0 || n >= _recentChatBuffer.Count) ? _recentChatBuffer.Count : n;
                var result = new System.Collections.Generic.List<ChatMessage>(take);
                int skip = _recentChatBuffer.Count - take;
                int i = 0;
                foreach (var m in _recentChatBuffer)
                {
                    if (i++ < skip) continue;
                    result.Add(m);
                }
                return result;
            }
        }

        /// <summary>
        /// Awaits the next chat line that matches the given filters, up to <paramref name="timeoutMs"/>.
        /// Empty filters mean "any". <paramref name="commandFilter"/> matches `!{cmd}` at message start
        /// (case-insensitive); leading `!` is stripped from the filter for the user. Returns null on
        /// timeout. Implementation is one-shot: an OnChatMessage handler is attached for the duration
        /// of the wait and removed on completion / timeout / cancel — no backing dictionary, no risk
        /// of stale subscribers leaking across script runs.
        /// </summary>
        public async Task<ChatMessage?> WaitForNextChatAsync(
            string? userFilter, string? commandFilter, int timeoutMs, CancellationToken ct = default)
        {
            // RunContinuationsAsynchronously so resolving the wait inside OnChatMessage
            // doesn't run the script's continuation on the chat-receive thread.
            var tcs = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? cmdNoBang = string.IsNullOrWhiteSpace(commandFilter)
                ? null
                : commandFilter.TrimStart('!').Trim();

            void Handler(ChatMessage msg)
            {
                if (!string.IsNullOrEmpty(userFilter)
                    && !string.Equals(msg.Username, userFilter, StringComparison.OrdinalIgnoreCase))
                    return;
                if (!string.IsNullOrEmpty(cmdNoBang))
                {
                    if (!msg.Message.StartsWith("!" + cmdNoBang, StringComparison.OrdinalIgnoreCase))
                        return;
                    // Require a word boundary so `!he` doesn't match `!hello`.
                    int after = 1 + cmdNoBang.Length;
                    if (msg.Message.Length > after && msg.Message[after] != ' ' && msg.Message[after] != '\t')
                        return;
                }
                tcs.TrySetResult(msg);
            }

            OnChatMessage += Handler;
            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = Task.Delay(timeoutMs, delayCts.Token);
                var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
                try { delayCts.Cancel(); } catch { }
                if (completed != tcs.Task)
                {
                    tcs.TrySetCanceled();
                    ct.ThrowIfCancellationRequested();
                    return null;
                }
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                OnChatMessage -= Handler;
            }
        }

        private async Task ProcessScriptQueueAsync()
        {
            // Wrap the GetConsumingEnumerable foreach in an outer
            // try/catch. The pre-fix try/catch sat INSIDE the foreach, so an
            // exception from GetConsumingEnumerable itself (e.g. the
            // settings-reload race where Dispose() runs on the
            // BlockingCollection mid-enumeration → ObjectDisposedException) would
            // terminate the consumer silently. With _consumerStarted pinned at 1
            // by Initialize(), the next Initialize() call would skip restart and
            // every subsequent chat message would queue forever with no consumer
            // to drain.
            //
            // Fix: outer try/catch logs the fault and resets _consumerStarted to
            // 0 so the next Initialize() restart spawns a fresh consumer task on
            // the fresh _scriptQueue.
            try
            {
                foreach (var msg in _scriptQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (msg.Message.StartsWith("!"))
                        {
                            string cmdName = msg.Message.Split(' ')[0].Substring(1).ToLower();
                            if (!string.IsNullOrWhiteSpace(cmdName))
                            {
                                // Session.* / SessionManager retired: spawning a Process via
                                // process_spawn is now the unified async-spawn primitive,
                                // and chat-driven participation tracking — if any future
                                // graph needs it — should ride a User_Sessions databank
                                // table or a Process's own state, not a separate manager.
                                // Stays at StreamEvent — LiveFeedSource keys on
                                // Level==StreamEvent and uses this line as the
                                // chat-command surface for the curated feed.
                                // Demoting to Debug to silence the SystemLog
                                // killed the LiveFeed for chat commands.
                                GlobalLogger.Log($"!{cmdName} by {msg.Username}", "Chat", LogLevel.StreamEvent);
                            }
                        }

                        // Run all .phx files that have an on_chat: block (any exported ChatMessage graph)
                        await ScriptManager.Instance.ExecuteOnChatScriptsAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("WS", "script queue execution failed", ex);
                    }
                }
            }
            catch (Exception outerEx)
            {
                GlobalLogger.Error(
                    "WS",
                    "ProcessScriptQueueAsync consumer foreach terminated by unhandled exception — _consumerStarted reset so the next Initialize() spawns a fresh consumer.",
                    outerEx);
                // Reset the consumer guard so the next Initialize() restart can
                // spin up a new consumer task against the fresh _scriptQueue.
                Interlocked.Exchange(ref _consumerStarted, 0);
            }
        }

        // Producer side of the audit coalescer (see the field block for the
        // full rationale). TryWrite never blocks: on a full channel DropOldest
        // evicts the oldest pending row, on a completed channel (post-Stop)
        // the write is dropped — matching the old fire-and-forget semantics
        // where a shutdown-window write could vanish. The pump is started
        // lazily on first write, CAS-gated so exactly one runs per channel.
        private void EnqueueAuditEvent(string source, string type, string user, string payload)
        {
            var channel = _auditChannel;
            var row = new AuditRow(
                Interlocked.Increment(ref _auditEnqueueSeq), source, type, user, payload);
            if (!channel.Writer.TryWrite(row))
                return;

            if (Interlocked.CompareExchange(ref _auditPumpStarted, 1, 0) == 0)
            {
                // Capture the channel instance so a Stop→Initialize swap can't
                // leave the pump draining a different channel than it was
                // started for.
                _auditPumpTask = AsyncErrorBoundary.SafeRunAsync(
                    () => PumpAuditEventsAsync(channel),
                    "WS", "audit log batch pump");
            }
        }

        // Single consumer: waits for the first row, then lingers up to
        // AuditFlushMs so a burst coalesces, then flushes everything gathered
        // (capped at AuditBatchSize per transaction). WaitToReadAsync returns
        // false only when the writer completes (StopAsync), after which the
        // trailing drain flushes whatever queued between the last read and
        // completion.
        private async Task PumpAuditEventsAsync(Channel<AuditRow> channel)
        {
            var reader = channel.Reader;
            var batch = new List<(string Source, string Type, string User, string Payload)>(AuditBatchSize);
            // Eviction accounting: consecutive Seq values gap exactly by the
            // number of rows DropOldest discarded. -1 = first read initializes
            // (the counter is process-wide, so a pump on a fresh channel after
            // Stop→Initialize must not count the seam as a gap).
            long expectedSeq = -1;
            long evictedThisFlush = 0;

            bool Take(AuditRow row)
            {
                if (expectedSeq >= 0 && row.Seq != expectedSeq)
                    evictedThisFlush += row.Seq - expectedSeq;
                expectedSeq = row.Seq + 1;
                batch.Add((row.Source, row.Type, row.User, row.Payload));
                return true;
            }

            async Task FlushAsync()
            {
                if (evictedThisFlush > 0)
                {
                    long total = Interlocked.Add(ref _auditEvictedTotal, evictedThisFlush);
                    GlobalLogger.Log(
                        $"EventLog audit backlog overflowed — {evictedThisFlush} oldest rows evicted " +
                        $"before persistence ({total} total this session). The DB is not keeping up.",
                        "WS", LogLevel.CriticalError);
                    evictedThisFlush = 0;
                }
                await FlushAuditBatchAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }

            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < AuditBatchSize && reader.TryRead(out var row))
                    Take(row);

                if (batch.Count < AuditBatchSize)
                {
                    // Below the size trigger — wait out the flush interval so
                    // trailing burst rows ride the same transaction. The delay
                    // bounds row visibility at ~AuditFlushMs, well under the
                    // EventLog panel's 2 Hz poll.
                    await Task.Delay(AuditFlushMs).ConfigureAwait(false);
                    while (batch.Count < AuditBatchSize && reader.TryRead(out var row))
                        Take(row);
                }

                await FlushAsync().ConfigureAwait(false);
            }

            // Writer completed — shutdown drain.
            while (reader.TryRead(out var row))
            {
                Take(row);
                if (batch.Count >= AuditBatchSize)
                    await FlushAsync().ConfigureAwait(false);
            }
            await FlushAsync().ConfigureAwait(false);
        }

        private static async Task FlushAuditBatchAsync(
            List<(string Source, string Type, string User, string Payload)> batch)
        {
            if (batch.Count == 0) return;
            try
            {
                await DB.Instance.LogEventsBatchAsync(batch).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A single poison row faults the whole transaction — retry the
                // rows individually so the healthy ones still land, restoring
                // the per-row loss granularity of the old per-event writes. A
                // row that fails again is dropped silently (best-effort audit
                // surface; per-row logging would amplify a wedged DB).
                GlobalLogger.Error("WS", "audit log batch write failed", ex);
                foreach (var (source, type, user, payload) in batch)
                {
                    try
                    {
                        await DB.Instance.LogEventAsync(source, type, user, payload).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public async Task StartAsync()
        {
            if (_client == null)
            {
                GlobalLogger.Log("WS.StartAsync called before Initialize() — aborting.", "WS", LogLevel.CriticalError);
                return;
            }
            GlobalLogger.Log("Initializing WebSocket link...", "WS");
            await _client.Start();
        }

        private int _stopped;

        /// <summary>
        /// Async teardown so a UI-thread shutdown path (HubBootstrapper.
        /// ShutdownAsync, future Hub close handlers) doesn't block on the
        /// Websocket.Client's synchronous Dispose. The library's Dispose path
        /// joins its internal worker thread, which can take up to ~3 s on a
        /// half-open socket — long enough to register as a freeze in WinUI's
        /// dispatcher health metrics and trigger the "not responding" overlay
        /// on the closing splash.
        ///
        /// The expensive parts (the Rx subscription disposes and the
        /// WebsocketClient.Dispose) are offloaded to a Task.Run so the caller's
        /// continuation resumes on the original sync context only after the
        /// blocking work is off the UI thread. All other state mutation
        /// (CompleteAdding, _pendingRequests cancellation, IsConnected flip)
        /// stays inline because it's microsecond-cheap.
        ///
        /// Idempotent — safe to call from FormClosing even if Start() never ran.
        /// </summary>
        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

            try { _scriptQueue.CompleteAdding(); } catch { }

            foreach (var (_, tcs) in _pendingRequests) tcs.TrySetCanceled();
            _pendingRequests.Clear();

            // Wait for ProcessScriptQueueAsync to exit before
            // disposing _scriptQueue. CompleteAdding above unblocks the
            // GetConsumingEnumerable foreach when the queue drains; the loop
            // then returns naturally. Without the drain, Dispose() could race
            // an in-flight iteration and surface ObjectDisposedException from
            // inside ScriptManager.ExecuteOnChatScriptsAsync. Bounded wait so a
            // wedged script handler can't pin shutdown indefinitely.
            var consumer = _consumerTask;
            if (consumer is not null)
            {
                try
                {
                    var drainTimeout = Task.Delay(TimeSpan.FromSeconds(3));
                    await Task.WhenAny(consumer, drainTimeout).ConfigureAwait(false);
                    if (!consumer.IsCompleted)
                    {
                        GlobalLogger.Log(
                            "WS.Stop: script-queue consumer did not drain within 3s — disposing the queue anyway. In-flight chat scripts may surface ObjectDisposedException.",
                            "WS", LogLevel.Communication);
                    }
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("WS", "consumer drain in Stop() faulted", ex);
                }
            }

            // Dispose the BlockingCollection so its underlying
            // ConcurrentQueue + waiters are released. The consumer loop has either
            // exited (drain above) or timed out; either way no further
            // iteration is in flight from this side.
            try { _scriptQueue.Dispose(); } catch { }

            IsConnected = false;

            // Offload the potentially blocking Dispose calls so the caller's
            // SyncContext (UI thread) is unblocked while the library joins its
            // worker. ConfigureAwait(false) since none of the post-await work
            // touches UI state.
            await Task.Run(() =>
            {
                try { _reconnectionSub?.Dispose(); }    catch { }
                try { _disconnectionSub?.Dispose(); }   catch { }
                try { _messageReceivedSub?.Dispose(); } catch { }

                // Lock so a concurrent Send can't see a half-disposed client. Send takes
                // the same lock and re-checks _client / IsRunning under it.
                lock (_clientLock)
                {
                    try { _client?.Dispose(); } catch (Exception ex) { GlobalLogger.Error("WS", "client dispose error", ex); }
                }

                // Dispose the BlockingCollection so its underlying
                // ConcurrentQueue + waiters are released. The consumer loop has already
                // exited via CompleteAdding above; calling Dispose after consumers drain
                // is safe.
                try { _scriptQueue.Dispose(); } catch { }
            }).ConfigureAwait(false);

            // Flush the audit coalescer LAST — after the client dispose — so
            // frames received right up to the socket teardown still get their
            // audit rows enqueued before the writer completes. Completing the
            // writer wakes the pump out of WaitToReadAsync/its flush linger;
            // it drains the remainder and exits. Bounded wait mirrors the
            // script-queue drain above so a stalled DB can't pin shutdown.
            Interlocked.Exchange(ref _auditChannelCompleted, 1);
            try { _auditChannel.Writer.TryComplete(); } catch { }
            var auditPump = _auditPumpTask;
            if (auditPump is not null)
            {
                try
                {
                    var flushTimeout = Task.Delay(TimeSpan.FromSeconds(3));
                    await Task.WhenAny(auditPump, flushTimeout).ConfigureAwait(false);
                    if (!auditPump.IsCompleted)
                    {
                        GlobalLogger.Log(
                            "WS.Stop: audit-log pump did not flush within 3s — unflushed audit rows may be lost.",
                            "WS", LogLevel.Communication);
                    }
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("WS", "audit pump drain in Stop() faulted", ex);
                }
            }
        }

        /// <summary>
        /// Synchronous compatibility facade for callers that can't yet await
        /// StopAsync (none today; this is here for future-proofing). Off-thread
        /// .GetResult() avoids the UI-thread block of the previous in-process
        /// Dispose path, but new code should prefer awaiting StopAsync directly.
        /// </summary>
        public void Stop() => Task.Run(StopAsync).GetAwaiter().GetResult();

        public async Task<string> SendAndWaitAsync(string json, string requestId, int timeoutMs = 5000)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;
            Send(json);
            using var cts = new CancellationTokenSource(timeoutMs);
            // On timeout: remove the entry BEFORE completing the TCS so a late
            // response can't TryRemove a stale entry and call TrySetResult on a
            // TCS whose awaiter has already returned. Also prevents a slow
            // memory leak of stale entries per timed-out request.
            cts.Token.Register(() =>
            {
                _pendingRequests.TryRemove(requestId, out _);
                tcs.TrySetResult("");
            });
            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Generates a stable, short, collision-resistant request id for SB correlation.
        /// All callers awaiting an SB response should use this so the format stays uniform
        /// in logs and the dispatcher.
        ///
        /// The default prefix is "phx-" (Phoenix Controls), flipped from the legacy
        /// "sov-" brand drift. Streamer.bot treats request ids as
        /// opaque correlation keys; no integration depends on the literal prefix.
        /// </summary>
        public static string NewRequestId(string prefix = "phx")
            => $"{prefix}-{Guid.NewGuid().ToString("N").Substring(0, 12)}";

        /// <summary>
        /// Sends an SB request and awaits a correlated response, returning the parsed root
        /// JsonElement (or null on timeout / parse failure). The caller is expected to have
        /// embedded the requestId into the JSON's "id" field — mirror the existing
        /// twitch.get_viewers pattern in ScriptManager.
        ///
        /// On timeout SendAndWaitAsync resolves to "" (empty string sentinel) — we surface
        /// that as null so callers don't have to know the sentinel value.
        /// </summary>
        public async Task<System.Text.Json.JsonElement?> SendAndWaitJsonAsync(
            string json, string requestId, int timeoutMs = 5000)
        {
            string response = await SendAndWaitAsync(json, requestId, timeoutMs).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response)) return null;
            try
            {
                // JsonDocument is IDisposable but the cloned RootElement detaches the value
                // from the document so the caller can use it freely after we dispose. Without
                // the clone, returning the live RootElement would be a use-after-dispose.
                using var doc = System.Text.Json.JsonDocument.Parse(response);
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WS", $"SendAndWaitJsonAsync parse error (req={requestId})", ex);
                return null;
            }
        }

        public void Send(string message)
        {
            // Lock against concurrent Initialize/Stop so the client can't be Dispose()d
            // between our IsRunning check and Send call. Without this, ObjectDisposedException
            // can leak out of the public Send surface during shutdown or settings reload.
            lock (_clientLock)
            {
                // Guard against the pre-Initialize NRE. `_client = null!` in the
                // ctor sets the trap; only Initialize() populates the field. Public
                // surface = callable from outside, so the safe response is to log + drop
                // (mirroring the !IsRunning branch below) instead of crashing the caller.
                if (_client == null)
                {
                    GlobalLogger.Log(
                        $"WS→SB DROPPED (not initialized): {message}",
                        "WS", LogLevel.CriticalError);
                    return;
                }
                if (_client.IsRunning)
                {
                    try { _client.Send(message); }
                    catch (ObjectDisposedException)
                    {
                        // Race window: Stop completed between IsRunning and Send. Treat as drop.
                        GlobalLogger.Log($"WS→SB DROPPED (client disposed mid-send): {message}", "WS", LogLevel.CriticalError);
                    }
                }
                else
                {
                    GlobalLogger.Log($"WS→SB DROPPED (not connected): {message}", "WS", LogLevel.CriticalError);
                }
            }
        }

        // The legacy `RoleDebugLogging` compile-time gate has been
        // removed alongside the per-chat [RoleDebug] log it controlled. The
        // role-flag resolver is covered by unit tests, and a one-shot
        // role-tag-map sample fires from the reconnect callback for live
        // diagnosis (see the connect handler).

        /// <summary>
        /// Derives the four chat role flags from a Streamer.bot
        /// chat payload using the union of (role-int threshold) and (legacy boolean
        /// field). Either schema variant on its own produces the correct badge.
        ///
        /// Streamer.bot's role integers (when present):
        ///   0 = viewer, 1 = subscriber, 2 = VIP, 3 = moderator, 4 = broadcaster.
        /// The boolean fields (when present) are <c>moderator</c> / <c>subscriber</c> /
        /// <c>vip</c>. Streamer.bot does not ship a `broadcaster` boolean — only the
        /// role-int conveys that identity.
        ///
        /// The fix is load-bearing: the
        /// previous code read each boolean in isolation, so a payload that used
        /// <c>role:3</c> without booleans collapsed a moderator to "Viewer".
        /// </summary>
        internal readonly struct ChatRoleFlags
        {
            public readonly bool IsBroadcaster;
            public readonly bool IsMod;
            public readonly bool IsVip;
            public readonly bool IsSub;
            public ChatRoleFlags(bool bc, bool mod, bool vip, bool sub)
            { IsBroadcaster = bc; IsMod = mod; IsVip = vip; IsSub = sub; }
        }

        internal static ChatRoleFlags ResolveChatRoleFlags(System.Text.Json.JsonElement data)
        {
            int role = 0;
            if (data.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == System.Text.Json.JsonValueKind.Number
                && roleEl.TryGetInt32(out int rv))
                role = rv;

            bool modBool = data.TryGetProperty("moderator",  out var modEl) && modEl.ValueKind == System.Text.Json.JsonValueKind.True;
            bool subBool = data.TryGetProperty("subscriber", out var subEl) && subEl.ValueKind == System.Text.Json.JsonValueKind.True;
            bool vipBool = data.TryGetProperty("vip",        out var vipEl) && vipEl.ValueKind == System.Text.Json.JsonValueKind.True;

            bool isBroadcaster = role >= 4;
            // Higher tiers imply lower ones via the role-int ladder. Booleans fill in
            // payloads that omit the role field entirely (older SB versions).
            bool isMod = isBroadcaster || role >= 3 || modBool;
            bool isVip = role >= 2 || vipBool;
            bool isSub = role >= 1 || subBool;
            return new ChatRoleFlags(isBroadcaster, isMod, isVip, isSub);
        }

        // One-shot latch so reconnect storms don't multiply the
        // role-tag-map diagnostic. Reset on Initialize so settings reload
        // logs it again, which is a useful "I changed config and reconnected"
        // breadcrumb.
        private int _roleTagMapLogged;

        private void EmitRoleTagMapDiagnosticOnce()
        {
            if (Interlocked.Exchange(ref _roleTagMapLogged, 1) != 0) return;
            GlobalLogger.Log(
                "Role tag map: SB role-int 0=viewer / 1=subscriber / 2=VIP / 3=moderator / 4=broadcaster. " +
                "Flags derive from (role-int >= threshold) OR legacy booleans (moderator/subscriber/vip). " +
                "Example: payload {role:3} → IsMod=true; payload {moderator:true} (no role-int) → IsMod=true.",
                "WS", LogLevel.Debug);
        }

        // One-time-warning latch for missing cumulative-months fields on
        // sub gift events. Process-lifetime flag (not per-payload) so a noisy
        // upstream payload-shape change logs once and stops, rather than
        // flooding the Debug log on every gift sub.
        private static int _subMonthsMissingWarned;

        /// <summary>
        /// Defensive cumulative-subscription-months parser. Streamer.bot has shipped
        /// at least three different field names for this counter across releases:
        /// <c>cumMonths</c>, <c>cumulativeMonths</c>, and <c>cumulative_months</c>.
        /// First non-null integer wins. Returns 0 if none are present (preserving
        /// the old behavior). Logs a one-time Debug warning if all three are missing
        /// on an event that's expected to carry the field (sub-gift family).
        /// </summary>
        private static int ResolveSubMonths(System.Text.Json.JsonElement data, string eventType)
        {
            // Each candidate is tried in declared priority order: current SB → older SB → snake_case fork.
            string[] candidates = { "cumMonths", "cumulativeMonths", "cumulative_months" };
            foreach (var key in candidates)
            {
                if (data.TryGetProperty(key, out var el) && el.TryGetInt32(out int months))
                    return months;
            }

            // Only worth warning about for sub-shaped events; chat lines from non-subs
            // legitimately omit the field and we don't want to flood the log.
            bool isSubFamily =
                eventType.Equals("Sub", StringComparison.OrdinalIgnoreCase) ||
                eventType.Equals("Resub", StringComparison.OrdinalIgnoreCase) ||
                eventType.Equals("GiftSub", StringComparison.OrdinalIgnoreCase) ||
                eventType.Equals("GiftBomb", StringComparison.OrdinalIgnoreCase);

            if (isSubFamily &&
                System.Threading.Interlocked.Exchange(ref _subMonthsMissingWarned, 1) == 0)
            {
                GlobalLogger.Log(
                    $"Sub event '{eventType}' missing cumMonths / cumulativeMonths / cumulative_months — payload shape may have drifted (one-time warning).",
                    "WS", LogLevel.Debug);
            }
            return 0;
        }

        // Twitch / YouTube / Kick / OBS / General event subscription set.
        //
        // Single source of truth so Subscribe and UnSubscribe stay in lockstep
        // (a drift here used to leak subscriptions on the Streamer.bot side
        // every Hub restart).
        //
        //   Twitch:  the well-documented v0.2-era subset (unchanged).
        //   YouTube: the full Streamer.bot 1.0.x event source (31 names;
        //            UserTimedout + JewelsGifted need SB ≥ 1.0.5).
        //   Kick:    the full Streamer.bot 1.0.x event source (21 names;
        //            native Kick support needs SB ≥ 1.0.0, RewardRedemption +
        //            sGifted ≥ 1.0.2). "sGifted" is the literal wire name for
        //            Kicks Gifted — normalized to KicksGifted on receive.
        //   General: Custom.
        //   OBS:     SceneChanged.
        //
        // An SB build that doesn't know a name simply never delivers it; the
        // GetEvents probe (HandleGetEventsResponse) reports what the connected
        // build actually offers so missing platforms are loudly visible.
        //
        // Twitch per-reward redemptions, Charity, Goals, Shoutouts, Bans,
        // Timeouts, and Channel.Update are intentionally NOT added here — the
        // exact-event-name ambiguity kept them out of this pass.
        private static readonly string[] TwitchEvents = new[]
        {
            "ChatMessage", "Cheer", "Sub", "Resub", "GiftSub", "GiftBomb",
            "Follow", "RewardRedemption", "Raid",
            "PollCreated", "PollUpdated", "PollCompleted",
            "PredictionCreated", "PredictionUpdated", "PredictionLocked", "PredictionEnded",
            "HypeTrainStart", "HypeTrainUpdate", "HypeTrainEnd",
            "AdRun", "StreamOnline", "StreamOffline",
        };
        private static readonly string[] YouTubeEvents = new[]
        {
            "Message", "SuperChat", "SuperSticker",
            "NewSponsor", "MemberMileStone", "MembershipGift", "GiftMembershipReceived",
            "FirstWords", "NewSubscriber", "UserBanned", "UserTimedout",
            "MessageDeleted", "JewelsGifted", "StatisticsUpdated", "PresentViewers",
            "BroadcastStarted", "BroadcastEnded", "BroadcastAdded", "BroadcastRemoved",
            "BroadcastUpdated", "BroadcastMonitoringStarted", "BroadcastMonitoringEnded",
            "NewSponsorOnlyStarted", "NewSponsorOnlyEnded",
            "PollStarted", "PollUpdated", "PollClosed",
            "SevenTVEmoteAdded", "SevenTVEmoteRemoved",
            "BetterTTVEmoteAdded", "BetterTTVEmoteRemoved",
        };
        private static readonly string[] KickEvents = new[]
        {
            "ChatMessage", "FirstWords", "Follow",
            "Subscription", "Resubscription", "GiftSubscription", "MassGiftSubscription",
            "RewardRedemption", "sGifted",
            "StreamOnline", "StreamOffline", "ViewerCountUpdate", "ChannelUpdate",
            "UserBanned", "UserTimedOut", "PresentViewers",
            "BroadcasterAuthenticated", "BroadcasterChatConnected", "BroadcasterChatDisconnected",
            "SevenTVEmoteAdded", "SevenTVEmoteRemoved",
        };
        private static readonly string[] GeneralEvents = new[] { "Custom" };
        private static readonly string[] ObsEvents     = new[] { "SceneChanged" };

        private const string GetEventsRequestId = "phoenix-getevents";

        private static string BuildSubscriptionEnvelope(string request, string requestId)
        {
            // Anonymous-object serialization keeps Subscribe and UnSubscribe envelopes
            // mechanically identical — adding a new event family is a one-line edit
            // on the static arrays above.
            var payload = new
            {
                request = request,
                id      = requestId,
                events  = new
                {
                    Twitch  = TwitchEvents,
                    YouTube = YouTubeEvents,
                    Kick    = KickEvents,
                    General = GeneralEvents,
                    OBS     = ObsEvents,
                },
            };
            return System.Text.Json.JsonSerializer.Serialize(payload);
        }

        private void SubscribeToEvents()
        {
            // Unsubscribe first to clear any stacked subscriptions from a prior session
            // that Streamer.bot hasn't cleaned up yet. Safe to call even when not subscribed.
            Send(BuildSubscriptionEnvelope("UnSubscribe", "phoenix-hub-unsub"));
            Send(BuildSubscriptionEnvelope("Subscribe",   "phoenix-hub-sub"));
            // Feature probe: ask the connected build what it can actually deliver,
            // so a too-old Streamer.bot (no Kick, partial YouTube) is loudly
            // visible in the log instead of silently never firing nodes.
            Send(System.Text.Json.JsonSerializer.Serialize(new { request = "GetEvents", id = GetEventsRequestId }));
            GlobalLogger.Log($"Subscriptions requested for Twitch/YouTube/Kick/OBS events to {_url}", "WS", LogLevel.Communication);
        }

        // GetEvents response: { id, events: { Twitch: [...], YouTube: [...], ... } }.
        // Purely diagnostic — subscription is already sent with the full list; SB
        // ignores names it doesn't know. One log line per platform, only when
        // something is missing.
        private static void HandleGetEventsResponse(System.Text.Json.JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("events", out var events) ||
                    events.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return;

                WarnIfPlatformEventsMissing(events, "YouTube", YouTubeEvents,
                    "YouTube events/nodes will not fire — connect the YouTube platform in Streamer.bot (SB ≥ 1.0 recommended; UserTimedout/JewelsGifted need ≥ 1.0.5).");
                WarnIfPlatformEventsMissing(events, "Kick", KickEvents,
                    "Kick events/nodes will not fire — Streamer.bot ≥ 1.0.2 with the Kick platform connected (app login + streamer.bot website account link) is required.");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WS", "GetEvents probe parse failed", ex);
            }
        }

        private static void WarnIfPlatformEventsMissing(System.Text.Json.JsonElement events, string source, string[] wanted, string hint)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Case-insensitive property scan — TryGetProperty is case-SENSITIVE
            // and the probe must not report "NO events" just because an SB build
            // keys the bucket "youtube" instead of "YouTube".
            foreach (var prop in events.EnumerateObject())
            {
                if (!prop.Name.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                foreach (var e in prop.Value.EnumerateArray())
                    if (e.ValueKind == System.Text.Json.JsonValueKind.String)
                        available.Add(e.GetString() ?? string.Empty);
            }

            if (available.Count == 0)
            {
                GlobalLogger.Log(
                    $"Connected Streamer.bot exposes NO {source} events — {hint}",
                    "WS", LogLevel.Communication);
                return;
            }

            var missing = new List<string>();
            foreach (var w in wanted)
                if (!available.Contains(w)) missing.Add(w);
            if (missing.Count > 0)
            {
                GlobalLogger.Log(
                    $"Connected Streamer.bot lacks {missing.Count} {source} event(s): {string.Join(", ", missing)} — those nodes will not fire (older SB build?).",
                    "WS", LogLevel.Communication);
            }
        }
    }
}
