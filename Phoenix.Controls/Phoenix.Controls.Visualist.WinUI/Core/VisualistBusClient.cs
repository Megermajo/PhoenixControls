using System;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

// NOTE: the pre-T15 baseline declared
//   [assembly: InternalsVisibleTo("Phoenix.Controls.Tests")]
// here. The WinUI assembly already declares that grant via an MSBuild
// <InternalsVisibleTo> item in Phoenix.Controls.Visualist.WinUI.csproj, so
// re-declaring it in source would be a duplicate-attribute compile error
// (CS0579). The internal test hooks at the bottom of this file are therefore
// already reachable from the test project — no source attribute needed.

namespace Phoenix.Controls.Visualist.WinUI.Core
{
    /// <summary>
    /// VisualistBusClient — connects Visualist to the Bus (port 18081) as a named client.
    ///
    /// Design-time-only bus link. The runtime lives in Hub (LayerRuntime + LayerWatcher),
    /// so Visualist does not route VISUAL_TRIGGER / COMPOSITION_CANCEL / SCENE_CHANGED /
    /// *_COMPLETE messages as a server — it only sends design-time pokes (Test Run,
    /// live widget rect updates) and listens for LAYER_RELOADED to refresh the preview.
    ///
    /// Responsibilities:
    ///   - Maintain the WebSocket connection to Hub.
    ///   - Send a VISUAL_READY ping on connect (so Hub status bar reflects Visualist presence).
    ///   - Expose OnConnectionStatusChanged so the editor chrome can update its bus indicator.
    ///   - Expose OnLayerReloaded so the previewer can refresh the WebView2 preview when Hub
    ///     broadcasts LAYER_RELOADED (raised inside the receive loop's switch).
    ///   - SendVisualTriggerAsync (Test Run) + SendWidgetLiveUpdateAsync (live rect publish).
    ///
    /// Hardening carried over from the baseline:
    ///   - bounded outbound queue (capacity 256, drop-oldest on overflow) drained in
    ///     order on (re)connect so messages aren't lost mid-disconnect.
    ///   - StopAsync uses WaitAsync(1000ms) rather than a blocking .Wait(1000); the
    ///     synchronous Stop() shim fires the close through Task.Run to avoid
    ///     sync-over-async deadlocks for UI callers.
    ///   - malformed inbound messages are logged via GlobalLogger.Error instead of
    ///     being silently swallowed.
    ///
    /// (Ported from the pre-T15 WinForms baseline
    /// <c>Phoenix.Controls.Visualist/Core/VisualistBusClient.cs</c> during the Visualist
    /// WinUI parity restoration. The class is
    /// platform-agnostic — ClientWebSocket + System.Threading.Channels, no UI dependency —
    /// so the port is byte-identical to the baseline apart from the namespace and the
    /// removed in-source InternalsVisibleTo noted above.)
    /// </summary>
    public class VisualistBusClient
    {
        // Fallback used when AppConfig has no BusUrl property (or the configured
        // value is empty). Identical to the historical hardcoded value so behavior is
        // unchanged for default installs.
        internal const string DefaultBusUrl = "ws://127.0.0.1:18081/";

        private static VisualistBusClient? _instance;
        public static VisualistBusClient Instance => _instance ??= new VisualistBusClient();

        // Guards Start() so the Visualist pillar (MainView owns the lifecycle) plus
        // any defensive caller can't spawn a second ConnectLoopAsync / a duplicate
        // "Visualist" socket. Reset by StopAsync so a pillar reload can restart cleanly.
        private bool _started;

        private ClientWebSocket? _ws;
        // Resolved from AppConfig.BusUrl (via reflection) at construction time;
        // falls back to DefaultBusUrl. Captured once so a runtime config edit doesn't
        // race the in-flight ConnectLoopAsync.
        private readonly string _busUrl;
        private CancellationTokenSource _cts = new();

        private VisualistBusClient()
        {
            _busUrl = ResolveBusUrl();
        }

