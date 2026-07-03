using System;
using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Process-wide live propagation of Event.Trigger / Event.Executor /
/// Event.Return dynamic-bubble edits to EVERY open Architect canvas — so a
/// paired Event node open in ANOTHER window (main pillar tab or a sibling
/// window) updates the instant a bubble is added / removed / retyped, without a
/// disk round-trip or a manual reload.
/// <para>
/// This closes the gap that made cross-file event sync "look broken":
/// <see cref="Core.EventPairCrossFileSync"/> only ever wrote peer <c>.phxg</c>
/// files to DISK, and Architect has no <c>.phxg</c> FileSystemWatcher, so an
/// already-open peer window never saw the change. Because both the disk sync and
/// this live sync run the SAME idempotent algorithm
/// (<see cref="Core.EventPairCrossFileSync.ApplyEventPairSyncToGraph"/>) against
/// the same source snapshot, an open peer that receives the live update matches
/// what the disk sync writes — so the disk write can't clobber it, and no
/// "skip open peers" bookkeeping is needed.
/// </para>
/// <remarks>
/// All Architect canvases live on Hub's single UI dispatcher, and
/// <see cref="Broadcast"/> is invoked from the canvas's debounced sync tick
/// (UI thread), so peers are applied synchronously on the UI thread — no
/// marshalling. A sibling that faults while applying is logged and skipped so
/// one bad peer can't break the source edit or the other peers.
/// </remarks>
/// </summary>
public static class EventPairLiveSync
{
    /// <summary>A canvas that can receive a live cross-window Event-pair sync.</summary>
    internal interface IPeer
    {
        /// <summary>The .phxg path this canvas currently shows, or null if unsaved.</summary>
        string? LiveSyncFilePath { get; }
        void ApplyIncomingEventPairSync(IReadOnlyList<Node> sourceEventNodes);
    }

    private static readonly object s_gate = new();
    private static readonly List<IPeer> s_peers = new();

    /// <summary>
    /// Canonical absolute paths of every registered open canvas that currently
    /// shows a saved .phxg. The on-disk peer sync
    /// (<see cref="Core.EventPairCrossFileSync.SyncAsync(string?,string?,IReadOnlyList{Node},ISet{string})"/>)
    /// SKIPS these: an open peer is already updated live in-memory by
    /// <see cref="Broadcast"/> and persists through its own save, so a background
    /// disk write to the same file would only risk racing that save. Closed peers
    /// (not in this set) still get written to disk.
    /// </summary>
    internal static ISet<string> GetOpenPeerFilePaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IPeer[] snapshot;
        lock (s_gate) { snapshot = s_peers.ToArray(); }
        foreach (var p in snapshot)
        {
            var path = p.LiveSyncFilePath;
            if (string.IsNullOrEmpty(path)) continue;
            try { set.Add(System.IO.Path.GetFullPath(path)); }
            catch { set.Add(path); }
        }
        return set;
    }

    internal static void Register(IPeer peer)
    {
        if (peer is null) return;
        lock (s_gate) { if (!s_peers.Contains(peer)) s_peers.Add(peer); }
    }

    internal static void Unregister(IPeer peer)
    {
        if (peer is null) return;
        lock (s_gate) { s_peers.Remove(peer); }
    }

    /// <summary>
    /// Push <paramref name="sourceEventNodes"/> (a detached Event-node snapshot
    /// of the SOURCE graph) to every registered canvas EXCEPT
    /// <paramref name="source"/>. Each peer applies the socket delta to its own
    /// live graph if it holds a matching (Title, EventName) Event node and
    /// rebuilds the affected views. Must run on the UI thread.
    /// </summary>
    internal static void Broadcast(IPeer source, IReadOnlyList<Node> sourceEventNodes)
    {
        if (sourceEventNodes is null || sourceEventNodes.Count == 0) return;

        IPeer[] targets;
        lock (s_gate)
        {
            if (s_peers.Count == 0) return;
            targets = s_peers.Where(p => !ReferenceEquals(p, source)).ToArray();
        }

        foreach (var t in targets)
        {
            try { t.ApplyIncomingEventPairSync(sourceEventNodes); }
            catch (Exception ex)
            {
                GlobalLogger.Error("EventPairLiveSync",
                    "applying incoming Event-pair sync to a peer canvas", ex);
            }
        }
    }
}
