using System;
using System.Collections.Generic;
using System.IO;

namespace Phoenix.Controls.Updater;

/// <summary>
/// Post-swap user-state preservation.
///
/// <para><see cref="UpdateRunner.ApplyArchiveSwap"/> replaces the ENTIRE
/// install root with the freshly extracted release tree — the prior tree is
/// renamed aside to <c>&lt;installRoot&gt;.bak.&lt;stamp&gt;</c>. Everything
/// the user authored under the install tree therefore vanishes from the live
/// install on every update: scripts in <c>Hub\data\logic\</c>, layers in
/// <c>Hub\data\layers\</c>, media, script backups. The release zip only ships
/// seed content (examples, the default GiveawayTicket script, overlay
/// runtime), so without this merge an update silently deletes the user's
/// working set (P0, reported 2026-07-14).</para>
///
/// <para>This helper runs after a successful swap and merges the backup's
/// data tree back into the new install:</para>
/// <list type="bullet">
///   <item>A backup file the new tree does NOT ship → copied over
///         (user-authored content survives).</item>
///   <item>A conflict inside a release-owned subtree (<c>overlay/</c>,
///         <c>streamerbot/</c>) → the freshly shipped file stays
///         authoritative (runtime code must keep updating).</item>
///   <item>Any other conflict (<c>config.json</c>, <c>logic/</c>,
///         <c>layers/</c>, <c>media/</c>, <c>assets/</c>, …) → the user's
///         copy wins. The shipped defaults only matter for fresh installs;
///         on an update the user's working set is sacred. (Accepted
///         tradeoff: seed-script fixes shipped under these roots never
///         reach existing installs.)</item>
/// </list>
///
/// <para>Merge failures are never fatal to the update — every unrestored file
/// still exists in the retained backup, and the caller logs the backup path
/// for manual recovery. BCL-only on purpose (the Updater carries no
/// Phoenix.Controls.Shared reference).</para>
/// </summary>
internal static class UserStateMerge
{
    /// <summary>
    /// Data roots relative to the install root, in the order they are probed.
    /// The installer / Releases layout keeps the suite under per-pillar
    /// folders (<c>Hub\data</c>); a flat dev bin has <c>data\</c> at the
    /// root. Every candidate that exists in the backup is merged.
    /// </summary>
    internal static readonly string[] DataRootCandidates =
    {
        Path.Combine("Hub", "data"),
        "data",
    };

    /// <summary>
    /// First path segments under a data root that the release payload owns.
    /// On conflict the freshly extracted file is kept — a stale backup copy
    /// of e.g. <c>overlay\compositor.js</c> must never mask the version the
    /// new Hub build expects. Files under these subtrees that the payload
    /// does NOT ship (user-added files) are still restored.
    /// <c>assets/</c> is deliberately NOT here: CreateShortcuts.ps1 exposes
    /// it as the user's "Open Assets Folder" for overlay media, so user
    /// copies win there like everywhere else.
    /// </summary>
    internal static readonly string[] ReleaseOwnedSubtrees = { "overlay", "streamerbot" };

    internal sealed class MergeReport
    {
        /// <summary>Backup files absent from the new tree that were copied over.</summary>
        public int Restored;
        /// <summary>Conflicts where the user's (backup) copy replaced the shipped file.</summary>
        public int UserKept;
        /// <summary>Conflicts inside release-owned subtrees where the shipped file was kept.</summary>
        public int ReleaseKept;
        /// <summary>Per-file failures, as <c>relative-path: reason</c>.</summary>
        public List<string> Failures { get; } = new();

        public string Summary() =>
            $"{Restored} restored, {UserKept} user-kept conflict(s), " +
            $"{ReleaseKept} release-kept conflict(s), {Failures.Count} failure(s)";
    }

    /// <summary>
    /// Merges user state from <paramref name="backupRoot"/> (the renamed-aside
    /// pre-update install) into <paramref name="installRoot"/> (the freshly
    /// swapped-in release tree). Never throws for per-file problems — they are
    /// recorded in the report and the files stay recoverable in the backup.
    /// </summary>
    internal static MergeReport Merge(string backupRoot, string installRoot, Action<string>? log = null)
    {
        var report = new MergeReport();

        foreach (string candidate in DataRootCandidates)
        {
            string backupData = Path.Combine(backupRoot, candidate);
            if (!Directory.Exists(backupData)) continue;

            string installData = Path.Combine(installRoot, candidate);

            // Materialize the enumeration inside its own guard so an
            // enumerator fault (unreadable subdir, pathological path) costs
            // only the remainder of THIS data root's listing — recorded, not
            // thrown — instead of aborting the whole merge.
            var sourceFiles = new List<string>();
            try
            {
                sourceFiles.AddRange(Directory.EnumerateFiles(backupData, "*", CreateEnumeration()));
            }
            catch (Exception ex)
            {
                report.Failures.Add($"{candidate}: enumeration stopped early: {ex.Message}");
                log?.Invoke($"user-state merge: enumeration of {backupData} stopped early: {ex.Message}");
            }

            foreach (string sourceFile in sourceFiles)
            {
                string rel = Path.GetRelativePath(backupData, sourceFile);
                try
                {
                    string dest = Path.Combine(installData, rel);
                    if (File.Exists(dest))
                    {
                        if (IsReleaseOwned(rel))
                        {
                            report.ReleaseKept++;
                            continue;
                        }
                        CopyReplaceAtomic(sourceFile, dest);
                        report.UserKept++;
                    }
                    else
                    {
                        string? destDir = Path.GetDirectoryName(dest);
                        if (destDir is not null) Directory.CreateDirectory(destDir);
                        CopyReplaceAtomic(sourceFile, dest);
                        report.Restored++;
                    }
                }
                catch (Exception ex)
                {
                    report.Failures.Add($"{Path.Combine(candidate, rel)}: {ex.Message}");
                    log?.Invoke($"user-state merge: could not restore {rel}: {ex.Message}");
                }
            }
        }

        return report;
    }

    /// <summary>
    /// Shared enumeration policy for backup walks. The BCL default silently
    /// skips Hidden + System entries — a hidden user file must still survive
    /// the update — so only reparse points are skipped (junction/symlink
    /// loops inside a backup must not recurse). IgnoreInaccessible keeps one
    /// unreadable subdirectory from killing the walk; the per-root guard in
    /// <see cref="Merge"/> records anything the enumerator still throws on.
    /// </summary>
    internal static EnumerationOptions CreateEnumeration() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// Copies via a temp sibling + atomic rename so a mid-copy failure (disk
    /// full, IO error) can never leave a truncated file at the destination —
    /// a direct <c>File.Copy(overwrite: true)</c> truncates first and would
    /// turn a shipped default into a corrupt half-file on failure.
    /// </summary>
    internal static void CopyReplaceAtomic(string sourceFile, string destFile)
    {
        string tmp = destFile + ".phx-restore-tmp";
        try
        {
            File.Copy(sourceFile, tmp, overwrite: true);
            File.Move(tmp, destFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// True when the first segment of <paramref name="relativePath"/> (relative
    /// to a data root) names a release-owned subtree.
    /// </summary>
    internal static bool IsReleaseOwned(string relativePath)
    {
        int cut = relativePath.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        if (cut <= 0) return false; // top-level file (e.g. config.json) is user state
        string first = relativePath.Substring(0, cut);
        foreach (string owned in ReleaseOwnedSubtrees)
        {
            if (string.Equals(first, owned, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
