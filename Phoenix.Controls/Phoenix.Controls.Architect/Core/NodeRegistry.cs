using System;
using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.Core
{
    public class NodeTemplate
    {
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public Color HeaderColor { get; set; }
        public string Description { get; set; } = "";
        public List<(string Name, Color Color)> Inputs  { get; set; } = new();
        public List<(string Name, Color Color)> Outputs { get; set; } = new();
        public Dictionary<string, string> DefaultProperties { get; set; } = new();

        // Per-socket descriptions, keyed by socket name. Out-of-band so the dense
        // AddTemplate signature doesn't grow another optional parameter for ~140
        // existing call sites; backfilled via NodeRegistry.SetSocketDescriptions
        // after the AddTemplate call. Used by:
        //   * CreateNode → Socket.Description (per-instance copy so a
        //     canvas-side rename / future authoring tool can override per-graph),
        //   * NodeDocumentationForm — sub-text under each socket row,
        //   * Canvas hover — tooltip when the cursor lands on a socket pin.
        public Dictionary<string, string> SocketDescriptions { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // L43 — UE-Blueprints-style cosmetic title override. Title remains the
        // canonical registration key (used by the exporter, ScriptManager command
        // routing, the .phxg JSON, every test that pins a node by name, etc.) —
        // DisplayName is a render-only hint for the canvas chrome / palette so
        // Math.Add can show as "Add +" without breaking any persisted graph.
        // Defaults to Title when AddTemplate doesn't supply an override, which
        // keeps every existing template node-name-rendered exactly as before.
        public string DisplayName { get; set; } = string.Empty;

        // P1 #1 — Optional compact-display glyph (UE-Blueprints "compact node" idiom).
        // Templates that set this opt in to a single-character / short-symbol render
        // mode the user can toggle via the canvas right-click menu ("Convert to
        // Compact Node" / "Convert to Full Node"). Empty string = no compact mode
        // available for this template.
        public string CompactSymbol { get; set; } = string.Empty;

        // 0.10.0 UX P2 — Optional per-category Segoe Fluent Icons codepoint
        // rendered as a 16×16 FontIcon left of the node header title. Most
        // templates leave this empty; a small set of high-traffic templates
        // (DB.*, HTTP.*, Twitch.*, etc.) opt in for at-a-glance category
        // recognition on the canvas. Empty = no icon (existing rendering
        // path).
        public string IconGlyph { get; set; } = string.Empty;

        // P3 — Token-aware fuzzy search alias list. Populated for nodes whose canonical
        // title doesn't match the word users would think of: "if" finds Logic.If, "loop"
        // finds Flow.ForEach / Flow.ForLoop, "wait" finds Async.Delay + Async.WaitForEvent.
        // Token matcher in Canvas's spawn-search treats every entry as an
        // additional token to prefix-match against the query.
        public List<string> Keywords { get; set; } = new();

        // Names of input sockets that must be wired for the node to be valid.
        // SocketViewModel.IsRequired reads this; the canvas paints a yellow halo
        // on any input listed here that has no incoming link (TODO Architect UX
        // P1 — "Required-but-empty input markers"). Opt-in: empty set means no
        // socket on this template is marked required.
        public HashSet<string> RequiredInputs { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Registration-time lookup caches over Inputs/Outputs. The socket lists
        // are only populated inside AddTemplate and templates are immutable
        // post-RegisterDefaults, so these are computed once there instead of per
        // node on every graph load: MigrateNodes reads the name sets for the
        // stale-socket sweep and ReorderSocketsToTemplate reads the
        // first-occurrence index maps for template-order comparisons.
        public IReadOnlySet<string> InputNames  { get; private set; } =
            new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlySet<string> OutputNames { get; private set; } =
            new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, int> InputOrder  { get; private set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, int> OutputOrder { get; private set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Rebuild the derived name/order caches from the current Inputs/Outputs.
        /// Called by AddTemplate after the socket lists are populated — must be
        /// re-invoked if a template's socket lists are ever mutated later (none
        /// are today; templates freeze after RegisterDefaults).
        /// </summary>
        internal void RebuildSocketLookups()
        {
            (InputNames,  InputOrder)  = BuildSocketLookups(Inputs);
            (OutputNames, OutputOrder) = BuildSocketLookups(Outputs);
        }

        private static (IReadOnlySet<string> Names, IReadOnlyDictionary<string, int> Order)
            BuildSocketLookups(List<(string Name, Color Color)> sockets)
        {
            var names = new HashSet<string>(sockets.Count, StringComparer.Ordinal);
            var order = new Dictionary<string, int>(sockets.Count, StringComparer.Ordinal);
            for (int i = 0; i < sockets.Count; i++)
            {
                string name = sockets[i].Name;
                names.Add(name);
                // First occurrence wins — matches the reorder comparer's
                // duplicate-name handling in ReorderSocketsToTemplate.
                if (!order.ContainsKey(name)) order[name] = i;
            }
            return (names, order);
        }
    }

    public static partial class NodeRegistry
    {
        private static readonly Dictionary<string, NodeTemplate> _templates = new();

        // Color palette for socket data types
        public static readonly Color ColExec    = Color.White;
        public static readonly Color ColString  = Color.FromArgb(255, 220, 100);
        public static readonly Color ColNumber  = Color.FromArgb(130, 200, 255);
        public static readonly Color ColFloat   = Color.FromArgb(100, 212, 200);
        public static readonly Color ColBool    = Color.FromArgb(140, 220, 140);
        public static readonly Color ColObject  = Color.FromArgb(220, 160, 255);
        public static readonly Color ColReturn  = Color.FromArgb(255, 165, 0);
        public static readonly Color ColList    = Color.FromArgb(255, 170, 100);

        // Precomputed ARGB forms of the palette constants above (declared after
        // them so static-field init order stays valid). DataTypeFromColor runs
        // per socket on every graph load/migration; Color.ToArgb() re-derives
        // the value from the struct's name/state flags each call, so the six
        // constant conversions are hoisted out of the per-call path.
        private static readonly int _argbExec   = ColExec.ToArgb();
        private static readonly int _argbString = ColString.ToArgb();
        private static readonly int _argbNumber = ColNumber.ToArgb();
        private static readonly int _argbFloat  = ColFloat.ToArgb();
        private static readonly int _argbBool   = ColBool.ToArgb();
        private static readonly int _argbList   = ColList.ToArgb();

        static NodeRegistry()
        {
            RegisterDefaults();
        }

        public static SocketDataType DataTypeFromColorPublic(Color c) => DataTypeFromColor(c);

        private static SocketDataType DataTypeFromColor(Color c)
        {
            // Compare via ARGB int rather than Color equality: Color.White (named)
            // is not == to a Color.FromArgb(255,255,255,255) rehydrated by the
            // JSON converter, even though both have identical ARGB values.
            // ToArgb-based compare makes ColExec (named) and the FromArgb
            // constants behave uniformly across save/load round-trips.
            int argb = c.ToArgb();
            if (argb == _argbExec)   return SocketDataType.Flow;
            if (argb == _argbString) return SocketDataType.String;
            if (argb == _argbNumber) return SocketDataType.Int;
            if (argb == _argbFloat)  return SocketDataType.Float;
            if (argb == _argbBool)   return SocketDataType.Bool;
            if (argb == _argbList)   return SocketDataType.Collection;
            return SocketDataType.Any;
        }

        public static Color ColorFromDataType(SocketDataType dt) => dt switch
        {
            SocketDataType.Flow       => ColExec,
            SocketDataType.String     => ColString,
            SocketDataType.Int        => ColNumber,
            SocketDataType.Float      => ColFloat,
            SocketDataType.Bool       => ColBool,
            SocketDataType.Collection => ColList,
            _                         => ColObject
        };

        public static Color ColumnTypeToSocketColor(string sqlType) => sqlType switch
        {
            "INTEGER" or "REAL" => ColNumber,
            "BOOLEAN"           => ColBool,
            _                   => ColString
        };

        public static void ApplyColumnTypeToNode(Node node, Color valueColor)
        {
            SocketDataType dt = DataTypeFromColor(valueColor);
            // Per-node socket descriptor: which socket carries the column value
            // and which direction it has. DB.FetchRow surfaces the row as an
            // OUTPUT named "Row"; the others read or write a "Value" socket.
            (string socketName, SocketType direction) = node.Title switch
            {
                "DB.GetCell"   => ("Value", SocketType.Output),
                "DB.FetchRow"  => ("Row",   SocketType.Output),
                "DB.SetCell"   => ("Value", SocketType.Input),
                "DB.FindRow"   => ("Value", SocketType.Input),
                "DB.InsertRow" => ("Value", SocketType.Input),
                _              => ("Value", SocketType.Input),
            };
            var s = node.Sockets.Find(s => s.Name == socketName && s.Type == direction);
            if (s == null) return;
            s.Color    = valueColor;
            s.DataType = dt;
        }

        // Wildcard groups: sockets in the same group on a node mirror each other's type when connected.
        // L12 — single-element groups are dead (mirroring requires ≥2 members), so the Var.Get / Var.Set
        // entries were removed; their lone "Value" socket has nothing to mirror against.
        private static readonly Dictionary<string, string[][]> _wildcardGroups = new()
        {
            { "Logic.If",        new[] { new[] { "A", "B" } } },
            { "Logic.Equals",    new[] { new[] { "A", "B" } } },
            { "Logic.NotEquals", new[] { new[] { "A", "B" } } },
            { "Logic.Select",    new[] { new[] { "A", "B", "Value" } } },
            { "Flow.Select",     new[] { new[] { "A", "B", "C", "D", "Value" } } },
            { "Flow.Reroute",    new[] { new[] { "In", "Out" } } },
        };

        // R13 — single source of truth for comparison-operator symbols.
        // Maps the comparison-style node title (and the Logic.If "Operator" attribute value
        // produced from it) to its emitted operator symbol. Both Logic.If's default Operator
        // and the description's "Supported operators" list are derived from this table so the
        // symbols only live in one place.
        public static readonly IReadOnlyDictionary<string, string> ComparisonOperators =
            new Dictionary<string, string>
            {
                { "Logic.Equals",       "==" },
                { "Logic.NotEquals",    "!=" },
                { "Logic.GreaterThan",  ">"  },
                { "Logic.LessThan",     "<"  },
                { "Logic.GreaterEqual", ">=" },
                { "Logic.LessEqual",    "<=" },
            };

        public static string[]? GetWildcardGroup(string nodeTitle, string socketName)
        {
            if (!_wildcardGroups.TryGetValue(nodeTitle, out var groups)) return null;
            foreach (var group in groups)
                if (Array.IndexOf(group, socketName) >= 0) return group;
            return null;
        }

        /// <summary>
        /// L32 — Live-instance-aware overload. The static <see cref="GetWildcardGroup(string,string)"/>
        /// returns every member declared in the static template table, regardless of which
        /// sockets the runtime node actually carries. That's correct for callers building
        /// templates (no node yet), but wrong for live-instance callers — e.g. a wire-drop
        /// cascade on a Logic.Switch node whose socket list has been pruned by a graph
        /// migration. This overload filters the returned group to only the sockets that
        /// exist on <paramref name="liveNode"/>, so callers don't try to mirror a type onto
        /// a missing socket.
        ///
        /// Returns null when the title has no wildcard group, when <paramref name="socketName"/>
        /// isn't part of any group on the title, or when the filter leaves fewer than two
        /// members (single-element groups are dead — mirroring requires ≥2 sockets).
        /// </summary>
        public static string[]? GetWildcardGroup(Node liveNode, string socketName)
        {
            if (liveNode == null) return null;
            var staticGroup = GetWildcardGroup(liveNode.Title, socketName);
            if (staticGroup == null) return null;

            // Build a name set from the live instance's actual sockets.
            var liveNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in liveNode.Sockets) liveNames.Add(s.Name);

            // Filter the declared group to the subset that actually exists on the node.
            var filtered = new List<string>(staticGroup.Length);
            foreach (var n in staticGroup)
                if (liveNames.Contains(n)) filtered.Add(n);

            // The queried socket must be present (otherwise the caller is asking about a
            // socket that doesn't exist on the instance), and the filtered group must have
            // at least two members for mirroring to be meaningful.
            if (filtered.Count < 2) return null;
            if (!filtered.Contains(socketName)) return null;
            return filtered.ToArray();
        }

        /// <summary>
        /// Sweep every wired socket in <paramref name="graph"/> and, for any socket that belongs
        /// to a wildcard group, propagate the resolved (non-Any) type across the group on its
        /// owning node. Idempotent; safe to call multiple times. Mirrors what the canvas does
        /// on wire-drop, but for graphs loaded from disk where wire-drop never fired.
        ///
        /// PERF (perf/architect-blockers, BlockerG): pre-cache this used
        /// graph.Nodes.Find(predicate) for both endpoints of every link and
        /// node.Sockets.Find(predicate) for both sockets — O(L × N × S). On
        /// a 500-node / 800-link graph that's ~half a million predicate
        /// evaluations on the UI thread before the canvas paints. Switched
        /// to graph.FindNodeById / graph.FindSocketById (both O(1) with
        /// self-healing dictionary caches) so total cost is now O(L) lookups.
        ///
        /// Fixed-point iteration — the original single-pass walk was link-order
        /// dependent: `Reroute → Reroute → Logic.If` only fully propagated if `graph.Links`
        /// iterated head-first. With more than one hop in a wildcard chain the type wouldn't
        /// carry across the chain on the first pass if the downstream link was visited before
        /// its upstream feeder. The loop re-runs until no socket data-type changes — bounded
        /// by node count so a degenerate cycle still terminates.
        /// </summary>
        public static void ResolveWildcardCascade(Graph graph)
        {
            if (graph == null) return;

            // Hard ceiling so a pathological cycle can't spin forever. One iteration per
            // node is the worst case for a linear chain — beyond that we've converged or
            // hit a cycle that won't resolve further. Cheap because most graphs converge
            // in 1-2 passes.
            int maxIters = Math.Max(2, graph.Nodes.Count + 1);

            for (int iter = 0; iter < maxIters; iter++)
            {
                bool changed = false;
                foreach (var link in graph.Links)
                {
                    var fromNode = graph.FindNodeById(link.FromNodeId);
                    var toNode   = graph.FindNodeById(link.ToNodeId);
                    var fromSock = graph.FindSocketById(link.FromSocketId);
                    var toSock   = graph.FindSocketById(link.ToSocketId);
                    if (fromNode == null || toNode == null || fromSock == null || toSock == null)
                        continue;

                    // Only propagate across a link whose
                    // endpoints are type-compatible. A hand-edited / legacy .phxg
                    // can carry a syntactically-valid but type-incompatible link
                    // (e.g. an Int output wired into a String wildcard input); the
                    // load-time dangling-link sweep only checks socket EXISTENCE,
                    // not type, so such a link survives. Without this guard the
                    // cascade would force the wildcard group to the wrong type and
                    // paint the node accordingly. AreCompatible returns true when
                    // either side is Any (the normal one-end-resolved wildcard
                    // case we DO want to propagate) and for Int↔Float widening;
                    // it returns false only for genuinely incompatible resolved
                    // pairs, which we skip. LinkViewModel.RecomputePath still flags
                    // the bad link IsInvalid (dashed red) so the user sees it.
                    if (!AreCompatible(fromSock.DataType, toSock.DataType)) continue;

                    var resolved = fromSock.DataType != SocketDataType.Any
                        ? fromSock.DataType
                        : toSock.DataType;
                    if (resolved == SocketDataType.Any) continue;

                    // Record pre-state on the wildcard members so we can detect propagation.
                    if (ApplyWildcardOnNodeChanged(fromNode, fromSock.Name, resolved)) changed = true;
                    if (ApplyWildcardOnNodeChanged(toNode,   toSock.Name,   resolved)) changed = true;
                }
                if (!changed) break;
            }
        }

        // Variant of <see cref="ApplyWildcardOnNode"/> that returns whether any member
        // socket's DataType actually changed — drives the fixed-point loop in
        // <see cref="ResolveWildcardCascade"/>.
        private static bool ApplyWildcardOnNodeChanged(Node node, string socketName, SocketDataType resolved)
        {
            var group = GetWildcardGroup(node.Title, socketName);
            if (group == null) return false;
            var col = ColorFromDataType(resolved);
            bool any = false;
            foreach (var name in group)
            {
                var s = node.Sockets.Find(x => x.Name == name);
                if (s == null) continue;
                if (s.DataType != resolved) any = true;
                s.DataType = resolved;
                s.Color    = col;
            }
            return any;
        }

        private static void ApplyWildcardOnNode(Node node, string socketName, SocketDataType resolved)
        {
            var group = GetWildcardGroup(node.Title, socketName);
            if (group == null) return;
            var col = ColorFromDataType(resolved);
            foreach (var name in group)
            {
                var s = node.Sockets.Find(x => x.Name == name);
                if (s == null) continue;
                s.DataType = resolved;
                s.Color    = col;
            }
        }

        public static bool AreCompatible(SocketDataType a, SocketDataType b)
        {
            // Flow-vs-data guard MUST run before the Any short-circuit.
            // Flow is structurally distinct from every data type — it carries no
            // payload, only exec sequencing. If either side is Flow, both sides
            // must be Flow; otherwise the canvas wire-highlighter (which calls
            // this) would let an `Any`-typed wildcard pin visually accept a Flow
            // pin, producing a drop that the runtime cannot honour.
            bool aFlow = a == SocketDataType.Flow;
            bool bFlow = b == SocketDataType.Flow;
            if (aFlow != bFlow) return false;
            if (aFlow && bFlow) return true;

            if (a == SocketDataType.Any || b == SocketDataType.Any) return true;
            if (a == b) return true;
            // Numeric widening
            if ((a == SocketDataType.Int   && b == SocketDataType.Float) ||
                (a == SocketDataType.Float && b == SocketDataType.Int))  return true;
            return false;
        }


        public static IEnumerable<NodeTemplate> GetAllTemplates() => _templates.Values;

        // 0.13.9 — nodes whose live path doesn't exist yet, hidden from the
        // spawn palette / search / node-reference (but kept registered so existing
        // graphs still load + export, and tests still cover them). These are
        // exactly the Twitch actions with NO corresponding Streamer.bot
        // sub-action / Phoenix action-pack entry: SB can't create a poll the way
        // the node assumes; resolving a prediction needs ids DoAction can't return;
        // reward/redemption nodes need a reward pre-defined; Delete Message /
        // Sub-Only Mode / Whisper have no usable native sub-action (or need a
        // phone-verified bot). The OBS transform nodes (SetSourcePosition /
        // Scale / Rotation) are no longer listed — they dispatch for real via
        // Hub's direct OBS WebSocket (SB action relay as fallback). Un-hide a
        // title here the moment its action path lands. See PhoenixActionPack.md.
        public static readonly HashSet<string> HiddenFromPalette = new(StringComparer.Ordinal)
        {
            "Twitch.CreatePoll",
            "Twitch.ResolvePrediction",
            "Twitch.UpdateRewardCost",
            "Twitch.SetRewardEnabled",
            "Twitch.FulfillRedemption",
            "Twitch.RejectRedemption",
            "Twitch.DeleteMessage",
            "Twitch.Whisper",
            "Twitch.SubOnlyMode",
            // 2026-06-24 — the AI band (LLM / vision / image-gen nodes) is
            // neither tested nor fully functional yet, so it is hidden from
            // the spawn palette / search / node-reference and deferred as a
            // TODO. The templates stay REGISTERED (existing graphs still load
            // + export, and the manifest↔exporter↔Hub contract tests stay
            // green); the AI Settings fields are commented out in parallel so
            // no key can be entered. Un-hide these six titles — and uncomment
            // the Settings AI rows — the moment the AI runtime is verified.
            "AI.Prompt",
            "AI.Moderate",
            "AI.GenerateImage",
            "AI.VisionDescribe",
            "AI.WithTools",
            "AI.StreamText",
            // Live-processes redesign — the old fire-and-forget spawn nodes are
            // superseded by Process.Start / Process.Stop. Kept REGISTERED (legacy
            // graphs load + export, coverage tests stay green) but hidden from the
            // palette/search; ProcessNodeMigration upgrades placed nodes on load.
            "Process.Spawn",
            "Process.Terminate",
        };

        // Templates offered in the spawn palette / search / node-reference —
        // GetAllTemplates minus HiddenFromPalette. Graph load, export, validation,
        // and the coverage/description tests deliberately keep using
        // GetAllTemplates so hidden nodes stay fully functional if already placed.
        public static IEnumerable<NodeTemplate> GetPaletteTemplates()
            => _templates.Values.Where(t => !HiddenFromPalette.Contains(t.Title));

        public static NodeTemplate? GetTemplate(string title)
            => _templates.TryGetValue(title, out var t) ? t : null;

        public static Node? CreateNode(string title, Point location)
        {
            if (!_templates.TryGetValue(title, out var t)) return null;

            // Reroute node: small fixed size diamond
            if (title == "Flow.Reroute")
            {
                var rr = new Node
                {
                    Title = title, Category = t.Category,
                    HeaderColor = t.HeaderColor,
                    Location = location, Size = new Size(20, 20),
                    Attributes = new Dictionary<string, string>(t.DefaultProperties)
                };
                rr.Sockets.Add(new Socket { Name = "In",  Type = SocketType.Input,  Color = ColExec, Offset = new Point(-8, 4) });
                rr.Sockets.Add(new Socket { Name = "Out", Type = SocketType.Output, Color = ColExec, Offset = new Point(20, 4) });
                return rr;
            }

            // Macro.Entry / Macro.Exit / Process.Entry: dynamic placeholder
            // sockets (var-in / var-out surface). Process.Entry keeps this — its
            // "+ input" placeholder is how a user declares start params.
            //
            // Process.Exit is NOT here: in the live-process model it is the
            // "on stop" trigger (a fixed Flow-input + "On Stop" exec OUTPUT, per
            // its template) rather than a var-out surface — processes don't return
            // values — so it falls through to the generic template-based factory.
            if (title == "Macro.Entry" || title == "Macro.Exit"
             || title == "Process.Entry")
            {
                return CreateMacroEntryExitNode(title, t, location);
            }

            // Process.Spawn: basic Flow in + Done/InstanceId out; Architect canvas
            // rebuilds sockets from the linked Process's Entry/Exit shape
            // (RefreshProcessSpawnSockets, parallel to RefreshMacroCallSockets).
            if (title == "Process.Spawn")
            {
                int sw = 200, sh = 24, ss = 22;
                var sp = new Node
                {
                    Title       = title,
                    Category    = t.Category,
                    HeaderColor = t.HeaderColor,
                    Location    = location,
                    Size        = new Size(sw, sh + 14 + 2 * ss),
                    Attributes  = new Dictionary<string, string>(t.DefaultProperties)
                };
                // DataType set from colour at creation (see CreateVisualTriggerNode's
                // [visual-trigger-pins] note): Socket.DataType defaults to Any, and the
                // canvas derives the pin SHAPE + wire compatibility from DataType — so a
                // freshly-dropped Spawn whose Flow/Done pins were left at Any rendered as
                // ◆ Diamonds and refused to wire to normal ▶ Flow pins until save+reload.
                sp.Sockets.Add(new Socket { Name = "Flow",       Type = SocketType.Input,  Color = ColExec,   DataType = DataTypeFromColor(ColExec),   Offset = new Point(-6,       sh + 6) });
                sp.Sockets.Add(new Socket { Name = "Done",       Type = SocketType.Output, Color = ColExec,   DataType = DataTypeFromColor(ColExec),   Offset = new Point(sw - 14, sh + 6) });
                sp.Sockets.Add(new Socket { Name = "InstanceId", Type = SocketType.Output, Color = ColString, DataType = DataTypeFromColor(ColString), Offset = new Point(sw - 14, sh + 6 + ss) });
                return sp;
            }

            // Macro.Call: basic Flow in/out; Architect canvas rebuilds sockets from macro definition
            if (title == "Macro.Call")
            {
                var mc = new Node
                {
                    Title       = title,
                    Category    = t.Category,
                    HeaderColor = t.HeaderColor,
                    Location    = location,
                    Size        = new Size(200, 68),
                    Attributes  = new Dictionary<string, string>(t.DefaultProperties)
                };
                // DataType set from colour at creation — see the Process.Spawn /
                // CreateVisualTriggerNode notes: without it the Flow pins render as
                // ◆ Diamonds and refuse normal ▶ Flow wires until save+reload.
                mc.Sockets.Add(new Socket { Name = "Flow", Type = SocketType.Input,  Color = ColExec, DataType = DataTypeFromColor(ColExec), Offset = new Point(-6, 30) });
                mc.Sockets.Add(new Socket { Name = "Flow", Type = SocketType.Output, Color = ColExec, DataType = DataTypeFromColor(ColExec), Offset = new Point(186, 30) });
                return mc;
            }

            // Event.Trigger and Event.Executor: start with Flow + one placeholder variable socket
            if (title == "Event.Trigger" || title == "Event.Executor")
            {
                return CreateDynamicEventNode(title, t, location);
            }

            // Visual.Trigger: Flow in/out + dynamic variable inputs passed as composition event data
            if (title == "Visual.Trigger")
            {
                return CreateVisualTriggerNode(t, location);
            }

            // Event.Return: flow-in only, dynamic return-value inputs, no flow-out
            if (title == "Event.Return")
            {
                return CreateReturnNode(t, location);
            }

            int nodeWidth     = 200;
            int headerH       = 24;
            int socketSpacing = 22;
            int maxSockets    = Math.Max(t.Inputs.Count, t.Outputs.Count);
            int nodeHeight    = headerH + 14 + maxSockets * socketSpacing;

            var node = new Node
            {
                Title       = title,
                Category    = t.Category,
                HeaderColor = t.HeaderColor,
                Location    = location,
                Size        = new Size(nodeWidth, nodeHeight),
                Attributes  = new Dictionary<string, string>(t.DefaultProperties)
            };

            for (int idx = 0; idx < t.Inputs.Count; idx++)
            {
                var (name, color) = t.Inputs[idx];
                node.Sockets.Add(new Socket
                {
                    Name        = name,
                    Type        = SocketType.Input,
                    Color       = color,
                    DataType    = DataTypeFromColor(color),
                    Offset      = new Point(-6, headerH + 6 + idx * socketSpacing),
                    Description = t.SocketDescriptions.TryGetValue(name, out var desc) ? desc : ""
                });
            }

            for (int idx = 0; idx < t.Outputs.Count; idx++)
            {
                var (name, color) = t.Outputs[idx];
                node.Sockets.Add(new Socket
                {
                    Name        = name,
                    Type        = SocketType.Output,
                    Color       = color,
                    DataType    = DataTypeFromColor(color),
                    Offset      = new Point(nodeWidth - 14, headerH + 6 + idx * socketSpacing),
                    Description = t.SocketDescriptions.TryGetValue(name, out var desc) ? desc : ""
                });
            }

            return node;
        }

        /// <summary>
        /// Creates an Event.Trigger or Event.Executor node with its initial fixed sockets
        /// plus one placeholder variable socket ready to grow dynamically.
        /// </summary>
        private static Node CreateDynamicEventNode(string title, NodeTemplate t, Point location)
        {
            int nodeWidth     = 200;
            int headerH       = 24;
            int socketSpacing = 22;

            // Trigger: Flow in + EventName in (optional) + Flow out (fixed) + 1 arg placeholder input + 1 return placeholder output
            // Executor: Flow out (fixed) + 1 arg placeholder output + 1 return placeholder input
            bool isTrigger = (title == "Event.Trigger");

            // Trigger: flow row + EventName row + arg placeholder row + return placeholder row
            // Executor: flow row + arg placeholder row + return placeholder row
            int fixedRows  = isTrigger ? 4 : 3;
            int nodeHeight = headerH + 14 + fixedRows * socketSpacing;

            var node = new Node
            {
                Title       = title,
                Category    = t.Category,
                HeaderColor = t.HeaderColor,
                Location    = location,
                Size        = new Size(nodeWidth, nodeHeight),
                Attributes  = new Dictionary<string, string>(t.DefaultProperties)
            };

            // Fixed Flow input (Trigger only)
            if (isTrigger)
            {
                node.Sockets.Add(new Socket
                {
                    Name = "Flow", Type = SocketType.Input, Color = ColExec,
                    DataType = DataTypeFromColor(ColExec),
                    Offset = new Point(-6, headerH + 6)
                });

                // Optional EventName input — when wired, overrides the hardcoded EventName attribute
                node.Sockets.Add(new Socket
                {
                    Name = "EventName", Type = SocketType.Input, Color = ColString,
                    DataType = DataTypeFromColor(ColString),
                    Offset = new Point(-6, headerH + 6 + socketSpacing)
                });
            }

            // Fixed Flow output (both)
            node.Sockets.Add(new Socket
            {
                Name = "Flow", Type = SocketType.Output, Color = ColExec,
                DataType = DataTypeFromColor(ColExec),
                Offset = new Point(nodeWidth - 14, headerH + 6)
            });

            // Arg placeholder socket (variable input for Trigger, variable output for Executor)
            // For Trigger: shifted down one row to make room for EventName
            if (isTrigger)
            {
                node.Sockets.Add(new Socket
                {
                    Name = "+ variable", Type = SocketType.Input,
                    Color = ColString, IsPlaceholder = true,
                    DataType = DataTypeFromColor(ColString),
                    Offset = new Point(-6, headerH + 6 + 2 * socketSpacing)
                });
            }
            else
            {
                node.Sockets.Add(new Socket
                {
                    Name = "+ variable", Type = SocketType.Output,
                    Color = ColString, IsPlaceholder = true,
                    DataType = DataTypeFromColor(ColString),
                    Offset = new Point(nodeWidth - 14, headerH + 6 + socketSpacing)
                });
            }

            // Return placeholder socket (return output for Trigger, return input for Executor)
            // For Trigger: shifted down one row to make room for EventName
            if (isTrigger)
            {
                node.Sockets.Add(new Socket
                {
                    Name = "+ return", Type = SocketType.Output,
                    Color = ColReturn, IsPlaceholder = true,
                    DataType = DataTypeFromColor(ColReturn),
                    Offset = new Point(nodeWidth - 14, headerH + 6 + 3 * socketSpacing)
                });
            }
            else
            {
                node.Sockets.Add(new Socket
                {
                    Name = "+ return", Type = SocketType.Input,
                    Color = ColReturn, IsPlaceholder = true,
                    DataType = DataTypeFromColor(ColReturn),
                    Offset = new Point(-6, headerH + 6 + 2 * socketSpacing)
                });
            }

            return node;
        }

        private static Node CreateMacroEntryExitNode(string title, NodeTemplate t, Point location)
        {
            int nodeWidth     = 200;
            int headerH       = 24;
            int socketSpacing = 22;
            // Macro.Entry / Process.Entry → outputs (var-in surface).
            // Macro.Exit  / Process.Exit  → inputs  (var-out surface).
            bool isEntry      = (title == "Macro.Entry" || title == "Process.Entry");

            // Entry: Flow output + 1 placeholder output (inputs on Macro.Call)
            // Exit:  Flow input  + 1 placeholder input  (outputs on Macro.Call)
            int fixedRows  = 2; // flow row + placeholder row
            int nodeHeight = headerH + 14 + fixedRows * socketSpacing;

            var node = new Node
            {
                Title       = title,
                Category    = t.Category,
                HeaderColor = t.HeaderColor,
                Location    = location,
                Size        = new Size(nodeWidth, nodeHeight),
                Attributes  = new Dictionary<string, string>(t.DefaultProperties)
            };

            // Flow socket
            // DataType set from colour at creation (see CreateVisualTriggerNode's
            // [visual-trigger-pins] note). Without it this Flow pin defaulted to
            // SocketDataType.Any, so the canvas rendered it as a ◆ Diamond instead of
            // an exec ▶ Chevron AND AreCompatible rejected every Flow↔Any wire — which
            // made a freshly-created Macro/Process Entry/Exit unwireable to normal pipes
            // (and a new auto-seeded process unauthorable) until the next save+reload
            // re-synced DataType from Color via MigrateNodes.
            node.Sockets.Add(new Socket
            {
                Name     = "Flow",
                Type     = isEntry ? SocketType.Output : SocketType.Input,
                Color    = ColExec,
                DataType = DataTypeFromColor(ColExec),
                Offset   = isEntry
                    ? new Point(nodeWidth - 14, headerH + 6)
                    : new Point(-6, headerH + 6)
            });

            // Dynamic placeholder
            node.Sockets.Add(new Socket
            {
                Name          = isEntry ? "+ input" : "+ output",
                Type          = isEntry ? SocketType.Output : SocketType.Input,
                Color         = ColString,
                DataType      = DataTypeFromColor(ColString),
                IsPlaceholder = true,
                Offset        = isEntry
                    ? new Point(nodeWidth - 14, headerH + 6 + socketSpacing)
                    : new Point(-6, headerH + 6 + socketSpacing)
            });

            return node;
        }

        private static Node CreateReturnNode(NodeTemplate t, Point location)
        {
            int nodeWidth     = 200;
            int headerH       = 24;
            int socketSpacing = 22;
            int fixedRows     = 2; // flow row + placeholder row
            int nodeHeight    = headerH + 14 + fixedRows * socketSpacing;

            var node = new Node
            {
                Title       = "Event.Return",
                Category    = t.Category,
                HeaderColor = t.HeaderColor,
                Location    = location,
                Size        = new Size(nodeWidth, nodeHeight),
                Attributes  = new Dictionary<string, string>(t.DefaultProperties)
            };

            // Flow input only — no flow output (terminal node).
            // DataType set from colour at creation — see CreateVisualTriggerNode's
            // [visual-trigger-pins] note (Flow pins left at Any render as ◆ Diamonds
            // and reject normal ▶ Flow wires until save+reload).
            node.Sockets.Add(new Socket
            {
                Name = "Flow", Type = SocketType.Input, Color = ColExec,
                DataType = DataTypeFromColor(ColExec),
                Offset = new Point(-6, headerH + 6)
            });

            // One placeholder return-value input
            node.Sockets.Add(new Socket
            {
                Name = "+ return", Type = SocketType.Input,
                Color = ColReturn, IsPlaceholder = true,
                DataType = DataTypeFromColor(ColReturn),
                Offset = new Point(-6, headerH + 6 + socketSpacing)
            });

            return node;
        }

        private static Node CreateVisualTriggerNode(NodeTemplate t, Point location)
        {
            int nodeWidth     = 200;
            int headerH       = 24;
            int socketSpacing = 22;
            // Flow + Args (fixed Collection input) + one placeholder row.
            int fixedRows     = 3;
            int nodeHeight    = headerH + 14 + fixedRows * socketSpacing;

            var node = new Node
            {
                Title       = "Visual.Trigger",
                Category    = t.Category,
                HeaderColor = t.HeaderColor,
                Location    = location,
                Size        = new Size(nodeWidth, nodeHeight),
                Attributes  = new Dictionary<string, string>(t.DefaultProperties)
            };

            // [visual-trigger-pins 2026-06-10] Every socket below now sets
            // DataType = DataTypeFromColor(...). Socket.DataType DEFAULTS to
            // SocketDataType.Any (GraphModels.cs), and the WinUI canvas derives
            // the pin SHAPE from DataType (SocketPalette.KindFor) — so without an
            // explicit assignment the white "Flow"/"Done" pins rendered as Any
            // CIRCLES, not exec CHEVRONS ("Flow is not a flow node"), and Args
            // rendered as a circle instead of a Collection pin. The generic
            // CreateNode path sets this from the colour; this special-case builder
            // had silently omitted it. (Saved graphs were masked by MigrateNodes
            // re-syncing DataType from Color on load; a freshly-created or pasted
            // node had no such pass, so it rendered wrong until save+reload.)

            // Fixed Flow input
            node.Sockets.Add(new Socket
            {
                Name = "Flow", Type = SocketType.Input, Color = ColExec,
                DataType = DataTypeFromColor(ColExec),
                Offset = new Point(-6, headerH + 6)
            });

            // Fixed Done/Flow output
            node.Sockets.Add(new Socket
            {
                Name = "Done", Type = SocketType.Output, Color = ColExec,
                DataType = DataTypeFromColor(ColExec),
                Offset = new Point(nodeWidth - 14, headerH + 6)
            });

            // Fixed Args input (Collection / SaddleBrown). Wire a comma-separated
            // list (e.g. Array.Make / Array.Literal output) — Hub splits the value
            // into Args1..ArgsN keys before delivery so widget-side consumers can
            // reference them positionally as {Args1} etc.
            node.Sockets.Add(new Socket
            {
                Name = "Args", Type = SocketType.Input, Color = ColList,
                DataType = DataTypeFromColor(ColList),
                Offset = new Point(-6, headerH + 6 + socketSpacing)
            });

            // One placeholder variable input — grows dynamically when connected
            node.Sockets.Add(new Socket
            {
                Name = "+ variable", Type = SocketType.Input,
                Color = ColString, IsPlaceholder = true,
                DataType = DataTypeFromColor(ColString),
                Offset = new Point(-6, headerH + 6 + socketSpacing * 2)
            });

            return node;
        }

        // ─────────────────────────────────────────────────────────────────
        //  SHARED PLACEHOLDER HELPERS
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures a <c>DB.FetchRow</c> node carries one output
        /// socket per entry in its <c>KnownColumns</c> attribute (),
        /// in addition to the fixed Found / NotFound / Row sockets. Existing
        /// column sockets are preserved (their Id stays stable so attached
        /// wires stay valid); columns no longer in <c>KnownColumns</c> are
        /// removed along with any links wired to them.
        ///
        /// Idempotent — safe to call on every load and on every attribute
        /// edit. Sockets are appended at the end of the node's socket list
        /// so the fixed-shape (Flow / Found / NotFound / Row) layout stays
        /// intact at the top of the body.
        /// </summary>
        public static void EnsureFetchRowColumnSockets(Node node, Phoenix.Controls.Shared.Models.Graph? graph = null)
        {
            if (node?.Title != "DB.FetchRow") return;

            var fixedNames = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "Flow", "TableName", "RowId", "Found", "NotFound", "Row"
            };

            // Parse KnownColumns. Empty hint → no synthesized column sockets.
            string raw = node.Attributes.TryGetValue("KnownColumns", out var kc) ? kc ?? "" : "";
            var wanted = new List<string>();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var rawCol in raw.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                {
                    string col = rawCol.Trim('"');
                    if (string.IsNullOrEmpty(col)) continue;
                    if (fixedNames.Contains(col)) continue; // would collide with a built-in
                    if (!wanted.Contains(col)) wanted.Add(col);
                }
            }

            // Walk existing output sockets — keep the fixed ones and any wanted column,
            // collect ids of column sockets we're about to drop so the caller can sweep links.
            var dropIds = new List<string>();
            for (int i = node.Sockets.Count - 1; i >= 0; i--)
            {
                var s = node.Sockets[i];
                if (s.Type != Phoenix.Controls.Shared.Models.SocketType.Output) continue;
                if (fixedNames.Contains(s.Name)) continue;
                if (wanted.Contains(s.Name)) continue;
                // Synthesized column socket no longer in KnownColumns — drop it.
                dropIds.Add(s.Id);
                node.Sockets.RemoveAt(i);
            }

            // Append missing column sockets in the user-declared order.
            // Snapshot existing output-socket names once so the per-column
            // existence check is O(1) instead of an O(M) List.Exists scan per wanted
            // column (the loop runs on the UI thread during graph deserialization).
            var existingOutputNames = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var s in node.Sockets)
            {
                if (s.Type == Phoenix.Controls.Shared.Models.SocketType.Output)
                    existingOutputNames.Add(s.Name);
            }
            foreach (var col in wanted)
            {
                if (existingOutputNames.Contains(col))
                    continue;
                node.Sockets.Add(new Phoenix.Controls.Shared.Models.Socket
                {
                    Name     = col,
                    Type     = Phoenix.Controls.Shared.Models.SocketType.Output,
                    Color    = ColString,
                    DataType = Phoenix.Controls.Shared.Models.SocketDataType.String,
                });
            }

            // If a graph was supplied, prune links that point at dropped column sockets
            // (otherwise they'd dangle and trip the validator). Caller is responsible
            // for invoking MarkStructuralChange after.
            if (graph != null && dropIds.Count > 0)
            {
                graph.Links.RemoveAll(l => dropIds.Contains(l.FromSocketId) || dropIds.Contains(l.ToSocketId));
                graph.MarkStructuralChange();
            }
        }

        /// <summary>
        /// Ensure a macro / process sub-graph carries its boundary node pair — the
        /// <c>Macro.Entry</c>+<c>Macro.Exit</c> (or <c>Process.Entry</c>+<c>Process.Exit</c>)
        /// nodes that let the parent <c>Macro.Call</c> / <c>Process.Start</c> site pass
        /// flow + data into the body and read results back out. Seeds whichever
        /// singleton is missing and returns true if it added anything; a no-op when
        /// both already exist, so it is safe to call on every editor open as a
        /// self-heal for sub-graphs created before the boundary nodes were seeded.
        /// </summary>
        /// <remarks>
        /// Without these nodes the exporter walks an empty Entry/Exit and the
        /// macro / process transfers nothing — an "empty body" that cannot be
        /// authored. The rail's "New process" path seeded these inline ();
        /// the "New macro" path historically did not, so a freshly-created macro
        /// opened with no in/out node. Centralising the seed here keeps the create
        /// path (LeftRail) and the heal path (SubGraphWindow editor open) in lockstep.
        /// Entry is placed top-left, Exit to its right — the same layout the
        /// convert-to-macro collapse (CollapseSelectionToMacro) uses.
        /// </remarks>
        public static bool EnsureSubGraphBoundaryNodes(Graph graph, string entryTitle, string exitTitle)
        {
            if (graph is null) return false;
            bool added = false;
            if (!graph.Nodes.Exists(n => string.Equals(n.Title, entryTitle, StringComparison.Ordinal)))
            {
                var entry = CreateNode(entryTitle, new Point(80, 160));
                if (entry is not null) { graph.Nodes.Add(entry); added = true; }
            }
            if (!graph.Nodes.Exists(n => string.Equals(n.Title, exitTitle, StringComparison.Ordinal)))
            {
                var exit = CreateNode(exitTitle, new Point(520, 160));
                if (exit is not null) { graph.Nodes.Add(exit); added = true; }
            }
            return added;
        }

        /// <summary>
        /// Ensures Event.Trigger / Event.Executor / Event.Return nodes have the
        /// expected dynamic placeholder sockets. Safe to call on both newly created
        /// and deserialized nodes — does nothing if placeholders already exist.
        /// </summary>
        /// <remarks>
        /// When a recovery socket is added we restripe Y offsets via
        /// <see cref="PlaceholderActivator.RecalculateSocketOffsets"/> + also stamp
        /// the matching X offset on the new socket (the helper only touches Y).
        /// Without this, the recovery socket renders at (0,0) for a single frame
        /// on graph load — visible as a pin pasted into the node's top-left
        /// corner until the canvas's next layout recalculation runs.
        /// </remarks>
        public static void EnsureEventNodePlaceholders(Node node)
        {
            if (node is null) return;
            const int defaultNodeWidth = 200;
            int nodeWidth = node.Size.Width > 0 ? node.Size.Width : defaultNodeWidth;

            // Local helper — restripe Y offsets across the whole node and patch
            // the freshly-added socket's X offset (placeholder helper only owns Y).
            void RestripeAfterAdd(Socket newSocket)
            {
                PlaceholderActivator.RecalculateSocketOffsets(node);
                int x = newSocket.Type == SocketType.Input ? -6 : nodeWidth - 14;
                newSocket.Offset = new Point(x, newSocket.Offset.Y);
            }

            if (node.Title == "Event.Return")
            {
                if (!node.Sockets.Exists(s => s.IsPlaceholder && s.Type == SocketType.Input))
                {
                    var sock = new Socket
                    {
                        Name = "+ return", Type = SocketType.Input,
                        Color = ColReturn, DataType = DataTypeFromColor(ColReturn),
                        IsPlaceholder = true
                    };
                    node.Sockets.Add(sock);
                    RestripeAfterAdd(sock);
                }
                return;
            }

            bool isTrigger = node.Title == "Event.Trigger";
            var  argPhType = isTrigger ? SocketType.Input  : SocketType.Output;
            var  retPhType = isTrigger ? SocketType.Output : SocketType.Input;

            if (!node.Sockets.Exists(s => s.IsPlaceholder && s.Type == argPhType))
            {
                var sock = new Socket
                {
                    Name = "+ variable", Type = argPhType,
                    Color = ColString, DataType = DataTypeFromColor(ColString),
                    IsPlaceholder = true
                };
                node.Sockets.Add(sock);
                RestripeAfterAdd(sock);
            }

            if (!node.Sockets.Exists(s => s.IsPlaceholder && s.Type == retPhType))
            {
                var sock = new Socket
                {
                    Name = "+ return", Type = retPhType,
                    Color = ColReturn, DataType = DataTypeFromColor(ColReturn),
                    IsPlaceholder = true
                };
                node.Sockets.Add(sock);
                RestripeAfterAdd(sock);
            }
        }
    }
}
