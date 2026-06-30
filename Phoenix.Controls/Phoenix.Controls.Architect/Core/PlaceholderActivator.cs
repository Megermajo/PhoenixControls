using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Dynamic placeholder-socket lifecycle for Architect's variadic node
    /// templates: <c>Event.Trigger</c>, <c>Event.Executor</c>, <c>Event.Return</c>,
    /// <c>Macro.Entry</c>, <c>Macro.Exit</c>, <c>Process.Entry</c>,
    /// <c>Process.Exit</c>, <c>Visual.Trigger</c>.
    /// </summary>
    /// <remarks>
    /// Each of those node types persists one trailing <c>"+ variable"</c> /
    /// <c>"+ input"</c> / <c>"+ output"</c> / <c>"+ return"</c> placeholder
    /// socket. When the user drops a wire onto the placeholder,
    /// <see cref="Activate"/> turns it into a named, typed real socket
    /// (<c>Var1</c>, <c>In1</c>, etc.) and appends a fresh placeholder below
    /// it so the pattern can repeat. When the last wire on an activated
    /// socket is removed, <see cref="RevertIfOrphaned"/> resets the socket
    /// back to placeholder shape and drops the now-redundant trailing
    /// placeholder so the node doesn't accrete two side-by-side
    /// <c>+ variable</c> rows.
    /// <para/>
    /// Pre-T15 these methods lived in the WinForms canvas's
    /// <c>Canvas.EventPair.cs</c> / <c>Canvas.Wildcard.cs</c> partials. The
    /// retirement deleted those files but the WinUI port never replaced
    /// them, so dropping wires on placeholders silently failed: the wire
    /// landed but the socket stayed in placeholder shape and the next drop
    /// targeted the same row, evicting the first wire. Recreate the same
    /// graph-level mutation here, model-only — the canvas calls Activate
    /// from <c>TryCreateLink</c> and RevertIfOrphaned from the link-removal
    /// paths, then runs <c>NodeViewModel.RebuildSockets</c> to refresh the
    /// view.
    /// <para/>
    /// <see cref="SyncEventPair"/> mirrors the named arg / return sockets
    /// across every <c>Event.Trigger</c> / <c>Event.Executor</c> /
    /// <c>Event.Return</c> in the graph that share the same <c>EventName</c>
    /// attribute. The canvas calls it after every Activate / RevertIfOrphaned
    /// on those titles so renaming a slot on the trigger ripples into the
    /// executor (and vice versa) without the user touching the peer node.
    /// Cross-file propagation lives in <see cref="EventPairCrossFileSync"/>.
    /// </remarks>
    public static class PlaceholderActivator
    {
        // Mirror RecalculateSocketOffsets / RecalculateNodeSize from the
        // pre-T15 canvas — same constants the WinUI canvas uses.
        private const int SocketSpacing = 22;
        private const int NodeWidth     = 200;

        //  Header band height is no longer a single stale constant.
        // NodeGeometry (WinUI canvas) renders a 26px band for a plain node and
        // a 40px band for a node that shows an italic "definer" subtitle
        // (EventName / CompositionId / etc.). PlaceholderActivator lives in the
        // Architect *Core* library, which is referenced BY the WinUI assembly —
        // so it CANNOT take a using on Phoenix.Controls.Architect.WinUI.Canvas
        // (that would be a circular project reference and fail to build). The
        // header-height decision is therefore mirrored here, self-contained,
        // and MUST stay in lockstep with NodeGeometry.HeaderHeight /
        // NodeGeometry.HasDefiner / NodeGeometry.GetNodeDefiner. The old
        // hard-coded 24 placed sockets 2-16px too high on Event.* nodes whose
        // EventName grows the header to 40px, misaligning wire endpoints from
        // their rendered pins.
        private const int HeaderBandPlain = 26;
        private const int HeaderBandDef   = 40;

        /// <summary>
        /// Header band height for <paramref name="node"/> in the same units
        /// the WinUI canvas paints with — 40 when the node renders a definer
        /// subtitle (only Event.Trigger / Event.Executor / Event.Return via
        /// EventName and Visual.Trigger via CompositionId among the managed
        /// titles), 26 otherwise. Mirror of <c>NodeGeometry.HeaderHeight</c>;
        /// see the field comment above for why this is duplicated rather than
        /// referenced.
        /// </summary>
        private static int HeaderHeightFor(Node node)
            => NodeHasDefiner(node) ? HeaderBandDef : HeaderBandPlain;

        /// <summary>
        /// True when <paramref name="node"/> renders an italic definer
        /// subtitle for one of the titles PlaceholderActivator manages.
        /// Restricted to the managed set so the mirror stays small and
        /// auditable; the broader switch in <c>NodeGeometry.GetNodeDefiner</c>
        /// covers titles this helper never touches.
        /// </summary>
        private static bool NodeHasDefiner(Node node)
        {
            if (node?.Attributes is null) return false;
            string? v = node.Title switch
            {
                "Event.Trigger" or "Event.Executor" or "Event.Return"
                    => node.Attributes.TryGetValue("EventName", out var ev) ? ev : null,
                "Visual.Trigger"
                    => node.Attributes.TryGetValue("CompositionId", out var ci) ? ci : null,
                _ => null,
            };
            return !string.IsNullOrWhiteSpace(v);
        }

        /// <summary>
        /// Returns true when the node template carries a trailing
        /// <c>+ variable</c> / <c>+ input</c> / <c>+ output</c> /
        /// <c>+ return</c> placeholder socket whose lifecycle is managed
        /// by this helper.
        /// </summary>
        public static bool HasManagedPlaceholders(Node node)
            => node.Title is "Event.Trigger" or "Event.Executor" or "Event.Return"
                          or "Macro.Entry"   or "Macro.Exit"
                          or "Process.Entry" or "Process.Exit"
                          or "Visual.Trigger";

        /// <summary>
        /// Returns true when <paramref name="socket"/> on
        /// <paramref name="node"/> is a placeholder produced by this
        /// helper (i.e. one whose name matches the trailing-placeholder
        /// scheme for the node title).
        /// </summary>
        public static bool IsManagedPlaceholder(Node node, Socket socket)
        {
            if (!HasManagedPlaceholders(node)) return false;
            if (!socket.IsPlaceholder) return false;
            string n = socket.Name ?? string.Empty;
            return n is "+ variable" or "+ input" or "+ output" or "+ return";
        }

        /// <summary>
        /// Promote <paramref name="placeholder"/> on <paramref name="node"/>
        /// into a named, typed active socket and append a fresh
        /// placeholder of the same direction below it.
        /// <paramref name="sourceSocket"/> is the OTHER endpoint of the
        /// wire that triggered the activation — its color/data-type is
        /// inherited so the new active socket types correctly.
        /// </summary>
        public static void Activate(Graph? graph, Node node, Socket placeholder, Socket? sourceSocket = null)
        {
            if (node is null || placeholder is null) return; // [P1 swarm-audit 2026-05-29] guard null node/placeholder before IsManagedPlaceholder reads node.Title
            if (!IsManagedPlaceholder(node, placeholder)) return;

            //  Defend against a corrupted /
            // hand-edited graph whose placeholder carries the wrong polarity.
            // The node's already-activated managed sockets define the correct
            // direction for this group (non-Flow, non-placeholder); when they
            // are unambiguously one direction and the placeholder disagrees,
            // heal it before activating — otherwise a wrong-polarity socket
            // would render + wire on the wrong side and feed the wrong row math.
            // Direction-agnostic (matches whatever siblings are), so it never
            // rejects a legitimate activation; when the group is mixed or empty
            // (fresh node) the creation-time type stands.
            {
                var settled = node.Sockets
                    .Where(s => !s.IsPlaceholder && s.Name != "Flow")
                    .ToList();
                if (settled.Count > 0)
                {
                    bool anyIn  = settled.Any(s => s.Type == SocketType.Input);
                    bool anyOut = settled.Any(s => s.Type == SocketType.Output);
                    if (anyIn ^ anyOut)
                    {
                        var expected = anyIn ? SocketType.Input : SocketType.Output;
                        if (placeholder.Type != expected)
                        {
                            GlobalLogger.Log(
                                $"Placeholder polarity mismatch on '{node.Title}' (id {node.Id}) — healed " +
                                $"{placeholder.Type}→{expected} to match its socket group.",
                                "PlaceholderActivator", LogLevel.Communication);
                            placeholder.Type = expected;
                        }
                    }
                }
            }

            bool isReturnSocket = (node.Title == "Event.Executor" && placeholder.Type == SocketType.Input)
                               || (node.Title == "Event.Trigger"  && placeholder.Type == SocketType.Output)
                               ||  node.Title == "Event.Return";
            bool isMacroEntry = node.Title == "Macro.Entry";
            bool isMacroExit  = node.Title == "Macro.Exit";
            bool isProcEntry  = node.Title == "Process.Entry";
            bool isProcExit   = node.Title == "Process.Exit";

            int groupCount = node.Sockets.Count(s =>
                !s.IsPlaceholder && s.Name != "Flow" && s.Type == placeholder.Type);

            string newName = (isMacroEntry || isProcEntry) ? $"In{groupCount + 1}"
                          :  (isMacroExit  || isProcExit ) ? $"Out{groupCount + 1}"
                          :  isReturnSocket                 ? $"RetVal{groupCount + 1}"
                          :                                   $"Var{groupCount + 1}";

            Color inheritedColor = isReturnSocket
                ? NodeRegistry.ColReturn
                : (sourceSocket != null ? sourceSocket.Color : NodeRegistry.ColString);
            SocketDataType inheritedType = NodeRegistry.DataTypeFromColorPublic(inheritedColor);

            placeholder.IsPlaceholder = false;
            placeholder.Name          = newName;
            placeholder.Color         = inheritedColor;
            placeholder.DataType      = inheritedType;

            // Append a fresh placeholder of the same direction underneath.
            string nextPlaceholderName = isReturnSocket            ? "+ return"
                                       : (isMacroEntry || isProcEntry) ? "+ input"
                                       : (isMacroExit  || isProcExit ) ? "+ output"
                                       :                                  "+ variable";

            bool isInput  = placeholder.Type == SocketType.Input;
            int width     = node.Size.Width > 0 ? node.Size.Width : NodeWidth;
            int nextOffsetY = RecalculateSocketOffsets(node);

            // DataType derived from colour at creation — the canvas reads it for pin
            // SHAPE + wire compatibility; left at the Any default a freshly-appended
            // placeholder renders as a ◆ Diamond instead of the correct pin and rejects
            // normal wires until save+reload (same class as the node-factory fix).
            Color nextPhColor = isReturnSocket ? NodeRegistry.ColReturn : NodeRegistry.ColString;
            node.Sockets.Add(new Socket
            {
                Name          = nextPlaceholderName,
                Type          = placeholder.Type,
                Color         = nextPhColor,
                DataType      = NodeRegistry.DataTypeFromColorPublic(nextPhColor),
                IsPlaceholder = true,
                Offset        = new Point(isInput ? -6 : width - 14, nextOffsetY),
            });

            RecalculateNodeSize(node);

            //  Activate added a socket and renamed an existing one — both
            // structural changes that need to invalidate Graph's id-indexed caches
            // before the next per-paint lookup runs. The graph-level cache otherwise
            // self-heals lazily on each miss (paint-cost regression) until the next
            // legit MarkStructuralChange somewhere else.
            graph?.MarkStructuralChange();

            // Mirror the new socket onto every paired Event.* node sharing
            // the same EventName. Skipped for Macro / Process / Visual.Trigger
            // — those don't have the cross-node pairing model.
            if (graph is not null && IsEventPairTitle(node.Title))
                SyncEventPair(graph, node);
        }

        /// <summary>
        /// If <paramref name="socket"/> is a previously-activated placeholder
        /// (named <c>Var\d+</c> / <c>In\d+</c> / <c>Out\d+</c> /
        /// <c>RetVal\d+</c>) and no link references it any more, reset it to
        /// placeholder shape and drop the trailing <c>+ variable</c> /
        /// <c>+ input</c> / <c>+ output</c> / <c>+ return</c> sibling that
        /// was added when it was first activated. No-op when the socket is
        /// still wired or doesn't match the activation naming scheme.
        /// </summary>
        public static void RevertIfOrphaned(Graph graph, Node node, Socket socket)
        {
            if (graph is null || node is null || socket is null) return;
            if (!HasManagedPlaceholders(node)) return;

            // Only sockets that Activate would have produced are reset-eligible.
            if (!Regex.IsMatch(socket.Name ?? string.Empty, @"^(In|Out|Var|RetVal)\d+$"))
                return;

            // Still wired anywhere? Leave it active.
            if (graph.Links.Exists(l => l.FromSocketId == socket.Id || l.ToSocketId == socket.Id))
                return;

            bool isReturnSocket = (node.Title == "Event.Executor" && socket.Type == SocketType.Input)
                               || (node.Title == "Event.Trigger"  && socket.Type == SocketType.Output)
                               ||  node.Title == "Event.Return";
            bool isMacroEntry = node.Title == "Macro.Entry";
            bool isMacroExit  = node.Title == "Macro.Exit";
            bool isProcEntry  = node.Title == "Process.Entry";
            bool isProcExit   = node.Title == "Process.Exit";

            string trailingName = (isMacroEntry || isProcEntry) ? "+ input"
                                : (isMacroExit  || isProcExit ) ? "+ output"
                                : isReturnSocket                 ? "+ return"
                                :                                  "+ variable";

            // Drop the trailing placeholder of the same direction that
            // Activate appended; otherwise the node would carry two
            // placeholder rows after revert.
            var trailing = node.Sockets.LastOrDefault(s =>
                s.IsPlaceholder && s.Type == socket.Type && s.Name == trailingName);
            if (trailing is not null) node.Sockets.Remove(trailing);

            // Reset the activated socket back to placeholder shape. DataType is
            // derived from the (just-reset) colour, NOT hardcoded to String — a return
            // placeholder is ColReturn and must not be coerced to String, or it reverts
            // to the wrong pin shape (the canvas derives shape from DataType).
            socket.IsPlaceholder = true;
            socket.Name          = trailingName;
            socket.Color         = isReturnSocket ? NodeRegistry.ColReturn : NodeRegistry.ColString;
            socket.DataType      = NodeRegistry.DataTypeFromColorPublic(socket.Color);

            RecalculateSocketOffsets(node);
            RecalculateNodeSize(node);

            //  Revert removed the trailing placeholder and renamed the
            // activated socket back to placeholder shape — both structural mutations.
            graph.MarkStructuralChange();

            // Same paired-sync rationale as Activate — keep paired Event.*
            // nodes' socket lists in lockstep so a revert on the trigger
            // shrinks the executor side too.
            if (IsEventPairTitle(node.Title))
                SyncEventPair(graph, node);
        }

        /// <summary>
        /// One-shot sync called from <see cref="LogicCanvasViewModel.LoadGraph"/>
        /// — walks every <c>Event.Trigger</c> / <c>Event.Executor</c> /
        /// <c>Event.Return</c> in the graph and pulls the source-of-truth
        /// socket list onto each peer that shares the same EventName. Pre-fix
        /// the WinForms canvas had a <c>SyncAllEventPairs</c> at load time;
        /// without it, opening a graph saved while peers were out of sync
        /// rendered with stale socket names until the next user-triggered
        /// activation.
        /// </summary>
        public static void SyncAllEventPairs(Graph graph)
        {
            if (graph is null) return;
            // R4a (audit 2026-06-03): was O(uniqueEvents * n) on graph-load — each
            // unique EventName called SyncEventPair, which re-scanned graph.Nodes to
            // find peers. Build the EventName->nodes index ONCE (O(n)) and reuse it.
            // Behavior-identical (same pairing semantics, same mutation order).
            var byEventName = BuildEventNodeIndex(graph);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in graph.Nodes)
            {
                if (node is null) continue;
                if (!IsEventPairTitle(node.Title)) continue;
                if (!node.Attributes.TryGetValue("EventName", out var ev) || string.IsNullOrWhiteSpace(ev)) continue;
                if (!seenNames.Add(ev)) continue;
                SyncEventPairIndexed(graph, node, byEventName);
            }
        }

        // R4a — group event-pair nodes by EventName once (O(n)) so SyncEventPair's
        // peer loop is O(peers) instead of O(n). Local + rebuilt per top-level call,
        // so there is no index-staleness/invalidation risk.
        private static Dictionary<string, List<Node>> BuildEventNodeIndex(Graph graph)
        {
            var idx = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in graph.Nodes)
            {
                if (node is null || node.Attributes is null) continue;
                if (!IsEventPairTitle(node.Title)) continue;
                if (!node.Attributes.TryGetValue("EventName", out var ev) || string.IsNullOrWhiteSpace(ev)) continue;
                if (!idx.TryGetValue(ev, out var list)) { list = new List<Node>(); idx[ev] = list; }
                list.Add(node);
            }
            return idx;
        }

        /// <summary>
        /// Mirror the active named arg / return sockets from
        /// <paramref name="source"/> onto every paired Event.Trigger /
        /// Event.Executor / Event.Return in <paramref name="graph"/> that
        /// shares the same EventName attribute. Argument sockets live on the
        /// Input side of the trigger and the Output side of the executor;
        /// return sockets live on the Output side of the trigger and the
        /// Input side of the executor — the two role pairs are mirrored
        /// independently.
        /// </summary>
        public static void SyncEventPair(Graph graph, Node source)
        {
            if (graph is null || source is null) return;
            // R4a (audit 2026-06-03): standalone call (e.g. wire-drop) — build a
            // one-shot EventName index then run the shared indexed core, so the
            // peer loop no longer rescans all of graph.Nodes. Behavior-identical.
            SyncEventPairIndexed(graph, source, BuildEventNodeIndex(graph));
        }

        private static void SyncEventPairIndexed(Graph graph, Node source, Dictionary<string, List<Node>> byEventName)
        {
            if (graph is null || source is null) return;
            if (!IsEventPairTitle(source.Title)) return;
            if (!source.Attributes.TryGetValue("EventName", out var eventName)) return;
            if (string.IsNullOrWhiteSpace(eventName)) return;

            var srcArgType = source.Title == "Event.Trigger" ? SocketType.Input  : SocketType.Output;
            var srcRetType = source.Title == "Event.Trigger" ? SocketType.Output : SocketType.Input;

            var argSources = source.Sockets.Where(s => !s.IsPlaceholder && s.Name != "Flow" && s.Type == srcArgType).ToList();
            var retSources = source.Sockets.Where(s => !s.IsPlaceholder && s.Name != "Flow" && s.Type == srcRetType).ToList();

            //  Track whether any peer was actually mutated so we only
            // burn the cache when something changed. SyncSocketGroup adds/removes
            // sockets and prunes links, so any peer mutation is a structural change.
            bool anyMutation = false;

            // R4a: peers come from the prebuilt index (all event-pair nodes sharing
            // this EventName). The per-node title/name re-checks below are redundant
            // given the index but kept for obvious behavioral equivalence.
            if (!byEventName.TryGetValue(eventName, out var peers)) return;
            foreach (var node in peers)
            {
                if (node is null) continue;
                if (ReferenceEquals(node, source)) continue;
                if (!IsEventPairTitle(node.Title)) continue;
                if (!node.Attributes.TryGetValue("EventName", out var otherName)) continue;
                if (!otherName.Equals(eventName, StringComparison.OrdinalIgnoreCase)) continue;

                var peerArgType = node.Title == "Event.Trigger" ? SocketType.Input  : SocketType.Output;
                var peerRetType = node.Title == "Event.Trigger" ? SocketType.Output : SocketType.Input;

                int beforeCount = node.Sockets.Count;
                int beforeLinks = graph.Links.Count;
                SyncSocketGroup(graph, node, argSources, peerArgType, NodeRegistry.ColString);
                SyncSocketGroup(graph, node, retSources, peerRetType, NodeRegistry.ColReturn);
                if (node.Sockets.Count != beforeCount || graph.Links.Count != beforeLinks)
                    anyMutation = true;

                // Re-stripe offsets + size so the peer's visual layout
                // matches the new socket list.
                RecalculateSocketOffsets(node);
                RecalculateNodeSize(node);
            }

            if (anyMutation)
                graph.MarkStructuralChange();
        }

        /// <summary>
        /// Compute the set of node ids on <paramref name="graph"/> that are
        /// <c>Event.Trigger</c> / <c>Event.Executor</c> with a non-empty
        /// EventName attribute that has no peer of the opposite role in the
        /// same graph. The canvas paints these with a red border so the
        /// authoring slip (typo, deleted peer) is visible without opening
        /// the script for execution.
        /// </summary>
        public static HashSet<string> ComputeUnpairedEventNodeIds(Graph graph)
        {
            var bad = new HashSet<string>(StringComparer.Ordinal);
            if (graph is null) return bad;
            // R4a (audit 2026-06-03): was O(n^2) — an inner graph.Nodes.Any() peer
            // scan per Event node, fired on graph-load + every wire-drop. Build a
            // single O(n) presence index (EventName -> a Trigger exists / an Executor
            // exists), then do O(1) peer checks. Behavior-identical: a node is
            // "unpaired" iff no node of the opposite role shares its EventName.
            var hasTrigger  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasExecutor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in graph.Nodes)
            {
                if (node is null || node.Attributes is null) continue;
                if (node.Title is not ("Event.Trigger" or "Event.Executor")) continue;
                if (!node.Attributes.TryGetValue("EventName", out var ev) || string.IsNullOrWhiteSpace(ev)) continue;
                if (node.Title == "Event.Trigger") hasTrigger.Add(ev);
                else hasExecutor.Add(ev);
            }
            foreach (var node in graph.Nodes)
            {
                if (node is null || node.Attributes is null) continue; // [P1 swarm-audit 2026-05-29] guard null node/Attributes before deref
                if (node.Title is not ("Event.Trigger" or "Event.Executor")) continue;
                if (!node.Attributes.TryGetValue("EventName", out var ev) || string.IsNullOrWhiteSpace(ev)) continue;
                bool hasPeer = node.Title == "Event.Trigger"
                    ? hasExecutor.Contains(ev)
                    : hasTrigger.Contains(ev);
                if (!hasPeer) bad.Add(node.Id);
            }
            return bad;
        }

        private static bool IsEventPairTitle(string? title)
            => title is "Event.Trigger" or "Event.Executor" or "Event.Return";

        private static void SyncSocketGroup(
            Graph              graph,
            Node               peer,
            List<Socket>       sources,
            SocketType         targetType,
            Color              fallbackColor)
        {
            var targets = peer.Sockets
                .Where(s => !s.IsPlaceholder && s.Name != "Flow" && s.Type == targetType)
                .ToList();

            // Sync existing sockets in order (name + color + datatype).
            int common = Math.Min(sources.Count, targets.Count);
            for (int i = 0; i < common; i++)
            {
                targets[i].Name     = sources[i].Name;
                targets[i].Color    = sources[i].Color;
                targets[i].DataType = sources[i].DataType;
            }

            // Drop excess targets (sources shrank). Wires touching them go
            // too — the peer's sockets really are gone.
            for (int i = sources.Count; i < targets.Count; i++)
            {
                var excess = targets[i];
                graph.Links.RemoveAll(l => l.FromSocketId == excess.Id || l.ToSocketId == excess.Id);
                peer.Sockets.Remove(excess);
            }

            // Append new sockets that exist on source but not yet on peer.
            // Insert above the trailing placeholder so the placeholder stays
            // last; if no placeholder is present, append at the end.
            targets = peer.Sockets
                .Where(s => !s.IsPlaceholder && s.Name != "Flow" && s.Type == targetType)
                .ToList();
            int peerWidth = peer.Size.Width > 0 ? peer.Size.Width : NodeWidth;
            bool isInput  = targetType == SocketType.Input;
            while (targets.Count < sources.Count)
            {
                var src = sources[targets.Count];
                var newSock = new Socket
                {
                    Id       = Guid.NewGuid().ToString(),
                    Name     = src.Name,
                    Type     = targetType,
                    Color    = src.Color,
                    DataType = src.DataType,
                    Offset   = new Point(isInput ? -6 : peerWidth - 14, 0),
                };
                var trailingPlaceholder = peer.Sockets.FirstOrDefault(s => s.IsPlaceholder && s.Type == targetType);
                int insertAt = trailingPlaceholder is not null
                    ? peer.Sockets.IndexOf(trailingPlaceholder)
                    : peer.Sockets.Count;
                peer.Sockets.Insert(insertAt, newSock);
                targets.Add(newSock);
                _ = fallbackColor; // reserved for future "no source colour" branch — currently src.Color always wins
            }
        }

        /// <summary>
        /// Walk all non-Flow sockets on <paramref name="node"/> and
        /// re-stripe their Y offsets in declaration order so the visual
        /// layout matches the underlying socket list. Returns the Y
        /// offset for the next socket appended below the existing rows.
        /// </summary>
        /// <remarks>
        ///  Exposed as public (was private) so
        /// <see cref="NodeRegistry.EnsureEventNodePlaceholders"/> can stripe
        /// the freshly-added placeholder socket's Y offset on the recovery
        /// path. Without this the recovery sockets render at (0,0) for a
        /// single frame on graph load until the canvas's next layout pass.
        /// </remarks>
        public static int RecalculateSocketOffsets(Node node)
        {
            //  Use the node's dynamic header band (26 plain / 40 with a
            // definer subtitle) instead of a stale 24px constant so socket
            // offsets line up with the painted pins on Event.* nodes whose
            // EventName grows the header.
            int headerH = HeaderHeightFor(node);
            int inputRow  = 0;
            int outputRow = 0;
            foreach (var s in node.Sockets)
            {
                if (s.Name == "Flow")
                {
                    s.Offset = new Point(s.Offset.X, headerH + 6);
                    continue;
                }
                if (s.Type == SocketType.Input)
                {
                    inputRow++;
                    s.Offset = new Point(s.Offset.X, headerH + 6 + inputRow * SocketSpacing);
                }
                else
                {
                    outputRow++;
                    s.Offset = new Point(s.Offset.X, headerH + 6 + outputRow * SocketSpacing);
                }
            }
            int maxRow = inputRow > outputRow ? inputRow : outputRow;
            return headerH + 6 + (maxRow + 1) * SocketSpacing;
        }

        private static void RecalculateNodeSize(Node node)
        {
            //  Same dynamic header band as RecalculateSocketOffsets.
            int headerH = HeaderHeightFor(node);
            int rows = node.Sockets.Count(s => s.Name != "Flow") + 1; // +1 for Flow row
            node.Size = new Size(node.Size.Width, headerH + 14 + rows * SocketSpacing);
        }
    }
}