        // Read AppConfig.BusUrl via reflection; fall back to DefaultBusUrl if
        // absent or empty. Mirrors the WS reflective config helper.
        internal static string ResolveBusUrl()
        {
            try
            {
                var cfg = ConfigManager.Current;
                if (cfg == null) return DefaultBusUrl;
                var prop = cfg.GetType().GetProperty(
                    "BusUrl",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || prop.PropertyType != typeof(string))
                    return DefaultBusUrl;
                var value = prop.GetValue(cfg) as string;
                return string.IsNullOrWhiteSpace(value) ? DefaultBusUrl : value;
            }
            catch
            {
                return DefaultBusUrl;
            }
        }

        // Bounded outbound queue. Channel<string> stores pre-serialized JSON so a
        // backlog of BusMessage instances doesn't pin live references during a long
        // disconnect. BoundedChannelFullMode.DropOldest gives the "drop oldest, accept
        // newest" semantic. Capacity 256 mirrors the spec.
        private const int OutboundQueueCapacity = 256;
        private const int DropLogEveryN = 50;
        private readonly Channel<string> _outbound = Channel.CreateBounded<string>(
            new BoundedChannelOptions(OutboundQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        // Count of messages displaced because the outbound queue was full. Logged at
        // Communication tier once per `DropLogEveryN` drops to avoid log-flood during
        // prolonged disconnects.
        private long _droppedSendCount;
        public long DroppedSendCount => Interlocked.Read(ref _droppedSendCount);

        // Pump that drains _outbound onto the live socket while connected. Started by
        // ConnectLoopAsync after a successful handshake; cancelled when the socket goes
        // down so the pump task exits and a fresh one starts on reconnect. The Channel
        // itself is NOT recreated — pending messages survive across the gap.
        private Task? _pumpTask;
        private CancellationTokenSource? _pumpCts;

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        public event Action<bool>? OnConnectionStatusChanged;

        /// <summary>Fired when Hub reports a layer file reloaded so the WebView2 preview can refresh.</summary>
        public event Action<string>? OnLayerReloaded;

        /// <summary>
        /// Starts the connect loop. Idempotent — a second Start() with no intervening
        /// Stop()/StopAsync() is a no-op (the <c>_started</c> guard returns before any
        /// CTS work). Start/Stop must be paired: every Start() is matched by a Stop()/
        /// StopAsync() that flips <c>_started</c> back to false, which is what permits a
        /// later restart (pillar reload).
        /// </summary>
        public void Start()
        {
            // Guard FIRST so a defensive double-Start
            // (no Stop between) early-returns before touching _cts — otherwise the
            // dispose/recreate below would run on the live, in-flight CTS and leak it.
            // Idempotent — never spawn a second connect loop on the already-started path.
            if (_started) return;
            _started = true;

            // Recreate the CTS for this run, disposing the prior
            // one — a Start/Stop/Start cycle (pillar reload) otherwise leaked one
            // CancellationTokenSource each time. Mirrors ArchitectBusClient's
            // swap-then-dispose idiom: assign the fresh CTS BEFORE disposing the old so
            // the _cts field is never momentarily pointing at a disposed instance (and so
            // an exception from Dispose can't strand the field on a dead CTS).
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            try { oldCts.Dispose(); } catch { /* best-effort */ }

            _ = ConnectLoopAsync(_cts.Token);
        }

        /// <summary>
        /// Synchronous shim. UI callers (window-close handlers) cannot easily
        /// transition to async, so we keep the sync entry point but route the actual
        /// close through Task.Run to dodge the sync-over-async deadlock that .Wait(1000)
        /// on a captured UI SynchronizationContext used to cause. The Task.Run starts the
        /// awaitable shutdown and returns immediately; new callers that need to await can
        /// call <see cref="StopAsync"/> directly.
        /// </summary>
        public void Stop()
        {
            _cts.Cancel();
            // Fire-and-forget: kick the close off the UI thread. We deliberately don't
            // block the caller — the bounded WaitAsync inside StopAsync caps the worst-
            // case shutdown to ~1s in the background instead of blocking UI close.
            _ = Task.Run(async () =>
            {
                try { await StopAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("VisualistBusClient", "Stop background close failed", ex);
                }
            });
        }

        /// <summary>
        /// Async shutdown. Cancels the connect loop and closes the live socket with
        /// a 1s upper bound enforced via <c>WaitAsync(TimeSpan)</c>. Safe to await from
        /// any context; will not deadlock on a captured UI SynchronizationContext.
        /// </summary>
        public async Task StopAsync()
        {
            _started = false;   // allow a later Start() to restart after a pillar reload
            try { _cts.Cancel(); } catch { /* CTS may race a concurrent Start swap */ }

            // Stop the pump first so we don't try to send onto a closing socket.
            // Cancel AND dispose the linked pump CTS and
            // null the fields, so a Stop that races/precedes the connect-loop teardown
            // doesn't leave a stale (cancelled) CTS hanging on the singleton. Snapshot
            // first to avoid disposing a CTS a concurrent reconnect just swapped in.
            var pumpCts = _pumpCts;
            _pumpTask = null;
            _pumpCts  = null;
            if (pumpCts != null)
            {
                try { pumpCts.Cancel(); } catch { }
                try { pumpCts.Dispose(); } catch { }
            }

            var ws = _ws;
            if (ws == null) return;
            try
            {
                await ws
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromMilliseconds(1000))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Server didn't ack the close in time — abort so we don't leak the socket.
                try { ws.Abort(); } catch { }
            }
            catch
            {
                // Socket may already be torn down; nothing useful to log here.
            }
        }

        private async Task ConnectLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Dispose the previous socket before overwriting the field. Without
                    // this every reconnect leaks the prior ClientWebSocket — its receive
                    // buffers + handshake state stay alive until the GC collects them.
                    try { _ws?.Dispose(); } catch { }
                    _ws = new ClientWebSocket();
                    _ws.Options.SetRequestHeader("X-Client-Id", "Visualist");
                    await _ws.ConnectAsync(new Uri(_busUrl + "?id=Visualist"), ct);

                    GlobalLogger.Log($"Connected to Bus at {_busUrl}", "BusClient", LogLevel.Communication);
                    SafeEvent.Raise(OnConnectionStatusChanged, true, "VisualistBusClient", "OnConnectionStatusChanged");

                    // Start the outbound pump for this connection. Linked to the
                    // outer ct so a Stop() call collapses both at once. The VISUAL_READY
                    // ping below goes through SendAsync, which enqueues; the pump drains
                    // it (along with anything queued during the prior disconnect window)
                    // in FIFO order onto the freshly-opened socket.
                    StartPump(ct);

                    await SendAsync(new BusMessage
                    {
                        Type    = "VISUAL_READY",
                        Source  = "Visualist",
                        Target  = "Hub",
                        Payload = "",
                    });

                    await ReceiveLoopAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"BusClient connect error: {ex.Message}", "BusClient", LogLevel.Communication);
                }

