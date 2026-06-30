using System.Collections.Generic;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Result of <see cref="ScriptExporter.ExportAll"/>: the main `.phx` plus one
    /// standalone TEMPLATE per Process (live-process model). The export target
    /// writes <see cref="MainScript"/> beside the graph and each
    /// <see cref="ProcessTemplates"/> entry to <c>processes/&lt;ProcessId&gt;.phx</c>;
    /// <see cref="ProcessIds"/> is the current id set used to prune stale templates
    /// for processes that were deleted/renamed.
    /// </summary>
    public sealed class ProcessExportResult
    {
        public string MainScript { get; }
        public IReadOnlyDictionary<string, string> ProcessTemplates { get; }
        public IReadOnlyList<string> ProcessIds { get; }

        public ProcessExportResult(
            string mainScript,
            IReadOnlyDictionary<string, string> processTemplates,
            IReadOnlyList<string> processIds)
        {
            MainScript = mainScript;
            ProcessTemplates = processTemplates;
            ProcessIds = processIds;
        }
    }
}
