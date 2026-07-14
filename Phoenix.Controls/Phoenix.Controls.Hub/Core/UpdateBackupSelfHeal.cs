using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Startup rescue for installs whose user data was wiped by an update.
    ///
    /// <para>Until the 2026-07-14 P0 fix, the Updater's archive swap replaced
    /// the ENTIRE install root with the freshly extracted release tree — the
    /// user's scripts (<c>Hub\data\logic\</c>), layers, media and settings
    /// survived only inside the renamed-aside
    /// <c>&lt;installRoot&gt;.bak.&lt;stamp&gt;</c> sibling. The Updater now
    /// merges that state back itself (<c>UserStateMerge</c> in the Updater
    /// project), but two populations still need a Hub-side heal:</para>
    /// <list type="bullet">
    ///   <item>Installs that ALREADY updated with a wiping Updater — their
    ///         live tree is missing the user files, and the backup still has
    ///         them (unhealed backups get a 30-day prune grace, see
    ///         <c>UpdateRunner.UnhealedBackupRetentionDays</c>).</item>
    ///   <item>The next update FROM a pre-fix build: the swap is performed by
    ///         the OLD, in-place Updater, so it still wipes. The freshly
    ///         installed (fixed) Hub heals on its first launch.</item>
    /// </list>
    ///
    /// <para>The heal is strictly FILL-ONLY: a file is copied from a backup
    /// only when it does not exist in the live data tree. Nothing is ever
    /// overwritten — content the user changed since the update stays theirs,
    /// and a shipped seed occupying a path is never clobbered (we cannot know
    /// whether the user's backup copy was customized; conflicts are counted
    /// and surfaced in the log with the backup path for manual recovery).
    /// Each processed backup gets a marker file so a script the user
    /// deliberately deletes later does not resurrect on the next launch.</para>
    ///
    /// <para>ALL unmarked backups are drained, newest first. That is a
    /// deliberate rescue-maximal choice: after a chain of pre-fix updates the
    /// newest backup contains an already-wiped tree and the real user data
    /// sits only in an older sibling. The cost — a file deleted between two
    /// pre-fix updates can reappear once — is bounded to the first healed
    /// launch, because every backup is marked afterwards.</para>
    /// </summary>
    internal static class UpdateBackupSelfHeal
    {
        /// <summary>
        /// Marker dropped into a backup dir once it has been healed from.
        /// Keep in sync with <c>UpdateRunner.SelfHealMarkerFileName</c> in
        /// Phoenix.Controls.Updater (BCL-only project, no shared reference) —
        /// the Updater uses it to grant unhealed backups a longer prune grace.
        /// </summary>
        internal const string MarkerFileName = "phx-selfheal.done";

        internal sealed class SelfHealReport
        {
            public int BackupsSeen;
            public int BackupsProcessed;
            public int FilesRestored;
            /// <summary>
            /// Files present in BOTH a backup and the live tree that were left
            /// untouched (fill-only). Surfaced so a user who customized a
            /// shipped seed knows their copy still sits in the backup.
            /// </summary>
            public int ConflictsSkipped;
            public List<string> Failures { get; } = new();
        }

        /// <summary>
        /// Entry point called from HubBootstrapper before layers / scripts are
        /// enumerated. Never throws — a heal problem must not block Hub boot.
        /// </summary>
        internal static void Run()
        {
            try
            {
                string suiteRoot = UpdateChecker.ResolveSuiteRoot();
                SelfHealReport report = RunCore(suiteRoot);
                if (report.BackupsProcessed > 0 || report.FilesRestored > 0)
                {
                    GlobalLogger.Log(
                        $"Update-backup self-heal: restored {report.FilesRestored} user file(s) " +
                        $"from {report.BackupsProcessed} update backup(s).",
                        "UpdateBackupSelfHeal", LogLevel.System);
                }
                if (report.ConflictsSkipped > 0)
                {
                    GlobalLogger.Log(
                        $"Update-backup self-heal: {report.ConflictsSkipped} file(s) already exist in the live " +
                        $"data folder and were left untouched. If you had customized any of them before the " +
                        $"update, your copies are still inside the \"{Path.GetFileName(suiteRoot)}.bak.*\" " +
                        "folder(s) next to the install folder.",
                        "UpdateBackupSelfHeal", LogLevel.System);
                }
                foreach (string failure in report.Failures)
                {
                    GlobalLogger.Log($"Update-backup self-heal: {failure}",
                        "UpdateBackupSelfHeal", LogLevel.CriticalError);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UpdateBackupSelfHeal", "self-heal scan failed", ex);
            }
        }

        /// <summary>
        /// Scans <c>&lt;suiteRoot&gt;.bak.*</c> siblings (newest first) and
        /// fill-copies user data files that are missing from the live tree.
        /// Split from <see cref="Run"/> so tests can drive it against a
        /// scratch layout.
        /// </summary>
        internal static SelfHealReport RunCore(string suiteRoot)
        {
            var report = new SelfHealReport();

            suiteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(suiteRoot));
            string? parent = Path.GetDirectoryName(suiteRoot);
            string baseName = Path.GetFileName(suiteRoot);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(baseName)) return report;
            if (!Directory.Exists(parent)) return report;

            // Mirror the Updater's backup-name contract exactly
            // (UpdateRunner.ApplyArchiveSwap / TryPruneBackups):
            // "<baseName>.bak.yyyyMMddHHmmss", UTC stamp in the name.
            string prefix = $"{baseName}.bak.";
            var backups = new List<(DateTime StampUtc, string Dir)>();
            foreach (string dir in Directory.EnumerateDirectories(parent, $"{baseName}.bak.*"))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!DateTime.TryParseExact(name.Substring(prefix.Length), "yyyyMMddHHmmss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime stampUtc))
                {
                    continue; // unrecognised shape — not an Updater backup
                }
                backups.Add((stampUtc, dir));
            }
            report.BackupsSeen = backups.Count;
            if (backups.Count == 0) return report;

            // Newest first: for a file present in several backups the most
            // recent user state wins (fill-only, so the first copy sticks).
            backups.Sort((a, b) => b.StampUtc.CompareTo(a.StampUtc));

            // Release layout keeps Hub's data under Hub\data; a flat dev bin
            // has data\ at the root. Heal whichever roots exist per backup.
            string[] dataRootCandidates = { Path.Combine("Hub", "data"), "data" };

            // Default EnumerationOptions skips Hidden + System — hidden user
            // files must heal too; only reparse points are skipped.
            // IgnoreInaccessible keeps one unreadable subdir from killing the
            // walk; the per-root guard below records residual enumerator
            // faults without aborting the other roots / backups.
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach ((DateTime _, string backupDir) in backups)
            {
                string marker = Path.Combine(backupDir, MarkerFileName);
                if (File.Exists(marker)) continue;

                int restoredFromThisBackup = 0;
                foreach (string candidate in dataRootCandidates)
                {
                    string backupData = Path.Combine(backupDir, candidate);
                    if (!Directory.Exists(backupData)) continue;

                    string liveData = Path.Combine(suiteRoot, candidate);

                    var sourceFiles = new List<string>();
                    try
                    {
                        sourceFiles.AddRange(Directory.EnumerateFiles(backupData, "*", enumeration));
                    }
                    catch (Exception ex)
                    {
                        report.Failures.Add($"enumeration of {backupData} stopped early: {ex.Message}");
                    }

                    foreach (string sourceFile in sourceFiles)
                    {
                        string rel = Path.GetRelativePath(backupData, sourceFile);
                        try
                        {
                            string dest = Path.Combine(liveData, rel);
                            if (File.Exists(dest))
                            {
                                report.ConflictsSkipped++; // fill-only — never overwrite
                                continue;
                            }

                            string? destDir = Path.GetDirectoryName(dest);
                            if (destDir is not null) Directory.CreateDirectory(destDir);

                            // Temp + atomic rename: a mid-copy failure must
                            // not leave a truncated file that the fill-only
                            // rule would then treat as "already restored".
                            string tmp = dest + ".phx-restore-tmp";
                            try
                            {
                                File.Copy(sourceFile, tmp, overwrite: true);
                                File.Move(tmp, dest, overwrite: false);
                            }
                            finally
                            {
                                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                            }
                            restoredFromThisBackup++;
                        }
                        catch (Exception ex)
                        {
                            report.Failures.Add($"could not restore {rel} from {backupDir}: {ex.Message}");
                        }
                    }
                }

                report.FilesRestored += restoredFromThisBackup;
                report.BackupsProcessed++;

                try
                {
                    File.WriteAllText(marker,
                        $"processed {DateTime.UtcNow:O}; restored {restoredFromThisBackup} file(s); " +
                        "fill-only heal into " + suiteRoot + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    // Without the marker the backup is rescanned next launch —
                    // fill-only stays safe for existing files, but a file the
                    // user deletes could be re-copied until the marker lands
                    // or the backup is pruned. Surface it loudly.
                    report.Failures.Add($"could not write marker in {backupDir}: {ex.Message}");
                }
            }

            return report;
        }
    }
}
