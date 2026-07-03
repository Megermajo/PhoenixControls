// Framework types carved from ExporterRegistry.cs ().
// Owns: IExporterHandler, ExporterContext, SocketArg,
//   SimpleEmitDescriptor, SimpleEmitHandler, ExporterRegistry.

using System;
using System.Collections.Generic;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    #region Framework

    public interface IExporterHandler
    {
        string NodeTitle { get; }
        void Emit(Node node, int indent, string prefix, ExporterContext ctx);
    }

    /// <summary>
    /// Facade exposing the ScriptExporter helpers that handlers need.
    /// All members forward to the owning ScriptExporter; the visited and
    /// blocked-for-branch sets are exposed by reference so EmitBranch
    /// semantics are preserved.
    /// </summary>
    public sealed class ExporterContext
    {
        private readonly ScriptExporter _e;
        internal ExporterContext(ScriptExporter e) { _e = e; }

        public void Emit(string line) => _e.CtxEmit(line);
        public void AppendRawLine(string line) => _e.CtxAppendRawLine(line);
        // Handlers route ValidationWarnings through the same surface the
        // GraphValidator pre-pass uses, so the user sees them in the script header.
        public void AddRuntimeWarning(string message, string? nodeId = null)
            => _e.CtxAddRuntimeWarning(message, nodeId);

        public string Resolve(Node n, string socket, string fallback)
            => _e.CtxResolveInputValue(n, socket, fallback);

        public string Materialize(Node n, string socket, string fallback)
            => _e.CtxMaterializeInput(n, socket, fallback);

        public void FollowNamed(Node n, string outName, int indent)
            => _e.CtxFollowNamedOutput(n, outName, indent);

        public void FollowFlow(Node n, int indent)
            => _e.CtxFollowFlowOutput(n, indent);

        public Node? GetNamedTarget(Node n, string outName)
            => _e.CtxGetNamedOutputTarget(n, outName);

        public Node? GetTargetNode(string nodeId, string socketId)
            => _e.CtxGetTargetNode(nodeId, socketId);

        // ARCH-P2-HANDLER-SCANS — O(1) check for "does this output socket have any
        // outgoing wire", backed by the exporter's per-export link index. Replaces
        // per-socket `Graph.Links.Any(...)` scans in handlers.
        public bool IsOutputConnected(string nodeId, string socketId)
            => _e.CtxIsOutputConnected(nodeId, socketId);

        public void EmitBranch(Node n, string trueOut, string falseOut,
            string prefix, int indent, string truePfx, string? elsePfx)
            => _e.CtxEmitBranch(n, trueOut, falseOut, prefix, indent, truePfx, elsePfx);

        /// <summary>
        /// Emits a conditional `if {cond}: ... else: ...` for handlers whose
        /// shape is "test something, then route to True/False outs". Handles
        /// the empty-body cases that callers used to mishandle:
        ///   - Both outs unwired → emit nothing.
        ///   - Only False wired → emit a single negated `if not (cond):`.
        ///   - Only True  wired → emit `if {cond}:` with no `else:`.
        ///   - Both wired       → standard if/else (delegates to EmitBranch).
        /// </summary>
        public void EmitConditional(Node n, string condition,
            string trueOut, string falseOut, string prefix, int indent)
        {
            var trueT  = GetNamedTarget(n, trueOut);
            var falseT = GetNamedTarget(n, falseOut);
            if (trueT == null && falseT == null) return;
            if (trueT == null)
            {
                Emit($"{prefix}if not ({condition}):");
                ProcessNode(falseT!, indent + 1);
                return;
            }
            Emit($"{prefix}if {condition}:");
            EmitBranch(n, trueOut, falseOut, prefix, indent, "if", "else");
        }

        public void ProcessNode(Node n, int indent)
            => _e.CtxProcessNode(n, indent);

        public string CommandName(string title) => ScriptExporter.CtxCommandName(title);
        public string ComputeInline(Node n) => _e.CtxComputeInlineValue(n);
        public string GetDbGetResultVar(Node n) => ScriptExporter.CtxGetDbGetResultVar(n);
        public string StripQuotes(string s) => ScriptExporter.CtxStripQuotes(s);
        public string IdPrefix(Node n, int chars = 12) => ScriptExporter.CtxIdPrefix(n, chars);
        public string EscapeStringLiteral(string s) => ScriptExporter.CtxEscapeStringLiteral(s);
        public string SanitizeIdentifier(string s) => ScriptExporter.CtxSanitizeIdentifier(s);

        public string ExportMacroSubGraph(Graph macroGraph, string macroContextId)
            => _e.CtxExportMacroSubGraph(macroGraph, macroContextId);

        public HashSet<string> Visited => _e.CtxVisited;
        public HashSet<string> BlockedForBranch => _e.CtxBlockedForBranch;
        public Dictionary<string, string> NodeResultVars => _e.CtxNodeResultVars;
        public Graph Graph => _e.CtxGraph;
        public int CurrentIndent => _e.CtxCurrentIndent;
        public string MacroContextId => _e.CtxMacroContextId;
    }

    public sealed record SocketArg(
        string SocketName,
        string Fallback,
        bool UseMaterialize = false);

    /// <summary>
    /// Declarative descriptor for "simple emit" nodes — the ~90 nodes whose
    /// case body is `Emit($"{prefix}cmd({Resolve(...)}...)"); FollowFlow(...)`.
    /// Branching/loop nodes use <see cref="IExporterHandler"/> directly.
    /// </summary>
    public sealed record SimpleEmitDescriptor(
        string NodeTitle,
        string CommandName,
        IReadOnlyList<SocketArg> Args,
        string? FollowNamedOutput = null,
        bool UseFlowOutputFallback = true);

    public sealed class SimpleEmitHandler : IExporterHandler
    {
        public SimpleEmitDescriptor Descriptor { get; }
        public SimpleEmitHandler(SimpleEmitDescriptor d) { Descriptor = d; }
        public string NodeTitle => Descriptor.NodeTitle;

        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            var args = new string[Descriptor.Args.Count];
            for (int i = 0; i < Descriptor.Args.Count; i++)
            {
                var a = Descriptor.Args[i];
                args[i] = a.UseMaterialize
                    ? ctx.Materialize(node, a.SocketName, a.Fallback)
                    : ctx.Resolve(node, a.SocketName, a.Fallback);
            }

            ctx.Emit($"{prefix}{Descriptor.CommandName}({string.Join(", ", args)})");

            if (Descriptor.FollowNamedOutput != null)
                ctx.FollowNamed(node, Descriptor.FollowNamedOutput, indent);
            else if (Descriptor.UseFlowOutputFallback)
                ctx.FollowFlow(node, indent);
        }
    }

    public sealed class ExporterRegistry
    {
        private readonly Dictionary<string, IExporterHandler> _handlers
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// When false (default for production), a node Title with no registered
        /// handler hard-fails the export. When true, falls back to the legacy
        /// silent log() placeholder behaviour — only useful for in-development
        /// graphs being authored against unimplemented nodes.
        /// </summary>
        public bool AllowPlaceholderFallback { get; set; } = false;

        // Defensive against future plugin APIs registering a handler whose
        // NodeTitle collides with a built-in: throw unless the caller explicitly
        // asks for replacement. Catches authoring mistakes early instead of
        // silently shadowing a built-in handler.
        public void Register(IExporterHandler handler, bool replaceExisting = false)
        {
            if (!replaceExisting && _handlers.ContainsKey(handler.NodeTitle))
                throw new System.InvalidOperationException(
                    $"ExporterRegistry: handler for '{handler.NodeTitle}' already registered. " +
                    "Pass replaceExisting: true to override (rare — typically a name collision).");
            _handlers[handler.NodeTitle] = handler;
        }

        public void RegisterSimple(SimpleEmitDescriptor d)
            => Register(new SimpleEmitHandler(d));

        public bool TryGet(string title, out IExporterHandler handler)
            => _handlers.TryGetValue(title, out handler!);

        public IReadOnlyDictionary<string, IExporterHandler> All => _handlers;
    }

    #endregion
}
