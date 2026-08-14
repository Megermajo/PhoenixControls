using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// The suite's ONE viewer-presence sampler: a always-on background loop that asks
    /// Streamer.bot who is currently in chat, and turns that answer into three things
    /// nothing else in the Hub had before —
    ///
    /// <list type="number">
    /// <item><b>A shared snapshot.</b> Every consumer that used to open its own
    /// <c>GetActiveViewers</c> round-trip (the Loyalty payout tick, the Giveaway draw's
    /// subscriber re-check, the <c>twitch.get_viewers</c> script command) now reads one
    /// sample. Three fetches milliseconds apart returned three different answers to
    /// "who is watching"; one sample cannot disagree with itself.</item>
    ///
    /// <item><b>A platform-role cache.</b> The sweep carries each viewer's platform
    /// standing (subscriber / VIP / moderator). That is what lets a role question about
    /// an ARBITRARY login — <c>User.GetGroups("someone")</c>, a group gate evaluated
    /// outside a chat message — be answered from platform truth instead of from a
    /// hand-maintained member list.</item>
    ///
    /// <item><b>Passive watch time.</b> Minutes accrue into the OPEN <c>WatchTime</c>
    /// table for as long as a viewer is present, with no tool switched on. Before this,
    /// watch time was a passenger on the Loyalty tool's payout tick AND gated behind the
    /// Ranks tool being enabled, so a default install recorded nothing at all.</item>
    /// </list>
    ///
    /// <para><b>It is a data source, not a feature.</b> There is no master toggle to
    /// forget, no pre-build page that owns it, and nothing user-visible fires from it.
    /// It samples, it records, and other surfaces read what it recorded.</para>
    ///
    /// <para><b>Why the fetch is a seam.</b> The pillar rule says only the Hub's
    /// Streamer.bot plumbing talks to Streamer.bot; this service holds no socket. The
    /// single request/parse implementation lives in
    /// <c>ScriptManager.ViewerPresence.cs</c> and is handed here as
    /// <see cref="FetchProvider"/>, exactly like Loyalty's own seams.</para>
    /// </summary>
    public sealed class ViewerPresenceService
    {
        // ── Singleton ───────────────────────────────────────────────────────
        private static readonly Lazy<ViewerPresenceService> _instance =
            new(() => new ViewerPresenceService(null));
        public static ViewerPresenceService Instance => _instance.Value;

        /// <summary>Test ctor — an injected databank keeps the suite off the live file.</summary>
        internal ViewerPresenceService(DB? db) { _injectedDb = db; }

        private readonly DB? _injectedDb;
        // Resolved LIVE per call rather than pinned at construction: the singleton is
        // built before DB.Instance necessarily exists, and pinning a disposed instance
        // is the documented mechanism behind this suite's DB-singleton test flakes.
        private DB Db => _injectedDb ?? DB.Instance;

        // ── Seams (wired by ScriptManager; see ScriptManager.ViewerPresence.cs) ─
        /// <summary>The ONE <c>GetActiveViewers</c> round-trip. NULL means the sweep
        /// FAILED (disconnected mid-flight, timeout, malformed payload) — the caller
        /// keeps its previous sample. An EMPTY list is a healthy answer: the platform
        /// genuinely reports nobody watching. Collapsing the two was the bug that let
        /// one stalled round-trip blank the published sample and silently discard an
        /// interval of watch time.</summary>
        public Func<Task<IReadOnlyList<ActiveViewer>?>>? FetchProvider { get; set; }

        /// <summary>Live Hub "Bot Accounts" list (the comma-separated
        /// <c>AppConfig.BotUsername</c>), read per sample so a Settings edit lands
        /// without a restart. Excluded from watch-time accrual.</summary>
        public Func<IReadOnlyCollection<string>>? BotAccountsProvider { get; set; }

        /// <summary>Connectivity probe — swapped by tests; production reads WS.</summary>
        internal Func<bool> IsConnectedProbe = static () => WS.Instance.IsConnected;

        /// <summary>Monotonic clock seam so the accrual maths is testable without
        /// sleeping. Production is a process-wide Stopwatch.</summary>
        internal Func<long> NowMs = static () => _clock.ElapsedMilliseconds;
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>False in tests: skip every databank write.</summary>
        internal bool PersistAccrual = true;

        // ── Events ──────────────────────────────────────────────────────────
        /// <summary>Raised after every completed sample (including one that found
        /// nobody). UI subscribers MUST marshal.</summary>
        public event EventHandler? PresenceSampled;

        /// <summary>Raised after watch minutes were actually written, carrying the
        /// logins credited. The Ranks ladder rides this instead of owning the
        /// accrual — evaluating a ladder is a tool's job, recording the hours is not.
        /// </summary>
        public event EventHandler<WatchTimeCreditedEventArgs>? WatchTimeCredited;

        private void RaiseSampled()
            => SafeEvent.Raise(PresenceSampled, this, EventArgs.Empty, "ViewerPresenceService", "PresenceSampled");

        // ── Current sample ──────────────────────────────────────────────────
        private volatile ViewerPresenceSample _sample = ViewerPresenceSample.Empty;

        /// <summary>The most recent sample. <see cref="ViewerPresenceSample.Empty"/>
        /// until the first one lands — never null, and its age is what tells a caller
        /// whether to trust it.</summary>
        public ViewerPresenceSample Current => _sample;

        // ── Platform-role cache ─────────────────────────────────────────────
        // Keyed by lowercased login. An entry is a RECORDED OBSERVATION, not a
        // subscription: it says "the platform reported these flags at this instant".
        // Callers that care about staleness read Age and decide; callers that do not
        // get the last thing the platform actually said.
        private readonly ConcurrentDictionary<string, PlatformRoleRecord> _roles =
            new(StringComparer.OrdinalIgnoreCase);

        // A viewer who has not appeared in a sample for this long is forgotten, so the
        // cache tracks the channel's active population rather than growing for the
        // lifetime of the process. Twelve hours comfortably spans one stream plus the
        // gap either side of it.
        private const long RoleRetentionMs = 12L * 60 * 60 * 1000;

        // ── Watch-time accrual ──────────────────────────────────────────────
        // Sub-minute remainders live here and ONLY here: the WatchTime table stores
        // whole minutes (it is an OPEN user table a streamer may read with db.* and
        // hand-edit), so a 30-60s sample flushes the whole minutes it completed and
        // carries the rest forward. The carried remainder is at most 59 seconds per
        // viewer and is lost if Hub dies — which is the correct trade against writing
        // a second unit into a table other people's scripts already read as minutes.
        private readonly object _accrualGate = new();
        private readonly Dictionary<string, long> _pendingSeconds = new(StringComparer.OrdinalIgnoreCase);
        private long _lastSampleMs = -1;

        // A whole-table mirror so a group's watch-hour rule can be evaluated on the
        // chat hot path without touching SQLite. Written on every credit, and fully
        // reloaded on a slow cadence so a db.* script that edits the table by hand is
        // eventually reflected rather than silently ignored.
        private volatile IReadOnlyDictionary<string, long> _watchMinutes =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _lastMirrorReloadMs = -1;
        private const long MirrorReloadIntervalMs = 5 * 60 * 1000;

        /// <summary>True once the mirror has been loaded at least once. A rule that
        /// reads watch hours before the first load must not silently answer "nobody
        /// qualifies" — callers use this to stay passthrough until the data is real.
        /// </summary>
        public bool WatchTimeReady { get; private set; }

        // ── Live gate ───────────────────────────────────────────────────────
        // Defaults NOT live, matching the User-Management / Loyalty siblings: with
        // "only while live" ticked (the default) nothing accrues until a real go-live
        // edge arrives, so a Hub left running overnight does not hand everybody hours.
        private volatile bool _streamLive;

        /// <summary>Wired from the live-platform edge tracker beside the Timer /
        /// Scheduling / Loyalty / User-Management calls.</summary>
        public void SetStreamLive(bool live)
        {
            bool wasLive = _streamLive;
            _streamLive = live;
            // Going offline drops the carried remainders: they measure presence inside
            // a stream, and carrying them across the gap would credit the first sample
            // of the next stream with time nobody watched.
            if (wasLive && !live)
            {
                lock (_accrualGate)
                {
                    _pendingSeconds.Clear();
                    _lastSampleMs = -1;
                }
            }
        }

        // ── Loop lifecycle ──────────────────────────────────────────────────
        private readonly object _loopGate = new();
        private bool _started;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        /// <summary>Starts the sampling loop. Idempotent. The loop lives for the
        /// process; what it DOES each pass depends on config and connectivity.</summary>
        public void Start()
        {
            lock (_loopGate)
            {
                if (_started) return;
                _started = true;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _loop = Task.Run(() => LoopAsync(ct));
            }
            GlobalLogger.Log(
                $"Viewer presence sampling started (every {PollSeconds}s).",
                "ViewerPresenceService", LogLevel.System);
        }

        /// <summary>Cancels and drains the loop, then flushes whatever whole minutes
        /// the carried remainders had already earned — a clean shutdown should not
        /// throw away time that was genuinely watched.</summary>
        public async Task ShutdownAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_loopGate)
            {
                cts = _cts; loop = _loop;
                _started = false; _cts = null; _loop = null;
            }
            try { cts?.Cancel(); } catch { /* best-effort */ }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { GlobalLogger.Error("ViewerPresenceService", "sampling loop drain failed", ex); }
            }
            cts?.Dispose();
            try { await FlushWholeMinutesAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("ViewerPresenceService", "final watch-time flush failed", ex); }
        }

        /// <summary>Sampling cadence in seconds, read live from AppConfig and clamped to
        /// a band the Streamer.bot socket can carry comfortably.</summary>
        public int PollSeconds
        {
            get
            {
                int v = ConfigManager.Current?.ViewerPresencePollSeconds ?? 60;
                return Math.Clamp(v, MinPollSeconds, MaxPollSeconds);
            }
        }
        internal const int MinPollSeconds = 15;
        internal const int MaxPollSeconds = 300;

        private static bool TrackingEnabled => ConfigManager.Current?.WatchTimeTrackingEnabled ?? true;
        private static bool OnlyWhenLive => ConfigManager.Current?.WatchTimeOnlyWhenLive ?? true;

        /// <summary>Whether this pass should turn presence into minutes.</summary>
        public bool AccrualWanted => TrackingEnabled && (!OnlyWhenLive || _streamLive);

        private async Task LoopAsync(CancellationToken ct)
        {
            // The open WatchTime table must exist even on an install where the Ranks
            // tool never initialises — accrual is a background data source now, not a
            // passenger on that tool. CREATE TABLE IF NOT EXISTS, so calling it here
            // as well as from RanksService is free and order-independent.
            if (PersistAccrual)
            {
                try { await Db.EnsureRanksDataTablesAsync().ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("ViewerPresenceService", "watch-time table ensure failed", ex); }
            }

            // Load the mirror before the first sample so a rule evaluated in the first
            // minute of a session reads real hours rather than zeroes.
            try { await ReloadWatchMinutesAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("ViewerPresenceService", "initial watch-time load failed", ex); }

            while (!ct.IsCancellationRequested)
            {
                int seconds = PollSeconds;
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                // A background thread must never take the process down; the whole pass
                // is guarded. A failed pass (provider null / exception) leaves the
                // previous sample AND the accrual anchor in place — SampleOnceAsync
                // returns early on the null contract, and the 2× cadence clamp in
                // AccrueAsync bounds whatever gap the next good sweep then credits.
                try { await SampleOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("ViewerPresenceService", "presence sample failed", ex); }
            }
        }

        /// <summary>One full pass: fetch, publish, cache roles, accrue. Public so the
        /// tests (and a future manual refresh) can drive it deterministically.</summary>
        public async Task SampleOnceAsync()
        {
            if (!IsConnectedProbe())
            {
                // Disconnected is not "nobody is watching". Leave the last sample and
                // its age alone, and drop the accrual anchor so the reconnect does not
                // credit the whole outage as watch time.
                lock (_accrualGate) _lastSampleMs = -1;
                return;
            }

            IReadOnlyList<ActiveViewer>? viewers = await FetchAsync().ConfigureAwait(false);
            if (viewers is null)
            {
                // Failed round-trip while CONNECTED: keep the previous sample (its
                // age keeps growing, so on-demand readers re-fetch honestly) and keep
                // the accrual anchor — the next good sweep credits the gap, bounded
                // by the 2× cadence clamp. Deliberately asymmetric with the
                // disconnect branch above, which DROPS the anchor: a dead socket is
                // an outage of unknown length, one stalled request is a blip.
                // The mirror reload still runs — it is a databank read with no
                // dependency on the sweep, and skipping it would freeze the panel's
                // watch-time view for as long as the sweeps keep failing.
                await MaybeReloadMirrorAsync(NowMs()).ConfigureAwait(false);
                return;
            }
            long now = NowMs();

            _sample = new ViewerPresenceSample(viewers, DateTimeOffset.UtcNow);
            RecordRoles(viewers, now);

            if (viewers.Count > 0 && AccrualWanted)
                await AccrueAsync(viewers, now).ConfigureAwait(false);
            else
                lock (_accrualGate) _lastSampleMs = now;

            await MaybeReloadMirrorAsync(now).ConfigureAwait(false);
            RaiseSampled();
        }

        private async Task<IReadOnlyList<ActiveViewer>?> FetchAsync()
        {
            var provider = FetchProvider;
            if (provider is null) return null;    // no seam wired = nothing to say
            try { return await provider().ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("ViewerPresenceService", "FetchProvider failed", ex);
                return null;
            }
        }

        // ── Shared read for the on-demand consumers ─────────────────────────

        /// <summary>
        /// The active-viewer list for a consumer that wants one "now": the cached
        /// sample when it is younger than <paramref name="maxAge"/>, otherwise a fresh
        /// fetch that also refreshes the cache. Concurrent callers that arrive during a
        /// fetch share its result rather than opening a second round-trip.
        /// </summary>
        public async Task<IReadOnlyList<ActiveViewer>> GetOrFetchAsync(TimeSpan maxAge)
        {
            var current = _sample;
            if (current.Viewers.Count > 0 && current.Age <= maxAge) return current.Viewers;

            await _fetchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check under the gate: a caller that queued behind a fetch wants
                // that fetch's answer, not another one.
                current = _sample;
                if (current.Viewers.Count > 0 && current.Age <= maxAge) return current.Viewers;
                if (!IsConnectedProbe()) return Array.Empty<ActiveViewer>();

                var viewers = await FetchAsync().ConfigureAwait(false);
                if (viewers is { Count: > 0 })
                {
                    _sample = new ViewerPresenceSample(viewers, DateTimeOffset.UtcNow);
                    RecordRoles(viewers, NowMs());
                }
                // A FAILED fetch (null) answers this on-demand caller with an empty
                // list — the safe direction for its consumers (a payout tick skips
                // rather than paying off stale data) — while the published sample
                // stays whatever it was.
                return viewers ?? Array.Empty<ActiveViewer>();
            }
            finally { _fetchGate.Release(); }
        }
        private readonly SemaphoreSlim _fetchGate = new(1, 1);

        // ── Platform roles ──────────────────────────────────────────────────

        private void RecordRoles(IReadOnlyList<ActiveViewer> viewers, long nowMs)
        {
            foreach (var v in viewers)
            {
                if (string.IsNullOrWhiteSpace(v.Login)) continue;
                string key = NormalizeLogin(v.Login);
                if (key.Length == 0) continue;

                string display = string.IsNullOrWhiteSpace(v.Display) ? key : v.Display.Trim();
                bool haveOld = _roles.TryGetValue(key, out var old);

                // ★ The sweep is authoritative about SUBSCRIBED (it always carries that
                // flag) but only about mod/VIP when it actually sent a role field. When
                // it did not, the previous mod/VIP answer is KEPT rather than
                // overwritten with false — otherwise one role-less sweep would erase
                // what this viewer's own chat messages already proved, and a moderator
                // would silently lose every mod-gated command until they typed again.
                bool mod = v.RolesKnown ? v.IsMod : (haveOld && old.IsMod);
                bool vip = v.RolesKnown ? v.IsVip : (haveOld && old.IsVip);

                _roles[key] = new PlatformRoleRecord(v.Subscribed, mod, vip, nowMs, display);
            }
            PruneRoles(nowMs);
        }

        /// <summary>
        /// Records the platform role flags carried by a viewer's own chat message.
        ///
        /// <para><b>This is the primary role source, and the reason roles are reliably
        /// populated at all.</b> Every inbound chat line is stamped by the platform with
        /// that account's live moderator / VIP / subscriber standing — it is
        /// authoritative, it is free, and it arrives for every viewer who actually
        /// interacts. The presence sweep covers the lurkers on top of it, and the
        /// on-demand lookup covers whoever neither typed nor appeared.</para>
        ///
        /// <para>Call it from the chat path for EVERY message, not just commands: the
        /// point is to know a viewer's rank before they use something that needs it.</para>
        /// </summary>
        public void NoteChatRoles(string? login, string? display, bool isSub, bool isMod, bool isVip)
        {
            string key = NormalizeLogin(login);
            if (key.Length == 0) return;
            string name = string.IsNullOrWhiteSpace(display) ? key : display.Trim();
            _roles[key] = new PlatformRoleRecord(isSub, isMod, isVip, NowMs(), name);
        }

        // ── On-demand role resolution ───────────────────────────────────────

        /// <summary>
        /// Looks one login's platform roles up directly (the Hub's "Phoenix: Get User"
        /// round-trip). Wired by ScriptManager; null when Streamer.bot cannot answer.
        /// </summary>
        public Func<string, Task<PlatformRoles?>>? RoleFallbackProvider { get; set; }

        // A lookup costs a Streamer.bot round-trip, so it is rate-limited and its misses
        // are remembered: without the negative cache, a graph that asks about a login the
        // platform does not know would re-ask on every single evaluation. Pruned on the
        // lookup path itself (PruneLookupAttemptsLocked) — an entry older than the retry
        // window no longer gates anything, and a graph feeding arbitrary strings in here
        // must not grow the map for the life of the process.
        private readonly ConcurrentDictionary<string, long> _lookupAttempts = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _lookupGate = new(1, 1);
        private const long LookupRetryMs = 5 * 60 * 1000;
        private const int LookupsPerMinute = 12;
        private long _lookupWindowStartMs = -1;
        private int _lookupsThisWindow;

        /// <summary>
        /// The platform roles for a login, resolving on demand when the caches have never
        /// seen it. Cached answers return synchronously-fast; only a genuine miss pays a
        /// round-trip, at most <see cref="LookupsPerMinute"/> per minute and at most once
        /// per five minutes per login.
        ///
        /// <para>Use this from the places that can afford to await — the
        /// <c>User.GetGroups</c> node handler, a panel. NEVER from the chat hot path:
        /// <see cref="RolesFor(string?, out bool)"/> is the synchronous read, and by the
        /// time a chat line is being gated its author's own message has already populated
        /// the cache through <see cref="NoteChatRoles"/>.</para>
        /// </summary>
        public async Task<PlatformRoles> ResolveRolesAsync(string? login)
        {
            var cached = RolesFor(login, out bool known);
            if (known) return cached;

            string key = NormalizeLogin(login);
            if (key.Length == 0) return default;

            var provider = RoleFallbackProvider;
            if (provider is null) return default;

            long now = NowMs();
            if (_lookupAttempts.TryGetValue(key, out long lastTry) && now - lastTry < LookupRetryMs)
                return default;

            await _lookupGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check under the gate: a caller that queued behind an identical
                // lookup wants its answer, not a second round-trip.
                cached = RolesFor(login, out known);
                if (known) return cached;

                if (_lookupWindowStartMs < 0 || now - _lookupWindowStartMs >= 60_000)
                {
                    _lookupWindowStartMs = now;
                    _lookupsThisWindow = 0;
                }
                if (_lookupsThisWindow >= LookupsPerMinute) return default;
                _lookupsThisWindow++;
                PruneLookupAttemptsLocked(now);
                _lookupAttempts[key] = now;

                PlatformRoles? answer;
                try { answer = await provider(key).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("ViewerPresenceService", $"role lookup for '{key}' failed", ex);
                    return default;
                }
                if (answer is null) return default;

                // A resolved answer joins the cache as a real observation, so the next
                // ask is free and the negative cache never applies to it.
                _roles[key] = new PlatformRoleRecord(
                    answer.Value.IsSub, answer.Value.IsMod, answer.Value.IsVip, NowMs(), key);
                return answer.Value;
            }
            finally { _lookupGate.Release(); }
        }

        private void PruneRoles(long nowMs)
        {
            if (_roles.Count < 512) return;   // pruning a small map is pure overhead
            foreach (var kv in _roles)
                if (nowMs - kv.Value.StampMs > RoleRetentionMs)
                    _roles.TryRemove(kv.Key, out _);
        }

        // Caller holds _lookupGate — the only writer path, so the map cannot grow
        // faster than this runs. An expired entry no longer blocks a retry, so
        // dropping it changes no answer; the floor mirrors PruneRoles.
        private void PruneLookupAttemptsLocked(long nowMs)
        {
            if (_lookupAttempts.Count < 512) return;
            foreach (var kv in _lookupAttempts)
                if (nowMs - kv.Value >= LookupRetryMs)
                    _lookupAttempts.TryRemove(kv.Key, out _);
        }

        /// <summary>
        /// The platform standing last reported for a login, or false-everywhere when
        /// the platform has not told us about them. <paramref name="known"/> is the
        /// honest half of the answer: "not a moderator" and "never observed" are
        /// different facts, and a caller that renders them the same is lying.
        /// </summary>
        public PlatformRoles RolesFor(string? login, out bool known)
        {
            known = false;
            if (string.IsNullOrWhiteSpace(login)) return default;
            if (!_roles.TryGetValue(NormalizeLogin(login), out var rec)) return default;
            known = true;
            return new PlatformRoles(rec.IsSub, rec.IsMod, rec.IsVip);
        }

        /// <summary>Convenience overload for callers that treat unknown as "no".</summary>
        public PlatformRoles RolesFor(string? login) => RolesFor(login, out _);

        /// <summary>The display name last reported for a login (falls back to the
        /// login itself). Lets a viewer list render the name chat shows.</summary>
        public string DisplayFor(string? login)
        {
            if (string.IsNullOrWhiteSpace(login)) return "";
            string key = NormalizeLogin(login);
            return _roles.TryGetValue(key, out var rec) && rec.Display.Length > 0 ? rec.Display : key;
        }

        // ── Watch minutes ───────────────────────────────────────────────────

        private async Task AccrueAsync(IReadOnlyList<ActiveViewer> viewers, long nowMs)
        {
            var excluded = BuildExclusionSet();
            List<(string Name, long Minutes)> credits;

            lock (_accrualGate)
            {
                long last = _lastSampleMs;
                _lastSampleMs = nowMs;
                // No anchor (first pass, a reconnect, or a go-offline) means there is no
                // measured interval yet — record the anchor and credit nothing.
                if (last < 0) return;

                long elapsedMs = nowMs - last;
                if (elapsedMs <= 0) return;
                // Clamp to twice the configured cadence so a suspended machine, a long
                // GC pause or a stalled socket cannot hand everyone present an hour.
                long maxMs = 2L * PollSeconds * 1000;
                if (elapsedMs > maxMs) elapsedMs = maxMs;
                long elapsedSeconds = elapsedMs / 1000;
                if (elapsedSeconds <= 0) return;

                // Everyone present at the sample instant is credited for the interval
                // that just elapsed. That over-credits a viewer who arrived mid-interval
                // by at most one sampling period, which is the resolution of the whole
                // mechanism and is stated here rather than hidden.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in viewers)
                {
                    string name = NormalizeLogin(v.Login);
                    if (name.Length == 0 || excluded.Contains(name) || !seen.Add(name)) continue;
                    _pendingSeconds.TryGetValue(name, out long acc);
                    _pendingSeconds[name] = acc + elapsedSeconds;
                }

                credits = TakeWholeMinutesLocked();
            }

            if (credits.Count == 0) return;
            await WriteCreditsAsync(credits).ConfigureAwait(false);
        }

        // Drains the whole minutes out of the pending map, leaving the remainder.
        // Caller holds _accrualGate.
        private List<(string Name, long Minutes)> TakeWholeMinutesLocked()
        {
            var credits = new List<(string, long)>();
            List<string>? drained = null;
            foreach (var key in _pendingSeconds.Keys.ToList())
            {
                long seconds = _pendingSeconds[key];
                long minutes = seconds / 60;
                if (minutes <= 0) continue;
                long rest = seconds - (minutes * 60);
                if (rest == 0) (drained ??= new List<string>()).Add(key);
                else _pendingSeconds[key] = rest;
                credits.Add((key, minutes));
            }
            if (drained is not null)
                foreach (var k in drained) _pendingSeconds.Remove(k);
            return credits;
        }

        private async Task FlushWholeMinutesAsync()
        {
            List<(string Name, long Minutes)> credits;
            lock (_accrualGate) credits = TakeWholeMinutesLocked();
            if (credits.Count == 0) return;
            await WriteCreditsAsync(credits).ConfigureAwait(false);
        }

        private async Task WriteCreditsAsync(List<(string Name, long Minutes)> credits)
        {
            if (!PersistAccrual)
            {
                ApplyToMirror(credits);
                RaiseCredited(credits);
                return;
            }

            int applied;
            try
            {
                applied = await Db.WatchTimeAddManyAsync(credits).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ViewerPresenceService", "watch-time accrual failed", ex);
                return;
            }

            // A short count is a real failure the caller cannot see as an exception:
            // the write rolls back and returns 0 when the OPEN table is missing (a
            // streamer may drop it — it is theirs). Say so once rather than letting the
            // hours sit silently at zero forever.
            if (applied < credits.Count)
            {
                GlobalLogger.Log(
                    $"Watch time: credited {applied} of {credits.Count} viewer(s). " +
                    "The open \"WatchTime\" table is missing or unwritable — it is recreated on the next Hub start.",
                    "ViewerPresenceService", LogLevel.System);
                if (applied == 0) return;
            }

            ApplyToMirror(credits);
            RaiseCredited(credits);
        }

        private void RaiseCredited(List<(string Name, long Minutes)> credits)
            => SafeEvent.Raise(WatchTimeCredited, this,
                new WatchTimeCreditedEventArgs(credits.Select(c => c.Name).ToList()),
                "ViewerPresenceService", "WatchTimeCredited");

        private void ApplyToMirror(List<(string Name, long Minutes)> credits)
        {
            // Copy-on-write: readers hold a reference to an immutable map and never
            // lock, which is what makes a group's watch-hour rule free on the chat path.
            var next = new Dictionary<string, long>(_watchMinutes, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, minutes) in credits)
            {
                next.TryGetValue(name, out long have);
                next[name] = have + minutes;
            }
            _watchMinutes = next;
            WatchTimeReady = true;
        }

        private async Task MaybeReloadMirrorAsync(long nowMs)
        {
            if (_lastMirrorReloadMs >= 0 && nowMs - _lastMirrorReloadMs < MirrorReloadIntervalMs) return;
            try { await ReloadWatchMinutesAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("ViewerPresenceService", "watch-time reload failed", ex); }
        }

        /// <summary>Re-reads the whole open WatchTime table into the in-memory mirror.
        /// Called on a slow cadence, at startup, and whenever a surface edits the table
        /// by hand.</summary>
        public async Task ReloadWatchMinutesAsync()
        {
            _lastMirrorReloadMs = NowMs();
            if (!PersistAccrual) { WatchTimeReady = true; return; }
            var rows = await Db.WatchTimeAllAsync().ConfigureAwait(false);
            var map = new Dictionary<string, long>(rows.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, minutes) in rows)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                // A hand-built table may hold duplicate rows for one name; the reader
                // sums them, matching what a SELECT over the table would show a human.
                string key = NormalizeLogin(name);
                if (key.Length == 0) continue;
                map.TryGetValue(key, out long have);
                map[key] = have + minutes;
            }
            _watchMinutes = map;
            WatchTimeReady = true;
        }

        /// <summary>Accrued watch MINUTES for a login — a hash lookup against the
        /// in-memory mirror, safe to call on the chat hot path. 0 when unknown.</summary>
        public long WatchMinutesFor(string? login)
        {
            if (string.IsNullOrWhiteSpace(login)) return 0;
            return _watchMinutes.TryGetValue(NormalizeLogin(login), out long m) ? m : 0;
        }

        /// <summary>True when the login has watched at least <paramref name="hours"/>.
        /// A non-positive threshold means "no rule" and is never met, so a blank field
        /// can not silently promote the whole channel. Always false until the mirror
        /// has loaded, so a rule cannot mass-demote during startup either.</summary>
        public bool MeetsWatchHours(string? login, int hours)
        {
            if (hours <= 0 || !WatchTimeReady) return false;
            return WatchMinutesFor(login) >= (long)hours * 60;
        }

        /// <summary>The whole mirror, newest read wins — for the watch-time browser.</summary>
        public IReadOnlyDictionary<string, long> WatchMinutesSnapshot => _watchMinutes;

        /// <summary>Top watchers by accrued minutes, highest first.</summary>
        public IReadOnlyList<(string Login, long Minutes)> TopWatchers(int count)
        {
            if (count <= 0) return Array.Empty<(string, long)>();
            return _watchMinutes
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        /// <summary>
        /// Sets a viewer's accrued minutes outright — the manual adjust / reset the
        /// watch-time browser offers. Drops any carried remainder for that login so the
        /// next sample cannot immediately undo the correction.
        /// </summary>
        public async Task<bool> SetWatchMinutesAsync(string login, long minutes)
        {
            string key = NormalizeLogin(login);
            if (key.Length == 0) return false;
            if (minutes < 0) minutes = 0;

            lock (_accrualGate) _pendingSeconds.Remove(key);

            if (PersistAccrual)
            {
                try { await Db.WatchTimeSetAsync(key, minutes).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("ViewerPresenceService", "watch-time manual set failed", ex);
                    return false;
                }
            }

            var next = new Dictionary<string, long>(_watchMinutes, StringComparer.OrdinalIgnoreCase)
            {
                [key] = minutes,
            };
            _watchMinutes = next;
            WatchTimeReady = true;
            GlobalLogger.Log($"Watch time: {key} set to {minutes} minute(s) by hand.",
                "ViewerPresenceService", LogLevel.System);
            return true;
        }

        /// <summary>
        /// The ONE spelling a watch-time row is keyed under: trimmed, leading '@'
        /// removed, lowercased.
        ///
        /// <para>The '@' strip is not cosmetic. Every row already in the open
        /// "WatchTime" table was written through <c>RanksService.Normalize</c>, which
        /// strips it — while <c>LoyaltyService.Normalize</c>, which produced the login
        /// list handed to it, does not. Adopting the stricter of the two is what keeps
        /// a viewer's existing hours attached to them instead of silently starting a
        /// second row under a second spelling.</para>
        /// </summary>
        internal static string NormalizeLogin(string? s)
        {
            string t = (s ?? string.Empty).Trim();
            if (t.Length > 0 && t[0] == '@') t = t.Substring(1).Trim();
            return t.ToLowerInvariant();
        }

        // ── Exclusions ──────────────────────────────────────────────────────
        private HashSet<string> BuildExclusionSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var bots = BotAccountsProvider?.Invoke();
                if (bots is not null)
                    foreach (var b in bots)
                        if (!string.IsNullOrWhiteSpace(b)) set.Add(b.Trim());
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ViewerPresenceService", "BotAccountsProvider failed", ex);
            }
            return set;
        }

        // Cached observation of one viewer's platform standing.
        private readonly record struct PlatformRoleRecord(
            bool IsSub, bool IsMod, bool IsVip, long StampMs, string Display);
    }

    /// <summary>One presence sample: who the platform reported, and when.</summary>
    public sealed class ViewerPresenceSample
    {
        public static readonly ViewerPresenceSample Empty =
            new(Array.Empty<ActiveViewer>(), DateTimeOffset.MinValue);

        public ViewerPresenceSample(IReadOnlyList<ActiveViewer> viewers, DateTimeOffset sampledAtUtc)
        {
            Viewers = viewers ?? Array.Empty<ActiveViewer>();
            SampledAtUtc = sampledAtUtc;
        }

        public IReadOnlyList<ActiveViewer> Viewers { get; }
        public DateTimeOffset SampledAtUtc { get; }

        /// <summary>How old this sample is. <see cref="TimeSpan.MaxValue"/> for the
        /// empty sample, so an age test can never mistake "never sampled" for "fresh".
        /// </summary>
        public TimeSpan Age => SampledAtUtc == DateTimeOffset.MinValue
            ? TimeSpan.MaxValue
            : DateTimeOffset.UtcNow - SampledAtUtc;

        public int Count => Viewers.Count;
    }

    /// <summary>A viewer's platform standing as last reported by the sweep.</summary>
    public readonly record struct PlatformRoles(bool IsSub, bool IsMod, bool IsVip);

    /// <summary>The logins whose watch minutes were just written.</summary>
    public sealed class WatchTimeCreditedEventArgs : EventArgs
    {
        public WatchTimeCreditedEventArgs(IReadOnlyList<string> logins) => Logins = logins;
        public IReadOnlyList<string> Logins { get; }
    }
}
