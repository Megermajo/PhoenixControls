using System;
using System.IO;
using System.Reflection;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// GitInfoService — read-only metadata about the local git checkout the
    /// suite is running from. No shell-out, no network. Used by the auto-updater
    /// to display "version • short SHA • branch" in Settings, and by tests.
    ///
    /// All methods return <c>null</c> when the running .exe is not inside a git
    /// checkout (e.g. a copy-only install). Callers must handle the null case
    /// and fall back to "updates disabled".
    /// </summary>
    public static class GitInfoService
    {
        private const int MaxWalkDepth = 10;

        /// <summary>
        /// Walks up from <paramref name="startDir"/> (default: <see cref="Core.Paths.AppBase"/>)
        /// looking for a folder containing a <c>.git</c> entry (directory or
        /// gitdir-pointer file, the latter used by git worktrees). Returns the
        /// containing folder's full path, or <c>null</c>.
        /// </summary>
        public static string? GetRepoRoot(string? startDir = null)
        {
            startDir ??= Core.Paths.AppBase;

            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(startDir); }
            catch { return null; }

            for (int depth = 0; depth < MaxWalkDepth && dir != null; depth++, dir = dir.Parent)
            {
                string gitPath = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return dir.FullName;
            }
            return null;
        }

        /// <summary>
        /// Resolves the <c>.git</c> directory for <paramref name="repoRoot"/>.
        /// Handles both the normal case (a <c>.git</c> directory) and worktrees
        /// (a <c>.git</c> file containing <c>gitdir: &lt;path&gt;</c>).
        /// </summary>
        private static string? ResolveGitDir(string repoRoot)
        {
            string gitPath = Path.Combine(repoRoot, ".git");
            if (Directory.Exists(gitPath))
                return gitPath;

            if (File.Exists(gitPath))
            {
                try
                {
                    string content = File.ReadAllText(gitPath).Trim();
                    const string prefix = "gitdir:";
                    if (content.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        string raw = content.Substring(prefix.Length).Trim();
                        return Path.IsPathRooted(raw)
                            ? raw
                            : Path.GetFullPath(Path.Combine(repoRoot, raw));
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Returns the current branch name (e.g. "master", "Dev") or <c>null</c>
        /// when HEAD is detached or the repo can't be read.
        /// </summary>
        public static string? GetCurrentBranch(string? repoRoot = null)
        {
            repoRoot ??= GetRepoRoot();
            if (repoRoot is null) return null;

            string? gitDir = ResolveGitDir(repoRoot);
            if (gitDir is null) return null;

            string headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return null;

            try
            {
                string head = File.ReadAllText(headPath).Trim();
                const string refPrefix = "ref: refs/heads/";
                return head.StartsWith(refPrefix, StringComparison.Ordinal)
                    ? head.Substring(refPrefix.Length)
                    : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns the full 40-char SHA of the current HEAD, or <c>null</c>
        /// when the repo can't be read. Handles both the loose-ref form
        /// (<c>.git/refs/heads/&lt;branch&gt;</c>) and the packed-refs form.
        /// </summary>
        public static string? GetLocalSha(string? repoRoot = null)
        {
            repoRoot ??= GetRepoRoot();
            if (repoRoot is null) return null;

            string? gitDir = ResolveGitDir(repoRoot);
            if (gitDir is null) return null;

            string headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return null;

            string head;
            try { head = File.ReadAllText(headPath).Trim(); }
            catch { return null; }

            // Detached HEAD — HEAD itself contains the SHA.
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
                return LooksLikeSha(head) ? head : null;

            // ref: refs/heads/<branch>
            string refRel = head.Substring(4).Trim();
            string refPath = Path.Combine(gitDir, refRel.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(refPath))
            {
                try
                {
                    string sha = File.ReadAllText(refPath).Trim();
                    if (LooksLikeSha(sha)) return sha;
                }
                catch { return null; }
            }

            // Fall back to packed-refs.
            string packedPath = Path.Combine(gitDir, "packed-refs");
            if (File.Exists(packedPath))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(packedPath))
                    {
                        if (line.Length == 0 || line[0] == '#' || line[0] == '^') continue;
                        int sp = line.IndexOf(' ');
                        if (sp != 40) continue;
                        if (line.AsSpan(sp + 1).Trim().SequenceEqual(refRel.AsSpan()))
                            return line.Substring(0, 40);
                    }
                }
                catch { return null; }
            }

            return null;
        }

        /// <summary>Short 7-char form of <see cref="GetLocalSha"/>, or <c>null</c>.</summary>
        public static string? GetLocalShaShort(string? repoRoot = null)
        {
            string? sha = GetLocalSha(repoRoot);
            return sha is { Length: >= 7 } ? sha.Substring(0, 7) : null;
        }

        /// <summary>
        /// Suite version pulled from <see cref="AssemblyInformationalVersionAttribute"/>
        /// (or <see cref="AssemblyName.Version"/> as a fallback) on the calling
        /// assembly. Driven by <c>&lt;Version&gt;</c> in <c>Directory.Build.props</c>.
        /// </summary>
        public static string GetAssemblyVersion(Assembly? asm = null)
        {
            asm ??= Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip MSBuild-appended SourceLink metadata ("0.5.0+abc123") — we only want the SemVer.
                int plus = info.IndexOf('+');
                return plus >= 0 ? info.Substring(0, plus) : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        private static bool LooksLikeSha(string s)
        {
            if (s.Length != 40) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }
    }
}
