using System;
using System.IO;

namespace Phoenix.Controls.Updater;

/// <summary>
/// CLI arg parser for <see cref="Program"/>. Pulled out into its own type so
/// the parser is unit-testable without spawning the runner.
///
/// Two coexisting modes — the parser picks one based on which args were
/// supplied, the runner picks the matching flow:
///
///  • <b>Releases mode</b> (the only shipped update path):
///    <c>Updater.exe --install-root &lt;p&gt; --hub-pid &lt;n&gt;
///                    --asset-url &lt;u&gt; --asset-sha256 &lt;hex&gt;
///                    [--release-tag &lt;tag&gt;] [--launch-script &lt;p&gt;]
///                    [--no-relaunch]</c>
///    <c>Phoenix.Controls.Hub.Core.UpdateChecker.BeginApply</c> spawns the
///    Updater this way. The Updater downloads the GitHub Releases zip itself,
///    verifies its SHA-256 (integrity only — no Authenticode/signature check;
///    signing is a future rollout, see TODO §3), swaps.
///    <see cref="IsReleasesMode"/> is true.
///
///  • <b>Legacy git mode</b>:
///    <c>Updater.exe --install-root &lt;p&gt; --hub-pid &lt;n&gt;</c>
///    Without <c>--asset-url/--asset-sha256</c>, older invocations land
///    here. The runner refuses these (<c>git fetch + reset --hard + dotnet
///    build</c> was retired in the 0.6.2 cleanup) but the parser still
///    accepts the argv shape so the test contract holds.
///
/// <c>--repo-root</c> and <c>--target</c> are preserved as transparent
/// aliases of <c>--install-root</c> — existing spawn sites use the former,
/// and <see cref="UpdaterBootstrap"/>'s temp re-exec injects the latter.
/// </summary>
public sealed class UpdaterArgs
{
    /// <summary>
    /// Where the suite lives on disk.
    /// In Releases mode this is the folder that gets atomically swapped
    /// (<c>phoenix-controls/</c>); in legacy git mode this is the git repo
    /// root. Required by the parser in both modes.
    /// </summary>
    public string? InstallRoot { get; init; }

    /// <summary>Alias for <see cref="InstallRoot"/>; legacy callers used this name.</summary>
    public string RepoRoot => InstallRoot ?? "";

    /// <summary>PID of the spawning Hub. Required by the parser.</summary>
    public int HubPid { get; init; }

    public string LaunchScript { get; init; } = "";
    public bool   NoRelaunch   { get; init; }

    /// <summary>Direct download URL for the release zip (e.g. GitHub asset URL). Releases mode only.</summary>
    public string? AssetUrl    { get; init; }
    /// <summary>Expected SHA-256 hex of the release zip. Releases mode only.</summary>
    public string? AssetSha256 { get; init; }
    /// <summary>Informational tag (e.g. "0.6.0") recorded in updater.log.</summary>
    public string? ReleaseTag  { get; init; }

    // ── Self-relocation (temp re-exec) ──────────────────────────────────

    /// <summary>
    /// True when this process is the temp copy re-exec'd by
    /// <see cref="UpdaterBootstrap"/>. When the Updater ships inside the
    /// install tree it must rename (installer layout: <c>&lt;root&gt;\Updater\</c>),
    /// Windows won't let it move that tree while its own CWD + mapped .exe
    /// image sit inside it. The first instance copies itself to <c>%TEMP%</c>
    /// and relaunches with <c>--detached</c>; only the detached copy performs
    /// the swap. Never set by Hub — only by the relocation relaunch.
    /// </summary>
    public bool Detached { get; init; }

    /// <summary>
    /// PID of the in-tree Updater instance that relocated us. The detached
    /// copy waits for it to exit before mutating files so the original's
    /// mapped image (inside the tree being renamed) is released first.
    /// <c>0</c> when not relocated.
    /// </summary>
    public int ParentPid { get; init; }

    /// <summary>
    /// Test-only override for the runner's state directory (updater.log,
    /// last-update-result.json, updating.lock, …). Never parsed from argv —
    /// the real spawn path always uses the roaming default. Without this seam
    /// the unit tests wrote their fixture outcomes into the LIVE
    /// <c>%AppData%/PhoenixControls/Hub/last-update-result.json</c>, and the
    /// next real Hub launch on the same machine surfaced a phantom
    /// "Last update: Failed — SHA-256 mismatch" from a test's placeholder
    /// hash in its System Log.
    /// </summary>
    public string? StateDirOverride { get; init; }

    // ── Mode discriminators ────────────────────────────────────────────

    /// <summary>True when the args carry a verified-download URL payload. False = legacy git mode.</summary>
    public bool IsReleasesMode => AssetUrl is { Length: > 0 } && AssetSha256 is { Length: > 0 };

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
        bool    detached      = false;
        int?    parentPid     = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                // --install-root / --repo-root / --target are all aliases for
                // the same concept (the suite root). Existing spawn sites use
                // --install-root / --repo-root; UpdaterBootstrap's temp
                // re-exec injects --target.
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
                case "--detached":
                    detached = true;
                    break;
                case "--parent-pid":
                    if (++i >= args.Length) { error = "--parent-pid expects a number"; return false; }
                    if (!int.TryParse(args[i], out int ppid) || ppid <= 0) { error = $"--parent-pid: '{args[i]}' is not a positive integer"; return false; }
                    parentPid = ppid;
                    break;
                default:
                    error = $"unknown argument: {a}";
                    return false;
            }
        }

        // Required-fields contract for both modes.
        // (UpdaterArgsTests.Parse_rejects_invalid_input depends on this exact
        // failure ordering.)
        if (installRoot is null) { error = "--install-root is required"; return false; }
        if (hubPid      is null) { error = "--hub-pid is required";      return false; }

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

        // Normalise the install root (required, so always present here).
        string normalisedRoot = Path.GetFullPath(installRoot);

        // Default relaunch target for legacy / Releases mode: Hub.WinUI.exe
        // under the installer-style layout ({installRoot}\Hub\Phoenix.Controls.Hub.WinUI.exe).
        // LAUNCH_SUITE.bat + WinForms Hub.exe fallbacks were retired
        // in T15 and are no longer staged in either the Releases zip or the
        // Inno installer payload. Caller passes --launch-script explicitly
        // when it knows better.
        if (launchScript is null)
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
            Detached      = detached,
            ParentPid     = parentPid ?? 0,
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
