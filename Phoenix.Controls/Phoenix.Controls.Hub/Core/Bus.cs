using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Bus — Local WebSocket IPC event bus.
    /// Hosted by Hub on port 18081. Visualist and Architect connect as clients.
    /// Allows bidirectional event passing: e.g. Hub triggers a visual, Visualist
    /// fires VISUAL_COMPLETE back, Hub resumes the waiting script.
    /// </summary>
    public class Bus
    {
        private static Bus? _instance;
        public static Bus Instance => _instance ??= new Bus();

        private readonly int _port;
        private HttpListener? _httpListener;
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

        // Per-client send semaphore — WebSocket.SendAsync is not thread-safe;
        // concurrent BroadcastAsync calls used to interleave frames on the same
        // socket. Acquired around each send and released in finally.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();

        // Serialize the per-client snapshot+swap on reconnect so the new
        // handler installs (ws, sendLock) atomically. The OLD handler's finally
        // identity-checks before evicting; this gate makes "old vs new" coherent
        // even when accept races the previous loop's unwind.
        private readonly object _reconnectGate = new object();

        // Track in-flight HandleClientAsync tasks so Stop() can drain them
        // before disposing _sendLocks. Without this, Stop() races the handler's
        // finally block: Stop().Clear() can dispose semaphores the finally is
        // about to TryRemove or the WaitAsync inside BroadcastAsync is mid-flight
        // on. Keyed by Task to support cheap removal on completion via the
        // continuation registered in StartAsync.
        private readonly ConcurrentDictionary<Task, byte> _handlerTasks = new();

        // Tracks pending WaitForVisual / WaitForEvent completions: key = waitId, value = TaskCompletionSource
        private readonly ConcurrentDictionary<string, TaskCompletionSource<BusMessage>> _pendingWaits = new();

        // ─── V13 §8.1 — the resolved-visual-wait ledger ─────────────────────
        //
        // Records waitIds that a VISUAL_COMPLETE has already resolved, purely so a
        // LATER ack for the same waitId can be reported EXACTLY ONCE per waitId
        // instead of once per ack. Value is a 0/1 "already logged" latch.
        //
        // WHY THE LATCH CANNOT LIVE IN WidgetTriggerQueue.NotifyComplete, which is
        // otherwise the natural home (it is already the idempotent first-ack-wins
        // gate for the invocation): that method reads `_inFlight`, and the pump's
        // `finally` clears `_inFlight` asynchronously as soon as the FIRST ack
        // resolves the invocation's TCS. A second socket's ack arriving a few
        // microseconds later therefore hits the `current is null` early-return and
        // is indistinguishable from an ack for a trigger that was never queued — so
        // a fan-out of 8 would log somewhere between 0 and 1 lines depending on
        // thread-pool scheduling. `ResolveVisualWait`'s atomic
        // `_pendingWaits.TryRemove` is the one first-wins decision that is keyed by
        // waitId AND survives the invocation, which is why the report is anchored
        // here. No second completion mechanism is introduced: both gates already
        // existed and both remain idempotent; only the reporting is added.
        //
        // BOUNDED: capacity-capped FIFO. Entries are 32-char waitIds and are only
        // ever added by a REAL resolution, so steady-state occupancy is "the last N
        // visual waits this Hub completed". Trimming the oldest is correct because a
        // late ack that arrives more than N completions after its own resolution is
        // long past being an interesting collision.
        private const int MaxResolvedVisualWaitLedger = 128;
        private readonly ConcurrentDictionary<string, int> _resolvedVisualWaits = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _resolvedVisualWaitOrder = new();
        // LOGIC_RELOAD refresh scheduling (see the LOGIC_RELOAD case in
        // RouteIncomingMessage): LEADING-edge immediate refresh on a thread-pool
        // thread for the common single-save case (the bus path is the FAST
        // refresh — Architect saves rely on it so a chat command arriving right
        // after a save runs the NEW script text), plus a TRAILING-edge
        // 250ms coalesce for save storms (rapid graph switching fired 5-6
        // LOGIC_RELOADs in ~1s in the 2026-07-22 streaming-PC freeze log) so a
        // burst costs two background rescans, not six UI-thread ones.
        // _lastLogicReloadRefreshMs gates the leading edge (Environment.
        // TickCount64; CAS so racing messages elect exactly one immediate
        // runner). The debouncer is deliberately NOT disposed in StopAsync —
        // the same Bus instance can be restarted (StopAsync's HttpListener
        // teardown comment documents the Stop→StartAsync cycle) and a disposed
        // PathDebouncer turns Schedule into a permanent no-op; the refresh
        // action gates on _stopped instead, so a late fire during teardown is
        // a no-op.
        private const string LogicReloadDebounceKey = "bus-logic-reload";
        private const int LogicReloadDebounceMs = 250;
        private readonly PathDebouncer _logicReloadDebouncer = new();
        private long _lastLogicReloadRefreshMs;

        // Anonymous-id population counter. Each connection that arrives
        // without a query-string `id` (or with an id that fails validation) gets
        // a synthetic `client_xxxxxxxxxxxx`. Without a ceiling, a hostile loopback
        // peer could open + immediately drop sockets in a tight loop to bloat the
        // _clients / _sendLocks dictionaries (the post-loop eviction in
        // HandleClientAsync only fires AFTER the receive loop returns, so churn
        // faster than the loop unwinds and we accumulate live handler tasks).
        // Soft cap: when the live anonymous-client count is at or above the cap,
        // new anonymous connections are refused with a CriticalError log. The
        // counter is decremented when the handler's finally block evicts the entry.
        private const int MaxAnonymousClients = 32;
        private int _anonymousClientCount;

        // Id length cap and charset filter. Matches the documented
        // shape of well-known ids ("Architect", "Visualist", "client_<hex>"):
        // ASCII letters, digits, underscore, hyphen, up to 64 chars.
        private const int MaxClientIdLength = 64;
        private static readonly System.Text.RegularExpressions.Regex _clientIdRegex =
            new System.Text.RegularExpressions.Regex(@"\A[A-Za-z0-9_\-]{1,64}\z",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Keep-alive on each accepted WebSocket so a half-open peer
        // (privileged peer killed by SIGKILL / network partition / VM suspend)
        // is detected within ~60 s rather than holding the anti-
        // displacement slot until Hub itself restarts. The browser-direct
        // WebSockets on HUDServer use the same interval per their own keep-
        // alive setup. Kept as a TimeSpan field for ease of tuning.
        private static readonly TimeSpan WebSocketKeepAlive = TimeSpan.FromSeconds(30);

        // Prefix index for prefix-scanning wait keys.
        //
        // RouteIncomingMessage used to snapshot the entire _pendingWaits dictionary
        // via ToArray() per inbound message and linear-scan for keys matching
        // "{type}:{payload}:" or "{type}:*:". Under a single live wait that's still
        // O(N) where N = total pending waits (visual + event), allocating a fresh
        // KVP array each time. With this index lookup is O(1) per prefix.
        //
        // Wait keys created by WaitForEventAsync are of the form "{type}:{filter}:{guid}"
        // and are inserted/removed through RegisterEventWait/UnregisterEventWait so the
        // prefix bucket stays consistent with _pendingWaits. Visual waits keyed by a
        // bare waitId (no colon) are unaffected — they're always resolved via direct
        // ResolveVisualWait/CancelPendingVisualWait lookups, which are already O(1).
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _waitPrefixIndex = new();

        public event Action<BusMessage>? OnMessageReceived
        {
            add    { lock (_msgHandlerGate) { _onMessageReceived += value; RebuildOnMessageReceivedCache(); } }
            remove { lock (_msgHandlerGate) { _onMessageReceived -= value; RebuildOnMessageReceivedCache(); } }
        }
        private Action<BusMessage>? _onMessageReceived;
        private readonly object _msgHandlerGate = new object();
        // Cached invocation list for OnMessageReceived. Avoids the
        // per-message GetInvocationList() Delegate[] allocation. Rebuilt under
        // _msgHandlerGate on subscribe/unsubscribe; readers do a Volatile.Read so the
        // dispatch loop never needs to take the gate. Empty array (not null) when no
        // subscribers, so the read site can iterate unconditionally.
        private Action<BusMessage>[] _onMessageReceivedCache = Array.Empty<Action<BusMessage>>();
        private void RebuildOnMessageReceivedCache()
        {
            var list = _onMessageReceived?.GetInvocationList();
            if (list == null || list.Length == 0)
            {
                Volatile.Write(ref _onMessageReceivedCache, Array.Empty<Action<BusMessage>>());
                return;
            }
            var typed = new Action<BusMessage>[list.Length];
            for (int i = 0; i < list.Length; i++) typed[i] = (Action<BusMessage>)list[i];
            Volatile.Write(ref _onMessageReceivedCache, typed);
        }

        // Shared OnMessageReceived fan — ONE loop shape for all three
        // delivery sites (WS receive loop, in-proc bridge PublishAsync, and the
        // Hub-origin local delivery inside BroadcastAsync) so the isolation
        // semantics cannot drift apart between paths. Iterates the cached
        // invocation array via Volatile.Read (no per-message GetInvocationList()
        // Delegate[] allocation; the cache is rebuilt on subscribe/unsubscribe
        // under _msgHandlerGate) with per-handler try/catch so one throwing
        // subscriber can't kill the calling loop or starve later handlers.
        // errorContext prefixes the log label so the three call sites keep
        // their historically distinct breadcrumbs ("" / "InProc " / "Hub-local ").
        private void FanOnMessageReceived(BusMessage msg, string errorContext)
        {
            var handlers = Volatile.Read(ref _onMessageReceivedCache);
            for (int i = 0; i < handlers.Length; i++)
            {
                try { handlers[i](msg); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Bus",
                        $"{errorContext}OnMessageReceived subscriber threw on {msg.Type} from {msg.Source}", ex);
                }
            }
        }

        public event Action<string, bool>? OnClientConnectionChanged // clientId, connected
        {
            add    { lock (_connHandlerGate) { _onClientConnectionChanged += value; RebuildOnClientConnectionChangedCache(); } }
            remove { lock (_connHandlerGate) { _onClientConnectionChanged -= value; RebuildOnClientConnectionChangedCache(); } }
        }
        private Action<string, bool>? _onClientConnectionChanged;
        private readonly object _connHandlerGate = new object();
        private Action<string, bool>[] _onClientConnectionChangedCache = Array.Empty<Action<string, bool>>();
        private void RebuildOnClientConnectionChangedCache()
        {
            var list = _onClientConnectionChanged?.GetInvocationList();
            if (list == null || list.Length == 0)
            {
                Volatile.Write(ref _onClientConnectionChangedCache, Array.Empty<Action<string, bool>>());
                return;
            }
            var typed = new Action<string, bool>[list.Length];
            for (int i = 0; i < list.Length; i++) typed[i] = (Action<string, bool>)list[i];
            Volatile.Write(ref _onClientConnectionChangedCache, typed);
        }

        // Fan event invocations through per-handler try/catch so a single
        // misbehaving subscriber can't take down the receive loop or block any
        // peers from running. Mirrors the OnMessageReceived pattern below.
        //
        // Read the cached invocation array via Volatile.Read
        // rather than re-allocating a fresh Delegate[] from GetInvocationList()
        // on every call. The cache is refreshed on subscribe/unsubscribe under
        // its handler gate, so readers never need to lock.
        private void InvokeClientConnectionChanged(string clientId, bool connected)
        {
            var handlers = Volatile.Read(ref _onClientConnectionChangedCache);
            for (int i = 0; i < handlers.Length; i++)
            {
                try { handlers[i](clientId, connected); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Bus",
                        $"OnClientConnectionChanged subscriber threw for {clientId}={connected}", ex);
                }
            }
        }

        private void InvokeVisualistReady()
        {
            var handlers = Volatile.Read(ref _onVisualistReadyCache);
            for (int i = 0; i < handlers.Length; i++)
            {
                try { handlers[i](); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Bus", "OnVisualistReady subscriber threw", ex);
                }
            }
        }

        // Connection status for dashboard display.
        //
        // ★ Counts IN-PROCESS subscribers as well as WebSocket clients. This used
        // to be `_clients.ContainsKey("Architect")` alone, which was correct only
        // while Architect was a separate process. Since T15 it is a library hosted
        // in Hub's own process and connects over the InProcBus bridge, so it never
        // appears in `_clients` — the predicate was permanently FALSE in every
        // shipping build, and both Hub→Architect node-level emitters
        // (DEBUG_NODE_EXEC and DEBUG_VAR_SET in ScriptManager) were gated off. The
        // live-debug node flash has been dead the whole time, including the
        // GUID-first/title-fallback lookup repair that landed on that dead wire.
        //
        // Two definitions of "connected" disagreed and one of them was on screen:
        // ArchitectBusClient.IsConnected goes true on the in-proc bridge and paints
        // the chrome dot GREEN, so the UI asserted a link that emitted nothing.
        //
        // A remote WebSocket Architect (a future out-of-process peer, or a dev
        // attaching one) still satisfies the first half, so this is strictly wider
        // than what it replaces.
        public bool IsArchitectConnected
            => _clients.ContainsKey("Architect")
               || Volatile.Read(ref _inProcSubscribersCache).Length > 0;

        public int  ConnectedClientCount  => _clients.Count;

        // Status-strip readiness probe. True when the HttpListener owns its
        // prefix and the StartAsync accept loop is live (i.e. ready to accept new
        // bus client handshakes). Goes false during graceful shutdown.
        public bool IsListening => _httpListener?.IsListening ?? false;

        // Bus.HUDServer property was dead code: assigned by the (deleted)
        // WinForms shell and never read. HUD_BROADCAST routing happens through
        // HubHost.HUD directly, so the property is removed.

        // In-process subscriber list — Architect (and any future
        // peer that lives in Hub's process) subscribes here via the
        // InProcBus bridge, skipping the localhost-WebSocket hop. Fanned at
        // the end of BroadcastToPeersAsync (the transport tail of every
        // broadcast) alongside the remote-client send loop.
        private Action<BusMessage>[] _inProcSubscribersCache = Array.Empty<Action<BusMessage>>();
        private Action<BusMessage>? _inProcSubscribers;
        private readonly object _inProcSubscriberGate = new();

        private void RebuildInProcSubscribersCache()
        {
            var list = _inProcSubscribers?.GetInvocationList();
            if (list is null || list.Length == 0)
            {
                Volatile.Write(ref _inProcSubscribersCache, Array.Empty<Action<BusMessage>>());
                return;
            }
            var typed = new Action<BusMessage>[list.Length];
            for (int i = 0; i < list.Length; i++) typed[i] = (Action<BusMessage>)list[i];
            Volatile.Write(ref _inProcSubscribersCache, typed);
        }

        private Bus(int port = 18081)
        {
            _port = port;
            // Self-register so in-process peers can publish + subscribe
            // without the localhost-WebSocket round trip. Registration happens at
            // Bus.Instance first access; ArchitectBusClient reads InProcBus.Instance
            // at Start() (null → WebSocket fallback).
            try { InProcBus.Register(new BusInProcBridgeImpl(this)); }
            catch (Exception ex) { GlobalLogger.Error("Bus", "InProcBus.Register", ex); }

            // Invalidate the on_bus dispatch gate whenever the script registry
            // mutates (load/refresh, enable toggle, process-instance
            // register/unregister). The handler only flips a flag — the
            // recompute happens lazily on the next inbound message, so a burst
            // of OnChanged raises (e.g. RecordExecution per script run) stays
            // O(1) here.
            try { ScriptRegistry.Instance.OnChanged += () => Volatile.Write(ref _onBusHandlersDirty, 1); }
            catch (Exception ex) { GlobalLogger.Error("Bus", "ScriptRegistry.OnChanged hook", ex); }
        }

        /// <summary>
        /// Adapter that exposes Bus as an in-process publish /
        /// subscribe surface. Bridges Architect-side BusMessage traffic
        /// straight through Bus's routing without serialising to JSON +
        /// crossing the loopback WebSocket. Externally identical wire
        /// semantics — Source / Target / Type / Payload fields are
        /// dispatched the same way HandleClientAsync would route an
        /// inbound WebSocket frame.
        /// </summary>
        private sealed class BusInProcBridgeImpl : IInProcBusBridge
        {
            private readonly Bus _bus;
            public BusInProcBridgeImpl(Bus bus) { _bus = bus; }

            // IInProcBusBridge.PublishAsync's interface signature lives in
            // Shared/InProcBus.cs. We keep this method
            // matching the interface and delegate to a CT-aware private helper so
            // future callers (or a later change that widens the interface) can pass
            // a token without another shim. The interface-required entry point
            // forwards CancellationToken.None.
            //
            // TODO: widen IInProcBusBridge.PublishAsync to take a
            // CancellationToken = default so callers can thread shutdown tokens all
            // the way to ws.SendAsync.
            public Task PublishAsync(BusMessage msg) => PublishAsyncCore(msg, CancellationToken.None);

            private async Task PublishAsyncCore(BusMessage msg, CancellationToken ct)
            {
                if (msg is null) return;
                // Match the on-wire path: stamp SentAt at the bus boundary
                // so subscribers see the same field they would for a frame
                // arriving via HandleClientAsync.
                msg.SentAt = DateTime.UtcNow;

                // 1) Hub-side OnMessageReceived fanout (ScriptManager, etc.)
                //    via the shared fan helper — same per-handler isolation as
                //    the WS-arrival and Hub-origin-broadcast sites.
                _bus.FanOnMessageReceived(msg, "InProc ");

                // 2) Internal routing (visual-wait resolution, VISUAL_TRIGGER
                //    → LayerRuntime, etc.). Mirrors the WS-arrival path at
                //    line ~512.
                try { _bus.RouteIncomingMessage(msg); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Bus", $"InProc RouteIncomingMessage threw on {msg.Type}", ex);
                }

                // 3) Fan to remote WS peers so Architect→Visualist (and the
                //    rare Architect→broadcast) still reach the cross-process
                //    side. We deliberately DO NOT fan to in-proc subscribers
                //    here — Architect is the publisher and shouldn't see its
                //    own outbound MACRO_REQUEST as an inbound message.
                //
                // Pass the CT through so shutdown / a wedged peer can
                // interrupt a hung ws.SendAsync rather than wait the full
                // ClientSendLockTimeout.
                try { await _bus.BroadcastToRemoteAsync(msg, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Bus", $"InProc remote broadcast threw on {msg.Type}", ex);
                }
            }

            public void Subscribe(Action<BusMessage> handler)
            {
                if (handler is null) return;
                lock (_bus._inProcSubscriberGate)
                {
                    _bus._inProcSubscribers += handler;
                    _bus.RebuildInProcSubscribersCache();
                }
            }

            public void Unsubscribe(Action<BusMessage> handler)
            {
                if (handler is null) return;
                lock (_bus._inProcSubscriberGate)
                {
                    _bus._inProcSubscribers -= handler;
                    _bus.RebuildInProcSubscribersCache();
                }
            }
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            // Make StartAsync safe to call after a previous Stop().
            // The old listener was overwritten without disposing, leaking the socket and
            // leaving _stopped at 1 forever, which made the next bus message handler an
            // immediate no-op.
            if (Interlocked.Exchange(ref _stopped, 0) == 1)
            {
                try { _httpListener?.Close(); } catch { }
                _httpListener = null;
            }

            // If Prefixes.Add()/Start() throws (port in use,
            // ACL denied, etc.) the freshly-created HttpListener would leak its OS handle.
            // Dispose + null it on failure before rethrowing so a failed StartAsync leaves
            // no orphaned listener behind.
            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _httpListener.Start();
            }
            catch
            {
                try { _httpListener?.Close(); } catch { }
                _httpListener = null;
                throw;
            }

            GlobalLogger.Log($"Bus started on ws://127.0.0.1:{_port}/", "Bus", LogLevel.System);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        // Register the handler task so Stop() can await
                        // outstanding handlers before disposing _sendLocks. The
                        // continuation auto-evicts on completion so the dictionary
                        // never grows unbounded under healthy churn.
                        //
                        // Order is load-bearing: TryAdd must precede
                        // ContinueWith. The continuation's only job is to remove
                        // the task entry from _handlerTasks; if a synchronous
                        // continuation fires (e.g. the handler has already faulted
                        // by the time we schedule) BEFORE the TryAdd ran, the
                        // remove is a no-op and we leak the task entry the
                        // subsequent TryAdd inserts. Keep TryAdd first.
                        var handlerTask = HandleClientAsync(context, ct);
                        _handlerTasks.TryAdd(handlerTask, 0);
                        _ = handlerTask.ContinueWith(
                            t => _handlerTasks.TryRemove(t, out _),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
                // StopAsync disposes _httpListener without necessarily flipping
                // the caller's ct — so the accept loop sees ObjectDisposedException
                // (or HttpListenerException) raised synchronously from
                // GetContextAsync. Accept either signal as a shutdown — otherwise
                // every shutdown floods SystemHistory with "accept error" rows.
                catch (HttpListenerException) when (ct.IsCancellationRequested || Volatile.Read(ref _stopped) != 0) { break; }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested || Volatile.Read(ref _stopped) != 0) { break; }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Bus", "accept error", ex);
                }
            }
        }

        private async Task HandleClientAsync(HttpListenerContext context, CancellationToken ct)
        {
            // Set the keep-alive interval on the accepted WebSocket so
            // a half-open peer is detected within ~30 s. Without this, the
            // anti-displacement guard could hold a privileged slot
            // forever after a network partition; the only recovery would be a
            // manual Hub restart.
            var wsContext = await context.AcceptWebSocketAsync(
                subProtocol: null,
                receiveBufferSize: 16 * 1024,
                keepAliveInterval: WebSocketKeepAlive).ConfigureAwait(false);
            var ws = wsContext.WebSocket;

            // Validate the query-string id BEFORE accepting it.
            // Rejection here aborts the socket and returns; the previous
            // verbatim-accept path would log-inject `[CriticalError]` lines
            // (the id is interpolated into the connect/disconnect log) and
            // allow dictionary-bloat from arbitrary unique ids.
            string? rawId = context.Request.QueryString["id"];
            bool isAnonymous = false;
            string clientId;
            if (string.IsNullOrEmpty(rawId))
            {
                clientId = $"client_{Guid.NewGuid():N}".Substring(0, 12);
                isAnonymous = true;
            }
            else if (rawId.Length > MaxClientIdLength || !_clientIdRegex.IsMatch(rawId))
            {
                GlobalLogger.Log(
                    $"Bus: refusing connection with malformed client id (len={rawId.Length}) — must match [A-Za-z0-9_-]{{1,{MaxClientIdLength}}}.",
                    "Bus", LogLevel.CriticalError);
                try { ws.Abort(); } catch { }
                try { ws.Dispose(); } catch { }
                return;
            }
            else
            {
                clientId = rawId;
            }

            // Reserved names belong to the bus / browser plumbing. Any
            // peer claiming "Hub" could permanently mute legitimate triggers
            // (DispatchVisualTrigger uses Source=="Hub" as the echo-skip guard);
            // "Browser" could forge VISUAL_COMPLETE provenance. Rejection is
            // unconditional — there is no legitimate use-case for an external
            // peer to introduce itself as either name.
            if (IsReservedClientId(clientId))
            {
                GlobalLogger.Log(
                    $"Bus: refusing reserved client id '{clientId}' — that name is owned by the bus itself.",
                    "Bus", LogLevel.CriticalError);
                try { ws.Abort(); } catch { }
                try { ws.Dispose(); } catch { }
                return;
            }

            // Anonymous-client ceiling. A hostile loopback peer can
            // open + drop anonymous connections faster than the handler's
            // finally block evicts the entry; without a cap, _clients +
            // _sendLocks bloat without bound. Privileged / named-id peers are
            // not subject to the cap (Architect / Visualist + future tools).
            if (isAnonymous)
            {
                int current = Interlocked.Increment(ref _anonymousClientCount);
                if (current > MaxAnonymousClients)
                {
                    Interlocked.Decrement(ref _anonymousClientCount);
                    GlobalLogger.Log(
                        $"Bus: refusing anonymous connection — {current - 1} anonymous clients already attached (max {MaxAnonymousClients}).",
                        "Bus", LogLevel.CriticalError);
                    try { ws.Abort(); } catch { }
                    try { ws.Dispose(); } catch { }
                    return;
                }
            }

            // Refuse a second connection that claims a privileged identity.
            // Previously the snapshot+swap below treated all reconnects identically,
            // so any local process could `?id=Architect` and the existing Architect
            // peer would be Abort()'d off the bus. Loopback-only limits the threat
            // model, but the displace-incumbent behavior is also an availability
            // hazard for benign accidental collisions (e.g. a second Architect
            // launched by mistake silently kicks the first).
            // Anonymous / `client_*` ids retain the displacement semantics —
            // they're temporary by construction and don't have an "incumbent" to
            // protect.
            if (IsPrivilegedClientId(clientId) && _clients.ContainsKey(clientId))
            {
                GlobalLogger.Log(
                    $"Bus: refusing duplicate '{clientId}' connection — the incumbent peer is retained. " +
                    "If the previous Architect/Visualist process crashed, restart Hub or wait for its socket to close.",
                    "Bus", LogLevel.CriticalError);
                try { ws.Abort(); } catch { }
                try { ws.Dispose(); } catch { }
                if (isAnonymous) Interlocked.Decrement(ref _anonymousClientCount);
                return;
            }

            // Atomic snapshot+swap on reconnect.
            //
            // The earlier fix already aborted the prior socket and identity-checked
            // eviction in the OLD handler's finally. That narrowed the race but left
            // a TOCTOU: a send on the old socket could be mid-WaitAsync on the
            // per-client SemaphoreSlim while the OLD finally disposed it, throwing
            // ObjectDisposedException.
            //
            // Fix: under _reconnectGate, snapshot the OLD ws + OLD semaphore, then
            // install the NEW ws + a FRESH semaphore in one critical section. The
            // OLD semaphore is drained best-effort and disposed off-thread so a
            // stuck send cannot block reconnect. The OLD handler's finally only
            // evicts dictionary entries it still owns (ReferenceEquals checks).
            WebSocket? oldSocket = null;
            SemaphoreSlim? oldSendLock = null;
            lock (_reconnectGate)
            {
                if (_clients.TryGetValue(clientId, out var existing) && !ReferenceEquals(existing, ws))
                {
                    oldSocket = existing;
                    _sendLocks.TryRemove(clientId, out oldSendLock);
                }
                _clients[clientId] = ws;
                // Pre-install a fresh send lock so the very first BroadcastAsync that
                // races our finish-up path picks up the new semaphore, not a leftover
                // from the displaced connection.
                _sendLocks[clientId] = new SemaphoreSlim(1, 1);
            }

            if (oldSocket != null)
            {
                try { oldSocket.Abort(); } catch { }
            }
            if (oldSendLock != null)
            {
                // Best-effort drain so we don't dispose a semaphore another thread
                // is mid-WaitAsync on. If a send is genuinely stuck we still dispose
                // (better to leak the post-Wait Release than to block reconnect).
                _ = AsyncErrorBoundary.SafeRunAsync(async () =>
                {
                    bool drained = false;
                    try { drained = await oldSendLock.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (ObjectDisposedException) { /* already gone */ }
                    if (!drained)
                    {
                        GlobalLogger.Log(
                            $"Bus client {clientId}: prior send lock did not drain within 2s — disposing anyway.",
                            "Bus", LogLevel.Communication);
                    }
                    try { oldSendLock.Dispose(); } catch { }
                }, "Bus", $"reconnect drain for {clientId}");
            }

            // Fire OnClientConnectionChanged through per-handler try/catch
            // so a throwing subscriber can't take out this handler at startup.
            InvokeClientConnectionChanged(clientId, true);
            GlobalLogger.Log($"Bus client connected: {clientId}", "Bus", LogLevel.Communication);

            // Accumulate fragmented frames; previously a single 32 KB ReceiveAsync
            // truncated any message larger than the buffer. Now we buffer until
            // EndOfMessage and decode the full payload.
            const int MaxBusMessageBytes = 4 * 1024 * 1024; // hard DoS cap
            byte[] readBuf = new byte[32768];
            var seg = new ArraySegment<byte>(readBuf);
            bool closed = false;
            try
            {
                while (!closed && ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    bool isText = true;
                    do
                    {
                        result = await ws.ReceiveAsync(seg, ct);
                        if (result.MessageType == WebSocketMessageType.Close) { closed = true; break; }
                        if (result.MessageType != WebSocketMessageType.Text) { isText = false; }
                        if (isText && result.Count > 0)
                        {
                            if (ms.Length + result.Count > MaxBusMessageBytes)
                            {
                                GlobalLogger.Log($"Bus: bus client {clientId} message > {MaxBusMessageBytes} bytes — aborting socket.", "Bus", LogLevel.CriticalError);
                                try { ws.Abort(); } catch { }
                                closed = true;
                                break;
                            }
                            ms.Write(readBuf, 0, result.Count);
                        }
                    } while (!result.EndOfMessage);

                    if (closed) break;
                    if (!isText || ms.Length == 0) continue;

                    string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    try
                    {
                        var msg = JsonSerializer.Deserialize<BusMessage>(json);
                        if (msg != null)
                        {
                            // Per-field length caps. The aggregate
                            // MaxBusMessageBytes guard above (4 MiB) defends
                            // against payload bloat but lets a tiny envelope ship
                            // arbitrarily-long Type / Target strings that downstream
                            // consumers (log lines, RouteIncomingMessage's prefix-key
                            // construction "{type}:{payload}:" → ConcurrentDictionary
                            // key cost, JSON re-serialization in echo paths) all pay
                            // per-character. Cap at 64 chars each — matches the
                            // _clientIdRegex bound and is generous for every legitimate
                            // envelope (current message types are all ≤ 24 chars;
                            // privileged client targets are ≤ 9 chars). Source is
                            // overwritten with clientId on the next line so its
                            // caller-supplied value is moot; no need to cap.
                            const int MaxBusFieldLength = 64;
                            if ((msg.Type?.Length ?? 0) > MaxBusFieldLength ||
                                (msg.Target?.Length ?? 0) > MaxBusFieldLength)
                            {
                                GlobalLogger.Log(
                                    $"Bus: bus client {clientId} sent envelope with oversized header (Type or Target > {MaxBusFieldLength} chars) — dropping.",
                                    "Bus", LogLevel.Communication);
                                continue;
                            }
                            msg.Source = clientId;
                            // Clock-drift defence. The wire envelope's SentAt is
                            // populated by the sender's local clock (Architect/Visualist
                            // can be on machines with arbitrary skew, or even just NTP-
                            // adjusting mid-session). Downstream consumers — log timestamps,
                            // wait-timeout audits, MACRO_SYNC ordering — assume a single
                            // monotonic time source. Overwrite with the server's UtcNow at
                            // receive so every consumer sees Hub-local time.
                            //
                            // BusMessage doesn't currently carry a "sender-reported"
                            // companion field; that loss is acceptable for v1. If we ever
                            // need both, add a SenderReportedSentAt property to the model
                            // (the model file is off-limits for this sweep) and stamp both
                            // here.
                            msg.SentAt = DateTime.UtcNow;
                            // Fan handlers via the shared helper — per-handler
                            // try/catch so one throwing subscriber can't kill the
                            // receive loop or starve later handlers (msg.Source is
                            // clientId here; it was overwritten above).
                            FanOnMessageReceived(msg, "");
                            RouteIncomingMessage(msg);
                        }
                    }
                    catch (Exception msgEx)
                    {
                        GlobalLogger.Error("Bus", $"malformed message from {clientId}", msgEx);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Aborted is the controlled-exit state we put the socket in when a
                // newer connection from the same id arrives — don't log that as an error.
                if (ws.State != WebSocketState.Aborted)
                    GlobalLogger.Error("Bus", $"bus client {clientId} error", ex);
            }
            finally
            {
                // Only this handler's own (ws, sendLock) pair gets reaped.
                // A newer connection from the same id may already have taken over
                // and installed a fresh sendLock; evicting either would silently
                // break the live connection. The reconnect path above is what
                // disposes the OLD sendLock, so we don't re-do it here.
                //
                // Atomic compare-and-remove on _sendLocks.
                //
                // We snapshot OUR sendLock under _reconnectGate at the top of
                // HandleClientAsync (it's the one installed alongside our ws).
                // The plain TryRemove(key, out) overload was previously a TOCTOU:
                // a reconnect could swap a fresh lock in between our gate-held
                // _clients identity check and the dictionary remove, and we'd
                // dispose someone else's lock.
                //
                // The KeyValuePair overload of TryRemove only removes if the
                // entry's value still equals the snapshot — IConcurrentDictionary
                // guarantees that compare-and-remove is atomic. Pair this with
                // the _reconnectGate-held _clients identity check so the pair
                // (ws, sendLock) is reaped together or not at all.
                SemaphoreSlim? mySendLock = null;
                lock (_reconnectGate)
                {
                    // Capture the lock we'd own if no reconnect happened — i.e.
                    // the entry whose ws is ours.
                    if (_clients.TryGetValue(clientId, out var current) && ReferenceEquals(current, ws))
                    {
                        _sendLocks.TryGetValue(clientId, out mySendLock);
                    }
                }

                bool removed = ((ICollection<KeyValuePair<string, WebSocket>>)_clients)
                    .Remove(new KeyValuePair<string, WebSocket>(clientId, ws));
                if (removed && mySendLock != null)
                {
                    // Atomic compare-and-remove: only evict the sendLock entry
                    // if it's still the snapshot we captured. If a reconnect
                    // raced in between our snapshot and here, the dictionary
                    // value has changed and TryRemove(KeyValuePair) is a no-op,
                    // leaving the new connection's fresh lock in place.
                    bool sendLockEvicted = ((ICollection<KeyValuePair<string, SemaphoreSlim>>)_sendLocks)
                        .Remove(new KeyValuePair<string, SemaphoreSlim>(clientId, mySendLock));
                    if (sendLockEvicted)
                    {
                        try { mySendLock.Dispose(); }
                        catch (ObjectDisposedException) { /* race with Stop(); fine */ }
                    }
                    // else: a reconnect installed a fresh lock between our
                    // snapshot and here — that path owns disposal of OUR lock
                    // (it was passed as oldSendLock to the reconnect's drain).
                }
                if (removed)
                {
                    InvokeClientConnectionChanged(clientId, false);
                    GlobalLogger.Log($"Bus client disconnected: {clientId}", "Bus", LogLevel.Communication);
                }

                // Release the anonymous-client slot now that the entry
                // has been (potentially) evicted from _clients. Done unconditionally
                // on the isAnonymous flag rather than gated on `removed` because a
                // reconnect race may have already taken our slot — the anonymous
                // count still reflects "this connection attempt is done."
                if (isAnonymous) Interlocked.Decrement(ref _anonymousClientCount);

                // ConfigureAwait(false) so a shutdown path that awaits
                // outstanding handlers (Bus.StopAsync's drain block) doesn't
                // bounce the continuation back to the original SyncContext.
                if (ws.State != WebSocketState.Closed)
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
                ws.Dispose();
            }
        }

        // VISUAL_COMPLETE-specific cleanup on Visualist disconnect (legacy) was removed in Phase 2:
        // the new runtime owns its own queue/completion lifecycle via LayerRuntime, and there is
        // no Visualist-side TCS to cancel.

        // ── Events for new message types ─────────────────────────────────
        /// <summary>Fired when Visualist sends VISUAL_READY (connected and queues initialized).</summary>
        public event Action? OnVisualistReady
        {
            add    { lock (_visualistReadyGate) { _onVisualistReady += value; RebuildOnVisualistReadyCache(); } }
            remove { lock (_visualistReadyGate) { _onVisualistReady -= value; RebuildOnVisualistReadyCache(); } }
        }
        private Action? _onVisualistReady;
        private readonly object _visualistReadyGate = new object();
        private Action[] _onVisualistReadyCache = Array.Empty<Action>();
        private void RebuildOnVisualistReadyCache()
        {
            var list = _onVisualistReady?.GetInvocationList();
            if (list == null || list.Length == 0)
            {
                Volatile.Write(ref _onVisualistReadyCache, Array.Empty<Action>());
                return;
            }
            var typed = new Action[list.Length];
            for (int i = 0; i < list.Length; i++) typed[i] = (Action)list[i];
            Volatile.Write(ref _onVisualistReadyCache, typed);
        }

        /// <summary>
        /// Fired when an OBS scene change is broadcast via
        /// <see cref="BroadcastSceneChangedAsync"/>. The argument is the scene name.
        ///
        /// Mirrors the OnVisualistReady / OnClientConnectionChanged pattern:
        /// gives future Hub-internal scene-aware features (e.g. auto-pause widgets when
        /// leaving a streaming scene) a clean subscription surface without piggy-backing
        /// on the wire-level broadcast. Currently has no Hub-side subscribers — this is
        /// intentional. External clients (Architect) still receive the SCENE_CHANGED bus
        /// message via the normal client fan-out.
        ///
        /// TODO: when a real Hub subscriber is added, prefer this event over
        /// inspecting raw bus traffic through OnMessageReceived.
        /// </summary>
        public event Action<string>? OnSceneChanged
        {
            add    { lock (_sceneChangedGate) { _onSceneChanged += value; RebuildOnSceneChangedCache(); } }
            remove { lock (_sceneChangedGate) { _onSceneChanged -= value; RebuildOnSceneChangedCache(); } }
        }
        private Action<string>? _onSceneChanged;
        private readonly object _sceneChangedGate = new object();
        private Action<string>[] _onSceneChangedCache = Array.Empty<Action<string>>();
        private void RebuildOnSceneChangedCache()
        {
            var list = _onSceneChanged?.GetInvocationList();
            if (list == null || list.Length == 0)
            {
                Volatile.Write(ref _onSceneChangedCache, Array.Empty<Action<string>>());
                return;
            }
            var typed = new Action<string>[list.Length];
            for (int i = 0; i < list.Length; i++) typed[i] = (Action<string>)list[i];
            Volatile.Write(ref _onSceneChangedCache, typed);
        }

        // Gate for the per-message on_bus fan-out in DispatchOnBusScripts
        // (reached from RouteIncomingMessage's tail for peer frames and from
        // BroadcastAsync's Hub-local delivery for Hub-origin broadcast
        // frames). Building the busVars dictionary + closure and
        // entering ExecuteOnBusScriptsAsync (init-gate await + registry scan)
        // costs allocations on EVERY bus message even when no script declares an
        // on_bus header. Cache "any enabled script carries an on_bus block" and
        // skip the dispatch entirely when none does — the predicate is a strict
        // superset of the per-type match inside ExecuteOnBusScriptsAsync, so a
        // skip only ever elides a provably-empty scan.
        //
        // Starts TRUE so messages arriving before the registry's first load keep
        // pre-gate semantics (ExecuteOnBusScriptsAsync's own _initTask await
        // covers the not-yet-loaded window); the first OnChanged — raised at the
        // end of LoadScripts — marks the cache dirty and the next message
        // recomputes from the populated registry.
        private volatile bool _anyOnBusHandlers = true;
        private int _onBusHandlersDirty;
        private static readonly Func<ScriptInfo, bool> _hasOnBusHeader =
            static s => s.BusEventTypes.Count > 0;

        private bool AnyOnBusHandlers()
        {
            // Clear the dirty flag BEFORE scanning so a registry change that
            // lands mid-scan re-marks it and the next message recomputes from
            // fresh state (invalidate-before-read).
            if (Interlocked.Exchange(ref _onBusHandlersDirty, 0) == 1)
            {
                bool any = false;
                try
                {
                    foreach (var _ in ScriptRegistry.Instance.WhereEnabled(_hasOnBusHeader))
                    {
                        any = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // On any doubt, dispatch — the gate must only skip work that
                    // is provably a no-op.
                    GlobalLogger.Error("Bus", "on_bus gate recompute failed", ex);
                    any = true;
                }
                _anyOnBusHandlers = any;
            }
            return _anyOnBusHandlers;
        }

        private void RouteIncomingMessage(BusMessage msg)
        {
            // Structure: the switch below holds the INFRASTRUCTURE arms —
            // reactions to PEER frames (WS receive loop + in-proc bridge are the
            // only two callers). The Hub-consumer legs (pending-wait resolution +
            // on_bus dispatch) live in DeliverToHubConsumers, called as this
            // method's tail AND from BroadcastAsync for Hub-origin broadcast
            // frames — Hub-origin frames deliberately get the consumer legs
            // WITHOUT this switch (see the DeliverToHubConsumers doc for why
            // running these arms on Hub's own broadcasts would double-dispatch
            // visual triggers and re-enter the MACRO_* arms).

            // Named event routing
            switch (msg.Type)
            {
                case "VISUAL_READY":
                    InvokeVisualistReady();
                    GlobalLogger.Log("Visualist is ready.", "Bus", LogLevel.System);
                    break;

                case "VISUAL_TRIGGER":
                    DispatchVisualTrigger(msg);
                    break;

                case "WIDGET_UPDATE":
                    DispatchWidgetLiveUpdate(msg);
                    break;

                case "VISUAL_COMPLETE":
                    // Bus-relayed VISUAL_COMPLETE. The browser-direct path is
                    // already wired through HUDServer → LayerRuntime → ResolveVisualWait,
                    // but a relay (e.g. Visualist forwards a completion through the bus)
                    // wasn't being matched. Look for a waitId in the payload — accept
                    // either a bare string or `{ "waitId": "<hex>" }` JSON.
                    {
                        string waitId = msg.Payload?.Trim() ?? "";
                        if (waitId.StartsWith("{"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(waitId);
                                if (doc.RootElement.TryGetProperty("waitId", out var w))
                                    waitId = w.GetString() ?? "";
                            }
                            catch (JsonException ex)
                            {
                                GlobalLogger.Log(
                                    $"Bus: VISUAL_COMPLETE with malformed JSON payload — treating as bare waitId. {ex.Message}",
                                    "Bus", LogLevel.Communication);
                            }
                        }
                        // V13 §8.1 — DELIBERATELY no payload on this route. The
                        // completion payload is a browser capability that arrives on
                        // /hud/<layerId>, where HUDServer classifies the socket and
                        // strips the payload from an Untrusted one. A bus peer has no
                        // equivalent classification, so honouring a `payload` property
                        // here would hand every connected bus client an unfiltered write
                        // into `global._wait_payload` — the exact surface §8.3 exists to
                        // narrow. The relay still RESOLVES the wait (dropping that would
                        // hang the script); it just cannot supply data.
                        if (!string.IsNullOrEmpty(waitId))
                            ResolveVisualWait(waitId);
                    }
                    break;

                case "LOGIC_RELOAD":
                    // OFF-CALLER-THREAD refresh, leading+trailing edged. The old
                    // direct ScriptRegistry.Instance.Refresh() here ran
                    // synchronously on the CALLER's thread — and with the
                    // in-proc bridge that caller is the Architect UI thread
                    // (SaveAsync → SendAsync → PublishAsync's synchronous prefix
                    // → RouteIncomingMessage). Refresh() is a full logic-dir
                    // rescan whose per-file WaitForFileStable can Thread.Sleep
                    // up to ~1.5s on a locked .phx (the LogicWatcher backup
                    // writer holds FileShare.None on the very file that was just
                    // saved) — so every save stalled the UI thread, and a rapid
                    // graph-switch save storm serialized several rescans on it
                    // back-to-back (2026-07-22 streaming-PC freeze log).
                    //
                    // But the bus path is ALSO the fast-freshness path: it is
                    // what makes a chat command arriving right after a save run
                    // the NEW script text (the disk-watcher path is ~500ms+
                    // behind — double debounce + stability wait). So a plain
                    // trailing debounce would widen the post-save stale window
                    // from ~0 to ≥250ms. Hence LEADING edge: after a quiet
                    // period the refresh fires IMMEDIATELY on a thread-pool
                    // thread (freshness ≈ the rescan duration itself, UI thread
                    // untouched); messages inside the window coalesce onto ONE
                    // trailing 250ms refresh (storm → 2 background rescans
                    // total, and the final state always wins).
                    {
                        string reloadSource = msg.Source ?? "?";
                        long nowMs = Environment.TickCount64;
                        long last = Interlocked.Read(ref _lastLogicReloadRefreshMs);
                        bool leading = nowMs - last >= LogicReloadDebounceMs
                            && Interlocked.CompareExchange(ref _lastLogicReloadRefreshMs, nowMs, last) == last;
                        if (leading)
                        {
                            _ = AsyncErrorBoundary.SafeRunAsync(
                                () => Task.Run(() => RunLogicReloadRefresh(reloadSource, "immediate")),
                                "Bus", "LOGIC_RELOAD immediate refresh");
                        }
                        else
                        {
                            _logicReloadDebouncer.Schedule(LogicReloadDebounceKey, LogicReloadDebounceMs, () =>
                            {
                                Interlocked.Exchange(ref _lastLogicReloadRefreshMs, Environment.TickCount64);
                                RunLogicReloadRefresh(reloadSource, "coalesced");
                            });
                        }
                    }
                    // Route the ACK broadcast through SafeRunAsync.
                    // The bare `_ = BroadcastAsync(...)` swallowed any fault inside
                    // the broadcast loop; if a peer's send threw and the loop
                    // tried to log via GlobalLogger.Error from inside the bus
                    // receive thread, that exception would escape unobserved.
                    // The ACK stays IMMEDIATE (not debounced) — it acknowledges
                    // receipt, not completion, and Architect's round-trip signal
                    // must not lag behind the coalesce window.
                    {
                        // Coalesce the possibly-null inbound
                        // payload before it feeds BusMessage.Payload — matches the
                        // `?? "[]"` / `?? "{}"` idioms used by the neighbouring cases.
                        string ackPayload = msg.Payload ?? "{}";
                        // Transport-only send (BroadcastToPeersAsync, not
                        // BroadcastAsync): the ACK is an infrastructure echo for the
                        // editor that requested the reload, never a Hub-consumer
                        // event. Targeted at "Architect" so BroadcastAsync's local
                        // Target gate would skip it anyway; converted for uniformity
                        // with the MACRO_* arms below.
                        _ = AsyncErrorBoundary.SafeRunAsync(
                            () => BroadcastToPeersAsync(new BusMessage
                            {
                                Type   = "LOGIC_RELOAD_ACK",
                                Source = "Hub",
                                Target = "Architect",
                                Payload = ackPayload
                            }),
                            "Bus", "LOGIC_RELOAD_ACK broadcast");
                    }
                    break;

                case "MACRO_SYNC":
                {
                    // MergeMacroLibrary does synchronous File.ReadAllText/WriteAllText
                    // under a process-wide lock. Running it on the bus receive thread blocks
                    // every subsequent message until disk IO completes (worse under macro-sync
                    // bursts since the gate serializes them). Offload to a worker; the merge
                    // result feeds the rebroadcast, so the broadcast moves with it.
                    //
                    // Ordering note: this changes MACRO_SYNC handling from synchronous-on-receive
                    // to fire-and-forget. The library lock (_macroLibraryGate inside
                    // MergeMacroLibrary) still serializes concurrent merges across workers, so
                    // the data integrity contract (exercised by MacroBusAuditTests) is preserved.
                    // No caller of RouteIncomingMessage awaits this side-effect, so no
                    // observable consumer depends on the merge being complete before the next
                    // bus message returns.
                    string srcLabel = msg.Source;
                    string syncPayload = msg.Payload ?? "[]";
                    _ = AsyncErrorBoundary.SafeRunAsync(async () =>
                    {
                        string merged = await Task.Run(() => MergeMacroLibrary(syncPayload, removedId: null)).ConfigureAwait(false);
                        GlobalLogger.Log($"MACRO_SYNC from {srcLabel} — library updated.", "Bus", LogLevel.System);
                        // Transport-only re-broadcast (BroadcastToPeersAsync,
                        // NOT BroadcastAsync). This frame is Target "*", so the full
                        // BroadcastAsync would hand it to Hub's own consumers — and
                        // if local delivery ever grew a routing leg, this very arm
                        // would re-enter: MACRO_SYNC → MergeMacroLibrary →
                        // re-broadcast → ∞, with a file merge per iteration. The
                        // merged library is peer-facing infrastructure state, not a
                        // Hub-consumer event; keep it off the local legs entirely.
                        await BroadcastToPeersAsync(new BusMessage { Type = "MACRO_SYNC", Source = "Hub", Target = "*", Payload = merged }).ConfigureAwait(false);
                    }, "Bus", $"MACRO_SYNC from {srcLabel}");
                    break;
                }

                case "MACRO_REMOVE":
                {
                    // Same offload rationale as MACRO_SYNC above.
                    //
                    // D7 2026-08-02 — kept deliberately despite ZERO in-suite
                    // producers: Architect never sends MACRO_REMOVE (peer
                    // deletion propagates via MACRO_SYNC canonical-library
                    // diffing — see MacroOps.ApplyCanonicalGlobalMacros). This
                    // arm stays as inbound API surface for external clients on
                    // the open local bus (ws://127.0.0.1:18081); deleting a
                    // working inbound handler would be behavior removal, not
                    // dead-code cleanup.
                    string srcLabel  = msg.Source;
                    string? removeId = msg.Payload?.Trim();
                    _ = AsyncErrorBoundary.SafeRunAsync(async () =>
                    {
                        string merged = await Task.Run(() => MergeMacroLibrary("[]", removedId: removeId)).ConfigureAwait(false);
                        GlobalLogger.Log($"MACRO_REMOVE '{removeId}' from {srcLabel}.", "Bus", LogLevel.System);
                        // Transport-only — same recursion rationale as the
                        // MACRO_SYNC arm above (this re-broadcast is also a
                        // Target "*" MACRO_SYNC).
                        await BroadcastToPeersAsync(new BusMessage { Type = "MACRO_SYNC", Source = "Hub", Target = "*", Payload = merged }).ConfigureAwait(false);
                    }, "Bus", $"MACRO_REMOVE '{removeId}' from {srcLabel}");
                    break;
                }

                case "MACRO_REQUEST":
                {
                    // File.ReadAllText also blocks the receive loop. Same offload pattern.
                    string requesterLabel = msg.Source;
                    _ = AsyncErrorBoundary.SafeRunAsync(async () =>
                    {
                        string macroPath = GetGlobalMacroPath();
                        string payload = await Task.Run(() => File.Exists(macroPath) ? File.ReadAllText(macroPath) : "[]").ConfigureAwait(false);
                        // Transport-only for uniformity with the arms above.
                        // The reply targets the requester's label (non-wildcard),
                        // so BroadcastAsync's local Target gate would skip Hub-local
                        // delivery anyway — this just keeps every infrastructure
                        // echo on the same send shape.
                        await BroadcastToPeersAsync(new BusMessage { Type = "MACRO_SYNC", Source = "Hub", Target = requesterLabel, Payload = payload }).ConfigureAwait(false);
                    }, "Bus", $"MACRO_REQUEST from {requesterLabel}");
                    break;
                }

                // Acknowledge AI_CHUNK and WIDGET_TIMEOUT in the
                // routing table even though they need no transport-side dispatch.
                //
                // Both are emitted with Target = "*" from Hub-internal callers
                // (AI_CHUNK from ScriptManager.AI.cs's streaming loop, WIDGET_TIMEOUT
                // from WidgetTriggerQueue.cs when a widget completion times out).
                // As Hub-origin broadcasts they now reach scripts through
                // BroadcastAsync's local delivery (DeliverToHubConsumers) at send
                // time — they no longer depend on arriving back from a peer.
                //
                // Script-side delivery for PEER-sent copies of these types is
                // handled by DeliverToHubConsumers in this method's tail —
                // `on_bus("AI_CHUNK")` and `on_bus("WIDGET_TIMEOUT")` fire from
                // there. These explicit arms exist so the switch is a complete
                // routing audit: any reader can see at a glance that the message
                // type is known, intentionally has no transport-side fan-out, and
                // reaches scripts via on_bus.
                //
                // If a future feature needs an in-process listener (e.g. a Hub-side
                // "abandon all in-flight AI calls on widget timeout" rule), wire it
                // into the matching case here rather than in the on_bus path so the
                // dispatch isn't gated on a script's existence.
                case "AI_CHUNK":
                case "WIDGET_TIMEOUT":
                    break;
            }

            // Shared Hub-consumer tail — pending-wait resolution + on_bus
            // dispatch. BroadcastAsync calls the same helper for Hub-origin
            // broadcast frames, so the two delivery paths cannot diverge. (The
            // OnMessageReceived fan is NOT in the helper: both arrival callers
            // fanned it before invoking this method.)
            DeliverToHubConsumers(msg);
        }

        /// <summary>
        /// The Hub-side CONSUMER legs of message delivery — everything a frame
        /// owes to Hub's own in-process consumers, shared verbatim between the
        /// two paths that owe it:
        ///   * the arrival tail (<see cref="RouteIncomingMessage"/> — peer frames
        ///     from the WS receive loop / in-proc bridge), and
        ///   * <see cref="BroadcastAsync"/>'s Hub-origin local delivery (gated on
        ///     Target == "*" or "Hub"), which is what makes bus.send/bus.broadcast
        ///     scripts, on_bus handlers, Async.WaitForEvent and the Live Feed see
        ///     Hub-origin events at all.
        ///
        /// <para>DELIBERATELY NOT the <see cref="RouteIncomingMessage"/> switch.
        /// Those arms are infrastructure reactions to PEER frames and must not run
        /// for Hub-origin broadcasts. Concretely: a Hub-origin VISUAL_TRIGGER
        /// already reaches LayerRuntime through its own direct path — visual.trigger
        /// / visual.trigger_queued (ScriptManager.Visual.cs) and the alerts/loyalty
        /// fan-out (FireVisualTriggerFanOutAsync) all call
        /// <see cref="TriggerVisualQueuedAsync"/> or
        /// <see cref="TriggerVisualAndWaitWithPayloadAsync"/>, both of which enqueue
        /// into LayerRuntime.Instance.EnqueueTriggerAsync BEFORE broadcasting the
        /// bus envelope. Running DispatchVisualTrigger here would enqueue every
        /// Hub-origin trigger a second time (double-firing every widget; the
        /// Source=="Hub" skip inside DispatchVisualTrigger is a second net, not a
        /// license). The MACRO_* arms are the same hazard in recursion form —
        /// re-entering the arm that produced the frame.</para>
        ///
        /// <para>Pending-wait resolution is INCLUDED on purpose: it is single-shot
        /// safe for Hub-origin frames because (a) DrainPrefixBucket's
        /// _pendingWaits.TryRemove is an atomic first-wins per waiter, so one frame
        /// can never resolve the same waiter twice, and (b) a Hub-origin frame
        /// traverses exactly ONE delivery site (this call from BroadcastAsync) —
        /// it never re-enters an arrival path, since the WS receive loop overwrites
        /// Source with the peer's clientId and "Hub" is a reserved client id, so
        /// any peer echo is a distinct new frame.</para>
        /// </summary>
        private void DeliverToHubConsumers(BusMessage msg)
        {
            ResolvePendingEventWaits(msg);
            DispatchOnBusScripts(msg);
        }

        // Wait keys are stored as `${type}:${filter}:${waitId}` so multiple
        // callers can await the same (type, filter) pair without colliding.
        // Drain every waiter whose prefix matches the message — both
        // specific-payload waits and wildcard-payload waits.
        //
        // O(1) prefix lookup via _waitPrefixIndex instead of
        // a per-message ToArray() snapshot + linear StartsWith scan over every
        // pending wait. The two prefix buckets we need to drain are looked up
        // directly; matched entries are removed atomically from both _pendingWaits
        // and the prefix bucket. Visual waits (bare-guid keys) are unaffected —
        // they're never inserted into the prefix index.
        //
        // Skip the whole drain when nothing is pending — the specific
        // prefix interpolates the FULL payload into a string, a payload-sized
        // allocation per inbound message that matches nothing for the vast
        // majority of traffic (waiters exist only while a script awaits a bus
        // response). _pendingWaits also holds visual waits (bare-guid keys),
        // so an active visual wait occasionally lets a non-matching drain
        // through; that only costs what every message paid before the guard.
        private void ResolvePendingEventWaits(BusMessage msg)
        {
            if (!_pendingWaits.IsEmpty)
            {
                string specificPrefix = $"{msg.Type}:{msg.Payload ?? ""}:";
                string wildcardPrefix = $"{msg.Type}:*:";
                DrainPrefixBucket(specificPrefix, msg);
                DrainPrefixBucket(wildcardPrefix, msg);
            }
        }

        // Route bus messages to on_bus({Type}): script blocks. bus.target
        // is exposed alongside bus.source so the Bus.OnMessage Source/Target
        // wildcard guards (emitted by ScriptExporter) can compare against it.
        // Defensive null coalesce — Target defaults to "" on the model but a
        // legacy sender could set it null explicitly.
        //
        // Gated on the cached on_bus probe: when no enabled script carries an
        // on_bus header, the dictionary + closure + ExecuteOnBusScriptsAsync
        // scan would all be dead weight — skip them.
        private void DispatchOnBusScripts(BusMessage msg)
        {
            if (AnyOnBusHandlers())
            {
                // Relay-depth gate. Before D0a a Hub-origin broadcast never reached
                // Hub's own on_bus handlers, so a script that answered a bus message
                // by broadcasting could not re-trigger itself. Local delivery closes
                // that loop, and NOTHING else bounds it: every generation is a fresh
                // fire-and-forget execution, so ExecuteScriptAsync resets
                // _executionDepth (ScriptEngine) and MaxExecutionDepth never sees a
                // chain. Two authoring shapes reach it — a self-loop (on_bus("X") that
                // broadcasts "X") and a ping-pong across two scripts (A answers "X"
                // with "Y", B answers "Y" with "X"), the second of which no per-
                // execution cap could ever catch because each execution completes
                // normally.
                //
                // The counter rides the ambient ExecutionContext: SafeRunAsync below
                // captures it, so the value set for generation N is what generation
                // N+1 observes when its script's bus.broadcast re-enters here. A child
                // never writes back to its parent's flow, so sibling relays are
                // measured independently and a legitimate fan-out is not penalised by
                // a busy neighbour.
                int depth = _busRelayDepth.Value;
                if (depth >= MaxBusRelayDepth)
                {
                    ReportBusRelayDepthExceededOnce(msg.Type);
                    return;
                }
                int next = depth + 1;

                var busVars = new Dictionary<string, string>
                {
                    { "bus.type",    msg.Type           },
                    { "bus.source",  msg.Source         },
                    { "bus.target",  msg.Target ?? ""   },
                    { "bus.payload", msg.Payload ?? ""  }
                };
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () =>
                    {
                        _busRelayDepth.Value = next;
                        return ScriptManager.Instance.ExecuteOnBusScriptsAsync(msg.Type, busVars);
                    },
                    "Bus", $"on_bus({msg.Type})");
            }
        }

        /// <summary>
        /// How many chained on_bus relays one originating bus message may cause before
        /// the chain is cut. A generation is "an on_bus script ran and broadcast
        /// something that reached on_bus again", so this bounds a RELAY CHAIN, never
        /// the number of scripts answering one message (siblings each start from the
        /// same inherited depth) and never ordinary traffic (a message nobody relays
        /// dispatches at depth 0 forever).
        /// </summary>
        internal const int MaxBusRelayDepth = 8;

        // Ambient per-chain relay counter — see DispatchOnBusScripts. AsyncLocal (not a
        // field) because the chain is the unit being measured and it crosses
        // fire-and-forget task boundaries that no lock or instance field would follow.
        private readonly AsyncLocal<int> _busRelayDepth = new();

        // One CriticalError per message type per process. A tripped relay chain is an
        // authoring bug that repeats at the speed of the loop, so logging per
        // occurrence would bury the log in the same flood the cap exists to stop.
        private readonly ConcurrentDictionary<string, byte> _busRelayDepthReported = new();

        private void ReportBusRelayDepthExceededOnce(string msgType)
        {
            string key = string.IsNullOrEmpty(msgType) ? "(none)" : msgType;
            if (!_busRelayDepthReported.TryAdd(key, 1)) return;
            GlobalLogger.Log(
                $"on_bus relay chain for '{key}' hit the depth cap ({MaxBusRelayDepth}) and was cut. " +
                "A script answering this bus message is broadcasting a message that reaches on_bus again — " +
                "check for a Bus.OnMessage/Bus.Broadcast loop (either one script re-emitting its own event, " +
                "or two scripts answering each other). Further trips for this type are not logged.",
                "Bus", LogLevel.CriticalError);
        }

        // Register a prefix-keyed wait so RouteIncomingMessage
        // can find it via O(1) bucket lookup instead of scanning every pending wait.
        // The bucket keys reference _pendingWaits entries; removal of a wait must go
        // through UnregisterEventWait so both the dictionary and the bucket stay in sync.
        private void RegisterEventWait(string waitKey, string prefix, TaskCompletionSource<BusMessage> tcs)
        {
            _pendingWaits[waitKey] = tcs;
            var bucket = _waitPrefixIndex.GetOrAdd(prefix, _ => new ConcurrentDictionary<string, byte>());
            bucket[waitKey] = 0;
        }

        private void UnregisterEventWait(string waitKey, string prefix)
        {
            _pendingWaits.TryRemove(waitKey, out _);
            if (_waitPrefixIndex.TryGetValue(prefix, out var bucket))
            {
                bucket.TryRemove(waitKey, out _);
                // Don't bother evicting empty buckets — prefixes ("{type}:*:" / "{type}:{payload}:")
                // are bounded by message-type cardinality and reused across calls. Evicting
                // would race with concurrent inserts under the same prefix and gain nothing.
            }
        }

        private void DrainPrefixBucket(string prefix, BusMessage msg)
        {
            if (!_waitPrefixIndex.TryGetValue(prefix, out var bucket) || bucket.IsEmpty) return;

            // The previous `bucket.Keys.ToArray()`
            // allocated a fresh array per inbound message even when no
            // waiters were present in the bucket. Buckets are typically tiny
            // (often empty after the IsEmpty fast-path above misses a race);
            // iterate directly over the bucket and TryRemove inside the loop.
            // ConcurrentDictionary's enumerator is safe under concurrent
            // modification — the doc explicitly permits Remove during foreach.
            foreach (var entry in bucket)
            {
                string key = entry.Key;
                if (_pendingWaits.TryRemove(key, out var tcs))
                {
                    bucket.TryRemove(key, out _);
                    tcs.TrySetResult(msg);
                }
                else
                {
                    // Already drained by another caller (e.g. WaitForEventAsync's own
                    // post-wait cleanup); just evict the stale bucket entry.
                    bucket.TryRemove(key, out _);
                }
            }
        }

        // internal so the macro-audit test suite can verify the path resolution rules
        // without instantiating a Bus.
        internal static string GetGlobalMacroPath()
        {
            string logicDir = Phoenix.Controls.Shared.Services.ConfigManager.Current.LogicDirectory;
            string dataDir  = Path.GetDirectoryName(Path.GetFullPath(logicDir)) ?? "data";
            return Path.Combine(dataDir, "macros", "global_macros.json");
        }

        // Process-wide gate around MergeMacroLibrary's read+merge+write block.
        // Two MACRO_SYNC messages used to race the file: each read the same
        // library snapshot and the second writer's incoming addition overwrote
        // the first's, silently dropping merges. The test driving 50 parallel
        // merges saw only 1 of 50 macros land on disk before this gate was added.
        private static readonly object _macroLibraryGate = new object();

        /// <summary>
        /// Merges <paramref name="incomingJson"/> (array of Macro) into the on-disk library.
        /// Optionally removes the macro whose MacroId == <paramref name="removedId"/>.
        /// Returns the serialized merged library.
        /// </summary>
        // internal so the macro-audit test suite can drive concurrent calls and
        // verify the read–merge–write atomicity contract.
        // Shared, immutable-after-first-use options — hoisted off the per-message
        // VISUAL_TRIGGER deserialize path (and reused here) so STJ's metadata cache
        // isn't defeated by a fresh options instance each call.
        private static readonly JsonSerializerOptions s_caseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

        internal static string MergeMacroLibrary(string incomingJson, string? removedId)
        {
            var opts = s_caseInsensitiveJson;
            string macroPath = GetGlobalMacroPath();

            lock (_macroLibraryGate)
            {
                var library = new List<Phoenix.Controls.Shared.Models.Macro>();
                bool loadedSuccessfully = false;
                bool fileExists = File.Exists(macroPath);
                if (fileExists)
                {
                    try
                    {
                        library = JsonSerializer.Deserialize<List<Phoenix.Controls.Shared.Models.Macro>>(File.ReadAllText(macroPath), opts) ?? library;
                        loadedSuccessfully = true;
                    }
                    catch (Exception ex)
                    {
                        // The empty catch was swallowing JSON deserialization
                        // errors silently, leaving callers to debug "why is the macro library
                        // empty?" with zero log entries. Record the failure.
                        GlobalLogger.Error("Bus",
                            $"macro library at '{macroPath}' is corrupt — refusing to overwrite it", ex);
                    }
                }
                else
                {
                    // No file on disk yet — a clean first-write is safe.
                    loadedSuccessfully = true;
                }

                // Merge incoming (add or update by MacroId)
                var incoming = JsonSerializer.Deserialize<List<Phoenix.Controls.Shared.Models.Macro>>(incomingJson, opts) ?? new();
                foreach (var m in incoming)
                {
                    int i = library.FindIndex(e => e.MacroId == m.MacroId);
                    if (i >= 0) library[i] = m;
                    else library.Add(m);
                }

                // Remove if requested
                if (!string.IsNullOrEmpty(removedId))
                    library.RemoveAll(m => m.MacroId == removedId);

                string merged = JsonSerializer.Serialize(library);

                // The on-disk file exists but failed to
                // deserialize (corrupt). Writing here would overwrite it with
                // (incoming merged into empty), permanently dropping every macro
                // the corrupt file still holds. Skip the write and return the
                // best-effort merge to callers/rebroadcast without clobbering disk.
                if (!loadedSuccessfully && fileExists)
                {
                    GlobalLogger.Error("Bus",
                        $"skipping macro library write — '{macroPath}' is corrupt and has no recoverable prior state to merge from");
                    return merged;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(macroPath)!);
                    File.WriteAllText(macroPath, merged);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Bus", "macro library save failed", ex);
                }
                return merged;
            }
        }

        // Bounded wait for a Bus peer's send slot. A paused-debugger or
        // a peer whose dispatcher is wedged can hold the per-client semaphore
        // indefinitely, which would otherwise stall BroadcastAsync (and every
        // queue/script awaiting the broadcast). Five seconds is well past any
        // healthy send latency on loopback but short enough that a hang doesn't
        // freeze the suite. Drops on timeout are logged at Communication tier
        // so the operator sees the stalled peer without flooding the syslog.
        private static readonly TimeSpan DefaultClientSendLockTimeout = TimeSpan.FromSeconds(5);

        // Test seam. The 5s default above is correct for production, but a
        // unit test that deliberately drives concurrent Stop() + Broadcast() must
        // wait out that full bound × every in-flight broadcast, forcing an oversized
        // WaitAsync budget that flakes under suite contention. This volatile override
        // lets a test pin a short timeout so the budget can shrink; left at zero it
        // is ignored and production keeps the 5s default. Stored as ticks for a
        // lock-free volatile read on the hot broadcast path.
        private static long _clientSendLockTimeoutOverrideTicks; // 0 = no override

        /// <summary>
        /// The effective per-client send-lock acquisition timeout used by
        /// <see cref="BroadcastToRemoteAsync"/>. Returns the production default
        /// (5s) unless a test has pinned a shorter value via
        /// <see cref="SetClientSendLockTimeoutForTesting"/>.
        /// </summary>
        private static TimeSpan ClientSendLockTimeout
        {
            get
            {
                long overrideTicks = Volatile.Read(ref _clientSendLockTimeoutOverrideTicks);
                return overrideTicks > 0
                    ? TimeSpan.FromTicks(overrideTicks)
                    : DefaultClientSendLockTimeout;
            }
        }

        /// <summary>
        /// TEST-ONLY seam. Pins the per-client send-lock acquisition timeout
        /// so a concurrency test can use a tight WaitAsync budget instead of waiting
        /// out the 5s production default for every in-flight broadcast. Pass
        /// <c>null</c> (or a non-positive span) to restore the production default.
        /// Has no effect on production callers, which never invoke this. The value is
        /// stored via <see cref="Volatile.Write(ref long, long)"/> so the override is
        /// visible to the broadcast loop running on other threads.
        /// </summary>
        internal static void SetClientSendLockTimeoutForTesting(TimeSpan? timeout)
        {
            long ticks = timeout is { } t && t > TimeSpan.Zero ? t.Ticks : 0L;
            Volatile.Write(ref _clientSendLockTimeoutOverrideTicks, ticks);
        }

        /// <summary>Broadcast a message to all connected clients (or a specific target).
        /// Fans to remote WebSocket peers and to in-process subscribers via the
        /// InProcBus bridge (so Architect-in-Hub-process sees broadcasts without
        /// paying the localhost-WebSocket hop) — and, for Target "*" / "Hub"
        /// frames, ALSO delivers to Hub's own in-process consumers
        /// (OnMessageReceived subscribers such as the Live Feed, pending
        /// wait_for_event waits, and on_bus script handlers). Pre-fix, a
        /// Hub-originated broadcast never reached any Hub-side consumer: the
        /// Bus.Send / Bus.Broadcast Architect nodes were inert, on_bus scripts
        /// never fired for Hub-origin events, and the Live Feed missed every
        /// Hub-side trigger.
        ///
        /// Target discipline: local Hub delivery happens ONLY for
        /// Target == "*" or "Hub". Targeted frames (DEBUG_NODE_EXEC → "Architect",
        /// DEBUG_WIDGET_NODE → "Visualist", MACRO_SYNC → requester) must never
        /// reach Hub-local consumers — see HUDServer.ForwardWidgetNodeTrace's
        /// targeting rationale, which depends on this gate.
        ///
        /// Accepts an optional CancellationToken so a caller that wants
        /// shutdown to interrupt a hung ws.SendAsync can thread one through; existing
        /// callers (most of Hub) continue to pass nothing and get pre-fix semantics
        /// (token defaults to none, ClientSendLockTimeout is still the upper bound).
        ///
        /// Guards against `_stopped`: in-flight broadcasts called after
        /// Bus.StopAsync used to silently no-op (clients/sendLocks already cleared).
        /// We now log+return at Communication tier so the drop is visible during
        /// shutdown debugging.</summary>
        public async Task BroadcastAsync(BusMessage msg, CancellationToken ct = default)
        {
            // Stop-guard. After StopAsync flips _stopped, _clients and
            // _sendLocks are cleared and the inner loop has nothing to fan to.
            // Pre-fix the broadcast appeared to succeed (no exception, no log) which
            // masks send paths that fire during teardown. Surface the drop at
            // Communication tier so the log feed shows the broadcast was suppressed.
            // The guard sits ABOVE local delivery on purpose: after Stop, Hub-local
            // consumers are torn down too, so the whole broadcast drops as one unit.
            if (Volatile.Read(ref _stopped) != 0)
            {
                GlobalLogger.Log(
                    $"Bus.BroadcastAsync: bus is stopped — dropping {msg.Type} → {msg.Target}.",
                    "Bus", LogLevel.Communication);
                return;
            }

            // Local Hub delivery — mirror an ARRIVAL's Hub-side legs (fan
            // OnMessageReceived first, then the shared consumer tail), exactly the
            // order the in-proc bridge and the WS receive loop use. Deliberately
            // NOT RouteIncomingMessage: its switch arms are peer-frame
            // infrastructure and would double-dispatch / recurse for Hub-origin
            // frames — see DeliverToHubConsumers' doc.
            string localTarget = msg.Target ?? "*";
            if (localTarget == "*" || localTarget == "Hub")
            {
                FanOnMessageReceived(msg, "Hub-local ");
                DeliverToHubConsumers(msg);
            }

            await BroadcastToPeersAsync(msg, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Transport-only fan: remote WebSocket peers + in-proc bridge
        /// subscribers, WITHOUT the Hub-local consumer legs (OnMessageReceived /
        /// pending-wait resolution / on_bus dispatch) that
        /// <see cref="BroadcastAsync"/> adds for Target "*" / "Hub" frames.
        ///
        /// This is the send shape RouteIncomingMessage's infrastructure arms
        /// (MACRO_SYNC / MACRO_REMOVE / MACRO_REQUEST / LOGIC_RELOAD_ACK) use for
        /// their re-broadcasts: routing those through the full BroadcastAsync
        /// would hand a Hub-origin MACRO_SYNC back to Hub's own consumers — and
        /// if local delivery ever grew a routing leg, it would re-enter the very
        /// arm that sent it (MACRO_SYNC → MergeMacroLibrary → re-broadcast → ∞,
        /// one file merge per iteration). Keeping infrastructure echoes
        /// transport-only kills that cycle structurally, independent of what the
        /// local-delivery legs contain.
        ///
        /// Carries the same _stopped guard + per-client send-lock semantics those
        /// call sites previously got from BroadcastAsync. (When invoked through
        /// BroadcastAsync the guard re-checks — a benign double-read that only
        /// logs twice if Stop lands between the two reads.)
        /// </summary>
        private async Task BroadcastToPeersAsync(BusMessage msg, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                GlobalLogger.Log(
                    $"Bus.BroadcastToPeersAsync: bus is stopped — dropping {msg.Type} → {msg.Target}.",
                    "Bus", LogLevel.Communication);
                return;
            }

            await BroadcastToRemoteAsync(msg, ct).ConfigureAwait(false);

            // In-proc subscriber fan. Architect (and any future in-proc
            // peer) sees the same envelope a remote WebSocket peer would, without
            // serialisation + loopback overhead. Try/catch per subscriber so a
            // misbehaving in-proc consumer can't poison the broadcast for everyone.
            var inProc = Volatile.Read(ref _inProcSubscribersCache);
            for (int i = 0; i < inProc.Length; i++)
            {
                try { inProc[i](msg); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Bus",
                        $"InProc subscriber threw on broadcast {msg.Type} → {msg.Target}", ex);
                }
            }
        }

        /// <summary>
        /// Remote-only fan: serialises and writes the envelope to each
        /// matching connected WebSocket peer. Used internally by
        /// <see cref="BroadcastToPeersAsync"/> (the transport tail of every
        /// broadcast) and by the in-proc bridge's PublishAsync
        /// (which fires its own subscriber fanout via Hub-side OnMessageReceived
        /// before invoking this, so the in-proc subscriber list is intentionally
        /// skipped here to avoid echoing the publisher's own message back to itself).
        ///
        /// <paramref name="ct"/> is threaded down to ws.SendAsync; a hung
        /// peer can be interrupted by signalling the token rather than burning
        /// the full ClientSendLockTimeout. Defaulting to none is safe because the
        /// send is ALSO bounded independently: each write runs under a linked CTS
        /// armed with ClientSendLockTimeout, so a peer that has stopped draining
        /// its socket can no longer stall the fan-out for callers that supply no
        /// token (which is every production caller).
        /// </summary>
        private async Task BroadcastToRemoteAsync(BusMessage msg, CancellationToken ct = default)
        {
            // With zero remote peers the fan-out loop below never runs — skip
            // the JSON + UTF-8 serialization of the envelope entirely. In-proc
            // subscribers are fanned separately by BroadcastAsync / the bridge's
            // PublishAsync, so this remote-only early-out never affects them.
            if (_clients.IsEmpty) return;

            string json = JsonSerializer.Serialize(msg);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            // Record the (id, ws, sendLock) triplet at decision time so the
            // post-loop cleanup can use ICollection<KVP>.Remove (which only deletes
            // when the slot still holds the same value). The previous bare TryRemove(id)
            // would happily evict a fresh peer's send-lock and ws if a reconnect race
            // re-installed the same clientId between detection and cleanup.
            // Lazy — the happy path (no peer evicted) never allocates the list.
            List<(string Id, WebSocket Ws, SemaphoreSlim SendLock)>? toRemove = null;
            var target = msg.Target ?? "*";   // loop-invariant
            foreach (var (id, ws) in _clients)
            {
                if (target != "*" && target != id) continue;
                // TryGetValue (not GetOrAdd). HandleClientAsync owns
                // the SemaphoreSlim lifecycle (installs on connect, disposes on
                // disconnect under _reconnectGate); the previous GetOrAdd here
                // re-introduced a fresh semaphore for a clientId whose entry
                // had already been evicted, racing the handler's dispose path
                // and orphaning the original lock under reconnect churn.
                //
                // If the lookup misses, the client has detached between the
                // _clients enumeration and now — skip the send and let the
                // standard reconnect path re-install both _clients and
                // _sendLocks together.
                if (!_sendLocks.TryGetValue(id, out var sendLock))
                {
                    // No log line here — the miss is the routine "client torn
                    // down while broadcast loop was iterating" race, fires
                    // routinely under healthy churn, and the dictionary entry
                    // will be Removed below via the standard toRemove path.
                    continue;
                }
                bool acquired = false;
                try
                {
                    try
                    {
                        // Bounded WaitAsync. A peer whose previous send is
                        // stuck (paused debugger, ws.SendAsync wedged) used to
                        // block this loop forever, stranding every other peer
                        // for the same broadcast. With a timeout we record the
                        // stall, evict the stuck peer, and move on. Healthy
                        // send latency on loopback is sub-millisecond, so the
                        // 5-second bound never trips in normal operation.
                        acquired = await sendLock.WaitAsync(ClientSendLockTimeout).ConfigureAwait(false);
                        if (!acquired)
                        {
                            GlobalLogger.Log(
                                $"Bus.BroadcastAsync: '{id}' send slot did not acquire within {ClientSendLockTimeout.TotalSeconds:0}s — dropping frame for that peer (type={msg.Type}).",
                                "Bus", LogLevel.Communication);
                            (toRemove ??= new()).Add((id, ws, sendLock));
                            continue;
                        }
                    }
                    catch (ObjectDisposedException ode)
                    {
                        // The per-client semaphore was disposed by the reconnect path
                        // (or by Stop()) between GetOrAdd and WaitAsync. The OLD ws is no
                        // longer the canonical one for this clientId; skip the send rather
                        // than crash the broadcast loop. Logged at Communication-tier
                        // because under healthy reconnect this is expected, not erroneous.
                        GlobalLogger.Log(
                            $"Bus.BroadcastAsync: send lock for '{id}' was disposed — skipping ({ode.GetType().Name}).",
                            "Bus", LogLevel.Communication);
                        (toRemove ??= new()).Add((id, ws, sendLock));
                        continue;
                    }

                    if (ws.State == WebSocketState.Open)
                    {
                        // Bound the send ITSELF, not just the slot acquisition.
                        // The WaitAsync above only bounds waiting on a peer whose
                        // PREVIOUS send is stuck; the FIRST stuck send acquires a free
                        // slot and then blocks here. Every production caller passes no
                        // token, so pre-fix this write had no bound at all: a peer that
                        // stopped draining its socket (suspended process, paused
                        // debugger, wedged dispatcher) filled the loopback send buffer
                        // and pinned the whole fan-out — later peers never got the
                        // frame, the broadcast Task never completed, and any script
                        // awaiting it (wait_for_visual) was stranded mid-graph.
                        //
                        // The linked CTS keeps the caller's cancellation (shutdown
                        // still interrupts immediately) and adds the same
                        // ClientSendLockTimeout budget the slot wait uses. Cost is one
                        // CTS + timer per peer per broadcast — acceptable next to the
                        // JSON + UTF-8 encode already done above.
                        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        sendCts.CancelAfter(ClientSendLockTimeout);
                        try
                        {
                            await ws.SendAsync(segment, WebSocketMessageType.Text, true, sendCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // ★ ONLY the per-send TIMEOUT evicts. A caller-cancelled
                            // broadcast says nothing about the peer's health — during
                            // shutdown EVERY peer's send cancels at once, and evicting
                            // there would run the cleanup loop's ws.Abort() across every
                            // healthy socket and fire a spurious OnClientConnectionChanged
                            // (false) for each. That is strictly worse than the dropped
                            // frame this arm exists to handle, so the two causes are
                            // dispositioned differently, not just logged differently.
                            if (ct.IsCancellationRequested)
                            {
                                GlobalLogger.Log(
                                    $"Bus.BroadcastAsync: send to '{id}' cancelled by caller — dropping frame for that peer, peer left connected (type={msg.Type}).",
                                    "Bus", LogLevel.Communication);
                            }
                            else
                            {
                                GlobalLogger.Log(
                                    $"Bus.BroadcastAsync: send to '{id}' did not complete within {ClientSendLockTimeout.TotalSeconds:0}s — peer is not draining its socket; dropping frame and evicting (type={msg.Type}).",
                                    "Bus", LogLevel.Communication);
                                (toRemove ??= new()).Add((id, ws, sendLock));
                            }
                        }
                    }
                    else
                        // Socket is no longer Open (Aborted/Closed/CloseSent/None).
                        // Either the reconnect path swapped a placeholder in, or the
                        // remote tore down. Evict here so the dead clientId doesn't
                        // accumulate forever; the next reconnect will re-register.
                        (toRemove ??= new()).Add((id, ws, sendLock));
                }
                catch { (toRemove ??= new()).Add((id, ws, sendLock)); }
                finally
                {
                    if (acquired)
                    {
                        try { sendLock.Release(); }
                        catch (ObjectDisposedException) { /* lock raced with reconnect dispose; ignore */ }
                    }
                }
            }
            // KVP-scoped removal. ConcurrentDictionary's IDictionary view's
            // Remove(KeyValuePair) only succeeds when the slot still holds the same
            // value, so a reconnect that re-installed (id → freshWs/freshLock) is left
            // alone and the dead pair is the only thing evicted.
            if (toRemove != null)
            {
                var clientsCol = (ICollection<KeyValuePair<string, WebSocket>>)_clients;
                var locksCol   = (ICollection<KeyValuePair<string, SemaphoreSlim>>)_sendLocks;
                foreach (var entry in toRemove)
                {
                    bool evicted = clientsCol.Remove(new KeyValuePair<string, WebSocket>(entry.Id, entry.Ws));
                    if (evicted)
                    {
                        // Tear the socket down as well as the dictionary entry.
                        // A timeout-arm eviction can hit a peer whose socket is still
                        // Open and whose receive loop is still running: dropping only
                        // the _clients entry left that peer permanently invisible to
                        // every later broadcast (no LOGIC_RELOAD_ACK / MACRO_SYNC /
                        // LAYER_RELOADED / DEBUG_NODE_EXEC for the rest of the session)
                        // with no signal to trigger its reconnect backoff, because
                        // _clients is only ever written at connect time in
                        // HandleClientAsync. Abort unblocks its ReceiveAsync so the
                        // handler runs its normal finally and the peer reconnects.
                        // Mirrors the reconnect path's oldSocket.Abort().
                        //
                        // Only ever aborts a socket we actually evicted: the
                        // KVP-scoped Remove above fails if a reconnect already
                        // installed a fresh ws for this id, so a live replacement is
                        // never touched.
                        try { entry.Ws.Abort(); } catch { }

                        // Announce the disconnect from here. The handler's finally
                        // gates InvokeClientConnectionChanged on its own successful
                        // Remove, which is now false (we removed the pair first), so
                        // without this the peer's departure was completely silent —
                        // no subscriber notification, no "Bus client disconnected"
                        // line. Exactly-once still holds: ICollection.Remove(KVP) on a
                        // ConcurrentDictionary is an atomic compare-and-remove, so only
                        // one of the two sites can win for a given (id, ws) pair.
                        InvokeClientConnectionChanged(entry.Id, false);
                        GlobalLogger.Log($"Bus client disconnected: {entry.Id} (evicted by broadcast fan-out)",
                            "Bus", LogLevel.Communication);
                    }
                    if (locksCol.Remove(new KeyValuePair<string, SemaphoreSlim>(entry.Id, entry.SendLock)))
                        try { entry.SendLock.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Well-known / reserved client ids.
        ///
        /// Architect and Visualist are the canonical Phoenix Controls peers.
        /// Any local process that knows the name can otherwise displace the
        /// incumbent via the reconnect snapshot+swap path; privileged ids are
        /// non-displaceable, so subsequent connections with the same id are
        /// refused while the incumbent is alive.
        ///
        /// "Hub" and "Browser" are additionally reserved (never
        /// accepted as a client id). DispatchVisualTrigger and
        /// DispatchWidgetLiveUpdate both use <c>msg.Source == "Hub"</c> as the
        /// echo-skip guard, and ResolveVisualWait stamps <c>Source = "Browser"</c>
        /// on synthetic completion envelopes. A peer that claims either name
        /// would either permanently mute legitimate triggers (Hub) or be able
        /// to forge VISUAL_COMPLETE provenance (Browser). The check is split
        /// from IsPrivilegedClientId so the refusal log line can distinguish
        /// "your name collides with the bus itself" from "another peer already
        /// owns this name."
        /// </summary>
        private static bool IsPrivilegedClientId(string clientId) =>
            string.Equals(clientId, "Architect", StringComparison.Ordinal) ||
            string.Equals(clientId, "Visualist", StringComparison.Ordinal);

        private static bool IsReservedClientId(string clientId) =>
            string.Equals(clientId, "Hub",     StringComparison.Ordinal) ||
            string.Equals(clientId, "Browser", StringComparison.Ordinal);

        /// <summary>
        /// Routes a VISUAL_TRIGGER bus message to LayerRuntime. Called from RouteIncomingMessage
        /// when an external client (Architect, Hub-internal script) emits a VISUAL_TRIGGER.
        /// Fire-and-forget at the bus layer — the originating script either awaited via
        /// TriggerVisualAndWaitAsync (which already enqueued locally before broadcasting) or
        /// chose visual.trigger_queued (no awaiter expected).
        /// </summary>
        private static void DispatchVisualTrigger(BusMessage msg)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<VisualTriggerPayload>(
                    msg.Payload ?? "{}",
                    s_caseInsensitiveJson);
                if (payload is null || string.IsNullOrEmpty(payload.LayerId)) return;

                // Skip if this is the broadcast echo of a Hub-originated trigger — the Hub
                // already enqueued locally before broadcasting and we don't want to double-fire.
                if (string.Equals(msg.Source, "Hub", StringComparison.Ordinal)) return;

                var eventData = JsonSerializer.SerializeToElement(payload.EventData ?? new Dictionary<string, string>());
                // Route the LayerRuntime enqueue through AsyncErrorBoundary.
                // Pre-fix this bare _ = ...EnqueueTriggerAsync(...) was the only
                // fire-and-forget in DispatchVisualTrigger that bypassed the standard
                // boundary; a fault inside EnqueueTriggerAsync (e.g. a dispatcher-throw
                // during reconnect) would land on UnobservedTaskException with no
                // breadcrumb to the originating bus message.
                _ = AsyncErrorBoundary.SafeRunAsync(
                    async () =>
                    {
                        await LayerRuntime.Instance.EnqueueTriggerAsync(
                            payload.LayerId, payload.WidgetId, payload.TriggerName,
                            eventData, payload.WaitId).ConfigureAwait(false);
                    },
                    "Bus",
                    $"VISUAL_TRIGGER enqueue for {payload.LayerId}/{payload.WidgetId}/{payload.TriggerName}");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Bus", "VISUAL_TRIGGER dispatch failed", ex);
            }
        }

        /// <summary>
        /// Forwards a Visualist-originated WIDGET_UPDATE bus message onto the
        /// matching layer's OBS browser sources. The payload is the same shape
        /// compositor.js expects for the in-process design-time path
        /// (<c>{ type, widgetId, name, zIndex, rect }</c>) — extracted from the
        /// bus envelope, the layerId routes the message via
        /// <c>LayerRuntime.Dispatcher</c> (<c>HUDServer.SendToLayerAsync</c>),
        /// and the browser applies the rect change without a save+reload.
        /// </summary>
        private static void DispatchWidgetLiveUpdate(BusMessage msg)
        {
            // Skip echoed Hub-originated messages so we don't fan out twice.
            if (string.Equals(msg.Source, "Hub", StringComparison.Ordinal)) return;

            try
            {
                using var doc = JsonDocument.Parse(msg.Payload ?? "{}");
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                if (!root.TryGetProperty("layerId", out var layerEl)) return;
                string layerId = layerEl.GetString() ?? "";
                if (string.IsNullOrEmpty(layerId)) return;

                var dispatcher = LayerRuntime.Instance.Dispatcher;
                if (dispatcher is null)
                {
                    GlobalLogger.Log(
                        $"Bus: WIDGET_UPDATE dropped — LayerRuntime.Dispatcher not yet assigned (layer={layerId}).",
                        "Bus", LogLevel.Communication);
                    return;
                }

                // Hand compositor.js exactly what its WIDGET_UPDATE handler reads.
                // root.Clone() is required because the JsonDocument is disposed
                // before the async dispatcher runs.
                var cloned = root.Clone();
                // Route the dispatcher call through AsyncErrorBoundary.
                // The dispatcher target is HUDServer.SendToLayerAsync which can throw
                // on a torn-down socket; pre-fix the fault would land on the
                // UnobservedTaskException pump with no Bus-side breadcrumb.
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => dispatcher.Invoke(layerId, cloned, default),
                    "Bus",
                    $"WIDGET_UPDATE dispatch to layer '{layerId}'");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Bus", "WIDGET_UPDATE dispatch failed", ex);
            }
        }

        /// <summary>
        /// Hub-internal entry point for visual triggers — registers a pending wait keyed by waitId,
        /// enqueues into LayerRuntime, and broadcasts VISUAL_TRIGGER on the bus for external observers.
        /// The browser sends VISUAL_COMPLETE back through HUDServer; LayerRuntime forwards via
        /// <see cref="ResolveVisualWait"/>, which sets the TCS here. Returns true on completion,
        /// false on timeout. Inactive layers fast-succeed via LayerRuntime's short-circuit.
        ///
        /// <para>★ V13 — this is the PAYLOAD-DISCARDING form and it has NO caller at all any
        /// more: <c>wait_for_visual</c> calls <see cref="TriggerVisualAndWaitWithPayloadAsync"/>
        /// directly, because dropping <c>result.Payload</c> here is exactly what left §8.1's
        /// completion payload with no reader in the first round. It is retained deliberately as
        /// the documented payload-agnostic entry point for a future fire-and-forget waiter, but
        /// nothing exercises it, so treat its body and its <c>timeoutMs</c> default as
        /// unverified: the third of the three independent 10 s constants B3 keeps aligned is
        /// pinned on the payload-bearing overload below
        /// (<c>LayerRuntimeCompletionTimeoutTests.Script_Wait_Budget_Is_Forwarded_By_The_Waiting_Bus_Path</c>),
        /// which is the method the live path uses. An earlier revision of this comment claimed
        /// the pin was on THIS method; it was, and that was the defect — it guarded a method no
        /// stream can reach. If you are adding a new waiting call site, call the payload-bearing
        /// overload.</para>
        /// </summary>
        public async Task<bool> TriggerVisualAndWaitAsync(
            string layerId,
            string widgetId,
            string triggerName,
            Dictionary<string, string>? eventData = null,
            int timeoutMs = 10000,
            CancellationToken ct = default)
        {
            var result = await TriggerVisualAndWaitWithPayloadAsync(
                layerId, widgetId, triggerName, eventData, timeoutMs, ct).ConfigureAwait(false);
            return result.Completed;
        }

        /// <summary>
        /// V13 §8.1 — the payload-bearing form of <see cref="TriggerVisualAndWaitAsync"/>.
        /// Identical dispatch and identical completion semantics; it additionally hands back
        /// the browser's <c>Visual.Complete → Payload</c> string.
        ///
        /// <para><c>Payload</c> is <c>""</c> whenever the pin is unwired, the wait resolved by
        /// any path other than a real VISUAL_COMPLETE (queue hard-timeout, inactive-layer
        /// fast-succeed, outer timeout), or the ack was declined its payload for arriving on an
        /// Untrusted socket (§8.3). It is never null, so a caller can assign it straight into a
        /// script var without a coalesce.</para>
        ///
        /// <para>The intended consumer is <c>wait_for_visual</c>, which must write it to
        /// <c>global._wait_payload</c> — the SAME var <c>wait_for_event</c> already writes, via
        /// the same <c>SetScriptVarAsync</c> call, because the exporter's
        /// <c>Async.WaitForVisual.Payload</c> data-out resolves to <c>{global._wait_payload}</c>
        /// (ScriptExporter.cs, alongside the WaitForEvent mapping). Do not invent a second var
        /// name.</para>
        /// </summary>
        public async Task<(bool Completed, string Payload)> TriggerVisualAndWaitWithPayloadAsync(
            string layerId,
            string widgetId,
            string triggerName,
            Dictionary<string, string>? eventData = null,
            int timeoutMs = 10000,
            CancellationToken ct = default)
        {
            string waitId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<BusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingWaits[waitId] = tcs;

            // Reap _pendingWaits via try/finally instead of the inline
            // TryRemove below. Pre-fix a throw from BroadcastAsync — e.g.
            // ObjectDisposedException during Bus.StopAsync, or a reconnect-storm fault
            // — would propagate out of this method while leaving the (waitId → tcs)
            // entry pinned in _pendingWaits forever (or until Bus.StopAsync's
            // `_pendingWaits.Clear()` ran). On a Hub that doesn't restart between
            // such faults the entry accumulates; under churn it leaks memory
            // proportional to fault count. try/finally guarantees reap regardless
            // of how this method exits.
            try
            {
                var triggerPayload = new VisualTriggerPayload
                {
                    LayerId     = layerId,
                    WidgetId    = widgetId,
                    TriggerName = triggerName,
                    EventData   = eventData ?? new Dictionary<string, string>(),
                    WaitId      = waitId,
                };

                // Hub-side dispatch — runtime owns the trigger queue and browser dispatch. The returned
                // Task<bool> resolves on VISUAL_COMPLETE OR the queue's hard-timeout OR inactive-layer
                // fast-succeed; either way we use it as a fallback signal so the script doesn't deadlock.
                var elem = JsonSerializer.SerializeToElement(triggerPayload.EventData);
                var queueTask = LayerRuntime.Instance.EnqueueTriggerAsync(layerId, widgetId, triggerName, elem, waitId,
                    scriptWaitTimeoutMs: timeoutMs);

                // Arm the wait budget BEFORE the broadcast. Pre-fix the
                // `await BroadcastAsync(...)` sat above this line, so the script's
                // wait_for_visual was completely unprotected across it: a peer that
                // stopped draining its socket stalled the broadcast and the wait took
                // neither the Done nor the Timeout branch — it never returned at all
                // and the script was stranded mid-graph. The timeout must cover the
                // broadcast, not start after it.
                //
                // Wait for: (a) VISUAL_COMPLETE arrives via ResolveVisualWait → tcs resolves; or
                //           (b) the queue's path completes (timeout / inactive-layer fast-succeed); or
                //           (c) the outer timeoutMs; or
                //           (d) caller cancels via the script CT.
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = Task.Delay(timeoutMs, delayCts.Token);

                // Also broadcast on the bus so external observers (Architect dashboards) see it.
                // Started, not awaited-to-completion unconditionally: this frame is pure
                // observability for peers, and no part of the wait below depends on its
                // transport leg finishing. BroadcastAsync's Hub-local delivery leg runs
                // synchronously before its first await, so Hub-side consumers still see
                // the frame in the same order as before.
                var broadcastTask = BroadcastAsync(new BusMessage
                {
                    Type    = "VISUAL_TRIGGER",
                    Source  = "Hub",
                    Target  = "*",
                    Payload = JsonSerializer.Serialize(triggerPayload)
                });

                if (broadcastTask.IsCompleted)
                {
                    // Normal case (no remote peers, or every peer's socket accepted the
                    // write synchronously): observe it inline so a broadcast fault still
                    // propagates to the caller exactly as it did pre-fix.
                    await broadcastTask.ConfigureAwait(false);
                }
                else
                {
                    // A peer is draining slowly. Hand the tail to the error boundary so a
                    // fault is logged rather than lost as an unobserved task exception,
                    // and let the wait run on its own budget.
                    _ = AsyncErrorBoundary.SafeRunAsync(() => broadcastTask, "Bus",
                        $"VISUAL_TRIGGER broadcast for {layerId}/{widgetId}");
                }
                var completed = await Task.WhenAny(tcs.Task, queueTask, delayTask).ConfigureAwait(false);
                // Cancel the delay if we resolved by another path so its timer is freed.
                try { delayCts.Cancel(); } catch { }
                // ★ V13 — the wait's own TCS is authoritative whenever it resolved, REGARDLESS
                // of which task WhenAny handed back. One browser ack completes both this TCS
                // and queueTask, and LayerRuntime.NotifyTriggerComplete resolves THIS ONE
                // FIRST (see the ordering comment there) — so if WhenAny returned because
                // queueTask completed, this TCS is already completed too, by program order on
                // the notifying thread. Checking it before the winner comparison is what turns
                // "the payload usually survives" into "the payload always survives": the
                // pre-V13 shape let the thread pool decide, biased toward the payload-less
                // queueTask because its continuation was queued first.
                //
                // Only a genuine VISUAL_COMPLETE completes this TCS successfully. A shutdown /
                // timeout cancel leaves it Canceled, which IsCompletedSuccessfully excludes,
                // so the fallback branches below keep their exact pre-V13 meaning.
                if (tcs.Task.IsCompletedSuccessfully)
                    return (true, tcs.Task.Result.Payload ?? "");

                if (completed == tcs.Task)
                {
                    // Reached only when the TCS completed UNSUCCESSFULLY (cancelled by
                    // CancelPendingVisualWait or StopAsync) — the successful case is handled
                    // by the authoritative check above.
                    return (false, "");
                }
                if (completed == queueTask)
                {
                    // Queue path: hard-timeout, inactive-layer fast-succeed, or an evicted
                    // enqueue. None of those is a browser ack, so there is no payload.
                    return (queueTask.Status == TaskStatus.RanToCompletion && queueTask.Result, "");
                }
                tcs.TrySetCanceled();
                // Distinguish caller-cancelled (script CT) from timeout: throw to let the
                // script engine's `_executionCt.ThrowIfCancellationRequested` chain unwind
                // cleanly.
                ct.ThrowIfCancellationRequested();
                return (false, "");
            }
            finally
            {
                _pendingWaits.TryRemove(waitId, out _);
            }
        }

        /// <summary>
        /// Resolves a pending VISUAL_COMPLETE wait by waitId. Called by LayerRuntime when the
        /// browser echoes VISUAL_COMPLETE back through HUDServer. Idempotent — the underlying
        /// TCS uses TrySetResult so multi-client echo storms are absorbed.
        ///
        /// <para>V13 §8.1 — <paramref name="payload"/> is the browser's optional
        /// <c>Visual.Complete → Payload</c> string. It rides the resolved envelope's
        /// <c>Payload</c> field, which <see cref="TriggerVisualAndWaitWithPayloadAsync"/>
        /// hands back to the caller. That field previously echoed the waitId; nothing
        /// read it (the only consumer is the waiter, which minted the waitId itself and
        /// therefore already knows it), so carrying the completion payload there costs
        /// nothing and keeps the value on the SAME happens-before edge as the TCS
        /// completion — no side table, no eviction race, no read-after-free window.</para>
        ///
        /// <para><b>Unwired ⇒ unchanged.</b> A browser that omits <c>payload</c> reaches
        /// here with <c>null</c> and the envelope carries <c>""</c>, so a caller that
        /// ignores the payload (every pre-V13 call site) sees byte-identical behaviour.</para>
        ///
        /// <para><b>First ack wins.</b> The atomic <c>TryRemove</c> below IS that decision:
        /// exactly one caller can take the TCS, so exactly one payload is delivered and
        /// every later ack for the same waitId is dropped. Two OBS sources on one layer
        /// therefore make the payload nondeterministic; that is the accepted §8.1
        /// behaviour, reported once per waitId by <see cref="ReportLateVisualAckOnce"/>.</para>
        /// </summary>
        public void ResolveVisualWait(string waitId, string? payload = null)
        {
            if (string.IsNullOrEmpty(waitId)) return;
            if (_pendingWaits.TryRemove(waitId, out var tcs))
            {
                // Ledger FIRST, then complete the TCS. A racing second ack must be
                // able to find the record; if the order were reversed, the waiter's
                // continuation could run and the second ack could arrive in the window
                // before the record existed, and the collision would go unreported.
                NoteVisualWaitResolved(waitId);
                tcs.TrySetResult(new BusMessage
                {
                    Type    = "VISUAL_COMPLETE",
                    Source  = "Browser",
                    Target  = "Hub",
                    Payload = payload ?? "",
                });
                return;
            }

            // No pending wait for this id. Either a LATE ack for a waitId this Hub
            // already resolved (the §8.1 fan-out case — report once), or an id this
            // Hub has no record of at all (an expired/timed-out wait, or a spoofed
            // frame). The latter stays a silent no-op, exactly as before V13: logging
            // it would let anything that can reach the bus or /hud write log lines.
            ReportLateVisualAckOnce(waitId);
        }

        /// <summary>
        /// V13 §8.1 — records that <paramref name="waitId"/> has been resolved, so the
        /// first LATE ack for it can be reported and the rest suppressed. Capacity-capped
        /// FIFO; see the <c>_resolvedVisualWaits</c> field comment for why the record has
        /// to outlive the invocation.
        /// </summary>
        private void NoteVisualWaitResolved(string waitId)
        {
            // TryAdd, not indexer-assign: a waitId is a fresh GUID per wait, so a
            // collision is impossible in practice — but if one ever happened, keeping
            // the EXISTING entry preserves its already-logged latch rather than
            // re-arming it and allowing a second line for the same id.
            if (!_resolvedVisualWaits.TryAdd(waitId, 0)) return;
            _resolvedVisualWaitOrder.Enqueue(waitId);
            while (_resolvedVisualWaitOrder.Count > MaxResolvedVisualWaitLedger
                   && _resolvedVisualWaitOrder.TryDequeue(out var oldest))
            {
                _resolvedVisualWaits.TryRemove(oldest, out _);
            }
        }

        /// <summary>
        /// V13 §8.1 — reports a dropped VISUAL_COMPLETE for an already-resolved waitId,
        /// at most ONCE per waitId. A fan-out of eight sources produces one line, not seven.
        ///
        /// <para>Communication tier, not System: two browser sources on one layer is a
        /// legitimate setup and the drop is the DECIDED behaviour, so it must not light the
        /// operator's error surfaces. It is logged at all because "my Visual.Complete
        /// payload sometimes carries the other source's value" is otherwise undiagnosable.</para>
        /// </summary>
        private void ReportLateVisualAckOnce(string waitId)
        {
            // TryUpdate(key, 1, 0) is the whole latch: it succeeds for exactly one
            // caller per waitId, so N concurrent late acks produce one log line.
            if (!_resolvedVisualWaits.TryUpdate(waitId, 1, 0)) return;
            GlobalLogger.Log(
                $"Bus: VISUAL_COMPLETE for waitId '{waitId}' arrived after that wait was already " +
                $"resolved — first ack wins, so this ack (and any payload it carried) is dropped. " +
                $"Two browser sources rendering one layer make the completion payload " +
                $"nondeterministic by design. (Logged once per waitId.)",
                "Bus", LogLevel.Communication);
        }

        /// <summary>
        /// Reciprocal cleanup for the WidgetTriggerQueue's pump-side hard timeout. When the
        /// browser silently disconnects, VISUAL_COMPLETE never arrives and the queue resolves
        /// the wait via timeout instead. The corresponding _pendingWaits entry would otherwise
        /// only be cleaned by WaitForVisualAsync's WhenAny exit path — which doesn't run if
        /// the call site discarded the queueTask, or if the bus broadcast threw before the
        /// WhenAny could be set up. This forces the pending entry to be removed exactly when
        /// the pump declares timeout, regardless of how the originating script awaited it.
        /// </summary>
        public void CancelPendingVisualWait(string waitId)
        {
            if (string.IsNullOrEmpty(waitId)) return;
            if (_pendingWaits.TryRemove(waitId, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }

        /// <summary>Broadcasts an OBS scene change event to the bus.
        ///
        /// Historically this was FAN-OUT ONLY — nothing in Hub saw its own
        /// broadcasts. Since BroadcastAsync gained Hub-local delivery for
        /// Target "*" / "Hub" frames, that is no longer true: this SCENE_CHANGED
        /// broadcast now ALSO reaches Hub-side consumers — OnMessageReceived
        /// subscribers (Live Feed), pending wait_for_event("SCENE_CHANGED") waits,
        /// and on_bus("SCENE_CHANGED") script handlers — in addition to every
        /// connected external client (Architect dashboards, etc).
        ///
        /// For a strongly-typed Hub-internal hook, prefer the dedicated
        /// <see cref="OnSceneChanged"/> event below (raised synchronously before
        /// the broadcast) over pattern-matching raw envelopes out of
        /// <see cref="OnMessageReceived"/> — the typed event hands you the scene
        /// name without JSON plumbing and fires even if the broadcast faults.
        /// </summary>
        public Task BroadcastSceneChangedAsync(string sceneName)
        {
            // Raise the in-process event for any future Hub subscribers BEFORE the wire
            // broadcast so handlers run on the caller's thread and exceptions propagate
            // to the caller's logger context. Per-handler try/catch matches the pattern
            // used in HandleClientAsync's OnMessageReceived dispatch.
            //
            // Volatile.Read of the cached array; no per-call
            // GetInvocationList() allocation.
            var handlers = Volatile.Read(ref _onSceneChangedCache);
            for (int i = 0; i < handlers.Length; i++)
            {
                try { handlers[i](sceneName); }
                catch (Exception subEx)
                {
                    GlobalLogger.Error("Bus",
                        $"OnSceneChanged subscriber threw for scene '{sceneName}'", subEx);
                }
            }

            return BroadcastAsync(new BusMessage
            {
                Type    = "SCENE_CHANGED",
                Source  = "Hub",
                Target  = "*",
                Payload = sceneName
            });
        }

        /// <summary>
        /// Fire-and-forget visual trigger — enqueues into LayerRuntime and broadcasts
        /// the VISUAL_TRIGGER message on the bus. Does NOT wait for completion.
        /// </summary>
        public Task TriggerVisualQueuedAsync(
            string layerId,
            string widgetId,
            string triggerName,
            Dictionary<string, string>? eventData = null)
        {
            var triggerPayload = new VisualTriggerPayload
            {
                LayerId     = layerId,
                WidgetId    = widgetId,
                TriggerName = triggerName,
                EventData   = eventData ?? new Dictionary<string, string>()
            };

            var elem = JsonSerializer.SerializeToElement(triggerPayload.EventData);
            // Same wrap as DispatchVisualTrigger — the bare _ = ...EnqueueTriggerAsync
            // was the third fire-and-forget in Bus.cs that bypassed AsyncErrorBoundary
            // (peers at LOGIC_RELOAD_ACK / MACRO_SYNC / MACRO_REMOVE / MACRO_REQUEST
            // already wrap correctly).
            _ = AsyncErrorBoundary.SafeRunAsync(
                async () =>
                {
                    await LayerRuntime.Instance.EnqueueTriggerAsync(
                        layerId, widgetId, triggerName, elem).ConfigureAwait(false);
                },
                "Bus",
                $"TriggerVisualQueued enqueue for {layerId}/{widgetId}/{triggerName}");

            return BroadcastAsync(new BusMessage
            {
                Type    = "VISUAL_TRIGGER",
                Source  = "Hub",
                Target  = "*",
                Payload = JsonSerializer.Serialize(triggerPayload)
            });
        }

        /// <summary>
        /// Waits for a specific bus event type to arrive. Used by the
        /// <c>wait_for_event()</c> script command.
        ///
        /// <paramref name="payloadFilter"/> selects which messages of
        /// the given <paramref name="eventType"/> can resolve this wait:
        ///   <list type="bullet">
        ///   <item><description><c>"*"</c> or empty (default) — wildcard, accepts
        ///   any payload (preserves pre-fix behavior).</description></item>
        ///   <item><description>Anything else — exact payload match. The drain
        ///   path in <see cref="RouteIncomingMessage"/> already supports both
        ///   prefix forms via <c>DrainPrefixBucket</c>; this parameter is what
        ///   lets the caller choose which bucket they live in.</description></item>
        ///   </list>
        ///
        /// Pre-fix, the wait key hardcoded the wildcard prefix
        /// (<c>$"{type}:*:"</c>), which made the documented
        /// <c>$"{type}:{payloadFilter}:"</c> prefix-bucket unreachable —
        /// payload-filtered waits never resolved.
        /// </summary>
        public Task<BusMessage?> WaitForEventAsync(string eventType, int timeoutMs = 10000, CancellationToken ct = default)
            => WaitForEventAsync(eventType, payloadFilter: "*", timeoutMs, ct);

        /// <summary>
        /// Payload-aware overload — see the wildcard <see cref="WaitForEventAsync(string, int, CancellationToken)"/>
        /// for the parameter contract.
        /// </summary>
        public async Task<BusMessage?> WaitForEventAsync(string eventType, string payloadFilter, int timeoutMs = 10000, CancellationToken ct = default)
        {
            // Honour a pre-cancelled token before any allocation. Without
            // this, a cancelled ct paired with an immediate-resolve message would
            // return RanToCompletion and bypass the script engine's cancellation
            // contract (the chained _executionCt expects every long-running await
            // to throw on a pre-cancelled token).
            ct.ThrowIfCancellationRequested();

            string filter = string.IsNullOrEmpty(payloadFilter) ? "*" : payloadFilter;

            // Append a unique waitId so two callers awaiting the same eventType+filter
            // both resolve. Without the suffix the second registration silently
            // overwrote the first via the indexer-assignment.
            string prefix  = $"{eventType}:{filter}:";
            string waitKey = $"{prefix}{Guid.NewGuid():N}";
            // RunContinuationsAsynchronously so resolving the wait doesn't run the
            // script's continuation on the bus receive thread.
            var tcs = new TaskCompletionSource<BusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Register the wait in both the dictionary and the
            // prefix index so RouteIncomingMessage finds it via O(1) bucket lookup.
            RegisterEventWait(waitKey, prefix, tcs);

            // Link the script CT so timeout / cancel from the engine cuts the wait.
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(timeoutMs, delayCts.Token);
            var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
            try { delayCts.Cancel(); } catch { }
            UnregisterEventWait(waitKey, prefix);
            if (completed != tcs.Task)
            {
                tcs.TrySetCanceled(); // Release any awaiter still holding a reference
                ct.ThrowIfCancellationRequested();
                return null;
            }
            // Awaiting (rather than .Result) propagates cancellation/fault as a
            // bare exception instead of an AggregateException, and never blocks.
            return await tcs.Task.ConfigureAwait(false);
        }

        private int _stopped;

        /// <summary>
        /// Async teardown. The pre-fix Stop() blocked on
        /// <c>Task.WhenAny(...).GetAwaiter().GetResult()</c> while it drained
        /// in-flight HandleClientAsync tasks; on the UI thread that's a
        /// classic SyncContext-deadlock candidate, and even on a thread-pool
        /// caller it pinned the caller for up to 2 s per drain.
        ///
        /// StopAsync awaits the drain inline with ConfigureAwait(false) so the
        /// caller's continuation resumes on a worker thread, never on the
        /// SyncContext the call came from. Idempotent — the _stopped CAS
        /// guards against re-entry.
        /// </summary>
        // The shared body of the leading/trailing LOGIC_RELOAD refresh — always
        // on a background (thread-pool / timer) thread, never the bus caller's.
        private void RunLogicReloadRefresh(string source, string mode)
        {
            if (Volatile.Read(ref _stopped) != 0) return; // bus torn down mid-window
            try
            {
                ScriptRegistry.Instance.Refresh();
                GlobalLogger.Log(
                    $"LOGIC_RELOAD received from {source} — scripts refreshed ({mode}, off-thread).",
                    "Bus", LogLevel.System);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Bus", "LOGIC_RELOAD refresh failed", ex);
            }
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

            try { _httpListener?.Stop(); }  catch (ObjectDisposedException) { } catch (Exception ex) { GlobalLogger.Error("Bus", "stop error", ex); }
            try { _httpListener?.Close(); } catch { }
            // Stop()/Close() don't release every resource an
            // HttpListener holds; explicitly Dispose() (an explicit IDisposable impl) so
            // the listener is fully torn down before the next StartAsync recreates it.
            try { (_httpListener as IDisposable)?.Dispose(); } catch { }

            // Cancel any awaiters so scripts blocked on wait_for_visual / wait_for_event
            // unblock cleanly during shutdown.
            //
            // Snapshot before TrySetCanceled + Clear. The old enumerate-then-
            // Clear pattern raced concurrent TryAdds: a wait registered between the
            // foreach and the Clear was orphaned (cancellation skipped because the
            // foreach already finished, but the entry still got wiped). Snapshotting
            // gives us a stable view to iterate; any wait inserted afterwards is
            // also caught by the Clear() so callers don't deadlock.
            var pendingSnapshot = _pendingWaits.ToArray();
            foreach (var kvp in pendingSnapshot) kvp.Value.TrySetCanceled();
            _pendingWaits.Clear();
            // Drop prefix index buckets in lockstep with the
            // dictionary they shadow.
            _waitPrefixIndex.Clear();
            // V13 — the resolved-visual-wait ledger describes waits that no longer
            // exist, so it goes with them. Without this a Stop→StartAsync would carry
            // the previous session's already-logged latches into the new one.
            _resolvedVisualWaits.Clear();
            while (_resolvedVisualWaitOrder.TryDequeue(out _)) { }

            // Abort + drop all live websockets so background HandleClientAsync loops
            // exit and their finally blocks dispose sockets.
            foreach (var (id, ws) in _clients)
            {
                try { ws.Abort(); }   catch { }
                try { ws.Dispose(); } catch { }
            }
            _clients.Clear();

            // Wait for in-flight HandleClientAsync tasks to finish their
            // finally blocks before we tear down _sendLocks. The Abort() above
            // unblocks their ReceiveAsync, but the finally still needs to run
            // (it identity-evicts its own _sendLocks entry). Disposing the
            // dictionary out from under that finally is the original race.
            //
            // Snapshot the live tasks (the ContinueWith in StartAsync auto-prunes
            // the dictionary, so we tolerate a few entries already gone). Bound
            // the wait at 2 s — if a handler is genuinely hung (e.g. a blocked
            // CloseAsync) we still proceed to dispose so Stop() never deadlocks
            // shutdown.
            //
            // The drain is now awaited with ConfigureAwait(false)
            // instead of blocked through GetAwaiter().GetResult(). Behaviour
            // is identical (still bounded by 2 s, still proceeds with dispose
            // on timeout) — the change just stops pinning the caller's thread.
            var liveTasks = _handlerTasks.Keys.ToArray();
            if (liveTasks.Length > 0)
            {
                try
                {
                    var drainTimeout = Task.Delay(TimeSpan.FromSeconds(2));
                    var drainAll     = Task.WhenAll(liveTasks);
                    await Task.WhenAny(drainAll, drainTimeout).ConfigureAwait(false);
                    if (!drainAll.IsCompleted)
                    {
                        GlobalLogger.Log(
                            $"Bus.Stop: {liveTasks.Length} handler task(s) did not drain within 2s — disposing _sendLocks anyway.",
                            "Bus", LogLevel.Communication);
                    }
                }
                catch (Exception ex)
                {
                    // WhenAll surfaces handler exceptions as AggregateException;
                    // we already log faults via per-handler try/catch, so just
                    // record that the drain itself misbehaved.
                    GlobalLogger.Error("Bus", "handler-task drain in Stop() faulted", ex);
                }
            }
            _handlerTasks.Clear();

            // Per-client send semaphores are normally disposed in the
            // HandleClientAsync finally block, but Abort() above means those
            // loops may exit before they run. Drain whatever remains here so
            // we don't leak SemaphoreSlim handles across restarts.
            foreach (var (_, sem) in _sendLocks)
            {
                try { sem.Dispose(); } catch { }
            }
            _sendLocks.Clear();
        }

        /// <summary>
        /// Synchronous compatibility facade — see <see cref="StopAsync"/>.
        /// Off-thread .GetResult() avoids the UI-thread SyncContext deadlock
        /// of the previous in-process Stop(). No callers exist today; new
        /// shutdown wiring should await StopAsync directly.
        /// </summary>
        public void Stop() => Task.Run(StopAsync).GetAwaiter().GetResult();
    }
}
