using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Cross-file Event.Trigger / Event.Executor / Event.Return socket-list
    /// propagation. Mirrors the WinForms canvas's pre-T15
    /// <c>SyncCrossFileEventPairs</c> band: when a graph activates a new arg
    /// or return socket on a paired event node, peer .phxg files in the
    /// same project directory get the matching socket list applied and
    /// re-saved.
    /// </summary>
    /// <remarks>
    /// File IO sits here (out of <see cref="PlaceholderActivator"/> which is
    /// pure-graph) so the model layer stays unit-testable without a disk.
    /// All operations are best-effort — a corrupt peer .phxg / locked file
    /// gets logged via <see cref="GlobalLogger"/> and skipped, never
    /// rethrown into the canvas's wire-create path.
    /// </remarks>
    public static class EventPairCrossFileSync
    {
        // PERF (perf/architect-blockers, BlockerE): pre-cache, both SyncAsync
        // and LoadPeerEventIndex ran Directory.GetFiles + GraphSerializer.LoadGraph
        // for every peer .phxg on every call. SyncAsync fires from the canvas's
        // wire-drop / placeholder-rename / event-name-change paths — i.e. on
        // every Event-node edit — so a 50-file project meant 50 full JSON
        // deserialises per edit. LoadPeerEventIndex fires from
        // RefreshEventPairErrorState, also frequent.
        //
        // Gate by (projectDir + peer file mtime list): if no peer .phxg has
        // changed since the last index build, reuse the cached event-pair set
        // instead of re-deserialising every file. SyncAsync invalidates the
        // cache for projectDir whenever it writes back a peer — that's the
        // only mutation path that can change a peer's Event-node attributes
        // from within this process; external edits (e.g. Visualist saving a
        // .phxlayer) don't touch .phxg, and tools that DO modify .phxg outside
        // Architect already trigger the FileSystemWatcher which causes a full
        // graph reload (and any subsequent SyncAsync will see the new mtime).
        private sealed class PeerIndexCache
        {
            public required Dictionary<string, long> FileMtimes;   // path → ticks
            public required HashSet<(string Title, string EventName)> Index;
        }
        // Keyed by canonicalised projectDir; size-bounded by the number of
        // distinct project directories the user opens in a session (in practice 1-2).
        private static readonly Dictionary<string, PeerIndexCache> s_peerCache
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object s_peerCacheGate = new();

        private static void InvalidatePeerIndex(string projectDir)
        {
            if (string.IsNullOrEmpty(projectDir)) return;
            string canonical = SafeFullPath(projectDir) ?? projectDir;
            lock (s_peerCacheGate)
            {
                s_peerCache.Remove(canonical);
            }
        }

        /// <summary>
        /// Walk every <c>*.phxg</c> in <paramref name="projectDir"/> (other
        /// than <paramref name="currentFilePath"/>) and propagate the
        /// argument / return socket lists from <paramref name="sourceGraph"/>'s
        /// Event.* nodes onto matching peers in those files. Saves each
        /// modified peer back to disk via
        /// <see cref="GraphSerializer.SaveGraphAsync"/>.
        /// <para/>
        /// Returns silently when <paramref name="projectDir"/> is null or
        /// doesn't exist (user opened a graph from outside the canonical
        /// <c>data/logic/</c> folder).
        /// </summary>
        /// <summary>
        /// Build detached clones of every Event.Trigger / Event.Executor /
        /// Event.Return node in <paramref name="graph"/> — copying the Title,
        /// EventName attribute, and socket shape (name / type / colour /
        /// data-type) that <see cref="SyncSocketNamesAcrossGraphs"/> reads.
        /// MUST be called on the thread that owns <paramref name="graph"/>
        /// (the UI thread for the live canvas graph): the clone severs the
        /// shared-mutable reference so the peer enumerate / parse / write can
        /// run on a thread-pool thread without racing concurrent canvas edits.
        /// Cheap — proportional to the (typically few) event nodes, not the
        /// whole graph.
        /// <para/>
        /// PERF (perf/architect-blockers, freeze sweep): the canvas used to
        /// hand the live graph straight into <see cref="SyncAsync(string?, string?, Graph)"/>
        /// from a debounced DispatcherTimer tick. <c>SafeRunAsync</c> runs its
        /// delegate on the calling thread until the first await, and SyncAsync
        /// has no await in the common no-peer-change case, so the entire
        /// sibling-<c>.phxg</c> enumerate + parse loop ran on the UI thread
        /// ~350 ms after every edit to an event-bearing graph — a periodic,
        /// repro-resistant freeze that scaled with the project's file count.
        /// Snapshotting here + a <c>Task.Run</c> hop at the call site moves all
        /// of that off the UI thread.
        /// </summary>
        public static IReadOnlyList<Node> SnapshotEventNodes(Graph? graph)
        {
            var result = new List<Node>();
            if (graph?.Nodes is null) return result;
            foreach (var n in graph.Nodes)
            {
                if (n.Title is not ("Event.Trigger" or "Event.Executor" or "Event.Return")) continue;
                var clone = new Node
                {
                    Title      = n.Title,
                    Attributes = n.Attributes is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(n.Attributes),
                    Sockets    = new List<Socket>(n.Sockets?.Count ?? 0),
                };
                if (n.Sockets is not null)
                {
                    foreach (var s in n.Sockets)
                    {
                        clone.Sockets.Add(new Socket
                        {
                            Name          = s.Name,
                            Type          = s.Type,
                            DataType      = s.DataType,
                            Color         = s.Color,
                            IsPlaceholder = s.IsPlaceholder,
                        });
                    }
                }
                result.Add(clone);
            }
            return result;
        }

        /// <summary>
        /// Graph-overload kept for callers / tests that hand the whole graph.
        /// Snapshots on the CALLING thread (see <see cref="SnapshotEventNodes"/>),
        /// so off-UI-thread callers must pass a graph they own. The live canvas
        /// uses the snapshot overload directly so the snapshot stays on the UI
        /// thread and the file I/O stays off it.
        /// </summary>
        public static Task SyncAsync(string? projectDir, string? currentFilePath, Graph sourceGraph)
            => SyncAsync(projectDir, currentFilePath, SnapshotEventNodes(sourceGraph));

        public static async Task SyncAsync(
            string? projectDir, string? currentFilePath, IReadOnlyList<Node> sourceEventNodes)
        {
            if (sourceEventNodes is null || sourceEventNodes.Count == 0) return;
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return;

            // Already filtered to Event.* nodes by SnapshotEventNodes.
            var eventNodes = sourceEventNodes;

            string[] peerPaths;
            try { peerPaths = Directory.GetFiles(projectDir, "*.phxg"); }
            catch (Exception ex)
            {
                GlobalLogger.Error("EventPairCrossFileSync",
                    $"enumerate '*.phxg' in '{projectDir}' failed", ex);
                return;
            }

            string? selfFull = string.IsNullOrEmpty(currentFilePath)
                ? null
                : SafeFullPath(currentFilePath);

            foreach (var peerPath in peerPaths)
            {
                if (selfFull is not null && string.Equals(SafeFullPath(peerPath), selfFull, StringComparison.OrdinalIgnoreCase))
                    continue;

                Graph peerGraph;
                try { peerGraph = GraphSerializer.LoadGraph(peerPath); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("EventPairCrossFileSync",
                        $"load peer '{Path.GetFileName(peerPath)}' failed", ex);
                    continue;
                }
                if (peerGraph is null || peerGraph.Nodes.Count == 0) continue;

                bool changed = false;
                foreach (var srcNode in eventNodes)
                {
                    if (!srcNode.Attributes.TryGetValue("EventName", out var evName)) continue;
                    if (string.IsNullOrWhiteSpace(evName)) continue;

                    foreach (var peerNode in peerGraph.Nodes)
                    {
                        if (peerNode.Title != srcNode.Title) continue;
                        if (!peerNode.Attributes.TryGetValue("EventName", out var peerEv)) continue;
                        if (!peerEv.Equals(evName, StringComparison.OrdinalIgnoreCase)) continue;
                        SyncSocketNamesAcrossGraphs(srcNode, peerNode, ref changed);
                    }
                }

                if (!changed) continue;

                try { await GraphSerializer.SaveGraphAsync(peerGraph, peerPath).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("EventPairCrossFileSync",
                        $"save peer '{Path.GetFileName(peerPath)}' failed", ex);
                }
            }

            // We just (potentially) wrote peers — the next LoadPeerEventIndex
            // must re-scan to pick up our writes.
            InvalidatePeerIndex(projectDir);
        }

        /// <summary>
        /// Returns true when <paramref name="projectDir"/> contains any
        /// <c>*.phxg</c> with an Event.Trigger / Event.Executor whose
        /// EventName matches <paramref name="eventName"/> and whose title
        /// equals <paramref name="peerTitle"/>. Used by the canvas to keep
        /// red-border error state off nodes whose peer happens to live in
        /// a different file rather than the current one.
        /// </summary>
        /// <remarks>
        /// Single-shot variant — re-loads every sibling <c>.phxg</c> on each
        /// call. For tight loops over a graph's Event.* nodes (canvas's
        /// RefreshEventPairErrorState), build the index once via
        /// <see cref="LoadPeerEventIndex"/> and look up against the resulting
        /// HashSet so a 10-node × 8-file graph doesn't trigger 80 file parses.
        /// </remarks>
        public static bool PeerExistsInOtherFiles(
            string? projectDir,
            string? currentFilePath,
            string  eventName,
            string  peerTitle)
        {
            var index = LoadPeerEventIndex(projectDir, currentFilePath);
            return index.Contains((peerTitle, eventName));
        }

        /// <summary>
        /// Build a single index of <c>(Title, EventName)</c> tuples covering
        /// every Event.Trigger / Event.Executor / Event.Return in every
        /// sibling <c>*.phxg</c> under <paramref name="projectDir"/> (other
        /// than <paramref name="currentFilePath"/>). The returned HashSet is
        /// case-insensitive on EventName, ordinal on Title — matching the
        /// per-node lookup semantics in <see cref="PeerExistsInOtherFiles"/>.
        ///
        /// Use this when checking many nodes against the project: the canvas
        /// pre-T15 had its own in-memory event-name cache; without it, every
        /// graph load re-parses every sibling .phxg per Event-pair node.
        /// Returned index reflects disk state at call time — if a sibling
        /// changes mid-session, rebuild before the next refresh.
        /// </summary>
        public static HashSet<(string Title, string EventName)> LoadPeerEventIndex(
            string? projectDir,
            string? currentFilePath)
        {
            var index = new HashSet<(string, string)>(PeerEntryComparer.Instance);
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return index;

            string? selfFull = string.IsNullOrEmpty(currentFilePath)
                ? null
                : SafeFullPath(currentFilePath);
            string canonical = SafeFullPath(projectDir) ?? projectDir;

            // Build the current peer-path → mtime snapshot. Used both to
            // detect "nothing changed" (return cached index) and as the
            // payload to store for the next call.
            string[] peerPaths;
            try { peerPaths = Directory.GetFiles(projectDir, "*.phxg"); }
            catch (Exception ex)
            {
                GlobalLogger.Error("EventPairCrossFileSync",
                    $"enumerate '*.phxg' in '{projectDir}' failed", ex);
                return index;
            }

            var mtimes = new Dictionary<string, long>(peerPaths.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var p in peerPaths)
            {
                // Skip self before stat'ing — we don't want our own edits to
                // invalidate the cache (we re-render, peers don't change).
                if (selfFull is not null && string.Equals(SafeFullPath(p), selfFull, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { mtimes[p] = File.GetLastWriteTimeUtc(p).Ticks; }
                catch { mtimes[p] = 0; }
            }

            PeerIndexCache? cached;
            lock (s_peerCacheGate)
            {
                s_peerCache.TryGetValue(canonical, out cached);
            }

            if (cached is not null && MtimesEqual(cached.FileMtimes, mtimes))
            {
                // Return a copy so the caller is free to mutate without
                // corrupting our cached set.
                return new HashSet<(string Title, string EventName)>(cached.Index, PeerEntryComparer.Instance);
            }

            // Cold path — load every peer once, populate index.
            foreach (var peerPath in mtimes.Keys)
            {
                try
                {
                    var g = GraphSerializer.LoadGraph(peerPath);
                    foreach (var n in g.Nodes)
                    {
                        if (n.Title is not ("Event.Trigger" or "Event.Executor" or "Event.Return")) continue;
                        if (!n.Attributes.TryGetValue("EventName", out var ev) || string.IsNullOrWhiteSpace(ev)) continue;
                        index.Add((n.Title, ev));
                    }
                }
                catch { /* skip malformed peers */ }
            }

            lock (s_peerCacheGate)
            {
                s_peerCache[canonical] = new PeerIndexCache
                {
                    FileMtimes = mtimes,
                    Index      = new HashSet<(string Title, string EventName)>(index, PeerEntryComparer.Instance),
                };
            }
            return index;
        }

        private static bool MtimesEqual(Dictionary<string, long> a, Dictionary<string, long> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var v)) return false;
                if (v != kv.Value) return false;
            }
            return true;
        }

        private sealed class PeerEntryComparer : IEqualityComparer<(string Title, string EventName)>
        {
            public static readonly PeerEntryComparer Instance = new();
            public bool Equals((string Title, string EventName) x, (string Title, string EventName) y)
                => string.Equals(x.Title, y.Title, StringComparison.Ordinal)
                && string.Equals(x.EventName, y.EventName, StringComparison.OrdinalIgnoreCase);
            public int GetHashCode((string Title, string EventName) o)
                => HashCode.Combine(o.Title, o.EventName?.ToLowerInvariant());
        }

        private static void SyncSocketNamesAcrossGraphs(Node src, Node peer, ref bool changed)
        {
            foreach (var socketType in new[] { SocketType.Input, SocketType.Output })
            {
                var srcSockets  = src.Sockets.Where(s => s.Type == socketType && !s.IsPlaceholder && s.Name != "Flow").ToList();
                var peerSockets = peer.Sockets.Where(s => s.Type == socketType && !s.IsPlaceholder && s.Name != "Flow").ToList();
                int common = Math.Min(srcSockets.Count, peerSockets.Count);
                for (int i = 0; i < common; i++)
                {
                    if (peerSockets[i].Name     != srcSockets[i].Name     ||
                        peerSockets[i].Color    != srcSockets[i].Color     ||
                        peerSockets[i].DataType != srcSockets[i].DataType)
                    {
                        peerSockets[i].Name     = srcSockets[i].Name;
                        peerSockets[i].Color    = srcSockets[i].Color;
                        peerSockets[i].DataType = srcSockets[i].DataType;
                        changed = true;
                    }
                }
            }
        }

        private static string? SafeFullPath(string p)
        {
            try { return Path.GetFullPath(p); } catch { return null; }
        }
    }
}
