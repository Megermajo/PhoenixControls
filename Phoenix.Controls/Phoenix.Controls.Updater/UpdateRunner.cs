using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.Updater;

public enum UpdateOutcome
{
    /// <summary>Download + verify + atomic-swap all succeeded.</summary>
    Success,
    /// <summary>
    /// New install was applied then reverted to the prior bits — the swap-in
    /// dir was renamed back from the .bak after a partial failure.
    /// </summary>
    RolledBack,
    /// <summary>Hard failure. Suite likely needs manual recovery.</summary>
    Failed,
}

/// <summary>
/// UpdateRunner — the actual flow. Logs every step to <c>updater.log</c> in
/// <c>%AppData%/PhoenixControls/Hub/</c> AND a per-run timestamped file under
/// <c>%LocalAppData%/PhoenixControls/logs/</c>; a
/// JSON result file (<c>last-update-result.json</c>) is also written so the
/// next Hub launch can surface success / rolled-back / failed without
/// re-running anything.
///
/// Self-contained: BCL only, no Phoenix.Controls.Shared dependency. The
/// updater must be able to run even if the suite assemblies are mid-swap.
/// The brand folder name (<see cref="BrandFolder"/>) is duplicated here on
/// purpose — keeping it in sync with <c>Paths.AppDataFolderName</c> is the
/// price for keeping the Updater self-contained.
///
/// Two flows coexist:
///   • <b>Update flow</b>: caller pre-downloaded a <c>.phxupdate</c>
///     archive and verified its SHA before invoking us with <c>--update</c>.
///     We re-verify the archive SHA + Authenticode, stop the suite, swap.
///   • <b>Releases flow</b> (legacy URL-based): caller passes
///     <c>--asset-url</c> + <c>--asset-sha256</c>; we download the zip,
///     verify, swap. Kept so the WinForms Hub's auto-update keeps working.
///
/// The legacy <c>git fetch + dotnet build</c> path was removed in the 0.6.2
/// cleanup — distribution runs entirely through release archives now.
/// </summary>
public sealed class UpdateRunner
{
    /// <summary>
    /// Retail-brand folder name. Kept in sync with
    /// <c>Phoenix.Controls.Shared.Core.Paths.AppDataFolderName</c> by hand —
    /// Updater is BCL-only and cannot reference Shared.
    /// </summary>
    internal const string BrandFolder = "PhoenixControls";

    private readonly UpdaterArgs _args;
    private readonly string _stateDir;
    private readonly string _logPath;
    private readonly string _resultPath;
    private readonly string _localLogPath;
    private readonly string _progressPath;
    private readonly string _sentinelPath;
    private readonly string _cancelPath;
    private readonly StringBuilder _log = new();
    private DateTime _lastProgressWriteUtc = DateTime.MinValue;
    private bool _swapInFlight;

    public UpdateRunner(UpdaterArgs args)
    {
        _args = args;
        // Roaming state dir — defence in depth: locked-down corporate profiles
        // (GPO ACLs on %APPDATA%) can deny Directory.CreateDirectory on the
        // preferred path. Throwing out of the constructor would short-circuit
        // the try/catch in RunAsync — the process would die with no log file
        // and no result file, leaving Hub with nothing to surface. Fall back
        // to %TEMP% so we at least get diagnostics.
        string preferred = args.StateDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            BrandFolder, "Hub");
        try
        {
            Directory.CreateDirectory(preferred);
            _stateDir = preferred;
        }
        catch
        {
            string fallback = Path.Combine(Path.GetTempPath(), BrandFolder + ".Updater");
            try { Directory.CreateDirectory(fallback); } catch { }
            _stateDir = fallback;
        }
        _logPath      = Path.Combine(_stateDir, "updater.log");
        _resultPath   = Path.Combine(_stateDir, "last-update-result.json");
        _progressPath = Path.Combine(_stateDir, "updater-progress.json");
        _sentinelPath = Path.Combine(_stateDir, "updating.lock");
        _cancelPath   = Path.Combine(_stateDir, "cancel.signal");

