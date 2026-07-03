using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Discriminated state returned by <see cref="UpdateChecker.CheckAsync"/>. The
    /// Settings UI switches on the concrete type to render version + status.
    /// Every state carries enough context to be displayed without re-querying —
    /// there is no "fetch more on click" loop.
    ///
    /// Post-Releases-rework, only three states are ever produced at runtime:
    ///  • <see cref="UpToDate"/> — local version &gt;= latest published release.
    ///  • <see cref="ReleaseAvailable"/> — a tagged release on GitHub is newer
    ///    than the running assembly. Carries the asset URL + SHA-256 the
    ///    Updater needs.
    ///  • <see cref="NetworkError"/> — anything that goes wrong querying the
    ///    GitHub API.
    ///
    /// The other members (<see cref="UpdateAvailable"/>, <see cref="DirtyTree"/>,
    /// <see cref="NotOnMaster"/>, <see cref="NotAGitRepo"/>,
    /// <see cref="NotAGitHubRemote"/>) are kept on the union for ABI compat with
    /// the Settings UI's switch arms and the test suite. They are no longer
    /// returned by <see cref="UpdateChecker.CheckAsync"/> — those arms are dead
    /// in practice and will be removed alongside their UI handlers.
    /// </summary>
    public abstract record UpdateStatus
    {
        public sealed record UpToDate(string LocalSha) : UpdateStatus;
        public sealed record UpdateAvailable(string LocalSha, string RemoteSha, int CommitsBehind) : UpdateStatus;
        /// <summary>
        /// Tagged GitHub Release is newer than the running assembly.
        /// <paramref name="LocalVersion"/> / <paramref name="RemoteTag"/> are
        /// dotted triplets like "0.5.0"; <paramref name="AssetUrl"/> is the
        /// browser-download URL the Updater consumes; <paramref name="AssetSha256"/>
        /// is the lower-case hex digest read from the release's .sha256 sidecar.
        /// </summary>
        public sealed record ReleaseAvailable(string LocalVersion, string RemoteTag, string AssetUrl, string AssetSha256) : UpdateStatus;
        public sealed record DirtyTree(string LocalSha, IReadOnlyList<string> ModifiedFiles) : UpdateStatus;
        public sealed record NotOnMaster(string CurrentBranch) : UpdateStatus;
        public sealed record NotAGitRepo() : UpdateStatus;
        public sealed record NotAGitHubRemote(string RemoteUrl) : UpdateStatus;
        public sealed record NetworkError(string Message) : UpdateStatus;
    }

    /// <summary>
    /// UpdateChecker — Hub-side service that asks "is a newer release available?".
    /// Owns its own <see cref="HttpClient"/> with a short timeout and a per-call
    /// linked <see cref="CancellationTokenSource"/>. Every code path is
    /// non-throwing — errors come back as <see cref="UpdateStatus.NetworkError"/>
    /// and are logged via <see cref="GlobalLogger"/>.
    ///
    /// Releases-only flow:
    ///   * <c>GET /repos/{owner}/{repo}/releases/latest</c> — newest published tag.
    ///   * Compare <c>GitInfoService.GetAssemblyVersion()</c> with <c>tag_name</c>
    ///     (stripping the <c>release/</c> prefix) via <see cref="System.Version"/>.
    ///   * On newer-tag-available: surface <see cref="UpdateStatus.ReleaseAvailable"/>
    ///     with the .zip asset URL + the SHA-256 read from the sidecar asset.
    ///
    /// The previous SHA-comparison + dirty-tree probe was removed in the
    /// 0.6.2-era cleanup: tracked binaries got rewritten by every
    /// <c>dotnet build</c>, the dirty-tree gate then refused the next update,
    /// and end users were locked out. Distribution now flows entirely through
    /// release zips.
    /// </summary>
    public sealed class UpdateChecker : IDisposable
    {
        /// <summary>
        /// Owner/repo to query for release metadata. Hard-coded: end-user
        /// installs ship without <c>.git</c>, so we no longer try to discover
        /// this from a local remote.
        /// </summary>
#if PUBLIC_RELEASE
        public const string DefaultGitHubRepo = "Megermajo/PhoenixControls";
#else
        // Local builds of the public source stay inert: an empty owner/repo makes
        // the GitHub latest-release query 404 (suppressed), so no live update
        // fetch happens unless CI defines PUBLIC_RELEASE.
        public const string DefaultGitHubRepo = "";
#endif

        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

        private readonly HttpClient _http;
        private readonly string _userAgent;
        private CancellationTokenSource _cts = new();
        // Serialize concurrent CheckAsync calls so the
        // startup background check doesn't race a user clicking Check Now.
        // Without this gate, two overlapping checks both write LastStatus +
        // fire StatusChanged — slow-finisher's stale data wins, and the UI
        // can flip "UpdateAvailable" → "UpToDate" momentarily.
        private readonly SemaphoreSlim _checkGate = new(1, 1);
        private int _disposed;

        /// <summary>The most recent status surfaced by <see cref="CheckAsync"/>. <c>null</c> until the first call.</summary>
        public UpdateStatus? LastStatus { get; private set; }

        /// <summary>Raised on every <see cref="CheckAsync"/> completion (including error states).</summary>
        public event Action<UpdateStatus>? StatusChanged;

        public UpdateChecker(string? userAgentOverride = null)
        {
            string version = GitInfoService.GetAssemblyVersion();
            _userAgent = userAgentOverride ?? $"Phoenix.Controls/{version}";

            _http = new HttpClient { Timeout = HttpTimeout };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        /// <summary>
        /// Runs the full check pipeline. Always returns a status; never throws.
        /// </summary>
        public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
        {
            // Disposed-checker short-circuit. ShutdownAsync calls Dispose,
            // which disposes _http; an in-flight CheckAsync would otherwise crash
            // on the next GetAsync. Cheap pre-check + still safe via the catch
            // below if Dispose races between here and the linked-CTS creation.
            if (Volatile.Read(ref _disposed) != 0)
                return new UpdateStatus.NetworkError("Disposed");

            // Serialize concurrent CheckAsync calls. If the
            // gate has been disposed (Dispose ran while we were waiting), the
            // outer try/catch turns the ObjectDisposedException into a
            // NetworkError, which is the right state to surface during shutdown.
            try
            {
                await _checkGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return new UpdateStatus.NetworkError("Cancelled"); }
            catch (ObjectDisposedException)    { return new UpdateStatus.NetworkError("Disposed"); }

            UpdateStatus status;
            CancellationTokenSource? linked = null;
            try
            {
                // Cancel() races CheckAsync. Snapshot _cts under the same
                // lock the swapper uses, then build the linked CTS off the
                // snapshot. Without this, a swap-then-dispose between the field
                // read and `.Token` access throws ObjectDisposedException out of
                // the linked-CTS constructor and bypasses the try/catch (the
                // throw happens inside the using-initializer).
                CancellationTokenSource cts;
                lock (this) { cts = _cts; }
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                status = await CheckCoreAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked?.IsCancellationRequested == true)
            {
                status = new UpdateStatus.NetworkError("Cancelled");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UpdateChecker", "CheckAsync threw", ex);
                status = new UpdateStatus.NetworkError(ex.Message);
            }
            finally
            {
                linked?.Dispose();
                try { _checkGate.Release(); } catch { /* gate disposed during shutdown */ }
            }

            LastStatus = status;

            // Broadcast each subscriber via GetInvocationList so a single
            // bad handler doesn't skip later subscribers. Mirrors the
            // Bus.OnMessageReceived pattern.
            Action<UpdateStatus>? handlers = StatusChanged;
            if (handlers is not null)
            {
                foreach (Delegate d in handlers.GetInvocationList())
                {
                    try { ((Action<UpdateStatus>)d)(status); }
                    catch (Exception ex) { GlobalLogger.Error("UpdateChecker", "StatusChanged handler", ex); }
                }
            }
            return status;
        }

        private async Task<UpdateStatus> CheckCoreAsync(CancellationToken ct)
        {
            (string owner, string repo) = SplitDefaultRepo();

            ReleaseInfo? release = await GetLatestReleaseAsync(owner, repo, ct).ConfigureAwait(false);
            if (release is null)
                return new UpdateStatus.NetworkError("GitHub API: could not read latest release");

            string localVersion = GitInfoService.GetAssemblyVersion();

            if (!TryParseVersion(localVersion, out Version local))
            {
                GlobalLogger.Log($"Could not parse local version '{localVersion}'; assuming up-to-date.", "UpdateChecker", LogLevel.System);
                return new UpdateStatus.UpToDate(localVersion);
            }
            if (!TryParseVersion(release.Version, out Version remote))
            {
                GlobalLogger.Log($"Could not parse remote tag '{release.Tag}' as a version; treating as up-to-date.", "UpdateChecker", LogLevel.System);
                return new UpdateStatus.UpToDate(localVersion);
            }

            int cmp = local.CompareTo(remote);
            if (cmp == 0)
                return new UpdateStatus.UpToDate(localVersion);
            if (cmp > 0)
            {
                GlobalLogger.Log($"Local version {localVersion} is newer than latest release {release.Tag} — running an unreleased build.", "UpdateChecker", LogLevel.System);
                return new UpdateStatus.UpToDate(localVersion);
            }

            return new UpdateStatus.ReleaseAvailable(
                LocalVersion: localVersion,
                RemoteTag:    release.Version,
                AssetUrl:     release.ZipUrl,
                AssetSha256:  release.Sha256);
        }

        /// <summary>
        /// Force-fetch the latest published release's asset metadata regardless
        /// of how the local version compares. The normal CheckAsync path
        /// returns <see cref="UpdateStatus.UpToDate"/> when local == remote and
        /// gives the user no download option — that traps developers running
        /// Dev builds at the same Directory.Build.props version as master, who
        /// have local Dev binaries but want to flip to the master release zip.
        /// This method always returns <see cref="UpdateStatus.ReleaseAvailable"/>
        /// when a release exists; the caller (Settings UI's "Force download"
        /// button) feeds the result straight into <see cref="BeginApply"/>.
        /// </summary>
        public async Task<UpdateStatus> ForceDownloadLatestAsync(CancellationToken ct = default)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return new UpdateStatus.NetworkError("Disposed");

            try
            {
                await _checkGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return new UpdateStatus.NetworkError("Cancelled"); }
            catch (ObjectDisposedException)    { return new UpdateStatus.NetworkError("Disposed"); }

            UpdateStatus status;
            CancellationTokenSource? linked = null;
            try
            {
                CancellationTokenSource cts;
                lock (this) { cts = _cts; }
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

                (string owner, string repo) = SplitDefaultRepo();
                ReleaseInfo? release = await GetLatestReleaseAsync(owner, repo, linked.Token).ConfigureAwait(false);
                if (release is null)
                {
                    status = new UpdateStatus.NetworkError("GitHub API: could not read latest release");
                }
                else
                {
                    string localVersion = GitInfoService.GetAssemblyVersion();
                    status = new UpdateStatus.ReleaseAvailable(
                        LocalVersion: localVersion,
                        RemoteTag:    release.Version,
                        AssetUrl:     release.ZipUrl,
                        AssetSha256:  release.Sha256);
                }
            }
            catch (OperationCanceledException) when (linked?.IsCancellationRequested == true)
            {
                status = new UpdateStatus.NetworkError("Cancelled");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UpdateChecker", "ForceDownloadLatestAsync threw", ex);
                status = new UpdateStatus.NetworkError(ex.Message);
            }
            finally
            {
                linked?.Dispose();
                try { _checkGate.Release(); } catch { }
            }

            LastStatus = status;

            // Fire StatusChanged the same way CheckAsync does, so subscribers
            // are notified when a force-download completes (mirrors the
            // Bus.OnMessageReceived GetInvocationList pattern).
            Action<UpdateStatus>? handlers = StatusChanged;
            if (handlers is not null)
            {
                foreach (Delegate d in handlers.GetInvocationList())
                {
                    try { ((Action<UpdateStatus>)d)(status); }
                    catch (Exception ex) { GlobalLogger.Error("UpdateChecker", "StatusChanged handler", ex); }
                }
            }
            return status;
        }

        private static (string Owner, string Repo) SplitDefaultRepo()
        {
            int slash = DefaultGitHubRepo.IndexOf('/');
            return slash > 0
                ? (DefaultGitHubRepo.Substring(0, slash), DefaultGitHubRepo.Substring(slash + 1))
                : (DefaultGitHubRepo, "");
        }

        // ── Latest-release helpers ───────────────────────────────────────

        /// <summary>
        /// Subset of the GitHub /releases/latest payload the checker cares about.
        /// <see cref="Tag"/> is the raw tag (e.g. "release/0.6.0"); <see cref="Version"/>
        /// is the dotted triplet ("0.6.0"); <see cref="ZipUrl"/> is the
        /// browser-download URL of the .zip asset; <see cref="Sha256"/> is the
        /// lower-case hex digest read from the .sha256 sidecar asset.
        /// </summary>
        private sealed record ReleaseInfo(string Tag, string Version, string ZipUrl, string Sha256);

        private async Task<ReleaseInfo?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct)
        {
            string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            // Bounded retry envelope mirroring the download path:
            // 1 s / 2 s / 4 s backoff, four attempts total,
            // BUT 404/401/403 short-circuit immediately because retrying a
            // missing/forbidden release URL just wastes the polling budget.
            int[] backoffsMs = { 1_000, 2_000, 4_000 };
            const int MaxAttempts = 4;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using HttpResponseMessage resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var code = resp.StatusCode;
                        bool terminal = code == System.Net.HttpStatusCode.NotFound
                                     || code == System.Net.HttpStatusCode.Unauthorized
                                     || code == System.Net.HttpStatusCode.Forbidden;
                        if (terminal)
                        {
                            // 404 on /releases/latest is the normal first-run state
                            // (a freshly-published project with no Releases yet).
                            // Suppress entirely so the System Log doesn't lead with
                            // "GitHub API 404" every startup. 401/403 are real auth
                            // / permission problems and stay at System tier so the
                            // operator notices them.
                            if (code != System.Net.HttpStatusCode.NotFound)
                            {
                                GlobalLogger.Log($"GitHub API {(int)code} on {url} (terminal — not retrying)", "UpdateChecker", LogLevel.System);
                            }
                            return null;
                        }
                        // Transient (5xx, 429, etc.) — fall through to retry.
                        GlobalLogger.Log($"GitHub API {(int)code} on {url} (attempt {attempt}/{MaxAttempts})", "UpdateChecker", LogLevel.System);
                        if (attempt >= MaxAttempts) return null;
                        await DelayWithCancelAsync(backoffsMs[Math.Min(attempt - 1, backoffsMs.Length - 1)], ct).ConfigureAwait(false);
                        continue;
                    }
                    using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
                    JsonElement root = doc.RootElement;

                    string? tag = root.TryGetProperty("tag_name", out JsonElement tagEl) ? tagEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(tag)) return null;
                    string version = StripReleasePrefix(tag);

                    if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                        return null;

                    string? zipUrl = null;
                    string? shaUrl = null;
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                        string? dl   = asset.TryGetProperty("browser_download_url", out JsonElement d) ? d.GetString() : null;
                        if (name is null || dl is null) continue;
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && zipUrl is null) zipUrl = dl;
                        else if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase) && shaUrl is null) shaUrl = dl;
                    }
                    if (zipUrl is null || shaUrl is null)
                    {
                        GlobalLogger.Log($"Release {tag} missing .zip or .zip.sha256 asset", "UpdateChecker", LogLevel.System);
                        return null;
                    }

                    string? sha = await FetchSidecarShaAsync(shaUrl, ct).ConfigureAwait(false);
                    if (sha is null) return null;
                    return new ReleaseInfo(tag, version, zipUrl, sha);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
                {
                    // Transient network blip — retry.
                    GlobalLogger.Log($"GetLatestRelease attempt {attempt}/{MaxAttempts} failed: {ex.Message}", "UpdateChecker", LogLevel.System);
                    if (attempt >= MaxAttempts)
                    {
                        GlobalLogger.Error("UpdateChecker", "GetLatestRelease (exhausted)", ex);
                        return null;
                    }
                    await DelayWithCancelAsync(backoffsMs[Math.Min(attempt - 1, backoffsMs.Length - 1)], ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Anything else (JSON parse, etc.) is treated as permanent — surface and bail.
                    GlobalLogger.Error("UpdateChecker", "GetLatestRelease", ex);
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        /// Cancellable delay used by the GetLatestRelease retry
        /// envelope so a CheckAsync cancellation doesn't have to wait out the
        /// full backoff slot. Plain Task.Delay would honour the CT but throw —
        /// we already do try/catch around the loop body, so this is just a
        /// convenience wrapper that surfaces cancellation immediately.
        /// </summary>
        private static async Task DelayWithCancelAsync(int delayMs, CancellationToken ct)
        {
            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        private async Task<string?> FetchSidecarShaAsync(string url, CancellationToken ct)
        {
            try
            {
                using HttpResponseMessage resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                // Sidecar can be either bare hex or sha256sum format ("<hex>  filename").
                // Take the leading 64-char hex token in either case.
                string trimmed = body.TrimStart();
                int len = 0;
                while (len < trimmed.Length && IsHexChar(trimmed[len])) len++;
                if (len < 64) return null;
                return trimmed.Substring(0, 64).ToLowerInvariant();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                GlobalLogger.Error("UpdateChecker", "FetchSidecarSha", ex);
                return null;
            }
        }

        private static bool IsHexChar(char c)
            => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        /// <summary>
        /// Strips a leading <c>release/</c> from a tag name. Public-friendly
        /// helper used by tests; idempotent on already-stripped strings.
        /// </summary>
        public static string StripReleasePrefix(string tag)
        {
            const string p = "release/";
            return tag.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                ? tag.Substring(p.Length)
                : tag;
        }

        /// <summary>
        /// Permissive version parse: accepts "0.6.0", "0.6.0.0", and a leading
        /// "v"; rejects pre-release suffixes (we don't ship those today).
        /// </summary>
        public static bool TryParseVersion(string s, out Version v)
        {
            v = new Version(0, 0);
            if (string.IsNullOrWhiteSpace(s)) return false;
            string trimmed = s.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(1);
            return Version.TryParse(trimmed, out v!);
        }

        // ── Remote URL parsing ───────────────────────────────────────────

        /// <summary>
        /// Extracts owner / repo from a GitHub remote URL. Handles both forms:
        ///   * <c>https://github.com/Owner/Repo.git</c>
        ///   * <c>git@github.com:Owner/Repo.git</c>
        /// Returns false for non-GitHub remotes (gitlab, bitbucket, internal).
        /// Kept as a public helper because the Settings UI uses it to render
        /// the apply-confirm dialog's source label.
        /// </summary>
        public static bool TryParseGitHubRemote(string remoteUrl, out string owner, out string repo)
        {
            owner = ""; repo = "";
            if (string.IsNullOrWhiteSpace(remoteUrl)) return false;

            string url = remoteUrl.Trim();
            string? path = null;

            const string httpsPrefix = "https://github.com/";
            const string sshPrefix = "git@github.com:";
            if (url.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
                path = url.Substring(httpsPrefix.Length);
            else if (url.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
                path = url.Substring(sshPrefix.Length);
            else
                return false;

            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 4);
            path = path.TrimEnd('/');

            int slash = path.IndexOf('/');
            if (slash <= 0 || slash == path.Length - 1) return false;

            owner = path.Substring(0, slash);
            repo = path.Substring(slash + 1);
            return owner.Length > 0 && repo.Length > 0;
        }

        // ── Cancellation / lifetime ──────────────────────────────────────

        public void Cancel()
        {
            CancellationTokenSource old;
            lock (this)
            {
                old = _cts;
                _cts = new CancellationTokenSource();
            }
            try { old.Cancel(); old.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { _http.Dispose(); } catch { }
            try { _checkGate.Dispose(); } catch { }
        }

        // ── Post-update result file (written by Phoenix.Controls.Updater) ──

        /// <summary>
        /// Outcome the previous Updater run wrote to last-update-result.json.
        /// Hub reads this on startup and surfaces the result once, then deletes
        /// the file. <see cref="Outcome"/> values: "Success", "RolledBack",
        /// "Failed". Other fields are best-effort (may be null).
        /// </summary>
        public sealed record LastUpdateResult(
            string  Outcome,
            string? OldSha,
            string? NewSha,
            string? BuildLogPath,
            string? ErrorMessage,
            string? Timestamp);

        private static string ResultFilePath =>
            Path.Combine(Phoenix.Controls.Shared.Core.Paths.RoamingAppData("Hub"), "last-update-result.json");

        /// <summary>
        /// Reads (and deletes) the result file written by Phoenix.Controls.Updater
        /// on the previous run. Returns null if the file is absent or unparseable
        /// — callers should treat null as "no recent update activity, nothing to
        /// surface". Always non-throwing.
        /// </summary>
        public static LastUpdateResult? ReadAndClearLastUpdateResult()
        {
            string path = ResultFilePath;
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                var result = new LastUpdateResult(
                    Outcome:      root.TryGetProperty("outcome",      out JsonElement o) ? (o.GetString() ?? "") : "",
                    OldSha:       root.TryGetProperty("oldSha",       out JsonElement os) ? os.GetString() : null,
                    NewSha:       root.TryGetProperty("newSha",       out JsonElement ns) ? ns.GetString() : null,
                    BuildLogPath: root.TryGetProperty("buildLogPath", out JsonElement bl) ? bl.GetString() : null,
                    ErrorMessage: root.TryGetProperty("errorMessage", out JsonElement em) ? em.GetString() : null,
                    Timestamp:    root.TryGetProperty("timestamp",    out JsonElement ts) ? ts.GetString() : null);
                try { File.Delete(path); } catch { }
                return result;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UpdateChecker", "ReadAndClearLastUpdateResult", ex);
                try { File.Delete(path); } catch { }
                return null;
            }
        }

        // ── Spawning Phoenix.Controls.Updater ──────────────────────────

        /// <summary>
        /// Legacy overload kept so the Settings UI's <c>UpdateAvailable</c> arm
        /// (which can no longer be reached at runtime — see CheckCoreAsync)
        /// still compiles. The legacy git-checkout update flow has been
        /// removed; this overload now just logs and returns false.
        /// </summary>
        public static bool BeginApply(string repoRoot)
        {
            GlobalLogger.Log(
                "BeginApply(repoRoot) was invoked but the legacy git-checkout update flow has been removed. " +
                "Use BeginApply(ReleaseAvailable, installRoot) — release-zip distribution is the only supported update path.",
                "UpdateChecker", LogLevel.System);
            return false;
        }

        /// <summary>
        /// Resolves the suite root — the folder that holds the per-app
        /// subfolders (<c>Hub\</c>, <c>Updater\</c>, <c>Viewer\</c>) in the
        /// installer / Releases layout. The running Hub.WinUI.exe lives in
        /// <c>&lt;suiteRoot&gt;\Hub\</c>, so when our base dir is named "Hub"
        /// the suite root is its parent; a flat dev-tree (everything in one
        /// bin) has no "Hub" wrapper, so the base dir itself is the root.
        /// This is the value the Updater expects as <c>--install-root</c> and
        /// the anchor for locating <c>Updater\Phoenix.Controls.Updater.exe</c>.
        /// </summary>
        public static string ResolveSuiteRoot()
        {
            string baseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            try
            {
                var di = new DirectoryInfo(baseDir);
                if (string.Equals(di.Name, "Hub", StringComparison.OrdinalIgnoreCase) && di.Parent is not null)
                    return di.Parent.FullName;
            }
            catch { /* unparseable base dir — fall back to it as-is below */ }
            return baseDir;
        }

        /// <summary>
        /// Spawns Phoenix.Controls.Updater.exe with the asset URL + SHA-256 +
        /// tag taken from a <see cref="UpdateStatus.ReleaseAvailable"/> state.
        /// <paramref name="installRoot"/> is the folder the running suite lives
        /// in (the <c>phoenix-controls/</c> dir of a Releases install).
        /// Writes the Hub-exit sentinel (<see cref="SentinelFilePath"/>) before
        /// returning so the Updater can wait for this Hub PID to exit before
        /// touching files. Caller still owns the actual <c>Application.Exit</c>
        /// call — we only stage the sentinel so the wait order is deterministic
        /// even if Hub crashes in shutdown.
        /// </summary>
        public static bool BeginApply(UpdateStatus.ReleaseAvailable release, string installRoot)
        {
            // The Updater ships in a SIBLING folder of the Hub exe in the
            // installer / Releases layout (<suiteRoot>\Updater\…), NOT next to
            // Hub (<suiteRoot>\Hub\…) — Hub.WinUI doesn't ProjectReference the
            // Updater, so its exe is never copied into Hub\. Resolve the suite
            // root from the running Hub's base dir and look there; fall back to
            // a flat dev-tree where the Updater sits alongside Hub in one bin.
            string suiteRoot = ResolveSuiteRoot();
            string updaterDir = Path.Combine(suiteRoot, "Updater");
            string updaterExe = Path.Combine(updaterDir, "Phoenix.Controls.Updater.exe");
            if (!File.Exists(updaterExe))
            {
                string flat = Path.Combine(AppContext.BaseDirectory, "Phoenix.Controls.Updater.exe");
                if (File.Exists(flat)) updaterExe = flat;
            }
            if (!File.Exists(updaterExe))
            {
                GlobalLogger.Log(
                    $"Phoenix.Controls.Updater.exe not found (looked in {updaterDir} and next to Hub)",
                    "UpdateChecker", LogLevel.CriticalError);
                return false;
            }

            try
            {
                int hubPid = Environment.ProcessId;

                // Stage the Hub-exit sentinel BEFORE spawning the Updater. The
                // Updater's await_hub_exit phase reads this file and waits on
                // the recorded PID; if Hub catches an unrelated exception
                // mid-shutdown the Updater sees the PID is still alive and
                // refuses to mutate files until the timeout fires (or we exit
                // cleanly). Stale sentinels (Hub gone, file remained) are
                // detected by the Updater on next launch via
                // <see cref="IsSentinelStale"/> and silently cleaned.
                //
                // If WriteSentinel throws, the Updater would launch
                // with NO sentinel — its await_hub_exit phase has nothing to
                // key off and the swap can race Hub's still-running file
                // handles. Abort the launch rather than soft-bricking the
                // update coordination. Surface a CriticalError so the System
                // Log captures the reason; the user sees the auto-update
                // settings button stay enabled and can retry.
                try { WriteSentinel(hubPid); }
                catch (Exception sx)
                {
                    GlobalLogger.Error("UpdateChecker", "WriteSentinel", sx);
                    GlobalLogger.Log(
                        $"Aborting update launch: could not write Hub-exit sentinel ({sx.Message}). " +
                        "The Updater would have no way to wait for Hub to exit before touching files.",
                        "UpdateChecker", LogLevel.CriticalError);
                    return false;
                }

                // Clear any leftover progress / cancel files from a previous
                // run so the dialog doesn't latch onto stale state.
                try { if (File.Exists(ProgressFilePath))     File.Delete(ProgressFilePath); } catch { }
                try { if (File.Exists(CancelSignalFilePath)) File.Delete(CancelSignalFilePath); } catch { }

                var psi = new ProcessStartInfo(updaterExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow  = false, // Show the console so the user sees progress.
                    WorkingDirectory = installRoot,
                };
                // Use ArgumentList instead of Arguments string. Manual
                // double-quoting breaks when installRoot ends in a backslash:
                // `\"C:\foo\\\"` collapses the closing quote and the next arg
                // gets glued to the path. ArgumentList runs Win32-spec quoting
                // per element, which is the only correct option on Windows.
                psi.ArgumentList.Add("--install-root");
                psi.ArgumentList.Add(installRoot);
                psi.ArgumentList.Add("--hub-pid");
                psi.ArgumentList.Add(hubPid.ToString());
                psi.ArgumentList.Add("--asset-url");
                psi.ArgumentList.Add(release.AssetUrl);
                psi.ArgumentList.Add("--asset-sha256");
                psi.ArgumentList.Add(release.AssetSha256);
                psi.ArgumentList.Add("--release-tag");
                psi.ArgumentList.Add(release.RemoteTag);
                System.Diagnostics.Process.Start(psi);
                GlobalLogger.Log($"Spawned updater (hubPid={hubPid}, installRoot={installRoot}, tag={release.RemoteTag})", "UpdateChecker", LogLevel.System);
                return true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UpdateChecker", "BeginApply", ex);
                return false;
            }
        }

        // ── Hub-exit sentinel + Updater progress-pipe paths ──────────────

        /// <summary>
        /// <c>%AppData%/PhoenixControls/Hub/updating.lock</c>. Hub writes this
        /// before requesting <c>Application.Exit</c>; the Updater reads it,
        /// waits for the named PID to exit, then deletes it as the very last
        /// step of a successful update. A stale sentinel (PID gone, file
        /// remained) is detected by <see cref="IsSentinelStale"/> on next
        /// launch and silently cleared.
        /// </summary>
        public static string SentinelFilePath =>
            Path.Combine(Phoenix.Controls.Shared.Core.Paths.RoamingAppData("Hub"), "updating.lock");

        /// <summary>
        /// <c>%AppData%/PhoenixControls/Hub/updater-progress.json</c>. Updater
        /// writes its current phase / percent / status text here roughly every
        /// 500 ms; <see cref="UpdaterProgressDialog"/> polls it at 250 ms via a
        /// <c>DispatcherTimer</c>. Atomic-write via sibling <c>.tmp</c> +
        /// <c>File.Move(overwrite:true)</c> so readers see whole snapshots.
        /// </summary>
        public static string ProgressFilePath =>
            Path.Combine(Phoenix.Controls.Shared.Core.Paths.RoamingAppData("Hub"), "updater-progress.json");

        /// <summary>
        /// <c>%AppData%/PhoenixControls/Hub/cancel.signal</c>. Sentinel touched
        /// by the progress dialog's Cancel button; the Updater polls for it
        /// during pre-swap phases (query / download / verify / prepare) and
        /// aborts cleanly when present. Ignored once the swap is in flight —
        /// at that point cancel would leave a half-applied install.
        /// </summary>
        public static string CancelSignalFilePath =>
            Path.Combine(Phoenix.Controls.Shared.Core.Paths.RoamingAppData("Hub"), "cancel.signal");

        /// <summary>
        /// JSON shape Updater writes to <see cref="ProgressFilePath"/>.
        /// <see cref="Phase"/> is one of <c>query / download / verify /
        /// prepare / await_hub_exit / swap / complete / failed</c> (Hub UI
        /// only ever sees the first five — after <c>await_hub_exit</c> Hub
        /// itself has been told to exit). <see cref="Percent"/> is 0–100,
        /// <c>-1</c> meaning indeterminate.
        /// </summary>
        public sealed record UpdaterProgress(string Phase, int Percent, string Text);

        /// <summary>
        /// Reads the Updater progress JSON. Returns <c>null</c> if the file
        /// doesn't exist or is malformed. Always non-throwing — the dialog
        /// polls this aggressively and a transient half-write must not crash
        /// the UI.
        /// </summary>
        public static UpdaterProgress? ReadProgress()
        {
            string path = ProgressFilePath;
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string phase   = root.TryGetProperty("phase",   out JsonElement p)  ? (p.GetString()  ?? "") : "";
                int percent    = root.TryGetProperty("percent", out JsonElement pc) && pc.TryGetInt32(out int pv) ? pv : -1;
                string text    = root.TryGetProperty("text",    out JsonElement t)  ? (t.GetString()  ?? "") : "";
                return new UpdaterProgress(phase, percent, text);
            }
            catch
            {
                // Half-written file mid-poll. Caller will retry on next tick.
                return null;
            }
        }

        /// <summary>
        /// Writes the Hub-exit sentinel. Atomic via .tmp + Move-overwrite so a
        /// reader (the spawned Updater) never sees a half-written file.
        /// The sentinel identifies Hub by the (PID, StartTime)
        /// tuple, not PID alone — Windows recycles PIDs, and a recycled PID can
        /// either prematurely satisfy "Hub gone" (swap-while-Hub-alive) or
        /// keep the wait alive for the full timeout when the wrong process
        /// happens to inherit the number (aborted update). StartTime is
        /// expressed as the process's <see cref="Process.StartTime"/> in UTC
        /// ISO-8601 round-trip ("O") format for cross-process compare.
        /// </summary>
        public static void WriteSentinel(int hubPid)
        {
            string path = SentinelFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string? hubStart = null;
            try
            {
                using var self = System.Diagnostics.Process.GetProcessById(hubPid);
                hubStart = self.StartTime.ToUniversalTime().ToString("O");
            }
            catch { /* best-effort — pre-tuple-lookup readers fall back to PID alone */ }
            var payload = new { hubPid, hubStartUtc = hubStart, timestamp = DateTime.UtcNow.ToString("O") };
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>
        /// Sentinel age (in minutes) after which a Win32-blocked
        /// PID probe is treated as stale. The timestamp guard is the safety
        /// net for hostile AV environments where <c>Process.GetProcessById</c>
        /// throws <see cref="System.ComponentModel.Win32Exception"/> with
        /// "access denied". The PID probe is best-effort; if it succeeds
        /// the tuple compare is authoritative, but if it can't run we fall
        /// back to "sentinel is old enough that no real update is in flight
        /// any more". 30 min comfortably exceeds the slowest observed full
        /// download+swap (≈3 min on a healthy machine, ≈8 min on a slow
        /// network), so a live update never trips the timestamp guard while
        /// a long-dead one always does.
        /// </summary>
        private const int SentinelStaleAfterMinutes = 30;

        /// <summary>
        /// Returns true if a sentinel exists but the recorded (PID, StartTime)
        /// tuple no longer matches a live process — the previous Hub crashed
        /// mid-update before Application.Exit or before the Updater could
        /// clear it, OR the PID has been recycled to a different process since.
        /// Caller (<see cref="ClearStaleSentinel"/>) silently deletes such files
        /// on next Hub launch.
        ///
        /// Win32Exception / access-denied probes (hostile AV,
        /// elevated targets) no longer return false unconditionally — that
        /// bricked the sentinel-clear path on machines where the PID probe
        /// can never succeed, leaving a stuck sentinel that nothing could
        /// auto-expire. Now they fall back to the timestamp guard: if the
        /// sentinel's recorded write-time is older than
        /// <see cref="SentinelStaleAfterMinutes"/>, treat as stale; younger
        /// than that, give the in-flight update the benefit of the doubt
        /// and leave the file alone.
        /// </summary>
        public static bool IsSentinelStale(out int recordedPid)
        {
            recordedPid = 0;
            string path = SentinelFilePath;
            if (!File.Exists(path)) return false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("hubPid", out JsonElement pidEl) ||
                    !pidEl.TryGetInt32(out recordedPid) || recordedPid <= 0)
                    return true; // Malformed / no PID — treat as stale.

                string? recordedStart = doc.RootElement.TryGetProperty("hubStartUtc", out JsonElement stEl)
                    ? stEl.GetString()
                    : null;

                // Snapshot the sentinel's own write-time before
                // probing. Used by the Win32-exception fallback below; safe
                // to compute even on the happy path because it's a cheap
                // string parse and avoids re-reading the JsonDocument.
                string? recordedTimestamp = doc.RootElement.TryGetProperty("timestamp", out JsonElement tsEl)
                    ? tsEl.GetString()
                    : null;

                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(recordedPid);
                    if (proc.HasExited) return true;
                    // Tuple mismatch ⇒ PID was recycled to a different
                    // process. Treat as stale so the sentinel-clear path runs.
                    if (recordedStart is not null)
                    {
                        try
                        {
                            string liveStart = proc.StartTime.ToUniversalTime().ToString("O");
                            if (!string.Equals(liveStart, recordedStart, StringComparison.Ordinal))
                                return true;
                        }
                        catch { /* access-denied on StartTime — fall through, treat live */ }
                    }
                    return false;
                }
                catch (ArgumentException) { return true; } // PID not running.
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // AV / elevation / sandbox blocked the PID
                    // handle. Fall back to the timestamp guard: anything
                    // older than the threshold is treated as stale so a
                    // long-abandoned sentinel still auto-expires.
                    bool agedOut = IsSentinelAgedOut(recordedTimestamp);
                    GlobalLogger.Log(
                        $"Sentinel PID probe failed ({ex.NativeErrorCode}: {ex.Message}); " +
                        $"timestamp guard says {(agedOut ? "stale" : "live")} (cutoff {SentinelStaleAfterMinutes} min).",
                        "UpdateChecker", LogLevel.System);
                    return agedOut;
                }
                catch (UnauthorizedAccessException)
                {
                    // Same family as Win32 access-denied — apply the same
                    // timestamp fallback.
                    return IsSentinelAgedOut(recordedTimestamp);
                }
                catch (InvalidOperationException) { return true; } // Process exited between calls.
            }
            catch
            {
                // Unparseable file — clear it on next launch.
                return true;
            }
        }

        /// <summary>
        /// True when the sentinel's recorded write-time is older
        /// than <see cref="SentinelStaleAfterMinutes"/>. Safety net for
        /// the PID probe — used when <c>Process.GetProcessById</c> can't
        /// be evaluated (Win32Exception / UnauthorizedAccessException) so
        /// the sentinel doesn't get permanently stuck.
        /// Unparseable / missing timestamps are treated as stale (the
        /// sentinel is malformed regardless and ClearStaleSentinel should
        /// rebuild it). Returns false if the timestamp is in the future
        /// (clock skew or a freshly-written sentinel from a process we
        /// just spawned) so we don't kill our own in-flight update.
        /// </summary>
        private static bool IsSentinelAgedOut(string? recordedTimestamp)
        {
            if (string.IsNullOrWhiteSpace(recordedTimestamp)) return true;
            if (!DateTime.TryParse(recordedTimestamp,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTime writtenUtc))
                return true;
            writtenUtc = writtenUtc.ToUniversalTime();
            var age = DateTime.UtcNow - writtenUtc;
            if (age < TimeSpan.Zero) return false;  // clock skew / fresh write — assume live
            return age >= TimeSpan.FromMinutes(SentinelStaleAfterMinutes);
        }

        /// <summary>
        /// Clears a stale sentinel left over from a crashed prior run. Safe
        /// to call on every Hub launch — non-stale sentinels (live Updater
        /// holding the file) are left alone, missing files are a no-op.
        /// </summary>
        public static void ClearStaleSentinel()
        {
            try
            {
                if (IsSentinelStale(out int recordedPid))
                {
                    string path = SentinelFilePath;
                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch (Exception ex) { GlobalLogger.Error("UpdateChecker", "ClearStaleSentinel.Delete", ex); }
                        GlobalLogger.Log($"Cleared stale Hub-exit sentinel (PID {recordedPid} is no longer running).",
                            "UpdateChecker", LogLevel.System);
                    }
                }
            }
            catch (Exception ex) { GlobalLogger.Error("UpdateChecker", "ClearStaleSentinel", ex); }
        }
    }
}
