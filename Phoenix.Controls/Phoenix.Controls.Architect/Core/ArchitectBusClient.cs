using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Three-state bus-connection signal consumed by Architect chrome.
    /// Pre-fix the only signal was a boolean on
    /// <see cref="ArchitectBusClient.OnConnectionStatusChanged"/>; the chrome
    /// painted red the instant a socket dropped with no hint that the
    /// reconnect loop was already mid-retry. Architect UX review P0-8.
    /// </summary>
    public enum BusConnectionState
    {
        Disconnected,
        Reconnecting,
        Connected,
    }

    /// <summary>
    /// ArchitectBusClient — connects Architect to the Bus as a named client.
    ///
    ///  Prefers the in-process bridge (<see cref="InProcBus"/>) when
    /// Hub has registered one — which is always the case in the post-T15
    /// runtime where Architect.WinUI lives in Hub.WinUI's process. The bridge
    /// short-circuits the localhost WebSocket round trip and the JSON
    /// serialisation cost; the public event surface (<see cref="OnNodeExecuted"/>,
    /// <see cref="OnVariableSet"/>, <see cref="OnMacroSync"/>,
    /// <see cref="OnConnectionStateChanged"/>) stays byte-identical so script-
    /// driven debug-trace tests keep passing. Falls back to a WebSocket
    /// connect loop (port 18081) for the rare case where Hub isn't loaded
    /// in this process (headless tests, future standalone Architect variant).
    ///
    /// Inbound messages handled (both transports):
    ///   DEBUG_NODE_EXEC  → fires OnNodeExecuted(nodeId, scriptFile)
    ///   DEBUG_VAR_SET    → fires OnVariableSet(varName, value, scriptFile)
    ///   MACRO_SYNC       → fires OnMacroSync(payload)
    /// </summary>
    public class ArchitectBusClient : IDisposable // [P1 swarm-audit 2026-05-29] dispose _cts / _wsSendLock
    {
        private static ArchitectBusClient? _instance;
        public static ArchitectBusClient Instance => _instance ??= new ArchitectBusClient();

        // ── WebSocket fallback state (only used when InProcBus is unavailable) ──
        private ClientWebSocket? _ws;
        private readonly string _busUrl = "ws://127.0.0.1:18081/";
        private CancellationTokenSource _cts = new();
        //  Per-client send semaphore for the WS fallback. ClientWebSocket.SendAsync
        // is not thread-safe; pre-fix concurrent canvas-debug-trace + MACRO_REQUEST
        // sends could interleave frames on the same socket → Hub parse error → silent
        // drop. Mirrors the Hub-side _sendLocks pattern.
        private readonly SemaphoreSlim _wsSendLock = new SemaphoreSlim(1, 1);

        // Architect spawns a new sibling window per open/new file, and each
        // MainView calls Start() in its constructor. Without this guard each
        // window launches its own ConnectLoopAsync that fights over _ws,
        // producing ghost connections that all error simultaneously when GC
        // catches up.
        private int _started;

        //  Exponential reconnect backoff with jitter,
        // mirroring Hub/Core/WS.cs. Pre-fix every Architect host retried on a
        // flat 5s interval, so multiple host processes that started together
        // stayed phase-locked and re-hit the Bus in lockstep while Hub was down.
        // _consecutiveDisconnects resets to 0 on a successful connect.
        private int _consecutiveDisconnects;
        private static readonly Random _backoffJitter = new();

        // ── In-process bridge state ──
        //  When non-null, all inbound traffic flows through this handler
        // and all outbound traffic publishes via IInProcBusBridge.PublishAsync.
        // The bridge handler is registered exactly once at Start() and unregistered
        // at Stop() so a Restart cycle stays clean.
        private IInProcBusBridge? _bridge;
        private Action<BusMessage>? _bridgeHandler;

        // PERF (perf/architect-blockers, BlockerB): bus messages arrive on a
        // ThreadPool worker. Pre-cache, OnNodeExecuted / OnVariableSet were invoked
        // synchronously on that worker — subscribers (e.g. canvas flash logic) then
        // mutated INotifyPropertyChanged from a background thread, which is undefined
        // behaviour on WinUI bindings. Capture the calling SynchronizationContext
        // on Start and Post event dispatches through it so subscribers always run
        // on the UI thread.
        private SynchronizationContext? _uiContext;

        /// <summary>
        /// True once the bus client is wired (either via the in-proc bridge or a
        /// connected WebSocket). The previous implementation only reflected WS
        /// state; the in-proc path returns true as soon as Start() resolves the
        /// bridge.
        /// </summary>
        public bool IsConnected => _bridge is not null || _ws?.State == WebSocketState.Open;

        /// <summary>Fires when a DEBUG_NODE_EXEC bus message arrives. Args: (nodeId, scriptFile).</summary>
        public event Action<string, string>? OnNodeExecuted;

        /// <summary>Fires when a DEBUG_VAR_SET bus message arrives. Args: (varName, value, scriptFile).</summary>
        public event Action<string, string, string>? OnVariableSet;

        /// <summary>Fires when connection state changes. Arg: connected.</summary>
        public event Action<bool>? OnConnectionStatusChanged;

        /// <summary>
        /// Three-state connection signal. <see cref="OnConnectionStatusChanged"/>
        /// only carried connected vs disconnected — there was no
        /// "reconnecting" middle state, so the status dot flipped red the
        /// instant a socket dropped and stayed red for the entire 5s backoff
        /// even though the loop was already mid-retry. Subscribers should
        /// prefer this event going forward. Architect UX review P0-8.
        /// </summary>
        public event Action<BusConnectionState>? OnConnectionStateChanged;

        /// <summary>
        /// Last connect-loop / receive-loop error message, captured from
        /// the catch block that bumps the state to <see cref="BusConnectionState.Reconnecting"/>.
        /// The Architect chrome reads this into the BusStatusDot tooltip so a
        /// streamer debugging a missing live-debug trace doesn't have to
        /// dig through the System Log to find the underlying failure reason
        /// (ECONNREFUSED, hostname resolve failure, handshake reject, etc.).
        /// Null until the first failure; reset to null on successful connect.
        /// Set under the same DispatchToUi marshal as the state change so a
        /// subscriber reading the property from the UI thread sees the
        /// matching message for the current state.
        /// </summary>
        public string? LastErrorMessage { get; private set; }

        //  OnLogicReloadAck was a one-publisher / zero-subscriber event:
        // CHANGELOG 0.9.10 advertised LOGIC_RELOAD as Architect Phase E with a
        // round-trip ack, but the consumer never landed. Carrying it kept the
        // dead handler on the receive path. Removed; the LOGIC_RELOAD_ACK branch
        // in HandleMessage is now a no-op (left for forward-compat with Hub
        // emitters that still send the type — they're silently absorbed).

        /// <summary>Fires when a MACRO_SYNC broadcast arrives from Hub. Payload is JSON array of Macro.</summary>
        public event Action<string>? OnMacroSync;

        private ArchitectBusClient() { }

        // ─────────────────────────────────────────────────────────────────────
        //  START / STOP
        // ─────────────────────────────────────────────────────────────────────

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            // [P1 swarm-audit 2026-05-29] dispose the prior CTS before replacing it
            // so a Start/Stop/Start cycle doesn't leak the old token source.
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            try { oldCts?.Dispose(); } catch { }
            _uiContext = SynchronizationContext.Current;

            //  Prefer the in-process bridge when Hub registered one.
            // Hub registers via Bus's constructor (touching Bus.Instance is enough
            // to flip the registry slot). Any access to Bus.Instance from Hub
            // bootstrap is sufficient; in practice HubBootstrapper calls
            // Bus.Instance.StartAsync before pillar UI boots, so the bridge is
            // installed by the time Architect's MainView.OnLoaded fires.
            var bridge = InProcBus.Instance;
            if (bridge is not null)
            {
                _bridge = bridge;
                _bridgeHandler = OnBridgeMessage;
                bridge.Subscribe(_bridgeHandler);

                LastErrorMessage = null;
                GlobalLogger.Log("Architect connected to Bus (in-proc bridge)", "ArchitectBus", LogLevel.Communication);

                // Mirror the WS connect path's status broadcast so chrome
                // paints green immediately. No Reconnecting → Connected
                // transition since the bridge is synchronously available.
                {
                    var handler = OnConnectionStatusChanged;
                    if (handler is not null) DispatchToUi(() => handler.Invoke(true));
                }
                {
                    var state = OnConnectionStateChanged;
                    if (state is not null) DispatchToUi(() => state.Invoke(BusConnectionState.Connected));
                }

                // Same opening MACRO_REQUEST as the WS path so the macro library
                // gets seeded into Architect on Start.
                _ = SendAsync(new BusMessage
                {
                    Type   = "MACRO_REQUEST",
                    Source = "Architect",
                    Target = "Hub",
                });
                return;
            }

            // Fallback: WebSocket connect loop. See class doc comment for when
            // this path is exercised.
            var ctSnap = _cts.Token;
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => ConnectLoopAsync(ctSnap),
                "ArchitectBusClient", "ConnectLoopAsync",
                ctSnap);
        }

        // Marshal an event invocation to the captured UI SynchronizationContext.
        // Falls back to synchronous invoke when no context was captured (off-
        // thread Start, headless tests). Send vs Post: we Post so the receive
        // loop doesn't stall waiting for the UI thread to drain — bursts of
        // NODE_EXEC stay non-blocking.
        private void DispatchToUi(Action action)
        {
            var ctx = _uiContext;
            if (ctx is null) { try { action(); } catch (Exception ex) { GlobalLogger.Error("ArchitectBusClient", "DispatchToUi (sync)", ex); } return; }
            ctx.Post(static state =>
            {
                var a = (Action)state!;
                try { a(); }
                catch (Exception ex) { GlobalLogger.Error("ArchitectBusClient", "DispatchToUi (posted)", ex); }
            }, action);
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _started, 0) == 0) return;
            _cts.Cancel();

            //  Tear down the bridge subscription first so further bus
            // traffic stops reaching us before we touch the WS state below.
            var bridge = _bridge;
            var handler = _bridgeHandler;
            if (bridge is not null && handler is not null)
            {
                try { bridge.Unsubscribe(handler); }
                catch (Exception ex) { GlobalLogger.Error("ArchitectBusClient", "InProcBus.Unsubscribe", ex); }
                _bridge = null;
                _bridgeHandler = null;
            }

            // R2 — snapshot+swap the active socket so ConnectLoopAsync's reconnect
            // can't reassign _ws between our snapshot and the close call. The
            // ConnectLoop's finally checks identity before disposing, so a swapped
            // socket gets cleanly torn down by whichever path reaches it first.
            var ws = Interlocked.Exchange(ref _ws, null);
            if (ws != null && ws.State == WebSocketState.Open)
            {
                // P1-8 — route teardown faults through AsyncErrorBoundary so any
                // unexpected failure surfaces in GlobalLogger instead of being
                // swallowed by a bare Task.Run. Inner empty catches stay: a
                // best-effort close + dispose is fine when the socket is already
                // being torn down.
                _ = AsyncErrorBoundary.SafeRunAsync(async () =>
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None); }
                    catch { }
                    finally { try { ws.Dispose(); } catch { } }
                }, "ArchitectBusClient", "WebSocket teardown");
            }
            else
            {
                try { ws?.Dispose(); } catch { }
            }

            // Final Disconnected signal for the bridge path; the WS path emits
            // its own in ConnectLoopAsync.
            if (bridge is not null)
            {
                var statusHandler = OnConnectionStatusChanged;
                if (statusHandler is not null) DispatchToUi(() => statusHandler.Invoke(false));
                var stateHandler = OnConnectionStateChanged;
                if (stateHandler is not null) DispatchToUi(() => stateHandler.Invoke(BusConnectionState.Disconnected));
            }
        }

        // [P1 swarm-audit 2026-05-29] The SemaphoreSlim _wsSendLock and the
        // CancellationTokenSource _cts were created at field init / recreated in
        // Start() but never disposed, leaking a handle per client lifetime. Stop()
        // the lifecycle, then dispose both defensively. Safe to call repeatedly.
        public void Dispose()
        {
            try { Stop(); } catch { }
            try { _cts?.Dispose(); } catch { }
            try { _wsSendLock?.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }

        /// <summary>Sends a message to Hub via the active transport.</summary>
        public async Task SendAsync(BusMessage msg)
        {
            //  Prefer the in-proc bridge when it's the active transport.
            // PublishAsync runs Hub-side subscribers + RouteIncomingMessage
            // synchronously then fans to remote WS peers.
            var bridge = _bridge;
            if (bridge is not null)
            {
                try { await bridge.PublishAsync(msg).ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("ArchitectBusClient", "InProcBus.PublishAsync", ex); }
                return;
            }

            // BH-057: snapshot the field once. ConnectLoopAsync swaps _ws between
            // iterations and the finally block disposes the previous instance, so
            // a bare check-then-use sequence here can dereference a freshly-disposed
            // socket during reconnect.
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open) return;

            //  Serialize sends on the WS fallback. The Hub side has
            // had _sendLocks per client since H45; the Architect side never
            // installed the symmetric semaphore, so concurrent canvas-debug
            // + MACRO_REQUEST sends could interleave frames → Hub parse
            // errors → silent drops.
            // [P1 wave-1] WaitAsync sits outside the try below; a concurrent
            // Dispose() can dispose _wsSendLock and make WaitAsync throw
            // ObjectDisposedException with no handler — crashing the caller.
            // Guard the acquire itself and bail on the shutdown race.
            try { await _wsSendLock.WaitAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; /* shutdown race; fine */ }
            try
            {
                string json = JsonSerializer.Serialize(msg);
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { /* fire-and-forget: connection may have dropped */ }
            finally
            {
                try { _wsSendLock.Release(); }
                catch (ObjectDisposedException) { /* shutdown race; fine */ }
            }
        }

        /// <summary>
        /// Cap a tooltip-bound diagnostic at a reasonable length so a
        /// stack-flavoured exception message doesn't blow out the
        /// BusStatusDot tooltip surface.
        /// </summary>
        private static string TrimTooltip(string s)
        {
            const int cap = 200;
            if (string.IsNullOrEmpty(s)) return s;
            // Strip newlines that would make WinUI's tooltip render multi-line.
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= cap ? s : s.Substring(0, cap - 1) + "…";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  IN-PROC BRIDGE HANDLER
        // ─────────────────────────────────────────────────────────────────────

        //  Single entry point for inbound traffic via the bridge. Same
        // routing logic as the WS HandleMessage path (envelope validation +
        // typed dispatch) but receives the BusMessage object directly, skipping
        // the JSON parse step the WS path needs.
        private void OnBridgeMessage(BusMessage msg)
        {
            // Filter out our own outbound publishes. PublishAsync fires
            // OnMessageReceived which then re-enters this handler if Bus's
            // OnMessageReceived also fans to our subscriber list — defensive,
            // since Bus's current implementation does NOT echo, but the
            // contract from Bus's side could shift.
            if (string.Equals(msg.Source, "Architect", StringComparison.OrdinalIgnoreCase)) return;
            try { RouteValidatedMessage(msg); }
            catch (Exception ex)
            {
                GlobalLogger.Error("ArchitectBusClient", "OnBridgeMessage", ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONNECT LOOP (WebSocket fallback)
        // ─────────────────────────────────────────────────────────────────────

        private async Task ConnectLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // [P2 swarm-audit] Publish the new socket atomically so Stop()'s
                    // Interlocked.Exchange(ref _ws, null) and SendAsync's snapshot can
                    // never observe a half-assigned field. Build on a local first,
                    // configure it, then swap it into the field; use the local for the
                    // connect call so a concurrent Stop() swap can't null it mid-handshake.
                    var ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("X-Client-Id", "Architect");
                    Interlocked.Exchange(ref _ws, ws);
                    await ws.ConnectAsync(new Uri(_busUrl + "?id=Architect"), ct);

                    GlobalLogger.Log("Architect connected to Bus", "ArchitectBus", LogLevel.Communication);
                    // Clear the prior failure message on success so the
                    // status-dot tooltip stops claiming "Connection refused"
                    // once the reconnect lands.
                    LastErrorMessage = null;
                    //  reset the backoff streak on a
                    // successful (re)connect so the next outage starts at base.
                    System.Threading.Interlocked.Exchange(ref _consecutiveDisconnects, 0);
                    {
                        var handler = OnConnectionStatusChanged;
                        if (handler is not null) DispatchToUi(() => handler.Invoke(true));
                    }
                    {
                        var state = OnConnectionStateChanged;
                        if (state is not null) DispatchToUi(() => state.Invoke(BusConnectionState.Connected));
                    }

                    // Pull current global macro library from Hub
                    await SendAsync(new BusMessage
                    {
                        Type   = "MACRO_REQUEST",
                        Source = "Architect",
                        Target = "Hub"
                    });

                    await ReceiveLoopAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    string detail = ex.GetBaseException()?.Message ?? ex.Message;
                    LastErrorMessage = TrimTooltip($"{ex.GetType().Name}: {detail}");
                    GlobalLogger.Log($"ArchitectBus connect error: {ex.Message}", "ArchitectBus", LogLevel.Communication);
                }
                finally
                {
                    try { _ws?.Dispose(); } catch { }
                }

                {
                    var handler = OnConnectionStatusChanged;
                    if (handler is not null) DispatchToUi(() => handler.Invoke(false));
                }
                if (!ct.IsCancellationRequested)
                {
                    //  Exponential backoff (base × 2^n,
                    // capped at 60s) with ±25% jitter so multiple Architect hosts
                    // de-correlate instead of hammering the Bus in lockstep while
                    // Hub is down. The config knob is the BASE; n caps at 4 (16×).
                    int baseMs = ConfigManager.Current.ArchitectReconnectBackoffMs > 0
                        ? ConfigManager.Current.ArchitectReconnectBackoffMs
                        : 5000;
                    int n = System.Threading.Interlocked.Increment(ref _consecutiveDisconnects);
                    double scaled = Math.Min(baseMs * Math.Pow(2.0, Math.Min(n - 1, 4)), 60_000);
                    double jitterPct;
                    lock (_backoffJitter) jitterPct = _backoffJitter.NextDouble() * 0.5 - 0.25;
                    int backoff = (int)Math.Max(baseMs, scaled * (1.0 + jitterPct));
                    {
                        var state = OnConnectionStateChanged;
                        if (state is not null) DispatchToUi(() => state.Invoke(BusConnectionState.Reconnecting));
                    }
                    await Task.Delay(backoff, ct);
                }
                // [P1 wave-1] No Disconnected fire here on the cancellation
                // branch: the while loop exits immediately after and the final
                // block below fires Disconnected exactly once on loop exit.
                // Firing it here too double-signalled the transition on every
                // graceful Stop().
            }
            {
                var state = OnConnectionStateChanged;
                if (state is not null) DispatchToUi(() => state.Invoke(BusConnectionState.Disconnected));
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var ws = _ws;
            if (ws is null) return;
            var buffer = new byte[16384];
            const int MaxAggregateBytes = 4 * 1024 * 1024;
            var aggregate = new List<byte>(buffer.Length);
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                try
                {
                    aggregate.Clear();
                    System.Net.WebSockets.WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        if (result.Count > 0)
                        {
                            if (aggregate.Count + result.Count > MaxAggregateBytes)
                            {
                                GlobalLogger.Log(
                                    $"ArchitectBusClient.ReceiveLoopAsync: aggregate fragment size exceeded {MaxAggregateBytes} bytes — dropping message.",
                                    "ArchitectBusClient",
                                    LogLevel.CriticalError);
                                aggregate.Clear();
                                while (!result.EndOfMessage && ws.State == WebSocketState.Open)
                                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                                break;
                            }
                            aggregate.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                        }
                    }
                    while (!result.EndOfMessage);

                    if (aggregate.Count == 0) continue;
                    // [P2 swarm-audit] Deserialize straight off the aggregated bytes via
                    // a zero-copy span over the List<byte> backing array. Pre-fix this
                    // allocated twice per frame — ToArray() copy + GetString() — which is
                    // pure GC churn under NODE_EXEC bursts / MACRO_SYNC broadcasts.
                    HandleMessage(System.Text.Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(aggregate)));
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MESSAGE ROUTING
        // ─────────────────────────────────────────────────────────────────────

        private void HandleMessage(string json)
        {
            BusMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<BusMessage>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ArchitectBusClient", "Malformed message", ex);
                return;
            }

            if (msg == null) return;

            var errors = ValidateEnvelope(msg);
            if (errors.Count > 0)
            {
                GlobalLogger.Error(
                    "ArchitectBusClient",
                    "Rejected malformed envelope: " + string.Join("; ", errors));
                return;
            }

            RouteValidatedMessage(msg);
        }

        // Shared dispatch body — runs after envelope validation. Used by both
        // the WS HandleMessage path (after JSON parse) and the in-proc bridge
        // path (which receives the BusMessage directly).
        private void RouteValidatedMessage(BusMessage msg)
        {
            try
            {
                switch (msg.Type)
                {
                    case "DEBUG_NODE_EXEC":
                    {
                        //  Short-circuit before the
                        // JSON parse when no canvas window is subscribed — Hub
                        // emits NODE_EXEC markers throughout every script run, so
                        // parsing each one just to drop it is pure churn when
                        // live-debug isn't being observed.
                        var handler = OnNodeExecuted;
                        if (handler is null || string.IsNullOrEmpty(msg.Payload)) break;
                        using var doc = JsonDocument.Parse(msg.Payload);
                        string nodeId     = doc.RootElement.TryGetProperty("nodeId",     out var ni) ? ni.GetString() ?? "" : "";
                        string scriptFile = doc.RootElement.TryGetProperty("scriptFile", out var sf) ? sf.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(nodeId))
                            DispatchToUi(() => handler.Invoke(nodeId, scriptFile));
                        break;
                    }
                    case "DEBUG_VAR_SET":
                    {
                        //  subscriber-null check first.
                        var handler = OnVariableSet;
                        if (handler is null || string.IsNullOrEmpty(msg.Payload)) break;
                        using var doc = JsonDocument.Parse(msg.Payload);
                        string varName    = doc.RootElement.TryGetProperty("varName",    out var vn) ? vn.GetString() ?? "" : "";
                        string value      = doc.RootElement.TryGetProperty("value",      out var vv) ? vv.GetString() ?? "" : "";
                        string scriptFile = doc.RootElement.TryGetProperty("scriptFile", out var sf) ? sf.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(varName))
                            DispatchToUi(() => handler.Invoke(varName, value, scriptFile));
                        break;
                    }
                    case "LOGIC_RELOAD_ACK":
                    {
                        //  OnLogicReloadAck event deleted — no Architect-side
                        // consumer ever landed. Absorb the type so Hub emitters don't
                        // surface as "unknown type" errors here.
                        break;
                    }
                    case "MACRO_SYNC":
                    {
                        var handler = OnMacroSync;
                        var payload = msg.Payload ?? "";
                        if (handler is not null)
                            DispatchToUi(() => handler.Invoke(payload));
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ArchitectBusClient", "Handler failure for type=" + msg.Type, ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  R15 — ENVELOPE VALIDATION
        // ─────────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> _typesRequiringPayload = new(StringComparer.OrdinalIgnoreCase)
        {
            "DEBUG_NODE_EXEC",
            "DEBUG_VAR_SET",
            // LOGIC_RELOAD_ACK kept off the required list — the type was an ack
            // marker; payload may legitimately be empty when the Hub side just
            // wants to signal "reloaded".
            "MACRO_SYNC"
        };

        private static List<string> ValidateEnvelope(BusMessage? msg)
        {
            var errors = new List<string>();
            if (msg == null)
            {
                errors.Add("envelope is null");
                return errors;
            }

            if (string.IsNullOrWhiteSpace(msg.Type))
                errors.Add("Type is null or empty");

            if (msg.Source is null)
                errors.Add("Source is null");

            if (!string.IsNullOrEmpty(msg.Type)
                && _typesRequiringPayload.Contains(msg.Type)
                && string.IsNullOrEmpty(msg.Payload))
            {
                errors.Add($"Payload is null/empty for type={msg.Type} which requires a payload");
            }

            return errors;
        }
    }
}
