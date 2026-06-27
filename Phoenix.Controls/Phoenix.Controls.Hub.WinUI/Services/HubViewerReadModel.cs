using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Phoenix.Controls.ViewerServer;

namespace Phoenix.Controls.Hub.WinUI.Services;

/// <summary>
/// QC27-01 — Hub-side implementation of <see cref="IHubReadModel"/>. Wraps
/// the already-running <see cref="IHubServices"/> (LiveFeed / Chat /
/// ScriptHost / SystemLog / ConnectionStatus) plus the connection-flavour
/// signals so the v2 <see cref="ViewerServer"/> can serve a bootstrap
/// snapshot and push live updates without the server taking a direct
/// dependency on the Hub.WinUI service surface.
///
/// Lifetime mirrors HubServices: constructed once in
/// <see cref="HubBootstrapper"/> after the WinForms-era singletons (Bus /
/// WS / GlobalLogger / ScriptManager) are running, kept alive for the
/// process duration, disposed alongside the ViewerServer.
/// </summary>
internal sealed class HubViewerReadModel : IHubReadModel, IDisposable
{
    private const int SnapshotChatCap     = 100;
    private const int SnapshotLiveFeedCap = 100;
    private const int SnapshotSystemCap   = 200;

    private readonly IHubServices _services;
    private readonly string _channel;

    private readonly List<Action<ViewerEvent>> _subscribers = new();
    private readonly object _subLock = new();

    private readonly EventHandler<LiveFeedEntry>       _onLive;
    private readonly EventHandler<ChatMessage>         _onChat;
    private readonly EventHandler<ScriptStatus>        _onScript;
    private readonly EventHandler<SystemLogEntry>      _onSyslog;
    //  Payload-typed connection-status handler. The Viewer read-model
    // re-snapshots all three channels each fire, so e.Channel isn't routed on
    // here — but the type has to match the IConnectionStatus.StateChanged
    // contract.
    private readonly EventHandler<ConnectionStateChange> _onConn;

    private int _disposed;

    public HubViewerReadModel(IHubServices services, string channel)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _channel  = string.IsNullOrWhiteSpace(channel) ? "channel" : channel;

        _onLive   = (_, entry) => Dispatch(new ViewerLiveFeedAppended(ToViewerEntry(entry)));
        _onChat   = (_, msg)   => Dispatch(new ViewerChatAppended(ToViewerChat(msg)));
        _onScript = (_, st)    => Dispatch(new ViewerScriptStatusChanged(ToViewerScript(st)));
        _onSyslog = (_, e)     => Dispatch(new ViewerSystemLogAppended(ToViewerSyslog(e)));
        _onConn   = (_, _)     => Dispatch(new ViewerConnectionStatusChanged(SnapshotConnectionStatus()));

        _services.LiveFeed.EntryAdded     += _onLive;
        _services.Chat.MessageReceived    += _onChat;
        _services.Scripts.StatusChanged   += _onScript;
        _services.SystemLog.EntryAdded    += _onSyslog;
        _services.Status.StateChanged     += _onConn;
    }

    public Task<ViewerSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The underlying Snapshot() methods return point-in-time copies;
        // safe to bound + project on the calling thread.
        var live = _services.LiveFeed.Snapshot();
        var chat = _services.Chat.Snapshot();
        var scr  = _services.Scripts.Snapshot();
        var sys  = _services.SystemLog.Snapshot();

        var snap = new ViewerSnapshot
        {
            Channel          = _channel,
            CapturedAt       = DateTimeOffset.UtcNow,
            ConnectionStatus = SnapshotConnectionStatus(),
            LiveFeed         = TailProjection(live, SnapshotLiveFeedCap, ToViewerEntry),
            Chat             = TailProjection(chat, SnapshotChatCap, ToViewerChat),
            Scripts          = scr.Select(ToViewerScript).ToList(),
            SystemLog        = TailProjection(sys, SnapshotSystemCap, ToViewerSyslog),
        };
        return Task.FromResult(snap);
    }

    public IDisposable Subscribe(Action<ViewerEvent> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        lock (_subLock) _subscribers.Add(handler);
        return new Subscription(this, handler);
    }

    private void Dispatch(ViewerEvent evt)
    {
        Action<ViewerEvent>[] snap;
        lock (_subLock) snap = _subscribers.ToArray();
        foreach (var s in snap)
        {
            try { s(evt); }
            catch (Exception ex)
            {
                // ViewerServer wraps SendAsync per-session, so a faulting
                // subscriber here is the read-model's own bug — log loudly
                // rather than swallow.
                GlobalLogger.Error("HubViewerReadModel", "subscriber threw", ex);
            }
        }
    }

    private ViewerConnectionStatus SnapshotConnectionStatus()
    {
        var s = _services.Status;
        return new ViewerConnectionStatus
        {
            StreamerBot = s.StreamerBot.ToString(),
            HudOverlay  = s.HudOverlay.ToString(),
            IpcBus      = s.IpcBus.ToString(),
            // ViewerSnapshot has a TwitchIrc slot but the IHubServices
            // status aggregator doesn't currently expose it as a distinct
            // signal — Twitch IRC liveness travels through WS today, so we
            // mirror StreamerBot here. When the dedicated IRC signal lands
            // (TODO 2026-05-15 in ConnectionStatus.cs), swap in the real
            // value without touching the wire shape.
            TwitchIrc   = s.StreamerBot.ToString(),
        };
    }

    private static ViewerLiveFeedEntry ToViewerEntry(LiveFeedEntry e) =>
        new(e.Timestamp, e.Kind.ToString(), e.Who, e.Detail);

    private static ViewerChatMessage ToViewerChat(ChatMessage m) =>
        new(m.Timestamp, m.Role.ToString(), m.Username, m.Body);

    private static ViewerScriptStatus ToViewerScript(ScriptStatus s) =>
        new(
            s.Path,
            s.Name,
            s.State.ToString(),
            s.Enabled,
            s.LastCpu.TotalMilliseconds,
            s.LastRamBytes,
            s.RunCount,
            s.LastError);

    private static ViewerSystemLogEntry ToViewerSyslog(SystemLogEntry e) =>
        new(e.Timestamp, e.Level.ToString(), e.Source, e.Message);

    private static List<TOut> TailProjection<TIn, TOut>(
        IReadOnlyList<TIn> source, int max, Func<TIn, TOut> project)
    {
        if (source.Count <= max) return source.Select(project).ToList();
        var result = new List<TOut>(max);
        for (int i = source.Count - max; i < source.Count; i++)
            result.Add(project(source[i]));
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _services.LiveFeed.EntryAdded     -= _onLive;   } catch { }
        try { _services.Chat.MessageReceived    -= _onChat;   } catch { }
        try { _services.Scripts.StatusChanged   -= _onScript; } catch { }
        try { _services.SystemLog.EntryAdded    -= _onSyslog; } catch { }
        try { _services.Status.StateChanged     -= _onConn;   } catch { }
        lock (_subLock) _subscribers.Clear();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly HubViewerReadModel _owner;
        private readonly Action<ViewerEvent> _handler;
        private int _disposed;
        public Subscription(HubViewerReadModel owner, Action<ViewerEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (_owner._subLock) _owner._subscribers.Remove(_handler);
        }
    }
}