                // Tear down the pump for this connection so the next iteration starts a
                // fresh one against the new socket. The Channel survives — anything still
                // queued is delivered by the next pump.
                // Cancel AND dispose the per-connection
                // linked CTS — StartPump creates a fresh CreateLinkedTokenSource on every
                // (re)connect, so cancel-then-null without dispose leaked one CTS per
                // reconnect. Same leak class as the _cts headline finding.
                var pumpCts = _pumpCts;
                _pumpTask = null;
                _pumpCts  = null;
                if (pumpCts != null)
                {
                    try { pumpCts.Cancel(); } catch { }
                    try { pumpCts.Dispose(); } catch { }
                }

                SafeEvent.Raise(OnConnectionStatusChanged, false, "VisualistBusClient", "OnConnectionStatusChanged");
                if (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(5000, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[32768];
            var inbound = new StringBuilder();
            while (_ws!.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    inbound.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    string raw = inbound.ToString();
                    inbound.Clear();

                    BusMessage? msg;
                    try
                    {
                        msg = JsonSerializer.Deserialize<BusMessage>(raw);
                    }
                    catch (Exception jex)
                    {
                        // Previously a silent `catch { continue; }`. Surface the
                        // failure so authors notice malformed traffic instead of debugging
                        // "why is my Visualist preview not refreshing?" with zero logs.
                        // We deliberately log only at the deserialization layer — the
                        // outer receive-loop swallow path below still treats
                        // OperationCanceledException / socket-teardown as a normal exit.
                        GlobalLogger.Error("VisualistBusClient", "Malformed message", jex);
                        continue;
                    }
                    if (msg is null) continue;

                    switch (msg.Type)
                    {
                        case "LAYER_RELOADED":
                            try { OnLayerReloaded?.Invoke(msg.Payload ?? ""); }
                            catch (Exception ex)
                            {
                                GlobalLogger.Log($"OnLayerReloaded subscriber threw: {ex.Message}",
                                    "BusClient", LogLevel.CriticalError);
                            }
                            break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }

        /// <summary>
        /// Public send entry point. Always enqueues into the bounded outbound
        /// channel; the pump (started on each successful connect) drains it onto the
        /// live socket in FIFO order. Sends made while disconnected are buffered;
        /// queue overflow drops the OLDEST entry and bumps <see cref="DroppedSendCount"/>.
        /// </summary>
        public Task SendAsync(BusMessage message)
        {
            string json;
            try
            {
                json = JsonSerializer.Serialize(message);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("VisualistBusClient", "Send serialize failed", ex);
                return Task.CompletedTask;
            }

            // BoundedChannel with FullMode=DropOldest never blocks and never refuses —
            // TryWrite returns true and silently displaces the oldest entry. Detect the
            // displacement by sampling Count before/after so we can increment the
            // dropped counter for telemetry.
            int beforeCount = _outbound.Reader.Count;
            bool wrote = _outbound.Writer.TryWrite(json);
            if (!wrote)
            {
                // Should not happen for an unbounded-on-overflow drop-oldest channel,
                // but guard for the case where the writer is completed (post-Stop).
                return Task.CompletedTask;
            }

            // If the channel was already at capacity, TryWrite displaced one entry to
            // make room — count it. (Counting strictly: when count was at cap and we
            // just wrote, exactly one was dropped.)
            if (beforeCount >= OutboundQueueCapacity)
            {
                long total = Interlocked.Increment(ref _droppedSendCount);
                if (total % DropLogEveryN == 0)
                {
                    GlobalLogger.Log(
                        $"VisualistBusClient outbound queue overflow — dropped {total} message(s) total (oldest discarded).",
                        "BusClient", LogLevel.Communication);
                }
            }

            return Task.CompletedTask;
        }

        // Drain the outbound channel onto the socket. Exits when (a) the linked
        // CT trips (Stop or socket teardown), or (b) the socket leaves Open state. Send
        // failures abort the pump so the connect loop can re-establish; the not-yet-
        // sent queue head will be retried on reconnect because we read with await — only
        // messages actually transmitted leave the queue.
        private void StartPump(CancellationToken outerCt)
        {
            _pumpCts  = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            var pumpCt = _pumpCts.Token;
            _pumpTask = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCt.IsCancellationRequested)
                    {
                        if (!await _outbound.Reader.WaitToReadAsync(pumpCt).ConfigureAwait(false))
                            break;

                        if (!_outbound.Reader.TryPeek(out var json))
                            continue;

                        var ws = _ws;
                        if (ws == null || ws.State != WebSocketState.Open)
                        {
                            // Socket gone mid-pump. Don't consume — let the next
                            // connection's pump retry this message.
                            break;
                        }

                        try
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(json);
                            await ws.SendAsync(new ArraySegment<byte>(bytes),
                                WebSocketMessageType.Text, true, pumpCt).ConfigureAwait(false);
                            // Only consume on successful send so a transient send fault
                            // doesn't lose the message.
                            _outbound.Reader.TryRead(out _);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            GlobalLogger.Log($"BusClient send error: {ex.Message}", "BusClient", LogLevel.CriticalError);
                            // Bail; reconnect path will respawn the pump.
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { /* normal exit */ }
                catch (Exception ex)
                {
                    GlobalLogger.Error("VisualistBusClient", "Outbound pump faulted", ex);
                }
            }, pumpCt);
        }

        /// <summary>
        /// Fire-and-forget VISUAL_TRIGGER from the Visualist design-time editor, used by
        /// the per-trigger "Test Run" button in <c>WidgetEditorView</c>. The message
        /// envelope matches the wire format Hub's Bus already understands — it
        /// deserializes the payload, enqueues onto <c>LayerRuntime</c>, and the live OBS
        /// browser source picks it up. No completion wait — Test Run is a one-shot poke;
        /// the user observes the OBS render directly. <paramref name="eventData"/> may be
        /// null. Safe to call when disconnected — the message goes into the bounded
        /// outbound queue and pumps when reconnected.
        /// </summary>
        public Task SendVisualTriggerAsync(
            string layerId,
            string widgetId,
            string triggerName,
            System.Collections.Generic.Dictionary<string, string>? eventData = null)
        {
            if (string.IsNullOrEmpty(layerId) || string.IsNullOrEmpty(widgetId) || string.IsNullOrEmpty(triggerName))
                return Task.CompletedTask;

            var payload = new VisualTriggerPayload
            {
                LayerId     = layerId,
                WidgetId    = widgetId,
                TriggerName = triggerName,
                EventData   = eventData ?? new System.Collections.Generic.Dictionary<string, string>(),
                WaitId      = "",   // Test Run is fire-and-forget; no completion wait registered.
            };

            return SendAsync(new BusMessage
            {
                Type    = "VISUAL_TRIGGER",
                Source  = "Visualist",
                Target  = "Hub",
                Payload = JsonSerializer.Serialize(payload),
            });
        }

        /// <summary>
        /// Broadcasts a live widget rect change so production OBS browsers (connected to
        /// /hud/&lt;layerId&gt;) catch up immediately, without the save → LayerWatcher
        /// reload round-trip. Hub's Bus forwards the WIDGET_UPDATE payload to
        /// HUDServer.SendToLayerAsync, which fans it out to every connected browser;
        /// compositor.js's existing WIDGET_UPDATE handler applies the new rect and
        /// re-renders.
        ///
        /// Payload shape mirrors the in-process WebView2 message exactly so compositor.js
        /// doesn't need to know whether the update came from the design-time preview or
        /// the production browser path.
        /// </summary>
        public Task SendWidgetLiveUpdateAsync(string layerId, LayerWidget widget)
        {
            if (string.IsNullOrEmpty(layerId) || widget is null) return Task.CompletedTask;

            var payload = new
            {
                type     = "WIDGET_UPDATE",
                layerId,
                widgetId = widget.Id,
                name     = widget.Name,
                zIndex   = widget.ZIndex,
                rect     = new { x = widget.Rect.X, y = widget.Rect.Y, width = widget.Rect.Width, height = widget.Rect.Height },
            };

            return SendAsync(new BusMessage
            {
                Type    = "WIDGET_UPDATE",
                Source  = "Visualist",
                Target  = "Hub",
                Payload = JsonSerializer.Serialize(payload),
            });
        }

        // ── Test hooks (internal) ─────────────────────────────────────────
        // Exposed (via the csproj InternalsVisibleTo grant to Phoenix.Controls.Tests)
        // to verify queue + drop semantics without standing up a real WebSocket.
        // NOT part of the public API.
        internal int OutboundQueueCount => _outbound.Reader.Count;
        internal int OutboundQueueCapacityForTests => OutboundQueueCapacity;
        internal void EnqueueRawForTests(string json) => _outbound.Writer.TryWrite(json);
        internal bool TryDequeueRawForTests(out string? json)
        {
            if (_outbound.Reader.TryRead(out var s)) { json = s; return true; }
            json = null;
            return false;
        }
        internal void ResetDroppedCountForTests() => Interlocked.Exchange(ref _droppedSendCount, 0);
    }
}
