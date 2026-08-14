using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // RanksService — the Hub-runtime brain of the rank-ladder pre-build tool: a viewer's
    // watch minutes (or Loyalty points) resolve to a named rank, the ladder is published
    // as a live leaderboard, and reaching a new rung fires Rank.OnRankUp plus an optional
    // User-Management group grant.
    //
    // Stateless-over-DB, like CountersService: the OPEN "WatchTime" / "Ranks" tables and
    // the Loyalty balance table are the single source of truth, so nothing here caches a
    // viewer's value or rank. Only the tool CONFIG is held in memory.
    //
    // ── THE THREE THINGS THIS TOOL HAD TO BUILD RATHER THAN REUSE ───────────────────
    //
    //  1. THERE WAS NO WATCH-HOUR STORE, AND THIS TOOL NO LONGER OWNS THE ONE IT BUILT.
    //     Loyalty's watch-time tick credited POINTS and accumulated no minutes anywhere,
    //     so this tool introduced the open "WatchTime" table and accrued into it as a
    //     passenger on that tick, behind its OWN master toggle. Two things were wrong with
    //     that: a default install (this tool ships OFF) recorded nothing at all, and the
    //     minutes stopped the moment the points economy's tick was not wanted. Accrual is
    //     now a background data source — ViewerPresenceService, always on, gated only by
    //     AppConfig.WatchTimeTrackingEnabled / WatchTimeOnlyWhenLive — and this tool is one
    //     READER of the table among several (the User-Management watch-hour group rules are
    //     another). What survives here is the ladder: turning a number into a named rank is
    //     the tool's job; recording the number never was.
    //     ★ Loyalty's own RegularThresholdHours is deliberately STILL not wired to the
    //     store: lighting up a previously inert setting would silently start granting
    //     Regular on every existing install the moment they updated, which is a behaviour
    //     change nobody asked for. That stays a separate, deliberate decision.
    //
    //  2. EffectiveRoles IS READ-ONLY. Its only write path swapped the whole
    //     UserManagementConfig blob and raced the panel's debounced whole-config rebuild,
    //     last-write-wins. The grant therefore goes through the narrow incremental
    //     UserManagementService.GrantGroupAsync added beside it, and — because no lock can
    //     make a whole-blob UI writer safe on its own — the grant is IDEMPOTENT and
    //     RE-ASSERTED on every evaluation. That makes it convergent rather than racy: a
    //     grant lost to a save collision is simply reapplied at the next evaluation, and
    //     the common case costs one hash lookup and no write. See EvaluateOneAsync.
    //
    //  3. THERE IS NO PER-USER BALANCE-CHANGE HOOK. LoyaltyService's single funnel carries
    //     neither user nor balance, and the balance table is OPEN, so a db.set_cell script
    //     write never enters the service at all. Rank evaluation is therefore NOT purely
    //     event-driven; it has exactly three trigger points, and that is a deliberate,
    //     stated tradeoff rather than a gap:
    //       • ViewerPresenceService.WatchTimeCredited — every viewer whose minutes were
    //         just written, batched (the primary evaluator). ★ It is a WATCH-TIME signal,
    //         so OnWatchTimeCreditedAsync acts on it only while the ladder measures watch
    //         time; on the points metric the two remaining triggers are the whole set;
    //       • the !rank chat command — the ASKING viewer only, so someone who just earned
    //         sees the truth immediately. "!rank <someone else>" is a pure read: the
    //         argument is raw chat text, and evaluating it would let any viewer mint a rank
    //         row, a chat announcement and another tool's group membership for any string
    //         they cared to type;
    //       • the Rank.Evaluate node — the graph's own explicit re-check, which is what
    //         closes the db.* hole: a graph that moves points directly can evaluate the
    //         viewer on the next line and get the rank-up it would otherwise wait for.
    //     The three value READS (Rank.Get / Rank.Value / Rank.Top) are deliberately PURE:
    //     a data pin the exporter may inline into a condition must not fire events or
    //     write rows as a side effect of being read twice.
    //
    // Pillar rule: Hub-only. The service cannot touch the script dispatcher itself, so
    // RaiseScriptEvent / Announce are injected from ScriptManager.Ranks.cs, exactly like
    // Timer / Loyalty / Counters.
    public sealed class RanksService
    {
        private readonly DB _db;

        public RanksService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static RanksService? _instance;
        private static readonly object _instanceGate = new();
        public static RanksService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new RanksService(DB.Instance);
            }
        }

        // ── Config (cached; values and ranks are NEVER cached) ──────────────
        private volatile RanksConfig _config = new();
        public RanksConfig Config => _config;

        /// <summary>Master gate — false makes the chat commands, the watch-time accrual,
        /// the rank-up event, the group grant and the overlay publish a total no-op.</summary>
        public bool Active => _config.Enabled;

        // ── Injected Hub-side seams (null-safe) ─────────────────────────────
        /// <summary>Raises a Rank.OnRankUp script event (wired in RegisterRanksCommands).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>Posts one unprompted line to the primary chat — the rank-up
        /// announcement, which has no ChatMessage to reply to. Mirrors
        /// <c>PollsService.Announce</c>.</summary>
        public Action<string>? Announce { get; set; }

        /// <summary>The streamer's currency noun, for the <c>{unit}</c> token on the
        /// points metric. A seam rather than a direct LoyaltyService read so the chat
        /// replies stay testable in-memory. Falls back to "points" when unset.</summary>
        public Func<string>? CurrencyProvider { get; set; }

        /// <summary>
        /// The OPEN balance table the points metric reads. A seam for the same reason as
        /// <see cref="CurrencyProvider"/>; the persistence layer validates whatever comes
        /// back, and an unset seam leaves the points metric reading 0 rather than
        /// guessing a table name.
        /// </summary>
        public Func<string>? BalanceTableProvider { get; set; }

        /// <summary>
        /// The Overlay Live Channel this service publishes <c>rank.*</c> into. Defaults to
        /// the process-wide store. Public get / internal set mirrors
        /// <c>CountersService.LiveStore</c>: production cannot swap it, while the test
        /// assembly gives each test its OWN store instead of sharing one.
        /// </summary>
        public OverlayLiveStore LiveStore { get; internal set; } = OverlayLiveStore.Instance;

        /// <summary>
        /// The group-grant seam. A <c>Func</c> returning whether a write actually happened,
        /// NOT a bare <c>Task</c>: the caller checks the answer and logs a grant that could
        /// not be applied, because "the seam returned and nothing happened" is precisely
        /// the failure shape a previous tool shipped by accident. Defaults to the real
        /// UserManagementService; tests swap it.
        /// </summary>
        public Func<string, string, Task<bool>> GrantGroup { get; internal set; }
            = static (group, login) => UserManagementService.Instance.GrantGroupAsync(group, login);

        // ── Change notifications (UI) ───────────────────────────────────────
        public event EventHandler? ConfigChanged;
        public event EventHandler? RuntimeChanged;

        private void RaiseConfigChanged() => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "RanksService", "ConfigChanged");
        private void RaiseRuntimeChanged() => SafeEvent.Raise(RuntimeChanged, this, EventArgs.Empty, "RanksService", "RuntimeChanged");

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        internal static string Normalize(string? s) => (s ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

        // ── Live state ──────────────────────────────────────────────────────
        // ★ This flag GATES NOTHING any more, and that is the point of keeping it. The
        // live gate on watch-time accrual (AppConfig.WatchTimeOnlyWhenLive) now lives in
        // ViewerPresenceService, which is handed the same edge from the same dispatch site.
        // What this tool still needs from the edge is a REPAINT: the status pill's
        // "armed · waiting for live" state reads the presence service's own gate, and a
        // property has no change notification of its own, so without the edge arriving here
        // too the chip would go on claiming "waiting" for the rest of the stream. Hence a
        // field whose only job is to make the notification one-shot per transition rather
        // than one per event.
        private volatile bool _streamLive;

        /// <summary>Wired from ScriptManager's StreamOnline/Offline dispatch beside the
        /// Timer / Scheduling / Loyalty / User-Management calls. Notification only — see
        /// the field comment for why this tool still wants the edge.</summary>
        public void SetStreamLive(bool live)
        {
            if (_streamLive == live) return;
            _streamLive = live;
            RaiseRuntimeChanged();
        }

        // ── Stat-tile runtime state (ephemeral, never persisted) ────────────
        // NOT volatile: this one is bumped through Interlocked.Increment, and passing a
        // volatile field by ref is CS0420 ("a reference to a volatile field will not be
        // treated as volatile"). The interlocked write carries its own full fence, so the
        // qualifier bought nothing it did not already have. Matches every other Interlocked
        // target under Hub/Core — AlertsService._alertsFiredEver,
        // UserManagementService._greetedThisSession — none of which is volatile either.
        private int _rankUpsThisSession;
        /// <summary>Rank-ups announced since Hub start (for the panel's stat tile).</summary>
        public int RankUpsThisSession => _rankUpsThisSession;

        private volatile int _lastAccrualCount;
        /// <summary>
        /// Viewers credited by the most recent watch-time write this tool was told about
        /// (the stat tile). It counts the logins the presence service actually persisted
        /// minutes for, not the size of a sweep — the two differ whenever a viewer's
        /// sub-minute remainder has not rolled over yet.
        ///
        /// <para>Only updated while the tool is ENABLED, which is exactly how it behaved
        /// when this tool owned the accrual: a dormant tool's panel reports the last thing
        /// it saw rather than counting on behalf of a ladder that is switched off. It is
        /// deliberately NOT gated on the metric — a points ladder still shows what the
        /// background counter is doing, which is what the tile is for.</para>
        /// </summary>
        public int LastAccrualCount => _lastAccrualCount;

        // ── Lifecycle ───────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? raw = await _db.LoadRanksConfigAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cfg = JsonSerializer.Deserialize<RanksConfig>(raw!, JsonOpts);
                    if (cfg != null) _config = Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RanksService", "InitializeAsync: config load failed", ex);
            }

            try
            {
                await _db.EnsureRanksDataTablesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RanksService", "InitializeAsync: data-table ensure failed", ex);
            }

            // The pump runs unconditionally and self-gates inside, mirroring
            // SchedulingService's tick and the viewer queue's heartbeat: the gate can flip
            // at any time, and a loop that had to be started and stopped by config edits is
            // one more thing to keep in step for no gain. A dormant tick costs one volatile
            // read and returns before touching the databank.
            StartOverlayPump();

            GlobalLogger.Log(
                $"RanksService online — {_config.Ranks.Count} rank(s) on the {RankMetrics.Normalize(_config.Metric)} ladder, " +
                $"tool {(Active ? "ENABLED" : "disabled")}.",
                "RanksService", LogLevel.System);
            RaiseConfigChanged();
        }

        /// <summary>Cancels the overlay heartbeat and drains it. Config is persisted on
        /// every edit and both data tables live in the databank, so there is nothing to
        /// flush — this only stops the pump from re-dirtying a channel the shutdown
        /// coordinator already drained. Mirrors UserManagementService.ShutdownAsync.</summary>
        public async Task ShutdownAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_loopGate)
            {
                cts = _loopCts;
                loop = _loopTask;
                _loopStarted = false;
                _loopCts = null;
                _loopTask = null;
            }
            try { cts?.Cancel(); } catch { /* best-effort */ }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { GlobalLogger.Error("RanksService", "overlay loop drain failed", ex); }
            }
            cts?.Dispose();
        }

        private static RanksConfig Normalize(RanksConfig cfg)
        {
            cfg.Ranks ??= new List<RankDef>();
            foreach (var r in cfg.Ranks)
            {
                r.Name ??= "";
                r.GrantGroup ??= "";
            }
            cfg.Metric = RankMetrics.Normalize(cfg.Metric);
            cfg.ViewRoles ??= RankRoles.All();
            // `??=` only fills a NULL — an empty or whitespace-only string survived
            // it, and the match then required a non-empty verb, so the command
            // silently never fired while the panel still showed ChatEnabled=true.
            // Blank now falls back to the default like every sibling tool does.
            // A NON-blank value is kept VERBATIM: a configured "!rank" is handled at
            // the comparison (ChatVerb), so there is no reason to rewrite what the
            // streamer typed into the field — see feedback_no_unrequested_input_validation.
            if (string.IsNullOrWhiteSpace(cfg.RankCommand)) cfg.RankCommand = "rank";
            if (string.IsNullOrWhiteSpace(cfg.TopCommand)) cfg.TopCommand = "ranks";
            cfg.AnnounceMessage ??= "";
            cfg.RankReplyMessage ??= "";
            cfg.TopRankReplyMessage ??= "";
            cfg.UnrankedReplyMessage ??= "";
            cfg.NoLadderReplyMessage ??= "";
            cfg.TopReplyMessage ??= "";
            cfg.TopEmptyMessage ??= "";
            if (cfg.LeaderboardSize < 1) cfg.LeaderboardSize = 1;
            if (cfg.LeaderboardSize > 100) cfg.LeaderboardSize = 100;
            if (cfg.OverlaySize < 1) cfg.OverlaySize = 1;
            if (cfg.OverlaySize > 100) cfg.OverlaySize = 100;
            if (cfg.CooldownSeconds < 0) cfg.CooldownSeconds = 0;
            return cfg;
        }

        /// <summary>Replaces the whole config (deep-owned by the caller), persists it and
        /// notifies. The panel's single write path.</summary>
        public async Task UpdateConfigAsync(RanksConfig cfg)
        {
            _config = Normalize(cfg ?? new RanksConfig());
            try
            {
                string json = JsonSerializer.Serialize(_config, JsonOpts);
                await _db.SaveRanksConfigAsync(json, NowUnixMs()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RanksService", "UpdateConfigAsync: save failed", ex);
            }
            // Republish immediately rather than waiting out the heartbeat: switching the
            // overlay off and watching the board sit there for fifteen seconds reads as a
            // broken widget, and switching it on and seeing nothing reads as a broken tool.
            try { await PublishOverlayAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("RanksService", "UpdateConfigAsync: overlay publish failed", ex); }
            RaiseConfigChanged();
        }

        // ── The ladder ──────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a value to its rung and the one above it. The ENABLED rung with the
        /// highest Threshold &lt;= value wins and ties resolve to the first in list order —
        /// the identical rule <c>AlertsService.PickTier</c> uses, deliberately, so the two
        /// threshold ladders in the product cannot behave differently. <c>Next</c> is the
        /// enabled rung with the lowest Threshold strictly above the value (null at the
        /// top of the ladder), which is what the reply's "{needed} more" is measured to.
        ///
        /// ★ This is the raw arithmetic and nothing else. Every caller goes through
        /// <see cref="ResolveStanding"/> instead, which adds the one rule this deliberately
        /// does not know about: a viewer with nothing recorded is unranked.
        /// </summary>
        internal static (RankDef? Current, RankDef? Next) Resolve(IReadOnlyList<RankDef> ranks, long value)
        {
            RankDef? current = null, next = null;
            if (ranks is null) return (null, null);
            foreach (var r in ranks)
            {
                if (r is null || !r.Enabled) continue;
                if (r.Threshold <= value)
                {
                    if (current is null || r.Threshold > current.Threshold) current = r;
                }
                else
                {
                    if (next is null || r.Threshold < next.Threshold) next = r;
                }
            }
            return (current, next);
        }

        /// <summary>
        /// The ladder as every CALLER must see it — <see cref="Resolve"/> plus the
        /// no-standing guard.
        ///
        /// ★ A value of 0 is UNRANKED, whatever the ladder says. Zero is not a small
        /// number here, it is the answer for "there is no row in the metric table" — a
        /// viewer nobody has ever recorded, which includes any name a chatter simply typed.
        /// <see cref="Resolve"/> is pure threshold arithmetic, so a rung at Threshold 0
        /// matches a value of 0 and would hand that name a rank, a remembered row, a
        /// rank-up announcement and a User-Management group grant. A rung at 0 is not
        /// hypothetical: it is exactly what the panel's ✚ button creates for the FIRST row
        /// until the streamer types a number in.
        ///
        /// The guard is on the VALUE rather than on the rung, deliberately. Forbidding a
        /// zero threshold would be a rule about the streamer's ladder ("you may not label
        /// the bottom") enforced to fix a rule about our data ("nothing recorded is not an
        /// achievement"), and it would still leave every OTHER read trusting a bare
        /// Resolve. A rung at 0 keeps working — for anyone with a single minute on the
        /// clock.
        ///
        /// <c>Next</c> is passed through untouched, and that is what makes the zero case
        /// read correctly rather than merely refuse: <c>Next</c> already means "the lowest
        /// enabled rung strictly above the value", so on a value of 0 it is the lowest rung
        /// anybody could actually earn. A ladder consisting only of a rung at 0 therefore
        /// resolves to (null, null) — the no-ladder-to-climb case the chat reply has its own
        /// branch for.
        /// </summary>
        internal static (RankDef? Current, RankDef? Next) ResolveStanding(IReadOnlyList<RankDef> ranks, long value)
        {
            var (current, next) = Resolve(ranks, value);
            if (value <= 0) current = null;
            return (current, next);
        }

        private RankMetricSource MetricSource(RanksConfig cfg)
            => RankMetrics.Normalize(cfg.Metric) == RankMetrics.Points
                ? DB.PointsSource(SafeCall(BalanceTableProvider, ""))
                : DB.WatchTimeSource;

        private string Unit(RanksConfig cfg)
            => RankMetrics.UnitFor(RankMetrics.Normalize(cfg.Metric), SafeCall(CurrencyProvider, "points"));

        private static string SafeCall(Func<string>? provider, string fallback)
        {
            if (provider is null) return fallback;
            try
            {
                string v = provider();
                return string.IsNullOrWhiteSpace(v) ? fallback : v;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RanksService", "config provider seam failed", ex);
                return fallback;
            }
        }

        // ── Pure reads (no side effects — see the class doc) ────────────────

        /// <summary>The viewer's current metric value (0 when they have none).</summary>
        public Task<long> ValueAsync(string user)
            => _db.RankValueAsync(MetricSource(_config), Normalize(user));

        /// <summary>The viewer's current rank NAME, or "" when they are below the lowest
        /// rung (or the ladder is empty). Computed from the live value every time — never
        /// the remembered rank, which exists only to make rank-up one-shot.</summary>
        public async Task<string> RankNameAsync(string user)
        {
            var cfg = _config;
            long value = await _db.RankValueAsync(MetricSource(cfg), Normalize(user)).ConfigureAwait(false);
            return ResolveStanding(cfg.Ranks, value).Current?.Name ?? "";
        }

        /// <summary>Top-N standings on the ladder, position starting at 1. Each row
        /// carries the rank NAME its value resolves to, so a caller never has to run the
        /// ladder twice.</summary>
        public async Task<List<RankStanding>> TopAsync(int n)
        {
            var cfg = _config;
            var result = new List<RankStanding>();
            if (n <= 0) return result;
            if (n > 100) n = 100;
            var rows = await _db.RankTopAsync(MetricSource(cfg), n).ConfigureAwait(false);
            int position = 0;
            foreach (var (name, value) in rows)
            {
                position++;
                result.Add(new RankStanding(position, name, value, ResolveStanding(cfg.Ranks, value).Current?.Name ?? ""));
            }
            return result;
        }

        // ── Watch-time handover (the accrual itself is not ours) ────────────

        /// <summary>
        /// Driven by <c>ViewerPresenceService.WatchTimeCredited</c>: the logins whose watch
        /// minutes were just written. The write already happened — this tool neither
        /// decides whether hours are recorded nor performs the transaction — so all that is
        /// left is the one thing a ladder owes those viewers: re-resolve their rung and, on
        /// a promotion, announce it, raise Rank.OnRankUp and apply the rung's group grant.
        ///
        /// <para>Evaluating off the SAME batch that was just persisted is what keeps a
        /// viewer's minutes and their rank from disagreeing for a whole sampling interval —
        /// which is precisely the window in which a rank-up announcement is worth anything.
        /// It is also why the presence service raises logins rather than a bare "something
        /// changed": a ladder must never re-read the whole table to find out who moved.</para>
        ///
        /// <para>★ A no-op unless the ladder measures WATCH TIME. A credit event says
        /// "these people just gained minutes"; on a points ladder that is not news about
        /// any value this tool compares, so acting on it would mean re-running the ladder
        /// for every present viewer every sampling interval to discover nothing. The points
        /// metric's trigger points are !rank and the Rank.Evaluate node — see the class
        /// doc's three-trigger list, which states this rather than leaving it implied.</para>
        ///
        /// <para>Cheap on every path that does not evaluate: a volatile read, a length
        /// check and one string comparison, and it is called at most once per sampling
        /// interval.</para>
        /// </summary>
        public async Task OnWatchTimeCreditedAsync(IReadOnlyList<string> logins)
        {
            var cfg = _config;
            if (!cfg.Enabled || logins is null || logins.Count == 0) return;

            // The stat tile tracks the background counter regardless of metric — see
            // LastAccrualCount for why that is not gated with the evaluation below.
            _lastAccrualCount = logins.Count;
            RaiseRuntimeChanged();

            if (RankMetrics.Normalize(cfg.Metric) != RankMetrics.WatchTime) return;
            await EvaluateManyAsync(logins).ConfigureAwait(false);
        }

        // ── Evaluation (the only side-effecting path) ───────────────────────

        /// <summary>
        /// Evaluates one viewer: resolves their rank from the live value, remembers it, and
        /// — when it is a PROMOTION over the remembered one — announces, raises
        /// Rank.OnRankUp and applies the rung's group grant. Returns the resolved rank name
        /// ("" when unranked). A no-op while the tool is dormant.
        ///
        /// A viewer with no recorded value is also a no-op, writing nothing and firing
        /// nothing — see the guard at the top of <c>EvaluateOneAsync</c>. That is what keeps
        /// a name nobody has ever seen (a chat argument, a graph's hand-typed string) from
        /// minting a row, an announcement and a group grant.
        /// </summary>
        public async Task<string> EvaluateAsync(string user, string? display = null)
        {
            var cfg = _config;
            if (!cfg.Enabled) return "";
            string login = Normalize(user);
            if (login.Length == 0) return "";

            long value;
            string previous;
            try
            {
                value = await _db.RankValueAsync(MetricSource(cfg), login).ConfigureAwait(false);
                previous = (await _db.RankStateAsync(login).ConfigureAwait(false)).Rank;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RanksService", "EvaluateAsync: read failed", ex);
                return "";
            }
            return await EvaluateOneAsync(cfg, login, display, value, previous).ConfigureAwait(false);
        }

        /// <summary>
        /// The batch evaluator behind the watch-time tick: two reads for the whole sweep
        /// (values, remembered ranks) and one transaction for everything that changed,
        /// instead of three round-trips per viewer through the shared connection gate.
        /// </summary>
        public async Task EvaluateManyAsync(IReadOnlyList<string> users)
        {
            var cfg = _config;
            if (!cfg.Enabled || users is null || users.Count == 0) return;

            var logins = new List<string>(users.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users)
            {
                string n = Normalize(u);
                if (n.Length != 0 && seen.Add(n)) logins.Add(n);
            }
            if (logins.Count == 0) return;

            Dictionary<string, long> values;
            Dictionary<string, string> previous;
            try
            {
                values = await _db.RankValuesAsync(MetricSource(cfg), logins).ConfigureAwait(false);
                previous = await _db.RankStatesAsync(logins).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RanksService", "EvaluateManyAsync: read failed", ex);
                return;
            }

            foreach (string login in logins)
            {
                values.TryGetValue(login, out long value);
                previous.TryGetValue(login, out string? prev);
                await EvaluateOneAsync(cfg, login, null, value, prev ?? "").ConfigureAwait(false);
            }
        }

        // The one place a rank is decided, remembered and acted on. Every caller funnels
        // through here so the promotion rule, the grant re-assertion and the remembered
        // rank can never drift between the tick, the chat command and the node.
        private async Task<string> EvaluateOneAsync(
            RanksConfig cfg, string login, string? display, long value, string previousRank)
        {
            // ★ NOTHING RECORDED IS NOT AN ACHIEVEMENT. A viewer whose metric value is 0 has
            // no row in the metric table at all — which is also what any NAME that was never
            // on this channel reads as. Without this the whole side-effecting half of the
            // tool would run for such a name: a rung at Threshold 0 resolves as "reached",
            // so a row is written to the open Ranks table, Rank.OnRankUp fires, the bot
            // announces the promotion, and the rung's group is granted in ANOTHER tool's
            // config. Returning before any of that keeps an unknown name a pure read.
            // ResolveStanding enforces the same rule for every reader; this is the write
            // side of it, stated where the writes are.
            if (value <= 0) return "";

            var (current, next) = ResolveStanding(cfg.Ranks, value);
            string rank = current?.Name ?? "";

            // A PROMOTION is "the remembered rung is not this one, and this one is higher".
            // Comparing THRESHOLDS rather than list positions is what makes a mid-stream
            // reorder of the ladder harmless: the rungs are an unordered set of numbers to
            // this check, so moving a row in the panel can never mint a fake rank-up.
            //
            // ★ AN UNKNOWN REMEMBERED RANK IS NOT A PROMOTION. ThresholdOf returns null when
            // the remembered NAME is on no rung of the current ladder — the streamer renamed
            // the rung, deleted it, or a db.* script wrote the open Ranks table by hand. That
            // is missing information, not evidence of a climb, and reading it as "lower than
            // everything" is what made the event fire again for a rung the viewer already
            // held: rename one rung mid-stream and every viewer standing on it is announced
            // as promoted to it on the next tick. Skipping the announcement still re-remembers
            // the new name below, so the ladder converges in one pass and stays quiet.
            bool changed = !string.Equals(rank, previousRank, StringComparison.OrdinalIgnoreCase);
            long? previousThreshold = ThresholdOf(cfg.Ranks, previousRank);
            bool promoted = changed && current is not null
                            && previousThreshold.HasValue && previousThreshold.Value < current.Threshold;

            if (changed)
            {
                // ★ The RESULT is checked, not just the exception. RankStateSetManyAsync
                // rolls back and returns 0 when the open "Ranks" table is missing — a
                // streamer can drop it, it is theirs — and that is a SILENT failure with no
                // exception to catch. Announcing anyway would mean the next evaluation
                // reads the same stale previous rank and announces all over again: on a
                // busy channel, a promotion line every single tick, forever.
                int remembered;
                try
                {
                    remembered = await _db.RankStateSetManyAsync(new[] { (login, rank, value) }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("RanksService", "EvaluateOneAsync: rank persist failed", ex);
                    return rank;
                }
                if (remembered == 0)
                {
                    GlobalLogger.Log(
                        $"Ranks: could not remember {login}'s rank — the open \"Ranks\" databank table may be missing. " +
                        "Reopen the Ranks page to recreate it; no rank-up is announced until it can be recorded.",
                        "RanksService", LogLevel.CriticalError);
                    return rank;
                }
            }

            // The grant is re-asserted on EVERY evaluation, not only on promotion — see
            // the class doc. GrantGroupAsync is idempotent and answers false without
            // writing when the viewer is already in the group, so the steady state costs
            // one hash lookup; a grant that a whole-config panel save clobbered is simply
            // reapplied here. It is additive only: a demotion never takes a group back.
            if (current is not null && !string.IsNullOrWhiteSpace(current.GrantGroup))
                await ApplyGrantAsync(current.GrantGroup, login, current.Name).ConfigureAwait(false);

            if (!promoted) return rank;

            Interlocked.Increment(ref _rankUpsThisSession);
            RaiseRuntimeChanged();

            string who = string.IsNullOrWhiteSpace(display) ? login : display!.Trim();
            string unit = Unit(cfg);
            // Reached only past the verified persist above, so a row here can never claim a
            // promotion that did not stick.
            RecordActivity("UP", $"{ClipForActivity(who)} reached {ClipForActivity(rank)} " +
                                 $"({value.ToString(CultureInfo.InvariantCulture)} {ClipForActivity(unit)}).");
            RaiseRankUp(who, login, rank, value, unit, next?.Name ?? "");

            if (cfg.AnnounceEnabled && !string.IsNullOrWhiteSpace(cfg.AnnounceMessage))
            {
                string line = FormatReply(cfg.AnnounceMessage, who, rank, value, unit, next?.Name ?? "", 0);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var announce = Announce;
                    if (announce is not null)
                    {
                        try { announce(line); }
                        catch (Exception ex) { GlobalLogger.Error("RanksService", "rank-up announce failed", ex); }
                    }
                }
            }
            return rank;
        }

        // The height the remembered rank name stands at. THREE answers, and keeping them
        // apart is the whole point:
        //
        //   long.MinValue — nothing was remembered (a viewer this tool has never evaluated,
        //                   or one who was genuinely below the lowest rung). Below every
        //                   real rung, so reaching one is a promotion.
        //   null          — a name was remembered but it is on NO rung of the current
        //                   ladder: the streamer renamed or deleted that rung, or a db.*
        //                   script hand-wrote the open Ranks table. We cannot tell whether
        //                   the viewer went up or down, so this is UNKNOWN, not zero and not
        //                   MinValue. The caller must not announce on it.
        //   a threshold   — the ordinary case.
        //
        // Collapsing the middle case into MinValue is what used to make a ladder rename
        // announce a promotion for every viewer already standing on the renamed rung.
        private static long? ThresholdOf(IReadOnlyList<RankDef> ranks, string name)
        {
            if (string.IsNullOrEmpty(name) || ranks is null) return long.MinValue;
            foreach (var r in ranks)
                if (r is not null && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                    return r.Threshold;
            return null;
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "Ranks";

        // A display name is viewer-supplied; a rung name and a currency noun are
        // streamer-supplied. All three are unbounded, so all three go through here.
        private const int ActivityFieldMaxChars = 40;

        private static string ClipForActivity(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            return t.Length <= ActivityFieldMaxChars ? t : t[..ActivityFieldMaxChars].TrimEnd() + "...";
        }

        // Observation only: this sits inside the promotion commit, so a fault in it must
        // never become a fault in the ladder.
        private static void RecordActivity(string kind, string message)
        {
            try { ToolActivityRing.Record(ActivityTool, kind, message); }
            catch (Exception ex) { GlobalLogger.Error("RanksService", "activity record failed", ex); }
        }

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says. The states worth the space are the ones a
        /// streamer cannot otherwise see, and every one of them is real:
        ///
        /// <para>★ Every watch-time state below is a statement about
        /// <c>ViewerPresenceService</c>, not about this tool. The accrual moved there and
        /// the pill followed it: a chip that read this tool's own former switches would go
        /// on claiming the ladder was fed by settings that no longer decide anything.</para>
        ///
        /// <para><see cref="ArmedWaitingForLive"/> — watch-time tracking is on but the
        /// online-only gate is holding it (<c>AppConfig.WatchTimeOnlyWhenLive</c> is set and
        /// no go-live edge has been seen). Nothing is broken, so this is "waiting", not
        /// "frozen".</para>
        ///
        /// <para><see cref="FrozenCountingOff"/> — the ladder measures WATCH TIME and
        /// <c>AppConfig.WatchTimeTrackingEnabled</c> is off, so no minute is recorded
        /// anywhere. The ladder is not merely ungated-but-idle: unless something else writes
        /// the open "WatchTime" table, every value it resolves stays where it already was,
        /// and on a fresh install that is zero. This used to report <c>Accruing</c> — a
        /// green pulsing chip on the strip while the card below it said the ladder would
        /// stay at zero.</para>
        ///
        /// <para><see cref="FrozenStreamerBotDown"/> — the accrual's presence sweep comes
        /// from Streamer.bot and returns an empty list while the socket is down, so nobody
        /// accrues and nothing else on the page says so.</para>
        ///
        /// <para><see cref="FrozenLoyaltyOff"/> applies to the POINTS metric only. On the
        /// watch-time metric the accrual is wholly independent of the Loyalty master toggle
        /// — an hour watched is an hour watched whether or not the streamer has the points
        /// economy switched on, and since the move it does not even share a clock with it —
        /// so claiming frozen there would be false.</para>
        /// </summary>
        public enum RanksPillState
        {
            /// <summary>The tool is switched off.</summary>
            Dormant,
            /// <summary>On, measuring POINTS, and the Loyalty tool is off — the balance the
            /// ladder reads is not moving.</summary>
            FrozenLoyaltyOff,
            /// <summary>On, tracking watch time, but its online-only gate is holding accrual
            /// because the stream is offline.</summary>
            ArmedWaitingForLive,
            /// <summary>On, tracking watch time, but Streamer.bot is down, so the presence
            /// sweep that feeds accrual sees no viewers.</summary>
            FrozenStreamerBotDown,
            /// <summary>On, measuring watch time, and the suite-wide minute counter is
            /// switched off — nothing is ever credited, so the ladder cannot move on its
            /// own.</summary>
            FrozenCountingOff,
            /// <summary>On and accruing (or, on the points metric, reading a live
            /// economy).</summary>
            Accruing,
        }

        /// <summary>
        /// Pure state machine behind <see cref="PillState"/>. <paramref name="pointsMetric"/>
        /// is "the ladder measures the Loyalty balance"; <paramref name="trackWatchTime"/>
        /// and <paramref name="accrualWanted"/> are the two halves of the watch-time gate,
        /// and both are passed because they answer different questions —
        /// <paramref name="trackWatchTime"/> is "is the counter switched on at all"
        /// (<c>AppConfig.WatchTimeTrackingEnabled</c>) while
        /// <paramref name="accrualWanted"/> is "is it counting right now"
        /// (<c>ViewerPresenceService.AccrualWanted</c>, which is additionally false while
        /// the online-only gate is closed). Collapsing them would make "switched off" and
        /// "waiting for the stream" render as one state.
        ///
        /// <para>Kept a pure static over booleans so the suite can pin every transition
        /// without a Hub process, a databank or a live socket behind it. The arguments'
        /// SOURCES moved when the accrual moved; their meanings did not, which is why this
        /// function is unchanged by that move.</para>
        /// </summary>
        internal static RanksPillState ComputePillState(
            bool enabled, bool pointsMetric, bool trackWatchTime, bool accrualWanted,
            bool loyaltyActive, bool connected)
        {
            if (!enabled) return RanksPillState.Dormant;
            if (pointsMetric)
                return loyaltyActive ? RanksPillState.Accruing : RanksPillState.FrozenLoyaltyOff;
            // ★ NOT Accruing. With the minute counter off nothing writes the "WatchTime"
            // table, so a watch-time ladder cannot move on its own — which is exactly what
            // the panel's own watch-time card says. Reporting the good state here made the
            // strip contradict the card below it.
            if (!trackWatchTime) return RanksPillState.FrozenCountingOff;
            if (!accrualWanted) return RanksPillState.ArmedWaitingForLive;
            if (!connected) return RanksPillState.FrozenStreamerBotDown;
            return RanksPillState.Accruing;
        }

        /// <summary>
        /// The live pill state.
        ///
        /// <para>★ The two watch-time arguments are read from the thing that actually
        /// decides — AppConfig for the switch, <c>ViewerPresenceService.AccrualWanted</c>
        /// for "is it counting right now" — and never re-derived from this tool's own
        /// config or its own <c>_streamLive</c> flag. A pill is the surface a streamer
        /// trusts to tell them the tool is working, so it may only ever report the state of
        /// the code that does the work: if the presence service is not accruing, this must
        /// say so even when everything on the Ranks page looks configured.</para>
        /// </summary>
        public RanksPillState PillState
        {
            get
            {
                var cfg = _config;
                bool pointsMetric = RankMetrics.Normalize(cfg.Metric) == RankMetrics.Points;
                // Same fallback as ViewerPresenceService's own reader: config not loaded
                // yet reads as the shipped default (tracking ON) rather than as OFF, so the
                // pill cannot flash "frozen · counting off" during startup.
                bool trackWatchTime = ConfigManager.Current?.WatchTimeTrackingEnabled ?? true;
                return ComputePillState(cfg.Enabled, pointsMetric, trackWatchTime,
                                        ViewerPresenceService.Instance.AccrualWanted,
                                        LoyaltyService.Instance.Active,
                                        WS.Instance.IsConnected);
            }
        }

        private async Task ApplyGrantAsync(string group, string login, string rankName)
        {
            var grant = GrantGroup;
            if (grant is null) return;
            try
            {
                // The seam answers whether a write happened; false is the ordinary
                // already-a-member case, so it is deliberately NOT logged. A group that
                // does not exist is logged by the grant itself, once per attempt, at the
                // one place that can tell the two apart.
                await grant(group, login).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RanksService", $"group grant for rank \"{rankName}\" failed", ex);
            }
        }

        // Rank.OnRankUp script event. The token set is pinned in three more places (the
        // exporter arm, AutocompleteScopeBuilder and VarChainAnalyzer.ResultEmitterMap),
        // so it is built here once rather than at each raise site.
        private void RaiseRankUp(string display, string login, string rank, long value, string unit, string next)
        {
            var raise = RaiseScriptEvent;
            if (raise is null) return;
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event.user"] = display,
                    // The DISPLAY name is what a chat line prints, but a databank row, a
                    // group check or any Rank.* node needs the stable LOGIN. It is bound
                    // under BOTH spellings on purpose:
                    //   event.login      — the Login output socket on this root, and the
                    //                      spelling User.OnFirstMessage already uses.
                    //   event.user_login — the suite-wide login token BuildChatVars parks on
                    //                      every chat run. ★ This one is load-bearing, not a
                    //                      convenience: every Rank.* node's empty-User
                    //                      fallback resolves through it (ResolveRankUser),
                    //                      so an unwired Rank.Get dropped onto THIS root
                    //                      would otherwise fall through to {user.name} — the
                    //                      display name — and read the login-keyed ladder
                    //                      with the wrong key.
                    ["event.login"] = login,
                    ["event.user_login"] = login,
                    ["event.rankname"] = rank,
                    ["event.value"] = value.ToString(CultureInfo.InvariantCulture),
                    ["event.unit"] = unit,
                    ["event.next"] = next,
                    ["user.name"] = display,
                };
                raise("Rank.OnRankUp", vars);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("RanksService", "RaiseScriptEvent(Rank.OnRankUp) failed", ex);
            }
        }

        // ── Built-in chat commands ──────────────────────────────────────────

        /// <summary>Outcome of offering one chat line to the tool. <see cref="Handled"/>
        /// stops the built-in dispatch; <see cref="Reply"/> is what to post and may be
        /// empty for a handled-but-silent case — a role rejection, a cooldown, or a target
        /// that is not a chat login. None of those posts anything: a rejection any viewer can
        /// repeat at will must never be a line the bot can be made to say. Handled stays TRUE
        /// for them, so the message also cannot fall through to an authored on_chat script
        /// that would answer what the tool just refused.</summary>
        public readonly struct RankChatResult
        {
            public RankChatResult(bool handled, string reply) { Handled = handled; Reply = reply; }
            public bool Handled { get; }
            public string Reply { get; }
            public static readonly RankChatResult NotHandled = new(false, "");
            public static RankChatResult Silent() => new(true, "");
            public static RankChatResult Say(string reply) => new(true, reply ?? "");
        }

        // Per-user cooldown across both verbs. Monotonic Stopwatch clock — never wall clock
        // — so an NTP step or a DST change can't unblock a cooldown early. Both verbs are
        // reads, so unlike Counters / the viewer queue there is no modify bucket to split.
        private readonly object _cdGate = new();
        private readonly Dictionary<string, long> _userCdMs = new(StringComparer.Ordinal);
        private static readonly System.Diagnostics.Stopwatch _mono = System.Diagnostics.Stopwatch.StartNew();

        // ★ BOUNDED. The shape this was copied from grows by one PERMANENT entry per
        // distinct chatter and never shrinks for the life of the process — a `!rank` is one
        // line of chat, so a raid or a big hype train walks thousands of one-off names
        // through here in minutes and every one of them stays. SoundboardService says the
        // same thing on its own map, and SongRequestService bounded its viewer cooldown for
        // the same reason; this is that lighter idiom verbatim. Past a small ceiling, drop
        // everything already expired, on the WRITE path because that is the only path that
        // can grow the map. The hot path stays one probe plus one store.
        //
        // No hard ceiling behind it, unlike Soundboard's: a sweep's survivors are exactly
        // the users still inside a cooldown, so "nothing was reclaimed" means a chat that
        // really does have that many distinct people asking within one cooldown window —
        // and evicting a live cooldown would hand one of them a free re-ask.
        private const int CooldownMapSweepThreshold = 256;

        /// <summary>
        /// The built-in Ranks chat commands: <c>!&lt;rank&gt;</c> (optionally
        /// <c>!&lt;rank&gt; &lt;user&gt;</c>) answers with a viewer's rung and how far the
        /// next one is, and <c>!&lt;ranks&gt;</c> prints the ladder leaderboard.
        ///
        /// ★ Neither verb is <c>!top</c>. Loyalty owns that word and its provider runs
        /// FOUR slots earlier in a first-handled-wins dispatch that logs nothing, so a
        /// Ranks <c>!top</c> would simply never be reached — the failure would look like
        /// the tool being broken.
        ///
        /// A bare <c>!rank</c> EVALUATES the caller (the only chat-side evaluation trigger),
        /// so someone who just earned their way up sees the truth on the line they asked on
        /// rather than at the next tick.
        ///
        /// ★ <c>!rank &lt;someone else&gt;</c> is a READ, and only a read. The argument is
        /// raw chat text from any viewer, so evaluating it would mean any viewer could name
        /// anything — <c>!rank Everyone go to evil.example.com</c> — and have the Hub write
        /// that string into the open Ranks table, fire Rank.OnRankUp for it, POST it to chat
        /// as a rank-up announcement, and add it as a member of a User-Management group.
        /// Repeatable once per cooldown, per viewer, forever. So the third-party branch
        /// never evaluates, and the name must first look like a chat login at all
        /// (<see cref="IsPlausibleLogin"/>).
        /// </summary>
        public async Task<RankChatResult> TryHandleChatAsync(ChatMessage msg)
        {
            var cfg = _config;
            if (!cfg.Enabled || !cfg.ChatEnabled || msg is null) return RankChatResult.NotHandled;

            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return RankChatResult.NotHandled;

            string body = text.Substring(1);
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body.Substring(0, sp)).Trim();
            if (token.Length == 0) return RankChatResult.NotHandled;
            string rest = sp < 0 ? string.Empty : body.Substring(sp + 1).Trim();

            // ChatVerb.Matches canonicalizes the CONFIGURED side (strips a leading
            // '!', which `token` never has) and keeps the empty-never-matches rule
            // the local Eq used to carry. Every built-in provider compares through
            // it, so a configured "!rank" behaves identically here and everywhere
            // else — see ChatVerb for the whole rationale.
            bool isRank = ChatVerb.Matches(cfg.RankCommand, token);
            bool isTop = ChatVerb.Matches(cfg.TopCommand, token);
            if (!isRank && !isTop) return RankChatResult.NotHandled;

            // Roles resolve off the SAME EffectiveRoles overlay every other built-in tool
            // gate consults — a group-granted Mod/VIP/Sub passes like the platform rank,
            // and the Regular tick resolves off the User-Management group store, which is
            // also where this tool's own grants land.
            var eff = UserManagementService.Instance.Effective(msg);
            var roles = cfg.ViewRoles ?? RankRoles.All();
            if (!roles.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular))
                return RankChatResult.Silent();

            string caller = Normalize(!string.IsNullOrWhiteSpace(msg.Login) ? msg.Login : msg.Username);
            if (!CooldownOk(caller, cfg.CooldownSeconds)) return RankChatResult.Silent();

            string unit = Unit(cfg);

            if (isTop)
            {
                var board = await TopAsync(cfg.LeaderboardSize).ConfigureAwait(false);
                if (board.Count == 0) return RankChatResult.Say(cfg.TopEmptyMessage ?? "");
                return RankChatResult.Say((cfg.TopReplyMessage ?? "")
                    .Replace("{count}", board.Count.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                    .Replace("{list}", BoardCsv(board), StringComparison.OrdinalIgnoreCase));
            }

            // "!rank someone_else" reads that viewer; a bare "!rank" reads the caller.
            bool thirdParty = rest.Length > 0;

            string targetLogin, targetDisplay;
            if (thirdParty)
            {
                // The argument is arbitrary text a viewer typed. Gate it on looking like a
                // chat login BEFORE it becomes a databank key or a name in a chat line —
                // see the ★ note on this method for what the ungated version handed out.
                string raw = rest.TrimStart('@').Trim();
                if (!IsPlausibleLogin(raw)) return RankChatResult.Silent();
                targetDisplay = raw;                       // preserve the casing they typed
                targetLogin = raw.ToLowerInvariant();
            }
            else
            {
                targetLogin = caller;
                targetDisplay = (msg.Username ?? "").Trim();
                if (targetLogin.Length == 0) return RankChatResult.Silent();
            }

            long value = await ValueAsync(targetLogin).ConfigureAwait(false);
            var (current, next) = ResolveStanding(cfg.Ranks, value);
            long needed = next is null ? 0 : Math.Max(0, next.Threshold - value);

            // Evaluate AFTER reading, so the reply describes the same instant the ladder was
            // resolved from and a rank-up announcement lands beside — not before — the answer
            // the viewer asked for.
            //
            // ★ ONLY for the caller. A lookup about somebody else is a pure read: the tool's
            // whole side-effecting half (remember the rank, announce it, grant the rung's
            // User-Management group) must never be reachable by typing a name into chat. The
            // asked-about viewer loses nothing — the watch-time tick evaluates every active
            // viewer anyway, and their own !rank still evaluates them.
            if (!thirdParty)
                await EvaluateAsync(targetLogin, targetDisplay).ConfigureAwait(false);

            // Four branches for four ladder states, and the fourth is not decoration: with
            // NO reachable rung — a freshly enabled tool whose ladder is still empty — both
            // current and next are null, and the unranked template renders as
            // "majo has 0 minutes — 0 more to reach ." A dangling sentence about a rank with
            // no name reads as a broken tool rather than as an unfinished one.
            string template =
                  current is not null && next is not null ? (cfg.RankReplyMessage ?? "")
                : current is not null                     ? (cfg.TopRankReplyMessage ?? "")
                : next is not null                        ? (cfg.UnrankedReplyMessage ?? "")
                                                          : (cfg.NoLadderReplyMessage ?? "");
            return RankChatResult.Say(FormatReply(
                template, targetDisplay, current?.Name ?? "", value, unit, next?.Name ?? "", needed));
        }

        // A chat login is [A-Za-z0-9_], 25 characters at most, on every platform this Hub
        // speaks to. Deliberately a WHITELIST rather than a blacklist of dangerous
        // characters: the accepted string goes on to key an OPEN databank table, to be
        // matched against User-Management group members, and to be printed back into chat,
        // and a blacklist only ever excludes the attacks somebody already thought of.
        // Rejecting is SILENT — a mistyped name is a repeatable rejection, and those get no
        // chat line by house rule.
        private const int MaxLoginLength = 25;

        internal static bool IsPlausibleLogin(string? login)
        {
            if (string.IsNullOrEmpty(login) || login!.Length > MaxLoginLength) return false;
            foreach (char c in login)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Reply-template substitution. Tokens: {rank} {value} {unit} {next}
        /// {needed} and — LAST, because a display name is external input and a name
        /// containing a literal "{value}" must not get a second pass — {user}.</summary>
        internal static string FormatReply(
            string template, string user, string rank, long value, string unit, string next, long needed)
        {
            if (string.IsNullOrEmpty(template)) return "";
            return template
                .Replace("{rank}", rank ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{value}", value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Replace("{unit}", unit ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{next}", next ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{needed}", needed.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Replace("{user}", user ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // The chat leaderboard is CAPPED for the same reason the viewer queue's is:
        // SendTwitchChatCore DROPS a message over Twitch's 500-character limit outright —
        // one CriticalError and no reply at all — and a display name arrives from chat with
        // no length guarantee. LeaderboardSize bounds the row count; this bounds the
        // characters those rows may spend, leaving the rest of the 500 to the streamer's
        // own template around {list}. The first entry always prints, however long it is: a
        // reply that names nobody is no better than the dropped message this prevents.
        private const int BoardMaxChars = 300;

        internal static string BoardCsv(IReadOnlyList<RankStanding> board)
        {
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var s in board)
            {
                string entry = s.Position.ToString(CultureInfo.InvariantCulture) + ". " + s.Name +
                               (string.IsNullOrEmpty(s.Rank) ? "" : " (" + s.Rank + ")");
                if (shown > 0 && sb.Length + 2 + entry.Length > BoardMaxChars) break;
                if (shown > 0) sb.Append(", ");
                sb.Append(entry);
                shown++;
            }
            int hidden = board.Count - shown;
            if (hidden > 0)
                sb.Append(" and ").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append(" more");
            return sb.ToString();
        }

        private bool CooldownOk(string user, int cooldownSeconds)
        {
            if (cooldownSeconds <= 0) return true;
            long now = _mono.ElapsedMilliseconds;
            lock (_cdGate)
            {
                if (_userCdMs.TryGetValue(user, out long end) && now < end) return false;
                _userCdMs[user] = now + cooldownSeconds * 1000L;
                SweepExpiredCooldownsLocked(now);
                return true;
            }
        }

        // Caller holds _cdGate. See CooldownMapSweepThreshold for why this exists at all.
        // Runs only past the ceiling, so an ordinary chat never pays for the walk, and it
        // drops ONLY entries whose cooldown has already elapsed — a live cooldown is never
        // evicted, because that would silently grant a free re-ask.
        private void SweepExpiredCooldownsLocked(long now)
        {
            if (_userCdMs.Count <= CooldownMapSweepThreshold) return;

            List<string>? stale = null;
            foreach (var kv in _userCdMs)
                if (kv.Value <= now) (stale ??= new List<string>()).Add(kv.Key);
            if (stale is null) return;
            foreach (var k in stale) _userCdMs.Remove(k);
        }

        /// <summary>Test seam — the live per-user cooldown entry count, so the suite can
        /// assert the sweep actually reclaims instead of trusting the ceiling.</summary>
        internal int CooldownEntryCountForTests
        {
            get { lock (_cdGate) return _userCdMs.Count; }
        }

        // ── Overlay Live Channel ────────────────────────────────────────────

        // Provenance tag on every rank key. Identical on EVERY publish by design: the store
        // inherits a declared ExpectedInterval across writes only while the Source string
        // matches, so a second spelling here would let one publish silently drop the cadence
        // and the keys could never report Stale.
        private const string LiveSource = "tool:Ranks";

        /// <summary>
        /// The publish cadence, and the ExpectedInterval every <c>rank.*</c> key declares.
        ///
        /// ★ A live-channel key MUST declare a cadence to be honest: the store has no
        /// remove API and no TTL, so a key published once reports Active for the rest of
        /// the session — a ladder abandoned by a switched-off tool or a wedged Hub would go
        /// on painting as live. Declaring the interval is only half of it; something has to
        /// keep publishing, or a perfectly current ladder would report Stale within
        /// seconds. Hence the heartbeat. The store COALESCES an identical value (refreshes
        /// LastWriteUtc, does not dirty the key, ships no frame), so a ladder nobody is
        /// climbing costs one top-N query and two dictionary writes per tick, and nothing
        /// on the wire.
        ///
        /// 15 s because a rank ladder moves on the watch-time tick (minutes apart) or on a
        /// points change — there is no countdown to paint, so a faster cadence would buy
        /// nothing but databank traffic.
        /// </summary>
        internal static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Every key this tool publishes, paired with the value its RETRACTION writes. The
        /// retraction WALKS this table (see <see cref="PublishOverlayAsync"/>), so a new key
        /// added to the publish and forgotten here really is the one left painting a ladder
        /// the streamer switched off — the property this list is here to hold.
        ///
        /// The tombstone shape differs per key and that is deliberate, which is why this is
        /// a table rather than a bare name list. <c>rank.leaderboard</c> is consumed by a
        /// List.Live widget, which ITERATES its value: an empty array makes that widget draw
        /// nothing, where a JSON null makes it draw whatever a missing array draws. The two
        /// scalars have no such consumer and tombstone as null, which is also what the
        /// sibling PollsService writes for every one of its keys.
        /// </summary>
        private static readonly (string Key, bool IsArray)[] LiveKeys =
        {
            ("rank.leaderboard", true),
            ("rank.metric",      false),
            ("rank.unit",        false),
        };

        private readonly object _loopGate = new();
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private bool _loopStarted;
        // Whether the last publish painted a ladder. Drives the ONE retraction write on the
        // way down; while false the tool writes nothing at all, so a Hub whose Ranks tool
        // was never enabled leaves every rank.* key Missing rather than Active-and-empty.
        private bool _overlayWasOn;
        // The heartbeat and the config-edit republish both publish, so they are serialized:
        // two overlapping reads could otherwise land out of order and leave the channel
        // holding the OLDER board until the next tick corrected it. One publisher at a time
        // also makes _overlayWasOn a plain field rather than a race.
        private readonly SemaphoreSlim _publishGate = new(1, 1);

        private void StartOverlayPump()
        {
            lock (_loopGate)
            {
                if (_loopStarted) return;
                _loopStarted = true;
                _loopCts = new CancellationTokenSource();
                var ct = _loopCts.Token;
                _loopTask = Task.Run(() => OverlayLoopAsync(ct));
            }
        }

        private async Task OverlayLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(LiveInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                // A bg-thread throw must never kill the process.
                try { await PublishOverlayAsync().ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("RanksService", "overlay publish failed", ex); }
            }
        }

        /// <summary>
        /// Publishes the ladder under the <c>rank.</c> root. THIS KEY LIST IS THE CONTRACT
        /// a Var.Live / List.Live widget graph binds against — the browser derives its
        /// subscription from literal key text, so a rename here is a silently blank widget
        /// with no error anywhere:
        ///
        ///   rank.leaderboard  array of { position, name, value, rank }
        ///   rank.metric       "watchtime" | "points"
        ///   rank.unit         the {unit} word ("minutes", or the currency noun)
        ///
        /// The array shape is what List.Live reads: its default Format is
        /// <c>"{index}. {name}"</c> and its default Field is <c>name</c>, so a widget that
        /// binds the key and changes nothing else already prints a numbered ladder.
        ///
        /// ★ RETRACTION. Switching the tool (or just the overlay) off publishes an EMPTY
        /// array and a null on the two scalars, then goes quiet — so the board reads empty
        /// immediately AND the keys decay to Stale. Publishing nothing at all would have
        /// been the bug: the store has no remove API, so the last frame would sit in OBS
        /// painting a ladder that no code path can update again.
        /// </summary>
        internal async Task PublishOverlayAsync()
        {
            await _publishGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cfg = _config;
                var store = LiveStore;
                if (!cfg.Enabled || !cfg.OverlayEnabled)
                {
                    if (!_overlayWasOn) return;
                    _overlayWasOn = false;
                    // Walks LiveKeys rather than naming the three keys again — a second
                    // hand-written list is exactly how a key gets added to the publish and
                    // missed here. A fresh JsonArray per entry, never a shared instance: a
                    // JsonNode carries a parent once it is stored.
                    foreach (var (key, isArray) in LiveKeys)
                        store.Publish(key, isArray ? new JsonArray() : null, LiveSource, LiveInterval);
                    return;
                }

                var board = await TopAsync(cfg.OverlaySize).ConfigureAwait(false);
                var array = new JsonArray();
                foreach (var s in board)
                {
                    array.Add(new JsonObject
                    {
                        ["position"] = s.Position,
                        ["name"] = s.Name,
                        ["value"] = s.Value,
                        ["rank"] = s.Rank,
                    });
                }
                store.Publish("rank.leaderboard", array, LiveSource, LiveInterval);
                store.PublishString("rank.metric", RankMetrics.Normalize(cfg.Metric), LiveSource, LiveInterval);
                store.PublishString("rank.unit", Unit(cfg), LiveSource, LiveInterval);
                _overlayWasOn = true;
            }
            finally { _publishGate.Release(); }
        }

        // Test seam: the suite must be able to assert the retraction without waiting out a
        // 15-second heartbeat, and LiveKeys is otherwise unreachable from outside.
        internal static IReadOnlyList<string> LiveKeysForTests { get; } =
            Array.ConvertAll(LiveKeys, k => k.Key);
    }
}