        // Local log path: %LocalAppData%/PhoenixControls/logs/updater-<utc>.log
        // (one file per run so a corrupt update + reattempt produces two distinct
        // logs the user can attach to a bug report). Best-effort: failures here
        // never propagate — we still have the roaming log + the in-memory tail.
        // StateDirOverride redirects this too — a test run must leave NOTHING
        // in the machine's real profile, not even diagnostic logs.
        string localLogDir = args.StateDirOverride is not null
            ? Path.Combine(args.StateDirOverride, "logs")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BrandFolder, "logs");
        try { Directory.CreateDirectory(localLogDir); } catch { }
        _localLogPath = Path.Combine(localLogDir, $"updater-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.log");
    }

    public async Task<UpdateOutcome> RunAsync()
    {
        Log("--- Phoenix.Controls.Updater starting ---");
        Log($"  installRoot   = {_args.InstallRoot ?? "(infer)"}");
        Log($"  hubPid        = {_args.HubPid}");
        Log($"  launchScript  = {_args.LaunchScript}");
        Log($"  noRelaunch    = {_args.NoRelaunch}");
        Log($"  releaseTag    = {_args.ReleaseTag ?? "(none)"}");
        Log($"  updateArchive = {_args.UpdateArchive ?? "(none)"}");
        Log($"  archiveSha256 = {_args.ArchiveSha256 ?? "(none)"}");

        try
        {
            // Update mode: caller already downloaded an archive.
            if (_args.IsUpdateMode)
            {
                await WaitForSuiteShutdownAsync().ConfigureAwait(false);
                return await RunUpdateFlowAsync().ConfigureAwait(false);
            }

            // Releases mode (legacy URL-based): we download the asset.
            if (_args.IsReleasesMode)
            {
                await WaitForSuiteShutdownAsync().ConfigureAwait(false);
                return await RunReleasesFlowAsync().ConfigureAwait(false);
            }

            // Legacy git mode is gone. The CLI parser still accepts the
            // shape (for tests / partial rollouts) but the runner refuses.
            Log("refusing to update -- legacy git-checkout update flow has been removed.");
            WriteResult(UpdateOutcome.Failed, oldSha: null, newSha: null,
                error: "The legacy git-checkout update flow has been removed. " +
                       "Use --update <archive> (preferred) or --asset-url/--asset-sha256 (Releases mode).");
            return UpdateOutcome.Failed;
        }
        catch (Exception ex)
        {
            Log("unhandled: " + ex);
            try { WriteResult(UpdateOutcome.Failed, oldSha: null, newSha: null, error: ex.Message); } catch { }
            return UpdateOutcome.Failed;
        }
        finally
        {
            FlushLog();
        }
    }

    // ── Update flow (.phxupdate) ────────────────────────────────────────

    private async Task<UpdateOutcome> RunUpdateFlowAsync()
    {
        await Task.Yield();

        string archive = _args.UpdateArchive!;
        if (!File.Exists(archive))
        {
            Log($"archive not found: {archive}");
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag, error: $"archive not found: {archive}");
            return UpdateOutcome.Failed;
        }

        // Defensive staging-dir cleanup. The Update-flow consumes a
        // caller-provided archive, so we MUST NOT delete the archive itself;
        // the Releases flow's "wipe downloadDir on entry" pattern (line ~360)
        // can't be applied verbatim here. Instead, age out any *other* file
        // in the archive's directory that hasn't been touched in 7+ days, so
        // half-downloaded retries, orphaned .meta sidecars, and stale extract
        // dirs from interrupted prior runs don't accumulate indefinitely.
        TryAgeOutStaging(archive, ageDays: 7);

        long bytes = new FileInfo(archive).Length;
        Log($"archive size: {bytes:N0} bytes");

        // 1. Contract: --update REQUIRES --archive-sha256. Skipping the
        //    archive-wide hash collapses to "trust any file on local disk",
        //    which means a malicious local writer can substitute the staging
        //    archive between download and apply. Manifest-per-file hashes are
        //    defence-in-depth, NOT a substitute (a malicious archive can ship
        //    its own manifest).
        if (string.IsNullOrEmpty(_args.ArchiveSha256))
        {
            Log("refusing to apply -- --update mode requires --archive-sha256.");
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                error: "--update mode requires --archive-sha256. Aborted to protect the install.");
            return UpdateOutcome.Failed;
        }
        {
            string actual;
            try { actual = ComputeSha256(archive); }
            catch (Exception ex)
            {
                Log($"sha256 read failed: {ex.Message}");
                WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag, error: $"sha256 read failed: {ex.Message}");
                return UpdateOutcome.Failed;
            }
            // Constant-time hex compare.
            if (!HexEqualsFixedTime(actual, _args.ArchiveSha256))
            {
                Log($"sha256 mismatch -- expected {_args.ArchiveSha256}, got {actual}");
                WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                    error: $"SHA-256 mismatch on update archive (expected {_args.ArchiveSha256}, got {actual}). Aborted.");
                return UpdateOutcome.Failed;
            }
            Log($"sha256 verified: {actual}");
        }

        // 2. Contract: Authenticode Unsigned is REJECTED in --update
        //    mode. Today's pipeline doesn't sign archives, so this hard-gates
        //    behind the signing infrastructure -- which is the intended fail-
        //    closed posture until that ships. Untrusted has always failed.
        Authenticode.VerifyResult ac = Authenticode.Verify(archive, out string acDetail);
        Log($"authenticode: {ac} ({acDetail})");
        if (ac == Authenticode.VerifyResult.Untrusted)
        {
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                error: $"Authenticode verification failed on the update archive: {acDetail}. Aborted to protect the install.");
            return UpdateOutcome.Failed;
        }
        if (ac == Authenticode.VerifyResult.Unsigned)
        {
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                error: "Update archive is unsigned. Refusing to apply in --update mode until signing infrastructure ships.");
            return UpdateOutcome.Failed;
        }

        // 3. Resolve the install root -- explicit --target / --install-root
        //    wins, otherwise the Updater is sitting next to the suite at
        //    <installRoot>/Updater/Phoenix.Controls.Updater.exe, so the parent
        //    of AppContext.BaseDirectory is the install root.
        string installRoot = ResolveInstallRoot();
        Log($"installRoot resolved to: {installRoot}");

        // Reject system-critical install-root targets up-front.
        // Combined with the Zip Slip primitives the install-root was a
        // swap-anywhere vector; the elevated-installer flow will tighten this
        // further when it ships.
        if (!IsInstallRootSafe(installRoot, out string rootReason))
        {
            Log($"refusing to update -- unsafe install root: {rootReason}");
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                error: $"--target / --install-root rejected: {rootReason}");
            WriteProgress("failed", -1, $"Unsafe install root: {rootReason}");
            return UpdateOutcome.Failed;
        }

        // 4. Read the manifest if the archive carries one. Manifest-free zips
        //    (legacy phoenix-controls/ root) work too -- fall through to the
        //    same swap routine the Releases flow uses.
        UpdateManifest? manifest = UpdateManifest.LoadFromArchive(archive);
        if (manifest is not null)
            Log($"manifest: version={manifest.Version}, files={manifest.Files.Count}");
        else
            Log("manifest: (none -- treating as legacy zip with phoenix-controls/ root)");

        // 5. Extract + atomic swap.
        WriteProgress("await_hub_exit", -1, "Waiting for Hub to exit before applying swap...");
        if (!await AwaitSentinelHubExitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
        {
            Log("aborting -- Hub PID from sentinel did not exit within 30s; refusing to mutate files.");
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                error: "Hub did not exit within 30 seconds after the update was authorized. Aborted to protect the install. Restart Hub and retry.");
            WriteProgress("failed", -1, "Hub did not exit in time — aborted to protect the install.");
            return UpdateOutcome.Failed;
        }

        _swapInFlight = true;
        WriteProgress("swap", -1, "Applying atomic swap...");
        UpdateOutcome outcome = ApplyArchiveSwap(archive, installRoot);
        if (outcome != UpdateOutcome.Success)
        {
            WriteProgress("failed", -1, "Swap failed.");
            return outcome;
        }

        // 6. Verify per-file hashes via the manifest (defence in depth).
        if (manifest is not null)
        {
            var problems = manifest.VerifyInstall(installRoot);
            if (problems.Count > 0)
            {
                Log("manifest verification FAILED:");
                foreach (string p in problems) Log("  " + p);
                WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                    error: $"manifest verification failed after extract: {problems.Count} problem(s) (see updater.log)");
                WriteProgress("failed", -1, "Manifest verification failed after extract.");
                return UpdateOutcome.Failed;
            }
            Log($"manifest verification: ok ({manifest.Files.Count} files)");
        }

        // Relaunch + hubAlive verification must precede WriteResult(Success).
        // The new Hub's ReadAndClearLastUpdateResult otherwise races + clears the
        // success-file before a relaunch-failure overwrite has a chance to land,
        // so the user sees "Update applied" when it actually failed to relaunch.
        TryPruneBackups(installRoot, ageDays: 7);
        bool relaunchOk = true;
        string? relaunchError = null;
        if (!_args.NoRelaunch)
            (relaunchOk, relaunchError) = await MaybeRelaunchAsync(installRoot).ConfigureAwait(false);

        if (relaunchOk)
        {
            WriteResult(UpdateOutcome.Success, oldSha: null, newSha: _args.ReleaseTag, error: null);
            WriteProgress("complete", 100, "Update complete.");
            TryDeleteSentinel();
            return UpdateOutcome.Success;
        }
        else
        {
            WriteResult(UpdateOutcome.Failed, oldSha: null, newSha: _args.ReleaseTag,
                error: relaunchError ?? "Update applied but Hub did not come back up after relaunch.");
            WriteProgress("failed", -1, relaunchError ?? "Update applied but Hub did not relaunch.");
            TryDeleteSentinel();
            return UpdateOutcome.Failed;
        }
    }

    // ── Releases flow (legacy URL-based; download + verify + swap) ──────

    private async Task<UpdateOutcome> RunReleasesFlowAsync()
    {
        // Don't operate on a path that doesn't already contain a recognisable
        // Phoenix Controls install. Either Hub.WinUI.exe is here at the
        // release-layout location (phoenix-controls/Hub/Phoenix.Controls.Hub.WinUI.exe),
        // or it is right next to us (a dev tree where the Updater was launched
        // from a flat project bin). The WinForms Hub.exe was retired
        // in T15 and is no longer staged in either the zip or the installer.
        string installRoot = _args.InstallRoot!;
        // Same install-root sanity gate as Update mode.
        if (!IsInstallRootSafe(installRoot, out string rootReason))
        {
            Log($"refusing to update -- unsafe install root: {rootReason}");
            WriteResult(UpdateOutcome.Failed, null, null,
                error: $"--install-root rejected: {rootReason}");
            WriteProgress("failed", -1, $"Unsafe install root: {rootReason}");
            return UpdateOutcome.Failed;
        }
        bool looksLikeRelease = Directory.Exists(Path.Combine(installRoot, "Hub"))
                             && File.Exists(Path.Combine(installRoot, "Hub", "Phoenix.Controls.Hub.WinUI.exe"));
        bool looksLikeDevTree = File.Exists(Path.Combine(installRoot, "Phoenix.Controls.Hub.WinUI.exe"));
        if (!looksLikeRelease && !looksLikeDevTree)
        {
            Log($"refusing to update -- {installRoot} does not look like a Phoenix Controls install.");
            WriteResult(UpdateOutcome.Failed, null, null,
                error: $"--install-root does not look like a Phoenix Controls install: {installRoot}");
            WriteProgress("failed", -1, $"Install root not recognised: {installRoot}");
            return UpdateOutcome.Failed;
        }

        WriteProgress("query", -1, "Querying GitHub release metadata.");

        string downloadDir;
        try
        {
            downloadDir = Path.Combine(_stateDir, "download");
            // Clean any stale download from a previous failed run.
            if (Directory.Exists(downloadDir)) { try { Directory.Delete(downloadDir, recursive: true); } catch { } }
            Directory.CreateDirectory(downloadDir);
        }
        catch (Exception ex)
        {
            Log($"could not prepare download dir: {ex.Message}");
            WriteResult(UpdateOutcome.Failed, null, null, error: $"prepare download dir: {ex.Message}");
            WriteProgress("failed", -1, $"Prepare download dir: {ex.Message}");
            return UpdateOutcome.Failed;
        }

        if (CancelRequested())
        {
            Log("cancel requested before download started");
            WriteResult(UpdateOutcome.Failed, null, null, error: "Cancelled by user before download started.");
            WriteProgress("failed", -1, "Cancelled by user.");
            ClearCancelSignal();
            return UpdateOutcome.Failed;
        }

        string zipPath = Path.Combine(downloadDir, "release.zip");

        Log($"downloading {_args.AssetUrl}");
        // Download + size + SHA all sit inside one retry envelope.
        // The helper writes its own failure result/progress entries before
        // returning false, so we just early-exit on its signal.
        // cancellation is threaded through the helper's CancellationToken
        // path; the inline-CTS approach is unnecessary now that the
        // helper exists.
        if (!await DownloadVerifiedZipWithRetryAsync(zipPath).ConfigureAwait(false))
            return UpdateOutcome.Failed;

        if (CancelRequested())
        {
            Log("cancel requested before staging swap");
            WriteResult(UpdateOutcome.Failed, null, null, error: "Cancelled by user before staging swap.");
            WriteProgress("failed", -1, "Cancelled by user.");
            ClearCancelSignal();
            return UpdateOutcome.Failed;
        }

        WriteProgress("prepare", -1, "Staging swap directory...");

        // Sentinel-based PID validation. Hub writes %AppData%/PhoenixControls/
        // Hub/updating.lock with its PID before requesting Application.Exit;
        // we re-read it here and refuse to swap until that PID is gone. Adds
        // robustness on top of the legacy --hub-pid arg: if the WaitForSuiteShutdown
        // pass spotted Hub still alive, AwaitSentinelHubExit holds the swap
        // until the file disappears or the timeout fires.
        WriteProgress("await_hub_exit", -1, "Waiting for Hub to exit before applying swap...");
        if (!await AwaitSentinelHubExitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
        {
            Log("aborting -- Hub PID from sentinel did not exit within 30s; refusing to mutate files.");
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                error: "Hub did not exit within 30 seconds after the update was authorized. Aborted to protect the install. Restart Hub and retry.");
            WriteProgress("failed", -1, "Hub did not exit in time — aborted to protect the install.");
            return UpdateOutcome.Failed;
        }

        // Past this point cancellation isn't honoured — the swap is the
        // atomic boundary. The dialog disables its Cancel button accordingly.
        _swapInFlight = true;
        WriteProgress("swap", -1, "Applying atomic swap...");
        UpdateOutcome outcome = ApplyArchiveSwap(zipPath, installRoot);
        if (outcome != UpdateOutcome.Success)
        {
            WriteProgress("failed", -1, "Swap failed.");
            return outcome;
        }

        // Relaunch + hubAlive verification must precede WriteResult(Success).
        // The new Hub's ReadAndClearLastUpdateResult otherwise races + clears the
        // success-file before a relaunch-failure overwrite has a chance to land,
        // so the user sees "Update applied" when it actually failed to relaunch.
        TryPruneBackups(installRoot, ageDays: 7);
        bool relaunchOk = true;
        string? relaunchError = null;
        if (!_args.NoRelaunch)
            (relaunchOk, relaunchError) = await MaybeRelaunchAsync(installRoot).ConfigureAwait(false);

        if (relaunchOk)
        {
            WriteResult(UpdateOutcome.Success, oldSha: null, newSha: _args.ReleaseTag, error: null);
            WriteProgress("complete", 100, "Update complete.");
            // Sentinel served its purpose -- delete it as the very last step
            // of a successful update so a future Hub launch sees no stale lock.
            TryDeleteSentinel();
            return UpdateOutcome.Success;
        }
        else
        {
            WriteResult(UpdateOutcome.Failed, oldSha: null, newSha: _args.ReleaseTag,
                error: relaunchError ?? "Update applied but Hub did not come back up after relaunch.");
            WriteProgress("failed", -1, relaunchError ?? "Update applied but Hub did not relaunch.");
            TryDeleteSentinel();
            return UpdateOutcome.Failed;
        }
    }

    // ── Archive swap helper (shared between Update + Releases flows) ────

    /// <summary>
    /// Extracts <paramref name="archivePath"/> (zip or .phxupdate -- same
    /// format) into a staging directory next to it, then atomically renames
    /// the current install to a timestamped backup and the staging dir into
    /// the install path. Tolerates two archive layouts: a top-level
    /// <c>phoenix-controls/</c> wrapper, or a flat layout where the suite
    /// folders sit at the archive root.
    /// </summary>
    private UpdateOutcome ApplyArchiveSwap(string archivePath, string installRoot)
    {
        // Normalise the archive path up front. GetDirectoryName returns
        // null on a bare filename, which would silently fall back to _stateDir
        // and stage the extract somewhere unrelated if the Updater's CWD has
        // drifted (e.g. relaunched from a transient working directory). A full
        // path also makes the subsequent Zip Slip / backup logging consistent.
        archivePath = Path.GetFullPath(archivePath);
        string parent = Path.GetDirectoryName(archivePath) ?? _stateDir;
        string extractDir = Path.Combine(parent, "extracted-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        try
        {
            // Per-entry Zip Slip guard. ZipFile.ExtractToDirectory in
            // .NET 8 already rejects traversal entries, but we extract entry-by
            // -entry here so the contract is explicit + survives any future
            // BCL relaxation. Every resolved destination must be a child of
            // extractDir; reject anything that escapes.
            Directory.CreateDirectory(extractDir);
            string extractFullRoot = Path.GetFullPath(extractDir);
            // Use OS-specific directory separator for the StartsWith guard.
            string extractFullRootWithSep = extractFullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? extractFullRoot
                : extractFullRoot + Path.DirectorySeparatorChar;

            using (var zip = ZipFile.OpenRead(archivePath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    // Skip purely-directory entries (entries that end in '/').
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // Directory entry; ensure the path is in-bounds, then create.
                        string dirCandidate = Path.GetFullPath(Path.Combine(extractFullRoot, entry.FullName));
                        if (!dirCandidate.StartsWith(extractFullRootWithSep, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(dirCandidate, extractFullRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException($"Zip Slip rejected (directory): {entry.FullName}");
                        }
                        Directory.CreateDirectory(dirCandidate);
                        continue;
                    }

                    string destFull = Path.GetFullPath(Path.Combine(extractFullRoot, entry.FullName));
                    if (!destFull.StartsWith(extractFullRootWithSep, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"Zip Slip rejected: {entry.FullName} resolved outside extract dir");
                    }

                    string? destDir = Path.GetDirectoryName(destFull);
                    if (destDir is not null) Directory.CreateDirectory(destDir);
                    entry.ExtractToFile(destFull, overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"extract failed: {ex.Message}");
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag, error: $"extract failed: {ex.Message}");
            return UpdateOutcome.Failed;
        }

        // The release zip stores its content under `phoenix-controls/`. If
        // the extracted root contains exactly one `phoenix-controls` folder,
        // use that as the swap-in source. Otherwise treat the extract dir
        // itself as the payload.
        string swapSource = extractDir;
        string nestedRoot = Path.Combine(extractDir, "phoenix-controls");
        if (Directory.Exists(nestedRoot)) swapSource = nestedRoot;
        Log($"swapSource = {swapSource}");

        string backupDir = $"{installRoot}.bak.{DateTime.UtcNow:yyyyMMddHHmmss}";
        bool installRenamed = false;
        try
        {
            // Updater.exe lives inside installRoot (in the release layout at
            // <root>/Updater/Phoenix.Controls.Updater.exe). Renaming the
            // parent we are running from is risky on Windows -- the OS keeps
            // the original directory entry valid for the running process,
            // but a fresh open fails. Mitigation: we rename and immediately
            // re-create the install path with the extracted bits, so any
            // post-swap relaunch resolves through the new tree. The Updater
            // process itself is about to exit; it does not re-open files
            // from its own directory after the swap.
            if (Directory.Exists(installRoot))
            {
                Directory.Move(installRoot, backupDir);
                installRenamed = true;
                Log($"renamed install -> backup: {backupDir}");
            }

            // Cross-volume safety: Directory.Move fails across volumes with
            // IOException. The state dir (%APPDATA%) and install dir are
            // typically on the same volume, but if they're not, fall back to
            // a recursive copy. If the cross-volume copy throws
            // mid-way, CopyDirectory has already mkdir'd installRoot, so the
            // catch block's "!Directory.Exists(installRoot)" rollback gate
            // would have falsely failed. Wrap the copy so any failure tears
            // down the half-copied tree before re-throwing.
            try
            {
                Directory.Move(swapSource, installRoot);
                Log($"moved {swapSource} -> {installRoot}");
            }
            catch (IOException)
            {
                Log("Directory.Move failed (likely cross-volume) -- copying instead.");
                try
                {
                    CopyDirectory(swapSource, installRoot);
                }
                catch
                {
                    // Ensure rollback gate sees an empty install path.
                    try { if (Directory.Exists(installRoot)) Directory.Delete(installRoot, recursive: true); }
                    catch (Exception delEx) { Log($"could not clean partial copy: {delEx.Message}"); }
                    throw;
                }
            }

            Log($"swap complete; backup retained at {backupDir}");
            return UpdateOutcome.Success;
        }
        catch (Exception ex)
        {
            Log($"atomic swap failed: {ex.Message}");
            // Try to restore the backup so the user isn't left without an install.
            // If a partial-copy failure left installRoot present, the
            // CopyDirectory catch above tore it down — so the gate below now
            // correctly returns true and the rollback runs.
            if (installRenamed && !Directory.Exists(installRoot) && Directory.Exists(backupDir))
            {
                try
                {
                    Directory.Move(backupDir, installRoot);
                    Log("rolled back: backup restored to install path.");
                    WriteResult(UpdateOutcome.RolledBack, null, _args.ReleaseTag,
                        error: $"swap failed, restored prior install: {ex.Message}");
                    return UpdateOutcome.RolledBack;
                }
                catch (Exception restoreEx)
                {
                    Log($"rollback also failed: {restoreEx.Message}");
                    WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag,
                        error: $"swap failed AND rollback also failed; manual recovery needed. Backup at {backupDir}. Original error: {ex.Message}");
                    return UpdateOutcome.Failed;
                }
            }
            WriteResult(UpdateOutcome.Failed, null, _args.ReleaseTag, error: $"swap failed: {ex.Message}");
            return UpdateOutcome.Failed;
        }
        finally
        {
            // Clean up any leftover staging dir if it wasn't moved into place.
            if (Directory.Exists(extractDir))
            {
                try { Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }
    }

    /// <summary>
    /// Resolves the install root for Update mode. Caller-supplied --target /
    /// --install-root wins. Otherwise: assume the Updater is sitting at
    /// <c>&lt;installRoot&gt;/Updater/Phoenix.Controls.Updater.exe</c> per the
    /// installer layout, so the parent of <see cref="AppContext.BaseDirectory"/>
    /// is the install root. As a final fallback, return the Updater's own
    /// directory (dev-tree case where Hub and Updater coexist in one bin).
    /// </summary>
    private string ResolveInstallRoot()
    {
        if (!string.IsNullOrEmpty(_args.InstallRoot)) return _args.InstallRoot;

        string baseDir = AppContext.BaseDirectory;
        // Trim trailing separator so DirectoryInfo.Parent works.
        baseDir = Path.TrimEndingDirectorySeparator(baseDir);

        var di = new DirectoryInfo(baseDir);
        // If we're under <root>/Updater/, the parent is the install root.
        if (string.Equals(di.Name, "Updater", StringComparison.OrdinalIgnoreCase) && di.Parent is not null)
            return di.Parent.FullName;

        // Dev-tree case: the Updater lives alongside Hub in a flat bin.
        return baseDir;
    }

    /// <summary>
    /// Install-root sanity gate. Rejects targets that are obviously
    /// system-critical (Windows, System32, Program Files roots, drive roots).
    /// Conservative — the elevated-installer flow will tighten this further.
    /// </summary>
    private static bool IsInstallRootSafe(string installRoot, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            reason = "install root is empty";
            return false;
        }
        string full;
        try { full = Path.GetFullPath(installRoot); }
        catch (Exception ex) { reason = $"unparseable path: {ex.Message}"; return false; }

        full = Path.TrimEndingDirectorySeparator(full);
        // Drive root ("C:\" / "D:\") is never a valid install location.
        if (full.Length <= 3)
        {
            reason = $"drive root is not a valid install location: {full}";
            return false;
        }

        string? windir = Environment.GetEnvironmentVariable("WINDIR");
        string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        string? programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        string? programData = Environment.GetEnvironmentVariable("ProgramData");

        // Belt-and-braces: also reject System32 / SysWOW64 even if WINDIR
        // points somewhere unusual.
        string?[] forbidden =
        {
            windir,
            programFiles,
            programFilesX86,
            programData,
            windir is null ? null : Path.Combine(windir, "System32"),
            windir is null ? null : Path.Combine(windir, "SysWOW64"),
        };

        foreach (string? entry in forbidden)
        {
            if (string.IsNullOrEmpty(entry)) continue;
            string entryFull;
            try { entryFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)); }
            catch { continue; }
            if (string.Equals(full, entryFull, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"refuses to touch system path: {entryFull}";
                return false;
            }
        }
        return true;
    }

    // ── Networking + hashing helpers ────────────────────────────────────

    // Single shared HttpClient. Updater is a one-shot, but using a
    // singleton avoids the socket-exhaustion footgun if anyone refactors
    // this into a long-lived service later.
    //
    // `internal static` settable for test-side injection. A DelegatingHandler-wrapped
    // client lets UpdateRunnerDownloadResumeTests fake 200/206/416 server
    // responses without spinning a real HTTP server. Production callers
    // never reassign; the slot stays the default SocketsHttpHandler client.
    internal static HttpClient s_http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect      = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// Non-retryable download failure — terminal HTTP status (401/403/404)
    /// or any other condition where retrying would just hit the same wall.
    /// Carried separately from <see cref="HttpRequestException"/> so the
    /// retry envelope in <see cref="DownloadVerifiedZipWithRetryAsync"/>
    /// can short-circuit on it instead of burning the full backoff budget.
    /// </summary>
    private sealed class PermanentDownloadException : Exception
    {
        public System.Net.HttpStatusCode StatusCode { get; }
        public PermanentDownloadException(System.Net.HttpStatusCode code, string message)
            : base(message)
        {
            StatusCode = code;
        }
    }

    /// <summary>
    /// Cached server validators for the staging file written by
    /// <see cref="DownloadAsync"/>. Persisted as JSON next to the staging
    /// payload (<c>&lt;dest&gt;.meta</c>) so a subsequent retry can decide
    /// whether the partial bytes still represent a prefix of the same asset
    /// or whether the upstream changed mid-retry and the work has to be
    /// thrown away.
    /// </summary>
    // `internal` (was `private`) so tests can construct meta
    // instances to pin the read/write round-trip without crossing a private
    // boundary. Production usage is unchanged.
    internal sealed class DownloadResumeMeta
    {
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public long? TotalBytes { get; set; }
    }

    // `internal static` (was `private`) so tests can pin the
    // resume-meta sidecar round-trip directly without driving a download.
    internal static string ResumeMetaPath(string destPath) => destPath + ".meta";

    internal static DownloadResumeMeta? TryReadResumeMeta(string destPath)
    {
        string metaPath = ResumeMetaPath(destPath);
        try
        {
            if (!File.Exists(metaPath)) return null;
            string json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<DownloadResumeMeta>(json);
        }
        catch { return null; }
    }

    internal static void TryWriteResumeMeta(string destPath, DownloadResumeMeta meta)
    {
        try
        {
            File.WriteAllText(ResumeMetaPath(destPath),
                JsonSerializer.Serialize(meta));
        }
        catch { /* best-effort -- losing the meta only forces a restart on the next retry */ }
    }

    private static void TryDeleteResumeMeta(string destPath)
    {
        try { File.Delete(ResumeMetaPath(destPath)); } catch { }
    }

    private static void TryDeleteStagingPayload(string destPath)
    {
        try { File.Delete(destPath); } catch { }
        TryDeleteResumeMeta(destPath);
    }

    internal static async Task DownloadAsync(string url, string destPath,
        Action<long, long?>? onProgress = null,
        CancellationToken ct = default)
    {
        // Resume mid-retry rather than throwing away every partial
        // download. If a previous attempt left bytes on disk, send a
        // Range: bytes=<n>- header and append to the existing file. The
        // server may answer:
        //   • 206 Partial Content  → genuine resume; append the new bytes.
        //   • 200 OK               → server ignored Range (no support, or
        //                            chose not to); treat as a full restart
        //                            and truncate before writing.
        //   • ETag/Last-Modified mismatch vs the validators we cached on
        //     the prior attempt → asset changed under us; truncate-and-restart.
        //   • 416 Range Not Satisfiable → partial is past EOF (probably
        //                            corrupt); truncate and retry without
        //                            a Range header.
        long currentSize = 0;
        try { if (File.Exists(destPath)) currentSize = new FileInfo(destPath).Length; }
        catch { currentSize = 0; }

        DownloadResumeMeta? prevMeta = currentSize > 0 ? TryReadResumeMeta(destPath) : null;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Set a UA -- GitHub may rate-limit anonymous requests without one.
        req.Headers.UserAgent.ParseAdd("Phoenix.Controls.Updater/1.0");

        bool requestedResume = false;
        if (currentSize > 0)
        {
            try
            {
                req.Headers.Range = new RangeHeaderValue(currentSize, null);
                requestedResume = true;
            }
            catch
            {
                // Range construction should never fail for a non-negative
                // offset, but guard anyway -- worst case is a fresh download.
                requestedResume = false;
                currentSize = 0;
            }
        }

        // Thread the cancellation token through send + stream so a
        // Cancel button press during an in-flight download actually stops the
        // network read rather than waiting for the 10-minute HttpClient timeout.
        using HttpResponseMessage resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Terminal HTTP codes are surfaced as a permanent
        // exception so the retry envelope skips its backoff schedule.
        // 401 (auth), 403 (forbidden — eg. private repo), and 404 (asset
        // missing) won't succeed on the next call to the same URL; retrying
        // wastes time and double-counts the failure in the UI.
        // EnsureSuccessStatusCode comes after this check so non-terminal
        // failures (500, 502, 503, 504) fall through to HttpRequestException
        // and trigger a retry as before.
        var code = resp.StatusCode;
        if (code == System.Net.HttpStatusCode.NotFound
            || code == System.Net.HttpStatusCode.Unauthorized
            || code == System.Net.HttpStatusCode.Forbidden)
        {
            throw new PermanentDownloadException(code,
                $"HTTP {(int)code} {code} on {url} — release asset is missing or access is denied; will not retry.");
        }

        // 416 Range Not Satisfiable — partial is past EOF (server's idea of
        // the asset shrank, or the partial is corrupt). Wipe it and let the
        // outer retry loop come back without a Range header.
        if (requestedResume && code == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDeleteStagingPayload(destPath);
            throw new HttpRequestException(
                $"HTTP 416 on {url} — staged partial ({currentSize} bytes) is past EOF; discarded and will restart.");
        }

        resp.EnsureSuccessStatusCode();

        // Decide whether the server honoured our Range request. If we asked
        // for a partial but the server answered 200, the whole asset is
        // about to come back — drop the prior partial before writing.
        bool serverResumed = requestedResume && code == System.Net.HttpStatusCode.PartialContent;

        // Capture validators NOW so a mid-retry mutation of the upstream
        // asset shows up on the very next attempt.
        string? etag = resp.Headers.ETag?.Tag;
        string? lastMod = resp.Content.Headers.LastModified?.ToString("O");

        // If the upstream validators rotated since we last persisted them,
        // the partial bytes no longer match the asset we're being handed —
        // treat the resume request as a restart even if the server returned 206.
        bool validatorsRotated =
            prevMeta is not null
            && ((!string.IsNullOrEmpty(prevMeta.ETag)         && !string.IsNullOrEmpty(etag)    && !string.Equals(prevMeta.ETag, etag, StringComparison.Ordinal))
             || (!string.IsNullOrEmpty(prevMeta.LastModified) && !string.IsNullOrEmpty(lastMod) && !string.Equals(prevMeta.LastModified, lastMod, StringComparison.Ordinal)));

        if (validatorsRotated)
        {
            serverResumed = false;
            currentSize = 0;
            TryDeleteStagingPayload(destPath);
        }
        else if (!serverResumed && requestedResume)
        {
            // Range asked, 200 served — server doesn't support Range. Restart.
            currentSize = 0;
            TryDeleteStagingPayload(destPath);
        }

        // Compute the expected total. 206 carries the remaining bytes; we
        // want the *full* asset length for progress reporting.
        long? bodyLength = resp.Content.Headers.ContentLength;
        long? total = serverResumed
            ? (resp.Content.Headers.ContentRange?.Length ?? (bodyLength is { } b ? b + currentSize : null))
            : bodyLength;

        // Persist validators + expected total so the next retry can compare.
        TryWriteResumeMeta(destPath, new DownloadResumeMeta
        {
            ETag = etag,
            LastModified = lastMod,
            TotalBytes = total,
        });

        await using Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // FileMode.Append when resuming, otherwise truncate-and-create. Both
        // share Write access; the resume path picks up at currentSize and
        // continues without seeking.
        FileMode mode = serverResumed ? FileMode.Append : FileMode.Create;
        await using FileStream fs = new FileStream(destPath, mode, FileAccess.Write, FileShare.Read);

        // Stream + emit progress every ~256 KiB. Cheap on the network side --
        // the OS buffers writes anyway -- and gives the dialog a smooth bar.
        byte[] buffer = new byte[256 * 1024];
        long copied = serverResumed ? currentSize : 0;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            onProgress?.Invoke(copied, total);
        }

        // Sanity-check that the server actually sent the bytes it
        // promised. A truncated stream that returned 0 reads early
        // (network drop mid-body, proxy / antivirus interception) is a
        // transient failure — surface it as HttpRequestException so the
        // retry envelope counts it like any other connection blip.
        if (total is { } expected && copied < expected)
        {
            throw new HttpRequestException(
                $"partial download — got {copied} / {expected} bytes from {url}");
        }

        // Download completed cleanly. The .meta file is only useful to a
        // future retry — once the payload is whole, drop it so a stale
        // validator never poisons a *next* update cycle.
        TryDeleteResumeMeta(destPath);
    }

    /// <summary>
    /// Download-and-verify with exponential-backoff retry.
    /// Calls <see cref="DownloadAsync"/>, then size-sanity-checks the
    /// payload, then verifies the SHA-256 against
    /// <see cref="UpdaterArgs.AssetSha256"/>. On any transient failure
    /// (<see cref="HttpRequestException"/>, partial download caught by
    /// the size guard, or SHA mismatch) waits 1 s / 2 s / 4 s and retries
    /// up to a total of 3 attempts. <see cref="PermanentDownloadException"/>
    /// short-circuits the loop — 401/403/404 will never succeed on retry,
    /// so we surface them immediately.
    ///
    /// Returns <c>true</c> on a verified download. On failure, writes the
    /// terminal failure result/progress entry and returns <c>false</c> so
    /// the caller can early-exit with <see cref="UpdateOutcome.Failed"/>.
    ///
    /// CancelRequested is honoured between attempts so the user isn't
    /// forced to wait out the full backoff schedule.
    /// </summary>
    private async Task<bool> DownloadVerifiedZipWithRetryAsync(string zipPath)
    {
        // Backoff schedule: 1s / 2s / 4s between attempts.
        // Three retries = four attempts total in the worst case.
        // Doubling each step keeps the budget bounded at 7s of waiting.
        int[] backoffsMs = { 1_000, 2_000, 4_000 };
        const int MaxAttempts = 4;
        Exception? lastTransient = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (CancelRequested())
            {
                Log("cancel requested between download retries");
                WriteResult(UpdateOutcome.Failed, null, null, error: "Cancelled by user during download retry.");
                WriteProgress("failed", -1, "Cancelled by user.");
                ClearCancelSignal();
                return false;
            }

            string status = attempt == 1
                ? "Downloading release zip..."
                : $"Downloading release zip (attempt {attempt} of {MaxAttempts})...";
            WriteProgress("download", 0, status);

            // 1. Try the bytes.
            try
            {
                await DownloadAsync(_args.AssetUrl!, zipPath, OnDownloadProgress).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log("download cancelled by user");
                WriteResult(UpdateOutcome.Failed, null, null, error: "Cancelled by user during download.");
                WriteProgress("failed", -1, "Cancelled by user.");
                ClearCancelSignal();
                return false;
            }
            catch (PermanentDownloadException perm)
            {
                Log($"download failed permanently ({(int)perm.StatusCode}): {perm.Message}");
                WriteResult(UpdateOutcome.Failed, null, null,
                    error: $"Download failed (HTTP {(int)perm.StatusCode}). {perm.Message}");
                WriteProgress("failed", -1, $"Download failed (HTTP {(int)perm.StatusCode}) — will not retry.");
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
            {
                lastTransient = ex;
                if (!await WaitBeforeRetryAsync(attempt, MaxAttempts, backoffsMs,
                    reason: $"download failed: {ex.Message}").ConfigureAwait(false))
                {
                    Log($"download failed after {MaxAttempts} attempts: {ex.Message}");
                    WriteResult(UpdateOutcome.Failed, null, null,
                        error: $"Download failed after {MaxAttempts} attempts: {ex.Message}");
                    WriteProgress("failed", -1, $"Download failed after {MaxAttempts} attempts: {ex.Message}");
                    return false;
                }
                continue;
            }
            catch (Exception ex)
            {
                // Anything we didn't explicitly classify is treated as
                // permanent — better to surface the unfamiliar fault to the
                // user than to retry blindly and amplify it.
                Log($"download failed (unexpected): {ex}");
                WriteResult(UpdateOutcome.Failed, null, null,
                    error: $"Download failed: {ex.Message}");
                WriteProgress("failed", -1, $"Download failed: {ex.Message}");
                return false;
            }

            // 2. Size sanity check — anything under 1 MB is almost certainly
            //    an HTML error page (rate-limit, redirect to login). Treated
            //    as a transient failure: a re-fetch often succeeds once the
            //    upstream issue clears.
            long zipBytes;
            try { zipBytes = new FileInfo(zipPath).Length; }
            catch (Exception ex)
            {
                Log($"could not stat downloaded zip: {ex.Message}");
                WriteResult(UpdateOutcome.Failed, null, null, error: $"stat zip: {ex.Message}");
                WriteProgress("failed", -1, $"Stat zip: {ex.Message}");
                return false;
            }
            if (zipBytes < 1_000_000)
            {
                lastTransient = new InvalidDataException(
                    $"download too small ({zipBytes} bytes) — likely an error page, not the release zip.");
                if (!await WaitBeforeRetryAsync(attempt, MaxAttempts, backoffsMs,
                    reason: $"download too small ({zipBytes} bytes)").ConfigureAwait(false))
                {
                    Log($"download too small ({zipBytes} bytes) after {MaxAttempts} attempts");
                    WriteResult(UpdateOutcome.Failed, null, null,
                        error: $"Downloaded asset is {zipBytes} bytes -- too small to be the release zip. URL may be wrong or the asset was redirected.");
                    WriteProgress("failed", -1, $"Download too small ({zipBytes} bytes) — gave up after {MaxAttempts} attempts.");
                    return false;
                }
                continue;
            }
            Log($"downloaded {zipBytes} bytes");

            // 3. SHA-256 verify. Read failure is permanent (the file's on
            //    our own disk — retrying won't change anything).
            //    SHA mismatch is ALSO permanent. The pre-image of the
            //    expected hash was signed/published; if the bytes on disk hash
            //    to anything else, an attacker (or, less excitingly, a broken
            //    proxy) substituted them. Retrying just hands the attacker
            //    another swing at us with the same intercept primitive. Only
            //    network / IO transients retry — integrity failures abort.
            WriteProgress("verify", -1, "Computing SHA-256 of downloaded archive...");
            string actualSha;
            try { actualSha = ComputeSha256(zipPath); }
            catch (Exception ex)
            {
                Log($"sha256 read failed: {ex.Message}");
                WriteResult(UpdateOutcome.Failed, null, null, error: $"sha256 read failed: {ex.Message}");
                WriteProgress("failed", -1, $"SHA-256 read failed: {ex.Message}");
                return false;
            }
            // Constant-time hex compare.
            if (!HexEqualsFixedTime(actualSha, _args.AssetSha256))
            {
                // Permanent failure, no retry — surface immediately so
                // the user sees the integrity violation rather than spinning
                // through the backoff schedule first.
                Log($"sha256 mismatch -- expected {_args.AssetSha256}, got {actualSha} (PERMANENT — refusing to retry)");
                WriteResult(UpdateOutcome.Failed, null, null,
                    error: $"SHA-256 mismatch on downloaded asset (expected {_args.AssetSha256}, got {actualSha}). Aborted to protect the install — integrity failure is not retried.");
                WriteProgress("failed", -1, $"SHA-256 mismatch — refusing to retry (integrity failure).");
                // Wipe the corrupted file (and its resume meta) so a future
                // run starts clean. The .meta sidecar carries the ETag
                // we just disproved -- leaving it behind would let a future
                // resume believe the bad bytes match the upstream asset.
                TryDeleteStagingPayload(zipPath);
                return false;
            }

            Log($"sha256 verified: {actualSha}");
            return true;
        }

        // Logically unreachable — every loop branch either returns or
        // calls `continue`. Kept as a defensive net so a future refactor
        // that drops a `continue` doesn't silently fall off the end.
        Log($"download retry envelope exited without success: {lastTransient?.Message ?? "unknown"}");
        WriteResult(UpdateOutcome.Failed, null, null,
            error: $"Download retry envelope exited without success: {lastTransient?.Message ?? "unknown"}");
        WriteProgress("failed", -1, "Download retry envelope exited without success.");
        return false;
    }

    /// <summary>
    /// Centralised retry-gate. Logs the failure, surfaces a
    /// "Retrying… (Nth of M)" status to the progress dialog, and waits
    /// for the configured backoff. Returns <c>true</c> if another attempt
    /// should be made, <c>false</c> if the retry budget is exhausted.
    /// </summary>
    private async Task<bool> WaitBeforeRetryAsync(int attempt, int maxAttempts, int[] backoffsMs, string reason)
    {
        Log($"attempt {attempt} of {maxAttempts} failed: {reason}");
        if (attempt >= maxAttempts) return false;

        int delayMs = backoffsMs[Math.Min(attempt - 1, backoffsMs.Length - 1)];
        WriteProgress("download", -1,
            $"Attempt {attempt} of {maxAttempts} failed ({reason}). Retrying in {delayMs / 1000}s…");

        // Wake every ~250 ms during the backoff so a user-initiated
        // cancel doesn't have to wait out the full delay.
        int waited = 0;
        while (waited < delayMs)
        {
            if (CancelRequested()) return false;
            int slice = Math.Min(250, delayMs - waited);
            await Task.Delay(slice).ConfigureAwait(false);
            waited += slice;
        }
        return true;
    }

    /// <summary>
    /// Translates byte counts from <see cref="DownloadAsync"/> into a 0–100
    /// percent + human-readable text and rate-limits writes to roughly twice
    /// per second via <see cref="WriteProgress"/>'s own throttle.
    /// </summary>
    private void OnDownloadProgress(long copied, long? total)
    {
        if (total is { } len && len > 0)
        {
            int pct = (int)Math.Min(100L, ((copied * 100L) + len - 1) / len);
            WriteProgress("download", pct, $"Downloading release zip… {copied / 1_000_000} / {len / 1_000_000} MB");
        }
        else
        {
            // Server didn't send Content-Length -- show indeterminate but
            // include the byte count so the dialog still proves liveness.
            WriteProgress("download", -1, $"Downloading release zip… {copied / 1_000_000} MB");
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(fs);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// Constant-time hex compare to drop the timing side-channel that
    /// String.Equals leaks. Length mismatches are an immediate fail (no leak —
    /// the attacker already controls the input length they sent).
    /// </summary>
    private static bool HexEqualsFixedTime(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        byte[] av = HexToBytes(a);
        byte[] bv = HexToBytes(b);
        if (av.Length == 0 || av.Length != bv.Length) return false;
        return CryptographicOperations.FixedTimeEquals(av, bv);
    }

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return Array.Empty<byte>();
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out bytes[i])) return Array.Empty<byte>();
        }
        return bytes;
    }

    /// <summary>Recursive directory copy -- fallback for the cross-volume rename case.</summary>
    /// <remarks>
    /// Derive child paths via <see cref="Path.GetRelativePath"/> rather
    /// than string.Replace. Replace mangles the destination path whenever the
    /// source's basename legitimately repeats deeper in the tree (e.g. src
    /// <c>C:\foo</c>, child <c>C:\foo\bar\foo\file</c> would rewrite BOTH
    /// occurrences and dump <c>file</c> under <c>bar\dst\</c> instead of
    /// <c>dst\bar\foo\</c>). GetRelativePath only strips the prefix and
    /// preserves repeated segments inside the subtree.
    /// </remarks>
    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            string? targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Defensive cleanup: age out anything in <paramref name="protectedFile"/>'s
    /// directory older than <paramref name="ageDays"/>, while preserving the
    /// caller-provided archive itself. The staging dir is treated as scratch
    /// space — orphaned .meta sidecars, half-downloaded .zips, stale extract
    /// folders from interrupted prior runs all accumulate here and would
    /// otherwise leak disk space across releases.
    /// </summary>
    private void TryAgeOutStaging(string protectedFile, int ageDays)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(protectedFile));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            string protectedFull;
            try { protectedFull = Path.GetFullPath(protectedFile); }
            catch { return; }

            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(ageDays);

            foreach (string file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    string full = Path.GetFullPath(file);
                    // Never touch the archive the caller asked us to apply.
                    if (string.Equals(full, protectedFull, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DateTime mtime = File.GetLastWriteTimeUtc(file);
                    if (mtime < cutoff)
                    {
                        File.Delete(file);
                        // Communication-level log per spec: surfaces the
                        // cleanup in the user-facing update log without
                        // spamming Debug-only consumers.
                        Log($"staging cleanup: deleted stale file (mtime {mtime:O} < cutoff {cutoff:O}): {file}");
                    }
                }
                catch (Exception ex) { Log($"staging cleanup: could not inspect {file}: {ex.Message}"); }
            }

            // Also age out stale extracted-* subdirectories from interrupted
            // ApplyArchiveSwap runs. They sit next to the archive (see
            // line ~478) and would otherwise leak GBs across releases.
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                try
                {
                    DateTime mtime = Directory.GetLastWriteTimeUtc(sub);
                    if (mtime < cutoff)
                    {
                        Directory.Delete(sub, recursive: true);
                        Log($"staging cleanup: deleted stale directory (mtime {mtime:O} < cutoff {cutoff:O}): {sub}");
                    }
                }
                catch (Exception ex) { Log($"staging cleanup: could not inspect {sub}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log($"staging cleanup: scan failed: {ex.Message}"); }
    }

    private void TryPruneBackups(string installRoot, int ageDays)
    {
        try
        {
            string parent = Path.GetDirectoryName(installRoot) ?? "";
            string baseName = Path.GetFileName(installRoot);
            if (parent.Length == 0 || baseName.Length == 0) return;

            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(ageDays);
            string prefix = $"{baseName}.bak.";
            foreach (string dir in Directory.EnumerateDirectories(parent, $"{baseName}.bak.*"))
            {
                try
                {
                    // Parse the timestamp from the directory name rather
                    // than reading CreationTimeUtc — Directory.Move (used during
                    // swap) preserves the original creation time of the install
                    // tree, so every backup looks "old" and the 7-day window
                    // collapses to 0. The name format is yyyyMMddHHmmss UTC, set
                    // when the backup was created in ApplyArchiveSwap.
                    string name = Path.GetFileName(dir);
                    if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        // Defence-in-depth: skip anything that doesn't match the
                        // exact prefix — the EnumerateDirectories glob is lax
                        // enough that ".bak.foo" could land here.
                        continue;
                    }
                    string stamp = name.Substring(prefix.Length);
                    if (!DateTime.TryParseExact(stamp, "yyyyMMddHHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out DateTime backupUtc))
                    {
                        // Unrecognised timestamp shape — skip rather than guess.
                        Log($"could not parse backup timestamp '{stamp}'; leaving alone.");
                        continue;
                    }
                    if (backupUtc < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                        Log($"pruned old backup: {dir}");
                    }
                }
                catch (Exception ex) { Log($"could not prune {dir}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log($"prune scan failed: {ex.Message}"); }
    }

    // ── Shutdown coordination ───────────────────────────────────────────

    private static readonly string[] SuiteImageNames =
    {
        "Phoenix.Controls.Hub",
        "Phoenix.Controls.Hub.WinUI",
        "Phoenix.Controls.Architect",
        "Phoenix.Controls.Architect.WinUI",
        "Phoenix.Controls.Visualist",
        "Phoenix.Controls.Visualist.WinUI",
    };

    /// <summary>
    /// True if any process with the given image
    /// name is alive, disposing every Process handle the query materialises.
    /// <see cref="Process.GetProcessesByName(string)"/> returns owned Process
    /// objects; checking <c>.Length</c>/<c>.Any()</c> on the bare array leaks
    /// each handle, which matters on the hot liveness-poll path.
    /// </summary>
    private static bool AnyLiveProcess(string imageName)
    {
        var procs = Process.GetProcessesByName(imageName);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>
    /// Stops the suite. In Releases mode the caller supplied <c>--hub-pid</c>
    /// so we can wait specifically for that PID to exit (it's about to call
    /// <c>Application.Exit()</c> on its own). In Update mode the caller IS
    /// the Hub itself which is exiting before we run, so we skip the
    /// wait-for-PID step and go straight to image-name discovery.
    ///
    /// After the (optional) wait, we close anything still alive across every
    /// WinForms + WinUI image name. Spec: graceful first, then
    /// <c>Process.Kill</c> after a 10s budget.
    /// </summary>
    private async Task WaitForSuiteShutdownAsync()
    {
        if (_args.HubPid > 0)
        {
            // Prefer the sentinel's (PID, StartTime) tuple over the
            // bare --hub-pid when available — covers the race where Hub exited
            // between spawning us and us reaching this line, and Windows
            // recycled its PID to an unrelated process.
            SentinelRecord? sentinel = ReadSentinel();
            string? expectedStart = (sentinel is not null && sentinel.Pid == _args.HubPid)
                ? sentinel.StartUtc
                : null;
            try
            {
                // dispose the Process handle on
                // every path — bare GetProcessById leaks the underlying OS
                // handle (and the Process object) when this scope exits.
                using var hub = Process.GetProcessById(_args.HubPid);
                if (expectedStart is not null)
                {
                    try
                    {
                        string liveStart = hub.StartTime.ToUniversalTime().ToString("O");
                        if (!string.Equals(liveStart, expectedStart, StringComparison.Ordinal))
                        {
                            Log($"Hub PID {_args.HubPid} was recycled (StartTime mismatch); treating as already exited.");
                            return;
                        }
                    }
                    catch { /* access-denied — fall through to WaitForExit */ }
                }
                Log($"waiting for Hub PID {_args.HubPid} to exit (up to 10s)...");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await hub.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { Log("Hub did not exit in time -- will force-kill."); }
            }
            catch (ArgumentException) { Log($"Hub PID {_args.HubPid} already gone."); }
            catch (Exception ex)      { Log($"Hub PID lookup error: {ex.Message}"); }
        }
        else
        {
            // Update-mode best-effort: wait briefly so any in-flight CloseMainWindow
            // calls land before we start force-killing.
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        // Graceful pass first -- request main-window close; give it 10s, then kill.
        DateTime gracefulDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        foreach (string name in SuiteImageNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    Log($"closing {name} PID {proc.Id} (graceful)");
                    proc.CloseMainWindow();
                }
                catch (Exception ex) { Log($"  CloseMainWindow failed: {ex.Message}"); }
                finally { proc.Dispose(); }
            }
        }

        // Wait until the deadline OR everything's gone.
        while (DateTime.UtcNow < gracefulDeadline)
        {
            // dispose every Process handle the
            // liveness probe materialises — GetProcessesByName returns owned
            // Process objects that otherwise leak each poll cycle (every
            // 250ms across the whole graceful window).
            bool anyAlive = SuiteImageNames.Any(n => AnyLiveProcess(n));
            if (!anyAlive) break;
            await Task.Delay(250).ConfigureAwait(false);
        }

        // Force-kill the holdouts.
        foreach (string name in SuiteImageNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    Log($"force-killing {name} PID {proc.Id}");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
                catch (Exception ex) { Log($"  kill failed: {ex.Message}"); }
                finally { proc.Dispose(); }
            }
        }
    }

    // ── Result file + relaunch ──────────────────────────────────────────

    private void WriteResult(UpdateOutcome outcome, string? oldSha, string? newSha, string? error)
    {
        var payload = new
        {
            outcome      = outcome.ToString(),
            oldSha,
            newSha,
            buildLogPath = (string?)null,
            errorMessage = error,
            timestamp    = DateTime.UtcNow.ToString("O"),
            releaseTag   = _args.ReleaseTag,
        };
        try
        {
            // Atomic write -- if Hub starts and reads the file mid-write,
            // File.WriteAllText would expose a truncated JSON document. Write
            // to a sibling .tmp first and Move-with-overwrite, which is atomic
            // on the same volume, so readers see either the previous file or
            // the complete new one -- never a half-written state.
            string tmp = _resultPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _resultPath, overwrite: true);
            Log($"wrote result file: {_resultPath} ({outcome})");
        }
        catch (Exception ex) { Log($"could not write result file: {ex.Message}"); }
    }

    /// <summary>
    /// Spawns the Hub after a successful swap. WinUI Hub wins when its exe is
    /// present at the post-swap location; falls back to the WinForms Hub.exe.
    /// Must precede WriteResult(Success) — returns true iff Hub came
    /// back up within the verification window, so the caller can write
    /// Success only after the suite is confirmed alive.
    /// </summary>
    private async Task<(bool ok, string? error)> MaybeRelaunchAsync(string installRoot)
    {
        string? error = null;
        string? target = ChooseRelaunchTarget(installRoot);
        if (target is null) { Log("no relaunch target found"); error = "no relaunch target found in install root"; return (false, error); }

        try
        {
            // UseShellExecute=true asks the OS shell to spawn the target the
            // same way a double-click would. Detaches and survives this
            // process exiting. Letting the shell pick the verb avoids cmd
            // metacharacter parsing hazards in the path (`&`, `^`, `(`, `)`,
            // `%`).
            var psi = new ProcessStartInfo(target)
            {
                WorkingDirectory = Path.GetDirectoryName(target) ?? installRoot,
                UseShellExecute  = true,
            };
            using var spawned = Process.Start(psi);
            Log($"spawned: {target}");

            // Process.Start success doesn't prove the launch target actually
            // brought Hub back up. AV quarantine, missing .NET runtime, or a
            // corrupt install all leave us logging "spawned" while the suite
            // never reappears. Wait briefly then verify Hub.exe is in the
            // process list; the caller writes Success / Failed accordingly.
            await Task.Delay(3000).ConfigureAwait(false);
            // route the post-relaunch liveness
            // probe through AnyLiveProcess so the Process handles returned by
            // GetProcessesByName are disposed instead of leaked.
            bool hubAlive = AnyLiveProcess("Phoenix.Controls.Hub")
                         || AnyLiveProcess("Phoenix.Controls.Hub.WinUI");
            if (!hubAlive)
            {
                Log("relaunch verification failed: no Hub image in the process list 3s after spawn");
                error = $"Update applied but Hub did not come back up after relaunch. Open {target} manually.";
                return (false, error);
            }
            Log("relaunch verified -- Hub is back");
            return (true, error);
        }
        catch (Exception ex)
        {
            Log($"relaunch failed: {ex.Message}");
            error = $"relaunch failed: {ex.Message}";
            return (false, error);
        }
    }

    private string? ChooseRelaunchTarget(string installRoot)
    {
        // Caller-specified launch script wins (legacy releases-mode arg).
        if (!string.IsNullOrEmpty(_args.LaunchScript) && File.Exists(_args.LaunchScript))
            return _args.LaunchScript;

        // Prefer WinUI Hub when it's been deployed.
        string winuiHub = Path.Combine(installRoot, "Hub", "Phoenix.Controls.Hub.WinUI.exe");
        if (File.Exists(winuiHub)) return winuiHub;

        // WinForms Hub.exe + LAUNCH_SUITE.bat fallbacks were retired in
        // T15; only the WinUI Hub ships now. The flat-bin dev case still applies.
        // Dev-tree fallback: flat bin where Hub.WinUI.exe sits next to the Updater.
        string flatHubWinUI = Path.Combine(installRoot, "Phoenix.Controls.Hub.WinUI.exe");
        if (File.Exists(flatHubWinUI)) return flatHubWinUI;

        return null;
    }

    // ── Logging ─────────────────────────────────────────────────────────

    private void Log(string line)
    {
        string stamped = $"[{DateTime.UtcNow:HH:mm:ss.fff}Z] {line}";
        _log.AppendLine(stamped);
        try { Console.WriteLine(stamped); } catch { }
    }

    private void FlushLog()
    {
        // Roaming log: append (one big file across all updater runs -- Hub
        // tails it). Local log: per-run timestamped file, easier to
        // attach to a bug report.
        try
        {
            File.AppendAllText(_logPath,
                $"=== run @ {DateTime.UtcNow:O} ===\n{_log}\n");
        }
        catch { }
        try
        {
            File.WriteAllText(_localLogPath,
                $"=== Phoenix.Controls.Updater run @ {DateTime.UtcNow:O} ===\n{_log}\n");
        }
        catch { }
    }

    // ── Progress pipe (Hub-side dialog reads these) ─────────────────────

    /// <summary>
    /// Writes the current phase / percent / text to
    /// <c>updater-progress.json</c> via an atomic .tmp + Move so the
    /// dialog never reads a half-written document. Throttled to ~2 writes
    /// per second for the high-frequency <c>download</c> phase; terminal
    /// states (<c>complete</c> / <c>failed</c>) and phase transitions
    /// always write through immediately.
    /// </summary>
    private void WriteProgress(string phase, int percent, string text)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            // Always write on terminal / boundary phases so the dialog
            // sees the final state immediately. Throttle the chatty
            // intra-phase percent updates to keep disk traffic sane.
            bool terminal = phase is "complete" or "failed" or "swap" or "await_hub_exit"
                                  or "query" or "verify" or "prepare";
            if (!terminal && (now - _lastProgressWriteUtc).TotalMilliseconds < 500)
                return;
            _lastProgressWriteUtc = now;

            var payload = new { phase, percent, text, timestamp = now.ToString("O") };
            string tmp = _progressPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
            File.Move(tmp, _progressPath, overwrite: true);
        }
        catch { /* best-effort; the user has the console too */ }
    }

    // ── Cancel signal (dialog Cancel button creates the file) ───────────

    /// <summary>
    /// True if the Hub-side progress dialog created
    /// <c>cancel.signal</c>. Honoured only during pre-swap phases —
    /// <see cref="_swapInFlight"/> overrides this once <c>ApplyArchiveSwap</c>
    /// is in progress so a half-applied install can never result.
    /// </summary>
    private bool CancelRequested()
    {
        if (_swapInFlight) return false;
        try { return File.Exists(_cancelPath); }
        catch { return false; }
    }

    private void ClearCancelSignal()
    {
        try { if (File.Exists(_cancelPath)) File.Delete(_cancelPath); }
        catch { }
    }

    // ── Hub-exit sentinel (Hub writes %AppData%/.../updating.lock) ──────

    /// <summary>
    /// Reads <c>updating.lock</c>, polls <see cref="Process.HasExited"/> on
    /// the recorded PID, and waits up to <paramref name="timeout"/> for
    /// it to terminate. Returns <c>true</c> if Hub is gone (or no sentinel
    /// existed in the first place — in test rigs the Hub may not stage one);
    /// <c>false</c> on timeout. Polls every 250 ms.
    /// </summary>
    private async Task<bool> AwaitSentinelHubExitAsync(TimeSpan timeout)
    {
        SentinelRecord? sentinel = ReadSentinel();
        if (sentinel is null)
        {
            Log("no sentinel found; proceeding without sentinel-PID wait.");
            return true;
        }

        Log($"sentinel PID = {sentinel.Pid} startUtc = {sentinel.StartUtc ?? "(none)"}; waiting up to {timeout.TotalSeconds:F0}s for it to exit.");

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var proc = Process.GetProcessById(sentinel.Pid);
                if (proc.HasExited)
                {
                    Log($"sentinel PID {sentinel.Pid} has exited.");
                    return true;
                }
                // The live PID might have been recycled to a different
                // process after Hub exited. Compare against the recorded start
                // time — a mismatch means Hub is already gone and we can stop
                // waiting.
                if (sentinel.StartUtc is not null)
                {
                    try
                    {
                        string liveStart = proc.StartTime.ToUniversalTime().ToString("O");
                        if (!string.Equals(liveStart, sentinel.StartUtc, StringComparison.Ordinal))
                        {
                            Log($"sentinel PID {sentinel.Pid} was recycled (StartTime mismatch); treating Hub as exited.");
                            return true;
                        }
                    }
                    catch { /* access-denied on StartTime — fall through, keep waiting */ }
                }
            }
            catch (ArgumentException)
            {
                // PID no longer in the process table -- exited cleanly.
                Log($"sentinel PID {sentinel.Pid} is gone.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"sentinel PID poll error: {ex.Message}");
                // Don't bail on a transient Process API hiccup; the deadline
                // catches genuinely-stuck Hubs.
            }
            await Task.Delay(250).ConfigureAwait(false);
        }

        Log($"sentinel PID {sentinel.Pid} did not exit within {timeout.TotalSeconds:F0}s.");
        return false;
    }

    private sealed record SentinelRecord(int Pid, string? StartUtc);

    private SentinelRecord? ReadSentinel()
    {
        try
        {
            if (!File.Exists(_sentinelPath)) return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_sentinelPath));
            if (!doc.RootElement.TryGetProperty("hubPid", out JsonElement pidEl) ||
                !pidEl.TryGetInt32(out int pid) || pid <= 0)
            {
                return null;
            }
            string? startUtc = doc.RootElement.TryGetProperty("hubStartUtc", out JsonElement stEl)
                ? stEl.GetString()
                : null;
            return new SentinelRecord(pid, startUtc);
        }
        catch (Exception ex) { Log($"sentinel parse error: {ex.Message}"); }
        return null;
    }

    private void TryDeleteSentinel()
    {
        try
        {
            if (File.Exists(_sentinelPath))
            {
                File.Delete(_sentinelPath);
                Log("deleted Hub-exit sentinel.");
            }
        }
        catch (Exception ex) { Log($"could not delete sentinel: {ex.Message}"); }
    }
}
