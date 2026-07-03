using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Viewer-roadmap Slice 0 — multi-Viewer WS session tracking.
    ///
    /// Holds the live <c>(deviceId → session[])</c> map for the RemoteBridge WS
    /// surface. Multiple Viewers may attach with the same paired device (rare but
    /// allowed); the map keeps every active socket and per-socket send lock so
    /// fanout from <see cref="BroadcastAsync"/> doesn't tear when one socket
    /// stalls. <see cref="CloseDeviceAsync"/> tears down all sessions tied to a
    /// device — Used by the Revoke action and on Hub shutdown.
    ///
    /// The registry is independent of <see cref="LayerRegistry"/> on purpose: the
    /// Bus/HUD surfaces deal in trusted-loopback peers, while this surface deals
    /// in authenticated-but-remote peers. Mixing them blurs the threat model and
    /// makes per-pillar ownership harder to reason about (see Viewer_Roadmap.md
    /// §"Architecture summary" invariant 1).
    /// </summary>
    public sealed class RemoteSessionRegistry
    {
        /// <summary>
        /// Per-socket SendAsync timeout. Mirrors the constant in
        /// <c>RemoteBridgeServer</c>; bumping one without the other is a bug
        /// magnet so consider extracting if a third caller appears.
        /// </summary>
        private static readonly TimeSpan SocketSendTimeout = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<string, List<RemoteSession>> _sessions = new();
        private readonly object _mutationLock = new();

        public int SessionCount
        {
            get
            {
                int total = 0;
                foreach (var list in _sessions.Values) total += list.Count;
                return total;
            }
        }

        public void Add(string deviceId, RemoteSession session)
        {
            lock (_mutationLock)
            {
                if (!_sessions.TryGetValue(deviceId, out var list))
                {
                    list = new List<RemoteSession>();
                    _sessions[deviceId] = list;
                }
                list.Add(session);
            }
        }

        public void Remove(string deviceId, RemoteSession session)
        {
            lock (_mutationLock)
            {
                if (_sessions.TryGetValue(deviceId, out var list))
                {
                    list.Remove(session);
                    if (list.Count == 0)
                        _sessions.TryRemove(deviceId, out _);
                }
            }
        }

        public IEnumerable<(string DeviceId, RemoteSession Session)> Snapshot()
        {
            // Copy under lock so concurrent Add/Remove can't tear the iteration.
            var copy = new List<(string, RemoteSession)>();
            lock (_mutationLock)
            {
                foreach (var kv in _sessions)
                    foreach (var s in kv.Value)
                        copy.Add((kv.Key, s));
            }
            return copy;
        }

        /// <summary>
        /// Fan a JSON-encoded payload to every connected Viewer. Per-socket send
        /// lock serializes frames on each socket so two concurrent broadcasts
        /// don't interleave on the wire. Failed sockets are scheduled for
        /// teardown but the loop continues for the rest.
        /// </summary>
        public async Task BroadcastAsync(string json, CancellationToken ct = default)
        {
            byte[] buf = Encoding.UTF8.GetBytes(json);
            var seg = new ArraySegment<byte>(buf);
            var dead = new List<(string DeviceId, RemoteSession Session)>();

            foreach (var (deviceId, session) in Snapshot())
            {
                if (session.Socket.State != WebSocketState.Open)
                {
                    dead.Add((deviceId, session));
                    continue;
                }
                try
                {
                    await session.SendLock.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue;
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { dead.Add((deviceId, session)); continue; }
                CancellationTokenSource? linked = null;
                try
                {
                    if (session.Socket.State != WebSocketState.Open)
                    {
                        dead.Add((deviceId, session));
                        continue;
                    }
                    // Bound each per-socket send so one stalled
                    // Viewer can't pin BroadcastAsync forever; on timeout we
                    // abort the socket and mark it dead so the next broadcast
                    // skips it cleanly.
                    linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(SocketSendTimeout);
                    try
                    {
                        await session.Socket.SendAsync(seg, WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        try { session.Socket.Abort(); } catch { }
                        dead.Add((deviceId, session));
                        GlobalLogger.Log(
                            $"RemoteSessionRegistry: aborted stalled WS for {deviceId} after {SocketSendTimeout.TotalSeconds:0}s.",
                            "RemoteSessions", LogLevel.CriticalError);
                    }
                }
                catch
                {
                    dead.Add((deviceId, session));
                }
                finally
                {
                    try { linked?.Dispose(); } catch { }
                    try { session.SendLock.Release(); } catch { }
                }
            }

            foreach (var (deviceId, session) in dead)
            {
                Remove(deviceId, session);
                try { session.SendLock.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Close every session bound to the given device. Used by Revoke and by
        /// Hub shutdown. Aborts each socket and disposes its send lock so
        /// awaiters unblock.
        /// </summary>
        public async Task CloseDeviceAsync(string deviceId, string reason)
        {
            List<RemoteSession>? list;
            lock (_mutationLock)
            {
                _sessions.TryRemove(deviceId, out list);
            }
            if (list is null) return;
            foreach (var session in list)
            {
                try
                {
                    if (session.Socket.State == WebSocketState.Open)
                        await session.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
                }
                catch { try { session.Socket.Abort(); } catch { } }
                try { session.SendLock.Dispose(); } catch { }
            }
        }

        public async Task CloseAllAsync(string reason)
        {
            List<string> ids;
            lock (_mutationLock)
            {
                ids = new List<string>(_sessions.Keys);
            }
            foreach (var id in ids)
                await CloseDeviceAsync(id, reason).ConfigureAwait(false);
        }
    }

    public sealed class RemoteSession
    {
        public WebSocket    Socket   { get; }
        public SemaphoreSlim SendLock { get; }
        public DateTime     ConnectedAtUtc { get; }

        public RemoteSession(WebSocket socket)
        {
            Socket = socket ?? throw new ArgumentNullException(nameof(socket));
            SendLock = new SemaphoreSlim(1, 1);
            ConnectedAtUtc = DateTime.UtcNow;
        }
    }
}
