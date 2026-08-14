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
    ///         them (unhealed backups are content-verified before pruning and
    ///         retried each launch, see <c>UpdateRunner.TryPruneBackups</c>).</item>
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
        /// the Updater uses it to relax the prune verify gate for this backup
        /// from content-sensitive to existence-only.
        /// </summary>
        internal const string MarkerFileName = "phx-selfheal.done";

        /// <summary>
        /// First path segments under a data root that the RELEASE payload owns
        /// (<c>data\overlay\*</c>, <c>data\streamerbot\*</c>). Keep in sync with
        /// <c>UserStateMerge.ReleaseOwnedSubtrees</c> in Phoenix.Controls.Updater
        /// (BCL-only project, no shared reference) — duplicated by hand for the
        /// same reason <see cref="MarkerFileName"/> is.
        /// <para>Inside these subtrees the shipped file wins every conflict by
        /// design: <c>UserStateMerge.Merge</c> keeps the freshly extracted copy
        /// (counted as <c>ReleaseKept</c>) so a stale backup of e.g.
        /// <c>overlay\compositor.js</c> can never mask the version the new Hub
        /// build expects. A backup therefore ALWAYS differs from live here after
        /// any release that touched the overlay runtime — that difference is
        /// expected, not unrestored user data, and must not withhold the heal
        /// marker. <c>UpdateRunner.BackupHoldsUnrestoredUserData</c> applies the
        /// identical rule on the prune side.</para>
        /// </summary>
        private static readonly string[] ReleaseOwnedSubtrees = { "overlay", "streamerbot" };

        /// <summary>
        /// True when the first segment of <paramref name="relativePath"/>
        /// (relative to a data root) names a release-owned subtree. Mirror of
        /// <c>UserStateMerge.IsReleaseOwned</c>. A top-level file (e.g.
        /// <c>config.json</c>) is user state, never release-owned.
        /// </summary>
        internal static bool IsReleaseOwned(string relativePath)
        {
            int cut = relativePath.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (cut <= 0) return false;
            string first = relativePath.Substring(0, cut);
            foreach (string owned in ReleaseOwnedSubtrees)
            {
                if (string.Equals(first, owned, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

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
            /// <summary>
            /// Subset of <see cref="ConflictsSkipped"/> where the backup copy
            /// DIFFERS byte-for-byte from the live file — the backup holds a
            /// distinct user version (e.g. a customized shipped seed that a
            /// wipe replaced with the stock seed). Any of these keeps the backup
            /// UNMARKED so it is never pruned while it is the only copy.
            /// Release-owned paths (<c>overlay\</c>, <c>streamerbot\</c>) are
            /// excluded — there the shipped copy wins by design, so a difference
            /// is expected rather than lost user work.
            /// </summary>
            public int ConflictsDiffering;
            /// <summary>
            /// Backups that were scanned but NOT fully drained — a copy failed,
            /// an enumeration faulted, or a differing conflict occurred — so the
            /// heal marker was deliberately withheld. Leaving them unmarked keeps
            /// the stronger content-sensitive verify-before-delete protection and
            /// forces a retry next launch, closing the 2026-07-16
            /// marker-before-verify P0.
            /// </summary>
            public int BackupsRetainedForRecovery;
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
                if (report.BackupsRetainedForRecovery > 0)
                {
                    // Persistent (re-logged each launch until resolved) notice —
                    // the audit flagged the old one-shot log. These backups are
                    // deliberately NOT marked healed, so within the prune window
                    // TryPruneBackups keeps them while they remain the only copy;
                    // the 7-day hard age cap eventually clears them, so the notice
                    // urges timely recovery rather than promising indefinite keep.
                    GlobalLogger.Log(
                        $"Update-backup self-heal: {report.BackupsRetainedForRecovery} update backup(s) still hold " +
                        "user file(s) that could not be auto-restored (a newer/different file already occupies that " +
                        "path, or a copy failed). Your originals are kept in the " +
                        $"\"{Path.GetFileName(suiteRoot)}.bak.*\" folder(s) next to the install folder and are retried " +
                        "on the next launch — recover them soon, as update backups are cleared about a week after they are made.",
                        "UpdateBackupSelfHeal", LogLevel.CriticalError);
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
                // A backup is marked "healed" ONLY when it was FULLY drained —
                // every source file either restored, or already present in the
                // live tree with byte-IDENTICAL content. A copy failure, an
                // enumeration fault, or a conflict against a DIFFERING live file
                // (e.g. a freshly shipped seed now occupying a path the user had
                // customized) means the backup still holds the only copy of user
                // work. Such a backup is left UNMARKED so it keeps the stronger
                // content-sensitive verify-before-delete protection and is retried
                // next launch (fill-only makes retry safe). Fix for the 2026-07-16
                // marker-before-verify P0: the marker used to be written
                // unconditionally, which both blocked retry and flipped the prune
                // gate to the weaker existence-only check (a healed backup does not
                // block on a present-but-differing file), so TryPruneBackups could
                // delete a sole or customized copy of a .phx/.phxlayer.
                bool cleanDrain = true;
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
                        // Files after the fault point were never enumerated, so
                        // they were never attempted — the backup is not fully
                        // drained and must stay unmarked / re-scannable.
                        cleanDrain = false;
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
                                // Inside a release-owned subtree (overlay /
                                // streamerbot) the shipped file winning is the
                                // designed outcome of UserStateMerge, so the
                                // backup's older copy differing is EXPECTED and
                                // is not unrestored user data. Counting it would
                                // withhold the marker forever and raise the
                                // "possible data loss" CriticalError after every
                                // release that touched the overlay runtime.
                                // Same rule as UpdateRunner.BackupHoldsUnrestoredUserData.
                                if (IsReleaseOwned(rel)) continue;
                                // A byte-identical live copy is a benign skip
                                // (the file genuinely IS present live). A
                                // DIFFERING live copy means the backup holds a
                                // distinct user version we must not lose, so the
                                // backup stays unmarked and kept for recovery.
                                if (!FilesContentEqual(sourceFile, dest))
                                {
                                    report.ConflictsDiffering++;
                                    cleanDrain = false;
                                }
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
                            // This file never reached the live tree — the backup
                            // is still its only home, so don't mark it healed.
                            cleanDrain = false;
                            report.Failures.Add($"could not restore {rel} from {backupDir}: {ex.Message}");
                        }
                    }
                }

                report.FilesRestored += restoredFromThisBackup;
                report.BackupsProcessed++;

                if (cleanDrain)
                {
                    // Fully drained — safe to mark healed. The marker relaxes
                    // TryPruneBackups' verify gate from content-sensitive to
                    // existence-only for this backup: a later user edit that makes
                    // a live file differ no longer pins the (now redundant) backup
                    // on disk.
                    try
                    {
                        File.WriteAllText(marker,
                            $"processed {DateTime.UtcNow:O}; restored {restoredFromThisBackup} file(s); " +
                            "fill-only heal into " + suiteRoot + Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        // Marker write failed — leave the backup unmarked. It
                        // keeps the stronger content-sensitive prune protection
                        // and is retried next launch; a later clean drain
                        // re-attempts the marker.
                        report.Failures.Add($"could not write marker in {backupDir}: {ex.Message}");
                    }
                }
                else
                {
                    // Not fully drained: the backup still holds the only copy of
                    // one or more user files. Leave it UNMARKED so it keeps the
                    // stronger content-sensitive verify-before-delete protection (a
                    // present-but-differing live file blocks its prune), and the
                    // next launch retries.
                    report.BackupsRetainedForRecovery++;
                }
            }

            return report;
        }

        /// <summary>
        /// Byte-for-byte content comparison used to tell a benign conflict-skip
        /// (the same file is already present live) from a lossy one (the backup
        /// holds a DIFFERENT user version that a wipe replaced with a shipped
        /// seed). Length is checked first as a cheap reject. Any IO problem is
        /// treated conservatively as "differing" so a backup we cannot verify is
        /// never mistaken for a clean drain and pruned.
        /// </summary>
        internal static bool FilesContentEqual(string pathA, string pathB)
        {
            try
            {
                if (new FileInfo(pathA).Length != new FileInfo(pathB).Length) return false;

                const int chunk = 64 * 1024;
                using var a = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var b = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bufA = new byte[chunk];
                var bufB = new byte[chunk];
                int n;
                while ((n = ReadBlock(a, bufA)) > 0)
                {
                    int m = ReadBlock(b, bufB);
                    if (m != n) return false;
                    if (!bufA.AsSpan(0, n).SequenceEqual(bufB.AsSpan(0, m))) return false;
                }
                return true;
            }
            catch
            {
                return false; // unknown ⇒ treat as differing (retain the backup)
            }
        }

        /// <summary>
        /// Reads up to <paramref name="buf"/>.Length bytes, looping over short
        /// reads so a partial <see cref="Stream.Read(byte[], int, int)"/> does
        /// not produce a false content mismatch. Returns the total read (0 at EOF).
        /// </summary>
        private static int ReadBlock(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int r = s.Read(buf, total, buf.Length - total);
                if (r == 0) break;
                total += r;
            }
            return total;
        }
    }
}
