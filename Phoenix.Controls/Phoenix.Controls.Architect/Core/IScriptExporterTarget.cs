using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// R10 (sweep 18) — strategy seam for exporting a <see cref="Graph"/>
    /// to its destination.
    ///
    /// Today there is exactly one implementation: <see cref="FileExportTarget"/>,
    /// which writes a <c>.phx</c> file to disk for Hub's <c>LogicWatcher</c> to
    /// pick up. The seam exists so a future "export to Hub bus" strategy can swap
    /// in without rewriting MainView's save path.
    ///
    /// Implementations own their I/O — they call <c>ScriptExporter.Export()</c>
    /// internally and decide where the rendered <c>.phx</c> string lands.
    /// MainView's in-app preview tab uses the exporter directly
    /// (no destination); it doesn't go through this seam.
    /// </summary>
    public interface IScriptExporterTarget
    {
        /// <param name="graph">
        /// The graph to export. Caller-owned; the target must not mutate it.
        /// </param>
        /// <param name="graphFilePath">
        /// Caller-supplied destination identifier. For <see cref="FileExportTarget"/>
        /// this is the absolute <c>.phx</c> path (the user's <c>SaveFileDialog</c>
        /// choice or the cached <c>_currentCommandPath</c>). A future bus strategy
        /// would likely treat this as a logical script name; the exact contract is
        /// per-implementation.
        /// </param>
        /// <param name="ct">
        /// Optional cancellation. Implementations honor it on a best-effort basis;
        /// short writes may complete before a cancellation can take effect.
        /// </param>
        Task ExportAsync(Graph graph, string graphFilePath, CancellationToken ct = default);
    }
}
