using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json.Serialization;

namespace Phoenix.Controls.Shared.Models
{
    public enum SocketType { Input, Output }

    // Append-only — ordinals are persisted in .phxg / .phxlayer files. Insertions
    // anywhere except the end will silently re-map types in saved graphs.
    public enum SocketDataType { Flow, String, Int, Float, Bool, Collection, Any, Image, Color, Scalar, Vector2, Vector3, Vector4, Audio }

    public class Socket
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public SocketType Type { get; set; }
        public SocketDataType DataType { get; set; } = SocketDataType.Any;
        public Color Color { get; set; } = Color.White;

        // UI Position (Offset relative to Node)
        public Point Offset { get; set; }

        /// <summary>
        /// When true this socket is an inactive placeholder shown in grey.
        /// It becomes a real socket the moment a wire is connected to/from it.
        /// Used by Event.Trigger and Event.Executor for their dynamic variable sockets.
        /// </summary>
        public bool IsPlaceholder { get; set; } = false;

        /// <summary>
        /// Optional human-readable description of what this socket carries — surfaced
        /// in NodeDocumentationForm (Inputs/Outputs sections) and as the canvas hover
        /// tooltip when the cursor lands on a socket. Backfill on non-obvious cases:
        /// per-iteration semantics (Flow.ForEach.Item), wildcard-resolved sockets,
        /// anything where the socket name alone doesn't convey the contract. Empty
        /// means "fall back to node-level description / no socket-specific text".
        /// Persisted on each Socket so authored .phxg files keep the
        /// override even if the registry default later changes.
        ///
        /// JsonIgnoreCondition.WhenWritingDefault keeps the .phxg footprint identical
        /// to before this field existed for any socket that doesn't carry a
        /// description — empty strings round-trip silently rather than ballooning
        /// every saved graph.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string Description { get; set; } = "";
    }

    public class Node
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "New Node";
        public string Category { get; set; } = "General";
        public Point Location { get; set; }
        public Size Size { get; set; } = new Size(160, 100);
        public Color HeaderColor { get; set; } = Color.FromArgb(45, 45, 48);

        public List<Socket> Sockets { get; set; } = new List<Socket>();
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Per-instance description override. When non-empty this is the text the
        /// LogicInspector renders below the eyebrow + title. When empty, the inspector
        /// falls back to the registered <c>NodeTemplate.Description</c> via
        /// NodeRegistry. Persisted on each Node so authored graphs can carry an
        /// instance-specific clarification (e.g. "fires when subscriber count
        /// crosses the 100-mark") without rewriting the template.
        ///
        /// JsonIgnoreCondition.WhenWritingDefault keeps the .phxg footprint
        /// identical to before this field existed for any node that doesn't carry
        /// an override.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string Description { get; set; } = "";
    }

    public class Link
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FromNodeId { get; set; } = "";
        public string FromSocketId { get; set; } = "";
        public string ToNodeId { get; set; } = "";
        public string ToSocketId { get; set; } = "";
    }

    /// <summary>A labeled comment/frame region drawn behind nodes on the canvas.</summary>
    public class Frame
    {
        public string Id    { get; set; } = Guid.NewGuid().ToString();
        public string Label { get; set; } = "Comment";
        public Rectangle Bounds { get; set; } = new Rectangle(0, 0, 300, 200);
        public Color FrameColor { get; set; } = Color.FromArgb(255, 200, 80); // Default: amber

        /// <summary>
        /// When true the frame acts as a "placeholder" section: nodes inside are
        /// silently skipped by the script exporter and flow terminates at the frame boundary.
        /// </summary>
        public bool IsPlaceholder { get; set; } = false;

        /// <summary>
        /// Per-frame z-order within the FrameLayer ( P2-A4 — "Bring to Front" /
        /// "Send to Back" entries on the frame context menu). Default 0 mirrors pre-fix
        /// behaviour (insertion-order); higher values paint on top. Architect's
        /// FrameLayer applies this via Canvas.SetZIndex on each frame view.
        /// </summary>
        public int ZOrder { get; set; } = 0;
    }

    /// <summary>A named viewport bookmark for quick canvas navigation (Ctrl+B).</summary>
    public class GraphBookmark
    {
        public string Id   { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Bookmark";

        /// <summary>Canvas pan offset saved with this bookmark.</summary>
        public Point ViewOffset { get; set; } = new Point(0, 0);

        /// <summary>Canvas zoom level saved with this bookmark.</summary>
        public float Zoom { get; set; } = 1.0f;
    }

    /// <summary>A reusable sub-graph embedded inside a parent graph, callable via a Macro.Call node.</summary>
    public class Macro
    {
        public string MacroId { get; set; } = Guid.NewGuid().ToString();
        public string Name    { get; set; } = "NewMacro";

        /// <summary>The inner graph of nodes and links that this macro contains.</summary>
        public Graph Graph { get; set; } = new Graph();

        /// <summary>Promoted input port names surfaced on the Macro.Call node.</summary>
        public List<string> InputNames  { get; set; } = new List<string>();

        /// <summary>Promoted output port names surfaced on the Macro.Call node.</summary>
        public List<string> OutputNames { get; set; } = new List<string>();
    }

    /// <summary>
    /// A named, detached, async-spawnable sub-graph embedded inside a parent
    /// graph. Process.Spawn launches the body on its own cancellation token
    /// (fire-and-forget); the parent script continues past the spawn point
    /// without waiting. Authored in a macro-editor-style ProcessEditorForm —
    /// the inner graph is bracketed by Process.Entry / Process.Exit markers
    /// (parallel to Macro.Entry / Macro.Exit), and var-in/var-out shapes will
    /// surface on the Process.Spawn call site in a follow-up sweep.
    ///
    /// Sister type to Macro: same persistence story (lives on the
    /// parent <see cref="Graph.Processes"/> list, round-trips through
    /// GraphSerializer alongside Macros), same authoring window pattern.
    /// Diverges in runtime: macros inline-expand into the calling script,
    /// processes spawn detached via the engine's `process_spawn:` block.
    /// </summary>
    public class Process
    {
        public string ProcessId { get; set; } = Guid.NewGuid().ToString();
        public string Name      { get; set; } = "NewProcess";

        /// <summary>The inner graph of nodes and links that this process contains.</summary>
        public Graph Graph { get; set; } = new Graph();

        /// <summary>Process-level input arg names surfaced on the Process.Spawn node (phase-2 wiring).</summary>
        public List<string> InputNames  { get; set; } = new List<string>();

        /// <summary>Process-level result names surfaced on the Process.Spawn node (phase-2 wiring).</summary>
        public List<string> OutputNames { get; set; } = new List<string>();
    }

    /// <summary>A graph-scoped variable shown in the Variables panel and used by Var.Get / Var.Set nodes.</summary>
    public class VariableDefinition
    {
        public string Name         { get; set; } = "MyVar";

        /// <summary>One of: String, Number, Bool</summary>
        public string Type         { get; set; } = "String";

        public string DefaultValue { get; set; } = "";
    }

    public class Graph
    {
        public string Name { get; set; } = "New Logic Graph";
        public List<Node>      Nodes     { get; set; } = new List<Node>();
        public List<Link>      Links     { get; set; } = new List<Link>();
        public List<Frame>     Frames    { get; set; } = new List<Frame>();
        public List<GraphBookmark>      Bookmarks { get; set; } = new List<GraphBookmark>();
        public List<Macro>     Macros    { get; set; } = new List<Macro>();
        public List<Process>   Processes { get; set; } = new List<Process>();
        public List<VariableDefinition> Variables { get; set; } = new List<VariableDefinition>();

        /// <summary>Saved canvas pan and zoom — restored when the graph is reopened.</summary>
        public int   ViewOffsetX { get; set; } = 0;
        public int   ViewOffsetY { get; set; } = 0;
        public float ViewZoom    { get; set; } = 1.0f;

        /// <summary>
        ///  Set by <c>GraphSerializer.LoadGraph</c> when the file on
        /// disk was saved by a newer schema version than the running Architect
        /// understands. The canvas may still display the placeholder graph so
        /// the user sees the file opened, but <c>GraphSerializer.SaveGraphAsync</c>
        /// refuses to overwrite the source file — without this flag the silent
        /// "return new Graph()" path would let a save round-trip clobber the
        /// newer-format payload on the very next autosave. JsonIgnore so the
        /// flag never leaks into the wire format itself.
        /// </summary>
        [JsonIgnore]
        public bool IsReadOnly { get; set; }

        // ─────────────────────────────────────────────────────────────────
        // Pattern A — id-indexed lookup caches.
        // O(N) `Nodes.Find(n => n.Id == ...)` / `node.Sockets.Find(s => s.Id == ...)`
        // appears 17× in Canvas, mostly inside per-link / per-paint / per-traversal
        // loops. With 50 nodes and a few hundred links per graph, those quadratic scans
        // dominate paint cost. The dictionaries below cache id → node and id → socket;
        // both are lazily (re)built on first access after a structural change and are
        // [JsonIgnore]'d so they never bleed into the .phxg / .phxlayer wire format.
        //
        // Contract: any mutator that adds/removes a node or socket — or replaces a
        // node/socket id — must call <see cref="MarkStructuralChange"/>. Lookups are
        // self-healing in the failure case (entry missing → rebuild and retry once)
        // but the explicit invalidation is what keeps per-paint lookups O(1).
        // ─────────────────────────────────────────────────────────────────
        [JsonIgnore]
        private Dictionary<string, Node>? _nodesById;
        [JsonIgnore]
        private Dictionary<string, Socket>? _socketsById;

        /// <summary>
        /// Invalidate the id-indexed caches. Call after any mutation that adds or
        /// removes a node, adds or removes a socket on any node, or rewrites a
        /// node/socket Id. Cheap — caches rebuild lazily on next lookup.
        /// </summary>
        public void MarkStructuralChange()
        {
            _nodesById = null;
            _socketsById = null;
        }

        /// <summary>
        /// O(1) lookup for a node by Id. Returns null when missing or when id is null/empty.
        /// Auto-builds the cache on first call; callers don't need to seed anything.
        /// </summary>
        public Node? FindNodeById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var dict = _nodesById ??= BuildNodesById();
            if (dict.TryGetValue(id, out var n)) return n;
            // Self-heal: a mutator forgot to call MarkStructuralChange. Rebuild
            // once and retry — preserves correctness while exposing missed callers
            // as a paint-cost regression (which the next profile run would catch).
            _nodesById = BuildNodesById();
            return _nodesById.TryGetValue(id, out var n2) ? n2 : null;
        }

        /// <summary>
        /// O(1) lookup for a socket by Id across every node. Returns null when missing
        /// or when id is null/empty. Same self-healing contract as <see cref="FindNodeById"/>.
        /// </summary>
        public Socket? FindSocketById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var dict = _socketsById ??= BuildSocketsById();
            if (dict.TryGetValue(id, out var s)) return s;
            _socketsById = BuildSocketsById();
            return _socketsById.TryGetValue(id, out var s2) ? s2 : null;
        }

        private Dictionary<string, Node> BuildNodesById()
        {
            if (Nodes == null) return new Dictionary<string, Node>();
            var dict = new Dictionary<string, Node>(Nodes.Count);
            foreach (var n in Nodes)
                if (n != null && !string.IsNullOrEmpty(n.Id)) dict[n.Id] = n;
            return dict;
        }

        private Dictionary<string, Socket> BuildSocketsById()
        {
            if (Nodes == null) return new Dictionary<string, Socket>();
            // Compute the actual total socket count once so the dictionary is
            // sized correctly up-front and avoids growth/rehashing on rebuild.
            int totalSockets = 0;
            foreach (var n in Nodes)
                if (n?.Sockets != null) totalSockets += n.Sockets.Count;
            var dict = new Dictionary<string, Socket>(totalSockets);
            foreach (var n in Nodes)
            {
                if (n?.Sockets == null) continue;
                foreach (var s in n.Sockets)
                    if (s != null && !string.IsNullOrEmpty(s.Id)) dict[s.Id] = s;
            }
            return dict;
        }
    }

    /// <summary>
    /// Single source of truth for "is this socket an execution-flow pin?".
    /// Replaces three previously-divergent sources:
    ///   - Phoenix.Controls.Architect.Controls.Canvas.ExecutionPinNames
    ///   - Phoenix.Controls.Architect.Core.ScriptExporter.FlowSocketNames
    ///   - inline `socket.Color == NodeRegistry.ColExec` checks
    /// Newer graphs should rely on SocketDataType.Flow exclusively; the name
    /// list is a back-compat fallback for legacy graphs that pre-date the
    /// SocketDataType field.
    /// </summary>
    public static class SocketTypeHelper
    {
        private static readonly HashSet<string> _legacyFlowNames =
            new(System.StringComparer.OrdinalIgnoreCase)
            {
                "flow", "done", "sent", "active", "out", "completed", "received",
                "ontime", "late", "true", "false", "both",
                "1", "2", "3", "branch1", "branch2", "branch3"
            };

        /// <summary>True when the socket carries execution flow rather than data.</summary>
        public static bool IsFlowPin(Socket socket)
        {
            if (socket == null) return false;
            if (socket.DataType == SocketDataType.Flow) return true;
            return _legacyFlowNames.Contains(socket.Name ?? string.Empty);
        }

        /// <summary>True when the socket name (case-insensitive) is one of the historical flow-pin names.</summary>
        public static bool IsFlowPinName(string name)
            => name != null && _legacyFlowNames.Contains(name);
    }
}
