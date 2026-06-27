using System;
using System.IO;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// Central resolver for both AppBase-relative paths and solution-anchored
    /// dev-source paths. R18 introduced the AppBase helpers; R29 collapsed
    /// the eight separate dev-mode walk-up implementations (HUDServer x3,
    /// LayerWatcher, LogicWatcher, SchedulerService, Architect.FindHubLogicPath,
    /// Visualist.HubPaths, DiagnosticsWindow + 3 tests) into the single
    /// <see cref="FindSolutionRoot"/> + <see cref="HubData"/> family below.
    ///
    /// The walk-up is bounded: it climbs up looking for a folder literally
    /// named <c>Phoenix.Controls</c> (the solution folder, parent of every
    /// project folder). It STOPS at that folder — never goes above. This is
    /// the "base root" cap. AppData (<c>Environment.SpecialFolder.ApplicationData</c>)
    /// is unrelated and lives elsewhere.
    /// </summary>
    public static class Paths
    {
        /// <summary>The current process's executable directory.</summary>
        public static string AppBase => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary><c>{AppBase}/data</c> — root of all bundled data folders next to the .exe.</summary>
        public static string AppData => Path.Combine(AppBase, "data");

        /// <summary><c>{AppData}/logic</c> — Hub-side <c>.phx</c> + <c>.phxg</c> files.</summary>
        public static string AppLogic => Path.Combine(AppData, "logic");

        /// <summary><c>{AppData}/layers</c> — Hub-side <c>.phxlayer</c> files.</summary>
        public static string AppLayers => Path.Combine(AppData, "layers");

        /// <summary><c>{AppData}/overlay</c> — Hub-side overlay HTML/JS served to OBS browser sources.</summary>
        public static string AppOverlay => Path.Combine(AppData, "overlay");

        /// <summary><c>{AppData}/assets</c> — Hub-side static assets exposed via /asset/ HUDServer route.</summary>
        public static string AppAssets => Path.Combine(AppData, "assets");

        /// <summary><c>{AppData}/media</c> — user-imported image/video/audio library exposed via /media/ HUDServer route.</summary>
        public static string AppMedia => Path.Combine(AppData, "media");

        /// <summary>
        ///  The single, suite-wide AppConfig location:
        /// <c>%AppData%/PhoenixControls/config.json</c>.
        /// <para>
        /// Pre-fix this resolved to <c>{AppBase}/data/config.json</c>, which is
        /// per-exe — Hub.WinUI, Architect.WinUI, and Visualist.WinUI each kept
        /// their own copy and global fields (API keys, BotUsername, Schedules)
        /// silently bifurcated. The new path is roaming-AppData-rooted so all
        /// three pillars hit the same file. One-shot legacy migration lives in
        /// <see cref="Phoenix.Controls.Shared.Services.ConfigManager.Load"/>.
        /// </para>
        /// </summary>
        public static string AppConfigJson => RoamingAppData("config.json");

        /// <summary>
        ///  Legacy per-pillar candidate paths the migration in
        /// <see cref="Phoenix.Controls.Shared.Services.ConfigManager.Load"/>
        /// scans on first run, in priority order (Hub first since Hub is the
        /// runtime that holds the canonical credential set; Architect and
        /// Visualist only mirrored a subset). A non-empty match becomes the
        /// seed for the unified path.
        /// </summary>
        public static string[] LegacyAppConfigJsonCandidates
        {
            get
            {
                // %AppBase% varies by exe — at runtime each pillar resolves
                // its own copy. The current pillar's pre-fix path always goes
                // first; sibling-pillar paths are appended only when a
                // solution root is reachable (dev-mode), so a release install
                // never scans non-existent sibling bin/ trees.
                var list = new System.Collections.Generic.List<string>();
                string current = Path.Combine(AppData, "config.json");
                list.Add(current);

                string? sln = FindSolutionRoot();
                if (sln is not null)
                {
                    foreach (string pillarDir in new[] {
                        "Phoenix.Controls.Hub.WinUI",
                        "Phoenix.Controls.Architect.WinUI",
                        "Phoenix.Controls.Visualist.WinUI",
                    })
                    {
                        string bin = Path.Combine(sln, pillarDir, "bin", "Debug",
                                                  "net8.0-windows10.0.19041.0", "data", "config.json");
                        if (!list.Contains(bin, StringComparer.OrdinalIgnoreCase))
                            list.Add(bin);
                    }
                }
                return list.ToArray();
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Solution-anchored dev-source paths (R29).
        // ─────────────────────────────────────────────────────────────────

        /// <summary>The folder name that marks the solution root. Walk-up stops here.</summary>
        public const string SolutionFolderName = "Phoenix.Controls";

        /// <summary>The Hub project folder — sibling of every other project under the solution root.</summary>
        public const string HubProjectFolderName = "Phoenix.Controls.Hub";

        /// <summary>Safety cap so a misconfigured layout can't loop forever.</summary>
        private const int MaxWalkDepth = 10;

        /// <summary>
        /// Walks up from <paramref name="startDir"/> (default: <see cref="AppBase"/>)
        /// looking for a folder literally named <see cref="SolutionFolderName"/>
        /// that ALSO contains at least one <c>.sln</c> file. Returns its full path,
        /// or <c>null</c> when not found within <see cref="MaxWalkDepth"/> levels.
        ///
        /// <para>
        ///  The <c>.sln</c> sibling check is what protects an end-user
        /// install from being treated as a dev tree: a user who extracts the
        /// release zip into a folder they happen to have named
        /// <c>Phoenix.Controls\</c> will satisfy the folder-name match but their
        /// folder has no <c>.sln</c>, so we correctly fall through to the
        /// AppBase-relative production paths. Without this guard
        /// <see cref="HubData"/> and every other solution-anchored consumer
        /// would point into nowhere.
        /// </para>
        /// </summary>
        public static string? FindSolutionRoot(string? startDir = null)
        {
            if (string.IsNullOrWhiteSpace(startDir))
                startDir = AppBase;

            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(startDir); }
            catch { return null; }

            for (int depth = 0; depth < MaxWalkDepth && dir != null; depth++, dir = dir.Parent)
            {
                if (string.Equals(dir.Name, SolutionFolderName, StringComparison.Ordinal)
                    && HasSlnSibling(dir))
                {
                    return dir.FullName;
                }
            }
            return null;
        }

        /// <summary>
        ///  Sibling-of-folder <c>.sln</c> probe used by
        /// <see cref="FindSolutionRoot"/>. Best-effort — IO failures (access
        /// denied, vanished mid-walk) are treated as "no sln found" rather
        /// than propagated, so a permission glitch can't crash the path
        /// resolver.
        /// </summary>
        private static bool HasSlnSibling(DirectoryInfo dir)
        {
            try
            {
                // Single-level enumeration only — the .sln always sits next to
                // the project folders, not nested. EnumerateFiles is preferred
                // over GetFiles so we can short-circuit on the first match.
                foreach (var _ in dir.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly))
                    return true;
            }
            catch
            {
                // Swallow — see method docs.
            }
            return false;
        }

        /// <summary>
        /// Returns <c>{solutionRoot}/Phoenix.Controls.Hub/data/{sub}</c> when the
        /// solution folder is reachable from <paramref name="startDir"/> AND the
        /// resolved data folder actually exists on disk, otherwise the
        /// AppBase-relative fallback <c>{AppBase}/data/{sub}</c>. Pass an
        /// empty <paramref name="sub"/> to get the data root itself.
        ///
        /// The Directory.Exists check is what makes a moved build work: when
        /// only the bin/ folder is relocated, the walk-up may still find a
        /// folder named <c>Phoenix.Controls</c> somewhere up the parent chain
        /// but its <c>Phoenix.Controls.Hub/data</c> won't be present. Without
        /// this guard every Hub.csproj-shipped data root would resolve to a
        /// non-existent source path and downstream callers (ScriptRegistry,
        /// LogicWatcher, LayerRuntime, HUDServer) would silently load nothing.
        /// </summary>
        public static string HubData(string sub = "", string? startDir = null)
        {
            string? sln = FindSolutionRoot(startDir);
            string root;
            if (sln is not null)
            {
                string solutionData = Path.Combine(sln, HubProjectFolderName, "data");
                root = Directory.Exists(solutionData) ? solutionData : AppData;
            }
            else
            {
                root = AppData;
            }
            return string.IsNullOrEmpty(sub) ? root : Path.Combine(root, sub);
        }

        /// <summary>Solution-anchored Hub <c>data/logic</c>.</summary>
        public static string HubLogic   => HubData("logic");
        /// <summary>Solution-anchored Hub <c>data/layers</c>.</summary>
        public static string HubLayers  => HubData("layers");
        /// <summary>Solution-anchored Hub <c>data/overlay</c>.</summary>
        public static string HubOverlay => HubData("overlay");
        /// <summary>Solution-anchored Hub <c>data/assets</c>.</summary>
        public static string HubAssets  => HubData("assets");
        /// <summary>Solution-anchored Hub <c>data/media</c>.</summary>
        public static string HubMedia   => HubData("media");

        // ─────────────────────────────────────────────────────────────────
        // Per-user AppData (Phoenix Controls retail brand).
        //
        // Single source of truth for the rebrand: every persisted-user-state
        // path resolves through these helpers. Pre-rebrand the tree was split
        // across <c>%AppData%/PhoenixControls/</c> (no dot — DB, language,
        // viewer creds) and <c>%AppData%/Phoenix.Controls/</c> (with dot —
        // dock layout, updater state, recent files, window state). The retail
        // rebrand collapses both roots into <c>%AppData%/PhoenixControls/</c>;
        // the same convention applies to <c>%LOCALAPPDATA%</c> for cache-tier
        // state. <see cref="Phoenix.Controls.Shared.Services.AppDataMigrator"/>
        // moves any legacy data forward on first launch.
        // ─────────────────────────────────────────────────────────────────

        /// <summary>The retail-brand folder name used under both AppData roots.</summary>
        public const string AppDataFolderName = "PhoenixControls";

        /// <summary><c>%AppData%/PhoenixControls/</c>.</summary>
        public static string RoamingAppDataRoot =>
            Path.Combine(RequireRootedSpecialFolder(Environment.SpecialFolder.ApplicationData),
                         AppDataFolderName);

        /// <summary><c>%LOCALAPPDATA%/PhoenixControls/</c>.</summary>
        public static string LocalAppDataRoot =>
            Path.Combine(RequireRootedSpecialFolder(Environment.SpecialFolder.LocalApplicationData),
                         AppDataFolderName);

        /// <summary>
        /// Resolves an OS special folder and guarantees the result is a rooted
        /// absolute path. <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
        /// returns <see cref="string.Empty"/> when the folder cannot be located;
        /// feeding that into <see cref="Path.Combine(string, string)"/> would
        /// yield a relative path and silently write user state into the current
        /// working directory, breaking persistence across restarts. Fail fast
        /// instead so the broken environment is visible rather than corrupting
        /// config/credential storage.
        /// </summary>
        private static string RequireRootedSpecialFolder(Environment.SpecialFolder folder)
        {
            string path = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
                throw new InvalidOperationException(
                    $"AppData path for special folder '{folder}' is not rooted: '{path}'.");
            return path;
        }

        /// <summary>
        /// Returns <c>%AppData%/PhoenixControls/<paramref name="sub"/></c>.
        /// Pass an empty string for the root itself.
        /// </summary>
        public static string RoamingAppData(string sub = "")
            => string.IsNullOrEmpty(sub) ? RoamingAppDataRoot : Path.Combine(RoamingAppDataRoot, sub);

        /// <summary>
        /// Returns <c>%LOCALAPPDATA%/PhoenixControls/<paramref name="sub"/></c>.
        /// Pass an empty string for the root itself.
        /// </summary>
        public static string LocalAppData(string sub = "")
            => string.IsNullOrEmpty(sub) ? LocalAppDataRoot : Path.Combine(LocalAppDataRoot, sub);
    }
}
