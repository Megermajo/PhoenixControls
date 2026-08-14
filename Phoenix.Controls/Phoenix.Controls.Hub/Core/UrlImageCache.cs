using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// UrlImageCache — Hub-side production cache for <c>Image.LoadUrl</c> nodes.
    /// Maps a URL to <c>%LOCALAPPDATA%/PhoenixControls/Hub/cache/&lt;sha256&gt;.&lt;ext&gt;</c>.
    /// HTTP-only fetch (no Chromium); the Visualist-side WebView2 cache for editor previews
    /// is queued for a later phase.
    ///
    /// Writes go to a <c>.tmp</c> sibling and only get atomically moved into place after
    /// the entire body has streamed successfully. A failed fetch never leaves a 0-byte cache file.
    ///
    /// SSRF defense + MIME / magic-byte / size validation. Pre-fetch we reject non-HTTP(S)
    /// schemes and any host that resolves to a loopback / link-local / private / ULA address
    /// (incl. cloud metadata 169.254.169.254). Redirects are followed MANUALLY with the same
    /// validator re-run on every <c>Location</c> and a 5-hop cap, because auto-redirect would
    /// walk a single 302 straight past the pre-fetch check. Post-fetch we verify Content-Type
    /// against an allowlist, sniff the first bytes for a matching magic header, and cap body
    /// size at AppConfig.MaxAssetSizeBytes (default 5 MiB).
    ///
    /// The cache filename's extension is the canonical one for the MIME the guard ACCEPTED —
    /// never one guessed from the remote URL's path — because <c>HUDServer</c>'s
    /// <c>/asset/url</c> response Content-Type is derived from it.
    /// </summary>
    public sealed class UrlImageCache : IDisposable
    {
        /// <summary>
        /// How long a cached file is considered fresh before being re-fetched.
        /// Defaults to 24h. Settable so callers (typically HubBootstrapper)
        /// can drive it from configuration without touching the constructor signature.
        /// Wired from <c>AppConfig.UrlImageCacheTtlHours</c> at the HUDServer ctor.
        /// </summary>
        public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

        // Hard cap on any single fetch attempt. Without this,
        // a slow-loris CDN could hold the HttpClient socket open indefinitely —
        // HttpClient.Timeout is set at construction time on shared injected
        // instances we don't own, so we layer a per-call deadline via a linked
        // CancellationTokenSource. Ten seconds is long enough for a 5 MB
        // image over a marginal connection but short enough that a stalled
        // fetch fails before the OBS browser source notices a missing asset.
        private static readonly TimeSpan PerFetchTimeout = TimeSpan.FromSeconds(10);

        // Soft cap on cache directory size. When the cache
        // total exceeds this value after a successful fetch, the LRU sweep
        // (oldest-LastWriteTime-first) deletes entries until the total drops
        // below the cap. The value is local rather than wired through
        // AppConfig to keep the sweep within the Hub project boundary.
        // 256 MiB is generous for a typical overlay
        // session while bounding long-running Hub processes that accumulate
        // stale CDN payloads across many stream sessions.
        public long MaxCacheBytes { get; set; } = 256L * 1024 * 1024;

        // Single-in-flight gate for the LRU sweep. SuccessfulFetch enqueues a
        // sweep iff one isn't already running; subsequent fetches during the
        // sweep see _sweepRunning == 1 and skip, so there's at most one
        // sweep walk over the cache directory at a time. Interlocked so the
        // check + set is atomic across the fan-out of concurrent fetches.
        private int _sweepRunning;

        // Per-URL fetch lock. Without this, two widgets pointing at
        // the same external image both raced the same `<sha>.tmp` write. The
        // unique-tmp-suffix change in M-prev solved torn writes but didn't
        // deduplicate the fetches themselves; cold-cache spikes for shared
        // images still hit the origin N times. A per-URL SemaphoreSlim
        // serializes the cold-fetch critical section so the first caller does
        // the HTTP work and writes the file, and subsequent callers fall
        // through to the cache-hit fast path on their re-entry below.
        //
        // Refcount tracking lets us remove the semaphore from the dict once
        // the last waiter releases, so a one-off URL doesn't leak a
        // semaphore for the process lifetime. The refcount lives in a tuple
        // alongside the semaphore so the inc/dec stays atomic with the
        // dict lookup.
        private sealed class FetchLock
        {
            public readonly SemaphoreSlim Sem = new SemaphoreSlim(1, 1);
            public int RefCount;
        }
        private readonly ConcurrentDictionary<string, FetchLock> _fetchLocks = new();

        // Negative result cache. A failed fetch (5xx, MIME reject,
        // SSRF reject) used to re-hit the origin on every subsequent /asset/url
        // request, amplifying any client-side retry storm into a DDoS against
        // the origin. Cache the failure for a short TTL so the next attempts
        // skip the origin. Keyed by sha256(url) for stability across
        // querystring ordering / case differences.
        //
        // It suppresses the FETCH, never the SERVE. The entry is shared by every
        // caller of a URL — including sibling Image.LoadUrl nodes that pass no
        // freshness token — so a stamp laid down by one failed forced
        // revalidation must not blank a widget whose TTL-fresh, already-validated
        // file is still sitting in the cache directory. Every read path therefore
        // falls back to TryServeUsableCachedPath before returning null, no stamp
        // is laid down while such a file exists, and a successful fetch clears
        // any stamp a racing failure left behind.
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(60);
        private readonly ConcurrentDictionary<string, DateTime> _negativeCache = new();

        // Last-access timestamps for LRU eviction. The previous sweep
        // sorted by LastWriteTimeUtc (= fetch time), which meant a freshly-
        // fetched-and-never-used image survived while a stale-mtime hot image
        // got evicted. Track in-process access time on TryGet hits and prefer
        // that over mtime when present; fall back to mtime for files we don't
        // have an access stamp for (e.g. on first run after process restart).
        private readonly ConcurrentDictionary<string, DateTime> _lastAccessUtc = new();

        // Client-supplied freshness token, keyed by sha256(url).
        // compositor.js appends a cache-busting `_ts` bucket to /asset/url for
        // WebSource nodes with RefreshSeconds > 0; the bucket flips value once
        // per refresh window. The route used to drop it entirely, so every
        // WebSource was pinned to the global 24h Ttl and a live scoreboard
        // painted exactly one frame per stream. We treat the token as opaque:
        // a value we have not fetched under yet means "revalidate against the
        // origin now". Storing the instant of the last forced refetch alongside
        // it lets us rate-limit, so a page spinning the token can't turn the
        // proxy into an origin-hammering relay.
        private readonly ConcurrentDictionary<string, (string Token, DateTime ForcedAtUtc)> _freshnessTokens = new();
        private static readonly TimeSpan MinForcedRefetchInterval = TimeSpan.FromSeconds(1);
        private const int MaxFreshnessTokenLength = 64;

        // Absolute per-map cap on the three per-URL bookkeeping maps above
        // (_negativeCache, _lastAccessUtc, _freshnessTokens). The disk LRU
        // sweep reclaims FILES, not these maps — before this cap a WebSource
        // or script feeding rotating URLs (per-stream query strings, signed
        // CDN links) grew each map by one entry per distinct URL for the
        // process lifetime; only ClearCache / Dispose ever emptied them. On
        // breach the oldest entries by the map's own timestamp semantics are
        // dropped down to a lower watermark (7/8 of the cap) so a saturated
        // map pays the snapshot + sort once per batch of inserts rather than
        // once per insert.
        //
        // Evicting a bookkeeping row is always SAFE, never a wrong answer:
        //   - a swept negative-cache stamp just means the next request for
        //     that URL re-validates against the origin instead of being
        //     suppressed — one extra origin attempt, then it re-stamps on
        //     failure;
        //   - a swept freshness token means the next `_ts` bucket counts as
        //     unseen and forces exactly one refetch (unthrottled once,
        //     because the rate-limit stamp lives in the same entry), after
        //     which the re-created entry restores both dedupe and throttle;
        //   - a swept last-access stamp makes the disk LRU fall back to the
        //     file's mtime, exactly as it already does for files fetched by
        //     a previous process run.
        // 4096 entries is a few hundred KiB worst-case across all three maps
        // while comfortably exceeding the distinct-URL count of any realistic
        // overlay session between process restarts. Settable like
        // MaxCacheBytes so tests can exercise the bound without minting
        // thousands of URLs; <= 0 disables the cap (mirrors MaxCacheBytes).
        public int MaxBookkeepingEntries { get; set; } = 4096;

        // Single-in-flight gates for the per-map trims, mirroring
        // _sweepRunning: the breacher that wins the CAS pays the trim and
        // concurrent inserts skip rather than stack snapshot+sort passes.
        private int _negativeCacheTrimRunning;
        private int _lastAccessTrimRunning;
        private int _freshnessTokenTrimRunning;

        // Test / diagnostics seams: entry counts for the bookkeeping maps so
        // the MaxBookkeepingEntries bound is observable without reflection.
        public int NegativeCacheEntryCount => _negativeCache.Count;
        public int LastAccessEntryCount => _lastAccessUtc.Count;
        public int FreshnessTokenEntryCount => _freshnessTokens.Count;

        // Redirect hop cap for the manual follower. Matches
        // ScriptManager.MaxRedirectHops so both outbound paths behave alike.
        private const int MaxRedirectHops = 5;

        // Canonical on-disk extension per accepted MIME. The cache filename
        // must describe the CONTENT the guard validated, never the
        // attacker-supplied URL path: /asset/url re-derives its response
        // Content-Type from this extension, so a URL ending in `.html` used to
        // make a validated PNG go back out as text/html on the overlay origin.
        private static readonly string[] CanonicalExtensions = { ".png", ".jpg", ".gif", ".webp" };

        // Test-only escape hatch: the SSRF guard rejects loopback addresses in
        // production, but UrlImageCacheTests run an in-process HttpListener on
        // 127.0.0.1. Tests flip this to true; production code must never set it.
        // Scheme + MIME + magic-byte + size validation still apply when on.
        public static bool AllowLoopbackForTesting { get; set; } = false;

        private readonly string _cacheDir;
        private readonly HttpClient _http;
        // Only dispose the HttpClient if we created it. An
        // injected one is owned by the caller (typically a shared singleton in
        // tests / future host wiring).
        private readonly bool _ownsHttp;

        public UrlImageCache(string? cacheDir = null, HttpClient? http = null, TimeSpan? ttl = null)
        {
            _cacheDir = cacheDir ?? DefaultCacheDir();
            if (http is null)
            {
                // AllowAutoRedirect stays OFF — DoFetchAsync follows 3xx hops
                // itself so ValidateUrlForOutboundAsync re-runs on every
                // Location. With the framework default (true, up to 50 hops) a
                // single 302 from an attacker-controlled origin walked straight
                // past the pre-fetch SSRF guard into loopback / RFC1918 /
                // 169.254.169.254, which is precisely what
                // ScriptManager.SendWithManualRedirectAsync exists to prevent on
                // the script HTTP path.
                _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                _ownsHttp = true;
            }
            else
            {
                _http = http;
                _ownsHttp = false;
            }
            // TTL is now injectable. Default of 24h preserves the previous
            // behavior for any call site that doesn't pass a value. A non-positive
            // TimeSpan would produce nonsense semantics, so silently fall back.
            if (ttl is TimeSpan t && t > TimeSpan.Zero)
            {
                Ttl = t;
            }
            Directory.CreateDirectory(_cacheDir);
        }

        public void Dispose()
        {
            if (_ownsHttp)
            {
                try { _http.Dispose(); } catch { }
            }
            // Drop any gates still in the dict (process shutdown or test
            // teardown). Deliberately no Sem.Dispose: a fetch still in flight
            // would hit ObjectDisposedException on its WaitAsync/Release, and a
            // SemaphoreSlim whose AvailableWaitHandle is never touched carries
            // no unmanaged resources — clearing the references is sufficient.
            _fetchLocks.Clear();
            _negativeCache.Clear();
            _lastAccessUtc.Clear();
            _freshnessTokens.Clear();
        }

        public string CacheDirectory => _cacheDir;

        public static string DefaultCacheDir() =>
            Paths.LocalAppData(Path.Combine("Hub", "cache"));

        /// <summary>
        /// Resolve a URL to a local cached file path. Fetches if missing or expired.
        /// Returns null on validation or fetch failure <em>only when nothing usable is
        /// cached</em>; callers should fall back gracefully.
        /// <para>
        /// <paramref name="freshnessToken"/> is an opaque client-supplied cache-busting
        /// value (compositor.js's <c>_ts</c> bucket, which flips once per
        /// WebSource.RefreshSeconds window). A token value this cache has not fetched
        /// under yet forces a revalidation against the origin instead of honoring
        /// <see cref="Ttl"/>; passing null keeps the plain TTL behavior.
        /// </para>
        /// <para>
        /// Stale-while-error: a failed revalidation degrades to the plain-<see cref="Ttl"/>
        /// copy rather than to null (see <see cref="TryServeUsableCachedPath"/>), so a
        /// down origin costs the overlay its updates, never its picture.
        /// </para>
        /// </summary>
        public async Task<string?> GetCachedPathAsync(string url, CancellationToken ct = default, string? freshnessToken = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            string sha = Sha256Hex(url);

            // Negative-cache short-circuit. If the previous fetch for
            // this URL failed (any reason) within NegativeCacheTtl, skip the
            // origin entirely — no re-validating, no re-resolving DNS. Cleanup
            // is implicit: a successful fetch drops the entry, and the stamp is
            // checked relative to NegativeCacheTtl on every read so expired
            // entries are ignored.
            //
            // Suppressing the fetch is not the same as suppressing the answer:
            // a TTL-fresh file already validated by an earlier fetch is still
            // perfectly servable, and returning null for it is what turned a
            // transient origin hiccup into a blank on-air widget for a full
            // NegativeCacheTtl (the stamp is shared with callers that pass no
            // freshness token at all).
            if (_negativeCache.TryGetValue(sha, out var negStamp))
            {
                if (DateTime.UtcNow - negStamp < NegativeCacheTtl) return TryServeUsableCachedPath(sha);
                _negativeCache.TryRemove(sha, out _);
            }

            // Defense-in-depth: validate URL even if HUDServer pre-validated.
            var (preOk, preReason) = await ValidateUrlForFetchAsync(url, ct).ConfigureAwait(false);
            if (!preOk)
            {
                GlobalLogger.Log($"UrlImageCache: rejected fetch '{url}' — {preReason}", "UrlImageCache", LogLevel.CriticalError);
                // A transient DNS failure reaches this arm too, so serve the
                // already-validated copy when there is one — reading a local
                // file the guard previously accepted issues no outbound request
                // and so gives up none of the SSRF posture. Only stamp when
                // there is nothing to serve; stamping while a usable file exists
                // would suppress every later read of that file, not just the
                // fetch we are declining.
                string? servable = TryServeUsableCachedPath(sha);
                if (servable is not null) return servable;
                StampNegativeCache(sha);
                return null;
            }

            // Honor the client's cache-busting token before consulting disk.
            // Returns the instant a cached file must be newer than to still
            // count as fresh, or null for the plain Ttl comparison.
            DateTime? forceCutoffUtc = ResolveForcedRefetchCutoff(sha, freshnessToken);

            // Bump last-access on hot reads so the LRU sweep favors
            // recently-used assets. Done outside the fetch lock because the
            // existence check is a cheap stat that doesn't need serialization.
            // The filename extension is whichever canonical form the last
            // successful fetch's validated MIME produced, so the lookup probes
            // the canonical set rather than computing one from the URL.
            string? hit = TryGetCachedPath(sha, forceCutoffUtc);
            if (hit is not null)
            {
                TouchLastAccess(hit);
                return hit;
            }

            // Serialize concurrent fetches of the same URL onto one
            // SemaphoreSlim. The first caller wins the gate and does the HTTP
            // work; the runners-up wait on the semaphore and then re-check the
            // file existence above on retry. This caps the cold-cache origin
            // amplification at exactly 1 fetch per URL no matter how many
            // widgets share the asset.
            FetchLock gate = _fetchLocks.GetOrAdd(sha, _ => new FetchLock());
            Interlocked.Increment(ref gate.RefCount);
            try
            {
                await gate.Sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Re-check after acquiring the lock — another waiter may have
                    // already populated the cache file while we waited.
                    hit = TryGetCachedPath(sha, forceCutoffUtc);
                    if (hit is not null)
                    {
                        TouchLastAccess(hit);
                        return hit;
                    }
                    // Negative cache may also have been populated by the prior
                    // holder of this lock — honor it on the inner re-check too,
                    // with the same serve-what-we-have fallback as the outer one.
                    if (_negativeCache.TryGetValue(sha, out var innerNeg)
                        && DateTime.UtcNow - innerNeg < NegativeCacheTtl)
                    {
                        return TryServeUsableCachedPath(sha);
                    }

                    string? result = await DoFetchAsync(url, sha, ct).ConfigureAwait(false);
                    if (result is null)
                    {
                        // Stale-while-error. A forced revalidation rejects the
                        // on-disk copy on the way in (mtime < forceCutoffUtc), so
                        // reaching here does NOT mean the cache is empty — it
                        // means the origin would not confirm the copy we hold.
                        // Fall back to the plain-Ttl probe: the widget keeps the
                        // frame it had, which is exactly the pre-token behavior,
                        // instead of going blank until the origin recovers.
                        string? servable = TryServeUsableCachedPath(sha);
                        if (servable is not null) return servable;
                        StampNegativeCache(sha);
                    }
                    else
                    {
                        TouchLastAccess(result);
                        // A success invalidates any stamp a racing failure left
                        // behind. Without this, a caller that entered before the
                        // failed attempt stamped could publish a good file and
                        // still leave every reader inside the window looking at
                        // a null for a URL that is now cached and healthy.
                        _negativeCache.TryRemove(sha, out _);
                    }
                    return result;
                }
                finally
                {
                    gate.Sem.Release();
                }
            }
            finally
            {
                // Refcount drop + dict eviction on zero. Avoids leaking one
                // SemaphoreSlim per unique URL ever fetched.
                //
                // Deliberately NO Dispose on eviction: a concurrent caller can
                // GetOrAdd this same gate and increment its refcount between our
                // decrement-to-zero and the TryRemove — disposing here made that
                // caller's WaitAsync throw ObjectDisposedException. A SemaphoreSlim
                // whose AvailableWaitHandle is never touched holds no unmanaged
                // resources, so dropping the dictionary reference is enough; the
                // GC reclaims it once the last in-flight caller releases.
                if (Interlocked.Decrement(ref gate.RefCount) == 0)
                {
                    if (_fetchLocks.TryRemove(sha, out var removed) && !ReferenceEquals(removed, gate))
                    {
                        // Another caller GetOrAdd'd a fresh gate while we were
                        // tearing down — put theirs back. Race is benign; their
                        // RefCount is already incremented.
                        _fetchLocks[sha] = removed;
                    }
                }
            }
        }

        /// <summary>
        /// Performs the network fetch + validation + atomic write. Always called
        /// under the per-URL <see cref="FetchLock"/>. Returns the cached path on
        /// success or null on any failure; the caller is responsible for stamping
        /// the negative cache.
        /// </summary>
        private async Task<string?> DoFetchAsync(string url, string sha, CancellationToken ct)
        {
            // Disambiguate the temp filename per-fetch. The previous
            // `path + ".tmp"` formula meant every concurrent fetch of the same
            // URL wrote into the same file: writer A's stream would race
            // writer B's stream, and the `File.Move(tmp, path, overwrite: true)`
            // for the loser could fail because writer A already moved the temp
            // out from under writer B (FileNotFoundException), or — worse —
            // succeed with a half-written B body atop A's good content.
            //
            // The per-URL semaphore above already guarantees only one fetch
            // is in flight per URL, but unique temp suffixes keep us robust to
            // ANY future change that might allow concurrent writes (e.g. a
            // future per-extension fanout) and to crashed leftovers from a
            // prior process.
            //
            // Rooted at the sha rather than the final path because the final
            // path is not known until the response's MIME has been validated.
            string tmp = Path.Combine(_cacheDir, $"{sha}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            int maxBytes = ConfigManager.Current.MaxAssetSizeBytes > 0
                ? ConfigManager.Current.MaxAssetSizeBytes
                : 5 * 1024 * 1024;

            // Link the caller's CT with a 10s per-fetch
            // deadline so a stalled CDN socket can't hold the HttpClient
            // indefinitely. We don't set HttpClient.Timeout because the client
            // may be a shared singleton injected by the caller (test / future
            // host wiring); the linked CTS is the only way to enforce a per-
            // call cap without mutating shared state.
            using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fetchCts.CancelAfter(PerFetchTimeout);
            var fetchCt = fetchCts.Token;

            try
            {
                // Manual redirect follow — every hop re-validated. A null
                // return means a hop was rejected or the chain overran the hop
                // cap; the helper has already logged the reason.
                using var resp = await SendFollowingValidatedRedirectsAsync(url, fetchCt).ConfigureAwait(false);
                if (resp is null) return null;
                resp.EnsureSuccessStatusCode();

                // Content-Length pre-check — bail before allocating the response stream
                // if the server advertises an oversized body.
                if (resp.Content.Headers.ContentLength is long advertised && advertised > maxBytes)
                {
                    GlobalLogger.Log($"UrlImageCache: rejected '{url}' — Content-Length {advertised} exceeds {maxBytes}", "UrlImageCache", LogLevel.CriticalError);
                    return null;
                }

                // MIME allowlist on Content-Type header.
                string? mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (!IsAllowedImageMime(mediaType))
                {
                    GlobalLogger.Log($"UrlImageCache: rejected '{url}' — disallowed Content-Type '{mediaType}'", "UrlImageCache", LogLevel.CriticalError);
                    return null;
                }

                // Final on-disk name is decided HERE, from the MIME the
                // allowlist just accepted — not from the URL path. HUDServer
                // labels the /asset/url response by reading this extension back,
                // so deriving it from the remote URL let a `…/evil.html` URL
                // serving a valid PNG come back as text/html on the overlay
                // origin (and an extensionless-but-`.svg`-suffixed CDN URL come
                // back as image/svg+xml, which silently renders nothing).
                string path = Path.Combine(_cacheDir, sha + CanonicalExtensionForMime(mediaType));

                await using var src = await resp.Content.ReadAsStreamAsync(fetchCt).ConfigureAwait(false);

                // Peek first bytes for magic-byte sniff. We then write the peeked bytes
                // into the temp file and continue streaming the rest, so we never buffer
                // the full response.
                const int sniffSize = 12;
                byte[] sniff = new byte[sniffSize];
                int sniffRead = 0;
                while (sniffRead < sniffSize)
                {
                    int n = await src.ReadAsync(sniff, sniffRead, sniffSize - sniffRead, fetchCt).ConfigureAwait(false);
                    if (n <= 0) break;
                    sniffRead += n;
                }

                if (!IsMagicByteMatch(sniff, sniffRead, mediaType))
                {
                    GlobalLogger.Log($"UrlImageCache: rejected '{url}' — magic-byte mismatch for '{mediaType}'", "UrlImageCache", LogLevel.CriticalError);
                    return null;
                }

                await using (var dst = File.Create(tmp))
                {
                    long written = 0;
                    if (sniffRead > 0)
                    {
                        await dst.WriteAsync(sniff, 0, sniffRead, fetchCt).ConfigureAwait(false);
                        written += sniffRead;
                        if (written > maxBytes) throw new IOException("response exceeds MaxAssetSizeBytes");
                    }
                    await CopyWithLimitAsync(src, dst, maxBytes, written, fetchCt).ConfigureAwait(false);
                }

                File.Move(tmp, path, overwrite: true);
                // Drop any entry for this same URL under a different canonical
                // extension — reachable when an origin changes the format it
                // serves. Without it the superseded file lingers until the LRU
                // sweep and counts against MaxCacheBytes.
                PruneStaleSiblings(sha, path);
                // Opportunistic LRU sweep after a successful fetch. The
                // sweep runs off-thread (background Task) so the caller doesn't
                // pay the directory-walk latency on the hot fetch path. Single-
                // in-flight gate via Interlocked so concurrent fetches don't
                // each spawn their own sweep.
                TryScheduleLruSweep();
                return path;
            }
            catch (OperationCanceledException) when (fetchCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Per-fetch deadline hit (caller's CT wasn't cancelled). Surface
                // as a Communication-tier log + non-throwing null return so a
                // stalled CDN host doesn't tear down the overlay; the caller
                // falls through to its placeholder image path.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                GlobalLogger.Log(
                    $"UrlImageCache: fetch timed out after {PerFetchTimeout.TotalSeconds:0}s: {url}",
                    "UrlImageCache", LogLevel.Communication);
                return null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                GlobalLogger.Error("UrlImageCache", $"fetch failed: {url}", ex);
                return null;
            }
        }

        /// <summary>
        /// Issues the GET and follows 3xx responses manually, re-running
        /// <see cref="ValidateUrlForOutboundAsync"/> on every <c>Location</c> before
        /// the next hop and capping the chain at <see cref="MaxRedirectHops"/>.
        /// Mirrors <c>ScriptManager.SendWithManualRedirectAsync</c>; the asset proxy
        /// keeps its own copy because it streams the body
        /// (<see cref="HttpCompletionOption.ResponseHeadersRead"/>) and carries no
        /// request headers worth stripping on a cross-host hop.
        /// Returns null when a hop is rejected or the cap is exceeded — the reason is
        /// logged here and the caller treats null as a fetch failure.
        /// </summary>
        private async Task<HttpResponseMessage?> SendFollowingValidatedRedirectsAsync(string url, CancellationToken ct)
        {
            string current = url;
            for (int hop = 0; ; hop++)
            {
                HttpResponseMessage resp;
                var req = new HttpRequestMessage(HttpMethod.Get, current);
                try
                {
                    resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                finally
                {
                    req.Dispose();
                }

                if (!IsRedirectStatus(resp.StatusCode) || resp.Headers.Location is null)
                    return resp;

                if (hop >= MaxRedirectHops)
                {
                    GlobalLogger.Log(
                        $"UrlImageCache: rejected '{url}' — redirect chain exceeded {MaxRedirectHops} hops",
                        "UrlImageCache", LogLevel.CriticalError);
                    resp.Dispose();
                    return null;
                }

                Uri target = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(new Uri(current), resp.Headers.Location);
                resp.Dispose();

                // The whole point of the manual loop: a 30x pointing at
                // 127.0.0.1 / 10.0.0.0/8 / 169.254.169.254 must be refused
                // exactly the way the initial URL would have been.
                var (hopOk, hopReason) = await ValidateUrlForOutboundAsync(target.AbsoluteUri, ct).ConfigureAwait(false);
                if (!hopOk)
                {
                    GlobalLogger.Log(
                        $"UrlImageCache: rejected redirect '{current}' → '{target}' — {hopReason}",
                        "UrlImageCache", LogLevel.CriticalError);
                    return null;
                }

                current = target.AbsoluteUri;
            }
        }

        private static bool IsRedirectStatus(HttpStatusCode code) => code switch
        {
            HttpStatusCode.MovedPermanently  => true, // 301
            HttpStatusCode.Found             => true, // 302
            HttpStatusCode.SeeOther          => true, // 303
            HttpStatusCode.TemporaryRedirect => true, // 307
            HttpStatusCode.PermanentRedirect => true, // 308
            _ => false
        };

        public long ClearCache()
        {
            long total = 0;
            if (!Directory.Exists(_cacheDir)) return 0;
            foreach (var f in Directory.EnumerateFiles(_cacheDir))
            {
                try { var len = new FileInfo(f).Length; File.Delete(f); total += len; } catch { }
            }
            // Drop the in-process access-time map alongside the
            // files. Otherwise the next fetch would inherit stamps for paths
            // that no longer exist on disk.
            _lastAccessUtc.Clear();
            // Flush the negative cache too. ClearCache is the only
            // user-initiated "forget everything" entry point, and a stuck
            // negative entry would defeat the point.
            _negativeCache.Clear();
            // Same reasoning for the freshness tokens: with the files gone
            // there is nothing left for a remembered token to keep fresh, and a
            // stale entry would suppress the next forced revalidation.
            _freshnessTokens.Clear();
            return total;
        }

        // ── LRU sweep ────────────────────────────────────────────────

        /// <summary>
        /// Schedule a background LRU sweep iff the cap is enabled and another
        /// sweep isn't already in flight. Non-blocking; the actual walk runs
        /// on the thread pool so the hot fetch path returns immediately.
        /// </summary>
        private void TryScheduleLruSweep()
        {
            if (MaxCacheBytes <= 0) return;
            if (Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0) return;
            _ = Task.Run(() =>
            {
                try { RunLruSweep(); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("UrlImageCache", "LRU sweep faulted", ex);
                }
                finally { Interlocked.Exchange(ref _sweepRunning, 0); }
            });
        }

        /// <summary>
        /// Walk the cache directory, compute total size, and if it exceeds
        /// <see cref="MaxCacheBytes"/> delete oldest-LastWriteTime-first
        /// entries until the total is back under the cap. Caller pays the
        /// IO cost — invoke from a background task.
        ///
        /// Public so tests / health-UI can trigger an immediate sweep without
        /// the rate-limit gate; the internal call site goes through
        /// <see cref="TryScheduleLruSweep"/> instead so concurrent fetches
        /// don't pile up sweeps.
        /// </summary>
        public long RunLruSweep()
        {
            if (MaxCacheBytes <= 0) return 0;
            if (!Directory.Exists(_cacheDir)) return 0;

            // Snapshot the directory once; FileInfo lookups can race a
            // concurrent fetch's File.Move, so we tolerate Length=0 / file-
            // missing exceptions during the walk by skipping the entry.
            //
            // Score each entry by its in-process last-access stamp
            // when we have one (TryGet hits update _lastAccessUtc), and fall
            // back to mtime when we don't. Without this fallback an entry
            // fetched in a prior process run would have no stamp and would
            // look infinitely old next time around.
            var entries = new List<(string Path, long Length, DateTime LastAccess)>();
            long total = 0;
            foreach (var path in Directory.EnumerateFiles(_cacheDir))
            {
                // Skip in-flight temp files — those belong to an active fetch
                // and the rename happens atomically; deleting them mid-fetch
                // would surface as a spurious "fetch failed" log.
                if (path.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                try
                {
                    var info = new FileInfo(path);
                    DateTime score = _lastAccessUtc.TryGetValue(path, out var stamp)
                        ? stamp
                        : info.LastWriteTimeUtc;
                    entries.Add((path, info.Length, score));
                    total += info.Length;
                }
                catch
                {
                    // File deleted between EnumerateFiles and FileInfo —
                    // safe to skip.
                }
            }

            long cap = MaxCacheBytes;
            if (total <= cap) return 0;

            // Sort ascending by last-access — oldest-touched first. This is a
            // true LRU rather than an LRC (least-recently-created): a freshly
            // fetched but unused image gets evicted before a stale-mtime hot
            // image, which is what callers actually want.
            entries.Sort((a, b) => a.LastAccess.CompareTo(b.LastAccess));

            long freed = 0;
            foreach (var entry in entries)
            {
                if (total <= cap) break;
                try
                {
                    File.Delete(entry.Path);
                    _lastAccessUtc.TryRemove(entry.Path, out _);
                    // The file IS the referent of the sha-keyed bookkeeping:
                    // with it gone the freshness token has nothing left to
                    // keep fresh (the next `_ts` bucket takes the cold path
                    // and refetches anyway) and a negative stamp no longer
                    // guards a stale-while-error fallback (worst case the
                    // next request costs one origin attempt and re-stamps).
                    // Dropping both here keeps the maps shrinking with the
                    // disk instead of outliving every file they described.
                    // Cache filenames are `<sha><canonical ext>`, so the
                    // name sans extension is exactly the map key.
                    string sha = Path.GetFileNameWithoutExtension(entry.Path);
                    _freshnessTokens.TryRemove(sha, out _);
                    _negativeCache.TryRemove(sha, out _);
                    total -= entry.Length;
                    freed += entry.Length;
                }
                catch
                {
                    // Race with a concurrent fetch / Windows file lock — skip.
                }
            }

            return freed;
        }

        public long GetCacheSizeBytes()
        {
            if (!Directory.Exists(_cacheDir)) return 0;
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(_cacheDir))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }

        // ── SSRF + MIME validation ────────────────────────────────────────

        /// <summary>
        /// Pre-fetch URL validator. Returns <c>(true, "")</c> on success, <c>(false, reason)</c> on rejection.
        /// Instance wrapper preserves the existing API; delegates to the static
        /// <see cref="ValidateUrlForOutboundAsync"/> peer so script-driven http.* /
        /// ai.* paths in <c>ScriptManager</c> can share the same validator without
        /// holding a UrlImageCache reference.
        /// </summary>
        public Task<(bool ok, string reason)> ValidateUrlForFetchAsync(string url, CancellationToken ct)
            => ValidateUrlForOutboundAsync(url, ct);

        /// <summary>
        /// Static peer of <see cref="ValidateUrlForFetchAsync"/>. Honors the
        /// shared <see cref="AllowLoopbackForTesting"/> gate so test fixtures
        /// can keep routing through 127.0.0.1 while production calls still
        /// reject loopback. The validator carries no instance state — the
        /// blocked-address rules are class-static and DNS lookup goes through
        /// the system resolver — so making it static lets the script-driven
        /// SendWithManualRedirectAsync / SendStreamingWithManualRedirectAsync
        /// gate every initial URL + redirect target.
        ///
        /// <para>
        /// <paramref name="allowLoopback"/> opens a per-call escape hatch
        /// for callers wired to an explicitly-configured local service
        /// (currently only ai.stream_text's Ollama arm, whose default URL is
        /// <c>http://localhost:11434</c>). Every other ban — RFC1918, link-local
        /// /16 (cloud metadata), CGNAT, multicast, ULA — still applies; the
        /// flag only relaxes the loopback check. Tests instead flip
        /// <see cref="AllowLoopbackForTesting"/>, which is class-wide.
        /// </para>
        /// </summary>
        public static async Task<(bool ok, string reason)> ValidateUrlForOutboundAsync(string url, CancellationToken ct, bool allowLoopback = false)
        {
            if (string.IsNullOrWhiteSpace(url)) return (false, "empty url");

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return (false, "not an absolute URI");

            // Scheme allowlist — rejects file://, gopher://, ftp://, ws://, etc.
            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https")
                return (false, $"disallowed scheme '{scheme}'");

            // 1) If the host parses directly as an IP literal, validate that IP without DNS.
            if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
            {
                if (IsBlockedAddress(literal, allowLoopback))
                    return (false, $"blocked literal IP {literal}");
            }
            else
            {
                // 2) Otherwise resolve via DNS and reject if ANY result is blocked
                //    (rebinding-style attacks may use multiple A records).
                IPAddress[] resolved;
                try
                {
                    resolved = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return (false, $"DNS resolve failed: {ex.Message}");
                }
                if (resolved == null || resolved.Length == 0)
                    return (false, "no DNS records");
                foreach (var ip in resolved)
                {
                    if (IsBlockedAddress(ip, allowLoopback))
                        return (false, $"blocked resolved IP {ip} for host {uri.Host}");
                }
            }

            return (true, "");
        }

        private static bool IsBlockedAddress(IPAddress ip, bool allowLoopback = false)
        {
            // Loopback: 127.0.0.0/8 + ::1.
            // Tests set AllowLoopbackForTesting to permit 127.0.0.1 fixtures;
            // every other class of blocked address (private/link-local/ULA/etc.)
            // still applies, and scheme/MIME/size/magic-byte checks are unaffected.
            // allowLoopback is a per-call escape (e.g. Ollama on
            // localhost) — opens only the loopback ban, never the others.
            if (IPAddress.IsLoopback(ip)) return !(AllowLoopbackForTesting || allowLoopback);

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                if (b.Length != 4) return true;
                // 0.0.0.0/8 — "this network" / unspecified
                if (b[0] == 0) return true;
                // RFC1918: 10.0.0.0/8
                if (b[0] == 10) return true;
                // RFC1918: 172.16.0.0/12
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                // RFC1918: 192.168.0.0/16
                if (b[0] == 192 && b[1] == 168) return true;
                // Link-local: 169.254.0.0/16  (covers cloud metadata 169.254.169.254)
                if (b[0] == 169 && b[1] == 254) return true;
                // CGNAT: 100.64.0.0/10
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
                // Multicast: 224.0.0.0/4
                if (b[0] >= 224 && b[0] <= 239) return true;
                // Reserved: 240.0.0.0/4 (incl. 255.255.255.255 broadcast)
                if (b[0] >= 240) return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal) return true;     // fe80::/10
                if (ip.IsIPv6SiteLocal) return true;     // fec0::/10 (deprecated but block)
                if (ip.IsIPv6Multicast) return true;     // ff00::/8

                byte[] b = ip.GetAddressBytes();
                if (b.Length != 16) return true;
                // Unique-local: fc00::/7  (Docker/Tailscale/internal)
                if ((b[0] & 0xFE) == 0xFC) return true;
                // IPv4-mapped: ::ffff:0:0/96  → unwrap and re-check.
                if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0
                 && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0
                 && b[8] == 0 && b[9] == 0 && b[10] == 0xFF && b[11] == 0xFF)
                {
                    var v4 = new IPAddress(new byte[] { b[12], b[13], b[14], b[15] });
                    return IsBlockedAddress(v4);
                }
                // Unspecified ::
                bool allZero = true;
                for (int i = 0; i < 16; i++) if (b[i] != 0) { allZero = false; break; }
                if (allZero) return true;
            }

            return false;
        }

        private static bool IsAllowedImageMime(string? media)
        {
            if (string.IsNullOrEmpty(media)) return false;
            // image/svg+xml dropped from the allowlist. SVG was safe
            // by accident — the only consumer (compositor.js) loaded cached
            // images via <img>, which suppresses <script> inside SVG. Any
            // future fetch+blob+iframe path would have inherited an XSS
            // sink. Raster-only is the safer floor; reintroduce SVG only
            // alongside an explicit sanitiser pass.
            return media == "image/png"
                || media == "image/jpeg"
                || media == "image/gif"
                || media == "image/webp";
        }

        /// <summary>
        /// Returns true if the first <paramref name="len"/> bytes of <paramref name="head"/>
        /// match a known image magic header consistent with the advertised <paramref name="mediaType"/>.
        /// </summary>
        private static bool IsMagicByteMatch(byte[] head, int len, string? mediaType)
        {
            if (head == null || len <= 0) return false;
            switch (mediaType)
            {
                case "image/png":
                    return len >= 8
                        && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                        && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A;
                case "image/jpeg":
                    return len >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
                case "image/gif":
                    return len >= 4 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38;
                case "image/webp":
                    return len >= 12
                        && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46  // RIFF
                        && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50; // WEBP
                // SVG magic-byte arm removed alongside the MIME allowlist entry.
                default:
                    return false;
            }
        }

        /// <summary>
        /// Streams <paramref name="src"/> into <paramref name="dst"/>, throwing IOException
        /// if total bytes written would exceed <paramref name="maxBytes"/>.
        /// </summary>
        private static async Task CopyWithLimitAsync(Stream src, Stream dst, long maxBytes, long initialWritten, CancellationToken ct)
        {
            byte[] buf = new byte[81920];
            long total = initialWritten;
            int read;
            while ((read = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new IOException($"response exceeds MaxAssetSizeBytes ({maxBytes})");
                await dst.WriteAsync(buf, 0, read, ct).ConfigureAwait(false);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string Sha256Hex(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Canonical cache-file extension for an accepted image MIME. Replaces the
        /// old URL-path-derived guess: the extension is the only record of what the
        /// guard validated, and <c>/asset/url</c> turns it back into the response
        /// Content-Type.
        /// </summary>
        private static string CanonicalExtensionForMime(string? media) => media switch
        {
            "image/png"  => ".png",
            "image/jpeg" => ".jpg",
            "image/gif"  => ".gif",
            "image/webp" => ".webp",
            // Unreachable — IsAllowedImageMime gates every caller. Kept total so a
            // future allowlist entry added without a matching arm here fails closed
            // on the MimeForCachedFile lookup instead of inheriting a wrong type.
            _            => ".bin",
        };

        /// <summary>
        /// The validated image MIME for a file this cache produced, or null when the
        /// path is not one of the canonical forms. <c>HUDServer.ServeCachedUrlAsync</c>
        /// labels the proxied body with this instead of re-deriving a type from the
        /// remote URL's path extension.
        /// </summary>
        public static string? MimeForCachedFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png"  => "image/png",
            ".jpg"  => "image/jpeg",
            ".gif"  => "image/gif",
            ".webp" => "image/webp",
            _       => null,
        };

        /// <summary>
        /// Probe the canonical-extension set for a usable cache entry for
        /// <paramref name="sha"/>. With <paramref name="mustBeNewerThanUtc"/> null the
        /// entry counts as fresh while its age is under <see cref="Ttl"/>; with a value
        /// (a forced revalidation) only a file written since that instant counts, so a
        /// concurrent caller that already refetched still produces a hit.
        /// </summary>
        private string? TryGetCachedPath(string sha, DateTime? mustBeNewerThanUtc)
        {
            foreach (var ext in CanonicalExtensions)
            {
                string p = Path.Combine(_cacheDir, sha + ext);
                if (!File.Exists(p)) continue;
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(p); }
                catch { continue; }

                if (mustBeNewerThanUtc is DateTime cutoff)
                {
                    if (mtime >= cutoff) return p;
                    continue;
                }
                if (DateTime.UtcNow - mtime < Ttl) return p;
            }
            return null;
        }

        /// <summary>
        /// Stale-while-error probe: the plain-<see cref="Ttl"/> cache entry for
        /// <paramref name="sha"/>, or null when there is none. Touches the LRU
        /// access stamp exactly like an ordinary hit, because serving it is one.
        /// <para>
        /// Enforces the invariant every failure arm of <see cref="GetCachedPathAsync"/>
        /// leans on: while a TTL-fresh file the guard already validated sits in the
        /// cache directory, this class does not answer null. A failed forced
        /// revalidation degrades to that file — i.e. to the behavior the cache had
        /// before it honored the <c>_ts</c> token — rather than to nothing.
        /// <c>HUDServer</c> renders a null as a 502 and <c>compositor.js</c> renders
        /// that as an empty widget, so on an on-air overlay "stopped updating" is
        /// the strictly cheaper failure mode against a flaky origin.
        /// </para>
        /// </summary>
        private string? TryServeUsableCachedPath(string sha)
        {
            string? hit = TryGetCachedPath(sha, null);
            if (hit is not null) TouchLastAccess(hit);
            return hit;
        }

        /// <summary>
        /// Delete cache entries for the same URL under a different canonical
        /// extension, keeping <paramref name="keep"/>.
        /// </summary>
        private void PruneStaleSiblings(string sha, string keep)
        {
            foreach (var ext in CanonicalExtensions)
            {
                string p = Path.Combine(_cacheDir, sha + ext);
                if (string.Equals(p, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.Exists(p))
                    {
                        File.Delete(p);
                        _lastAccessUtc.TryRemove(p, out _);
                    }
                }
                catch
                {
                    // Concurrent read / Windows file lock — the LRU sweep will
                    // reclaim it later.
                }
            }
        }

        /// <summary>
        /// Decide whether a client-supplied cache-busting token demands a fresh
        /// origin fetch. Returns the cutoff instant a cached file must be newer than,
        /// or null to fall back on the plain <see cref="Ttl"/> comparison.
        /// A token value not seen before (including the first one for a URL) forces;
        /// an unchanged token never does; and a changed token is ignored when the
        /// previous forced refetch was under <see cref="MinForcedRefetchInterval"/>
        /// ago, so a page spinning the token cannot relay a request flood at the
        /// origin.
        /// </summary>
        private DateTime? ResolveForcedRefetchCutoff(string sha, string? freshnessToken)
        {
            if (string.IsNullOrEmpty(freshnessToken)) return null;
            // Bound what we are willing to remember per URL — the token comes
            // straight off the query string.
            if (freshnessToken.Length > MaxFreshnessTokenLength) return null;

            DateTime now = DateTime.UtcNow;
            if (_freshnessTokens.TryGetValue(sha, out var prev))
            {
                if (string.Equals(prev.Token, freshnessToken, StringComparison.Ordinal)) return null;
                if (now - prev.ForcedAtUtc < MinForcedRefetchInterval)
                {
                    // Record the new token but keep the old forced-at stamp, so the
                    // rate limit is measured from the last actual origin hit.
                    StoreFreshnessToken(sha, freshnessToken, prev.ForcedAtUtc);
                    return null;
                }
            }
            StoreFreshnessToken(sha, freshnessToken, now);
            return now;
        }

        // ── Bookkeeping bounds ────────────────────────────────────────────────
        // The three per-URL maps GAIN entries only through the helpers below,
        // so every insert path shares the MaxBookkeepingEntries enforcement
        // (removals — the LRU sweep, ClearCache, Dispose — stay where they
        // are). See the property's comment block for why eviction is always
        // safe for each map.

        /// <summary>
        /// Record a hot-read access stamp and keep the map under
        /// <see cref="MaxBookkeepingEntries"/>. Losing a stamp to the trim is
        /// benign: <see cref="RunLruSweep"/> already falls back to file mtime
        /// for any path it has no stamp for.
        /// </summary>
        private void TouchLastAccess(string path)
        {
            _lastAccessUtc[path] = DateTime.UtcNow;
            EnforceBookkeepingCap(_lastAccessUtc, static stamp => stamp, ref _lastAccessTrimRunning);
        }

        /// <summary>
        /// Lay down a negative stamp and keep the map under
        /// <see cref="MaxBookkeepingEntries"/>. Oldest-stamp-first eviction
        /// takes already-expired entries before live ones; evicting a live
        /// stamp only means the next request for that URL re-validates against
        /// the origin (and re-stamps on failure) instead of being suppressed —
        /// one extra origin attempt, never a wrong answer.
        /// </summary>
        private void StampNegativeCache(string sha)
        {
            _negativeCache[sha] = DateTime.UtcNow;
            EnforceBookkeepingCap(_negativeCache, static stamp => stamp, ref _negativeCacheTrimRunning);
        }

        /// <summary>
        /// Remember the client's freshness token and keep the map under
        /// <see cref="MaxBookkeepingEntries"/>. An evicted entry makes the next
        /// token for that URL count as unseen — exactly one forced refetch,
        /// after which the re-created entry restores both the token dedupe and
        /// the <see cref="MinForcedRefetchInterval"/> throttle that live in it.
        /// </summary>
        private void StoreFreshnessToken(string sha, string token, DateTime forcedAtUtc)
        {
            _freshnessTokens[sha] = (token, forcedAtUtc);
            EnforceBookkeepingCap(_freshnessTokens, static v => v.ForcedAtUtc, ref _freshnessTokenTrimRunning);
        }

        /// <summary>
        /// Bound one bookkeeping map to <see cref="MaxBookkeepingEntries"/>,
        /// evicting oldest-by-<paramref name="stampOf"/> first down to a 7/8
        /// watermark. Runs inline on the inserting caller — a snapshot + sort
        /// over a few thousand entries is microseconds, cheaper than the file
        /// stat the hot path just paid — and is gated by an Interlocked flag
        /// per map (the <c>_sweepRunning</c> pattern) so concurrent breachers
        /// don't stack passes: the CAS winner trims, losers skip. The maps stay
        /// ConcurrentDictionary, so reads everywhere remain lock-free and no
        /// lock ordering is introduced anywhere on the fetch path.
        /// </summary>
        private void EnforceBookkeepingCap<TValue>(
            ConcurrentDictionary<string, TValue> map,
            Func<TValue, DateTime> stampOf,
            ref int trimRunning)
        {
            int cap = MaxBookkeepingEntries;
            if (cap <= 0) return;
            if (map.Count <= cap) return;
            if (Interlocked.CompareExchange(ref trimRunning, 1, 0) != 0) return;
            try
            {
                // Re-check under the gate — the previous holder may already
                // have brought the map back under the cap.
                if (map.Count <= cap) return;

                // Trim to a lower watermark (cap - cap/8) so a map saturated
                // by rotating URLs re-pays the snapshot once per ~cap/8
                // inserts instead of on every single one.
                int target = cap - Math.Max(1, cap / 8);
                var snapshot = new List<KeyValuePair<string, TValue>>(map.Count);
                foreach (var kv in map) snapshot.Add(kv);
                snapshot.Sort((a, b) => stampOf(a.Value).CompareTo(stampOf(b.Value)));

                int toRemove = snapshot.Count - target;
                for (int i = 0; i < snapshot.Count && toRemove > 0; i++)
                {
                    // KeyValuePair overload: removes only while the value still
                    // matches the snapshot, so an entry a concurrent caller
                    // refreshed mid-trim keeps its newer stamp instead of
                    // being evicted out from under that caller.
                    if (map.TryRemove(snapshot[i])) toRemove--;
                }
            }
            finally
            {
                Interlocked.Exchange(ref trimRunning, 0);
            }
        }
    }
}
