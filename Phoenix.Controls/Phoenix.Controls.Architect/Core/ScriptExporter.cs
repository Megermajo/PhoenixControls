using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    public class ScriptExporter
    {
        private readonly Graph _graph;
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly HashSet<string> _visitedNodes = new HashSet<string>();

        // Insertion-order companion to _visitedNodes, kept in lock-step (adds go
        // through MarkVisited; the per-event reset clears both). Traversal only
        // ever ADDS to the visited set (nothing outside EmitBranch ever removes
        // entries), so EmitBranch can capture a branch arm's visited-delta as
        // a list segment and undo exactly those entries — O(delta) instead of
        // snapshotting the whole set per conditional. EmitBranch is the sole
        // remover and keeps both containers in sync.
        private readonly List<string> _visitedOrder = new();

        private void MarkVisited(string nodeId)
        {
            if (_visitedNodes.Add(nodeId)) _visitedOrder.Add(nodeId);
        }

        // Tracks the indent level of the currently executing ProcessNode call so that
        // ResolveOutputFromNode can emit on-demand pre-statements for pure data nodes.
        private int _currentIndent = 1;

        // Maps node ID → the resolved value/variable reference to use when referencing its output.
        // For single-use nodes: the inline expression. For multi-use nodes: "{$varName}".
        private readonly Dictionary<string, string> _nodeResultVars = new();

        // Counters for generating unique $varName per node title
        private readonly Dictionary<string, int> _varNameCounters = new();

        // Nodes blocked from being processed inside a branch because they are convergence/merge points.
        // They will be processed at the outer indent after the branch completes.
        private readonly HashSet<string> _blockedForBranch = new();

        // Flow-pin detection lives in SocketTypeHelper (Phoenix.Controls.Shared.Models)
        // — single source of truth shared with Visualist's canvas-side code.
        //
        // BUT: traversal/merge logic (FollowFlowOutput, FindMergePoint, CountIncomingFlows)
        // must walk *linear* flow only. Branching outputs ("True"/"False"/"1"/"2"/"3"/etc.)
        // are owned by their handler (BranchHandler/IfHandler/SwitchHandler) which calls
        // EmitBranch / ProcessNode per arm — if the merge BFS traverses through them, it
        // collapses inner-Logic.If subtrees onto a single shared continuation node and
        // suppresses per-branch emits. Mirror the pre-SocketTypeHelper linear-flow set
        // here so traversal stays scoped.
        private static readonly HashSet<string> LinearFlowOutputNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "flow","done","sent","active","out","completed","received","ontime","late"
        };
        private static bool IsLinearFlowOutput(Socket s)
            => s.Type == SocketType.Output && LinearFlowOutputNames.Contains(s.Name ?? "");

        // A node soft-disabled from the editor — "Disable Node" toggles the
        // __disabled attribute (NodeViewModel.IsDisabled owns the write side;
        // the flag round-trips through .phxg via the normal attribute
        // serializer). Case-insensitive to match the ViewModel's read contract,
        // so a hand-edited "True" in a .phxg behaves like the UI's "true".
        private static bool IsDisabled(Node node)
            => string.Equals(node.GetAttr("__disabled", ""), "true", StringComparison.OrdinalIgnoreCase);

        private readonly string _macroContextId; // set when exporting a macro sub-graph

        // Shared across nested ScriptExporter instances so we can detect
        // circular macro references before they recurse into a stack overflow.
        private readonly Stack<string> _macroStack;

        // O(1) Contains companion for _macroStack — kept in sync on every push/pop
        // (CtxExportMacroSubGraph) so the per-spawn cycle check doesn't do an
        // O(depth) linear scan of the stack on each Process.Spawn / Macro.Call.
        private readonly HashSet<string> _macroStackSet;

        // Hard ceiling on macro/process expansion nesting depth. The _macroStack
        // guard above catches CYCLES, but a legitimately deep UNIQUE chain
        // (M1→M2→…→Mn, no repeated key) still recurses one nested
        // ScriptExporter().Export() per level — past a few dozen levels that
        // overflows the thread stack as an UNCATCHABLE StackOverflowException that
        // kills the whole process (Architect on save, or a test host). This is the
        // export-time analogue of ScriptEngine.MaxExecutionDepth: no real graph
        // nests macros more than single digits deep, so a ceiling of 64 rejects
        // only pathological / corrupt input, and does so as a clean throw.
        internal const int MaxMacroExpansionDepth = 64;

        // ── Export index ─────────────────────────────────────────────────────
        // Lazily-built per-export lookup indices over the (immutable-during-export)
        // _graph. Built once on first use, reused for the whole Export() pass, and
        // reset at the top of Export() so a re-export of the same instance rebuilds
        // against the current graph. Macro/process sub-exporters construct a fresh
        // ScriptExporter per body, so each builds its own indices over its own graph.
        // INSTANCE fields (never static) — they key on this exporter's _graph.
        private Dictionary<string, Node>? _nodeById;
        // First-wins per (fromNodeId, fromSocketId) to match the old
        // _graph.Links.FirstOrDefault(...) selection semantics exactly.
        private Dictionary<(string fromNodeId, string fromSocketId), Link>? _linkByFromSocket;
        // Incoming links grouped by ToNodeId, preserving _graph.Links order so any
        // per-socket count stays identical to the old linear Count(...) scans.
        private Dictionary<string, List<Link>>? _linksByToNode;
        // Outgoing link count per FromNodeId (for OutputConsumerCount).
        private Dictionary<string, int>? _outgoingCountByNode;

        private void EnsureIndices()
        {
            if (_nodeById != null) return;

            var nodeById = new Dictionary<string, Node>(_graph.Nodes.Count);
            foreach (var n in _graph.Nodes)
                if (n.Id != null) nodeById[n.Id] = n; // last-wins mirrors FirstOrDefault for unique ids

            var linkByFromSocket = new Dictionary<(string, string), Link>();
            var linksByToNode = new Dictionary<string, List<Link>>();
            var outgoingCount = new Dictionary<string, int>();
            foreach (var l in _graph.Links)
            {
                var fromKey = (l.FromNodeId, l.FromSocketId);
                if (!linkByFromSocket.ContainsKey(fromKey))
                    linkByFromSocket[fromKey] = l; // first wins — matches FirstOrDefault

                outgoingCount.TryGetValue(l.FromNodeId, out int oc);
                outgoingCount[l.FromNodeId] = oc + 1;

                if (!linksByToNode.TryGetValue(l.ToNodeId, out var list))
                    linksByToNode[l.ToNodeId] = list = new List<Link>();
                list.Add(l);
            }

            _nodeById = nodeById;
            _linkByFromSocket = linkByFromSocket;
            _linksByToNode = linksByToNode;
            _outgoingCountByNode = outgoingCount;
        }

        private Node? NodeById(string id)
        {
            EnsureIndices();
            return _nodeById!.TryGetValue(id, out var n) ? n : null;
        }

        // Title-keyed dispatch — single source of truth for all node emit logic.
        private readonly ExporterRegistry _registry = new();
        private readonly ExporterContext _ctx;

        // Categories whose nodes are pure-data (consumed inline by
        // ResolveOutputFromNode). When such a node is reached via flow input,
        // populate its result cache via ComputeInlineValue and skip emit.
        // Exposed publicly so NodeCoverageTests can read from the
        // canonical source instead of re-declaring its own copy. The test
        // wrapper now consumes PureDataCategories directly.
        public static readonly HashSet<string> _pureDataCategories
            = new(StringComparer.OrdinalIgnoreCase)
        {
            "Math", "Text", "Convert", "Logic", "Values",
            "Collections", "Databank", "Variables", "State", "System", "Twitch Data",
            "Platform Data"
        };

        /// <summary>
        /// Read-only accessor for the canonical pure-data category set.
        /// Mirrors <see cref="_pureDataCategories"/> for callers that prefer a
        /// non-mutable view (e.g. the NodeCoverageTests guard).
        /// </summary>
        public static IReadOnlyCollection<string> PureDataCategories => _pureDataCategories;

        // Strict callable detector. A "callable" expression is one that
        // looks like `name(` or `module.name(` (a dotted lowercase identifier
        // chain immediately followed by an open paren — matches all Script
        // commands like `math.add(`, `text.format(`, `db.get_cell(`, plus bare
        // helpers like `not(`). Previously the exporter used a loose
        // `Contains("(")` heuristic which misfired on user-typed strings
        // such as "(foo)" — a parenthesized literal that is NOT a function call.
        internal static readonly Regex CallableRegex =
            new(@"^[a-z_][a-z0-9_]*(\.[a-z_][a-z0-9_]*)*\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// Strict callable check exposed to handler-side code that does its
        /// own hoist (currently <c>ForLoopHandler</c>). Mirrors the gate in
        /// <see cref="MaterializeInput"/> so the two paths stay consistent — a
        /// parenthesised literal like "(foo)" is NOT a function call and shouldn't
        /// trip the hoist.
        /// </summary>
        public static bool IsCallableExpression(string val) =>
            !string.IsNullOrEmpty(val) && CallableRegex.IsMatch(val);

        // Runtime warnings raised during traversal (e.g. ambiguous multi-link
        // input sockets). Surfaced as `# WARNING: ...` comment lines after the
        // pre-pass GraphValidator results in Export(). De-duped across calls so a
        // single ambiguity doesn't show up multiple times if the graph is walked
        // through more than one ResolveInputValue path.
        private readonly List<ValidationWarning> _runtimeWarnings = new();
        private readonly HashSet<string> _runtimeWarningsSeen = new(StringComparer.OrdinalIgnoreCase);

        private void AddRuntimeWarning(string message, string? nodeId = null)
        {
            string key = (nodeId ?? "") + "|" + message;
            if (!_runtimeWarningsSeen.Add(key)) return;
            _runtimeWarnings.Add(new ValidationWarning
            {
                Severity = ValidationSeverity.Warning,
                Message  = message,
                NodeId   = nodeId
            });
        }

        // Macro-export memo — shared across the whole nested-exporter tree (threaded
        // through the private ctor like _macroStack) so a macro/process body called
        // from N reachable sites/events exports its sub-script once. Keyed on
        // everything that affects sub-export output: the body graph identity, the
        // call-site slot prefix (embeds the unique call-site id), and a snapshot of
        // the in-flight macro stack (nesting context). See CtxExportMacroSubGraph.
        private readonly Dictionary<string, string> _macroExportCache;

        // Validation memo — shared across the nested-exporter tree (threaded
        // through the private ctor like _macroExportCache) so a macro/process
        // body graph reached from N call sites is validated once per export run
        // instead of once per call site. Keyed on graph object identity; graphs
        // are immutable during an export pass, so the memoized warning list is
        // exactly what a re-run would produce. Cleared at the top of each
        // TOP-LEVEL Export() so a re-export of the same instance re-validates
        // against the current graph state.
        private readonly Dictionary<Graph, List<ValidationWarning>> _validationCache;

        // Live-processes — when true, this exporter is producing a process
        // TEMPLATE (a standalone mini-script): Process.Entry → on_process_start:,
        // Process.Exit → on_process_stop:, and Process.Entry param outputs resolve
        // to {param.<name>} (the instance-scoped start params). Off for the normal
        // main-script export and the legacy inline Process.Spawn path.
        private readonly bool _processTemplateMode;

        public ScriptExporter(Graph graph, string macroContextId = "")
            : this(graph, macroContextId, new Stack<string>(), new Dictionary<string, string>(StringComparer.Ordinal),
                   new Dictionary<Graph, List<ValidationWarning>>(ReferenceEqualityComparer.Instance))
        {
        }

        /// <summary>Constructs an exporter that emits a process TEMPLATE (see
        /// <see cref="_processTemplateMode"/> / <see cref="ExportAll"/>).</summary>
        public ScriptExporter(Graph graph, bool processTemplateMode)
            : this(graph, "", new Stack<string>(), new Dictionary<string, string>(StringComparer.Ordinal),
                   new Dictionary<Graph, List<ValidationWarning>>(ReferenceEqualityComparer.Instance), processTemplateMode)
        {
        }

        private ScriptExporter(Graph graph, string macroContextId, Stack<string> macroStack,
                               Dictionary<string, string> macroExportCache,
                               Dictionary<Graph, List<ValidationWarning>> validationCache,
                               bool processTemplateMode = false)
        {
            _graph = graph;
            _macroContextId = macroContextId;
            _macroStack = macroStack;
            _macroExportCache = macroExportCache;
            _validationCache = validationCache;
            _processTemplateMode = processTemplateMode;
            // Seed the O(1) cycle-check companion from whatever is already on the
            // shared stack (a sub-exporter inherits the parent's in-flight chain).
            _macroStackSet = new HashSet<string>(_macroStack, StringComparer.Ordinal);
            _ctx = new ExporterContext(this);
            ExporterRegistrations.RegisterAllBuiltIns(_registry);
        }

        // Emit a line to the output buffer.
        // Using a string-typed helper prevents the compiler from choosing the
        // StringBuilder.AppendLine(ref DefaultInterpolatedStringHandler) overload,
        // which writes interpolation chunks directly into _sb as each expression
        // evaluates. That caused ResolveInputValue side-effects (pre-statement
        // emission) to be interleaved mid-line. By routing through a plain
        // string parameter the full interpolated string is always built first.
        private void Emit(string line) => _sb.AppendLine(line);

        // Indent-prefix cache — indent prefixes ("    " × depth) are rebuilt in
        // several hot resolve/emit paths. Cache them by depth so repeated emits at
        // the same indent reuse one interned string. Output is byte-identical to
        // `new string(' ', depth * 4)`. Grown on demand; instance-local.
        private readonly List<string> _indentCache = new();
        private string Indent(int depth)
        {
            if (depth < 0) depth = 0;
            while (_indentCache.Count <= depth)
                _indentCache.Add(new string(' ', _indentCache.Count * 4));
            return _indentCache[depth];
        }

        public string Export()
        {
            _sb.Clear();
            _runtimeWarnings.Clear();
            _runtimeWarningsSeen.Clear();
            // Drop any stale indices so a re-export of this
            // same instance rebuilds against the current graph on next use.
            _nodeById = null;
            _linkByFromSocket = null;
            _linksByToNode = null;
            _outgoingCountByNode = null;
            // Same staleness guard for the validation memo — but only at the top
            // level: nested sub-exporters share the parent's per-run cache so a
            // body graph reused from several call sites validates once per run.
            if (_macroContextId.Length == 0) _validationCache.Clear();
            if (_processTemplateMode)
                Emit($"# Process template — \"{_graph.Name}\" — generated, do not edit by hand");
            else
                Emit($"# Script — Generated from \"{_graph.Name}\"");
            Emit($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm}");

            // Validator pass — surface any Errors (cycles, dangling links) and
            // Warnings (placeholder-frame contamination, etc.) in the script
            // header so the user sees them. On any Error severity we refuse to
            // emit body content: a cyclic or fundamentally broken graph must
            // not produce a runnable script — the user has to fix it first.
            var validation = ValidateOnce(_graph);
            foreach (var w in validation)
                Emit($"# {w.Severity.ToString().ToUpperInvariant()}: {w.Message}");

            bool hasError = validation.Any(w => w.Severity == ValidationSeverity.Error);
            if (hasError)
            {
                Emit("# Export aborted: graph has validation errors (see above). Fix the graph and re-export.");
                return _sb.ToString();
            }

            // Build the body into a separate buffer first so any runtime warnings
            // raised during traversal (e.g. ambiguous multi-link inputs) can be
            // hoisted into the header alongside the GraphValidator pre-pass results.
            var headerSnapshot = _sb.ToString();
            _sb.Clear();

            // Only true entry-point event nodes start a script block.
            // Inline event nodes (Trigger, Return) are visited during flow traversal.
            // Process.Entry joins the entry-point set when this exporter is running
            // a process sub-graph (CtxExportMacroSubGraph reuses ScriptExporter for
            // both macro and process bodies). It carries no `on_event(...)` header —
            // ProcessEventNode special-cases it so the process_spawn(...): block
            // header emitted by ProcessSpawnHandler is the only wrapper.
            var inlineEventTitles = new HashSet<string> { "Event.Trigger", "Event.Return" };
            var eventNodes = _graph.Nodes
                .Where(n => (n.Category == "Events" && !inlineEventTitles.Contains(n.Title))
                         || n.Title == "Process.Entry"
                         // Live-process template mode — Process.Exit is the "on stop"
                         // trigger root (its "On Stop" output → on_process_stop:). Only
                         // an entry point while exporting a template; in the legacy inline
                         // path it stays a plain terminator handled by ProcessExitHandler.
                         || (_processTemplateMode && n.Title == "Process.Exit")
                         // Macro.Entry lives in Category="Macros" (not "Events"),
                         // so it never matched the Events filter and macro bodies rooted
                         // on its Flow output silently exported empty. Treat it as an
                         // entry point parallel to Process.Entry; ProcessEventNode walks
                         // its Flow output to emit the macro body.
                         || n.Title == "Macro.Entry")
                .ToList();
            foreach (var evt in eventNodes)
            {
                // A disabled event root suppresses its entire handler — no
                // header, no body. The engine simply finds no matching block
                // for that event, so a graph whose roots are all disabled still
                // exports a loadable (header-only) script.
                if (IsDisabled(evt)) continue;
                _visitedNodes.Clear();
                _visitedOrder.Clear();
                ProcessEventNode(evt);
                _sb.AppendLine();
            }
            string body = _sb.ToString();

            // Re-build final output: header + runtime warnings + blank line + body.
            _sb.Clear();
            _sb.Append(headerSnapshot);
            foreach (var w in _runtimeWarnings)
                Emit($"# {w.Severity.ToString().ToUpperInvariant()}: {w.Message}");
            _sb.AppendLine();
            _sb.Append(body);

            return _sb.ToString();
        }

        /// <summary>
        /// Full export for the live-process model: the main script PLUS one
        /// standalone TEMPLATE per Process in the graph. A process body is no
        /// longer inlined into the main script — its Process.Start nodes emit
        /// `process.start(...)` (see ProcessStartHandler) and the Hub runs the
        /// template as a live, event-driven mini-script per started instance.
        /// Each template is produced by a fresh top-level exporter in
        /// <see cref="_processTemplateMode"/>, so its Process.Entry / Process.Exit /
        /// Schedule / on_chat / … nodes become real top-level blocks.
        /// </summary>
        public ProcessExportResult ExportAll()
        {
            string main = Export();
            var templates = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = new List<string>();
            if (_graph.Processes != null)
            {
                foreach (var p in _graph.Processes)
                {
                    if (p == null || string.IsNullOrEmpty(p.ProcessId) || p.Graph == null) continue;
                    templates[p.ProcessId] = new ScriptExporter(p.Graph, processTemplateMode: true).Export();
                    ids.Add(p.ProcessId);
                }
            }
            return new ProcessExportResult(main, templates, ids);
        }

        // Per-run validation memo (see _validationCache). Callers only
        // enumerate the returned list, so sharing one instance across cache
        // hits is safe.
        private List<ValidationWarning> ValidateOnce(Graph graph)
        {
            if (_validationCache.TryGetValue(graph, out var cached)) return cached;
            var computed = GraphValidator.Validate(graph);
            _validationCache[graph] = computed;
            return computed;
        }

        // ══════════════════════════════════════════════════════════════════
        // EVENT ENTRY POINTS
        // ══════════════════════════════════════════════════════════════════

        // CamelCase→snake_case boundary. Hoisted + compiled like CallableRegex —
        // CommandName runs per emitted command line, so the inline Regex.Replace
        // re-parsed the pattern on every call.
        private static readonly Regex CamelBoundaryRegex =
            new(@"(?<=[a-z0-9])()", RegexOptions.Compiled);

        /// <summary>Converts "Twitch.SendChat" → "twitch.send_chat", "Math.Add" → "math.add".</summary>
        private static string CommandName(string title)
        {
            var parts = title.Split('.');
            return string.Join(".", parts.Select(p =>
                CamelBoundaryRegex.Replace(p, "_$1").ToLower()
            ));
        }

        private int OutputConsumerCount(Node node)
        {
            EnsureIndices();
            return _outgoingCountByNode!.TryGetValue(node.Id, out int c) ? c : 0;
        }

        private string LocalVarName(Node node)
        {
            string baseName = "$" + CommandName(node.Title).Replace(".", "_");
            _varNameCounters.TryGetValue(baseName, out int n);
            _varNameCounters[baseName] = n + 1;
            return n == 0 ? baseName : $"{baseName}_{n + 1}";
        }

        private void ProcessEventNode(Node node)
        {
            _nodeResultVars.Clear();
            _varNameCounters.Clear();
            // Process.Entry — process body's entry point, no on_event wrapper.
            // ProcessSpawnHandler already emitted the `process_spawn(...):`
            // block-header; we just walk the flow at indent 0 so the caller
            // can re-indent inside that block.
            //
            // Macro.Entry is the macro-body analogue: it lives in
            // Category="Macros" (so it newly joins the entry-point set above) and,
            // exactly like Process.Entry, carries NO header. MacroCallHandler emits
            // the call-site framing and re-indents the body via the content-based
            // header strip on ExportMacroSubGraph's output. Without this special-case the
            // node would fall through to the `on_event(...)` switch below and emit a
            // spurious `on_event(Macro.Entry):` header, corrupting the macro body.
            // Live-process template mode — Process.Entry is the "on start" trigger
            // (header on_process_start:, body at indent 1) and Process.Exit is the
            // "on stop" trigger (header on_process_stop:, walking its "On Stop" output).
            if (_processTemplateMode && node.Title == "Process.Entry")
            {
                Emit("on_process_start:");
                FollowNamedOutput(node, "Flow", 1);
                return;
            }
            if (_processTemplateMode && node.Title == "Process.Exit")
            {
                Emit("on_process_stop:");
                FollowNamedOutput(node, "On Stop", 1);
                return;
            }
            if (node.Title == "Process.Entry" || node.Title == "Macro.Entry")
            {
                FollowNamedOutput(node, "Flow", 0);
                // Return fallback: when the body's flow never reaches ANY
                // Macro.Exit (the author only data-wired the exit), bind the
                // return slots after the body so the call-site outputs still
                // read real values. Binds the first Exit — mirrors
                // MacroCallHandler's "only the first is used" contract. If flow
                // reached SOME exit, that executed path already bound the slots
                // (MacroExitHandler) — appending the first exit's unconditional
                // bind here would clobber the executed path's value, so the
                // fallback is skipped entirely in that case.
                if (node.Title == "Macro.Entry" && !string.IsNullOrEmpty(_macroContextId))
                {
                    var exits = _graph.Nodes.Where(n => n.Title == "Macro.Exit").ToList();
                    if (exits.Count > 0 && !exits.Any(e => _visitedNodes.Contains(e.Id)))
                        ProcessNode(exits[0], 0);
                }
                return;
            }
            // The unified Chat.Message node (and the legacy Twitch.ChatMessage
            // title, still honored for graphs that predate the migration) has
            // special handling for platform checkmarks + Commands filtering.
            if (node.Title == "Chat.Message" || node.Title == "Twitch.ChatMessage")
            {
                ProcessChatMessageEventNode(node);
                return;
            }
            // Unified stream-lifecycle triggers — platform checkmarks like
            // Chat.Message, but emitting on_go_live(...) / on_session_end(...).
            if (node.Title == "Stream.GoingLive" || node.Title == "Stream.SessionEnd")
            {
                ProcessStreamLifecycleEventNode(node);
                return;
            }
            // Bus.OnMessage may need a Source/Target wildcard guard injected
            // between the on_bus header and the body. Done in a dedicated method
            // so the surrounding switch stays readable.
            if (node.Title == "Bus.OnMessage")
            {
                ProcessBusOnMessageEventNode(node);
                return;
            }

            string trigger = node.Title switch
            {
                "Twitch.Subscription" => "on_event(Twitch.Sub)",
                "Twitch.Resub"        => "on_event(Twitch.Resub)",
                "Twitch.GiftSub"      => "on_event(Twitch.GiftSub)",
                "Twitch.GiftBomb"     => "on_event(Twitch.GiftBomb)",
                "Twitch.Follow"       => "on_event(Twitch.Follow)",
                "Twitch.Raid"         => "on_event(Twitch.Raid)",
                "Twitch.Cheer"        => "on_event(Twitch.Cheer)",
                "Twitch.PointRedeem"  => "on_event(Twitch.PointRedeem)",
                // Inbound whisper node title differs from the SB event type
                // (its title is taken by the outbound send-whisper action), so
                // map it explicitly to the real event the Hub dispatches.
                "Twitch.InWhisper"    => "on_event(Twitch.Whisper)",
                "YouTube.Message"     => "on_event(YouTube.Message)",
                "System.Startup"      => "on_startup",
                "Event.Executor"        => $"on_event(Internal.{node.GetAttr("EventName", "MyEvent")})",
                "HTTP.WebhookListener"  => $"on_webhook(\"{node.GetAttr("Name", "default")}\")",
                "WS.Server"             => $"on_websocket(\"{node.GetAttr("Name", "default")}\")",
                "System.Hotkey"         => $"on_hotkey(\"{node.GetAttr("Combination", "Ctrl+Shift+P")}\")",
                "System.Clipboard"      => "on_clipboard",
                // OBS WS v5 event subscription. EventType attribute is
                // the bare OBS event name (e.g. CurrentProgramSceneChanged).
                // Dispatched by ScriptManager.DispatchObsEvent against the
                // matching on_obs("<EventType>") block.
                "OBS.Event"             => $"on_obs(\"{node.GetAttr("EventType", "CurrentProgramSceneChanged")}\")",
                "Schedule.Cron"         => $"on_schedule(\"{node.GetAttr("CronExpression", "*/5 * * * *")}\")",
                "Schedule.RunAt"        => $"on_schedule_once(\"{node.GetAttr("DateTime", "")}\")",
                "Schedule.Recurring"    => BuildOnIntervalTrigger(node),
                "State.OnChange"        => $"on_state_change({node.GetAttr("StateName", "stream_phase")})",
                _                       => $"on_event({node.Title})"
            };

            Emit($"{trigger}:");

            FollowNamedOutput(node, "Flow", 1);

            // Event.Executor: capture return values after flow completes, only for linked sockets
            if (node.Title == "Event.Executor")
            {
                EnsureIndices();
                _linksByToNode!.TryGetValue(node.Id, out var incoming);
                foreach (var s in node.Sockets.Where(s => s.Type == SocketType.Input && !s.IsPlaceholder && s.Name != "Flow"))
                {
                    bool hasLink = incoming != null && incoming.Any(l => l.ToSocketId == s.Id);
                    if (hasLink)
                        Emit($"    event.ret.{s.Name} = {ResolveInputValue(node, s.Name, "\"\"")}");
                }
            }
        }

        // Schedule.Recurring header builder. Emits on_interval(seconds) — with an
        // optional SECOND arg (min chat lines that must have arrived since the last
        // fire) ONLY when MinChatLines > 0, and an optional THIRD arg (max number of
        // fires) ONLY when MaxCount > 0. Both `> 0` guards are load-bearing: the
        // defaults "0" MUST keep the shorter form so every existing on_interval
        // golden / hand-authored .phx stays byte-identical.
        //
        // The args are positional, so a MaxCount with no MinChatLines has to fill the
        // middle slot with an explicit 0 — SchedulerService reads a 0 there as "no
        // chat-activity gate", which is exactly the two-arg-absent behaviour.
        private static string BuildOnIntervalTrigger(Node node)
        {
            string sec = node.GetAttr("IntervalSeconds", "60");
            string min = node.GetAttr("MinChatLines", "0");
            string max = node.GetAttr("MaxCount", "0");
            bool hasMin = int.TryParse(min, out var mv) && mv > 0;
            bool hasMax = int.TryParse(max, out var xv) && xv > 0;
            if (hasMax)
                return $"on_interval({sec}, {(hasMin ? min : "0")}, {max})";
            return hasMin
                ? $"on_interval({sec}, {min})"
                : $"on_interval({sec})";
        }

        // Bus.OnMessage with optional Source/Target wildcard filter.
        // Source / Target attributes default to "*" (match-any). When either is
        // narrowed to a concrete value, emit an `if {bus.source} == "X" and
        // {bus.target} == "Y":` guard between the on_bus header and the body
        // so the user's flow only runs when the envelope matches. Bus
        // exposes both bus.source and bus.target in the engine's vars dict
        // (the latter added in this same sweep).
        private void ProcessBusOnMessageEventNode(Node node)
        {
            _nodeResultVars.Clear();
            _varNameCounters.Clear();

            // Must match the template default in NodeRegistry.Templates.Events.cs.
            // D0a moved that default off VISUAL_COMPLETE precisely because Hub-origin
            // broadcasts now reach on_bus for real, so a node left at the default would
            // fire on EVERY visual completion — but this fallback (used when the
            // attribute key is absent entirely, e.g. a graph predating the attribute)
            // was left behind, which re-introduced the same hazard through the back door.
            string evType = node.GetAttr("EventType", "MY_EVENT");
            string source = node.GetAttr("Source", "*");
            string target = node.GetAttr("Target", "*");

            Emit($"on_bus({evType}):");

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(source) && source.Trim() != "*")
                conditions.Add($"{{bus.source}} == \"{EscapeStringLiteral(source.Trim())}\"");
            if (!string.IsNullOrWhiteSpace(target) && target.Trim() != "*")
                conditions.Add($"{{bus.target}} == \"{EscapeStringLiteral(target.Trim())}\"");

            if (conditions.Count > 0)
            {
                Emit($"    if {string.Join(" and ", conditions)}:");
                FollowNamedOutput(node, "Flow", 2);
            }
            else
            {
                FollowNamedOutput(node, "Flow", 1);
            }
        }

        private void ProcessChatMessageEventNode(Node node)
        {
            _nodeResultVars.Clear();
            _varNameCounters.Clear();

            // Platform checkmarks (unified Chat.Message). A legacy
            // Twitch.ChatMessage title defaults YouTube/Kick to OFF; a fresh
            // Chat.Message template seeds all three ON. A Twitch-only selection
            // emits the bare legacy `on_chat:` header — engine-side that means
            // Twitch chat only, which keeps every existing .phx and golden
            // byte-identical. Any other selection emits the platform list the
            // engine gate parses: on_chat(twitch, youtube, kick):
            bool isLegacyTitle = node.Title == "Twitch.ChatMessage";
            static bool ParseBoolAttr(string raw) =>
                raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            bool twitch  = ParseBoolAttr(node.GetAttr("Twitch",  "true"));
            bool youtube = ParseBoolAttr(node.GetAttr("YouTube", isLegacyTitle ? "false" : "true"));
            bool kick    = ParseBoolAttr(node.GetAttr("Kick",    isLegacyTitle ? "false" : "true"));

            if (!twitch && !youtube && !kick)
            {
                _sb.AppendLine($"# WARNING: Chat node (id {IdPrefix(node, 8)}) has no platform enabled — block skipped");
                return;
            }

            if (twitch && !youtube && !kick)
            {
                _sb.AppendLine("on_chat:");
            }
            else
            {
                var platforms = new List<string>(3);
                if (twitch)  platforms.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.Twitch);
                if (youtube) platforms.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.YouTube);
                if (kick)    platforms.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.Kick);
                _sb.AppendLine($"on_chat({string.Join(", ", platforms)}):");
            }

            string cmdsRaw = node.GetAttr("Commands", "");
            if (!string.IsNullOrWhiteSpace(cmdsRaw))
            {
                var cmds = cmdsRaw.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                // The command word is author-edited free text, so it goes through
                // EscapeStringLiteral exactly like the structural siblings in
                // ProcessBusEventNode above — a value carrying a `"` (or a control
                // character) would otherwise close the literal early and emit a
                // malformed guard line into the generated .phx. Nothing validates
                // this attribute anywhere, so the escape is the only defence.
                // Leading `!` is stripped BEFORE escaping: {user.command} is the
                // bare verb, and the strip must not be able to eat an escape byte.
                string condition = string.Join(" or ", cmds.Select(c =>
                    $"{{user.command}} == \"{EscapeStringLiteral(c.TrimStart('!'))}\""));
                Emit($"    if {condition}:");
                FollowNamedOutput(node, "Flow", 2);
            }
            else
            {
                FollowNamedOutput(node, "Flow", 1);
            }
        }

        // Unified stream-lifecycle trigger (Stream.GoingLive / Stream.SessionEnd).
        // Mirrors ProcessChatMessageEventNode: the Twitch / YouTube / Kick bool
        // checkmarks select which platforms' go-live / session-end events enter
        // the block. Unlike on_chat there is no legacy bare form to preserve, so
        // the platform list is ALWAYS explicit — e.g.
        // "on_go_live(twitch, youtube, kick):". The engine gates entry by the
        // firing event's platform (ShouldEnterBlock) and debounces the block per
        // instance (StreamLifecycle.CooldownSeconds). StreamLifecycle owns the
        // header spelling; ChatPlatforms owns the platform-token spelling.
        private void ProcessStreamLifecycleEventNode(Node node)
        {
            _nodeResultVars.Clear();
            _varNameCounters.Clear();

            static bool ParseBoolAttr(string raw) =>
                raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            bool twitch  = ParseBoolAttr(node.GetAttr("Twitch",  "true"));
            bool youtube = ParseBoolAttr(node.GetAttr("YouTube", "true"));
            bool kick    = ParseBoolAttr(node.GetAttr("Kick",    "true"));

            string header = node.Title == "Stream.SessionEnd"
                ? Phoenix.Controls.Shared.Core.StreamLifecycle.SessionEndHeader
                : Phoenix.Controls.Shared.Core.StreamLifecycle.GoLiveHeader;

            if (!twitch && !youtube && !kick)
            {
                _sb.AppendLine($"# WARNING: {node.Title} node (id {IdPrefix(node, 8)}) has no platform enabled — block skipped");
                return;
            }

            var platforms = new List<string>(3);
            if (twitch)  platforms.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.Twitch);
            if (youtube) platforms.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.YouTube);
            if (kick)    platforms.Add(Phoenix.Controls.Shared.Core.ChatPlatforms.Kick);
            _sb.AppendLine($"{header}({string.Join(", ", platforms)}):");

            FollowNamedOutput(node, "Flow", 1);
        }

        // ══════════════════════════════════════════════════════════════════
        // FLOW WALKER
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Follow a specifically named output socket (e.g. "Flow", "Done", "True").</summary>
        private void FollowNamedOutput(Node node, string socketName, int indent)
        {
            var socket = node.Sockets.FirstOrDefault(s =>
                s.Type == SocketType.Output &&
                s.Name.Equals(socketName, StringComparison.OrdinalIgnoreCase));
            if (socket == null) return;
            var next = GetTargetNode(node.Id, socket.Id);
            if (next != null) ProcessNode(next, indent);
        }

        /// <summary>Follow all flow-type output sockets (for nodes with a single continuation).</summary>
        private void FollowFlowOutput(Node node, int indent)
        {
            foreach (var socket in node.Sockets.Where(IsLinearFlowOutput))
            {
                var next = GetTargetNode(node.Id, socket.Id);
                if (next != null) ProcessNode(next, indent);
                return; // Only follow first flow output for linear nodes
            }
        }

        private void ProcessNode(Node node, int indent)
        {
            if (_visitedNodes.Contains(node.Id)) return;
            if (_blockedForBranch.Contains(node.Id)) return;

            // Disabled-node bypass/splice. A node the author switched off emits
            // no command — only a trace comment — and the walk continues
            // downstream, so upstream is spliced straight to downstream as if
            // the node weren't there. The continuation is picked in two tiers:
            //
            //   1. EXACTLY ONE flow output (per SocketTypeHelper.IsFlowPin, the
            //      shared exec-vs-data discriminator) — follow it regardless of
            //      its name. Single-continuation nodes are unambiguous, and the
            //      linear-name set below misses some of them (Flow.Delay's only
            //      output is "Then"), which would otherwise dead-end the whole
            //      downstream chain.
            //   2. Zero or multiple flow outputs — fall back to the FIRST
            //      linear flow output (FollowFlowOutput; the name set stays
            //      untouched because it also scopes merge traversal). Loop
            //      nodes continue via their Completed/Done linear output (the
            //      loop body is simply never entered); multi-output branch
            //      nodes (Logic.If/Branch, Flow.FlipFlop, Switch, Sequence,
            //      Async.Parallel, Flow.Cooldown) expose NO linear output, so
            //      disabling one honestly dead-ends that whole branch — there
            //      is no sensible arm to pick on the author's behalf.
            //
            // The "(disabled)" suffix keeps the comment out of the engine's
            // `# [Title]` node-exec marker shape (StartsWith "# [" AND EndsWith
            // "]"), so a disabled node never flashes in the debug trace. A
            // comment-only (or fully empty) block body is engine-safe:
            // ExecuteBlock skips '#' lines at any indent and FindBlockEnd /
            // ExecuteBlock tolerate empty bodies, so no `pass` filler is needed
            // — a bare `pass` line would only hit the engine's
            // unrecognized-command log path on every run.
            if (IsDisabled(node))
            {
                MarkVisited(node.Id);
                Emit($"{Indent(indent)}# [{node.Title}] (disabled)");
                var flowOuts = node.Sockets
                    .Where(s => s.Type == SocketType.Output && SocketTypeHelper.IsFlowPin(s))
                    .Take(2)
                    .ToList();
                if (flowOuts.Count == 1)
                {
                    var next = GetTargetNode(node.Id, flowOuts[0].Id);
                    if (next != null) ProcessNode(next, indent);
                }
                else
                {
                    FollowFlowOutput(node, indent);
                }
                return;
            }

            MarkVisited(node.Id);

            int savedIndent = _currentIndent;
            _currentIndent = indent;

            string prefix = Indent(indent);

            Emit($"{prefix}# [{node.Title}]");

            // 1. Title-keyed registry — every flow-emitting node has a handler here.
            if (_registry.TryGet(node.Title, out var handler))
            {
                handler.Emit(node, indent, prefix, _ctx);
                _currentIndent = savedIndent;
                return;
            }

            // 2. Pure-data fallback. A pure-data node reached via a flow input
            //    populates its result cache; the value will be consumed by a
            //    downstream ResolveOutputFromNode call. No emit, no follow.
            if (_pureDataCategories.Contains(node.Category))
            {
                if (!_nodeResultVars.ContainsKey(node.Id))
                    _nodeResultVars[node.Id] = ComputeInlineValue(node);
                _currentIndent = savedIndent;
                return;
            }

            // 3. Truly unknown — silent log() if explicitly opted-in, else hard-fail.
            if (_registry.AllowPlaceholderFallback)
            {
                Emit($"{prefix}log(\"[{node.Category}] {node.Title}\")");
                FollowFlowOutput(node, indent);
            }
            else
            {
                throw new System.InvalidOperationException(
                    $"Exporter has no handler for node Title '{node.Title}' " +
                    $"(category '{node.Category}'). Register a handler in " +
                    $"ExporterRegistrations.RegisterAllBuiltIns, mark the category " +
                    $"as pure-data, or set ExporterRegistry.AllowPlaceholderFallback = true " +
                    $"for in-development graphs.");
            }

            _currentIndent = savedIndent;
        }

        // ══════════════════════════════════════════════════════════════════
        // BRANCH CONVERGENCE HELPERS
        // ══════════════════════════════════════════════════════════════════

        private int CountIncomingFlows(Node node)
        {
            EnsureIndices();
            if (!_linksByToNode!.TryGetValue(node.Id, out var incoming)) return 0;
            int total = 0;
            foreach (var s in node.Sockets)
            {
                if (s.Type != SocketType.Input || !SocketTypeHelper.IsFlowPin(s)) continue;
                foreach (var l in incoming)
                    if (l.ToSocketId == s.Id) total++;
            }
            return total;
        }

        // BFS from both branch outputs to find the first node reachable from both sides
        // that also has multiple incoming flow edges (the merge/join point).
        private Node? FindMergePoint(Node branchNode, string outA, string outB)
        {
            var targetA = GetNamedOutputTarget(branchNode, outA);
            var targetB = GetNamedOutputTarget(branchNode, outB);
            if (targetA == null || targetB == null) return null;

            // Collect all flow-reachable node IDs from side A
            var reachableFromA = new HashSet<string>();
            var q = new Queue<string>();
            q.Enqueue(targetA.Id);
            while (q.Count > 0)
            {
                var id = q.Dequeue();
                if (!reachableFromA.Add(id)) continue;
                var n = NodeById(id);
                if (n == null) continue;
                foreach (var s in n.Sockets.Where(IsLinearFlowOutput))
                {
                    if (_linkByFromSocket!.TryGetValue((id, s.Id), out var lnk))
                        q.Enqueue(lnk.ToNodeId);
                }
            }

            // BFS from side B; the first node also in A with >1 incoming flows is the merge point
            var visitedB = new HashSet<string>();
            var qB = new Queue<string>();
            qB.Enqueue(targetB.Id);
            while (qB.Count > 0)
            {
                var id = qB.Dequeue();
                if (!visitedB.Add(id)) continue;
                var n = NodeById(id);
                if (n == null) continue;
                if (reachableFromA.Contains(id) && CountIncomingFlows(n) > 1
                    && id != targetA.Id && id != targetB.Id)
                    return n;
                foreach (var s in n.Sockets.Where(IsLinearFlowOutput))
                {
                    if (_linkByFromSocket!.TryGetValue((id, s.Id), out var lnk))
                        qB.Enqueue(lnk.ToNodeId);
                }
            }
            return null;
        }

        private void EmitBranch(Node branchNode, string trueOut, string falseOut,
                                string prefix, int indent, string truePfx, string? elsePfx)
        {
            var merge = FindMergePoint(branchNode, trueOut, falseOut);

            // Guarantee the merge node is unblocked even if
            // a branch arm throws, so a later export pass / re-entry doesn't see a
            // stale _blockedForBranch entry that silently swallows the merge node.
            // The Remove() lives in the finally; ProcessNode(merge) stays on the
            // normal path so output is unchanged.
            if (merge != null) _blockedForBranch.Add(merge.Id);
            try
            {
                // Isolate visited state AND resolved-result cache so the False branch can
                // independently re-visit nodes that the True branch already processed.
                // Without restoring _nodeResultVars too, ResolveInputValue's "prefer
                // resolved-anywhere" rule reuses the True branch's cached upstream
                // resolution on the False side, producing incorrect scripts.
                //
                // Isolation is delta-based rather than full-snapshot: arms only ever
                // ADD to _visitedNodes (tracked in insertion order via _visitedOrder)
                // and add/overwrite in _nodeResultVars — nothing net-removes from
                // either during an arm, so undoing exactly the True arm's delta is
                // equivalent to rebuilding both containers from pre-branch copies.
                int visitedMark = _visitedOrder.Count;
                var resultsBefore = new Dictionary<string, string>(_nodeResultVars);

                FollowNamedOutput(branchNode, trueOut, indent + 1);

                // Capture the True arm's visited additions + result-cache delta,
                // then restore the pre-branch state for False.
                var trueVisited = _visitedOrder.GetRange(visitedMark, _visitedOrder.Count - visitedMark);
                Dictionary<string, string>? trueResults = null;
                foreach (var kv in _nodeResultVars)
                {
                    if (!resultsBefore.TryGetValue(kv.Key, out var prev)
                        || !string.Equals(prev, kv.Value, StringComparison.Ordinal))
                        (trueResults ??= new Dictionary<string, string>())[kv.Key] = kv.Value;
                }

                foreach (var id in trueVisited) _visitedNodes.Remove(id);
                _visitedOrder.RemoveRange(visitedMark, _visitedOrder.Count - visitedMark);
                if (trueResults != null)
                {
                    foreach (var kv in trueResults)
                    {
                        if (resultsBefore.TryGetValue(kv.Key, out var prev)) _nodeResultVars[kv.Key] = prev;
                        else _nodeResultVars.Remove(kv.Key);
                    }
                }

                var falseTarget = GetNamedOutputTarget(branchNode, falseOut);
                if (falseTarget != null)
                {
                    Emit($"{prefix}{elsePfx ?? "else"}:");
                    ProcessNode(falseTarget, indent + 1);
                }

                // Union both arms' state so nothing runs again after the branch
                // completes. Mirrors the historical "overwrite everything with the
                // after-True map" union exactly: after-True == resultsBefore +
                // trueResults, so re-applying both reproduces it key-for-key —
                // including reverting a False-arm overwrite of a pre-branch entry
                // (True-side state wins at the join) — while False-arm pure
                // additions, absent from both, survive untouched.
                foreach (var id in trueVisited)
                    if (_visitedNodes.Add(id)) _visitedOrder.Add(id);
                foreach (var kv in resultsBefore) _nodeResultVars[kv.Key] = kv.Value;
                if (trueResults != null)
                    foreach (var kv in trueResults) _nodeResultVars[kv.Key] = kv.Value;
            }
            finally
            {
                if (merge != null) _blockedForBranch.Remove(merge.Id);
            }

            if (merge != null)
                ProcessNode(merge, indent);
        }


        // ══════════════════════════════════════════════════════════════════
        // GRAPH TRAVERSAL HELPERS
        // ══════════════════════════════════════════════════════════════════

        private string ResolveInputValue(Node node, string socketName, string fallback)
        {
            var inputSocket = node.Sockets.FirstOrDefault(s =>
                s.Name.Equals(socketName, StringComparison.OrdinalIgnoreCase) && s.Type == SocketType.Input);
            if (inputSocket == null)
                return node.GetAttr(socketName, fallback);

            // Use JSON insertion order. System.Text.Json preserves array order
            // on deserialization, so graph.Links iteration is already deterministic
            // for the same .phxg file. Long-standing tests (ExporterBranchMergeTests)
            // depend on the user's drop order being the tiebreaker; sorting by
            // FromNodeId/FromSocketId would shuffle multi-link selection arbitrarily.
            //
            // Use the pre-built _linksByToNode index (grouped by ToNodeId,
            // built preserving _graph.Links order) instead of a linear O(all_links)
            // scan. ResolveInputValue is a hot path (50+ call sites: Math/Text/Array/
            // Convert/Logic), so the full-graph traversal dominated dense-graph
            // exports. Filter the per-node incoming list by ToSocketId — order is
            // preserved, so the drop-order tiebreaker above is unchanged.
            EnsureIndices();
            if (!_linksByToNode!.TryGetValue(node.Id, out var incomingLinks))
                return InlineLiteralOrFallback(node, inputSocket, socketName, fallback);
            var links = incomingLinks
                .Where(l => l.ToSocketId == inputSocket.Id)
                .ToList();
            if (links.Count == 0)
                return InlineLiteralOrFallback(node, inputSocket, socketName, fallback);

            // Surface multi-link inputs as a runtime warning so the user notices
            // their graph is ambiguous. Flow inputs legitimately accept multiple
            // upstream connections (merge points); only warn for non-flow data inputs.
            if (links.Count > 1 && !SocketTypeHelper.IsFlowPin(inputSocket))
            {
                AddRuntimeWarning(
                    $"Input '{node.Title}.{inputSocket.Name}' has {links.Count} inbound links — exporter picked the first in drop order. Remove the extra links to make intent explicit.",
                    node.Id);
            }

            // Prefer: source visited in current branch > source resolved anywhere > first stable
            var link = links.FirstOrDefault(l => _visitedNodes.Contains(l.FromNodeId))
                       ?? links.FirstOrDefault(l => _nodeResultVars.ContainsKey(l.FromNodeId))
                       ?? links.First();

            var src = NodeById(link.FromNodeId);
            var srcSocket = src?.Sockets.FirstOrDefault(s => s.Id == link.FromSocketId);
            if (src == null || srcSocket == null) return fallback;

            // Disabled data provider — treat the consumer's socket as UNWIRED so
            // it falls back to its inline pill / socket default instead of
            // evaluating (and possibly hoisting) a node the author switched off.
            // Data-side mirror of the flow splice in ProcessNode; every handler
            // arg (Resolve/Materialize) and pure-data chain (ComputeInlineValue)
            // funnels through here, so this one gate covers them all.
            if (IsDisabled(src))
                return InlineLiteralOrFallback(node, inputSocket, socketName, fallback);

            return ResolveOutputFromNode(src, srcSocket);
        }

        // Resolve an UNWIRED input socket to either its inline-
        // pill literal or the caller's fallback — quoting a String-typed literal ONLY
        // when its raw text contains a character that would break the engine's
        // quote-/paren-aware command-arg splitter if emitted bare. Without this, an
        // inline message like `Hello, world` emitted as `twitch.send_chat(Hello, world)`
        // was split by SplitArgs on the comma into TWO args, so only the text BEFORE
        // the first comma reached Twitch (Majo). Wrapping it —
        // `twitch.send_chat("Hello, world")` — keeps it a single argument and round-
        // trips losslessly through the engine's quote-strip/unescape (the SAME
        // contract Value.String already relies on).
        //
        // The trigger is deliberately NARROW so this stays a surgical fix:
        //   * Only String sockets (numbers / bools / Any are emitted bare — Math /
        //     Logic inlining and numeric assignments are untouched).
        //   * Only an actual inline attribute value (not the caller's fallback, which
        //     already carries its own quoting where needed).
        //   * Skipped for a CALLABLE expression a user typed into a String pill (e.g.
        //     `math.add(1, 2)`) — those must stay bare so the exporter still hoists +
        //     evaluates them.
        //   * Skipped for an already-quoted literal.
        //   * Skipped unless the value actually contains a comma / double-quote /
        //     newline — so plain literals (`LogicExecution`, `42`, single-word
        //     messages) emit byte-for-byte as before (no golden churn, no behavioural
        //     change in assignment / inline contexts).
        private static string InlineLiteralOrFallback(Node node, Socket? inputSocket, string socketName, string fallback)
        {
            string raw = node.GetAttr(socketName, fallback);
            if (inputSocket?.DataType == SocketDataType.String
                && node.Attributes != null
                && node.Attributes.TryGetValue(socketName, out var inline)
                && !string.IsNullOrEmpty(inline)
                && ReferenceEquals(raw, inline)        // raw came from the attribute, not the fallback
                && ArgLiteralNeedsQuoting(inline)
                && !IsCallableExpression(inline.Trim()))
            {
                var trimmed = inline.Trim();
                bool alreadyQuoted = trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"';
                if (!alreadyQuoted)
                    return $"\"{QuoteSafeLiteralBody(EscapeStringLiteral(inline))}\"";
            }
            return raw;
        }

        // Mirror of ScriptEngine.CallShapeRegex — the classifier the ENGINE uses to
        // decide "this argument is itself a command call, execute it". It is LOOSER
        // than the exporter's own CallableRegex (which is lowercase-anchored and
        // requires the paren to follow the identifier immediately), so a literal the
        // exporter classifies as DATA can still be classified as CODE downstream.
        // Everything in that gap must be emitted quoted — see ArgLiteralNeedsQuoting.
        private static readonly Regex EngineCallShapeRegex =
            new(@"^[\w\.]+\s*\(.*\)$", RegexOptions.Compiled | RegexOptions.Singleline);

        // True when a bare emission of this literal would break the engine's command-
        // arg parse: a comma splits the arg, a double-quote toggles its quote state,
        // and a newline / CR would split the .phx line itself. EscapeStringLiteral
        // neutralises the comma and the line breaks inside a "..." literal; the quote
        // needs QuoteSafeLiteralBody on top, because SplitArgs is escape-blind and a
        // plain `\"` in the body would just move the parity break rather than remove it.
        //
        // Also true for a literal the ENGINE would mistake for a nested command call.
        // An inline pill like `Welcome (say hi in chat)` contains none of the four
        // break characters, so it used to emit bare —
        // `twitch.send_chat(Welcome (say hi in chat))` — and ScriptEngine's looser
        // CallShapeRegex then recursed on it, found no command named `welcome`, and
        // substituted the EMPTY STRING, so the message was never sent. Callers gate
        // this helper behind their own IsCallableExpression check, so a deliberate
        // call-shaped pill (`math.add(1, 2)`) never reaches here and still emits bare.
        private static bool ArgLiteralNeedsQuoting(string s)
        {
            foreach (char c in s)
                if (c == ',' || c == '"' || c == '\n' || c == '\r')
                    return true;
            return EngineCallShapeRegex.IsMatch(s.Trim());
        }

        /// <summary>
        /// Like ResolveInputValue but if the result is a function call, pre-emits an assignment
        /// to a temp global var and returns a {var} reference. This ensures function calls are
        /// evaluated before being used in positions where the engine can't evaluate them inline
        /// (e.g. condition operands, bare variable matching, DB arguments).
        /// </summary>
        private string MaterializeInput(Node node, string socketName, string fallback)
        {
            string val = ResolveInputValue(node, socketName, fallback);
            // Strict callable detection. The previous Contains("(") heuristic
            // misfired on user-typed strings like "(foo)" (which legitimately
            // contain a paren but are NOT a function call). CallableRegex matches
            // only `name(` shaped expressions.
            if (CallableRegex.IsMatch(val))
            {
                string safeSocket = socketName.ToLower().Replace(" ", "_");
                string preVar = $"global._pre_{IdPrefix(node, 6)}_{safeSocket}";
                Emit($"{Indent(_currentIndent)}{preVar} = {val}");
                return $"{{{preVar}}}";
            }
            return val;
        }

        /// <summary>
        /// True when <paramref name="expr"/> is a name the engine can actually address
        /// as a static <c>{state.NAME}</c> token — i.e. a compile-time literal whose
        /// characters all survive <c>ScriptEngine.VarRefRegex</c> (<c>\{([\$\w\.]+)\}</c>).
        /// Surrounding quotes are peeled first, so the auto-quoted pill <c>"phase"</c>
        /// and the bare attribute <c>phase</c> both qualify. A <c>{var.x}</c> reference,
        /// an inline call, or a name carrying spaces/punctuation does NOT — those need
        /// the runtime probe in <see cref="HoistDynamicStateRead"/>.
        /// </summary>
        internal static bool TryStaticStateName(string expr, out string name)
        {
            name = string.Empty;
            if (string.IsNullOrWhiteSpace(expr)) return false;
            string t = expr.Trim();
            if (IsSingleQuotedLiteral(t)) t = t[1..^1];
            if (t.Length == 0) return false;
            foreach (char c in t)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '$')
                    return false;
            name = t;
            return true;
        }

        /// <summary>
        /// Emit a runtime state read for a state NAME that is only known at runtime
        /// (a wired Var.Get / Text.* / Math.* source), and return the token that holds
        /// the value.
        /// </summary>
        /// <remarks>
        /// There is no <c>{state.{expr}}</c> indirection in the engine — SubstituteVars
        /// is single-pass — and <c>state.get</c> cannot be inlined as a value expression
        /// either, because the Hub handler returns null and publishes the value on
        /// <c>result.state_value</c>. So the read has to be hoisted as two real lines
        /// ahead of the consumer, exactly like <see cref="MaterializeInput"/> hoists a
        /// call expression. The result is cached per node id so a second consumer of the
        /// same State.Get reuses the probe instead of re-reading.
        /// </remarks>
        private string HoistDynamicStateRead(Node src, string nameExpr)
        {
            if (_nodeResultVars.TryGetValue(src.Id, out var cached)) return cached;

            string probeVar = $"global._stateget_{IdPrefix(src, 6)}";
            string indentSp = Indent(_currentIndent);
            Emit($"{indentSp}state.get({nameExpr})");
            Emit($"{indentSp}{probeVar} = {{result.state_value}}");
            string token = $"{{{probeVar}}}";
            _nodeResultVars[src.Id] = token;
            return token;
        }

        // Inline-or-hoist resolver for the pure-data inline pattern shared by
        // Math/Text/Logic/Collections/Convert + DB.RowCount/GetCell/GetColumn +
        // HTTP.ParseJson/Queue.Length. Single-consumer: inline ComputeInlineValue.
        // Multi-consumer: emit a `var = ComputeInlineValue` hoist + `# [Title]`
        // trace comment, then return the var reference. The cached path keeps
        // re-entrant resolves stable across the same node id.
        private string ResolvePureData(Node src)
        {
            if (_visitedNodes.Contains(src.Id))
                return _nodeResultVars.TryGetValue(src.Id, out var cached) ? cached : ComputeInlineValue(src);

            int consumers = OutputConsumerCount(src);
            if (consumers <= 1)
            {
                MarkVisited(src.Id);
                string inlineVal = ComputeInlineValue(src);
                _nodeResultVars[src.Id] = inlineVal;
                return inlineVal;
            }
            string varName = LocalVarName(src);
            string hoistVal = ComputeInlineValue(src);
            string indentSp = Indent(_currentIndent);
            Emit($"{indentSp}# [{src.Title}]");
            Emit($"{indentSp}{varName} = {hoistVal}");
            MarkVisited(src.Id);
            _nodeResultVars[src.Id] = $"{{{varName}}}";
            return $"{{{varName}}}";
        }

        // Giveaway.* result-var helpers — shared by ResolveOutputFromNode (read
        // side, above) and the giveaway exporter handlers in
        // ExporterRegistry.Giveaway.cs (write side) so both compute the same
        // "_gw_<id6>" base and the same per-socket key. The Hub command writes
        // each value output under "{base}_<key>" via SetLocalResultVar.
        internal static string GiveawayResultBase(Node n)
        {
            string compact = n.Id.Replace("-", "");
            return "_gw_" + (compact.Length >= 6 ? compact[..6] : compact);
        }

        internal static string GiveawaySocketKey(string socketName)
        {
            var sb = new System.Text.StringBuilder(socketName.Length);
            foreach (char c in socketName)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>Per-node result-var base for the multi-output hoisted reads
        /// (Song.Current / Song.UpNext / Poll.Status / DB.Top). One hoisted read backs
        /// every output socket, so each node needs its own private base — mirroring
        /// GiveawayResultBase's "_gw_&lt;id6&gt;" scheme so two nodes in one script can't
        /// clobber each other's outputs — and the prefix identifies which node it came
        /// from when reading the emitted .phx.</summary>
        internal static string SongReadResultBase(Node n, string prefix)
        {
            string compact = n.Id.Replace("-", "");
            return prefix + (compact.Length >= 6 ? compact[..6] : compact);
        }

        private string ResolveOutputFromNode(Node src, Socket srcSocket)
        {
            // Live-process template mode — Process.Entry param outputs are the
            // instance-scoped start params injected by process.start, read in the
            // template body as {param.<name>}. (Outside template mode, Process.Entry
            // rides the legacy _macroContextId slot scheme below — the deprecated
            // inline Process.Spawn path.) Sanitize so a param renamed to "User Name"
            // produces {param.user_name} matching the process.start write side.
            if (_processTemplateMode && src.Title == "Process.Entry"
                && srcSocket.Type == SocketType.Output
                && srcSocket.Name != "Flow" && !srcSocket.IsPlaceholder)
            {
                return $"{{param.{SanitizeIdentifier(srcSocket.Name)}}}";
            }

            // Macro.Entry outputs resolve to the global variables bound by the parent Macro.Call
            // expansion. _macroContextId is now the FULL slot-prefix (set by MacroCallHandler) of
            // shape "_macro_<stableMacroId>_<callSiteId>" — both sides of the contract use it
            // verbatim so per-call-site parameter slots don't collide with sibling invocations.
            // Process.Entry rides the same machinery — ProcessSpawnHandler sets
            // _macroContextId to "_process_<stableProcessId>_<callSiteId>" and
            // emits identical `global.<context>_<arg> = <value>` assignments,
            // so the read side here is shape-agnostic.
            if ((src.Title == "Macro.Entry" || src.Title == "Process.Entry")
                && srcSocket.Type == SocketType.Output
                && srcSocket.Name != "Flow" && !srcSocket.IsPlaceholder && !string.IsNullOrEmpty(_macroContextId))
            {
                // Sanitize so a socket renamed to e.g. "User Name" produces the
                // same identifier on both the read side here and the write side
                // in MacroCallHandler / ProcessSpawnHandler — otherwise the
                // emitted script contains raw spaces and is unparseable.
                return $"{{global.{_macroContextId}_{SanitizeIdentifier(srcSocket.Name)}}}";
            }

            // Macro.Call return outputs read the per-call-site return slot that
            // MacroExitHandler writes inside the inlined body. The slot shape
            // must mirror MacroCallHandler's slotPrefix composition exactly:
            //   "_macro_<dash-stripped MacroId>_<IdPrefix(call, 12)>_ret_<name>"
            // Gated on the macro actually existing in the graph — mirroring the
            // write side, which inlines no body (and writes no slot) for an
            // unbound (MacroId="") or deleted-but-still-referenced macro. Those
            // fall through to the generic dead-literal fallback below.
            if (src.Title == "Macro.Call" && srcSocket.Type == SocketType.Output
                && srcSocket.Name != "Flow" && !srcSocket.IsPlaceholder)
            {
                string rawMacroId = src.GetAttr("MacroId", "");
                // `_graph.Macros` can be null on an unhealed programmatic graph
                // (explicit `"Macros": null`) — and this arm is reachable for a
                // flow-disconnected, data-only-wired Macro.Call whose flow handler
                // (which would take the not-found skip) never runs.
                if (!string.IsNullOrEmpty(rawMacroId)
                    && _graph.Macros is not null
                    && _graph.Macros.Any(m => m.MacroId == rawMacroId))
                    return $"{{global._macro_{rawMacroId.Replace("-", "")}_{IdPrefix(src)}_ret_{SanitizeIdentifier(srcSocket.Name)}}}";
            }

            if (src.Category == "Events")
            {
                // Event.Trigger return outputs carry values handed back by the executor
                if (src.Title == "Event.Trigger" && srcSocket.Type == SocketType.Output
                    && srcSocket.Name != "Flow" && !srcSocket.IsPlaceholder)
                    return $"{{event.ret.{srcSocket.Name}}}";

                // Event.Executor outputs map to the event's named arguments
                if (src.Title == "Event.Executor" && srcSocket.Type == SocketType.Output
                    && srcSocket.Name != "Flow" && !srcSocket.IsPlaceholder)
                    return $"{{event.arg.{srcSocket.Name}}}";

                // Chat-node outputs map to runtime-provided standard vars. Covers
                // the unified Chat.Message node and the legacy Twitch.ChatMessage
                // title. The $twitch_user_ cache-var NAME is deliberately kept as
                // is — it doesn't depend on the title, so a migrated node's
                // emission stays byte-identical to its pre-migration export.
                if (src.Title == "Twitch.ChatMessage" || src.Title == "Chat.Message")
                {
                    if (srcSocket.Name == "Platform") return "{user.platform}";
                    // MessageId → {event.message_id} (BuildChatVars binds it from
                    // ChatMessage.MessageId). Explicit mapping required — otherwise
                    // it falls through to the generic {event.<name>} default below,
                    // which would lowercase-collapse to {event.messageid}.
                    if (srcSocket.Name == "MessageId") return "{event.message_id}";
                    if (srcSocket.Name == "Command") return "{user.command}";
                    if (srcSocket.Name == "Args")    return "{user.args}";
                    if (srcSocket.Name == "IsCommand") return "{event.iscommand}";
                    // Chatter-role / metadata outputs. The engine binds these in
                    // BuildChatVars; map each socket name to its {user.*} var here so the
                    // new outputs resolve correctly instead of falling through to the
                    // generic {event.<name>} default below.
                    if (srcSocket.Name == "IsMod")         return "{user.is_mod}";
                    if (srcSocket.Name == "IsSub")         return "{user.is_sub}";
                    if (srcSocket.Name == "IsBroadcaster") return "{user.is_broadcaster}";
                    if (srcSocket.Name == "IsVip")         return "{user.is_vip}";
                    // IsRegular → {user.is_regular} (User-Management Regular-group
                    // membership; BuildChatVars binds it beside the role flags).
                    if (srcSocket.Name == "IsRegular")     return "{user.is_regular}";
                    if (srcSocket.Name == "SubMonths")     return "{user.sub_months}";
                    if (srcSocket.Name == "ColorHex")      return "{user.color_hex}";
                    if (srcSocket.Name == "User")
                    {
                        // Emit array.make() once and cache — pipe-delimited literals break
                        // all comma-based array operations (array.get, for_each, array.length, etc.)
                        string cacheKey = src.Id + "_user";
                        if (!_nodeResultVars.TryGetValue(cacheKey, out var cachedUserVar))
                        {
                            string varName = $"$twitch_user_{IdPrefix(src, 6)}";
                            Emit($"{Indent(_currentIndent)}{varName} = array.make({{user.name}}, {{user.is_mod}}, {{user.is_sub}}, {{user.is_vip}}, {{user.is_broadcaster}}, {{user.color_hex}}, {{user.sub_months}})");
                            _nodeResultVars[cacheKey] = $"{{{varName}}}";
                            cachedUserVar = $"{{{varName}}}";
                        }
                        return cachedUserVar;
                    }
                }
                // WS.Server's Body / Path map to event.body / event.path
                // (set by ScriptManager.ExecuteOnWebSocketScriptsAsync). Fall through
                // to the generic mapping for any other socket name on the node.
                if (src.Title == "WS.Server")
                {
                    if (srcSocket.Name == "Body") return "{event.body}";
                    if (srcSocket.Name == "Path") return "{event.path}";
                }
                // Stream.GoingLive / Stream.SessionEnd outputs. Platform → the
                // shared {user.platform} token (bound by BuildGenericEventVars from
                // the event-type prefix — same pin semantics as Chat.Message's
                // Platform). Title / Category → {event.title} / {event.category},
                // bound by the Hub dispatch (ApplyStreamLifecycleVars). Explicit so
                // Platform doesn't fall through to the generic {event.platform}
                // default, which nothing binds.
                if (src.Title == "Stream.GoingLive" || src.Title == "Stream.SessionEnd")
                {
                    if (srcSocket.Name == "Platform") return "{user.platform}";
                    if (srcSocket.Name == "Title")    return "{event.title}";
                    if (srcSocket.Name == "Category") return "{event.category}";
                }
                // System.Hotkey's Combo socket maps to event.combo
                // (set by ScriptManager.ExecuteOnHotkeyScriptsAsync alongside
                // hotkey.combo for clarity).
                if (src.Title == "System.Hotkey" && srcSocket.Name == "Combo")
                    return "{event.combo}";
                // System.Clipboard's Text socket maps to event.text
                // (set by ScriptManager.ExecuteOnClipboardScriptsAsync alongside
                // clipboard.text).
                if (src.Title == "System.Clipboard" && srcSocket.Name == "Text")
                    return "{event.text}";
                // OBS.Event's EventData socket maps to event.data (the
                // raw OBS WS v5 eventData JSON object as a string). Scripts
                // can run http.parse_json on it or substring-match the raw
                // text. obs.event_type also surfaces the matched event name
                // so a handler binding to a broad subscription mask can branch.
                if (src.Title == "OBS.Event" && srcSocket.Name == "EventData")
                    return "{event.data}";
                // State.OnChange's Name / OldValue / NewValue outputs map to the
                // state.* tokens bound by ScriptManager on a state-change run
                // (state.name / state.new_value / state.old_value). Without this
                // guard they fell through to the generic {event.<socket>} form
                // ({event.oldvalue} etc.) which nothing binds, so a wired output
                // pin leaked the raw token downstream.
                if (src.Title == "State.OnChange")
                {
                    switch (srcSocket.Name)
                    {
                        case "Name":     return "{state.name}";
                        case "OldValue": return "{state.old_value}";
                        case "NewValue": return "{state.new_value}";
                    }
                }
                // Counter.OnChanged's Counter / Count outputs map to the event.*
                // tokens CountersService raises (event.counter / event.count).
                // Without this guard "Count" would hit the generic switch below and
                // collapse to {user.count} — which nothing binds on a counter-change
                // run — instead of {event.count}. (Counter alone would fall through
                // to {event.counter} correctly, but map both here for clarity.)
                if (src.Title == "Counter.OnChanged")
                {
                    switch (srcSocket.Name)
                    {
                        case "Counter": return "{event.counter}";
                        case "Count":   return "{event.count}";
                    }
                }
                // Automod.OnViolation's outputs map to the event.* tokens AutomodService
                // raises. User / Message MUST be mapped here or they'd hit the generic
                // switch below and collapse to {user.name} / {user.message} — which
                // nothing binds on a violation run — instead of {event.user} /
                // {event.message}. Rule/Action/Reason would fall through to
                // {event.<name>} correctly, but map all five here for clarity.
                if (src.Title == "Automod.OnViolation")
                {
                    switch (srcSocket.Name)
                    {
                        case "User":    return "{event.user}";
                        case "Rule":    return "{event.rule}";
                        case "Action":  return "{event.action}";
                        case "Reason":  return "{event.reason}";
                        case "Message": return "{event.message}";
                    }
                }
                // Quote.OnAdded's outputs map to the event.* tokens QuotesService raises
                // (event.number / event.text / event.name). These would each fall through
                // to the generic {event.<name>} form below correctly today, but map them
                // explicitly (like Counter.OnChanged) so a future generic-switch addition
                // for "Name"/"Text"/"Number" can't silently redirect them.
                if (src.Title == "Quote.OnAdded")
                {
                    switch (srcSocket.Name)
                    {
                        case "Number": return "{event.number}";
                        case "Text":   return "{event.text}";
                        case "Name":   return "{event.name}";
                    }
                }
                // Command.OnCustom's outputs map to the event.* tokens CustomCommandsService
                // raises (event.command / event.user / event.args). "User" MUST be mapped
                // here or it'd hit the generic switch below and collapse to {user.name} —
                // which nothing binds on a custom-command run — instead of {event.user}.
                // Command / Args would fall through to {event.<name>} correctly, but map all
                // three here for clarity.
                if (src.Title == "Command.OnCustom")
                {
                    switch (srcSocket.Name)
                    {
                        case "Command": return "{event.command}";
                        case "User":    return "{event.user}";
                        case "Args":    return "{event.args}";
                    }
                }
                // Song.On*'s outputs map to the event.* tokens SongRequestService raises.
                // The arm is MANDATORY, not decorative: VideoId / DurationSeconds /
                // SkippedBy would each hit the generic switch below and emit
                // {event.videoid} / {event.durationseconds} / {event.skippedby} — the
                // lower-cased socket name — while the runtime binds event.video_id /
                // event.duration_seconds / event.skipped_by. That is the multi-word-token
                // collapse the catalog comment further down describes, and the symptom is
                // a wired pin bound to nothing with no error anywhere. Title / Requester /
                // Position are mapped alongside them for the same immunity
                // Counter.OnChanged documents. All three roots share one arm because they
                // share the base token set.
                if (src.Title is "Song.OnQueued" or "Song.OnPlay" or "Song.OnSkip")
                {
                    switch (srcSocket.Name)
                    {
                        case "Title":           return "{event.title}";
                        case "Requester":       return "{event.requester}";
                        case "VideoId":         return "{event.video_id}";
                        case "Position":        return "{event.position}";
                        case "DurationSeconds": return "{event.duration_seconds}";
                        case "SkippedBy":       return "{event.skipped_by}";
                    }
                }
                // Poll.On*'s outputs map to the event.* tokens PollsService raises. The arm
                // is MANDATORY for the same reason Song.On*'s is: TotalVotes / WinnerVotes /
                // WinnerCount / DurationSeconds would each hit the generic switch below and
                // emit {event.totalvotes} / {event.winnervotes} / {event.winnercount} /
                // {event.durationseconds} — the lower-cased socket name — while the runtime
                // binds event.total_votes / event.winner_votes / event.winner_count /
                // event.duration_seconds. Title / Winner / Options / Outcome / Pot /
                // Winners / Betting / Currency are mapped alongside them for the same
                // immunity Counter.OnChanged documents. All three roots share one arm
                // because they share the base token set.
                if (src.Title is "Poll.OnOpened" or "Poll.OnClosed" or "Poll.OnSettled")
                {
                    switch (srcSocket.Name)
                    {
                        case "Title":           return "{event.title}";
                        case "Options":         return "{event.options}";
                        case "DurationSeconds": return "{event.duration_seconds}";
                        case "Betting":         return "{event.betting}";
                        case "Winner":          return "{event.winner}";
                        case "WinnerVotes":     return "{event.winner_votes}";
                        case "TotalVotes":      return "{event.total_votes}";
                        case "Outcome":         return "{event.outcome}";
                        case "Pot":             return "{event.pot}";
                        case "Winners":         return "{event.winners}";
                        case "WinnerCount":     return "{event.winner_count}";
                        case "Currency":        return "{event.currency}";
                    }
                }
                // Rank.OnRankUp's outputs map to the event.* tokens RanksService raises.
                // The arm is MANDATORY for TWO of them: "User" would hit the generic switch
                // below and collapse to {user.name} — which nothing binds on a rank-up run —
                // and "RankName" would lower-case to {event.rankname}, which is in fact what
                // the runtime binds, but only because the raise site was written to match
                // this arm rather than the other way round. Value / Next are mapped
                // alongside for the same immunity Counter.OnChanged documents: a future
                // addition to the generic override list must not be able to silently
                // redirect them.
                //
                // ★ There is deliberately no socket called "Rank" or "Level". "Rank" means
                // leaderboard POSITION in five other places in this product, and "Level" is
                // already {event.level} on the four hype-train events — an output by either
                // name would read as a different thing on the same canvas.
                //
                // Login is a REAL output socket rather than a token a graph author has to
                // know to type: User is the display name, every store this band touches is
                // keyed on the login, and "there is a hidden var for that" is not a design
                // — it is a trap that only fires for viewers whose two spellings differ.
                if (src.Title == "Rank.OnRankUp")
                {
                    switch (srcSocket.Name)
                    {
                        case "User":     return "{event.user}";
                        case "Login":    return "{event.login}";
                        case "RankName": return "{event.rankname}";
                        case "Value":    return "{event.value}";
                        case "Next":     return "{event.next}";
                    }
                }
                // Soundboard.OnPlay's outputs map to the event.* tokens
                // SoundboardService.RaisePlayed raises. The arm is MANDATORY for "User":
                // the generic switch below rewrites that name to {user.name}, which nothing
                // binds on a soundboard run, so the pin would export empty forever. Command
                // and Clip are mapped alongside for the immunity Counter.OnChanged
                // documents — neither collides today, and neither should be able to start
                // colliding because someone widened the generic list.
                if (src.Title == "Soundboard.OnPlay")
                {
                    switch (srcSocket.Name)
                    {
                        case "Command": return "{event.command}";
                        case "User":    return "{event.user}";
                        case "Clip":    return "{event.clip}";
                    }
                }
                // Queue.OnChanged's outputs map to the event.* tokens NamedQueueService
                // raises (event.queue / event.entry / event.action / event.length). The
                // arm is MANDATORY for "Length": the generic switch below has no "Length"
                // override today, but "Count" and friends prove how easily one gets added,
                // and Queue / Entry / Action are mapped alongside it for the same
                // immunity Counter.OnChanged documents.
                if (src.Title == "Queue.OnChanged")
                {
                    switch (srcSocket.Name)
                    {
                        case "Queue":  return "{event.queue}";
                        case "Entry":  return "{event.entry}";
                        case "Action": return "{event.action}";
                        case "Length": return "{event.length}";
                    }
                }
                // User.OnFirstMessage's outputs map to the event.* tokens
                // UserManagementService raises. "User" and "Message" MUST be mapped here
                // or the generic switch below collapses them to {user.name} / {user.message}
                // — and on a tool re-raise NOTHING binds those, because the raise ships
                // presetVars and never the chat-path var block. Platform / FirstEver would
                // fall through to {event.<name>} correctly; mapped for clarity.
                if (src.Title == "User.OnFirstMessage")
                {
                    switch (srcSocket.Name)
                    {
                        case "User":      return "{event.user}";
                        case "Message":   return "{event.message}";
                        case "Platform":  return "{event.platform}";
                        case "FirstEver": return "{event.first_ever}";
                    }
                }
                // Loyalty.On* event roots map each output socket to the EXACT
                // {event.*} token the matching RaiseScript(...) call sets in
                // LoyaltyService (Earn.cs: OnEarn/OnPayout, LoyaltyService.cs:
                // OnRedeem, Games.cs: OnRaffle). Without this arm several sockets
                // collapse via the generic switch below: "Reward" → {user.reward}
                // (the OnRedeem Reward-pin-empty bug — the redeem raise sets
                // event.reward, never user.reward), "Count" → {user.count}, and
                // "User" → {user.name}. None of {user.reward}/{user.count} is bound
                // on a loyalty run, so a wired pin would leak an empty token.
                if (src.Title is "Loyalty.OnEarn" or "Loyalty.OnPayout"
                    or "Loyalty.OnRedeem" or "Loyalty.OnRaffle")
                {
                    switch (srcSocket.Name)
                    {
                        case "User":     return "{event.user}";
                        case "Amount":   return "{event.amount}";
                        case "Reason":   return "{event.reason}";
                        case "Reward":   return "{event.reward}";
                        case "Cost":     return "{event.cost}";
                        case "Count":    return "{event.count}";
                        case "Total":    return "{event.total}";
                        case "Currency": return "{event.currency}";
                        case "Winners":  return "{event.winners}";
                        case "Pot":      return "{event.pot}";
                        case "Entrants": return "{event.entrants}";
                    }
                }

                // ── Catalog-declared events: the catalog IS the token contract ──
                // Every PlatformEventCatalog entry already carries an explicit
                // socket→token mapping, and that same mapping is what ScriptManager
                // writes into vars at runtime. Reading it here means the exporter
                // and the runtime share ONE source of truth instead of two that
                // happen to agree.
                //
                // They only *happened* to agree before: the generic switch below
                // ends in `event.<lowercased socket name>`, which silently
                // collapses any multi-word token — a socket named AmountCents would
                // emit {event.amountcents} while the runtime wrote
                // event.amount_cents, leaving the pin bound to nothing. That is the
                // same class of bug the Chat.Message MessageId guard above exists
                // to paper over, one hand-written guard at a time.
                //
                // Behaviour-identical for all pre-existing catalog entries: their
                // socket names resolve to the same token through either path (the
                // only name appearing in both special-case lists is "Payload", and
                // both map it to event.payload), so exporter goldens are unchanged.
                var catalogDef = Phoenix.Controls.Shared.Core.PlatformEventCatalog.Find(src.Title);
                if (catalogDef is not null)
                {
                    foreach (var catSocket in catalogDef.Sockets)
                        if (string.Equals(catSocket.Name, srcSocket.Name, StringComparison.Ordinal))
                            return "{" + catSocket.VarToken + "}";
                }

                // Twitch.InWhisper's UserId output → {user.id} (the whisper
                // sender's numeric id, set by BuildGenericEventVars from
                // data.user.id). Without this guard it falls through to the
                // generic {event.userid} form which nothing binds. User /
                // Message resolve via the generic switch below.
                // Sits AFTER the catalog lookup because Twitch.InWhisper has no
                // PlatformEventCatalog entry, so the two can never both fire.
                if (src.Title == "Twitch.InWhisper" && srcSocket.Name == "UserId")
                    return "{user.id}";

                return srcSocket.Name switch
                {
                    "Message"   => "{user.message}",
                    "User"      => "{user.name}",
                    "Gifter"    => "{user.gifter}",
                    "Recipient" => "{user.recipient}",
                    "Months"    => "{user.sub_months}",
                    "Viewers"   => "{user.viewers}",
                    "Bits"      => "{user.bits}",
                    "Reward"    => "{user.reward}",
                    "Input"     => "{user.input}",
                    "Count"     => "{user.count}",
                    "Tier"      => "{user.tier}",
                    "IsAnonymous" => "{user.is_anonymous}",
                    "Payload"   => "{event.payload}",
                    _           => $"{{event.{srcSocket.Name.ToLower()}}}"
                };
            }
            // Giveaway.Id / Giveaway.IsActive are no-flow pure-data probes
            // resolved inline as giveaway.default_id() / giveaway.is_active(…)
            // (see ComputeInlineValue), NOT result-base value outputs — handle
            // them BEFORE the Giveaway.* value-output branch below so they don't
            // resolve to a never-written "_gw_<id6>_…" var.
            if (src.Title is "Giveaway.Id" or "Giveaway.IsActive")
                return ResolvePureData(src);

            // Giveaway.* value outputs — Close (TotalTickets/EntrantCount),
            // Ticket (Tickets), Winner (WinnerName/WinnerTickets). The matching
            // exporter handler emits a "_gw_<id6>" result-var base and the Hub
            // command writes each value under "{base}_<socket-key>"; resolve the
            // output socket to that key so downstream nodes read it. Must come
            // before the generic category handlers below.
            if (src.Title.StartsWith("Giveaway.", System.StringComparison.Ordinal)
                && srcSocket.Type == SocketType.Output
                && !srcSocket.Name.Equals("Flow", System.StringComparison.OrdinalIgnoreCase)
                && !srcSocket.Name.Equals("Done", System.StringComparison.OrdinalIgnoreCase))
            {
                return $"{{{GiveawayResultBase(src)}_{GiveawaySocketKey(srcSocket.Name)}}}";
            }

            if (src.Title == "Logic.EnumMatch" && srcSocket.Name == "MatchedKey")
                return $"{{global._ematch_{IdPrefix(src, 6)}}}";
            if (src.Title == "DB.GetVariable")
            {
                // Honor the Default attribute by pre-emitting an assignment
                // to a result var, with a conditional fallback when the resolved
                // value is empty (i.e. the key was missing in the databank). This
                // mirrors the Logic.Select pre-statement pattern below: we pay one
                // assignment per node-id (cached in _nodeResultVars) and downstream
                // consumers read the materialized var via {global._dbget_…}.
                string rVar = GetDbGetResultVar(src);
                string cacheKey = src.Id + "_dbget";
                if (!_nodeResultVars.TryGetValue(cacheKey, out var cached))
                {
                    string keyStripped = StripQuotes(src.GetAttr("Key", "user.points"));
                    string indentSp = Indent(_currentIndent);
                    Emit($"{indentSp}{rVar} = {{{keyStripped}}}");
                    string defaultVal = src.GetAttr("Default", "");
                    if (!string.IsNullOrEmpty(defaultVal))
                    {
                        Emit($"{indentSp}if {rVar} == \"\":");
                        Emit($"{indentSp}    {rVar} = \"{EscapeStringLiteral(defaultVal)}\"");
                    }
                    _nodeResultVars[cacheKey] = rVar;
                    cached = rVar;
                }
                return cached;
            }
            // DB.FetchRow per-column synthesized output sockets
            // resolve to {<RowVar>.<column>}. Must come BEFORE the generic
            // DB.* fallthrough below so the column socket name doesn't get
            // swallowed by the cached row-var return path.
            if (src.Title == "DB.FetchRow"
                && srcSocket.Type == SocketType.Output
                && srcSocket.Name != "Flow"
                && srcSocket.Name != "Found"
                && srcSocket.Name != "NotFound"
                && srcSocket.Name != "Row")
            {
                string rowVar = src.GetAttr("Row", $"global._row_{IdPrefix(src, 6)}");
                return $"{{{rowVar}.{srcSocket.Name}}}";
            }

            // DB.Top — multi-output pure-data read (Labels / Values). ONE db.top(...)
            // call must back BOTH outputs from the SAME snapshot: two separate inline
            // reads could interleave with a write and return rank-misaligned lists.
            // Hoist one db.top(<Table>, <ValueColumn>, <LabelColumn>, <Count>, "<base>")
            // call (the Song.Current result-base pattern) that the Hub
            // command answers by writing {base}_labels / {base}_values via
            // SetLocalResultVar; resolve each output socket to its sub-var. Must sit
            // BEFORE the generic DB.* fallthrough below, which would otherwise swallow
            // the title into the cached-row-var return path.
            if (src.Title == "DB.Top")
            {
                string baseVar = SongReadResultBase(src, "_dt_");
                if (!_visitedNodes.Contains(src.Id))
                {
                    string table    = ResolveInputValue(src, "TableName",   "\"\"");
                    string valueCol = ResolveInputValue(src, "ValueColumn", "\"\"");
                    string labelCol = ResolveInputValue(src, "LabelColumn", "\"\"");
                    string count    = ResolveInputValue(src, "Count",       "5");
                    string indentSp = Indent(_currentIndent);
                    Emit($"{indentSp}# [DB.Top]");
                    Emit($"{indentSp}db.top({table}, {valueCol}, {labelCol}, {count}, \"{baseVar}\")");
                    MarkVisited(src.Id);
                }
                return srcSocket.Name switch
                {
                    "Values" => $"{{{baseVar}_values}}",
                    _        => $"{{{baseVar}_labels}}",   // Labels (and any unexpected socket)
                };
            }

            if (src.Title.StartsWith("DB.") && src.Title is not "DB.RowCount" and not "DB.GetCell" and not "DB.GetColumn")
            {
                if (_nodeResultVars.TryGetValue(src.Id, out var cachedVar))
                    return cachedVar;
                if (src.Title == "DB.FindRow")
                    return $"global._rid_{IdPrefix(src, 6)}";
                return StripQuotes(src.GetAttr("Key", "var"));
            }

            // Pure-data DB nodes resolve inline like Math nodes
            if (src.Title is "DB.RowCount" or "DB.GetCell" or "DB.GetColumn")
                return ResolvePureData(src);

            // ── Special-case nodes that must be resolved BEFORE the generic
            //    category handler, because they use runtime variables rather
            //    than a ComputeInlineValue formula. ──────────────────────────

            if (src.Title == "Array.Unpack")
            {
                // Array.Unpack populates id-derived global vars at flow time;
                // when consumed inline as a data output, dispatch through the
                // registry so the unpack statements are emitted before use.
                ProcessNode(src, _currentIndent);
                // Emit the substitution form `{global.x}` so nested macro/event
                // re-entries resolve through SubstituteVars rather than treating the
                // bare `global._unpack_...` token as a literal identifier.
                if (srcSocket.Name == "Rest") return $"{{global._unpack_{IdPrefix(src, 6)}_rest}}";
                int idx = int.TryParse(srcSocket.Name.Replace("Item ", ""), out int i) ? i : 0;
                return $"{{global._unpack_{IdPrefix(src, 6)}_{idx}}}";
            }

            if (src.Title == "Array.Literal")
            {
                string items = src.GetAttr("Items", "");
                string joined = string.Join(",", items.Split(',').Select(x => x.Trim()));
                return $"\"{joined}\"";
            }

            if (src.Title == "Text.ParseCommand")
            {
                // Per-node-unique result var. The old shared key
                // (global._result_text_parsecommand) clobbered itself when two
                // Text.ParseCommand nodes ran in one script: the second node's emit
                // overwrote the first, so a downstream array.get against the first
                // node's "Parts" output read the second node's segments. Namespacing
                // by node id (the same scheme Array.Unpack / DB.FindRow use via
                // IdPrefix) lets multiple parse-command nodes coexist. The emit and
                // the read-back both flow through this single return, so they stay in
                // sync automatically.
                string resultVar = $"global._parsecmd_{IdPrefix(src, 6)}";
                if (!_visitedNodes.Contains(src.Id))
                {
                    string inlineVal = ComputeInlineValue(src);
                    string indentSp = Indent(_currentIndent);
                    Emit($"{indentSp}# [{src.Title}]");
                    Emit($"{indentSp}{resultVar} = {inlineVal}");
                    MarkVisited(src.Id);
                }
                return resultVar;
            }

            if (src.Title == "Flow.Reroute")
                return ResolveInputValue(src, "In", "\"\"");

            // Logic.Select — pure-data ternary multiplexer. The runtime has no inline
            // ternary expression, so we pre-emit an if/else block that populates a local
            // var, then return a reference to that var for the consumer to substitute.
            if (src.Title == "Logic.Select")
            {
                if (_visitedNodes.Contains(src.Id) && _nodeResultVars.TryGetValue(src.Id, out var cachedSel))
                    return cachedSel;
                string varName  = LocalVarName(src);
                string cond     = ResolveInputValue(src, "Cond", "false");
                string aVal     = ResolveInputValue(src, "A",    "\"\"");
                string bVal     = ResolveInputValue(src, "B",    "\"\"");
                string indentSp = Indent(_currentIndent);
                Emit($"{indentSp}# [Logic.Select]");
                Emit($"{indentSp}if {cond}:");
                Emit($"{indentSp}    {varName} = {aVal}");
                Emit($"{indentSp}else:");
                Emit($"{indentSp}    {varName} = {bVal}");
                MarkVisited(src.Id);
                _nodeResultVars[src.Id] = $"{{{varName}}}";
                return $"{{{varName}}}";
            }

            // HTTP.Get/Post/Put/Delete write into the shared result.http_*
            // engine vars at runtime. Map every output socket on those nodes
            // to the corresponding var. Hub does NOT per-node-id namespace
            // these (verified against ScriptManager handler implementations);
            // if two HTTP nodes execute in the same script, the second's
            // response overwrites the first — that's the Hub contract today,
            // not an exporter concern.
            if (src.Title is "HTTP.Get" or "HTTP.Post" or "HTTP.Put" or "HTTP.Patch" or "HTTP.Delete")
            {
                return srcSocket.Name switch
                {
                    "StatusCode" => "{result.http_status}",
                    "Body"       => "{result.http_body}",
                    "Response"   => "{result.http_body}",  // Hub writes the body to result.http_body for Get/Post/Put
                    "Error"      => "{result.http_error}",
                    _            => $"\"{src.Title}.{srcSocket.Name}\""
                };
            }

            // HTTP.ParseJson is a pure-data node whose Value socket inlines the
            // http.parse_json(...) call (handled below via ResolvePureData). The
            // additive Error output socket has no inline expression — the Hub
            // handler surfaces a parse failure on result.json_error — so map that
            // socket directly here before the pure-data branch swallows it.
            if (src.Title == "HTTP.ParseJson" && srcSocket.Name == "Error")
                return "{result.json_error}";

            // P3 — File I/O result vars. Engine handlers in ScriptManager write
            // result.file_content / result.file_error; the same shared-slot caveat as
            // http.* applies (a second File.* call overwrites the first within the
            // same script run).
            if (src.Title is "File.ReadText" or "File.WriteText" or "File.ReadJSON" or "File.WriteJSON")
            {
                return srcSocket.Name switch
                {
                    "Content" => "{result.file_content}",
                    "Error"   => "{result.file_error}",
                    _         => $"\"{src.Title}.{srcSocket.Name}\""
                };
            }

            // P4 — Discord bot REST result vars. Engine handlers write
            // result.discord_message_id, result.discord_error, and (GetUser only)
            // result.discord_user_*; same shared-slot caveat as http.* — a second
            // Discord call overwrites the first within the same script run.
            if (src.Title is "Discord.SendMessage" or "Discord.SendEmbed"
                          or "Discord.AddRole" or "Discord.RemoveRole"
                          or "Discord.React"   or "Discord.GetUser")
            {
                return srcSocket.Name switch
                {
                    "MessageId"  => "{result.discord_message_id}",
                    "Username"   => "{result.discord_user_name}",
                    "GlobalName" => "{result.discord_user_global_name}",
                    "AvatarUrl"  => "{result.discord_user_avatar}",
                    "Error"      => "{result.discord_error}",
                    _            => $"\"{src.Title}.{srcSocket.Name}\""
                };
            }

            // API.Call writes the HTTP body to result.api_response at runtime;
            // any downstream node consuming the Response socket reads the
            // engine variable directly.
            if (src.Title == "API.Call" && srcSocket.Name == "Response")
                return "{result.api_response}";

            // AI nodes write result.ai_* engine vars at runtime
            // (ScriptManager.AI.cs), but ResolveOutputFromNode had no branch, so a
            // wire from an AI output socket emitted the dead literal
            // "AI.Prompt.Response". Map each declared output to its result var.
            if (src.Title is "AI.Prompt" or "AI.VisionDescribe" or "AI.StreamText"
                          or "AI.WithTools" or "AI.Moderate" or "AI.GenerateImage")
            {
                return srcSocket.Name switch
                {
                    "Response"   => "{result.ai_response}",
                    "ToolCalls"  => "{result.ai_tool_calls}",
                    "Flagged"    => "{result.ai_flagged}",
                    "Category"   => "{result.ai_category}",
                    "ImageUrl"   => "{result.ai_image_url}",
                    // AI.StreamText surfaces the stream-close / failure
                    // sentinels its handler sets (result.ai_done /
                    // result.ai_error_kind / result.ai_retry_after). Only
                    // AI.StreamText declares these output sockets, so the
                    // mapping is unambiguous within the shared AI title-set.
                    "Done"       => "{result.ai_done}",
                    "ErrorKind"  => "{result.ai_error_kind}",
                    "RetryAfter" => "{result.ai_retry_after}",
                    // GenerateImage uses a dedicated error slot; the others share result.ai_error.
                    "Error"      => src.Title == "AI.GenerateImage" ? "{result.ai_image_error}" : "{result.ai_error}",
                    _            => $"\"{src.Title}.{srcSocket.Name}\""
                };
            }

            // Bus.OnMessage Type/Payload outputs map to the {bus.*}
            // vars the Hub bus dispatch populates (Bus.cs busVars); they previously
            // resolved to {event.type}/{event.payload}, which that path never sets.
            if (src.Title == "Bus.OnMessage")
            {
                return srcSocket.Name switch
                {
                    "Type"    => "{bus.type}",
                    "Payload" => "{bus.payload}",
                    _         => $"\"{src.Title}.{srcSocket.Name}\""
                };
            }

            // Twitch.CreatePoll / CreatePrediction no longer expose a PollId /
            // PredictionId output — Streamer.bot's DoAction can't return the created
            // id, so those sockets were pruned and the runtime no longer sets
            // result.poll_id / result.prediction_id (see ScriptManager.Twitch).

            // Async.WaitForEvent / Async.WaitForVisual Payload data-out maps to the
            // engine's global._wait_payload (ScriptManager.Wait writes it on resume —
            // ONE var, written by BOTH commands, so both sockets resolve to it). Without
            // the WaitForVisual half the wired pin fell through every title gate to the
            // terminal fallback and emitted the dead literal "Async.WaitForVisual.Payload".
            //
            // ★ THE SINGLE-SLOT LIMITATION — pre-existing (wait_for_event has always
            // shipped it), inherited by WaitForVisual because it shares the one var, and
            // documented here because this arm is where an author's wire becomes that var.
            //
            // The payload is a SLOT, not a value carried along the wire. Two consequences,
            // both invisible on the canvas because a data link expresses no flow order:
            //
            //   1. READ IT AFTER ITS OWN WAIT AND BEFORE THE NEXT ONE. A consumer that
            //      runs BEFORE the wait still emits {global._wait_payload}, so the token
            //      appears upstream of its own writer. If no Vars row exists yet the
            //      engine's {global.*} pre-load skips the empty key (ScriptEngine.cs:687
            //      — IsNullOrEmpty rows are not loaded), SubstituteVars finds nothing to
            //      substitute, and the RAW braced token reaches the node as text (what a
            //      chat.send would then say on stream).
            //   2. TWO WAITS IN ONE SCRIPT SHARE THE ONE SLOT — the second wait's write
            //      overwrites the first's, whichever node the wires visually point at.
            //      A value that must outlive the next wait has to be copied into an
            //      author-owned var (Var.Set) immediately after its own wait resumes.
            if ((src.Title is "Async.WaitForEvent" or "Async.WaitForVisual") && srcSocket.Name == "Payload")
                return "{global._wait_payload}";

            // StreamerBot.GetUser is a byte-identical alias of
            // twitch.get_user; its single Data output had no resolver branch and
            // emitted a dead literal. The lookup populates user.* result vars;
            // surface the most useful single field (display name) through Data.
            // (Fuller typed-socket parity with Twitch.GetUser is feature-adjacent,
            // tracked separately.)
            if (src.Title == "StreamerBot.GetUser" && srcSocket.Name == "Data")
                return "{user.display_name}";

            // 0.13.9 — Twitch.CreateClip exposes the created clip's URL + ok flag as
            // output sockets (the engine writes clip.url / clip.ok after the
            // "Phoenix: Create Clip" C# sub-action returns). Braced-token form like
            // the StreamerBot.GetUser arm above; also reachable as {clip.url}/{clip.ok}.
            if (src.Title == "Twitch.CreateClip")
                return srcSocket.Name switch
                {
                    "ClipUrl" => "{clip.url}",
                    "ClipOk"  => "{clip.ok}",
                    _         => "\"\"",
                };

            // Flow.Select — pure-data N-way multiplexer. Engine has no `select`
            // runtime, so pre-emit an if/elif/elif/elif/else block populating a
            // local var and return its reference (same pattern as Logic.Select).
            if (src.Title == "Flow.Select")
            {
                if (_visitedNodes.Contains(src.Id) && _nodeResultVars.TryGetValue(src.Id, out var cachedSel))
                    return cachedSel;
                string varName  = LocalVarName(src);
                string idx      = ResolveInputValue(src, "Index", "0");
                string aVal     = ResolveInputValue(src, "A",     "\"\"");
                string bVal     = ResolveInputValue(src, "B",     "\"\"");
                string cVal     = ResolveInputValue(src, "C",     "\"\"");
                string dVal     = ResolveInputValue(src, "D",     "\"\"");
                string indentSp = Indent(_currentIndent);
                Emit($"{indentSp}# [Flow.Select]");
                Emit($"{indentSp}{varName} = \"\"");
                Emit($"{indentSp}if {idx} == 0:");
                Emit($"{indentSp}    {varName} = {aVal}");
                Emit($"{indentSp}elif {idx} == 1:");
                Emit($"{indentSp}    {varName} = {bVal}");
                Emit($"{indentSp}elif {idx} == 2:");
                Emit($"{indentSp}    {varName} = {cVal}");
                Emit($"{indentSp}elif {idx} == 3:");
                Emit($"{indentSp}    {varName} = {dVal}");
                MarkVisited(src.Id);
                _nodeResultVars[src.Id] = $"{{{varName}}}";
                return $"{{{varName}}}";
            }

            // Song.Current / Song.UpNext — multi-output pure-data reads (the DB.Top
            // hoisting shape): the queue and the transport state
            // move under the graph, so five separate inline calls could report a title
            // from before a skip beside a state from after it. One song.current("<base>")
            // / song.up_next("<base>") call is hoisted and the Hub command answers it by
            // writing {base}_title / {base}_requester / {base}_video_id (plus
            // {base}_state / {base}_volume for Current) via SetLocalResultVar; each output
            // socket then resolves to its sub-var. "Song Requests" is not a
            // _pureDataCategory — the Song control nodes must emit flow — so both are
            // title-routed like the Timer.Get* family.
            if (src.Title is "Song.Current" or "Song.UpNext")
            {
                bool current = src.Title == "Song.Current";
                string baseVar = SongReadResultBase(src, current ? "_sc_" : "_sn_");
                if (!_visitedNodes.Contains(src.Id))
                {
                    string indentSp = Indent(_currentIndent);
                    Emit($"{indentSp}# [{src.Title}]");
                    Emit($"{indentSp}{(current ? "song.current" : "song.up_next")}(\"{baseVar}\")");
                    MarkVisited(src.Id);
                }
                return srcSocket.Name switch
                {
                    "Requester" => $"{{{baseVar}_requester}}",
                    "VideoId"   => $"{{{baseVar}_video_id}}",
                    "State"     => $"{{{baseVar}_state}}",
                    "Volume"    => $"{{{baseVar}_volume}}",
                    _           => $"{{{baseVar}_title}}",   // Title (and any unexpected socket)
                };
            }

            // Poll.Status — an eight-output pure-data read, same hoisting shape and same
            // reason as Song.Current above, and here the reason is sharper than
            // "tidier": votes and the pot move continuously while a poll is open, so eight
            // separate inline calls could report a leader from before a vote beside a total
            // from after it — a graph would branch on a state that never existed. One
            // poll.status("<base>") call is hoisted and the Hub command answers it by
            // writing {base}_state / _is_open / _title / _leader / _leader_votes /
            // _total_votes / _pot / _seconds_left via SetLocalResultVar; each output socket
            // then resolves to its sub-var. "Polls" is not a _pureDataCategory — the Poll
            // control nodes must emit flow — so it is title-routed like the rest.
            if (src.Title == "Poll.Status")
            {
                string baseVar = SongReadResultBase(src, "_ps_");
                if (!_visitedNodes.Contains(src.Id))
                {
                    string indentSp = Indent(_currentIndent);
                    Emit($"{indentSp}# [Poll.Status]");
                    Emit($"{indentSp}poll.status(\"{baseVar}\")");
                    MarkVisited(src.Id);
                }
                return srcSocket.Name switch
                {
                    "IsOpen"      => $"{{{baseVar}_is_open}}",
                    "Title"       => $"{{{baseVar}_title}}",
                    "Leader"      => $"{{{baseVar}_leader}}",
                    "LeaderVotes" => $"{{{baseVar}_leader_votes}}",
                    "TotalVotes"  => $"{{{baseVar}_total_votes}}",
                    "Pot"         => $"{{{baseVar}_pot}}",
                    "SecondsLeft" => $"{{{baseVar}_seconds_left}}",
                    _             => $"{{{baseVar}_state}}",   // State (and any unexpected socket)
                };
            }

            // Pure-data nodes that live outside the Math/Text/Logic/Collections/Convert
            // categories but follow the same inline-or-hoist pattern.
            // The Timer.Get* value nodes are routed by title (NOT by category —
            // "Timer" is intentionally not a _pureDataCategory, because the Timer
            // control nodes must still emit flow). Their call expressions live in
            // ComputeInlineValue.
            if (src.Title is "HTTP.ParseJson"
                // Queue value nodes — no flow, resolved inline. "Queue" is deliberately
                // NOT a _pureDataCategory: the Push / Pop / Clear / Remove siblings must
                // still emit flow, so the three read-only members are routed by title
                // (exactly like the Timer value nodes below).
                or "Queue.Length" or "Queue.Position" or "Queue.List"
                // Timer value nodes — the 2026-08 tool-node cut retired GetFormatted /
                // GetPaused / GetProgress (Overlay.Get covers them via the 1 Hz
                // timer.<name>.* channel keys); the two survivors need a fresh
                // read-after-write the channel cannot guarantee.
                or "Timer.GetRemaining" or "Timer.GetState"
                // Song Request value nodes — same no-flow inline-title route ("Song
                // Requests" is not a _pureDataCategory; the Song control nodes must emit
                // flow). Their call expressions live in ComputeInlineValue. (Song.Current
                // and Song.UpNext are multi-output and handled by their own hoisting arms
                // above, not via ComputeInlineValue.)
                or "Song.QueueLength" or "Song.QueuePosition"
                // Polls value node — same no-flow inline-title route ("Polls" is not a
                // _pureDataCategory; the Poll.Open / Close / Cancel / Vote / Bet siblings
                // must still emit flow). Its call expression lives in ComputeInlineValue.
                // (Poll.Status is multi-output and handled by its own hoisting arm above,
                // not via ComputeInlineValue.)
                or "Poll.GetVotes"
                // Ranks value node — same no-flow inline-title route ("Ranks" is
                // deliberately NOT a _pureDataCategory: the Rank.Evaluate sibling must
                // still emit flow). Rank.Get survives the 2026-08 cut because the
                // value→rank-name ladder lives in the Ranks config, out of DB.*'s
                // reach; Rank.Value / Rank.Top were retired (DB.GetCell / DB.Top).
                or "Rank.Get"
                // Overlay Live Channel read — same no-flow inline-title route
                // ("Visuals" is deliberately NOT a _pureDataCategory: Overlay.Publish
                // and every Visual.* node in that band must still emit flow). Call
                // expression lives in ComputeInlineValue.
                or "Overlay.Get"
                // Chat.MessageCount — arrived with the Dev merge; same no-flow
                // inline-title route ("Chat" is not a _pureDataCategory).
                or "Chat.MessageCount")
                return ResolvePureData(src);

            // ── Generic pure-data category handler ──────────────────────────

            if (src.Category is "Math" or "Text" or "Logic" or "Collections" or "Convert")
                return ResolvePureData(src);

            if (src.Title == "Var.Get")
                // Blank/whitespace key would emit `{var.}` (never resolves); fall
                // back to the "myVar" default the same way Var.Set does.
                return $"{{var.{src.GetAttrOrFallback("VariableName", "myVar")}}}";

            if (src.Title == "Var.Set")
                // The Value OUTPUT relays the just-stored value. VarSetHandler
                // emitted `var.<name> = <value>` on the flow before any consumer
                // line runs, so at consume time {var.<name>} IS that value —
                // resolve to the same token Var.Get emits. (Without this arm the
                // socket fell to the dead-literal fallback at the bottom.)
                return $"{{var.{src.GetAttrOrFallback("VariableName", "myVar")}}}";

            if (src.Title == "Public.Get")
                // Blank/whitespace key would emit `{public.}`; fall back to "myKey"
                // matching Public.Set.
                return $"{{public.{src.GetAttrOrFallback("KeyName", "myKey")}}}";

            if (src.Title == "State.Get")
            {
                // Honor a wired Name input (mirror the "Values" category branch
                // below): if the Name socket has an incoming link, resolve it;
                // otherwise fall back to the static attribute.
                EnsureIndices();
                var nameLink = _linksByToNode!.TryGetValue(src.Id, out var nameLinks) && nameLinks.Count > 0
                    ? nameLinks[0]
                    : null;
                if (nameLink != null)
                {
                    var nameSrc  = NodeById(nameLink.FromNodeId);
                    var nameSock = nameSrc?.Sockets.FirstOrDefault(s => s.Id == nameLink.FromSocketId);
                    // A disabled Name source reads as-if-unwired — fall through
                    // to the static attribute (same rule as ResolveInputValue).
                    if (nameSrc != null && nameSock != null && !IsDisabled(nameSrc))
                    {
                        // The wired name was previously pasted straight into the
                        // braces, which NEVER resolved: a Value.String resolves to a
                        // QUOTED literal, so the token came out `{state."phase"}` —
                        // outside VarRefRegex's character class — and a Var.Get
                        // resolves to `{var.x}`, giving the nested `{state.{var.x}}`
                        // that SubstituteVars only ever half-expands (it is single-
                        // pass). Both shapes reached the script as raw text.
                        string nameExpr = ResolveOutputFromNode(nameSrc, nameSock);
                        if (TryStaticStateName(nameExpr, out string literalName))
                            return $"{{state.{literalName}}}";
                        return HoistDynamicStateRead(src, nameExpr);
                    }
                }
                // Normalize the inline pill the same way: the node editor auto-quotes
                // a String pill, so a hand-typed `"phase"` used to emit the equally
                // unresolvable `{state."phase"}`.
                return TryStaticStateName(src.GetAttr("Name", "phase"), out string attrName)
                    ? $"{{state.{attrName}}}"
                    : $"{{state.{src.GetAttr("Name", "phase")}}}";
            }

            if (src.Category == "Values")
            {
                // If input socket is wired, that value takes priority (already resolved upstream)
                EnsureIndices();
                var inLink = _linksByToNode!.TryGetValue(src.Id, out var inLinks) && inLinks.Count > 0
                    ? inLinks[0]   // first incoming in graph order — matches old FirstOrDefault
                    : null;
                if (inLink != null)
                {
                    var inSrc = NodeById(inLink.FromNodeId);
                    var inSock = inSrc?.Sockets.FirstOrDefault(s => s.Id == inLink.FromSocketId);
                    // A disabled upstream source reads as-if-unwired — fall
                    // through to this Value node's own literal attribute.
                    if (inSrc != null && inSock != null && !IsDisabled(inSrc)) return ResolveOutputFromNode(inSrc, inSock);
                }
                string v = src.GetAttr("Value", "");
                // H9 — escape inner quotes/backslashes so a Value.String containing
                // `Hello "world"` produces a syntactically-valid `.phx` literal instead
                // of breaking the parse. EscapeStringLiteral lives at the bottom of
                // this file but wasn't called here.
                return src.Title == "Value.String" ? $"\"{EscapeStringLiteral(v)}\"" : v;
            }

            // "Users" — the User-Management group lookup (User.GetGroups). Standard
            // groups map to fixed keys; ANY other output socket is a custom-group
            // bool whose key derives from the socket name via the shared sanitizer —
            // the exact keys the usermgmt.get_groups handler writes. A socket for a
            // since-deleted group resolves to the unset var (falsy), never stale.
            // Returned BRACED (unlike the legacy bare Twitch-Data convention): these
            // bools feed Logic.Branch/If CONDITIONS, and the engine substitutes
            // braces-only there — a bare key would be the DoN/DB.Increment
            // bare-vs-braced emission bug all over again.
            if (src.Category == "Users")
            {
                return srcSocket.Name switch
                {
                    "IsModerator"  => "{" + Phoenix.Controls.Shared.Models.UserGroupKeys.ModeratorKey + "}",
                    "IsVip"        => "{" + Phoenix.Controls.Shared.Models.UserGroupKeys.VipKey + "}",
                    "IsSubscriber" => "{" + Phoenix.Controls.Shared.Models.UserGroupKeys.SubscriberKey + "}",
                    "IsRegular"    => "{" + Phoenix.Controls.Shared.Models.UserGroupKeys.RegularKey + "}",
                    _              => "{" + Phoenix.Controls.Shared.Models.UserGroupKeys.VarKeyFor(srcSocket.Name) + "}",
                };
            }

            if (src.Category == "Twitch Data")
            {
                return (src.Title, srcSocket.Name) switch
                {
                    // 0.13.9 — full "Get User Info for Target" set (reverses the D1
                    // trim). The SubTier/SubMonths/FollowDate arms were dropped with
                    // their sockets — that data isn't in the native payload.
                    ("Twitch.GetUser",      "Id")            => "user.id",
                    ("Twitch.GetUser",      "Login")         => "user.login",
                    ("Twitch.GetUser",      "DisplayName")   => "user.display_name",
                    ("Twitch.GetUser",      "ProfileImage")  => "user.profile_image",
                    ("Twitch.GetUser",      "AccountCreated")=> "user.account_created",
                    ("Twitch.GetUser",      "Game")          => "user.game",
                    ("Twitch.GetUser",      "ChannelTitle")  => "user.channel_title",
                    ("Twitch.GetUser",      "IsMod")         => "user.is_mod",
                    ("Twitch.GetUser",      "IsSub")         => "user.is_sub",
                    ("Twitch.GetUser",      "IsVip")         => "user.is_vip",
                    // Regular-group membership (User-Management tool) — the handler
                    // writes it beside the platform flags in ApplyUserGlobals.
                    ("Twitch.GetUser",      "IsRegular")     => "user.is_regular",
                    // IsLive shares the same {stream.is_live} the twitch.get_stream
                    // handler writes — now Twitch's real answer for ANY channel,
                    // via the "Phoenix: Get Stream Status" action. The old shape
                    // (own channel: StreamOnline/Offline latch; arbitrary channel:
                    // hardcoded false) survives only as the fallback the handler
                    // takes when that action can't answer. Same var Twitch.IsOnline
                    // resolves.
                    ("Twitch.GetStream",    "IsLive")        => "stream.is_live",
                    ("Twitch.GetStream",    "Title")         => "stream.title",
                    // Engine writes stream.game / stream.viewers
                    // (ScriptManager.Twitch.cs); the exporter previously mapped to
                    // stream.category / stream.viewer_count, which the lookup never
                    // populates, leaving these outputs empty downstream.
                    ("Twitch.GetStream",    "Category")      => "stream.game",
                    ("Twitch.GetStream",    "ViewerCount")   => "stream.viewers",
                    ("Twitch.GetStream",    "Uptime")        => "stream.uptime",
                    ("Twitch.CheckRole",    "IsMod")         => "role.is_mod",
                    ("Twitch.CheckRole",    "IsSub")         => "role.is_sub",
                    ("Twitch.CheckRole",    "IsVip")         => "role.is_vip",
                    ("Twitch.CheckRole",    "IsBroadcaster") => "role.is_broadcaster",
                    ("Twitch.CheckRole",    "IsRegular")     => "role.is_regular",
                    ("Twitch.IsOnline",     "IsLive")        => "stream.is_live",
                    ("Twitch.GetFollowAge", "Days")          => "follow.days",
                    ("Twitch.GetFollowAge", "Formatted")     => "follow.formatted",
                    ("Twitch.GetFollowAge", "FollowDate")    => "follow.date",
                    ("Twitch.GetFollowAge", "IsFollowing")   => "follow.is_following",
                    ("Twitch.LastActive",   "MinutesAgo")    => _nodeResultVars.TryGetValue($"{src.Id}_MinutesAgo", out var minsVal) ? minsVal : "0",
                    ("Twitch.GetViewers",   "Viewers")       => _nodeResultVars.TryGetValue($"{src.Id}_Viewers",   out var vwrVal)  ? vwrVal  : "\"\"",
                    _ => $"twitch.{srcSocket.Name.ToLower()}"
                };
            }

            // "Platform Data" — YouTube/Kick user lookups (youtube.get_user /
            // kick.get_user data round-trips). Same bare-result-var convention
            // as the Twitch Data arm above; the Hub handlers write these keys
            // via SetLocalResultVar (ScriptManager.YouTube.cs / .Kick.cs).
            if (src.Category == "Platform Data")
            {
                return (src.Title, srcSocket.Name) switch
                {
                    ("YouTube.GetUser", "Id")            => "user.id",
                    ("YouTube.GetUser", "DisplayName")   => "user.display_name",
                    ("YouTube.GetUser", "ProfileImage")  => "user.profile_image",
                    ("YouTube.GetUser", "IsMod")         => "user.is_mod",
                    ("YouTube.GetUser", "IsSub")         => "user.is_sub",
                    ("YouTube.GetUser", "IsBroadcaster") => "user.is_broadcaster",
                    ("Kick.GetUser",    "Id")            => "user.id",
                    ("Kick.GetUser",    "Login")         => "user.login",
                    ("Kick.GetUser",    "DisplayName")   => "user.display_name",
                    ("Kick.GetUser",    "ProfileImage")  => "user.profile_image",
                    ("Kick.GetUser",    "IsMod")         => "user.is_mod",
                    ("Kick.GetUser",    "IsSub")         => "user.is_sub",
                    _ => $"platform.{srcSocket.Name.ToLower()}"
                };
            }

            if (src.Category == "System")
            {
                // P2 — Time.SecondsSinceLastFire is a side-effecting pure-data probe:
                // each evaluation reads AND updates the per-key timestamp on the engine.
                // Route it through ResolvePureData so a multi-consumer output evaluates
                // the call exactly once (hoisted into a single local var) and every
                // consumer reads the same materialized value — otherwise the second and
                // later reads in the same run see ~0 because the first read already
                // moved the timestamp forward. Single-consumer stays inlined verbatim.
                if (src.Title == "Time.SecondsSinceLastFire" && srcSocket.Name == "Seconds")
                    return ResolvePureData(src);

                return (src.Title, srcSocket.Name) switch
                {
                    ("System.GetTime", "Time")          => "{system.time}",
                    ("System.GetTime", "Hours")         => "{system.hours}",
                    ("System.GetTime", "Minutes")       => "{system.minutes}",
                    ("System.GetTime", "Seconds")       => "{system.seconds}",
                    ("System.GetTime", "UnixTimestamp") => "{system.unix}",
                    ("System.GetDate", "Date")          => "{system.date}",
                    ("System.GetDate", "Day")           => "{system.day}",
                    ("System.GetDate", "Month")         => "{system.month}",
                    ("System.GetDate", "MonthName")     => "{system.monthname}",
                    ("System.GetDate", "Year")          => "{system.year}",
                    // Time.StreamUptime maps each output socket to
                    // a {stream.*} token resolved by ScriptEngine
                    // against the configurable "stream start" anchor (defaults
                    // to Hub uptime).
                    ("Time.StreamUptime", "Uptime")          => "{stream.uptime}",
                    ("Time.StreamUptime", "UptimeSeconds")   => "{stream.uptime_seconds}",
                    ("Time.StreamUptime", "UptimeMinutes")   => "{stream.uptime_minutes}",
                    ("Time.StreamUptime", "UptimeHours")     => "{stream.uptime_hours}",
                    ("Time.StreamUptime", "Formatted")       => "{stream.uptime_formatted}",
                    ("Time.StreamUptime", "StartedAt")       => "{stream.started_at}",
                    _ => $"{{system.{srcSocket.Name.ToLower()}}}"
                };
            }

            if (src.Category == "Queue")
            {
                if (src.Title == "Queue.Pop")
                    // Substitution form so nested macro/event re-entries resolve
                    // these globals via SubstituteVars rather than as bare identifiers.
                    return $"{{global._queue_pop_{srcSocket.Name.ToLower()}_{IdPrefix(src, 6)}}}";
                // Queue.Length is handled above via the pure-data inline path.
                // Anything else in this category falls back to a runtime global.
                return $"global._queue_{srcSocket.Name.ToLower()}";
            }

            // Chat.WaitForNext / Chat.PeekRecent — per-node result vars cached by
            // their handlers; read back through the same {nodeId}_{SocketName} key
            // pattern used by Twitch.LastActive / Twitch.GetViewers.
            if (src.Title == "Chat.WaitForNext" || src.Title == "Chat.PeekRecent")
            {
                if (_nodeResultVars.TryGetValue($"{src.Id}_{srcSocket.Name}", out var cachedChat))
                    return cachedChat;
            }

            // Flow.ForEach "Item" output maps to the engine's loop.item variable
            if (src.Title == "Flow.ForEach" && srcSocket.Name == "Item")
                return "{loop.item}";

            // Flow.ForLoop "Index" output — per-id form so nested ForLoops don't
            // collide. The engine must populate both legacy {loop.index}
            // and per-id {loop.index_<id6>} per-iteration so this remains a
            // backward-compatible read.
            if (src.Title == "Flow.ForLoop" && srcSocket.Name == "Index")
                return $"{{loop.index_{IdPrefix(src, 6)}}}";

            // Array.Push "List" output is captured by the imperative handler
            // into _nodeResultVars; consumers read the post-push list from there.
            if (src.Title == "Array.Push" && srcSocket.Name == "List"
                && _nodeResultVars.TryGetValue(src.Id, out var pushed))
                return pushed;

            // Process.Start / Process.Spawn InstanceId — the handler caches the
            // minted instance global under the "{nodeId}_{SocketName}" key; read it
            // back so a wired Process.Stop / Process.Terminate references the same
            // instance global rather than the bare-literal fallback below.
            if ((src.Title == "Process.Start" || src.Title == "Process.Spawn")
                && _nodeResultVars.TryGetValue($"{src.Id}_{srcSocket.Name}", out var procInst))
                return procInst;

            return $"\"{src.Title}.{srcSocket.Name}\"";
        }

        private Node? GetTargetNode(string nodeId, string socketId)
        {
            EnsureIndices();
            return _linkByFromSocket!.TryGetValue((nodeId, socketId), out var link)
                ? NodeById(link.ToNodeId)
                : null;
        }

        private Node? GetNamedOutputTarget(Node node, string socketName)
        {
            var socket = node.Sockets.FirstOrDefault(s =>
                s.Type == SocketType.Output &&
                s.Name.Equals(socketName, StringComparison.OrdinalIgnoreCase));
            return socket == null ? null : GetTargetNode(node.Id, socket.Id);
        }

        private string ComputeInlineValue(Node src)
        {
            switch (src.Title)
            {
                case "Text.Builder":
                {
                    string tmpl = src.GetAttr("Template", "");
                    string arg1 = ResolveInputValue(src, "Arg1", "\"\"");
                    string arg2 = ResolveInputValue(src, "Arg2", "\"\"");
                    string arg3 = ResolveInputValue(src, "Arg3", "\"\"");
                    tmpl = tmpl.Replace("{Arg1}", "{0}").Replace("{Arg2}", "{1}").Replace("{Arg3}", "{2}");
                    // Escape the template so a Template containing a quote,
                    // backslash, or newline produces a valid `.phx` literal (every other
                    // quoted emission already routes through EscapeStringLiteral). The
                    // {0}/{1}/{2} placeholders are unaffected by escaping.
                    return $"text.format(\"{EscapeStringLiteral(tmpl)}\", {arg1}, {arg2}, {arg3})";
                }
                case "Math.Add":       return $"math.add({ResolveInputValue(src,"A","0")}, {ResolveInputValue(src,"B","0")})";
                case "Math.Subtract":  return $"math.subtract({ResolveInputValue(src,"A","0")}, {ResolveInputValue(src,"B","0")})";
                case "Math.Multiply":  return $"math.multiply({ResolveInputValue(src,"A","0")}, {ResolveInputValue(src,"B","0")})";
                case "Math.Divide":    return $"math.divide({ResolveInputValue(src,"A","0")}, {ResolveInputValue(src,"B","1")})";
                case "Math.Modulo":    return $"math.modulo({ResolveInputValue(src,"A","0")}, {ResolveInputValue(src,"B","1")})";
                case "Math.Clamp":     return $"math.clamp({ResolveInputValue(src,"Val","0")}, {ResolveInputValue(src,"Min","0")}, {ResolveInputValue(src,"Max","100")})";
                case "Math.Abs":       return $"math.abs({ResolveInputValue(src,"Val","0")})";
                case "Math.Min":       return $"math.min({ResolveInputValue(src,"A","0")}, {ResolveInputValue(src,"B","0")})";
                case "Math.Max":       return $"math.max({ResolveInputValue(src,"A","0")}, {ResolveInputValue(src,"B","0")})";
                case "Math.Floor":     return $"math.floor({ResolveInputValue(src,"Val","0")})";
                case "Math.Ceil":      return $"math.ceil({ResolveInputValue(src,"Val","0")})";
                case "Math.Random":    return $"math.random({ResolveInputValue(src,"Min","1")}, {ResolveInputValue(src,"Max","100")})";
                case "Text.Format":
                {
                    // Iterate every present Arg slot (A..D) rather than hardcoding
                    // A,B, so wired C/D reach the emitted text.format call. Mirrors the
                    // Array.Make slot-walk: resolve all four, then trim trailing empty
                    // fallbacks down to a two-arg floor (A,B) so an unwired Text.Format
                    // emits byte-identically to the old hardcoded form. The runtime
                    // handler is already variadic ({A}..{Z}), so the extra positional
                    // args bind cleanly.
                    //
                    // Blank-normalization: the C/D (and A/B) sockets carry an inline
                    // default pill that stores "" in Attributes. ResolveInputValue
                    // returns that "" verbatim (GetAttr returns a present-but-empty
                    // attribute as-is), which would emit a bare empty positional arg
                    // (`text.format(t, , )`). Collapse any blank resolved slot back to
                    // the empty-string literal "\"\"" — the same value the old
                    // no-attribute fallback produced — so emit is a valid `""` literal
                    // and the trailing-empty trim below matches it.
                    string fmtTmpl = ResolveInputValue(src, "Template", "\"\"");
                    var fmtSlots = new[] { "A", "B", "C", "D" };
                    var fmtResolved = fmtSlots
                        .Select(s =>
                        {
                            string v = ResolveInputValue(src, s, "\"\"");
                            return string.IsNullOrWhiteSpace(v) ? "\"\"" : v;
                        })
                        .ToList();
                    int fmtTrim = fmtResolved.Count - 1;
                    while (fmtTrim > 1 && fmtResolved[fmtTrim] == "\"\"") fmtTrim--;
                    return $"text.format({fmtTmpl}, {string.Join(", ", fmtResolved.Take(fmtTrim + 1))})";
                }
                case "Text.Contains":  return $"text.contains({ResolveInputValue(src,"Source","\"\"")}, {ResolveInputValue(src,"Search","\"\"")})";
                case "Text.Replace":   return $"text.replace({ResolveInputValue(src,"Source","\"\"")}, {ResolveInputValue(src,"Find","\"\"")}, {ResolveInputValue(src,"With","\"\"")})";
                case "Text.Split":     return $"text.split({ResolveInputValue(src,"Source","\"\"")}, {ResolveInputValue(src,"Delimiter","\",\"")})";
                case "Text.JoinList":  return $"text.join_list({ResolveInputValue(src,"List","\"\"")}, {ResolveInputValue(src,"Separator","\", \"")})";
                case "Text.Length":    return $"text.length({ResolveInputValue(src,"In","\"\"")})";
                case "Text.ToUpper":   return $"text.to_upper({ResolveInputValue(src,"In","\"\"")})";
                case "Text.ToLower":   return $"text.to_lower({ResolveInputValue(src,"In","\"\"")})";
                case "Text.ParseCommand": return $"text.parse_command({ResolveInputValue(src,"Message","{message}")}, {ResolveInputValue(src,"Segments","2")})";
                case "Array.Make":
                {
                    // Array.Make raised from 3 → 8 input
                    // slots in NodeRegistry but the inline emit path still hardcoded
                    // A,B,C. Walk all eight slots, then trim trailing empty fallbacks
                    // so a 3-wire Array.Make doesn't pad to length 8 (which would
                    // change array.length() observably). Always preserve at least one
                    // slot so the engine handler doesn't return null on zero args.
                    var slots = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
                    var resolved = slots
                        .Select(s => ResolveInputValue(src, s, "\"\""))
                        .ToList();
                    int trimIdx = resolved.Count - 1;
                    while (trimIdx > 0 && resolved[trimIdx] == "\"\"") trimIdx--;
                    return $"array.make({string.Join(", ", resolved.Take(trimIdx + 1))})";
                }
                case "Array.Get":      return $"array.get({ResolveInputValue(src,"List","\"\"")}, {ResolveInputValue(src,"Index","0")})";
                case "Array.Length":   return $"array.length({ResolveInputValue(src,"List","\"\"")})";
                case "Array.Contains": return $"array.contains({ResolveInputValue(src,"List","\"\"")}, {ResolveInputValue(src,"Value","\"\"")})";
                case "Array.Filter":   return $"array.filter({ResolveInputValue(src,"List","\"\"")}, {ResolveInputValue(src,"Contains","\"\"")})";
                case "Array.Sort":     return $"array.sort({ResolveInputValue(src,"List","\"\"")}, {ResolveInputValue(src,"Numeric","false")})";
                case "Array.Shuffle":  return $"array.shuffle({ResolveInputValue(src,"List","\"\"")})";
                case "Array.Reverse":  return $"array.reverse({ResolveInputValue(src,"List","\"\"")})";
                case "Array.Unique":   return $"array.unique({ResolveInputValue(src,"List","\"\"")})";
                case "Array.Literal":
                {
                    string items = src.GetAttr("Items", "");
                    string joined = string.Join(",", items.Split(',').Select(x => x.Trim()));
                    return $"\"{joined}\"";
                }
                // The documented inline `Default` attribute (used when In
                // is unwired) was dead: ResolveInputValue looked up a non-existent
                // "In" attribute and always returned the hardcoded fallback, silently
                // dropping a user-set Default. Read the Default attribute, escaped +
                // quoted, as the unwired fallback so a customized Default is honored.
                case "Convert.ToInt":    return $"convert.to_int({ResolveInputValue(src,"In",$"\"{EscapeStringLiteral(src.GetAttr("Default","0"))}\"")})";
                case "Convert.ToString": return $"convert.to_string({ResolveInputValue(src,"In",$"\"{EscapeStringLiteral(src.GetAttr("Default",""))}\"")})";
                case "Convert.ToBool":   return $"convert.to_bool({ResolveInputValue(src,"In",$"\"{EscapeStringLiteral(src.GetAttr("Default","false"))}\"")})";
                case "Convert.ToFloat":  return $"convert.to_float({ResolveInputValue(src,"In",$"\"{EscapeStringLiteral(src.GetAttr("Default","0"))}\"")})";
                case "Logic.Equals":     return $"{ResolveInputValue(src,"A","0")} == {ResolveInputValue(src,"B","0")}";
                case "Logic.NotEquals":  return $"{ResolveInputValue(src,"A","0")} != {ResolveInputValue(src,"B","0")}";
                case "Logic.GreaterThan":return $"{ResolveInputValue(src,"A","0")} > {ResolveInputValue(src,"B","0")}";
                case "Logic.LessThan":   return $"{ResolveInputValue(src,"A","0")} < {ResolveInputValue(src,"B","0")}";
                case "Logic.And":        return $"{ResolveInputValue(src,"A","false")} and {ResolveInputValue(src,"B","false")}";
                case "Logic.Or":         return $"{ResolveInputValue(src,"A","false")} or {ResolveInputValue(src,"B","false")}";
                case "Logic.Not":        return $"not {ResolveInputValue(src,"Val","false")}";
                case "Time.SecondsSinceLastFire": return $"time.seconds_since_last_fire({ResolveInputValue(src,"Key","\"\"")})";
                case "DB.RowCount":      return $"db.row_count({ResolveInputValue(src,"TableName","\"\"")})";
                case "State.Exists":     return $"state.exists({ResolveInputValue(src,"Name","\"\"")})";
                case "State.ListKeys":   return "state.list_keys()";
                case "DB.GetCell":       return $"db.get_cell({ResolveInputValue(src,"TableName","\"\"")}, {ResolveInputValue(src,"RowId","0")}, {ResolveInputValue(src,"Column","\"\"")})";
                case "DB.GetColumn":     return $"db.get_column({ResolveInputValue(src,"TableName","\"\"")}, {ResolveInputValue(src,"Column","\"\"")})";
                case "HTTP.ParseJson":   return $"http.parse_json({ResolveInputValue(src,"Json","\"\"")}, {ResolveInputValue(src,"Path","\"\"")})";
                // Flow.Select handled by dedicated branch in ResolveOutputFromNode (engine has no `select` runtime).
                //
                // Queue value reads — the optional Name is emitted ONLY when the author
                // filled it in, the same omit-when-unset rule the Queue.* flow handlers
                // follow (ExporterRegistry.Handlers2.cs), so an untouched Queue.Length
                // still emits the bare `queue.length()` it always did and no golden moves.
                //
                // queue.length's arg order is (ResultVar, Name), which is why the named
                // form has to pass an explicit empty ResultVar: the manifest kept ResultVar
                // first for back-compat with hand-authored .phx that used it, and the
                // exporter never emits a ResultVar at all — this node's value comes back as
                // the command's return.
                case "Queue.Length":
                {
                    string qn = ResolveInputValue(src, "Name", "\"\"");
                    return IsQueueArgSet(qn) ? $"queue.length(\"\", {qn})" : "queue.length()";
                }
                case "Queue.Position":
                {
                    string qn = ResolveInputValue(src, "Name", "\"\"");
                    string entry = ResolveInputValue(src, "Entry", "\"\"");
                    return IsQueueArgSet(qn)
                        ? $"queue.position({entry}, {qn})"
                        : $"queue.position({entry})";
                }
                case "Queue.List":
                {
                    string qn = ResolveInputValue(src, "Name", "\"\"");
                    return IsQueueArgSet(qn) ? $"queue.list({qn})" : "queue.list()";
                }
                // Chat.MessageCount — arrived with the Dev merge. Dev's bare
                // `case "Queue.Length": return "queue.length()";` is superseded by
                // the name-aware Queue block above.
                case "Chat.MessageCount": return "chat.message_count()";
                case "Giveaway.Id":      return "giveaway.default_id()";
                // Empty selector → giveaway.is_active() → the engine follows the
                // app-wide default giveaway; a named selector resolves by
                // id/key/title exactly like the flow giveaway nodes.
                case "Giveaway.IsActive": return $"giveaway.is_active({ResolveInputValue(src, "Giveaway", "\"\"")})";
                // Timer value nodes — resolved inline as timer.get_*(...) call
                // expressions (routed by title above, since "Timer" is not a
                // _pureDataCategory). Empty Name → the default timer at runtime.
                // (GetFormatted / GetPaused / GetProgress were retired in the 2026-08
                // tool-node cut — Overlay.Get on timer.<name>.* covers them.)
                case "Timer.GetRemaining": return $"timer.get_remaining({ResolveInputValue(src, "Name", "\"\"")})";
                case "Timer.GetState":     return $"timer.get_state({ResolveInputValue(src, "Name", "\"\"")})";
                // Song Request single-output reads — resolved inline (routed by title
                // above, since "Song Requests" is not a _pureDataCategory). Song.Current
                // and Song.UpNext are multi-output and hoisted by their own arm in
                // ResolveOutputFromNode, so they never reach ComputeInlineValue.
                // song.queue_position's User is emitted even when empty: the Hub handler
                // reads an empty one as "the triggering chatter", the Loyalty convention.
                case "Song.QueueLength":   return "song.queue_length()";
                case "Song.QueuePosition": return $"song.queue_position({ResolveInputValue(src, "User", "\"\"")})";
                // Polls single-output read — resolved inline (routed by title above, since
                // "Polls" is not a _pureDataCategory). Poll.Status is multi-output and
                // hoisted by its own arm in ResolveOutputFromNode, so it never reaches
                // ComputeInlineValue. The Option arg is emitted even when empty: the Hub
                // handler reads an empty one as "the whole poll's total", which is what
                // keeps an unwired node producing a true number instead of failing.
                case "Poll.GetVotes":      return $"poll.get_votes({ResolveInputValue(src, "Option", "\"\"")})";
                // Ranks value node — resolved inline (routed by title above, since
                // "Ranks" is not a _pureDataCategory). An unwired User emits "" and the Hub
                // resolves it (ResolveRankUser): {event.user_login} — the platform LOGIN —
                // first, {user.name} only when a non-chat trigger parked no login. Emitting
                // {user.name} here instead would bake the DISPLAY name into a lookup against
                // login-keyed stores, which reads correctly for every viewer whose display
                // name is their login re-cased and silently empty for everyone else. Same
                // convention as Song.QueuePosition above. (Rank.Value / Rank.Top were
                // retired in the 2026-08 tool-node cut — DB.GetCell / DB.Top cover them.)
                case "Rank.Get":           return $"rank.get({ResolveInputValue(src, "User", "\"\"")})";
                // Overlay Live Channel read — resolved inline as overlay.get(...)
                // (routed by title above, since "Visuals" is not a _pureDataCategory).
                // An empty / unknown key reads back as the empty string at runtime, so
                // an unwired node degrades to "" rather than failing the export.
                case "Overlay.Get":        return $"overlay.get({ResolveInputValue(src, "Key", "\"\"")})";
                default:
                    return $"\"{src.Title}.result\"";
            }
        }

        /// <summary>Whether an optional Queue.* socket expression carries a real value.
        /// The Queue band emits its trailing Name only when set — that omit-when-unset
        /// rule is what keeps a pre-generalisation graph exporting byte-identically — and
        /// this is the value-node half of the identical check
        /// <c>QueueHandlerHelp.IsSet</c> performs for the flow handlers.</summary>
        private static bool IsQueueArgSet(string v)
            => !(string.IsNullOrWhiteSpace(v) || v == "\"\"");

        private static string StripQuotes(string s) => s.Trim().Trim('"');

        /// <summary>
        /// Emit-side value for a trailing <c>key=value</c> command argument
        /// (Visual.Trigger's variable pins and Args list, Async.WaitForVisual's
        /// EventData pins, Event.Trigger's parameters, Process.Start's start params).
        /// </summary>
        /// <remarks>
        /// These sites used to emit <c>StripQuotes(Resolve(...))</c>, which threw away
        /// exactly the protection <see cref="InlineLiteralOrFallback"/> had just added:
        /// a pill containing a literal comma came back out BARE, so
        /// <c>ScriptEngine.SplitArgs</c> cut the argument at that comma and
        /// CommandBinder's KvPairs branch discarded everything after it (the remainder
        /// has no <c>=</c>). `Thanks for the raid, welcome!` reached the overlay as
        /// `Thanks for the raid`; an `Args` list of `a,b,c` arrived as `Args1` only.
        /// StripQuotes was also lossy in its own right — <c>Trim('"')</c> on an escaped
        /// literal such as <c>"He said \"hi\""</c> ate the trailing escape.
        ///
        /// So: keep (or add) the quotes whenever dropping them would change what the
        /// engine parses, and strip them otherwise. The strip case is the overwhelmingly
        /// common one (plain single-word / no-comma values), which is what keeps every
        /// existing golden byte-identical. ScriptEngine removes the quotes again — see
        /// the `key="value"` arm in ExecuteCommandWithResult — and unquoted
        /// <c>key=value</c> args from older .phx still bind exactly as before.
        /// </remarks>
        internal static string KvArgValue(string resolved)
        {
            if (string.IsNullOrEmpty(resolved)) return string.Empty;
            string trimmed = resolved.Trim();
            if (trimmed.Length == 0) return string.Empty;

            // Already a single well-formed "..." literal: keep the quotes only when the
            // inner text needs them, otherwise emit bare exactly as StripQuotes did.
            if (IsSingleQuotedLiteral(trimmed))
            {
                string inner = trimmed[1..^1];
                // Re-encode any `\"` in the body as `"` before re-adding the
                // transport quotes — SplitArgs is escape-blind, so a `\"` left in place
                // would flip its quote-state parity and cut the value at its own comma.
                return KvValueNeedsQuotes(inner) ? $"\"{QuoteSafeLiteralBody(inner)}\"" : inner;
            }

            // Unquoted: a {var} reference, a number, or a call expression — all of which
            // MUST stay bare ({var} so it substitutes, a call so the engine executes it;
            // SplitArgs is paren-aware, so a comma inside a call's own arg list is safe).
            if (!KvValueNeedsQuotes(trimmed) || IsCallableExpression(trimmed)) return trimmed;
            return $"\"{QuoteSafeLiteralBody(EscapeStringLiteral(trimmed))}\"";
        }

        /// <summary>True when <paramref name="s"/> is one <c>"..."</c> literal — i.e. the
        /// outer quotes actually pair up and no UNESCAPED quote sits between them (so
        /// <c>"a", "b"</c> is not mistaken for a single literal).</summary>
        private static bool IsSingleQuotedLiteral(string s)
        {
            if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return false;
            for (int i = 1; i < s.Length - 1; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return false;
            }
            return true;
        }

        /// <summary>Characters that must stay inside a <c>"..."</c> literal for a
        /// <c>key=value</c> argument to survive the engine's parse: a comma would end the
        /// argument, a newline/CR would split the .phx line, and a backslash only means
        /// what the author typed once ScriptEngine.UnescapeStringLiteral runs (which it
        /// only does inside a literal). A double-quote needs the literal too — but note
        /// the quotes alone do NOT neutralise it: <c>SplitArgs</c> is escape-blind, so a
        /// <c>\"</c> in the body still toggles its quote state. <see cref="QuoteSafeLiteralBody"/>
        /// is what actually makes a quote-bearing value safe, and every caller that adds
        /// the transport quotes must run the body through it.</summary>
        /// <remarks>
        /// Also true for a value the ENGINE would mistake for a nested command call —
        /// the same gap <see cref="ArgLiteralNeedsQuoting"/> covers on the positional
        /// path. Without this arm, an inline pill like <c>Welcome (say hi in chat)</c>
        /// was quoted by <see cref="InlineLiteralOrFallback"/> and then stripped bare
        /// again here, so <c>ExecuteCommandWithResult</c>'s <c>key=func(args)</c> arm
        /// recursed on it, found no command named <c>welcome</c>, and delivered an EMPTY
        /// value to the widget. Callers gate on <see cref="IsCallableExpression"/>, so a
        /// deliberate call-shaped value (<c>text.to_lower({user.message})</c>) still
        /// emits bare and still executes.
        /// </remarks>
        private static bool KvValueNeedsQuotes(string s)
        {
            foreach (char c in s)
                if (c == ',' || c == '"' || c == '\\' || c == '\n' || c == '\r')
                    return true;
            return EngineCallShapeRegex.IsMatch(s.Trim());
        }

        /// <summary>
        /// Re-encode every double quote inside an ALREADY-escaped string-literal body as
        /// <c>"</c> so the body contains no raw <c>"</c> character at all.
        /// </summary>
        /// <remarks>
        /// <para>Wrapping a value in transport quotes only protects it if the engine's
        /// splitter agrees where the literal ends — and <c>ScriptEngine.SplitArgs</c>
        /// (ScriptEngine.Utilities.cs) toggles its <c>inQuote</c> flag on EVERY <c>"</c>
        /// with no backslash awareness, as do <c>StripInlineComment</c> /
        /// <c>IndexOfOutsideQuotes</c>. So the conventional <c>\"</c> escape flips the
        /// quote-state PARITY: with the opening transport quote plus one <c>\"</c> in the
        /// body, the splitter is back OUTSIDE quotes when it reaches the value's comma
        /// and cuts the argument in half. `24" monitor, giveaway!` reached the widget as
        /// `24\" monitor` — a truncation the bare (unquoted) emission did not have.</para>
        /// <para><c>"</c> carries the quote with zero raw <c>"</c> characters, so the
        /// only quotes on the line are the two transport ones and the parity is always
        /// even. <c>ScriptEngine.UnescapeStringLiteral</c> already decodes 4-hex-digit
        /// <c>\u</c> escapes, so the value round-trips unchanged.</para>
        /// <para>This is deliberately a POST-pass over <see cref="EscapeStringLiteral"/>
        /// rather than a change to it: that helper is also used for literals the engine
        /// reads through paths where <c>\"</c> is the pinned wire form
        /// (ScriptStringRoundTripIntegrationTests), and only command-ARGUMENT literals
        /// meet the escape-blind splitter.</para>
        /// </remarks>
        internal static string QuoteSafeLiteralBody(string escapedBody)
        {
            if (string.IsNullOrEmpty(escapedBody)) return escapedBody ?? string.Empty;
            // Fast path: no quote character anywhere means nothing to re-encode, so
            // every ordinary literal emits byte-for-byte as before.
            if (escapedBody.IndexOf('"') < 0) return escapedBody;

            var sb = new System.Text.StringBuilder(escapedBody.Length + 8);
            for (int i = 0; i < escapedBody.Length; i++)
            {
                char c = escapedBody[i];
                if (c == '\\' && i + 1 < escapedBody.Length)
                {
                    char next = escapedBody[i + 1];
                    // `\"` is the sequence we are replacing. Every other escape pair
                    // (`\\`, `\n`, `\uXXXX`'s leading `\u`, …) is copied verbatim — and
                    // consuming both chars is what keeps a literal backslash (`\\`) from
                    // being mistaken for the escape introducer of a following quote.
                    if (next == '"') sb.Append("\\u0022");
                    else sb.Append(c).Append(next);
                    i++;
                    continue;
                }
                if (c == '"') sb.Append("\\u0022");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Backslash-escapes characters that would otherwise break a `"..."`
        /// string literal in the emitted script. Apply at every site that
        /// interpolates a user-edited attribute INSIDE a literal — case
        /// values, enum entries, command-filter aliases, etc.
        /// </summary>
        internal static string EscapeStringLiteral(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    default:
                        // Any remaining C0 control or DEL must be escaped — a raw
                        // 0x00-0x1F or 0x7F in a "..." literal would either break
                        // the .phx parser (newlines, NUL) or corrupt the output
                        // stream silently. \uXXXX pairs with ScriptEngine.UnescapeStringLiteral's
                        // 4-hex-digit \u decoder.
                        if (ch < 0x20 || ch == 0x7F)
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Stable per-node id prefix for naming runtime globals. Strips dashes
        /// and pads short ids so we never throw on hand-edited or imported
        /// graphs whose Ids are shorter than the historical 6-char slice.
        /// 12 hex chars = 48 bits — collision-safe for any plausible graph size.
        /// </summary>
        internal static string IdPrefix(Node node, int chars = 12)
        {
            string s = (node.Id ?? "").Replace("-", "");
            if (s.Length >= chars) return s.Substring(0, chars);
            return s.PadRight(chars, '0');
        }

        /// <summary>
        /// Coerces an arbitrary user-supplied socket name into a valid runtime
        /// identifier so it can be safely interpolated into emitted variable
        /// names (e.g. <c>global._macro_..._{name}</c>). Replaces any character
        /// outside <c>[A-Za-z0-9_]</c> with an underscore. Used at every
        /// macro-parameter / event-parameter binding site so the read and write
        /// halves converge on the same identifier even when an inline-renamed
        /// socket carries spaces or punctuation. Empty input returns "_" so
        /// the emitted symbol is never blank.
        /// </summary>
        internal static string SanitizeIdentifier(string name)
            => string.IsNullOrEmpty(name)
                 ? "_"
                 : Regex.Replace(name, @"[^A-Za-z0-9_]", "_");

        private static string GetDbGetResultVar(Node node)
            => $"global._dbget_{IdPrefix(node, 8)}";

        // ══════════════════════════════════════════════════════════════════
        // EXPORTER CONTEXT FACADE — internal entry points for ExporterContext
        // so the new Title-keyed registry can call into the same private
        // helpers that the legacy category emitters use.
        // ══════════════════════════════════════════════════════════════════

        internal void CtxEmit(string line) => Emit(line);
        internal void CtxAppendRawLine(string line) => _sb.AppendLine(line);
        // Handler-side hook into the runtime-warning surface used by Export().
        internal void CtxAddRuntimeWarning(string message, string? nodeId = null)
            => AddRuntimeWarning(message, nodeId);
        internal string CtxResolveInputValue(Node n, string socket, string fallback)
            => ResolveInputValue(n, socket, fallback);
        internal string CtxMaterializeInput(Node n, string socket, string fallback)
            => MaterializeInput(n, socket, fallback);
        internal void CtxFollowNamedOutput(Node n, string outName, int indent)
            => FollowNamedOutput(n, outName, indent);
        internal void CtxFollowFlowOutput(Node n, int indent)
            => FollowFlowOutput(n, indent);
        internal Node? CtxGetNamedOutputTarget(Node n, string outName)
            => GetNamedOutputTarget(n, outName);
        internal Node? CtxGetTargetNode(string nodeId, string socketId)
            => GetTargetNode(nodeId, socketId);
        // O(1) "is this output socket wired" probe backed by
        // the per-export link index, so handlers don't re-scan Graph.Links per socket.
        internal bool CtxIsOutputConnected(string nodeId, string socketId)
        {
            EnsureIndices();
            return _linkByFromSocket!.ContainsKey((nodeId, socketId));
        }
        internal void CtxEmitBranch(Node n, string trueOut, string falseOut,
            string prefix, int indent, string truePfx, string? elsePfx)
            => EmitBranch(n, trueOut, falseOut, prefix, indent, truePfx, elsePfx);
        internal void CtxProcessNode(Node n, int indent) => ProcessNode(n, indent);
        internal static string CtxGetDbGetResultVar(Node n) => GetDbGetResultVar(n);
        internal static string CtxStripQuotes(string s) => StripQuotes(s);
        internal static string CtxIdPrefix(Node n, int chars = 12) => IdPrefix(n, chars);
        internal static string CtxEscapeStringLiteral(string s) => EscapeStringLiteral(s);
        internal static string CtxSanitizeIdentifier(string s) => SanitizeIdentifier(s);
        internal string CtxExportMacroSubGraph(Graph macroGraph, string slotPrefix)
        {
            // Cycle detection keys on the routine's stable id (the segment of the
            // slot prefix after the routine-kind tag), NOT the per-call-site prefix —
            // otherwise two distinct call sites of the same recursive routine would
            // each get a unique key and the cycle would never be flagged.
            //   slotPrefix = "_macro_<stableMacroId>_<callSiteId>"
            //   slotPrefix = "_process_<stableProcessId>_<callSiteId>"
            //
            // The original hardcoded `"_macro_".Length` (=7) only
            // works for the macro shape; for `_process_xyz_abc` the same offset 7
            // lands inside the literal "process" token (between the two `s`s) so
            // `IndexOf('_', 7)` returns 8 — yielding cycle key `"_process"` for
            // EVERY Process.Spawn node in the graph. Two unrelated Process.Spawn
            // call sites then false-positive as a circular reference. Detect the
            // routine-kind tag dynamically and skip past it before searching for
            // the call-site separator.
            string cycleKey = slotPrefix;
            string? tag = slotPrefix.StartsWith("_macro_",   StringComparison.Ordinal) ? "_macro_"
                       : slotPrefix.StartsWith("_process_", StringComparison.Ordinal) ? "_process_"
                       : null;
            if (tag != null)
            {
                int second = slotPrefix.IndexOf('_', tag.Length);
                if (second > 0) cycleKey = slotPrefix.Substring(0, second);
            }

            // O(1) membership test instead of the
            // O(depth) Stack.Contains scan. _macroStackSet is kept in lock-step
            // with _macroStack on every push/pop below.
            if (_macroStackSet.Contains(cycleKey))
            {
                var chain = string.Join(" → ", _macroStack.Reverse().Append(cycleKey));
                throw new InvalidOperationException($"Circular macro reference: {chain}");
            }

            // Depth ceiling for the non-cyclic-but-pathologically-deep chain (see
            // MaxMacroExpansionDepth). _macroStack.Count is the current nesting
            // depth; throwing here — before the push/recurse below — stops the
            // recursion cleanly instead of letting it ride the call stack into an
            // uncatchable StackOverflowException.
            if (_macroStack.Count >= MaxMacroExpansionDepth)
            {
                var chain = string.Join(" → ", _macroStack.Reverse().Append(cycleKey));
                throw new InvalidOperationException(
                    $"Macro nesting too deep (>{MaxMacroExpansionDepth} levels): {chain}. " +
                    "Flatten the macro/process call chain.");
            }

            // A body called from multiple reachable sites/events
            // re-exports identical text each time. The fresh sub-exporter starts with
            // empty visited/result/var state, so its output is a pure function of
            // (body graph identity, slotPrefix, in-flight macro-stack snapshot) — all
            // captured in the key below, so a cache hit is byte-identical to a re-run.
            // The slotPrefix already embeds the unique call-site id; the stack snapshot
            // distinguishes the (theoretical) same key reached at different nesting.
            // Unit-separator () between segments so e.g. hashcode "12"+"3…"
            // can't collide with "1"+"23…".
            string cacheKey = string.Join("",
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(macroGraph).ToString(),
                slotPrefix,
                string.Join("", _macroStack.Reverse()));
            if (_macroExportCache.TryGetValue(cacheKey, out var cachedExport))
                return cachedExport;

            _macroStack.Push(cycleKey);
            _macroStackSet.Add(cycleKey);
            try
            {
                string exported = new ScriptExporter(macroGraph, slotPrefix, _macroStack, _macroExportCache, _validationCache).Export();
                _macroExportCache[cacheKey] = exported;
                return exported;
            }
            finally
            {
                _macroStack.Pop();
                _macroStackSet.Remove(cycleKey);
            }
        }
        internal Dictionary<string, string> CtxNodeResultVars => _nodeResultVars;
        internal Graph CtxGraph => _graph;
        internal string CtxMacroContextId => _macroContextId;

    }

    internal static class NodeExtensions
    {
        public static string GetAttr(this Node node, string key, string fallback = "")
            => node.Attributes != null && node.Attributes.TryGetValue(key, out var val) ? val : fallback;

        /// <summary>
        /// Same as <see cref="GetAttr"/> but treats blank/whitespace-only values
        /// as missing — falls back to the supplied default. Use this for
        /// identifier-like attributes (variable names, event names, keys) where
        /// an empty string would produce malformed script.
        /// </summary>
        public static string GetAttrOrFallback(this Node node, string key, string fallback)
        {
            if (node.Attributes == null) return fallback;
            if (!node.Attributes.TryGetValue(key, out var val)) return fallback;
            return string.IsNullOrWhiteSpace(val) ? fallback : val;
        }
    }

    public enum ValidationSeverity { Warning, Error }

    public class ValidationWarning
    {
        public ValidationSeverity Severity { get; init; }
        public string             Message  { get; init; } = "";
        public string?            NodeId   { get; init; }

        public override string ToString() => NodeId != null
            ? $"[{Severity}] {Message} (node {NodeId[..Math.Min(8, NodeId.Length)]})"
            : $"[{Severity}] {Message}";
    }

    public static class GraphValidator
    {
        public static List<ValidationWarning> Validate(Graph graph)
        {
            var warnings = new List<ValidationWarning>();
            if (graph == null) return warnings;

            // Node-id index shared by every pass below — populated first-wins so
            // lookups preserve the graph.Nodes.FirstOrDefault(n => n.Id == …)
            // selection semantics the passes previously re-ran per link.
            var nodeById = new Dictionary<string, Node>(graph.Nodes.Count);
            foreach (var n in graph.Nodes)
                if (n.Id != null) nodeById.TryAdd(n.Id, n);

            CheckDisconnectedFlowNodes(graph, nodeById, warnings);
            CheckUnmatchedEventPairs(graph, warnings);
            CheckCircularFlow(graph, nodeById, warnings);
            CheckIncompatibleLinks(graph, nodeById, warnings);
            CheckDanglingLinks(graph, nodeById, warnings);
            CheckPlaceholderFrameContents(graph, warnings);
            CheckMacroCallOrphans(graph, warnings);
            // Required-input wiring guard. Catches the "saved a
            // Twitch.SendChat with no Message wire + no inline pill" class of
            // gap at author-time rather than run-time. Warnings only — export
            // still completes so the user can save and triage.
            CheckRequiredInputs(graph, warnings);
            // Unreachable-conditional-code guard. Tractable subset
            // only: Logic.If with both A and B materialised as compile-time
            // constants, and Flow.Select with a constant Index. See the
            // method header for the deferred-to-runtime cases.
            CheckUnreachableConditionalCode(graph, nodeById, warnings);

            // Macro + inline-process bodies — save-time visibility. Without
            // this pass a broken sub-graph only fails inside the NESTED export
            // that MacroCallHandler / ProcessSpawnHandler splices, and
            // Architect's save dialog (which validates the top-level graph
            // only) never shows it.
            ValidateRoutineBodies(graph, warnings);

            return warnings;
        }

        // One-level structural sweep over every routine sub-graph — macros
        // (graph.Macros) AND inline-spawnable processes (graph.Processes) —
        // findings mapped into the caller's list. Both kinds splice a nested
        // export into the parent .phx (MacroCallHandler / ProcessSpawnHandler),
        // so both share the abort semantics this pass exists to surface.
        //
        // Severity is capped at Warning: an Error would abort the PARENT
        // export, but a broken routine body must only abort the NESTED export
        // (the body is skipped, the parent still ships). Findings that were
        // Errors get the "body will not be exported" suffix so the consequence
        // stays visible after the downgrade.
        //
        // Pass subset is structural correctness only — circular flow,
        // dangling links / missing sockets, incompatible types: exactly the
        // classes that abort or corrupt the nested export. Whole-script passes
        // are excluded because they assume a top-level graph and false-positive
        // on routine bodies: CheckDisconnectedFlowNodes flags the supported
        // data-only Macro.Exit pattern (and the seeded, not-yet-wired
        // Entry/Exit pair); CheckUnmatchedEventPairs pairs per-graph while a
        // routine's counterpart may live in the parent script;
        // CheckMacroCallOrphans resolves against graph.Macros, which routine
        // bodies do not carry. Advisory passes (required inputs, placeholder
        // frames, unreachable code) already surface at the call site via the
        // nested export's own validation header — the structural findings kept
        // here therefore appear twice (parent header + call site), which is
        // acceptable: the header copy is the discoverable one, the call-site
        // copy sits next to the skipped body.
        //
        // One level deep only: the individual passes are called directly (no
        // recursion into Validate), so a Macros/Processes list on a routine's
        // own graph — not a shape the editor produces — is deliberately not
        // descended into.
        private static void ValidateRoutineBodies(Graph graph, List<ValidationWarning> warnings)
        {
            if (graph.Macros != null)
                foreach (var macro in graph.Macros)
                    ValidateRoutineBody(macro?.Graph, $"Macro '{macro?.Name}'", "macro", warnings);

            if (graph.Processes != null)
                foreach (var process in graph.Processes)
                    ValidateRoutineBody(process?.Graph, $"Process '{process?.Name}'", "process", warnings);
        }

        private static void ValidateRoutineBody(Graph? body, string label, string kind, List<ValidationWarning> warnings)
        {
            if (body?.Nodes == null || body.Links == null) return;

            var bodyNodeById = new Dictionary<string, Node>(body.Nodes.Count);
            foreach (var n in body.Nodes)
                if (n.Id != null) bodyNodeById.TryAdd(n.Id, n);

            var findings = new List<ValidationWarning>();
            CheckCircularFlow(body, bodyNodeById, findings);
            CheckDanglingLinks(body, bodyNodeById, findings);
            CheckIncompatibleLinks(body, bodyNodeById, findings);

            foreach (var f in findings)
            {
                bool wasError = f.Severity == ValidationSeverity.Error;
                warnings.Add(new ValidationWarning
                {
                    Severity = ValidationSeverity.Warning,
                    Message  = $"{label}: {f.Message}"
                             + (wasError ? $" — the {kind} body will not be exported until this is fixed." : ""),
                    NodeId   = f.NodeId
                });
            }
        }

        // O(1) node resolve against the index built in Validate. Null id (only
        // possible on hand-edited/corrupt input) resolves to null, matching the
        // linear-scan behaviour for ids no node carries.
        private static Node? FindNode(Dictionary<string, Node> nodeById, string? id)
            => id != null && nodeById.TryGetValue(id, out var node) ? node : null;

        // Required-input pre-export validation.
        //
        // Sweep every non-Flow.Reroute / non-Flow.Select node. For each input
        // socket flagged required by the template (NodeTemplate.RequiredInputs
        // — opt-in per-template; templates that don't enrol any socket are
        // skipped entirely), confirm the user has either wired an incoming
        // link OR set the inline-pill attribute (node.Attributes[socketName])
        // to a non-empty value. Flow pins are excluded — the existing
        // CheckDisconnectedFlowNodes pass already covers them.
        //
        // Severity is Warning (not Error) so the export still produces a
        // .phx; the user just gets a "# WARNING: ..." comment header so they
        // notice the gap before triggering the script at runtime.
        private static void CheckRequiredInputs(Graph graph, List<ValidationWarning> warnings)
        {
            // Build a fast (nodeId, socketId) → has-incoming-link lookup once
            // so we don't quadratically scan graph.Links for every socket.
            var wiredInputs = new HashSet<(string nodeId, string socketId)>();
            foreach (var link in graph.Links)
            {
                if (!string.IsNullOrEmpty(link.ToNodeId) && !string.IsNullOrEmpty(link.ToSocketId))
                    wiredInputs.Add((link.ToNodeId, link.ToSocketId));
            }

            foreach (var node in graph.Nodes)
            {
                if (node.Title is "Flow.Reroute" or "Flow.Select") continue;

                NodeTemplate? template;
                try { template = NodeRegistry.GetTemplate(node.Title); }
                catch { template = null; }
                if (template == null) continue;

                // Opt-in: templates that don't declare any required input
                // contribute no warnings here. Avoids false positives across
                // the ~200 templates that haven't been audited for "must wire".
                if (template.RequiredInputs == null || template.RequiredInputs.Count == 0) continue;

                foreach (var socket in node.Sockets)
                {
                    if (socket.Type != SocketType.Input) continue;
                    if (string.IsNullOrEmpty(socket.Name)) continue;
                    if (SocketTypeHelper.IsFlowPin(socket)) continue;
                    if (!template.RequiredInputs.Contains(socket.Name)) continue;

                    // Wired? Then it's satisfied — even if the upstream value
                    // is itself blank we don't second-guess the wiring intent.
                    if (wiredInputs.Contains((node.Id, socket.Id))) continue;

                    // Inline-pill value? node.Attributes[socketName] non-empty
                    // counts as supplied. Matches the runtime path in
                    // ScriptExporter.MaterializeInput which reads the same key.
                    string inline = node.GetAttr(socket.Name, "");
                    if (!string.IsNullOrWhiteSpace(inline)) continue;

                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Warning,
                        Message  = $"Required input '{socket.Name}' on '{node.Title}' has no incoming wire and no inline value",
                        NodeId   = node.Id
                    });
                }
            }
        }

        // Unreachable conditional code (tractable subset).
        //
        // Identifies nodes whose only flow-input comes from a branch socket on
        // a Logic.If or Flow.Select where the branch is statically provable
        // never-taken. Two cases handled here:
        //
        //   1. Logic.If where BOTH A and B are compile-time constants (no
        //      incoming wires, attribute pills present) and the Operator
        //      attribute is one of NodeRegistry.ComparisonOperators. We
        //      string-compare or numeric-compare (when both parse) per the
        //      operator and pick which of True / False is unreachable.
        //
        //   2. Flow.Select where the Index attribute is a literal integer
        //      and Index is not wired. The non-selected output sockets
        //      (A / B / C / D minus the chosen one) are unreachable.
        //
        // Severity is Warning — false positives are conceivable when the
        // user is mid-edit, so we never block export.
        //
        // TODO: cases NOT handled by this first iteration —
        //   * Logic.If with one constant + one wired side (would require
        //     partial evaluation of the upstream chain).
        //   * Nested conditional chains (A is itself the output of another
        //     Logic.If whose own constant-eval result we'd need to thread).
        //   * Logic.EnumMatch / Flow.IsValid / Flow.ForLoop whose
        //     branch reachability also depends on runtime data.
        //   * Wildcard-Any operands where the operator + types disagree at
        //     runtime (e.g. ">" on two string-formatted but non-numeric
        //     literals — current logic conservatively skips numeric-only
        //     ops in that case so we don't false-positive).
        private static void CheckUnreachableConditionalCode(Graph graph, Dictionary<string, Node> nodeById, List<ValidationWarning> warnings)
        {
            // Pre-index incoming-wired inputs so we can cheaply tell whether
            // a given (nodeId, socketName) has any upstream connection.
            var wiredInputs = new HashSet<(string nodeId, string socketName)>(
                graph.Links
                     .Select(l =>
                     {
                         var toNode   = FindNode(nodeById, l.ToNodeId);
                         var toSocket = toNode?.Sockets.FirstOrDefault(s => s.Id == l.ToSocketId);
                         return (l.ToNodeId, toSocket?.Name ?? string.Empty);
                     })
                     .Where(p => !string.IsNullOrEmpty(p.Item1) && !string.IsNullOrEmpty(p.Item2)));

            // Pre-build a (fromNodeId, fromSocketName) → downstream-node list
            // so when a branch is found unreachable we can name the head node
            // of the dead chain in the warning. Single-step only — we don't
            // walk the whole subtree because labelling the entry node is
            // enough to point the user at the dead code.
            var downstream = new Dictionary<(string fromNodeId, string fromSocketName), List<Node>>();
            foreach (var link in graph.Links)
            {
                var fromNode = FindNode(nodeById, link.FromNodeId);
                if (fromNode == null) continue;
                var fromSocket = fromNode.Sockets.FirstOrDefault(s => s.Id == link.FromSocketId);
                if (fromSocket == null || string.IsNullOrEmpty(fromSocket.Name)) continue;
                var toNode = FindNode(nodeById, link.ToNodeId);
                if (toNode == null) continue;

                var key = (link.FromNodeId, fromSocket.Name);
                if (!downstream.TryGetValue(key, out var list))
                {
                    list = new List<Node>();
                    downstream[key] = list;
                }
                list.Add(toNode);
            }

            // Flow-level dead-edge propagation. Walk every
            // outgoing flow link in the graph once and bucket it as either
            // "live" (originates from an event/start OR from a non-branching
            // flow output) or "dead" (originates from a statically-known
            // never-taken branch socket on Logic.If / Flow.Select). A node
            // is reported as unreachable ONLY when ALL its inbound flow
            // links are dead — i.e. no surviving live path can ever ring
            // it. Keeps the false-positive surface tiny: if a node has any
            // wire from a non-dead source, we say nothing.
            //
            // Seed dead edges directly from the branch evaluators: each
            // entry is (fromNodeId, fromSocketName) and represents a flow
            // socket on Logic.If / Flow.Select that constant-evaluates to
            // never-taken. The BFS below grows this set transitively.
            var deadEdges = new HashSet<(string fromNodeId, string fromSocketName)>();
            var deadEdgeHeadIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in graph.Nodes)
            {
                if (node.Title == "Logic.If")
                {
                    EvaluateLogicIf(node, wiredInputs, downstream, warnings, deadEdgeHeadIds, deadEdges);
                }
                else if (node.Title == "Flow.Select")
                {
                    EvaluateFlowSelect(node, wiredInputs, downstream, warnings, deadEdgeHeadIds, deadEdges);
                }
            }

            if (deadEdges.Count == 0) return;

            // Build inbound-flow-link adjacency so we can decide "is every
            // inbound flow on this node dead?" without rescanning Links.
            var inboundFlowLinks = new Dictionary<string, List<Link>>();
            foreach (var link in graph.Links)
            {
                var toNode = FindNode(nodeById, link.ToNodeId);
                if (toNode == null) continue;
                var toSocket = toNode.Sockets.FirstOrDefault(s => s.Id == link.ToSocketId);
                if (toSocket == null) continue;
                if (!SocketTypeHelper.IsFlowPin(toSocket)) continue;

                if (!inboundFlowLinks.TryGetValue(link.ToNodeId, out var list))
                {
                    list = new List<Link>();
                    inboundFlowLinks[link.ToNodeId] = list;
                }
                list.Add(link);
            }

            // BFS — repeatedly find nodes whose every inbound flow is dead
            // and propagate their outbound flow edges as dead too.
            var alreadyReported = new HashSet<string>(deadEdgeHeadIds);
            bool grew = true;
            int safetyBudget = graph.Nodes.Count * 4 + 16;
            while (grew && safetyBudget-- > 0)
            {
                grew = false;
                foreach (var kv in inboundFlowLinks)
                {
                    string nodeId = kv.Key;
                    if (alreadyReported.Contains(nodeId)) continue;
                    var links = kv.Value;
                    if (links.Count == 0) continue;

                    bool allDead = true;
                    foreach (var link in links)
                    {
                        var fromNode = FindNode(nodeById, link.FromNodeId);
                        var fromSocket = fromNode?.Sockets.FirstOrDefault(s => s.Id == link.FromSocketId);
                        var key = (link.FromNodeId, fromSocket?.Name ?? "");
                        if (!deadEdges.Contains(key)) { allDead = false; break; }
                    }
                    if (!allDead) continue;

                    var n = FindNode(nodeById, nodeId);
                    if (n == null) continue;
                    // Don't re-warn nodes the head pass already flagged.
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Warning,
                        Message  = $"Unreachable code: node '{n.Title}' is downstream of a branch that constant-evaluates to never-taken",
                        NodeId   = n.Id
                    });
                    alreadyReported.Add(nodeId);

                    // Propagate: every outbound flow socket on this node
                    // is now a dead edge too. Marking by (nodeId, socket)
                    // means any further node whose inbound is entirely
                    // covered by these dead edges joins the dead set on
                    // the next loop iteration.
                    foreach (var s in n.Sockets)
                    {
                        if (s.Type != SocketType.Output) continue;
                        if (!SocketTypeHelper.IsFlowPin(s)) continue;
                        deadEdges.Add((n.Id, s.Name ?? ""));
                    }
                    grew = true;
                }
            }
        }

        private static void EvaluateLogicIf(
            Node node,
            HashSet<(string nodeId, string socketName)> wiredInputs,
            Dictionary<(string fromNodeId, string fromSocketName), List<Node>> downstream,
            List<ValidationWarning> warnings,
            HashSet<string> deadEdgeHeadIds,
            HashSet<(string fromNodeId, string fromSocketName)> deadEdges)
        {
            // Both A and B must be constants — i.e. NOT wired AND have an
            // inline-pill value present in node.Attributes.
            if (wiredInputs.Contains((node.Id, "A"))) return;
            if (wiredInputs.Contains((node.Id, "B"))) return;

            string a = node.GetAttr("A", "");
            string b = node.GetAttr("B", "");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;

            string op = node.GetAttr("Operator", "==");

            bool? result = TryEvaluateComparison(a, b, op);
            if (result == null) return; // operator not recognised, or numeric op with non-numeric operands — bail.

            // result==true → False branch is unreachable; result==false → True branch is unreachable.
            string deadBranch = result.Value ? "False" : "True";

            // Mark the dead branch socket as a dead edge for the BFS pass
            // even if it has no immediate downstream — propagation still
            // reasons over the absence correctly.
            deadEdges.Add((node.Id, deadBranch));

            if (!downstream.TryGetValue((node.Id, deadBranch), out var deadHeads)) return;
            foreach (var head in deadHeads)
            {
                warnings.Add(new ValidationWarning
                {
                    Severity = ValidationSeverity.Warning,
                    Message  = $"Unreachable code: node '{head.Title}' is downstream of 'Logic.If' branch '{deadBranch}' that constant-evaluates to never-taken (A='{a}' {op} B='{b}')",
                    NodeId   = head.Id
                });
                deadEdgeHeadIds.Add(head.Id);
            }
        }

        private static void EvaluateFlowSelect(
            Node node,
            HashSet<(string nodeId, string socketName)> wiredInputs,
            Dictionary<(string fromNodeId, string fromSocketName), List<Node>> downstream,
            List<ValidationWarning> warnings,
            HashSet<string> deadEdgeHeadIds,
            HashSet<(string fromNodeId, string fromSocketName)> deadEdges)
        {
            // Flow.Select branches on Index; only constant when not wired
            // AND the Index attribute parses as an integer.
            if (wiredInputs.Contains((node.Id, "Index"))) return;
            string idxStr = node.GetAttr("Index", "");
            if (string.IsNullOrWhiteSpace(idxStr)) return;
            if (!int.TryParse(idxStr.Trim(), out int idx)) return;

            // Index → live branch socket name (0=A, 1=B, 2=C, 3=D).
            string[] branches = { "A", "B", "C", "D" };
            string? live = (idx >= 0 && idx < branches.Length) ? branches[idx] : null;

            foreach (var b in branches)
            {
                if (b == live) continue;
                // Mark this branch's outgoing edge as dead even if no
                // immediate downstream exists — keeps the BFS invariant
                // honest for any node further along the chain.
                deadEdges.Add((node.Id, b));

                if (!downstream.TryGetValue((node.Id, b), out var deadHeads)) continue;
                foreach (var head in deadHeads)
                {
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Warning,
                        Message  = $"Unreachable code: node '{head.Title}' is downstream of 'Flow.Select' branch '{b}' that constant-evaluates to never-taken (Index={idx})",
                        NodeId   = head.Id
                    });
                    deadEdgeHeadIds.Add(head.Id);
                }
            }
        }

        private static bool? TryEvaluateComparison(string a, string b, string op)
        {
            // String comparisons are universally defined; numeric ops require
            // both sides to parse as doubles. Match ScriptEngine's runtime
            // semantics so we don't drift from what the script will actually do.
            switch (op)
            {
                case "==": return string.Equals(a, b, StringComparison.Ordinal);
                case "!=": return !string.Equals(a, b, StringComparison.Ordinal);
                case ">":
                case "<":
                case ">=":
                case "<=":
                    if (!double.TryParse(a, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double da)) return null;
                    if (!double.TryParse(b, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double db)) return null;
                    return op switch
                    {
                        ">"  => da >  db,
                        "<"  => da <  db,
                        ">=" => da >= db,
                        "<=" => da <= db,
                        _    => (bool?)null
                    };
                default:
                    // Unknown operator — don't claim a branch is dead.
                    return null;
            }
        }

        // Macro.Call nodes carry a MacroId attribute; if the referenced macro
        // is missing from graph.Macros (deleted in a previous session, paste
        // from another graph, etc.) MacroCallHandler emits a comment and
        // silently skips the body. Surface that as a validator warning so the
        // user notices before running the script.
        private static void CheckMacroCallOrphans(Graph graph, List<ValidationWarning> warnings)
        {
            // graph.Macros can be null on an unhealed programmatic graph — the
            // sibling validator passes above guard it; an empty live set makes
            // every bound Macro.Call correctly warn as orphaned.
            var liveMacroIds = graph.Macros is null
                ? new HashSet<string>()
                : new HashSet<string>(graph.Macros.Select(m => m.MacroId));
            foreach (var node in graph.Nodes)
            {
                if (node.Title != "Macro.Call") continue;
                if (node.Attributes == null) continue;
                if (!node.Attributes.TryGetValue("MacroId", out var mid)) continue;
                if (string.IsNullOrEmpty(mid)) continue;
                if (liveMacroIds.Contains(mid)) continue;

                string shortId = mid.Length >= 8 ? mid.Substring(0, 8) : mid;
                string callName = node.Attributes.TryGetValue("MacroName", out var cn) && !string.IsNullOrEmpty(cn) ? cn : "Macro.Call";
                warnings.Add(new ValidationWarning
                {
                    Severity = ValidationSeverity.Warning,
                    Message  = $"Macro.Call '{callName}' references missing macro (MacroId={shortId}…) — body will not be inlined. Remove the call or restore the macro.",
                    NodeId   = node.Id
                });
            }
        }

        private static void CheckDanglingLinks(Graph graph, Dictionary<string, Node> nodeById, List<ValidationWarning> warnings)
        {
            foreach (var link in graph.Links)
            {
                var fromNode = FindNode(nodeById, link.FromNodeId);
                var toNode   = FindNode(nodeById, link.ToNodeId);

                if (fromNode == null)
                {
                    // Dangling links are rare — surface the FULL NodeId so the
                    // user can grep the .phxg by raw GUID for triage.
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Error,
                        Message  = $"Link references missing source node (FromNodeId={link.FromNodeId})"
                    });
                    continue;
                }
                if (toNode == null)
                {
                    // Dangling links are rare — surface the FULL NodeId so the
                    // user can grep the .phxg by raw GUID for triage.
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Error,
                        Message  = $"Link references missing target node (ToNodeId={link.ToNodeId})",
                        NodeId   = link.FromNodeId
                    });
                    continue;
                }

                if (fromNode.Sockets.All(s => s.Id != link.FromSocketId))
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Error,
                        Message  = $"Link from '{fromNode.Title}' references missing source socket",
                        NodeId   = fromNode.Id
                    });

                if (toNode.Sockets.All(s => s.Id != link.ToSocketId))
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Error,
                        Message  = $"Link to '{toNode.Title}' references missing target socket",
                        NodeId   = toNode.Id
                    });
            }
        }

        private static void CheckPlaceholderFrameContents(Graph graph, List<ValidationWarning> warnings)
        {
            // Placeholder frames are UI-only metadata; the exporter no longer skips
            // their contents. Surface a warning so the user notices any nodes that
            // are geometrically inside one but participating in live flow.
            //
            // The link-endpoint set hoists the per-node "any link touches this
            // node" probe out of the frame×node loop — set membership is exactly
            // the old graph.Links.Any(...) predicate. Built lazily so graphs
            // without placeholder frames pay nothing.
            HashSet<string>? linkedNodeIds = null;
            foreach (var frame in graph.Frames.Where(f => f.IsPlaceholder))
            {
                if (linkedNodeIds == null)
                {
                    linkedNodeIds = new HashSet<string>();
                    foreach (var l in graph.Links)
                    {
                        linkedNodeIds.Add(l.FromNodeId);
                        linkedNodeIds.Add(l.ToNodeId);
                    }
                }
                foreach (var node in graph.Nodes)
                {
                    if (!frame.Bounds.Contains(node.Location)) continue;
                    bool inFlow = linkedNodeIds.Contains(node.Id);
                    if (!inFlow) continue;
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Warning,
                        Message  = $"Node '{node.Title}' is inside a placeholder frame but participates in live flow — placeholder frames are UI-only and no longer affect export",
                        NodeId   = node.Id
                    });
                }
            }
        }

        private static void CheckDisconnectedFlowNodes(Graph graph, Dictionary<string, Node> nodeById, List<ValidationWarning> warnings)
        {
            // Find non-event, non-data nodes that have no inbound flow link
            var eventTitles = new HashSet<string>
            {
                "Twitch.ChatMessage","Chat.Message","Twitch.Subscription","Twitch.Resub","Twitch.GiftSub",
                "Twitch.GiftBomb","Twitch.Follow","Twitch.Raid","Twitch.Cheer","Twitch.PointRedeem",
                "YouTube.Message","System.Startup","Bus.OnMessage",
                "Event.Executor","HTTP.WebhookListener","Schedule.Cron",
                "Schedule.RunAt","Schedule.Recurring","State.OnChange","Visual.Trigger",
                "WS.Server",
                "System.Hotkey",
                "System.Clipboard",
                "OBS.Event",
                // Timer event roots — output-only, so they're skipped by the
                // flow-input check below anyway; listed for parity with the other
                // event roots and to document them as entry points.
                "Timer.OnZero","Timer.OnMilestone","Timer.OnAdd",
                // Queue / User-Management / Ranks / Soundboard event roots — same
                // output-only shape, listed for the same parity reason.
                "Queue.OnChanged","User.OnFirstMessage","Rank.OnRankUp","Soundboard.OnPlay",
            };
            // Every catalog-declared platform event (YouTube.*, Kick.*) is an
            // event root too — one line instead of 50 hand-listed titles.
            foreach (var def in Phoenix.Controls.Shared.Core.PlatformEventCatalog.Events)
                eventTitles.Add(def.Title);

            var nodesWithInboundFlow = new HashSet<string>(
                graph.Links
                    .Where(l =>
                    {
                        var toNode = FindNode(nodeById, l.ToNodeId);
                        if (toNode == null) return false;
                        var toSocket = toNode.Sockets.FirstOrDefault(s => s.Id == l.ToSocketId);
                        return toSocket != null && SocketTypeHelper.IsFlowPin(toSocket);
                    })
                    .Select(l => l.ToNodeId)
            );

            foreach (var node in graph.Nodes)
            {
                if (eventTitles.Contains(node.Title)) continue;
                if (node.Category == "Values" || node.Category == "Math" || node.Category == "Logic") continue;

                // Flow.Reroute and Flow.Select are passthrough/multiplex routing nodes
                // that can be wired as either flow OR data carriers. The exporter
                // resolves them through ResolveOutputFromNode (data) or via flow,
                // so a missing inbound flow link is not a failure mode.
                if (node.Title is "Flow.Reroute" or "Flow.Select") continue;

                bool hasFlowInput = node.Sockets.Any(s =>
                    s.Type == SocketType.Input && SocketTypeHelper.IsFlowPin(s));
                if (!hasFlowInput) continue;

                if (!nodesWithInboundFlow.Contains(node.Id))
                {
                    // Short id = first 8 chars of the GUID node id (or the full
                    // id if it's already shorter). Lets users locate the failing
                    // node in a 50-node graph without dumping the whole GUID.
                    var shortNodeId = node.Id.Length > 8 ? node.Id[..8] : node.Id;
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Warning,
                        Message  = $"Node '{node.Title}' (id {shortNodeId}) has no inbound flow connection — it will never execute",
                        NodeId   = node.Id
                    });
                }
            }
        }

        private static void CheckUnmatchedEventPairs(Graph graph, List<ValidationWarning> warnings)
        {
            var triggers  = graph.Nodes
                                       .Where(n => n.Title == "Event.Trigger"
                                               && n.GetAttr("DisableConnectionWarnings", "false") != "true")
                                       .Select(n => n.GetAttr("EventName", "")).Where(e => e.Length > 0).ToHashSet();
            var executors = graph.Nodes
                                       .Where(n => n.Title == "Event.Executor"
                                               && n.GetAttr("DisableConnectionWarnings", "false") != "true")
                                       .Select(n => n.GetAttr("EventName", "")).Where(e => e.Length > 0).ToHashSet();

            foreach (var t in triggers.Except(executors))
                warnings.Add(new ValidationWarning
                {
                    Severity = ValidationSeverity.Warning,
                    Message  = $"Event.Trigger '{t}' has no matching Event.Executor in this script (may be in another script)"
                });

            foreach (var e in executors.Except(triggers))
                warnings.Add(new ValidationWarning
                {
                    Severity = ValidationSeverity.Warning,
                    Message  = $"Event.Executor '{e}' has no matching Event.Trigger in this script (may be in another script)"
                });
        }

        private static void CheckIncompatibleLinks(Graph graph, Dictionary<string, Node> nodeById, List<ValidationWarning> warnings)
        {
            foreach (var link in graph.Links)
            {
                var fromNode = FindNode(nodeById, link.FromNodeId);
                var toNode   = FindNode(nodeById, link.ToNodeId);
                if (fromNode == null || toNode == null) continue;

                var fromSocket = fromNode.Sockets.FirstOrDefault(s => s.Id == link.FromSocketId);
                var toSocket   = toNode.Sockets.FirstOrDefault(s => s.Id == link.ToSocketId);
                if (fromSocket == null || toSocket == null) continue;

                // Flow.Reroute is an any-type passthrough — it adopts whatever type
                // the wiring demands at runtime. Suppress incompatibility warnings
                // on links that cross a Reroute on either side.
                if (fromNode.Title == "Flow.Reroute" || toNode.Title == "Flow.Reroute") continue;

                if (!NodeRegistry.AreCompatible(fromSocket.DataType, toSocket.DataType))
                    warnings.Add(new ValidationWarning
                    {
                        Severity = ValidationSeverity.Warning,
                        Message  = $"Incompatible socket types: '{fromNode.Title}.{fromSocket.Name}' ({fromSocket.DataType}) → '{toNode.Title}.{toSocket.Name}' ({toSocket.DataType})",
                        NodeId   = link.FromNodeId
                    });
            }
        }

        private static void CheckCircularFlow(Graph graph, Dictionary<string, Node> nodeById, List<ValidationWarning> warnings)
        {
            var flowSocketNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "flow","done","sent","active","out","completed","received","ontime","late",
                "1","2","3","branch1","branch2","branch3","true","false","both"
            };

            // Build adjacency: nodeId → list of flow-successor nodeIds
            var adj = new Dictionary<string, List<string>>();
            foreach (var node in graph.Nodes) adj[node.Id] = new List<string>();

            foreach (var link in graph.Links)
            {
                var fromNode = FindNode(nodeById, link.FromNodeId);
                if (fromNode == null) continue;
                var fromSocket = fromNode.Sockets.FirstOrDefault(s => s.Id == link.FromSocketId);
                if (fromSocket == null || !flowSocketNames.Contains(fromSocket.Name)) continue;
                if (adj.ContainsKey(link.FromNodeId))
                    adj[link.FromNodeId].Add(link.ToNodeId);
            }

            var visited  = new HashSet<string>();
            var inStack  = new HashSet<string>();
            bool cycleFound = false;

            void Dfs(string nodeId)
            {
                if (cycleFound || !adj.ContainsKey(nodeId)) return;
                if (inStack.Contains(nodeId)) { cycleFound = true; return; }
                if (visited.Contains(nodeId)) return;
                visited.Add(nodeId);
                inStack.Add(nodeId);
                foreach (var next in adj[nodeId]) Dfs(next);
                inStack.Remove(nodeId);
            }

            foreach (var node in graph.Nodes) Dfs(node.Id);

            if (cycleFound)
                warnings.Add(new ValidationWarning
                {
                    Severity = ValidationSeverity.Error,
                    Message  = "Circular flow path detected — the graph contains a flow cycle"
                });
        }
    }
}
