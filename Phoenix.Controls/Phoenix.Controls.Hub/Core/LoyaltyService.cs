using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Loyalty — the Hub-runtime brain of the viewer points economy (sibling of the
    // Timer and Giveaway pre-build tools). Owns the rules: the watch-time earn tick,
    // the passive event→points map, the five minigames, and the reward store — on
    // top of the exploit-safe atomic persistence in DB.Loyalty.cs. Mirrors
    // TimerService/GiveawayService: a singleton over DB.Instance, crash-proof
    // SafeEvent change events, seams the Hub bootstrap wires in.
    //
    // MONEY DOCTRINE: every balance change goes through a DB.Instance.Loyalty*
    // atomic method — the service NEVER reads-then-writes a balance itself. The DB
    // layer guarantees no negative bet / no overspend regardless of game logic, so
    // this file only rolls the RNG, resolves config, and calls the settle method.
    //
    // MASTER GATE: Config.Enabled == false means fully dormant — every earn tick,
    // command, game and redemption early-returns. OFF by default.
    //
    // Pillar rule: this lives in the Hub runtime — the only process that touches the
    // DB and executes logic. Architect/Visualist never reference it.
    //
    // This class is split across three files: LoyaltyService.cs (lifecycle / config /
    // admin ops / reward store / overlay), LoyaltyService.Earn.cs (the earn engine),
    // and LoyaltyService.Games.cs (the five minigames).
    public sealed partial class LoyaltyService
    {
        // ★ The databank is resolved LIVE for the shared instance (null injected
        // DB ⇒ read DB.Instance per call) and pinned only when a test injects its
        // own. Pinning DB.Instance at construction was the mechanism behind the
        // suite's documented LoyaltyRaffleCancelTests flake: a test class that
        // disposes + re-initializes DB.Instance (the CounterDbOpenTableTests
        // isolation idiom) left the already-constructed Loyalty singleton holding
        // the DISPOSED databank — every later money call threw
        // ObjectDisposedException from the dead SemaphoreSlim. Production is
        // unaffected: the Hub never disposes DB.Instance, so live-resolve and
        // pin-at-construction are indistinguishable there.
        private readonly DB? _injectedDb;
        private DB Db => _injectedDb ?? DB.Instance;
        private readonly IRandomSource _rng;

        /// <summary>Constructs the service over a databank and an RNG seam
        /// (defaults to the shared thread-safe production RNG; tests inject a
        /// deterministic stub so the games are reproducible). A null
        /// <paramref name="db"/> means "always the live <see cref="DB.Instance"/>"
        /// — the shared <see cref="Instance"/> uses that form.</summary>
        public LoyaltyService(DB? db, IRandomSource? rng = null)
        {
            _injectedDb = db;
            _rng = rng ?? DefaultRandomSource.Instance;
        }

        // Shared instance over the singleton DB. Every front-end (the loyalty.*
        // script commands and the Hub Loyalty page) resolves THIS instance so they
        // observe the same change events. Mirrors DB.Instance / TimerService.Instance.
        private static LoyaltyService? _instance;
        private static readonly object _instanceGate = new();
        /// <summary>The shared Loyalty runtime instance (live over DB.Instance).</summary>
        public static LoyaltyService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new LoyaltyService(db: null);
            }
        }

        // ── Live config ─────────────────────────────────────────────────────
        // Replaced wholesale by UpdateConfigAsync (reference assignment is atomic;
        // volatile makes the swap visible across the earn/game threads). The service
        // never mutates a sub-config in place.
        private volatile LoyaltyConfig _config = new();

        /// <summary>The live configuration — never null (defaults until loaded).</summary>
        public LoyaltyConfig Config => _config;

        /// <summary>Master gate. Every earn/command/game/redeem entry point
        /// early-returns while this is false.</summary>
        public bool Active => _config.Enabled;

        // ── Seams (wired from HubBootstrapper / ScriptManager; null-safe) ────
        /// <summary>
        /// The active-viewer list the watch-time payout tick sweeps. This service never
        /// talks to Streamer.bot itself — and it does not own the fetch either: what the
        /// Hub wires here may be a SHARED presence sample that several consumers read, so
        /// the tick must treat the answer as borrowed, read-only data rather than a
        /// round-trip made on its own behalf. Returns an empty list while Streamer.bot is
        /// disconnected.
        /// </summary>
        public Func<Task<IReadOnlyList<ActiveViewer>>>? ActiveViewersProvider { get; set; }

        /// <summary>Live Hub Bot Accounts list (lowercased logins) — excluded from
        /// every earn path so bots never accrue points.</summary>
        public Func<IReadOnlyCollection<string>>? BotAccountsProvider { get; set; }

        /// <summary>
        /// The Overlay Live Channel this service publishes <c>loyalty.leaderboard</c> and
        /// <c>loyalty.currency</c> into. Defaults to the process-wide store, so the bespoke
        /// LOYALTY_UPDATE broadcast seam it replaced no longer needs wiring from the Hub
        /// bootstrapper.
        ///
        /// Public get / internal set mirrors <c>LayerRuntime.Registry</c>: production cannot
        /// swap the channel at runtime, while the test assembly (<c>InternalsVisibleTo</c>)
        /// hands each test its OWN store rather than sharing one.
        /// </summary>
        public OverlayLiveStore LiveStore { get; internal set; } = OverlayLiveStore.Instance;

        /// <summary>(busType, jsonPayload) → Bus broadcast (LOYALTY_REDEEM / …).</summary>
        public Action<string, string>? BusEmit { get; set; }

        /// <summary>(phoenixEventType, vars) → ScriptManager generic-event dispatch
        /// (Loyalty.OnEarn / Loyalty.OnPayout / Loyalty.OnRedeem).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>(layerId, triggerName) → the shared VISUAL_TRIGGER pipeline;
        /// reward redemption rides it so a reward effect is authored in Visualist
        /// like any other. The Hub wires it; the service just calls it.</summary>
        public Func<string, string, Task>? FireVisualTrigger { get; set; }

        // ── Change events (UI) ──────────────────────────────────────────────
        /// <summary>Raised when the configuration changes (load / UpdateConfig).</summary>
        public event EventHandler? ConfigChanged;
        /// <summary>Raised after any balance-changing operation.</summary>
        public event EventHandler? BalancesChanged;
        /// <summary>Raised with a short human-readable line for the activity feed.</summary>
        public event EventHandler<string>? Activity;

        private void RaiseConfigChanged() => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "LoyaltyService", "ConfigChanged");
        private void RaiseBalances() => SafeEvent.Raise(BalancesChanged, this, EventArgs.Empty, "LoyaltyService", "BalancesChanged");

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "Loyalty";

        /// <summary>
        /// The feed's kind token. <c>PAY</c> is reserved for the watch-time sweep's
        /// payout line — the one emit site that is a scheduled disbursement rather than
        /// a discrete event (<c>LoyaltyService.Earn</c>: "watch-time payout — N
        /// viewer(s), T &lt;currency&gt;"). No other sentence raised here starts with
        /// that prefix, so matching on it keeps the classification honest; everything
        /// else is <c>INF</c>.
        /// <para>Internal rather than private so the suite can pin the classification
        /// without reaching a Hub.WinUI ViewModel, which the test host cannot load.</para>
        /// </summary>
        internal static string ActivityKindFor(string? message)
            => (message ?? string.Empty).StartsWith("watch-time payout", StringComparison.OrdinalIgnoreCase)
                ? "PAY"
                : "INF";

        /// <summary>
        /// The ONE funnel every activity line goes through — the ~24 emit sites (admin
        /// adjust, transfer, spend, refund, wipe, wallet merge, redemption, watch-time
        /// payout, event earn, every minigame settle, both raffle notices) all reach the
        /// feed from here, via <see cref="AfterBalanceChangeAsync"/> or directly.
        ///
        /// <para>★ The ring write lives HERE, at the source, exactly like the nine
        /// sibling services. It used to live in <c>LoyaltyViewModel.OnActivity</c>, and
        /// that VM is constructed lazily when the Loyalty tab is first opened — so an
        /// unopened tab recorded NOTHING, and hours of earns, spends and redemptions
        /// showed up as an empty feed under a card labelled "this session". Recording
        /// here fills the ring whether or not the tab was ever opened. The VM's own
        /// <c>Ring.Record</c> was deleted in the same edit; re-adding it would record
        /// every line twice.</para>
        ///
        /// <para>Observation only: the ring write is wrapped so a fault in the feed can
        /// never become a fault in a money path.</para>
        /// </summary>
        private void RaiseActivity(string message)
        {
            try { ToolActivityRing.Record(ActivityTool, ActivityKindFor(message), message ?? string.Empty); }
            catch (Exception ex) { GlobalLogger.Error("LoyaltyService", "activity record failed", ex); }
            SafeEvent.Raise(Activity, this, message, "LoyaltyService", "Activity");
        }

        // ── Live state ──────────────────────────────────────────────────────
        // Broadcaster-live tracking (StreamOnline/Offline). Default true so a
        // manually-tested payout counts even off-air; OnlineOnly earn only freezes
        // once we actually see the stream go offline.
        private volatile bool _streamLive = true;

        // Monotonic clock for every cooldown — never wall clock, so an NTP step or
        // DST change can't unblock a cooldown early.
        private readonly Stopwatch _mono = Stopwatch.StartNew();
        private long NowMs => _mono.ElapsedMilliseconds;

        // Generic cooldown maps, shared by the games and the reward store. Keys are
        // namespaced ("gamble", "reward\0<id>") so scopes never collide.
        private readonly object _cdGate = new();
        private readonly Dictionary<string, long> _userCdMs = new(StringComparer.Ordinal);   // "<scope>\0<user>" -> end ms
        private readonly Dictionary<string, long> _globalCdMs = new(StringComparer.Ordinal);  // "<scope>" -> end ms

        // Per-stream reward redemption counts (reward key -> count). Reset when the
        // stream goes live.
        private readonly Dictionary<string, int> _rewardRedeemed = new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string Normalize(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

        // Resolve the ledger table only when logging is enabled; the DB layer also
        // validates the identifier and no-ops a system/invalid table.
        private static string? LedgerTableIfEnabled(LoyaltyConfig cfg)
            => cfg.Currency.LedgerEnabled ? cfg.Currency.LedgerTable : null;

        // ── Lifecycle ───────────────────────────────────────────────────────
        /// <summary>Loads the persisted config (defaults on null/parse-fail) and
        /// ensures the configured OPEN balance + ledger tables exist.</summary>
        public async Task InitializeAsync()
        {
            LoyaltyConfig cfg;
            try
            {
                string? json = await Db.LoadLoyaltyConfigAsync().ConfigureAwait(false);
                cfg = Deserialize(json);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LoyaltyService", "InitializeAsync: config load failed", ex);
                cfg = new LoyaltyConfig();
            }
            _config = cfg;

            try
            {
                await Db.EnsureLoyaltyWalletTablesAsync(
                    cfg.Currency.BalanceTable, cfg.Currency.LedgerTable, cfg.Currency.LedgerEnabled).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LoyaltyService", "InitializeAsync: wallet-table ensure failed", ex);
            }

            // Seed the Overlay Live Channel with the leaderboard as it already stands.
            // AfterBalanceChangeAsync is otherwise the ONLY publisher, so without this the
            // channel is empty from Hub start until the next balance-changing event — and a
            // quiet economy (nobody redeems, earn ticks off or off-air) means indefinitely.
            // The browser's honest-data rule renders an unpublished key as NOTHING rather
            // than the old fake "1. viewer_one — 12,400" mock, so the missing seed shows up
            // as a permanently blank leaderboard rather than merely stale-looking data.
            //
            // Placed AFTER the wallet-table ensure on purpose: the publish reads
            // LoyaltyTopAsync off the configured balance table, which the ensure above is
            // what guarantees exists. Same call the change path uses, so the array shape,
            // the provenance tag and BOTH gates (tool-Active + Overlay.Enabled) are shared
            // rather than re-stated; it swallows its own faults, and publishing into a
            // channel nobody subscribes to costs one dictionary write.
            //
            // NOTE the gates are NOT symmetric between the two callers and that asymmetry is
            // why the Active gate lives inside PublishLiveChannelAsync rather than here: the
            // change path is already unreachable while !Active (every mutation returns early
            // on it), so it never needed one — this seed is the FIRST caller that can run with
            // the tool switched off, and Overlay.Enabled alone would not stop it because that
            // one defaults TRUE while LoyaltyConfig.Enabled defaults FALSE.
            await PublishLiveChannelAsync().ConfigureAwait(false);
            // Seed the edge detector from the state we just acted on, so the FIRST config
            // save of the session compares against reality rather than against `false`
            // (which would make an ordinary save look like a rising edge and re-publish,
            // or a disable look like a no-op and skip the retraction).
            _liveChannelPublishing = cfg.Enabled && cfg.Overlay.Enabled;

            GlobalLogger.Log(
                $"LoyaltyService initialised — enabled={cfg.Enabled}, currency=\"{cfg.Currency.NamePlural}\", table=\"{cfg.Currency.BalanceTable}\".",
                "LoyaltyService", LogLevel.System);
            RaiseConfigChanged();
        }

        /// <summary>Cancels the earn tick, resolves any open raffle, and flushes the
        /// config to the databank.</summary>
        public async Task ShutdownAsync()
        {
            await StopEarningAsync().ConfigureAwait(false);

            try { await ResolveOpenRaffleOnShutdownAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("LoyaltyService", "ShutdownAsync: raffle resolve failed", ex); }

            try { await SaveConfigAsync().ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("LoyaltyService", "ShutdownAsync: config flush failed", ex); }

            GlobalLogger.Log("LoyaltyService shut down.", "LoyaltyService", LogLevel.System);
        }

        /// <summary>Broadcaster live state, from *.StreamOnline / *.StreamOffline.
        /// Going live resets the per-stream follow-dedupe and reward-quantity
        /// counters (a fresh stream is a fresh economy window).</summary>
        public void SetStreamLive(bool live)
        {
            if (_streamLive == live) return;
            _streamLive = live;
            if (live)
            {
                lock (_followGate) _followedThisStream.Clear();
                lock (_cdGate) _rewardRedeemed.Clear();
            }
            GlobalLogger.Log($"LoyaltyService: stream {(live ? "ONLINE" : "OFFLINE")}.", "LoyaltyService", LogLevel.System);
        }

        private static LoyaltyConfig Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new LoyaltyConfig();
            try { return JsonSerializer.Deserialize<LoyaltyConfig>(json, JsonOpts) ?? new LoyaltyConfig(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("LoyaltyService", "config parse failed — using defaults", ex);
                return new LoyaltyConfig();
            }
        }

        // ── Config writes ───────────────────────────────────────────────────
        /// <summary>Replaces the live config, persists it, re-ensures the wallet
        /// tables if the balance/ledger table names (or ledger toggle) changed, and
        /// raises <see cref="ConfigChanged"/>.</summary>
        public async Task UpdateConfigAsync(LoyaltyConfig newConfig)
        {
            if (newConfig is null) return;
            var old = _config;
            _config = newConfig;

            bool tablesChanged =
                !string.Equals(old.Currency.BalanceTable, newConfig.Currency.BalanceTable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(old.Currency.LedgerTable, newConfig.Currency.LedgerTable, StringComparison.OrdinalIgnoreCase) ||
                old.Currency.LedgerEnabled != newConfig.Currency.LedgerEnabled;
            if (tablesChanged)
            {
                try
                {
                    await Db.EnsureLoyaltyWalletTablesAsync(
                        newConfig.Currency.BalanceTable, newConfig.Currency.LedgerTable, newConfig.Currency.LedgerEnabled).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("LoyaltyService", "UpdateConfigAsync: wallet-table ensure failed", ex);
                }
            }

            // ── Retract on disable, re-seed on enable ───────────────────────
            //
            // Turning the tool (or just its overlay) OFF has to withdraw the keys, not
            // merely stop refreshing them. PublishLiveChannelAsync is gated on both
            // switches, so without this the last leaderboard simply STAYS in the store —
            // and because this family declares no ExpectedInterval, ComputeState can only
            // ever answer Active for it. The widget would keep painting standings that no
            // code path can update again: not stale-looking, just quietly wrong, for the
            // rest of the session.
            //
            // ★ The edge is measured against _liveChannelPublishing, a field this service
            // owns — NOT against `old`. `old` is worthless here: LoyaltyViewModel mutates
            // its working copy IN PLACE and hands the SAME instance back, and a previous
            // save already assigned that instance to _config (the panel depends on the
            // aliasing — it suppresses its own ConfigChanged with ReferenceEquals). So
            // from the second save of a panel session onward `old` IS `newConfig`, the two
            // sides of the comparison read the toggle the user just moved, and the guard
            // could never fire. A guard that compares an object with itself is the exact
            // shape this repo keeps shipping; the fix is to remember the state we last
            // ACTED on rather than trusting a caller-supplied "before" snapshot.
            bool nowPublishing = newConfig.Enabled && newConfig.Overlay.Enabled;
            if (_liveChannelPublishing && !nowPublishing)
            {
                try
                {
                    int retracted = LiveStore.RetractRoot(LiveKeyRoot, LiveSource);
                    if (retracted > 0)
                        GlobalLogger.Log(
                            $"Loyalty overlay disabled — retracted {retracted} live-channel key(s). "
                            + "Widgets bound to them now render nothing rather than the last standings.",
                            "LoyaltyService", LogLevel.System);
                }
                catch (Exception ex)
                {
                    // A retraction failure must not block the config save — the streamer's
                    // switch has already moved and the tool is off either way.
                    GlobalLogger.Error("LoyaltyService", "live-channel retraction on disable failed", ex);
                }
            }
            else if (!_liveChannelPublishing && nowPublishing)
            {
                // ★ The inverse, and it is not optional. Retraction tombstones the keys to
                // JSON null; the only other publisher is AfterBalanceChangeAsync, so on a
                // quiet economy (nobody redeeming, earn off or off-air) nothing would ever
                // republish and the widget would stay BLANK for the rest of the session
                // with the tool switched back ON — the same "indefinitely" hole the
                // startup seed in InitializeAsync exists to close, re-opened mid-session.
                await PublishLiveChannelAsync().ConfigureAwait(false);
            }
            _liveChannelPublishing = nowPublishing;

            await SaveConfigAsync().ConfigureAwait(false);
            RaiseConfigChanged();
        }

        /// <summary>Persists the current config JSON to the databank.</summary>
        public async Task SaveConfigAsync()
        {
            var cfg = _config;
            try
            {
                string json = JsonSerializer.Serialize(cfg, JsonOpts);
                await Db.SaveLoyaltyConfigAsync(json, NowUnixMs()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LoyaltyService", "SaveConfigAsync failed", ex);
            }
        }

        // ── Reads (always safe, no master gate) ─────────────────────────────
        /// <summary>The user's current balance (0 when missing).</summary>
        public Task<long> GetBalanceAsync(string user)
            => Db.LoyaltyGetBalanceAsync(_config.Currency.BalanceTable, Normalize(user));

        /// <summary>Top-N standings by balance (rank starts at 1).</summary>
        public Task<List<LoyaltyStanding>> TopAsync(int n)
            => Db.LoyaltyTopAsync(_config.Currency.BalanceTable, n);

        // NOT a read, despite sitting above the Active-gated block: this one WRITES, and it
        // lives here because it is un-gated like the reads rather than gated like its fellow
        // mutations. See its remarks.
        /// <summary>
        /// Opt-in repair for the duplicate rows the old display-name-keyed wallet left
        /// behind: folds <paramref name="from"/>'s row into <paramref name="to"/> and
        /// removes the source. Returns the applied folds (empty when the source has no
        /// row, or when the two names collate to the SAME row — every wallet lookup is
        /// NOCASE, so "merging" that would delete the wallet outright).
        ///
        /// <para>Deliberately NOT called from anywhere automatic. The balance table is an
        /// OPEN table the streamer owns and their own <c>db.*</c> scripts write to, and
        /// nothing in the suite can map a display name back to a login — so the pairing is
        /// the streamer's call, made from the Loyalty panel. Each fold is written to the
        /// ledger under <paramref name="byUser"/> like any other administrative move.</para>
        ///
        /// <para>A fold that lands fans out through <see cref="AfterBalanceChangeAsync"/>
        /// exactly like every other mutation: it deletes one wallet row and moves its whole
        /// balance, so leaving it out would strand the panel showing BOTH rows and the
        /// overlay showing the pre-merge standings until some unrelated balance change
        /// happened to republish — which on a quiet economy is the rest of the session.
        /// This is the one mutation that is NOT <see cref="Active"/>-gated (the repair has
        /// to be pressable with the tool switched off); the publish half is gated inside
        /// <c>PublishLiveChannelAsync</c> instead, so a merge made while disabled still
        /// refreshes the panel and still publishes nothing.</para>
        /// </summary>
        public async Task<List<LoyaltyWalletMerge>> MergeWalletsAsync(string from, string to, string byUser = "panel")
        {
            var cfg = _config;
            var merged = await Db.LoyaltyMergeWalletsAsync(
                cfg.Currency.BalanceTable,
                new[] { (From: Normalize(from), To: Normalize(to)) },
                LedgerTableIfEnabled(cfg),
                byUser).ConfigureAwait(false);

            if (merged.Count > 0)
            {
                var m = merged[0];
                await AfterBalanceChangeAsync(
                    $"{byUser} merged {m.FromName} ({m.FromBalance}) into {m.ToName} — now {m.ToBalance} {cfg.Currency.NamePlural}")
                    .ConfigureAwait(false);
            }
            return merged;
        }

        /// <summary>Total viewers holding a balance row — the real tracked
        /// count, not capped by any top-N page.</summary>
        public Task<long> CountViewersAsync()
            => Db.LoyaltyCountViewersAsync(_config.Currency.BalanceTable);

        /// <summary>Recent banking-ledger rows, newest first.</summary>
        public Task<List<LoyaltyLedgerEntry>> LedgerAsync(int limit)
            => Db.LoyaltyLedgerRecentAsync(_config.Currency.LedgerTable, limit);

        // ── Admin / mutation ops (no-op-return when !Active) ────────────────
        /// <summary>Credits a user (admin add). Rejects while disabled.</summary>
        public async Task<LoyaltyResult> AddAsync(string user, long amount, string byWhom)
        {
            if (!Active) return LoyaltyResult.Fail(LoyaltyOutcome.Disabled);
            var cfg = _config;
            string u = Normalize(user);
            var res = await Db.LoyaltyCreditAsync(cfg.Currency.BalanceTable, u, amount,
                LedgerTableIfEnabled(cfg), Normalize(byWhom), "addpoints").ConfigureAwait(false);
            if (res.Ok) await AfterBalanceChangeAsync($"{byWhom} gave {u} {amount} {cfg.Currency.NamePlural}").ConfigureAwait(false);
            return res;
        }

        /// <summary>Sets a user's balance to an absolute value (admin override).</summary>
        public async Task<LoyaltyResult> SetAsync(string user, long value, string byWhom)
        {
            if (!Active) return LoyaltyResult.Fail(LoyaltyOutcome.Disabled);
            var cfg = _config;
            string u = Normalize(user);
            var res = await Db.LoyaltySetAsync(cfg.Currency.BalanceTable, u, value,
                LedgerTableIfEnabled(cfg), Normalize(byWhom), "setpoints").ConfigureAwait(false);
            if (res.Ok) await AfterBalanceChangeAsync($"{byWhom} set {u} to {value} {cfg.Currency.NamePlural}").ConfigureAwait(false);
            return res;
        }

        /// <summary>Debits a user (admin remove); floors at 0 — removing more than
        /// the user holds zeroes them rather than failing.</summary>
        public async Task<LoyaltyResult> RemoveAsync(string user, long amount, string byWhom)
        {
            if (!Active) return LoyaltyResult.Fail(LoyaltyOutcome.Disabled);
            var cfg = _config;
            string u = Normalize(user);
            var res = await Db.LoyaltyDebitAsync(cfg.Currency.BalanceTable, u, amount,
                LedgerTableIfEnabled(cfg), Normalize(byWhom), "removepoints").ConfigureAwait(false);
            // Admin remove floors at 0: NoFunds means the balance was below `amount`,
            // so zero it out (still one atomic op — never a read-then-write).
            if (res.Outcome == LoyaltyOutcome.NoFunds)
                res = await Db.LoyaltySetAsync(cfg.Currency.BalanceTable, u, 0,
                    LedgerTableIfEnabled(cfg), Normalize(byWhom), "removepoints (floored)").ConfigureAwait(false);
            if (res.Ok) await AfterBalanceChangeAsync($"{byWhom} removed {amount} from {u}").ConfigureAwait(false);
            return res;
        }

        /// <summary>
        /// Charges a viewer for a PURCHASE — an atomic debit that REFUSES rather than
        /// floors. This is not the same primitive as <see cref="RemoveAsync"/>, and the
        /// difference is money: an admin remove floors at 0 (removing more than the viewer
        /// holds zeroes them and still reports Ok), which for a purchase would take
        /// whatever they had and hand over the goods anyway. <see cref="RedeemAsync"/> is
        /// the other existing debit, but it is reward-catalog-shaped — it needs an entry in
        /// the streamer's rewards list, applies that reward's quantity/cooldowns and raises
        /// Loyalty.OnRedeem — none of which fits a price attached to a tool.
        ///
        /// The Song Request tool's per-request price is the first caller. Returns
        /// <c>Disabled</c> while the tool is off and <c>NoFunds</c> when the balance can't
        /// cover it; the caller is expected to translate both into a chat line rather than
        /// silently proceeding. <paramref name="reason"/> lands in the ledger.
        /// </summary>
        public async Task<LoyaltyResult> ChargeAsync(string user, long amount, string reason)
        {
            if (!Active) return LoyaltyResult.Fail(LoyaltyOutcome.Disabled);
            if (amount <= 0) return LoyaltyResult.Fail(LoyaltyOutcome.Invalid);
            var cfg = _config;
            string u = Normalize(user);
            if (u.Length == 0) return LoyaltyResult.Fail(LoyaltyOutcome.Invalid);

            var res = await Db.LoyaltyDebitAsync(cfg.Currency.BalanceTable, u, amount,
                LedgerTableIfEnabled(cfg), u, reason ?? "purchase").ConfigureAwait(false);
            if (res.Ok) await AfterBalanceChangeAsync($"{u} spent {amount} {cfg.Currency.NamePlural} ({reason})").ConfigureAwait(false);
            return res;
        }

        /// <summary>
        /// Gives back a <see cref="ChargeAsync"/> charge — a plain credit with the refund
        /// named in the ledger, so a streamer auditing the currency can see the round trip
        /// rather than an unexplained grant. Separate from <see cref="AddAsync"/> only
        /// because AddAsync records the actor as an admin doing an "addpoints", which a
        /// refund is not.
        /// </summary>
        public async Task<LoyaltyResult> RefundAsync(string user, long amount, string reason)
        {
            if (!Active) return LoyaltyResult.Fail(LoyaltyOutcome.Disabled);
            if (amount <= 0) return LoyaltyResult.Fail(LoyaltyOutcome.Invalid);
            var cfg = _config;
            string u = Normalize(user);
            if (u.Length == 0) return LoyaltyResult.Fail(LoyaltyOutcome.Invalid);

            var res = await Db.LoyaltyCreditAsync(cfg.Currency.BalanceTable, u, amount,
                LedgerTableIfEnabled(cfg), u, reason ?? "refund").ConfigureAwait(false);
            if (res.Ok) await AfterBalanceChangeAsync($"{u} was refunded {amount} {cfg.Currency.NamePlural} ({reason})").ConfigureAwait(false);
            return res;
        }

        /// <summary>Atomic peer transfer. NewBalance is the sender's post-move balance.</summary>
        public async Task<LoyaltyResult> GiveAsync(string from, string to, long amount)
        {
            if (!Active) return LoyaltyResult.Fail(LoyaltyOutcome.Disabled);
            var cfg = _config;
            string f = Normalize(from), t = Normalize(to);
            if (f.Length == 0 || t.Length == 0 || f == t) return LoyaltyResult.Fail(LoyaltyOutcome.Invalid);
            var res = await Db.LoyaltyTransferAsync(cfg.Currency.BalanceTable, f, t, amount,
                LedgerTableIfEnabled(cfg), "give").ConfigureAwait(false);
            if (res.Ok) await AfterBalanceChangeAsync($"{f} gave {t} {amount} {cfg.Currency.NamePlural}").ConfigureAwait(false);
            return res;
        }

        /// <summary>
        /// Pays a whole batch of viewers in ONE transaction / one lock hold — the primitive
        /// a pot settlement needs. Returns how many credits the money layer actually
        /// applied, which the caller is expected to COMPARE against what it asked for: the
        /// batch reports 0 (rolled back) when the currency table is missing, and skips
        /// blank names / non-positive amounts, so a short count is a real failure and never
        /// an exception the caller can rely on catching.
        ///
        /// Not <see cref="RefundAsync"/> in a loop: N loops are N transactions and N lock
        /// holds, and a fault half-way leaves half a pot paid. Not
        /// <see cref="AwardActiveViewersAsync"/> either — that one derives its own
        /// recipient list from the live viewer list; this one is handed the list.
        /// The Polls tool's pot settlement is the first caller; the raffle predates it and
        /// reaches <c>Db.LoyaltyCreditManyAsync</c> directly from inside this class.
        /// </summary>
        public async Task<int> PayoutManyAsync(IReadOnlyList<(string Name, long Amount)> credits, string reason)
        {
            if (!Active || credits is null || credits.Count == 0) return 0;
            var cfg = _config;
            var normalized = new List<(string, long)>(credits.Count);
            foreach (var (n, amt) in credits)
            {
                string u = Normalize(n);
                if (u.Length == 0 || amt <= 0) continue;
                normalized.Add((u, amt));
            }
            if (normalized.Count == 0) return 0;

            int applied = await Db.LoyaltyCreditManyAsync(cfg.Currency.BalanceTable, normalized,
                LedgerTableIfEnabled(cfg), "payout", reason ?? "payout").ConfigureAwait(false);
            if (applied > 0)
                await AfterBalanceChangeAsync($"{reason} — paid {applied} viewer(s)").ConfigureAwait(false);
            return applied;
        }

        /// <summary>Resets every balance in the table to 0. Returns the row count.</summary>
        public async Task<int> WipeAsync(string byWhom)
        {
            if (!Active) return 0;
            var cfg = _config;
            int rows = await Db.LoyaltyWipeAsync(cfg.Currency.BalanceTable, LedgerTableIfEnabled(cfg), Normalize(byWhom)).ConfigureAwait(false);
            if (rows > 0) await AfterBalanceChangeAsync($"{byWhom} wiped all balances ({rows} row(s))").ConfigureAwait(false);
            return rows;
        }

        /// <summary>Credits a flat amount to every current active viewer (backs
        /// "!addpoints all"). Bots are excluded. Returns the number credited.</summary>
        public async Task<int> AwardActiveViewersAsync(long amount, string byWhom)
        {
            if (!Active || amount <= 0) return 0;
            var cfg = _config;
            IReadOnlyList<ActiveViewer> viewers = await FetchActiveViewersAsync().ConfigureAwait(false);
            if (viewers.Count == 0) return 0;

            var excluded = BuildExclusionSet(cfg);
            var credits = new List<(string, long)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in viewers)
            {
                string name = Normalize(v.Login);
                if (name.Length == 0 || excluded.Contains(name) || !seen.Add(name)) continue;
                credits.Add((name, amount));
            }
            if (credits.Count == 0) return 0;

            int applied = await Db.LoyaltyCreditManyAsync(cfg.Currency.BalanceTable, credits,
                LedgerTableIfEnabled(cfg), Normalize(byWhom), "addpoints all").ConfigureAwait(false);
            if (applied > 0) await AfterBalanceChangeAsync($"{byWhom} gave {amount} {cfg.Currency.NamePlural} to {applied} active viewer(s)").ConfigureAwait(false);
            return applied;
        }

        // ── Reward store ────────────────────────────────────────────────────
        /// <summary>The configured reward catalog.</summary>
        public IReadOnlyList<LoyaltyReward> Rewards => _config.Rewards;

        /// <summary>
        /// Redeems a reward by id or name: validates enabled / per-stream quantity /
        /// cooldowns, atomically debits the cost, then (on success) raises
        /// Loyalty.OnRedeem, rides the shared VISUAL_TRIGGER pipeline when the reward
        /// carries a layer + trigger, and returns an announce line.
        /// </summary>
        public async Task<LoyaltyRedeemResult> RedeemAsync(string user, string rewardIdOrName)
        {
            if (!Active) return LoyaltyRedeemResult.Fail(LoyaltyRedeemOutcome.Inactive);
            var cfg = _config;
            string u = Normalize(user);
            string key = (rewardIdOrName ?? string.Empty).Trim();
            if (u.Length == 0 || key.Length == 0)
                return LoyaltyRedeemResult.Fail(LoyaltyRedeemOutcome.NotFound);

            var reward = cfg.Rewards.FirstOrDefault(r =>
                (!string.IsNullOrEmpty(r.Id) && r.Id.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(r.Name) && r.Name.Equals(key, StringComparison.OrdinalIgnoreCase)));
            if (reward is null) return LoyaltyRedeemResult.Fail(LoyaltyRedeemOutcome.NotFound);
            if (!reward.Enabled) return LoyaltyRedeemResult.Fail(LoyaltyRedeemOutcome.Disabled, "reward disabled");

            string rid = !string.IsNullOrEmpty(reward.Id) ? reward.Id : reward.Name;
            string gKey = "reward\0" + rid;
            string uKey = gKey + "\0" + u;
            long now = NowMs;

            // Peek quantity + cooldowns; commit only after a successful debit so a
            // NoFunds attempt neither consumes stock nor starts a cooldown.
            lock (_cdGate)
            {
                if (reward.Quantity > 0)
                {
                    _rewardRedeemed.TryGetValue(rid, out int used);
                    if (used >= reward.Quantity)
                        return LoyaltyRedeemResult.Fail(LoyaltyRedeemOutcome.SoldOut, "sold out");
                }
                if (reward.GlobalCooldownSeconds > 0 && _globalCdMs.TryGetValue(gKey, out long gEnd) && now < gEnd)
                    return LoyaltyRedeemResult.Fail(LoyaltyRedeemOutcome.OnCooldown, "on cooldown");
                if (reward.PerUserCooldownSeconds > 0 && _userCdMs.TryGetValue(uKey, out long uEnd) && now < uEnd)
                    return LoyaltyRedeemResult.Fail(LoyaltyRedeemOutcome.OnCooldown, "on cooldown");
            }

            var debit = await Db.LoyaltyDebitAsync(cfg.Currency.BalanceTable, u, reward.Cost,
                LedgerTableIfEnabled(cfg), "reward:" + reward.Name, "redeem " + reward.Name).ConfigureAwait(false);
            if (!debit.Ok)
            {
                string why = debit.Outcome == LoyaltyOutcome.TableMissing ? "currency table missing" : "not enough points";
                return new LoyaltyRedeemResult(LoyaltyRedeemOutcome.NoFunds, why, rid, reward.Name, reward.Cost, debit.NewBalance);
            }

            lock (_cdGate)
            {
                if (reward.Quantity > 0)
                {
                    _rewardRedeemed.TryGetValue(rid, out int used);
                    _rewardRedeemed[rid] = used + 1;
                }
                if (reward.GlobalCooldownSeconds > 0) _globalCdMs[gKey] = now + reward.GlobalCooldownSeconds * 1000L;
                if (reward.PerUserCooldownSeconds > 0) _userCdMs[uKey] = now + reward.PerUserCooldownSeconds * 1000L;
            }

            string announce = FormatRedeemAnnounce(reward, u, debit.NewBalance, cfg);
            RaiseScript("Loyalty.OnRedeem", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.user"] = u,
                ["event.reward"] = reward.Name,
                ["event.cost"] = reward.Cost.ToString(CultureInfo.InvariantCulture),
                ["event.balance"] = debit.NewBalance.ToString(CultureInfo.InvariantCulture),
                ["event.currency"] = cfg.Currency.NamePlural,
                ["user.name"] = u,
                ["reward"] = reward.Name,
                ["cost"] = reward.Cost.ToString(CultureInfo.InvariantCulture),
                ["balance"] = debit.NewBalance.ToString(CultureInfo.InvariantCulture),
            });
            if (!string.IsNullOrEmpty(reward.LayerId) && !string.IsNullOrEmpty(reward.TriggerName))
                FireVisual(reward.LayerId, reward.TriggerName);
            BusEmitPayload("LOYALTY_REDEEM", new
            {
                type = "LOYALTY_REDEEM", user = u, reward = reward.Name, cost = reward.Cost, balance = debit.NewBalance,
            });
            await AfterBalanceChangeAsync($"{u} redeemed \"{reward.Name}\" for {reward.Cost} {cfg.Currency.NamePlural}").ConfigureAwait(false);

            return new LoyaltyRedeemResult(LoyaltyRedeemOutcome.Ok, announce, rid, reward.Name, reward.Cost, debit.NewBalance);
        }

        private static string FormatRedeemAnnounce(LoyaltyReward reward, string user, long balance, LoyaltyConfig cfg)
        {
            string template = string.IsNullOrEmpty(reward.AnnounceMessage)
                ? "{user} redeemed {reward} for {cost} {currency}!"
                : reward.AnnounceMessage;
            return template
                .Replace("{user}", user)
                .Replace("{reward}", reward.Name)
                .Replace("{cost}", reward.Cost.ToString(CultureInfo.InvariantCulture))
                .Replace("{currency}", cfg.Currency.NamePlural)
                .Replace("{balance}", balance.ToString(CultureInfo.InvariantCulture));
        }

        // ── Shared cooldown helpers (games + rewards) ───────────────────────
        // Peek: true when the scope is still cooling down (does not consume).
        private bool IsOnCooldown(string scope, string user, int userCdSec, int globalCdSec)
        {
            if (userCdSec <= 0 && globalCdSec <= 0) return false;
            long now = NowMs;
            string uk = scope + "\0" + user;
            lock (_cdGate)
            {
                if (globalCdSec > 0 && _globalCdMs.TryGetValue(scope, out long gEnd) && now < gEnd) return true;
                if (userCdSec > 0 && _userCdMs.TryGetValue(uk, out long uEnd) && now < uEnd) return true;
            }
            return false;
        }

        // Stamp both cooldowns (called only after a settled play).
        private void StampCooldown(string scope, string user, int userCdSec, int globalCdSec)
        {
            if (userCdSec <= 0 && globalCdSec <= 0) return;
            long now = NowMs;
            string uk = scope + "\0" + user;
            lock (_cdGate)
            {
                if (globalCdSec > 0) _globalCdMs[scope] = now + globalCdSec * 1000L;
                if (userCdSec > 0) _userCdMs[uk] = now + userCdSec * 1000L;
            }
        }

        // ── Fan-out helpers ─────────────────────────────────────────────────
        private async Task AfterBalanceChangeAsync(string? activity = null)
        {
            RaiseBalances();
            if (!string.IsNullOrEmpty(activity)) RaiseActivity(activity!);
            await PublishLiveChannelAsync().ConfigureAwait(false);
        }

        // Live active-viewer sweep via the wired provider (empty when unset/failed).
        private async Task<IReadOnlyList<ActiveViewer>> FetchActiveViewersAsync()
        {
            var provider = ActiveViewersProvider;
            if (provider is null) return Array.Empty<ActiveViewer>();
            try { return await provider().ConfigureAwait(false) ?? Array.Empty<ActiveViewer>(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("LoyaltyService", "ActiveViewersProvider failed", ex);
                return Array.Empty<ActiveViewer>();
            }
        }

        // Union of live Hub bot accounts + the config's extra exclusions (case-insensitive).
        private HashSet<string> BuildExclusionSet(LoyaltyConfig cfg)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var bots = BotAccountsProvider?.Invoke();
                if (bots != null)
                    foreach (var b in bots)
                        if (!string.IsNullOrWhiteSpace(b)) set.Add(b.Trim());
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LoyaltyService", "BotAccountsProvider failed", ex);
            }
            foreach (var x in cfg.AntiAbuse.ExtraExclusions)
                if (!string.IsNullOrWhiteSpace(x)) set.Add(x.Trim());
            return set;
        }

        // Provenance tag for both loyalty keys — one spelling, since the store scopes
        // ExpectedInterval inheritance by Source.
        private const string LiveSource = "tool:Loyalty";

        /// <summary>
        /// The dotted root this tool owns in the Overlay Live Channel. Used by the
        /// retract-on-disable sweep, which matches the LIVE store rather than a
        /// hand-listed key set — a list is what goes stale (silently) the next time this
        /// service learns to publish another <c>loyalty.*</c> key.
        /// </summary>
        private const string LiveKeyRoot = "loyalty";

        /// <summary>
        /// Whether this service currently considers itself the publisher of
        /// <c>loyalty.*</c> — i.e. both the master switch and the overlay switch were on
        /// the last time the config was applied.
        ///
        /// <para>Owned here rather than derived from a caller-supplied "previous config"
        /// because the panel reuses ONE <see cref="LoyaltyConfig"/> instance across saves:
        /// it mutates its working copy in place and passes it back, so the "old" reference
        /// a caller can hand us is frequently the same object as the new one. Remembering
        /// the state we last ACTED on is the only reading of the edge that cannot be
        /// defeated by aliasing.</para>
        /// </summary>
        private bool _liveChannelPublishing;

        /// <summary>
        /// Publishes the leaderboard into the Overlay Live Channel — leaderboard-only, kept
        /// small, gated on BOTH <see cref="Active"/> (the Loyalty tool's master switch) and
        /// <c>Config.Overlay.Enabled</c> (the streamer's "show my points overlay" switch).
        ///
        /// The <see cref="Active"/> gate is load-bearing for the startup seed and a no-op for
        /// the change path (every mutation already returns early on <c>!Active</c>, so it can
        /// never reach here with the tool off). It cannot be replaced by the Overlay gate:
        /// <c>Overlay.Enabled</c> defaults TRUE while <c>LoyaltyConfig.Enabled</c> defaults
        /// FALSE, so without it a streamer who used Loyalty and then switched the tool OFF
        /// would get, after every Hub restart, a published leaderboard of FROZEN standings that
        /// no code path can ever update again — and because this family declares no
        /// ExpectedInterval, the State pin would report <c>Active</c> forever about data that
        /// is structurally incapable of changing. Publishing nothing keeps State at
        /// <c>Missing</c>, which is the honest answer. Mirrors
        /// <c>CountersService.SeedLiveChannelAsync</c>, which gates the same way.
        ///
        /// <c>loyalty.leaderboard</c> is a real JSON ARRAY of
        /// <c>{ rank, name, balance }</c> objects, not a pre-joined string: the array is what
        /// lets a widget address rank N directly, and it is also how <c>Loyalty.Balance</c>
        /// resolves one viewer — the browser indexes the array by name exactly as it does
        /// today. Per-user balance keys are deliberately NOT published: that family is
        /// unbounded (one key per viewer who ever earned a point), and the leaderboard
        /// already carries every name the overlay can display.
        ///
        /// No expected interval: balances are event-driven, so a leaderboard that has not
        /// moved since the last redemption is fully Active and must not decay to Stale.
        /// </summary>
        private async Task PublishLiveChannelAsync()
        {
            var cfg = _config;
            if (!cfg.Enabled) return;          // master switch — covers BOTH callers (see remarks)
            if (!cfg.Overlay.Enabled) return;  // per-tool "show the points overlay" switch
            try
            {
                int size = cfg.Overlay.LeaderboardSize > 0 ? cfg.Overlay.LeaderboardSize : 10;
                var top = await Db.LoyaltyTopAsync(cfg.Currency.BalanceTable, size).ConfigureAwait(false);

                var board = new JsonArray();
                foreach (var s in top)
                    board.Add(new JsonObject
                    {
                        ["rank"]    = s.Rank,
                        ["name"]    = s.Name,
                        ["balance"] = s.Balance,
                    });

                var store = LiveStore;
                store.Publish("loyalty.leaderboard", board, LiveSource);
                // loyalty.currency is intentionally AHEAD of its reader: contract §1 reserves
                // the key and the reader matrix has Loyalty.Leaderboard subscribe it, but
                // today's compositor.js writes loyaltyState.currency and reads it nowhere. V4
                // adds the {currency} token to the Leaderboard format line, which is what
                // consumes it. Do NOT delete this as dead — the subscription that justifies it
                // is declared, and the two sibling caption fields were withheld precisely
                // because no V4 subscription declares THEM.
                store.PublishString("loyalty.currency", cfg.Currency.NamePlural, LiveSource);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LoyaltyService", "live-channel leaderboard publish failed", ex);
            }
        }

        // Fire the reward's Visualist effect through the shared pipeline; never let a
        // slow/failed trigger stall the redeem path.
        private void FireVisual(string layerId, string triggerName)
        {
            var fv = FireVisualTrigger;
            if (fv is null) return;
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => fv(layerId, triggerName), "LoyaltyService", $"FireVisualTrigger({layerId}/{triggerName})");
        }

        // Sync script-event dispatch seam (Action) — crash-isolated so a faulting
        // on_event handler can't unwind into the caller.
        private void RaiseScript(string phoenixEvent, IReadOnlyDictionary<string, string> vars)
        {
            var rse = RaiseScriptEvent;
            if (rse is null) return;
            try { rse(phoenixEvent, vars); }
            catch (Exception ex) { GlobalLogger.Error("LoyaltyService", $"RaiseScriptEvent({phoenixEvent})", ex); }
        }

        private void BusEmitPayload(string busType, object payload)
        {
            var be = BusEmit;
            if (be is null) return;
            try { be(busType, JsonSerializer.Serialize(payload, JsonOpts)); }
            catch (Exception ex) { GlobalLogger.Error("LoyaltyService", $"BusEmit({busType})", ex); }
        }
    }
}
