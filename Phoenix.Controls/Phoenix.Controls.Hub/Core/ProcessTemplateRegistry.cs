using System;
using System.Collections.Concurrent;
using System.IO;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Loads the live-process TEMPLATES Architect writes under
    /// <c>data/logic/processes/&lt;graph&gt;/&lt;ProcessId&gt;.phx</c> and serves them
    /// by ProcessId. Templates are NOT normal scripts: the top-level
    /// <see cref="ScriptRegistry"/> loader is non-recursive so it never picks them up;
    /// <see cref="ProcessInstanceManager"/> pulls a template here at
    /// <c>process.start</c> time and runs it per live instance.
    /// </summary>
    public sealed class ProcessTemplateRegistry
    {
        private static ProcessTemplateRegistry? _instance;
        public static ProcessTemplateRegistry Instance => _instance ??= new ProcessTemplateRegistry();

        public const string ProcessesFolderName = "processes";

        // ProcessId (GUID stem, case-insensitive) → template .phx content.
        private readonly ConcurrentDictionary<string, string> _templates =
            new(StringComparer.OrdinalIgnoreCase);

        private string _logicPath = "";

        private ProcessTemplateRegistry() { }

        /// <summary>Scans <c>&lt;logicPath&gt;/processes</c> recursively and (re)builds
        /// the ProcessId → template map.</summary>
        public void Load(string logicPath)
        {
            _logicPath = logicPath ?? "";
            _templates.Clear();
            if (string.IsNullOrEmpty(_logicPath)) return;

            string procRoot = Path.Combine(_logicPath, ProcessesFolderName);
            if (!Directory.Exists(procRoot)) return;

            string[] files;
            try { files = Directory.GetFiles(procRoot, "*.phx", SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                GlobalLogger.Error("ProcessTemplate", $"failed to enumerate '{procRoot}'", ex);
                return;
            }

            int n = 0;
            foreach (var file in files)
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(stem, out _)) continue; // only GUID-stemmed templates
                try
                {
                    _templates[stem] = ScriptRegistry.ReadAllTextStable(file);
                    n++;
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("ProcessTemplate", $"read failed for '{Path.GetFileName(file)}'", ex);
                }
            }
            if (n > 0)
                GlobalLogger.Log($"ProcessTemplateRegistry: loaded {n} process template(s).",
                    "ProcessTemplate", LogLevel.System);
        }

        /// <summary>Reloads from the last-known logic path (wired to LogicWatcher).</summary>
        public void Refresh()
        {
            if (!string.IsNullOrEmpty(_logicPath)) Load(_logicPath);
        }

        /// <summary>Template content for a ProcessId, or null if unknown.</summary>
        public string? GetContent(string processId)
            => !string.IsNullOrEmpty(processId) && _templates.TryGetValue(processId, out var c) ? c : null;

        /// <summary>True if <paramref name="fullPath"/> lives anywhere under a
        /// "processes" folder — used to defensively keep templates out of the
        /// normal script / scheduler scans.</summary>
        public static bool IsUnderProcessesFolder(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            var dir = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (string.Equals(Path.GetFileName(dir), ProcessesFolderName, StringComparison.OrdinalIgnoreCase))
                    return true;
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }
    }
}
