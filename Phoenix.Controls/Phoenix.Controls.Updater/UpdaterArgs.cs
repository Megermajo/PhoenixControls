using System;
using System.IO;

namespace Phoenix.Controls.Updater;

/// <summary>
/// CLI arg parser for <see cref="Program"/>. Pulled out into its own type so
/// the parser is unit-testable without spawning the runner.
///
/// Three coexisting modes — the parser picks one based on which args were
/// supplied, the runner picks the matching flow:
///
///  • <b>Update mode (Track 8 / spec)</b>:
///    <c>Updater.exe --update &lt;archivePath&gt; [--target &lt;suiteRoot&gt;]
///                    [--archive-sha256 &lt;hex&gt;] [--release-tag &lt;tag&gt;]
///                    [--no-relaunch]</c>
///    The new <c>.phxupdate</c> flow described in
///    <c>redesign-plan/design/project/extras.jsx §11</c>. Caller (the
///    <c>Phoenix.Controls.Hub.Core.UpdateChecker</c>) downloads
///    the archive, verifies its SHA, then hands off the path to this mode.
///    No <c>--install-root</c> / <c>--hub-pid</c> required: the running suite
///    is discovered by image name and stopped, the install root is inferred
///    from <see cref="AppContext.BaseDirectory"/> when <c>--target</c> is
///    omitted. <see cref="IsUpdateMode"/> is true.
///
///  • <b>Releases mode (legacy URL-based)</b>:
///    <c>Updater.exe --install-root &lt;p&gt; --hub-pid &lt;n&gt;
///                    --asset-url &lt;u&gt; --asset-sha256 &lt;hex&gt;
///                    [--release-tag &lt;tag&gt;] [--launch-script &lt;p&gt;]
///                    [--no-relaunch]</c>
///    Pre-rebrand Hub's <c>Phoenix.Controls.Hub.Core.UpdateChecker.BeginApply</c>
///    spawns the Updater this way. The Updater downloads the asset itself,
///    verifies the SHA, swaps. <see cref="IsReleasesMode"/> is true. Kept so
///    the WinForms Hub's auto-update button keeps working through the WinUI
///    transition.
///
///  • <b>Legacy git mode</b>:
///    <c>Updater.exe --install-root &lt;p&gt; --hub-pid &lt;n&gt;</c>
///    With neither <c>--update</c> nor <c>--asset-url/--asset-sha256</c>,
///    older invocations land here. The runner refuses these (<c>git fetch +
///    reset --hard + dotnet build</c> was retired in the 0.6.2 cleanup) but
///    the parser still accepts the argv shape so the test contract holds.
///
/// <c>--repo-root</c> is preserved as a transparent alias of
/// <c>--install-root</c> so existing spawn sites keep compiling.
/// <c>--target</c> is the spec's name for the same concept in Update mode.
/// </summary>
public sealed class UpdaterArgs
{
    /// <summary>
    /// Where the suite lives on disk.
    /// In Releases mode this is the folder that gets atomically swapped
    /// (<c>phoenix-controls/</c>); in legacy git mode this is the git repo
    /// root. <c>null</c> in Update mode unless the caller passed
    /// <c>--target</c> or <c>--install-root</c> — the runner falls back to
    /// the Updater's <see cref="AppContext.BaseDirectory"/> parent.
    /// </summary>
    public string? InstallRoot { get; init; }

    /// <summary>Alias for <see cref="InstallRoot"/>; legacy callers used this name.</summary>
    public string RepoRoot => InstallRoot ?? "";

    /// <summary>
    /// PID of the spawning Hub. <c>0</c> in Update mode (caller is normally
    /// the Hub itself, which kills the rest of the suite by image name and
    /// then exits before the swap touches its own .exe).
    /// </summary>
    public int HubPid { get; init; }

    public string LaunchScript { get; init; } = "";
    public bool   NoRelaunch   { get; init; }

    /// <summary>Direct download URL for the release zip (e.g. GitHub asset URL). Releases mode only.</summary>
    public string? AssetUrl    { get; init; }
    /// <summary>Expected SHA-256 hex of the release zip. Releases mode only.</summary>
    public string? AssetSha256 { get; init; }
    /// <summary>Informational tag (e.g. "0.6.0") recorded in updater.log.</summary>
    public string? ReleaseTag  { get; init; }

    // ── Update mode (Track 8) ──────────────────────────────────────────

    /// <summary>Absolute path to the <c>.phxupdate</c> archive to apply. Update mode only.</summary>
    public string? UpdateArchive { get; init; }
    /// <summary>Optional SHA-256 hex of the archive on disk. Re-verified before unpack; if absent, only the per-file manifest hashes are checked.</summary>
    public string? ArchiveSha256 { get; init; }

    // ── Mode discriminators ────────────────────────────────────────────

    /// <summary>True when the args carry a verified-download URL payload. False = legacy git or update flow.</summary>
    public bool IsReleasesMode => AssetUrl is { Length: > 0 } && AssetSha256 is { Length: > 0 };
    /// <summary>True when <c>--update &lt;archivePath&gt;</c> was passed (the spec-conforming flow).</summary>
    public bool IsUpdateMode   => UpdateArchive is { Length: > 0 };

