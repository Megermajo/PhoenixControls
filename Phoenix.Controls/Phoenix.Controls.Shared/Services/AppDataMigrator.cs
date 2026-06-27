using System;
using System.IO;
using System.Linq;
using System.Threading;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// One-shot, idempotent migration of pre-rebrand AppData folders into the
    /// new Phoenix Controls retail-brand layout.
    ///
    /// Pre-0.7.0 the suite wrote per-user state under two inconsistent roots
    /// in both <c>%AppData%</c> and <c>%LOCALAPPDATA%</c>:
    /// <list type="bullet">
    ///   <item><c>PhoenixSovereign/</c> — DB, language config, viewer creds, Visualist MRU.</item>
    ///   <item><c>Phoenix.Sovereign/</c> — dock layout, updater state, window state, recent files, thumbcaches.</item>
    /// </list>
    /// Both roots collapse into <c>PhoenixControls/</c> at the rebrand.
    ///
    /// <para>Design constraints:</para>
    /// <list type="bullet">
    ///   <item><b>Atomic where possible.</b> Same-volume moves use <see cref="Directory.Move"/> /
    ///         <see cref="File.Move"/> so the SQLite DB ships with its <c>-wal</c> / <c>-shm</c>
    ///         siblings together.</item>
    ///   <item><b>Never overwrite.</b> If a same-named entry already exists at the new path, the
    ///         old one is left in place. Defends against the case where the user manually created
    ///         the new folder, or a partial prior migration ran.</item>
    ///   <item><b>Crash-safe.</b> Permission failures, in-use file handles, cross-volume moves all
    ///         degrade gracefully — log + continue. Callers (Program.cs of each app) treat this as
    ///         best-effort: a fully-failed migration leaves the new path empty and Hub starts fresh.</item>
    ///   <item><b>Idempotent.</b> Safe to call on every startup. After a successful first run the
    ///         old roots are gone, so subsequent calls fall straight through.</item>
    ///   <item><b>Defence-in-depth.</b> The installer's Pascal Script also runs an equivalent
    ///         migration (so first launch already finds data at the new path), and every entry-point
    ///         <c>Program.cs</c> calls <see cref="RunOnce"/> before any service initialises — so a
    ///         user who side-loads binaries without running the installer still gets migrated.</item>
    /// </list>
    /// </summary>
    public static class AppDataMigrator
    {
        // Old root names used by the pre-rebrand suite. Order matters: when both
        // roots have entries with the same name (rare — they don't overlap by
        // design), the one listed first wins.
        private static readonly string[] LegacyRootNames = { "PhoenixSovereign", "Phoenix.Sovereign" };

        // Process-wide guard so RunOnce() is genuinely once per process even
        // when called from multiple Program.cs entry points (some test
        // configurations spin up multiple hosts in the same process).
        private static int s_ranOnce;

        /// <summary>
        /// Migrate legacy <c>PhoenixSovereign/</c> and <c>Phoenix.Sovereign/</c> folders
        /// (Roaming + Local AppData) into the current <c>PhoenixControls/</c> layout.
        /// Safe and idempotent — call from every entry-point <c>Program.cs</c> at the top of <c>Main</c>.
        /// </summary>
        public static void RunOnce()
        {
            if (Interlocked.Exchange(ref s_ranOnce, 1) != 0) return;

            try
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                MigrateRoot(roaming, Paths.AppDataFolderName);
                MigrateRoot(local,   Paths.AppDataFolderName);
            }
            catch (Exception ex)
            {
                // Last-ditch — never let a migration failure crash startup.
                SafeLog($"AppDataMigrator: unexpected failure: {ex.Message}");
            }
        }

        /// <summary>
        /// Test seam: migrate from <paramref name="baseDir"/> rooted at the supplied
        /// <paramref name="newRootName"/>. Bypasses the once-per-process guard so tests
        /// can run multiple scenarios in sequence.
        /// </summary>
        internal static void MigrateRootForTest(string baseDir, string newRootName)
            => MigrateRoot(baseDir, newRootName);

        private static void MigrateRoot(string baseDir, string newRootName)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            string newRoot = Path.Combine(baseDir, newRootName);

            foreach (string legacyName in LegacyRootNames)
            {
                string oldRoot = Path.Combine(baseDir, legacyName);
                if (!Directory.Exists(oldRoot)) continue;
                if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    Directory.CreateDirectory(newRoot);
                    MergeInto(oldRoot, newRoot);
                    TryDeleteIfEmpty(oldRoot);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // GPO-locked AppData or open file handle. Don't crash startup;
                    // log + continue. User keeps old data; new path stays empty
                    // (or partially populated) until they resolve the perm issue.
                    SafeLog($"AppDataMigrator: access denied migrating '{oldRoot}' → '{newRoot}': {ex.Message}");
                }
                catch (IOException ex)
                {
                    SafeLog($"AppDataMigrator: io error migrating '{oldRoot}' → '{newRoot}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Merge <paramref name="src"/>'s contents into <paramref name="dst"/>. Same-named entries
        /// already present in <paramref name="dst"/> are NEVER overwritten — the new install is
        /// authoritative once it has a same-named entry. Non-conflicting entries are atomic-renamed
        /// where the volumes match; cross-volume falls back to copy + best-effort delete.
        /// </summary>
        private static void MergeInto(string src, string dst)
        {
            // Files at the current level.
            foreach (string filePath in Directory.GetFiles(src))
            {
                string name   = Path.GetFileName(filePath);
                string target = Path.Combine(dst, name);
                if (File.Exists(target))
                {
                    // Don't overwrite the new install's file. Leave the legacy
                    // copy at the old path so the user can see/recover it.
                    continue;
                }
                try
                {
                    File.Move(filePath, target);
                }
                catch (IOException)
                {
                    // Cross-volume or in-use file handle. Copy + delete.
                    try
                    {
                        File.Copy(filePath, target, overwrite: false);
                        try { File.Delete(filePath); } catch { /* leave behind */ }
                    }
                    catch (IOException ex2)
                    {
                        SafeLog($"AppDataMigrator: failed to move file '{filePath}' → '{target}': {ex2.Message}");
                    }
                }
            }

            // Subdirectories.
            foreach (string dirPath in Directory.GetDirectories(src))
            {
                string name   = Path.GetFileName(dirPath);
                string target = Path.Combine(dst, name);

                if (Directory.Exists(target))
                {
                    // Both old + new have this subfolder — recurse and merge entry-by-entry.
                    MergeInto(dirPath, target);
                    TryDeleteIfEmpty(dirPath);
                }
                else
                {
                    try
                    {
                        Directory.Move(dirPath, target);
                    }
                    catch (IOException)
                    {
                        // Cross-volume or in-use child. Recursive copy then best-effort delete.
                        try
                        {
                            CopyDirectory(dirPath, target);
                            try { Directory.Delete(dirPath, recursive: true); }
                            catch { /* leave behind, log */ }
                        }
                        catch (IOException ex)
                        {
                            SafeLog($"AppDataMigrator: failed to move dir '{dirPath}' → '{target}': {ex.Message}");
                        }
                    }
                }
            }
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string filePath in Directory.GetFiles(src))
            {
                string name   = Path.GetFileName(filePath);
                string target = Path.Combine(dst, name);
                if (File.Exists(target)) continue;
                // [P1 swarm-audit 2026-05-29] File.Copy can throw
                // NotSupported/Argument/UnauthorizedAccess/IO uncaught and crash
                // the whole migration. Log the offending file and continue so one
                // bad child doesn't abort copying the rest of the tree.
                try
                {
                    File.Copy(filePath, target, overwrite: false);
                    // Preserve last-write timestamp so AppData-driven cache invalidation
                    // logic (e.g., MediaLibrary thumbcache keys based on mtime) still
                    // matches after the move.
                    try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(filePath)); } catch { }
                }
                catch (Exception ex)
                {
                    SafeLog($"AppDataMigrator: failed to copy file '{filePath}' → '{target}': {ex.Message}");
                }
            }
            foreach (string dirPath in Directory.GetDirectories(src))
            {
                string name   = Path.GetFileName(dirPath);
                string target = Path.Combine(dst, name);
                // [P1 swarm-audit 2026-05-29] Guard the recursive descent the same
                // way as File.Copy above so a permission-denied / in-use child dir
                // doesn't abort copying the rest of the tree (crash-safe degradation).
                try
                {
                    CopyDirectory(dirPath, target);
                }
                catch (Exception ex)
                {
                    SafeLog($"AppDataMigrator: failed to copy subdirectory '{dirPath}' → '{target}': {ex.Message}");
                }
            }
        }

        private static void TryDeleteIfEmpty(string dir)
        {
            try
            {
                // Early-exit emptiness check: EnumerateFileSystemEntries short-circuits
                // on the first entry, avoiding the full-array allocation/enumeration that
                // GetFileSystemEntries(dir).Length forces on large legacy AppData folders.
                if (Directory.Exists(dir)
                    && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch { /* leave behind */ }
        }

        /// <summary>
        /// GlobalLogger may not be initialised yet when the migrator runs (it's
        /// invoked from Main() before any service touches AppData). Fall back to
        /// a no-throw stderr write so we still get diagnostics from a dev console.
        /// </summary>
        private static void SafeLog(string message)
        {
            try
            {
                GlobalLogger.Log(message, "AppDataMigrator", LogLevel.System);
            }
            catch
            {
                try { Console.Error.WriteLine(message); } catch { }
            }
        }
    }
}
