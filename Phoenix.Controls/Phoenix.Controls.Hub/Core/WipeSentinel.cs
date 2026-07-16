using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Loud, best-effort detector for "the install tree looks WIPED, not fresh."
    ///
    /// <para>The 2026-07-14 P0 was invisible because the release payload ships
    /// seed scripts into <c>data\logic\</c> but (essentially) no seed layers:
    /// after a wipe, <c>logic\</c> was repainted with shipped seeds and looked
    /// alive while <c>layers\</c> sat empty. Nothing distinguished a wiped
    /// install from a fresh one, so Hub booted silently on seeds and the user
    /// only discovered the loss later. <see cref="UpdateBackupSelfHeal"/> +
    /// the Updater's <c>UserStateMerge</c> recover wipes that left a
    /// <c>&lt;root&gt;.bak.*</c> backup; this covers the case where NO backup
    /// survives (a non-Updater wipe, or a backup already pruned) — the one path
    /// the heals cannot rescue and used to boot in total silence.</para>
    ///
    /// <para>It keeps a small fingerprint of the user's authored files (basenames
    /// of <c>.phx</c>/<c>.phxg</c> in logic, <c>.phxlayer</c> in layers) in
    /// <c>%AppData%\PhoenixControls\</c> — OUTSIDE the install tree, so it
    /// survives the swap. On each boot (AFTER self-heal has had its chance to
    /// restore) it compares the previous fingerprint to the live tree: if files
    /// present last session have disappeared, it raises a loud
    /// <see cref="LogLevel.CriticalError"/> notice instead of booting silently.
    /// It never deletes, restores, or blocks — purely a warning — and is
    /// deliberately conservative (needs a real drop, not a single delete) so it
    /// does not cry wolf on ordinary editing.</para>
    /// </summary>
    internal static class WipeSentinel
    {
        internal const string SentinelFileName = "wipe-sentinel.json";

        /// <summary>
        /// A drop of this many authored files (or a populated dir emptied
        /// entirely) reads as a wipe rather than ordinary editing. Kept
        /// conservative to avoid false alarms on single intentional deletes.
        /// </summary>
        internal const int MissingWarnThreshold = 3;

        internal sealed class Fingerprint
        {
            public string SavedAtUtc { get; set; } = "";
            public List<string> Logic { get; set; } = new();
            public List<string> Layers { get; set; } = new();
        }

        internal sealed class CheckResult
        {
            public bool Warned;
            public int MissingLogic;
            public int MissingLayers;
            public bool BackupAvailable;
            public bool HadPreviousFingerprint;
        }

        /// <summary>
        /// Production entry point — resolves the live data dirs, suite root and
        /// sentinel path, then runs <see cref="CheckCore"/>. Never throws; a
        /// sentinel fault must not block Hub boot.
        /// </summary>
        internal static void Run()
        {
            try
            {
                CheckCore(
                    Paths.HubLogic,
                    Paths.HubLayers,
                    UpdateChecker.ResolveSuiteRoot(),
                    Paths.RoamingAppData(SentinelFileName));
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WipeSentinel", "sentinel check failed", ex);
            }
        }

        /// <summary>
        /// Compares the live logic/layer trees against the persisted fingerprint,
        /// logs a loud warning when a wipe is detected, then rewrites the
        /// fingerprint to the current tree. Split out so tests can drive it
        /// against scratch dirs. <paramref name="log"/> off suppresses the
        /// GlobalLogger call for tests.
        /// </summary>
        internal static CheckResult CheckCore(string logicDir, string layersDir, string suiteRoot,
                                              string sentinelPath, bool log = true)
        {
            var result = new CheckResult();

            var current = new Fingerprint
            {
                SavedAtUtc = DateTime.UtcNow.ToString("O"),
                Logic = UserFiles(logicDir, ".phx", ".phxg"),
                Layers = UserFiles(layersDir, ".phxlayer"),
            };

            Fingerprint? previous = TryLoad(sentinelPath);
            if (previous is not null)
            {
                result.HadPreviousFingerprint = true;

                var missingLogic = Missing(previous.Logic, current.Logic);
                var missingLayers = Missing(previous.Layers, current.Layers);
                result.MissingLogic = missingLogic.Count;
                result.MissingLayers = missingLayers.Count;
                result.BackupAvailable = BackupExists(suiteRoot);

                bool logicWiped = IsWipe(previous.Logic.Count, current.Logic.Count, missingLogic.Count);
                bool layersWiped = IsWipe(previous.Layers.Count, current.Layers.Count, missingLayers.Count);
                if (logicWiped || layersWiped)
                {
                    result.Warned = true;
                    if (log) LogWarning(missingLogic, missingLayers, result.BackupAvailable, suiteRoot);
                }
            }

            // Reset the baseline to the current tree — a one-shot warning. The
            // persistent recovery channel for recoverable wipes is
            // UpdateBackupSelfHeal's re-logged notice; this sentinel exists to
            // surface the UNrecoverable (no-backup) case once, loudly.
            TrySave(sentinelPath, current);
            return result;
        }

        private static bool IsWipe(int prevCount, int currCount, int missingCount)
        {
            if (missingCount == 0) return false;
            // Many authored files vanished, OR a dir that clearly held user work
            // was emptied entirely.
            return missingCount >= MissingWarnThreshold
                || (prevCount >= 2 && currCount == 0);
        }

        private static List<string> UserFiles(string dir, params string[] extensions)
        {
            var names = new List<string>();
            try
            {
                if (!Directory.Exists(dir)) return names;
                foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    string ext = Path.GetExtension(path);
                    foreach (string want in extensions)
                    {
                        if (string.Equals(ext, want, StringComparison.OrdinalIgnoreCase))
                        {
                            names.Add(Path.GetFileName(path));
                            break;
                        }
                    }
                }
            }
            catch { /* best-effort — a listing fault must not block boot */ }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static List<string> Missing(List<string> previous, List<string> current)
        {
            var live = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
            return previous.Where(n => !live.Contains(n)).ToList();
        }

        private static bool BackupExists(string suiteRoot)
        {
            try
            {
                suiteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(suiteRoot));
                string? parent = Path.GetDirectoryName(suiteRoot);
                string baseName = Path.GetFileName(suiteRoot);
                if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(baseName)) return false;
                if (!Directory.Exists(parent)) return false;
                return Directory.EnumerateDirectories(parent, $"{baseName}.bak.*").Any();
            }
            catch { return false; }
        }

        private static void LogWarning(List<string> missingLogic, List<string> missingLayers,
                                       bool backupAvailable, string suiteRoot)
        {
            var sb = new StringBuilder();
            sb.Append("Possible data loss: files present last session are missing now — ");
            if (missingLogic.Count > 0) sb.Append($"{missingLogic.Count} script(s) (e.g. {Sample(missingLogic)}) ");
            if (missingLogic.Count > 0 && missingLayers.Count > 0) sb.Append("and ");
            if (missingLayers.Count > 0) sb.Append($"{missingLayers.Count} layer(s) (e.g. {Sample(missingLayers)}) ");
            sb.Append("are gone from the live data folder. ");
            if (backupAvailable)
                sb.Append($"An update backup was found next to the install (\"{Path.GetFileName(suiteRoot)}.bak.*\") — your originals should be inside it.");
            else
                sb.Append("No update backup was found. If you did not remove these on purpose, they may have been removed by an update or an external tool (antivirus / file sync).");
            GlobalLogger.Log(sb.ToString(), "WipeSentinel", LogLevel.CriticalError);
        }

        private static string Sample(List<string> names) => string.Join(", ", names.Take(3));

        private static Fingerprint? TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<Fingerprint>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        private static void TrySave(string path, Fingerprint fp)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(fp));
                File.Move(tmp, path, overwrite: true);
            }
            catch { /* best-effort — never block boot */ }
        }
    }
}
