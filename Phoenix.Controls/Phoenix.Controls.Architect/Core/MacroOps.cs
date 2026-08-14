using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Pure helpers for graph-level macro mutations. Centralised here so that
    /// every caller (MainView delete handlers, Canvas paste/refresh,
    /// the audit test suite) drives the same code path.
    /// </summary>
    public static class MacroOps
    {
        /// <summary>
        /// Removes a macro from <paramref name="graph"/>.Macros and scrubs
        /// every Macro.Call node whose MacroId attribute still points at it.
        /// Also strips the parameter sockets that were derived from the macro's
        /// Entry/Exit signature plus any links that referenced those sockets,
        /// returning each affected Call node to the bare Flow-in/Flow-out
        /// shape that <c>NodeRegistry.CreateNode("Macro.Call", ...)</c> produces.
        /// Without this, deleted macros leave Call nodes with orphan sockets
        /// whose wires lead nowhere — and <c>GraphValidator.CheckMacroCallOrphans</c>
        /// can no longer flag them because the MacroId attribute is gone.
        /// </summary>
        public static void DeleteMacro(Graph graph, string macroId)
        {
            if (graph == null || string.IsNullOrEmpty(macroId)) return;

            // Same explicit-null class as the exporter-side Macros guards —
            // editor graphs are load-healed, but this helper also runs on
            // programmatic graphs.
            graph.Macros?.RemoveAll(m => m.MacroId == macroId);
            if (graph.Nodes is null) return;

            foreach (var node in graph.Nodes)
            {
                if (node == null) continue; // guard null node before deref
                if (node.Title != "Macro.Call") continue;
                if (node.Attributes == null) continue;
                if (!node.Attributes.TryGetValue("MacroId", out var mid) || mid != macroId) continue;

                node.Attributes.Remove("MacroId");

                // Drop parameter sockets — anything that isn't the canonical
                // Flow socket and isn't a placeholder. The bare Flow in/out
                // pair is what NodeRegistry.CreateNode("Macro.Call", ...)
                // produces, so we leave it intact.
                var droppedSocketIds = node.Sockets
                    .Where(s => s.Name != "Flow" && !s.IsPlaceholder)
                    .Select(s => s.Id)
                    .ToHashSet();
                if (droppedSocketIds.Count == 0) continue;

                node.Sockets.RemoveAll(s => droppedSocketIds.Contains(s.Id));
                graph.Links.RemoveAll(l =>
                    (l.FromNodeId == node.Id && droppedSocketIds.Contains(l.FromSocketId)) ||
                    (l.ToNodeId   == node.Id && droppedSocketIds.Contains(l.ToSocketId)));
            }
        }

        /// <summary>
        /// Applies a Hub-canonical <paramref name="canonical"/> macro library to
        /// <paramref name="globalMacros"/>, propagating remote deletions while
        /// preserving local-only entries that the Hub has never seen.
        ///
        /// <paramref name="hubAckedIds"/> tracks which MacroIds the Hub has
        /// confirmed in its canonical broadcasts. The contract:
        /// <list type="bullet">
        ///   <item>A local macro whose id is in <paramref name="hubAckedIds"/> but
        ///         absent from <paramref name="canonical"/> was remotely deleted —
        ///         remove it from <paramref name="globalMacros"/>.</item>
        ///   <item>A local macro whose id is NOT in <paramref name="hubAckedIds"/>
        ///         is local-only (not yet published) — keep it.</item>
        ///   <item>Every macro in <paramref name="canonical"/> is added or updated
        ///         in <paramref name="globalMacros"/> (id-keyed).</item>
        ///   <item>After application, <paramref name="hubAckedIds"/> is replaced
        ///         with exactly the canonical id-set so the next sync judges
        ///         deletions against the latest Hub view.</item>
        /// </list>
        ///
        /// Returns the list of MacroIds removed from <paramref name="globalMacros"/>
        /// so the caller can decorate the UI (refresh the macro list panel, etc.)
        /// without scanning the diff itself. Does NOT mutate any Graph —
        /// open-document macros and their Macro.Call references remain untouched
        /// so the user's working copy survives a peer's deletion.
        /// </summary>
        public static IReadOnlyList<string> ApplyCanonicalGlobalMacros(
            List<Macro> globalMacros,
            HashSet<string> hubAckedIds,
            IEnumerable<Macro> canonical)
        {
            var removed = new List<string>();
            if (globalMacros == null || hubAckedIds == null || canonical == null)
                return removed;

            var canonicalList = canonical as IList<Macro> ?? canonical.ToList();
            var canonicalIds  = new HashSet<string>(canonicalList.Select(m => m.MacroId));

            // Step 1: remote-deletion sweep. Anything previously Hub-acked that
            // dropped out of canonical was deleted by a peer — deletion
            // propagates purely by diffing against the canonical library that
            // arrives via MACRO_SYNC; Architect never emits MACRO_REMOVE.
            // Local-only macros (not in hubAckedIds) are preserved.
            for (int i = globalMacros.Count - 1; i >= 0; i--)
            {
                var id = globalMacros[i].MacroId;
                if (hubAckedIds.Contains(id) && !canonicalIds.Contains(id))
                {
                    removed.Add(id);
                    globalMacros.RemoveAt(i);
                }
            }

            // Step 2: add-or-update from canonical (id-keyed merge).
            foreach (var m in canonicalList)
            {
                int idx = globalMacros.FindIndex(g => g.MacroId == m.MacroId);
                if (idx >= 0) globalMacros[idx] = m;
                else          globalMacros.Add(m);
            }

            // Step 3: snapshot the new Hub-canonical id set so the next sync
            // judges deletions against the latest authoritative view.
            hubAckedIds.Clear();
            foreach (var id in canonicalIds) hubAckedIds.Add(id);

            return removed;
        }
    }
}
