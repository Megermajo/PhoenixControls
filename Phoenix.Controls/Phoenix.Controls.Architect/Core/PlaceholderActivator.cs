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

        // Header band height is no longer a single stale constant.
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
        /// True when <paramref name="s"/> is a real user PAYLOAD socket — an
        /// activated arg / return bubble — as opposed to a FIXED template plumbing
        /// socket that must never participate in the arg / return sync group. The
        /// plumbing sockets are the exec <c>Flow</c> pins and the Event.Trigger's
        /// optional <c>EventName</c> name-override input; syncing either as a
        /// payload sprouts a spurious bubble on the paired node (an Executor growing
        /// a phantom <c>EventName</c> output on pairing was exactly that bug).
        /// Placeholders are excluded too — they are the not-yet-activated add-slots.
        /// </summary>
        private static bool IsPayloadSocket(Socket s)
            => s is not null && !s.IsPlaceholder && s.Name != "Flow" && s.Name != "EventName";

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
            if (node is null || placeholder is null) return; // guard null node/placeholder before IsManagedPlaceholder reads node.Title
            if (!IsManagedPlaceholder(node, placeholder)) return;

            // NO placeholder "polarity heal" here — deliberately. The pre-WinUI
            // canvas (Canvas.EventPair.cs) trusted the placeholder's seeded Type,
            // and so do we. A heal that inferred the "correct" side from the node's
            // other managed sockets is WRONG for Event.Trigger / Event.Executor,
            // which carry TWO independent channels at once: ARGUMENTS on one side
            // (Trigger.Input / Executor.Output, "+ variable") and RETURNS on the
            // other (Trigger.Output / Executor.Input, "+ return"). While one channel
            // is populated and the other still empty, the node's payload sockets are
            // transiently all-one-direction — a sibling-direction heal then flips the
            // still-empty channel's placeholder to the populated side, so a return
            // gets activated as a second arg (Var2) and the return channel is
            // destroyed (the 2026-07-02 "Balance Check" garbled-sockets bug). The
            // placeholder Type is set correctly at creation (CreateDynamicEventNode)
            // and re-derived from the template on load (MigrateNodes /
            // EnsureEventNodePlaceholders), so it is authoritative here. See
            // EventPairChannelIntegrityTests for the contract this preserves.
            bool isReturnSocket = (node.Title == "Event.Executor" && placeholder.Type == SocketType.Input)
                               || (node.Title == "Event.Trigger"  && placeholder.Type == SocketType.Output)
                               ||  node.Title == "Event.Return";
            bool isMacroEntry = node.Title == "Macro.Entry";
            bool isMacroExit  = node.Title == "Macro.Exit";
            bool isProcEntry  = node.Title == "Process.Entry";
            bool isProcExit   = node.Title == "Process.Exit";

            int groupCount = node.Sockets.Count(s =>
                IsPayloadSocket(s) && s.Type == placeholder.Type);

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

            // Activate added a socket and renamed an existing one — both
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

            // Revert removed the trailing placeholder and renamed the
            // activated socket back to placeholder shape — both structural mutations.
            graph.MarkStructuralChange();

            // Same paired-sync rationale as Activate — keep paired Event.*
            // nodes' socket lists in lockstep so a revert on the trigger
            // shrinks the executor side too.
            if (IsEventPairTitle(node.Title))
                SyncEventPair(graph, node);
        }

        /// <summary>
        /// Repair an Event.Trigger / Event.Executor / Event.Return node whose
        /// socket list was corrupted by an earlier defect (duplicate managed
        /// placeholders, placeholders on a side the node's role doesn't have,
        /// payload sockets on Event.Return's non-existent Output channel, a
        /// leaked <c>EventName</c> socket on a non-Trigger). Graphs saved while
        /// those bugs were live keep the corruption forever — the placeholder
        /// machinery only ever ADDS sockets on load
        /// (<see cref="NodeRegistry.EnsureEventNodePlaceholders"/>), it never
        /// removes, so a corrupted node stays broken: its wrong-side
        /// placeholders reject every wire-drop as same-side and its stacked
        /// full-row placeholder hit-bands swallow every press, which presents
        /// as "the node cannot be edited at all". Returns true when anything
        /// was removed.
        /// </summary>
        /// <remarks>
        /// Deliberately conservative: only UNWIRED sockets are ever removed —
        /// a wired socket, however wrong it looks, is user work and is left in
        /// place with a log line (no modal, per the canvas-guardrail rule).
        /// The constructive half stays in
        /// <see cref="NodeRegistry.EnsureEventNodePlaceholders"/>, which the
        /// load path (<c>GraphSerializer.MigrateNodes</c>) runs right after
        /// this so any placeholder this pass removed as a wrong-side duplicate
        /// is re-seeded on its correct side.
        /// </remarks>
        public static bool RepairEventNodeShape(Graph graph, Node node)
        {
            if (graph is null || node is null) return false;
            if (!IsEventPairTitle(node.Title)) return false;

            // The managed placeholder set each event role is allowed to carry.
            // Trigger: args grow on the Input side, returns on the Output side.
            // Executor: mirrored. Return: returns only, Input side — a Return
            // node has NO arg channel and NO Output sockets at all (its factory
            // seeds Flow-in + "+ return"-in and nothing else).
            var allowed = node.Title switch
            {
                "Event.Trigger"  => new[] { (SocketType.Input,  "+ variable"), (SocketType.Output, "+ return") },
                "Event.Executor" => new[] { (SocketType.Output, "+ variable"), (SocketType.Input,  "+ return") },
                _                => new[] { (SocketType.Input,  "+ return") },
            };

            bool IsWired(Socket s) => graph.Links is not null
                && graph.Links.Exists(l => l.FromSocketId == s.Id || l.ToSocketId == s.Id);

            var remove = new List<Socket>();
            var keepLastPerChannel = new Dictionary<(SocketType, string), Socket>();

            foreach (var s in node.Sockets)
            {
                if (s is null) continue;

                if (s.IsPlaceholder)
                {
                    // Only the managed "+ …" names are ours to judge; an unknown
                    // placeholder name is left alone.
                    string n = s.Name ?? string.Empty;
                    if (n is not ("+ variable" or "+ input" or "+ output" or "+ return")) continue;

                    bool isAllowed = Array.Exists(allowed, a => a.Item1 == s.Type && a.Item2 == n);
                    if (!isAllowed)
                    {
                        remove.Add(s);
                        continue;
                    }

                    // Duplicate managed placeholder on the same channel — keep the
                    // LAST (trailing position is where Activate appends), drop the rest.
                    var key = (s.Type, n);
                    if (keepLastPerChannel.TryGetValue(key, out var earlier))
                        remove.Add(earlier);
                    keepLastPerChannel[key] = s;
                    continue;
                }

                // Event.Return must not carry ANY Output socket — no flow-out, no
                // arg channel. Everything the old Return-as-Executor sync appended
                // there is junk.
                if (node.Title == "Event.Return" && s.Type == SocketType.Output)
                {
                    remove.Add(s);
                    continue;
                }

                // "EventName" is fixed plumbing that exists ONLY as an Input on
                // Event.Trigger; anywhere else it leaked in via an old sync bug.
                if (s.Name == "EventName"
                    && !(node.Title == "Event.Trigger" && s.Type == SocketType.Input))
                {
                    remove.Add(s);
                }
            }

            int removedCount = 0;
            foreach (var s in remove)
            {
                if (IsWired(s))
                {
                    GlobalLogger.Log(
                        $"Event-node repair: '{node.Title}' (EventName '{GetEventName(node)}') carries a wrong-side socket " +
                        $"'{s.Name}' ({s.Type}) that is WIRED — left in place. Re-wire it to the correct side and it will be cleaned up on next load.",
                        "PlaceholderActivator", LogLevel.System);
                    continue;
                }
                node.Sockets.Remove(s);
                removedCount++;
            }

            bool changed = removedCount > 0;
            if (changed)
            {
                GlobalLogger.Log(
                    $"Event-node repair: removed {removedCount} corrupted socket(s) from " +
                    $"'{node.Title}' (EventName '{GetEventName(node)}').",
                    "PlaceholderActivator", LogLevel.System);
                RecalculateSocketOffsets(node);
                RecalculateNodeSize(node);
                graph.MarkStructuralChange();
            }
            return changed;
        }

        private static string GetEventName(Node node)
            => node.Attributes is not null && node.Attributes.TryGetValue("EventName", out var ev) ? ev : "";

        /// <summary>
        /// One-shot heal called from <see cref="LogicCanvasViewModel.LoadGraph"/>
        /// — converges every EventName group in the graph onto its canonical
        /// per-channel socket shape. Pre-fix this pushed the shape of the FIRST
        /// node in <c>graph.Nodes</c> order onto its peers — an arbitrary
        /// authority, so a stale Executor that happened to serialize first
        /// silently renamed every peer back on each load ("bubble names are
        /// constantly resetted") and its excess-drop DELETED sockets the first
        /// node lacked. Now the group adopts the same deterministic,
        /// non-destructive canonical <see cref="AdoptEventShapeOnJoin"/> uses:
        /// per channel, the participant with the most sockets is the naming
        /// authority (ties prefer an <c>Event.Trigger</c> — the natural
        /// definer — over graph order), and because the canonical is the group
        /// MAXIMUM no node's channel is ever shrunk — a divergent group
        /// converges by union, never by dropping a peer's authored bubble.
        /// </summary>
        public static void SyncAllEventPairs(Graph graph)
        {
            if (graph is null) return;
            var byEventName = BuildEventNodeIndex(graph);
            foreach (var kv in byEventName)
            {
                if (kv.Value.Count < 2) continue; // nothing to converge
                AdoptCanonicalShapeForGroup(graph, kv.Key, kv.Value, joined: null);
            }
        }

        // Group event-pair nodes by EventName once (O(n)) so SyncEventPair's
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
            // Standalone call (e.g. wire-drop) — build a
            // one-shot EventName index then run the shared indexed core, so the
            // peer loop no longer rescans all of graph.Nodes. Behavior-identical.
            SyncEventPairIndexed(graph, source, BuildEventNodeIndex(graph));
        }

        // Socket-role mapping for an Event node. argType is the socket side that
        // carries the event ARGUMENTS (payload); retType the side carrying RETURN
        // values. Event.Trigger: args=Input, returns=Output. Event.Executor:
        // args=Output, returns=Input. Event.Return has ONLY Input sockets — they
        // are return values, so it has NO arg channel (argType null) and returns
        // on Input; it participates in the return contract but must NEVER be given
        // arg sockets (doing so appended spurious Output sockets to it, or wiped a
        // peer's args with a Return's empty arg set).
        private static (SocketType? argType, SocketType retType) EventSocketRoles(string? title) => title switch
        {
            "Event.Trigger"  => (SocketType.Input,  SocketType.Output),
            "Event.Executor" => (SocketType.Output, SocketType.Input),
            "Event.Return"   => ((SocketType?)null, SocketType.Input),
            _                => ((SocketType?)null, SocketType.Input),
        };

        // Sync the source's arg + return sockets onto ONE peer, by role. Each
        // channel runs only when its src type is non-null: the ARG channel needs
        // BOTH sides to carry one (Trigger↔Executor), and the cross-graph push
        // additionally nulls out a channel the source holds ZERO sockets in (see
        // SyncEventPairAcrossGraph). In-graph callers always pass a non-null RET
        // type (all three titles carry returns). Returns true if the peer changed.
        // Shared by the in-graph and cross-graph event-pair sync so they stay
        // identical.
        private static bool SyncPeerByRole(
            Graph graph, Node peer,
            SocketType? srcArgType, List<Socket> argSources,
            SocketType? srcRetType, List<Socket> retSources)
        {
            var (peerArgType, peerRetType) = EventSocketRoles(peer.Title);
            bool argChanged = srcArgType is not null && peerArgType is SocketType pat
                              && SyncSocketGroup(graph, peer, argSources, pat, NodeRegistry.ColString);
            bool retChanged = srcRetType is not null
                              && SyncSocketGroup(graph, peer, retSources, peerRetType, NodeRegistry.ColReturn);
            if (argChanged || retChanged)
            {
                RecalculateSocketOffsets(peer);
                RecalculateNodeSize(peer);
                return true;
            }
            return false;
        }

        private static void SyncEventPairIndexed(Graph graph, Node source, Dictionary<string, List<Node>> byEventName)
        {
            if (graph is null || source is null) return;
            if (!IsEventPairTitle(source.Title)) return;
            if (!source.Attributes.TryGetValue("EventName", out var eventName)) return;
            if (string.IsNullOrWhiteSpace(eventName)) return;

            var (srcArgType, srcRetType) = EventSocketRoles(source.Title);
            var argSources = srcArgType is SocketType sat
                ? source.Sockets.Where(s => IsPayloadSocket(s) && s.Type == sat).ToList()
                : new List<Socket>();
            var retSources = source.Sockets.Where(s => IsPayloadSocket(s) && s.Type == srcRetType).ToList();

            // Track whether any peer was actually mutated so we only
            // burn the cache when something changed. SyncSocketGroup adds/removes
            // sockets and prunes links, so any peer mutation is a structural change.
            bool anyMutation = false;

            // Peers come from the prebuilt index (all event-pair nodes sharing
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

                if (SyncPeerByRole(graph, node, srcArgType, argSources, srcRetType, retSources))
                    anyMutation = true;
            }

            if (anyMutation)
                graph.MarkStructuralChange();
        }

        /// <summary>
        /// Mirror the arg / return socket shape of <paramref name="source"/> (an
        /// Event.Trigger / Event.Executor / Event.Return node — typically a detached
        /// snapshot from ANOTHER file's graph) onto every EventName-matched Event
        /// node in <paramref name="peerGraph"/>. Returns true when any peer changed.
        /// <para>
        /// This is the ROLE-AWARE, CROSS-TITLE sync — the same mapping
        /// <see cref="SyncEventPairIndexed"/> does in-graph: a Trigger's arg sockets
        /// are Inputs while an Executor's are Outputs, so the sync matches by ROLE
        /// (arg↔arg, return↔return) across the title boundary, for peers of ANY
        /// event title that share the EventName. The prior cross-file sync only
        /// mirrored SAME-title nodes by raw socket type, so a Trigger in one file
        /// never propagated its bubbles to an Executor in another file — the core of
        /// "cross-file event bubble sync is broken". Used by the on-disk peer sync
        /// (<see cref="EventPairCrossFileSync"/>) and the live cross-window sync so
        /// an open Executor in another window mirrors a Trigger edit exactly as an
        /// in-graph pair would.
        /// </para>
        /// </summary>
        public static bool SyncEventPairAcrossGraph(Node source, Graph peerGraph)
        {
            if (source is null || peerGraph?.Nodes is null) return false;
            if (!IsEventPairTitle(source.Title)) return false;
            if (source.Attributes is null || !source.Attributes.TryGetValue("EventName", out var eventName)) return false;
            if (string.IsNullOrWhiteSpace(eventName)) return false;

            var (argTypeRole, retTypeRole) = EventSocketRoles(source.Title);
            var argSources = argTypeRole is SocketType sat
                ? source.Sockets.Where(s => IsPayloadSocket(s) && s.Type == sat).ToList()
                : new List<Socket>();
            var retSources = source.Sockets.Where(s => IsPayloadSocket(s) && s.Type == retTypeRole).ToList();

            // EMPTY-CHANNEL GUARD (cross-graph ONLY): a channel the source holds
            // zero sockets in is not pushed at all. The source of this sync is a
            // node from ANOTHER file/window whose shape was supposed to be
            // pre-converged by adopt-on-join — but the adopt's sibling scan and
            // this push resolve peers independently, so when the adopt missed the
            // definer (unreadable/locked file, historic directory mismatch) the
            // source arrives EMPTY and SyncSocketGroup's excess-drop would delete
            // every authored bubble AND its wires from the peer graph, which the
            // disk sync then persists. Skipping the empty channel is the
            // non-destructive default; the union heal reconverges the group later.
            // Deliberate trade: removing the LAST socket of a channel no longer
            // propagates cross-file (peer keeps a stale bubble); a shrink to a
            // still-non-empty channel (2 → 1) propagates as before. In-graph sync
            // (SyncEventPairIndexed) is NOT guarded — there adopt-on-join runs
            // synchronously on the same graph object first, so an empty joiner
            // can't reach it, and intentional last-socket removals must still
            // reach in-graph peers.
            SocketType? srcArgType = argSources.Count == 0 ? null : argTypeRole;
            SocketType? srcRetType = retSources.Count == 0 ? null : retTypeRole;

            bool anyMutation = false;
            foreach (var node in peerGraph.Nodes)
            {
                if (node is null || ReferenceEquals(node, source)) continue;
                if (!IsEventPairTitle(node.Title)) continue;
                if (node.Attributes is null || !node.Attributes.TryGetValue("EventName", out var otherName)) continue;
                if (!otherName.Equals(eventName, StringComparison.OrdinalIgnoreCase)) continue;

                if (SyncPeerByRole(peerGraph, node, srcArgType, argSources, srcRetType, retSources))
                    anyMutation = true;
            }

            if (anyMutation) peerGraph.MarkStructuralChange();
            return anyMutation;
        }

        // A node's ARG-channel sockets, by its own role (Trigger=Input, Executor=Output,
        // Return=none). Empty when the title carries no arg channel.
        private static List<Socket> ArgSocketsOf(Node n)
        {
            var (argType, _) = EventSocketRoles(n.Title);
            return argType is SocketType at
                ? n.Sockets.Where(s => IsPayloadSocket(s) && s.Type == at).ToList()
                : new List<Socket>();
        }

        // A node's RETURN-channel sockets, by its own role (Trigger=Output,
        // Executor/Return=Input).
        private static List<Socket> RetSocketsOf(Node n)
        {
            var (_, retType) = EventSocketRoles(n.Title);
            return n.Sockets.Where(s => IsPayloadSocket(s) && s.Type == retType).ToList();
        }

        // Tie-break among equal-count channel authorities: prefer an EXISTING peer over
        // the newcomer, then an Event.Trigger (the natural definer). Non-destructive
        // either way (see AdoptEventShapeOnJoin) — this only decides whose NAMES win.
        private static bool BetterAuthority(Node cand, Node cur, Node joined)
        {
            bool candNotJoined = !ReferenceEquals(cand, joined);
            bool curNotJoined  = !ReferenceEquals(cur,  joined);
            if (candNotJoined != curNotJoined) return candNotJoined;
            return cur.Title != "Event.Trigger" && cand.Title == "Event.Trigger";
        }

        /// <summary>
        /// When <paramref name="joined"/> (re)joins an event by EventName, make the whole
        /// in-graph group ADOPT the event's canonical socket shape instead of letting the
        /// just-assigned node push its own (often empty) shape onto the group. This is the
        /// fix for "assign a new executor to an existing event RESETS everything": the
        /// canvas used to call <see cref="SyncEventPair"/> with the empty joiner as the
        /// source, deleting every defined peer's bubbles (in-graph AND on disk).
        /// <para/>
        /// The canonical shape is computed PER CHANNEL, independently: the ARGUMENT list
        /// comes from the participant with the most arg sockets, and the RETURN list from
        /// the participant with the most return sockets (participants = in-graph peers +
        /// <paramref name="crossFilePeers"/> snapshots + <paramref name="joined"/>). Ties
        /// prefer an existing peer over the newcomer, then an <c>Event.Trigger</c>. Because
        /// each channel's canonical is the MAXIMUM across the group, applying it never
        /// shrinks any node's channel below what it already had — so no authored bubble or
        /// wire is ever dropped; nodes only grow or get renamed to the canonical names.
        /// (Total-count selection was insufficient: an equal-total peer with a different
        /// arg/return split could wipe a whole channel.) The canonical is applied to every
        /// in-graph event node sharing the EventName, incl. <paramref name="joined"/>,
        /// role-aware and cross-title. Returns true when the graph changed. Pure model
        /// mutation — <paramref name="crossFilePeers"/> are read-only snapshots the caller
        /// loads from sibling files; no disk IO here (keeps this unit-testable).
        /// </summary>
        public static bool AdoptEventShapeOnJoin(Graph graph, Node joined, IReadOnlyList<Node>? crossFilePeers = null)
        {
            if (graph is null || joined is null) return false;
            if (!IsEventPairTitle(joined.Title)) return false;
            if (joined.Attributes is null
                || !joined.Attributes.TryGetValue("EventName", out var ev)
                || string.IsNullOrWhiteSpace(ev))
                return false;

            bool SharesEvent(Node n)
                => n is not null && IsEventPairTitle(n.Title)
                   && n.Attributes is not null
                   && n.Attributes.TryGetValue("EventName", out var e2)
                   && e2.Equals(ev, StringComparison.OrdinalIgnoreCase);

            // Participants sharing this EventName: in-graph nodes (incl. joined) + cross-file snapshots.
            var participants = new List<Node>();
            foreach (var n in graph.Nodes)
                if (SharesEvent(n)) participants.Add(n);
            if (crossFilePeers is not null)
                foreach (var n in crossFilePeers)
                    if (!ReferenceEquals(n, joined) && SharesEvent(n)) participants.Add(n);
            // Need at least one participant OTHER than joined to have anything to adopt from.
            if (!participants.Any(p => !ReferenceEquals(p, joined))) return false;

            return AdoptCanonicalShapeForGroup(graph, ev, participants, joined);
        }

        /// <summary>
        /// Shared core of the canonical-shape convergence: compute the
        /// per-channel authorities across <paramref name="participants"/>
        /// (in-graph members and, on the join path, cross-file snapshots) and
        /// apply the resulting (arg, return) canonical onto every IN-GRAPH
        /// event node sharing <paramref name="ev"/>. Each channel's canonical
        /// is the group MAXIMUM, so no node's channel is ever shrunk —
        /// SyncSocketGroup only renames and appends here. Ties prefer a node
        /// other than <paramref name="joined"/> (when given), then an
        /// <c>Event.Trigger</c>; with <paramref name="joined"/> null (the
        /// load-time heal) the Trigger preference alone decides, keeping the
        /// outcome independent of serialization order. Returns true when the
        /// graph changed.
        /// </summary>
        private static bool AdoptCanonicalShapeForGroup(
            Graph graph, string ev, List<Node> participants, Node? joined)
        {
            bool SharesEvent(Node n)
                => n is not null && IsEventPairTitle(n.Title)
                   && n.Attributes is not null
                   && n.Attributes.TryGetValue("EventName", out var e2)
                   && e2.Equals(ev, StringComparison.OrdinalIgnoreCase);

            // Per-channel authorities = participant with the most sockets in that channel.
            Node? argAuth = null; int argMax = -1;
            Node? retAuth = null; int retMax = -1;
            foreach (var p in participants)
            {
                int ac = ArgSocketsOf(p).Count;
                if (ac > argMax || (ac == argMax && argAuth is not null && BetterAuthority(p, argAuth, joined)))
                { argMax = ac; argAuth = p; }
                int rc = RetSocketsOf(p).Count;
                if (rc > retMax || (rc == retMax && retAuth is not null && BetterAuthority(p, retAuth, joined)))
                { retMax = rc; retAuth = p; }
            }

            var argCanon = argAuth is not null ? ArgSocketsOf(argAuth) : new List<Socket>();
            var retCanon = retAuth is not null ? RetSocketsOf(retAuth) : new List<Socket>();

            // Apply (argCanon, retCanon) onto every in-graph event node sharing the
            // EventName, incl. joined. argCanon/retCanon are each the group MAX, so no
            // peer is ever shrunk — SyncSocketGroup only renames + appends here.
            bool hasArgChannel = argMax > 0;
            bool anyMutation = false;
            foreach (var n in graph.Nodes)
            {
                if (!SharesEvent(n)) continue;
                if (SyncPeerByRole(graph, n,
                        hasArgChannel ? (SocketType?)SocketType.Input : null, argCanon,
                        SocketType.Input, retCanon))
                    anyMutation = true;
            }
            if (anyMutation) graph.MarkStructuralChange();
            return anyMutation;
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
            // Was O(n^2) — an inner graph.Nodes.Any() peer
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
                if (node is null || node.Attributes is null) continue; // guard null node/Attributes before deref
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

        private static bool SyncSocketGroup(
            Graph              graph,
            Node               peer,
            List<Socket>       sources,
            SocketType         targetType,
            Color              fallbackColor)
        {
            bool changed = false;
            var targets = peer.Sockets
                .Where(s => IsPayloadSocket(s) && s.Type == targetType)
                .ToList();

            // Sync existing sockets in order (name + color + datatype). Compare
            // before assigning so a pure rename / retype is reported as a change
            // (Color via ToArgb so a named-vs-FromArgb round-trip doesn't read as
            // a spurious diff) — the cross-window live sync needs to know a retype
            // happened even though it leaves the socket COUNT unchanged.
            int common = Math.Min(sources.Count, targets.Count);
            for (int i = 0; i < common; i++)
            {
                if (targets[i].Name != sources[i].Name
                    || targets[i].Color.ToArgb() != sources[i].Color.ToArgb()
                    || targets[i].DataType != sources[i].DataType)
                {
                    targets[i].Name     = sources[i].Name;
                    targets[i].Color    = sources[i].Color;
                    targets[i].DataType = sources[i].DataType;
                    changed = true;
                }
            }

            // Drop excess targets (sources shrank). Wires touching them go
            // too — the peer's sockets really are gone.
            for (int i = sources.Count; i < targets.Count; i++)
            {
                var excess = targets[i];
                graph.Links.RemoveAll(l => l.FromSocketId == excess.Id || l.ToSocketId == excess.Id);
                peer.Sockets.Remove(excess);
                changed = true;
            }

            // Append new sockets that exist on source but not yet on peer.
            // Insert above the trailing placeholder so the placeholder stays
            // last; if no placeholder is present, append at the end. The
            // surviving targets after the excess-drop are exactly the first
            // `common` entries, so a counter stands in for re-filtering
            // peer.Sockets; the insert index is resolved once and advanced per
            // insert — only non-placeholders are inserted, so which placeholder
            // comes first never changes mid-loop.
            int peerWidth = peer.Size.Width > 0 ? peer.Size.Width : NodeWidth;
            bool isInput  = targetType == SocketType.Input;
            var trailingPlaceholder = peer.Sockets.FirstOrDefault(s => s.IsPlaceholder && s.Type == targetType);
            int insertAt = trailingPlaceholder is not null
                ? peer.Sockets.IndexOf(trailingPlaceholder)
                : peer.Sockets.Count;
            for (int i = common; i < sources.Count; i++)
            {
                var src = sources[i];
                var newSock = new Socket
                {
                    Id       = Guid.NewGuid().ToString(),
                    Name     = src.Name,
                    Type     = targetType,
                    Color    = src.Color,
                    DataType = src.DataType,
                    Offset   = new Point(isInput ? -6 : peerWidth - 14, 0),
                };
                peer.Sockets.Insert(insertAt++, newSock);
                _ = fallbackColor; // reserved for future "no source colour" branch — currently src.Color always wins
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Walk all non-Flow sockets on <paramref name="node"/> and
        /// re-stripe their Y offsets in declaration order so the visual
        /// layout matches the underlying socket list. Returns the Y
        /// offset for the next socket appended below the existing rows.
        /// </summary>
        /// <remarks>
        /// Exposed as public (was private) so
        /// <see cref="NodeRegistry.EnsureEventNodePlaceholders"/> can stripe
        /// the freshly-added placeholder socket's Y offset on the recovery
        /// path. Without this the recovery sockets render at (0,0) for a
        /// single frame on graph load until the canvas's next layout pass.
        /// </remarks>
        public static int RecalculateSocketOffsets(Node node)
        {
            // Use the node's dynamic header band (26 plain / 40 with a
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
            // Same dynamic header band as RecalculateSocketOffsets.
            int headerH = HeaderHeightFor(node);
            int rows = node.Sockets.Count(s => s.Name != "Flow") + 1; // +1 for Flow row
            node.Size = new Size(node.Size.Width, headerH + 14 + rows * SocketSpacing);
        }
    }
}
