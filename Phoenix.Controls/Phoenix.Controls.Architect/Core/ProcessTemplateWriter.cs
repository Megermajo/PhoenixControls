using System;
using System.Collections.Generic;
using System.IO;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Single source of truth for writing a graph's export in the live-process
    /// model: the main <c>.phx</c> beside the graph PLUS one standalone template
    /// per Process. Templates live under a PER-GRAPH subfolder
    /// <c>processes/&lt;graphStem&gt;/&lt;ProcessId&gt;.phx</c> so stale-template
    /// pruning only ever touches the current graph's own templates — the shared
    /// <c>processes/</c> root holds every graph's processes, so a flat layout would
    /// let one graph's export delete another graph's templates.
    ///
    /// The Hub's <c>ProcessTemplateRegistry</c> scans <c>processes/</c> recursively
    /// and keys templates by their GUID stem (the ProcessId), so the per-graph
    /// subfolder is transparent at runtime. The Hub's normal (top-level only)
    /// script loader never sees these because they sit under a subfolder.
    /// </summary>
    public static class ProcessTemplateWriter
    {
        public const string ProcessesFolderName = "processes";

        /// <summary>
        /// Exports <paramref name="graph"/> and writes the main script to
        /// <paramref name="graphFilePath"/> plus its process templates. Throws on
        /// IO failure (callers mirror the prior single-write error contract).
        /// </summary>
        public static void WriteExport(Graph graph, string graphFilePath)
        {
            var result = new ScriptExporter(graph).ExportAll();
            File.WriteAllText(graphFilePath, result.MainScript);

            string? dir = Path.GetDirectoryName(graphFilePath);
            if (string.IsNullOrEmpty(dir)) return;

            string graphStem = Path.GetFileNameWithoutExtension(graphFilePath);
            string procDir = Path.Combine(dir, ProcessesFolderName, graphStem);

            if (result.ProcessTemplates.Count == 0)
            {
                // No processes left — prune any templates this graph used to own.
                PruneStale(procDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                return;
            }

            Directory.CreateDirectory(procDir);
            foreach (var kv in result.ProcessTemplates)
                File.WriteAllText(Path.Combine(procDir, kv.Key + ".phx"), kv.Value);

            PruneStale(procDir, new HashSet<string>(result.ProcessIds, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>Deletes GUID-stemmed templates in <paramref name="procDir"/> whose
        /// id is not in <paramref name="currentIds"/>. Only GUID-stemmed files are
        /// touched, so a user-dropped file in the folder is never removed.</summary>
        private static void PruneStale(string procDir, HashSet<string> currentIds)
        {
            if (!Directory.Exists(procDir)) return;
            foreach (var file in Directory.GetFiles(procDir, "*.phx"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(stem, out _) && !currentIds.Contains(stem))
                {
                    try { File.Delete(file); }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("ProcessTemplateWriter",
                            $"failed to prune stale process template {Path.GetFileName(file)}", ex);
                    }
                }
            }
        }
    }
}
