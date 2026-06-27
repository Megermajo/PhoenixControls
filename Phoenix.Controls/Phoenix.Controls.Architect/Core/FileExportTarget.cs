using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// R10 (sweep 18) — the file-on-disk implementation of
    /// <see cref="IScriptExporterTarget"/>. Renders the graph via
    /// <see cref="ScriptExporter.Export"/> and writes the <c>.phx</c> string to
    /// the caller-supplied path. Hub's <c>LogicWatcher</c> picks the file up via
    /// <c>FileSystemWatcher</c> and dispatches it through the script engine.
    ///
    /// 0.11.x freeze-sweep follow-up — exporter walk + file write run on
    /// the thread pool via <c>Task.Run</c> so a slow OneDrive / AV-scanned
    /// path can't block the UI thread that triggered the save. Pre-fix the
    /// method returned <see cref="Task.CompletedTask"/> AFTER a synchronous
    /// disk write, defeating any caller's <c>await</c>.
    /// </summary>
    public sealed class FileExportTarget : IScriptExporterTarget
    {
        public Task ExportAsync(Graph graph, string graphFilePath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var exporter = new ScriptExporter(graph);
                    string script = exporter.Export();
                    ct.ThrowIfCancellationRequested();
                    File.WriteAllText(graphFilePath, script);
                }
                catch (OperationCanceledException)
                {
                    // Cooperative cancellation is not a fault — let it surface on
                    // the returned Task as cancellation, not as a logged error.
                    throw;
                }
                catch (Exception ex)
                {
                    // Mirror GraphSerializer.SaveGraphAsync: pre-fix the exporter
                    // walk + disk write threw on the thread pool with no try/catch,
                    // so an export/IO fault faulted the returned Task unobserved and
                    // the caller's await saw a save that "succeeded". Log through
                    // GlobalLogger and rethrow so the failure stays on the returned
                    // Task for the caller / UX to react to.
                    GlobalLogger.Error(
                        "FileExportTarget",
                        $"Export to .phx failed for {Path.GetFileName(graphFilePath)}", ex);
                    throw;
                }
            }, ct);
        }
    }
}