    public static bool TryParse(string[] args, out UpdaterArgs parsed, out string error)
    {
        parsed = null!;
        error  = "";

        string? installRoot   = null;
        int?    hubPid        = null;
        string? launchScript  = null;
        bool    noRelaunch    = false;
        string? assetUrl      = null;
        string? assetSha      = null;
        string? releaseTag    = null;
        string? updateArchive = null;
        string? archiveSha    = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                // --install-root / --repo-root / --target are all aliases for
                // the same concept (the suite root). --target is the spec
                // name; the others are kept so existing spawn sites + tests
                // keep working transparently.
                case "--install-root":
                case "--repo-root":
                case "--target":
                    if (++i >= args.Length) { error = $"{a} expects a path"; return false; }
                    installRoot = args[i];
                    break;
                case "--hub-pid":
                    if (++i >= args.Length) { error = "--hub-pid expects a number"; return false; }
                    if (!int.TryParse(args[i], out int pid) || pid <= 0) { error = $"--hub-pid: '{args[i]}' is not a positive integer"; return false; }
                    hubPid = pid;
                    break;
                case "--launch-script":
                    if (++i >= args.Length) { error = "--launch-script expects a path"; return false; }
                    launchScript = args[i];
                    break;
                case "--no-relaunch":
                    noRelaunch = true;
                    break;
                case "--asset-url":
                    if (++i >= args.Length) { error = "--asset-url expects a URL"; return false; }
                    assetUrl = args[i];
                    break;
                case "--asset-sha256":
                    if (++i >= args.Length) { error = "--asset-sha256 expects a hex string"; return false; }
                    assetSha = args[i];
                    break;
                case "--release-tag":
                    if (++i >= args.Length) { error = "--release-tag expects a tag string"; return false; }
                    releaseTag = args[i];
                    break;
                case "--update":
                    if (++i >= args.Length) { error = "--update expects an archive path"; return false; }
                    updateArchive = args[i];
                    break;
                case "--archive-sha256":
                    if (++i >= args.Length) { error = "--archive-sha256 expects a hex string"; return false; }
                    archiveSha = args[i];
                    break;
                default:
                    error = $"unknown argument: {a}";
                    return false;
            }
        }

        bool hasUpdate = updateArchive is { Length: > 0 };

        if (!hasUpdate)
        {
            // Legacy / Releases path keeps the original required-fields contract.
            // (UpdaterArgsTests.Parse_rejects_invalid_input depends on this exact
            // failure ordering.)
            if (installRoot is null) { error = "--install-root is required"; return false; }
            if (hubPid      is null) { error = "--hub-pid is required";      return false; }
        }

        // Releases mode requires both --asset-url AND --asset-sha256. Half-set
        // is a silent foot-gun (download with no verification, or the inverse).
        // Reject up-front so the operator sees the real cause.
        bool hasUrl = assetUrl is { Length: > 0 };
        bool hasSha = assetSha is { Length: > 0 };
        if (hasUrl ^ hasSha)
        {
            error = "--asset-url and --asset-sha256 must be supplied together";
            return false;
        }
        if (hasSha && !LooksLikeHexSha256(assetSha!))
        {
            error = "--asset-sha256 must be a 64-character hex string";
            return false;
        }
        if (archiveSha is { Length: > 0 } && !LooksLikeHexSha256(archiveSha))
        {
            error = "--archive-sha256 must be a 64-character hex string";
            return false;
        }

        // Normalise the install root. Update mode tolerates a missing root
        // (the runner falls back to AppContext.BaseDirectory's parent), so
        // only call GetFullPath when one was actually provided.
        string? normalisedRoot = installRoot is null ? null : Path.GetFullPath(installRoot);

        // Default relaunch target for legacy / Releases mode: Hub.WinUI.exe
        // under the installer-style layout ({installRoot}\Hub\Phoenix.Controls.Hub.WinUI.exe).
        // MINE-12: LAUNCH_SUITE.bat + WinForms Hub.exe fallbacks were retired
        // in T15 and are no longer staged in either the Releases zip or the
        // Inno installer payload. Caller passes --launch-script explicitly
        // when it knows better.
        if (launchScript is null && normalisedRoot is not null)
        {
            string hubWinUI = Path.Combine(normalisedRoot, "Hub", "Phoenix.Controls.Hub.WinUI.exe");
            launchScript = hubWinUI;
        }

        parsed = new UpdaterArgs
        {
            InstallRoot   = normalisedRoot,
            HubPid        = hubPid ?? 0,
            LaunchScript  = launchScript ?? "",
            NoRelaunch    = noRelaunch,
            AssetUrl      = hasUrl ? assetUrl : null,
            AssetSha256   = hasSha ? assetSha!.ToLowerInvariant() : null,
            ReleaseTag    = releaseTag,
            UpdateArchive = hasUpdate ? Path.GetFullPath(updateArchive!) : null,
            ArchiveSha256 = archiveSha is { Length: > 0 } ? archiveSha.ToLowerInvariant() : null,
        };
        return true;
    }

    private static bool LooksLikeHexSha256(string s)
    {
        if (s.Length != 64) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }
}
