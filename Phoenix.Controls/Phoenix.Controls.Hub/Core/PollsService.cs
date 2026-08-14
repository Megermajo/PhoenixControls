using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // PollsService — the Hub-runtime brain of the Polls & Betting pre-build tool: a chat
    // poll (!vote <n>) with an optional points side-bet (!bet <n> <amount>) settled out of
    // the Loyalty wallet, and an optional per-poll mirror onto Twitch's own poll /
    // prediction surface.
    //
    // ── What this service owns, and what it deliberately does not ───────────────
    // It owns THE poll — one live session at a time, exactly like the Loyalty raffle — and
    // publishes its state onto the Overlay Live Channel under `poll.*`. It owns no money:
    // every stake and every payout goes through the Loyalty tool's own primitives via the
    // seams below, so the non-negativity guarantee stays in one place (DB.Loyalty.cs) and
    // the streamer sees the round trip in the one ledger they already read.
    //
    // ── Three siblings are cloned here, not one ─────────────────────────────────
    //   * LoyaltyService's RAFFLE (LoyaltyService.Games.cs) — the reservation-then-confirm
    //     split, the auto-close System.Threading.Timer scheduled OUTSIDE the session lock,
    //     the claim-under-lock that makes auto and manual close idempotent, and the
    //     refund-if-the-session-was-replaced path. A stake still in flight neither sizes
    //     the pot nor can win a share of it. That is the same problem this tool has, and it
    //     is solved there rather than re-derived here.
    //   * SongRequestService — the singleton / volatile-config / Active / SaveConfigAsync
    //     (deep-clone) / InitializeAsync shape, the chat parser's contract (strip '!', split
    //     on the first space, ordinal-ignore-case compare, role gate through the
    //     User-Management overlay, and the load-bearing convention that a role-denied
    //     command is CONSUMED but silent), and the honest live-channel heartbeat.
    //   * SongRequestService again for the SEAMS: this service is Hub-state-agnostic, so
    //     the script-event raise, the points charge/refund/payout and the native-platform
    //     mirror are all injected by ScriptManager.Polls.cs. That is what lets the whole
    //     open/vote/bet/settle core be unit-tested with no ScriptManager, no Streamer.bot
    //     and no databank.
    //
    // ── The native mirror is CAPABILITY-GATED, and that is not decoration ───────
    // Mirroring drives Streamer.bot wrapper actions that a given install may simply not
    // have. The seam therefore reports what it actually did, and the service records
    // whether the mirror ENGAGED — a mirror that could not be opened is never "closed",
    // and a mirror whose CLOSING half is unavailable is never opened in the first place.
    // That second rule is the important one: a native Twitch prediction that is created and
    // never resolved locks the channel's prediction UI until the streamer clears it by
    // hand. Refusing to open beats leaving that behind. When the mirror is refused the tool
    // runs chat-only and says so once.
    public sealed partial class PollsService
    {
        private readonly DB _db;
        public PollsService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static PollsService? _instance;
        private static readonly object _instanceGate = new();
        public static PollsService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new PollsService(DB.Instance);
            }
        }

        // ── Config (swapped wholesale; volatile ⇒ visible on the pump thread) ─
        private volatile PollsConfig _config = new();
        public PollsConfig Config => _config;

        /// <summary>Master gate — false makes the chat commands, the poll, the live-channel
        /// publish and the Poll.On* events a total no-op. The Architect Poll.* nodes go
        /// quiet with it too: unlike a databank read there is no tool-independent store
        /// underneath, so "the poll" simply does not exist while the tool is off.</summary>
        public bool Active => _config.Enabled;

        // ── Injected Hub-side seams ─────────────────────────────────────────
        /// <summary>
        /// The Overlay Live Channel this service publishes <c>poll.*</c> into. Defaults to
        /// the process-wide store. Public get / internal set mirrors
        /// SongRequestService.LiveStore: production cannot swap the channel at runtime,
        /// while the test assembly gives each test its OWN store instead of sharing one.
        /// </summary>
        public OverlayLiveStore LiveStore { get; internal set; } = OverlayLiveStore.Instance;

        /// <summary>Raises a Poll.On* script event (wired in RegisterPollsCommands).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>
        /// Takes a viewer's stake. Wired to LoyaltyService.ChargeAsync, which REFUSES an
        /// unaffordable debit where an admin remove would floor them at zero and report
        /// success. Null while unwired, which the caller treats as EconomyOff — a bet that
        /// cannot be collected is refused, never quietly accepted for free.
        /// </summary>
        public Func<string, long, Task<PollChargeResult>>? ChargePoints { get; set; }

        /// <summary>
        /// Asks the economy whether it could collect a stake RIGHT NOW — consulted once,
        /// when a betting poll is opened, so "the money is off" is discovered before the
        /// poll takes a single vote instead of one refused bettor at a time.
        ///
        /// ★ This is a genuine probe, not a wiring check, and that distinction is the whole
        /// reason it exists: <see cref="ChargePoints"/> is assigned unconditionally at
        /// command registration, so testing it for null asked a question whose answer was
        /// fixed at boot. The wired implementation reports the two states
        /// <see cref="PollOpenOutcome.EconomyOff"/> has always named — the Loyalty master
        /// toggle off, or its currency table missing. Null (unwired) means "no probe", which
        /// reads as collectable; see EconomyCanCollectAsync for why that direction is the
        /// safe one.
        /// </summary>
        public Func<Task<bool>>? EconomyReady { get; set; }

        /// <summary>
        /// Gives ONE viewer their stake back (a late-refused bet). Returns TRUE only when
        /// the points genuinely landed back on the balance.
        ///
        /// ★ The result is the whole point of the signature. The wired implementation
        /// reports a refused refund — the Loyalty master toggle flipped off mid-poll, its
        /// currency table renamed — as a RESULT and never as a throw, so a seam that
        /// returned bare <c>Task</c> would turn "the viewer is 500 points out of pocket"
        /// into silence in every log and every chat line. Null while unwired counts as a
        /// failed refund for exactly the same reason.
        /// </summary>
        public Func<string, long, Task<bool>>? RefundPoints { get; set; }

        /// <summary>
        /// Pays a whole batch in one transaction — the settlement primitive. Returns how
        /// many credits the money layer APPLIED, which the settlement compares against what
        /// it asked for; a short count is logged at CriticalError with the shortfall named.
        /// Null while unwired returns 0, which reports as a total settlement failure rather
        /// than a silent success.
        /// </summary>
        public Func<IReadOnlyList<(string Name, long Amount)>, string, Task<int>>? PayoutPoints { get; set; }

        /// <summary>
        /// Drives the native Twitch poll / prediction surface. Wired to the Streamer.bot
        /// action-pack dispatch in ScriptManager.Polls.cs, which is where the RUNTIME
        /// capability check lives. Returns what it actually did — a mirror is never assumed
        /// to have happened. Null while unwired reports
        /// <see cref="PollMirrorOutcome.Unavailable"/>, which is the honest answer for a
        /// tool that has no path to the platform at all.
        /// </summary>
        public Func<PollMirrorRequest, PollMirrorOutcome>? NativeMirror { get; set; }

        /// <summary>The economy's plural currency noun ("points", "gems", …) for the chat
        /// lines and the <c>event.currency</c> var. A seam for the same reason the money
        /// calls are: reading LoyaltyService.Instance here would drag the whole points
        /// economy, and the process-wide databank behind it, into every unit test of the
        /// poll core. Null falls back to "points".</summary>
        public Func<string>? CurrencyProvider { get; set; }

        /// <summary>
        /// Posts one unprompted line to the streamer's main chat — the poll's opening
        /// announcement and its result. Distinct from the per-message reply the chat parser
        /// uses, because a poll opened from the panel or closed by its own timer has no
        /// message to reply to. Wired to the same SendTwitchChatCore path chat.send uses
        /// (the SchedulingService.ChatSend precedent); null while unwired simply means no
        /// announcement, which is a supported state rather than a failure.
        /// </summary>
        public Action<string>? Announce { get; set; }

        // ── Change notifications (UI) ───────────────────────────────────────
        public event EventHandler? ConfigChanged;

        /// <summary>Raised after any poll change (chat, node or panel), so an open Polls
        /// page stays live.</summary>
        public event EventHandler? PollChanged;

        private void RaiseConfigChanged() => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "PollsService", "ConfigChanged");
        internal void RaisePollChanged() => SafeEvent.Raise(PollChanged, this, EventArgs.Empty, "PollsService", "PollChanged");

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // Monotonic clock for the poll countdown — never wall clock, so an NTP step or a
        // DST change can neither end a poll early nor strand it open.
        internal static readonly Stopwatch Clock = Stopwatch.StartNew();

        // ── Lifecycle ───────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? raw = await _db.LoadPollsConfigAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cfg = JsonSerializer.Deserialize<PollsConfig>(raw!, JsonOpts);
                    if (cfg != null) _config = Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("PollsService", "InitializeAsync: config load failed", ex);
            }

            // The heartbeat is started unconditionally (the TimerService / Scheduling /
            // SongRequest always-on family): the master toggle gates the publish BEHAVIOUR
            // inside the tick, not the loop's existence, so enabling the tool mid-stream
            // starts painting within one interval with no restart.
            StartOverlayPump();

            GlobalLogger.Log(
                $"PollsService online — tool {(Active ? "ENABLED" : "disabled")}.",
                "PollsService", LogLevel.System);
        }

        /// <summary>
        /// Cancels any open poll — REFUNDING every stake — then stops the overlay heartbeat
        /// and drains it.
        ///
        /// ★ The refund is the load-bearing half. Stakes are debited the moment a bet
        /// lands, so an open betting poll is holding real points; closing it normally at
        /// shutdown would settle a vote that never finished, and dropping it would keep the
        /// points. Cancel-and-refund is the only honest option, and it mirrors the Loyalty
        /// raffle's ResolveOpenRaffleOnShutdownAsync. A HARD kill still loses the stakes —
        /// see the money note in PollsModels.cs.
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                if (IsOpen)
                {
                    var res = await CancelAsync("shutdown").ConfigureAwait(false);
                    if (res.Ok)
                        GlobalLogger.Log(
                            $"Polls: an open poll was cancelled at shutdown — {res.Settlement.Applied.ToString(CultureInfo.InvariantCulture)} " +
                            $"stake(s) refunded out of {res.Settlement.Payouts.Count.ToString(CultureInfo.InvariantCulture)}.",
                            "PollsService", LogLevel.System);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("PollsService", "shutdown cancel/refund failed", ex);
            }

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
            try { cts?.Cancel(); } catch { }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { GlobalLogger.Error("PollsService", "overlay pump drain failed", ex); }
            }
            cts?.Dispose();
        }

        internal static PollsConfig Normalize(PollsConfig cfg)
        {
            static string Word(string? v, string fallback)
                => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim().TrimStart('!');

            cfg.VoteCommand = Word(cfg.VoteCommand, "vote");
            cfg.BetCommand  = Word(cfg.BetCommand,  "bet");

            cfg.VoteRoles ??= PollRoles.All();
            cfg.BetRoles  ??= PollRoles.All();

            if (cfg.DefaultDurationSeconds < 5) cfg.DefaultDurationSeconds = 5;
            if (cfg.DefaultDurationSeconds > 3600) cfg.DefaultDurationSeconds = 3600;
            // Two is the floor because one option is not a poll; the ceiling keeps a
            // pasted-in wall of options from producing an unreadable chat line.
            cfg.MaxOptions = Math.Clamp(cfg.MaxOptions, 2, 20);
            if (cfg.MinBet < 1) cfg.MinBet = 1;
            if (cfg.MaxBet < 0) cfg.MaxBet = 0;
            if (cfg.MaxBet > 0 && cfg.MaxBet < cfg.MinBet) cfg.MaxBet = cfg.MinBet;
            if (cfg.ResultLingerSeconds < 0) cfg.ResultLingerSeconds = 0;
            if (cfg.ResultLingerSeconds > 600) cfg.ResultLingerSeconds = 600;
            return cfg;
        }

        // ── Config edits (panel) ────────────────────────────────────────────
        /// <summary>Replaces the whole config, persists it, and notifies the UI. The
        /// incoming object is deep-CLONED (JSON round-trip) so the panel VM can't alias the
        /// hot-path config instance (the Counters/Automod lesson).</summary>
        public async Task SaveConfigAsync(PollsConfig cfg)
        {
            _config = Normalize(Clone(cfg ?? new PollsConfig()));

            // ★ Switching the tool OFF while a poll is open must not strand the pot. The
            // master toggle makes every other path a no-op — including the close — so a
            // still-open poll would sit there holding debited stakes with nothing left able
            // to settle it. Cancel-and-refund here, at the one moment the transition is
            // observable.
            if (!_config.Enabled && IsOpen)
            {
                try
                {
                    var cancelled = await CancelAsync("tool disabled").ConfigureAwait(false);
                    if (cancelled.Ok)
                        GlobalLogger.Log(
                            "Polls: the tool was switched off with a poll open — it was cancelled and every stake refunded.",
                            "Polls", LogLevel.System);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("PollsService", "cancel-on-disable failed", ex);
                }
            }

            try
            {
                string json = JsonSerializer.Serialize(_config);
                await _db.SavePollsConfigAsync(json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("PollsService", "SaveConfigAsync failed", ex);
            }
            RaiseConfigChanged();
            // The master toggle is part of the config, so a save can be the moment the tool
            // goes dark: publish immediately rather than waiting up to one interval for the
            // heartbeat to notice and paint the off-shape.
            PublishOverlay();
        }

        internal static PollsConfig Clone(PollsConfig src)
        {
            try
            {
                string json = JsonSerializer.Serialize(src ?? new PollsConfig());
                return JsonSerializer.Deserialize<PollsConfig>(json, JsonOpts) ?? new PollsConfig();
            }
            catch { return new PollsConfig(); }
        }

        internal static string NormalizeLogin(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

        // ── Script events ───────────────────────────────────────────────────
        // The token spellings below are the SOURCE OF TRUTH the three Architect analyzer
        // sites (the Poll.On* arm in ScriptExporter.ResolveOutputFromNode,
        // AutocompleteScopeBuilder, VarChainAnalyzer.ResultEmitterMap) are synced to. The
        // multi-word ones are snake_case — event.total_votes, event.winner_votes,
        // event.duration_seconds, event.winner_count — which is exactly why that exporter
        // arm has to exist: the generic Events tail would lower-case the socket name to
        // "totalvotes" and bind nothing.
        private void RaiseScript(string phoenixEvent, Dictionary<string, string> vars)
        {
            try { RaiseScriptEvent?.Invoke(phoenixEvent, vars); }
            catch (Exception ex)
            {
                GlobalLogger.Error("PollsService", $"RaiseScriptEvent({phoenixEvent}) failed", ex);
            }
        }

        internal void RaiseOpened(PollSnapshot snap)
        {
            // The activity row rides the raise rather than the open, so it records the poll
            // exactly as the snapshot describes it — one instant, one description.
            // Deliberately BEFORE the script raise: a seam that throws is swallowed by
            // RaiseScript, but a seam that runs slowly should not delay the panel's row.
            RecordActivity("OPEN", snap.BettingEnabled
                ? $"Poll opened: {Clip(snap.Title, TitleMaxChars)} — {snap.Options.Count.ToString(CultureInfo.InvariantCulture)} options, {snap.DurationSeconds.ToString(CultureInfo.InvariantCulture)}s, stakes on."
                : $"Poll opened: {Clip(snap.Title, TitleMaxChars)} — {snap.Options.Count.ToString(CultureInfo.InvariantCulture)} options, {snap.DurationSeconds.ToString(CultureInfo.InvariantCulture)}s.");
            RaiseScript("Poll.OnOpened", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.title"] = snap.Title,
                ["event.options"] = JoinLabels(snap),
                ["event.option_count"] = snap.Options.Count.ToString(CultureInfo.InvariantCulture),
                ["event.duration_seconds"] = snap.DurationSeconds.ToString(CultureInfo.InvariantCulture),
                ["event.betting"] = snap.BettingEnabled ? "true" : "false",
            });
        }

        internal void RaiseClosed(PollSnapshot snap)
        {
            // Leader is empty on a tie or with no votes at all — say which, rather than
            // printing a blank winner.
            RecordActivity("CLOSE", snap.TotalVotes == 0
                ? $"Poll closed: {Clip(snap.Title, TitleMaxChars)} — nobody voted."
                : snap.Leader.Length == 0
                    ? $"Poll closed: {Clip(snap.Title, TitleMaxChars)} — {snap.TotalVotes.ToString(CultureInfo.InvariantCulture)} vote(s), no clear winner."
                    : $"Poll closed: {Clip(snap.Title, TitleMaxChars)} — {Clip(snap.Leader, TitleMaxChars)} won with {snap.LeaderVotes.ToString(CultureInfo.InvariantCulture)} of {snap.TotalVotes.ToString(CultureInfo.InvariantCulture)} vote(s).");
            RaiseScript("Poll.OnClosed", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.title"] = snap.Title,
                ["event.winner"] = snap.Leader,
                ["event.winner_votes"] = snap.LeaderVotes.ToString(CultureInfo.InvariantCulture),
                ["event.total_votes"] = snap.TotalVotes.ToString(CultureInfo.InvariantCulture),
                ["event.options"] = JoinLabels(snap),
            });
        }

        internal void RaiseSettled(PollSnapshot snap, PollSettlement settlement, string currency)
        {
            var names = new List<string>(settlement.Payouts.Count);
            foreach (var p in settlement.Payouts) names.Add(p.User);
            RecordActivity("PAY", DescribeSettlementRow(settlement, currency));
            RaiseScript("Poll.OnSettled", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.title"] = snap.Title,
                ["event.winner"] = settlement.Outcome == PollSettlementOutcome.Paid ? snap.Leader : "",
                ["event.outcome"] = settlement.Token,
                ["event.pot"] = settlement.Pot.ToString(CultureInfo.InvariantCulture),
                ["event.winners"] = string.Join(",", names),
                ["event.winner_count"] = settlement.Payouts.Count.ToString(CultureInfo.InvariantCulture),
                ["event.currency"] = currency ?? "",
            });
        }

        private static string JoinLabels(PollSnapshot snap)
        {
            var labels = new List<string>(snap.Options.Count);
            foreach (var o in snap.Options) labels.Add(o.Label);
            return string.Join(", ", labels);
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "Polls";

        /// <summary>The settlement's row. <see cref="PollSettlement.Applied"/> is what the
        /// money layer CONFIRMED, so a short count is stated rather than rounded up to a
        /// clean "paid" — it is the same shortfall the settlement logs at CriticalError, and
        /// the panel is where a streamer would look for it.</summary>
        internal static string DescribeSettlementRow(PollSettlement settlement, string currency)
        {
            string unit = string.IsNullOrWhiteSpace(currency) ? "points" : currency.Trim();
            string pot = settlement.Pot.ToString(CultureInfo.InvariantCulture);
            string asked = settlement.Payouts.Count.ToString(CultureInfo.InvariantCulture);
            string applied = settlement.Applied.ToString(CultureInfo.InvariantCulture);
            bool short_ = settlement.Applied < settlement.Payouts.Count;

            return settlement.Outcome switch
            {
                PollSettlementOutcome.Paid => short_
                    ? $"Pot of {pot} {unit} settled — only {applied} of {asked} winner(s) could be paid."
                    : $"Pot of {pot} {unit} paid to {asked} winner(s).",
                PollSettlementOutcome.Refunded => short_
                    ? $"Pot of {pot} {unit} refunded — only {applied} of {asked} stake(s) went back."
                    : $"Pot of {pot} {unit} refunded to {asked} bettor(s).",
                _ => "Poll settled — nothing was staked.",
            };
        }

        // Observation only: recording sits on the open / close path of a real poll, so a
        // fault in it must never become a fault in the poll.
        private static void RecordActivity(string kind, string message)
        {
            try { ToolActivityRing.Record(ActivityTool, kind, message); }
            catch (Exception ex) { GlobalLogger.Error("PollsService", "activity record failed", ex); }
        }

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says. The one state the master switch cannot say is
        /// <see cref="IdleEconomyOff"/>: betting is armed while the points economy is off,
        /// so the next poll opened with stakes is refused
        /// (<see cref="PollOpenOutcome.EconomyOff"/>) before it takes a single vote.
        ///
        /// <para>NOT covered here: "open, but the native mirror was refused". The refusal is
        /// returned to the caller in <see cref="PollOpenResult.Mirror"/> and never stored on
        /// the session, so nothing this service holds afterwards can tell a
        /// never-requested mirror from a requested-and-refused one. Adding it means storing
        /// the outcome where the session is opened (PollsService.Session.cs) — see the
        /// delivery notes.</para>
        /// </summary>
        public enum PollsPillState
        {
            /// <summary>The tool is switched off — no poll can exist at all.</summary>
            Dormant,
            /// <summary>A poll is accepting votes.</summary>
            Open,
            /// <summary>Voting is over and the result is still on screen.</summary>
            Closed,
            /// <summary>No poll running, betting is configured on, and the points economy is
            /// off — a betting poll opened now would be refused.</summary>
            IdleEconomyOff,
            /// <summary>No poll running.</summary>
            Idle,
        }

        /// <summary>Pure state machine behind <see cref="PillState"/>.</summary>
        internal static PollsPillState ComputePillState(
            bool enabled, PollState state, bool bettingEnabled, bool economyActive)
        {
            if (!enabled) return PollsPillState.Dormant;
            if (state == PollState.Open) return PollsPillState.Open;
            if (state == PollState.Closed) return PollsPillState.Closed;
            if (bettingEnabled && !economyActive) return PollsPillState.IdleEconomyOff;
            return PollsPillState.Idle;
        }

        /// <summary>The live pill state. The economy half reads the Loyalty master toggle —
        /// the same fact <c>ChargePoints</c> translates into
        /// <see cref="PollChargeOutcome.EconomyOff"/> (ScriptManager.Polls.cs). It does NOT
        /// cover the second EconomyOff cause, a missing currency table, which only the
        /// async open-time probe can see.</summary>
        public PollsPillState PillState
        {
            get
            {
                var cfg = _config;
                return ComputePillState(cfg.Enabled, Snapshot().State, cfg.BettingEnabled,
                                        LoyaltyService.Instance.Active);
            }
        }

        // ── Overlay Live Channel ────────────────────────────────────────────

        // Provenance tag on every poll key. Identical on EVERY publish by design: the store
        // inherits a declared ExpectedInterval across writes only while the Source string
        // matches, so a second spelling here would let one publish silently drop the cadence
        // and the keys could never report Stale.
        private const string LiveSource = "tool:Polls";

        /// <summary>
        /// The publish cadence, and the ExpectedInterval every <c>poll.*</c> key declares.
        ///
        /// ★ A live-channel key MUST declare a cadence to be honest: the store has no
        /// remove API and no TTL, so a key published once reports Active for the rest of the
        /// session. Declaring the interval is only half of it; something has to keep
        /// publishing, or a perfectly current poll would report Stale within seconds. Hence
        /// the heartbeat. The store COALESCES an identical value (refreshes LastWriteUtc,
        /// does not dirty the key, ships no frame), so a quiet tool costs one dictionary
        /// write per key per tick and nothing on the wire.
        ///
        /// 1 s because a poll paints a COUNTDOWN: seconds_left changes every tick anyway,
        /// so a slower cadence would show a stuttering clock rather than save traffic.
        /// </summary>
        internal static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(1);

        /// <summary>Every key this tool publishes. The RETRACTION walks this list, so a new
        /// key added to <see cref="PublishOverlay"/> and forgotten here would be the one
        /// left painting a finished poll forever.</summary>
        private static readonly string[] LiveKeys =
        {
            "poll.state", "poll.title", "poll.options", "poll.total_votes",
            "poll.leader", "poll.leader_votes", "poll.seconds_left",
            "poll.betting", "poll.pot", "poll.bettors", "poll.winner",
        };

        private readonly object _loopGate = new();
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private bool _loopStarted;
        // Whether the last publish painted a poll. Drives the ONE retraction write on the
        // way down; while false the tool writes nothing at all, so a Hub that never ran a
        // poll leaves every poll.* key Missing rather than Active-and-empty.
        private bool _overlayWasOn;
        // The heartbeat and every mutation publish, so they are serialized — otherwise two
        // overlapping snapshots could land out of order and leave the channel holding the
        // OLDER state until the next tick corrected it.
        private readonly object _publishGate = new();

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
                try { PublishOverlay(); }
                catch (Exception ex) { GlobalLogger.Error("PollsService", "overlay publish failed", ex); }
            }
        }

        /// <summary>
        /// Publishes the poll under the <c>poll.</c> root. THIS KEY LIST IS THE CONTRACT a
        /// Var.Live / List.Live widget graph binds against — the browser derives its
        /// subscription from literal key text, so a rename here is a silently blank widget
        /// with no error anywhere:
        ///
        ///   poll.state         "open" | "closed"   (retracted while idle)
        ///   poll.title         the question
        ///   poll.options       array of { index, label, votes, percent, stake }
        ///   poll.total_votes   votes cast across every option
        ///   poll.leader        leading / winning option label ("" on a tie)
        ///   poll.leader_votes  its vote count
        ///   poll.seconds_left  whole seconds remaining (0 once closed)
        ///   poll.betting       whether this poll takes stakes
        ///   poll.pot           total points staked
        ///   poll.bettors       distinct viewers who staked
        ///   poll.winner        settled winner ("" while still open)
        ///
        /// ★ RETRACTION. A closed poll keeps painting for
        /// <see cref="PollsConfig.ResultLingerSeconds"/> so the result is actually readable,
        /// and is then RETRACTED: every key is published as JSON null and the tool goes
        /// quiet, so the value reads empty immediately AND the key decays to Stale.
        /// Publishing nothing at all would have been the bug — the store has no remove API,
        /// so the last frame would sit in OBS painting a finished poll for the rest of the
        /// stream.
        /// </summary>
        internal void PublishOverlay()
        {
            lock (_publishGate)
            {
                var store = LiveStore;
                var cfg = _config;
                PollSnapshot? snap = cfg.Enabled && cfg.PublishOverlay ? PaintableSnapshot() : null;

                if (snap is null)
                {
                    if (!_overlayWasOn) return;
                    _overlayWasOn = false;
                    foreach (var key in LiveKeys)
                        store.Publish(key, null, LiveSource, LiveInterval);
                    return;
                }

                store.PublishString("poll.state", snap.State == PollState.Open ? "open" : "closed", LiveSource, LiveInterval);
                store.PublishString("poll.title", snap.Title, LiveSource, LiveInterval);
                store.PublishNumber("poll.total_votes", snap.TotalVotes, LiveSource, LiveInterval);
                store.PublishString("poll.leader", snap.Leader, LiveSource, LiveInterval);
                store.PublishNumber("poll.leader_votes", snap.LeaderVotes, LiveSource, LiveInterval);
                store.PublishNumber("poll.seconds_left", snap.SecondsLeft, LiveSource, LiveInterval);
                store.PublishBool("poll.betting", snap.BettingEnabled, LiveSource, LiveInterval);
                store.PublishNumber("poll.pot", snap.Pot, LiveSource, LiveInterval);
                store.PublishNumber("poll.bettors", snap.BettorCount, LiveSource, LiveInterval);
                store.PublishString("poll.winner", snap.State == PollState.Closed ? snap.Leader : "", LiveSource, LiveInterval);

                var board = new JsonArray();
                foreach (var o in snap.Options)
                {
                    board.Add(new JsonObject
                    {
                        ["index"] = o.Index,
                        ["label"] = o.Label,
                        ["votes"] = o.Votes,
                        ["percent"] = o.Percent,
                        ["stake"] = o.Stake,
                    });
                }
                store.Publish("poll.options", board, LiveSource, LiveInterval);
                _overlayWasOn = true;
            }
        }

        // ── Built-in chat-command core (testable; ScriptManager supplies the send) ──
        /// <summary>
        /// The built-in Polls chat commands. Two verbs: <c>!vote &lt;number&gt;</c> and
        /// <c>!bet &lt;number&gt; &lt;amount&gt;</c>, both role-gated, and a role-denied
        /// command is still CONSUMED (returns true) but silent — the same convention the
        /// Counters / Quotes / Song Request parsers use, so a denied viewer's line never
        /// falls through to an authored on_chat script that would answer it anyway.
        ///
        /// The two verbs are DECLINED (return false) when no poll is running, so a streamer
        /// who also has a <c>!vote</c> custom command keeps it working between polls —
        /// the same "don't hijack the word while idle" rule the raffle applies to
        /// <c>!join</c>. That is only safe because the Polls provider sits ahead of
        /// CustomCommands in the dispatch order, never behind it.
        ///
        /// Returns true when a Polls command was recognized AND consumed (so the caller
        /// suppresses the author on_chat fan-out). DEFAULT-OFF IS A TOTAL NO-OP. Bot
        /// self-messages are already dropped upstream.
        /// </summary>
        public async Task<bool> TryHandleChatCommandAsync(ChatMessage msg, Func<string, Task> replyAsync)
        {
            if (!Active || msg is null) return false;
            replyAsync ??= static _ => Task.CompletedTask;

            var cfg = _config;
            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return false;

            string body = text[1..];
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body[..sp]).Trim();
            if (token.Length == 0) return false;
            string rest = sp < 0 ? string.Empty : body[(sp + 1)..].Trim();

            // This tool's Normalize already strips a configured '!' at load, so the
            // comparison was never broken here — it routes through the shared
            // canonicalizer anyway so all eleven providers answer one rule, and a
            // config pushed straight from the panel without a load pass behaves.
            static bool Eq(string a, string b) => ChatVerb.Matches(b, a);

            bool isVote = Eq(token, cfg.VoteCommand);
            bool isBet = Eq(token, cfg.BetCommand);
            if (!isVote && !isBet) return false;

            // Liveness FIRST, before the role gate. Refusing a non-subscriber's !vote while
            // no poll exists would consume the word and starve every provider behind this
            // one — the exact bug the viewer-queue sprint fixed in the raffle's !join arm.
            if (!IsOpen) return false;

            // Role checks consult the User-Management group overlay (a group-granted
            // Mod/VIP/Sub passes like the platform rank, and the Regular tick resolves off
            // the same overlay; passthrough while dormant).
            var eff = UserManagementService.Instance.Effective(msg);
            bool RoleOk(PollRoles? r) =>
                r != null && r.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);

            // The login is what every per-viewer record keys on; Twitch fills Username with
            // the DISPLAY name, so fall back to it only when no login came through.
            string login = NormalizeLogin(string.IsNullOrWhiteSpace(msg.Login) ? msg.Username : msg.Login);
            string display = string.IsNullOrWhiteSpace(msg.Username) ? login : msg.Username;
            if (login.Length == 0) return true;

            if (isVote)
            {
                if (!RoleOk(cfg.VoteRoles)) return true;
                if (!TryParseOptionToken(rest, out int oneBased))
                {
                    await replyAsync($"{display}, vote with !{cfg.VoteCommand} <number>. {OptionLine()}").ConfigureAwait(false);
                    return true;
                }
                var res = Vote(login, oneBased - 1);
                await replyAsync(DescribeVote(res, display)).ConfigureAwait(false);
                return true;
            }

            // !bet <number> <amount>
            if (!RoleOk(cfg.BetRoles)) return true;
            string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2
                || !TryParseOptionToken(parts[0], out int betOption)
                || !TryParseAmountToken(parts[1], out long amount))
            {
                await replyAsync($"{display}, bet with !{cfg.BetCommand} <number> <amount>. {OptionLine()}").ConfigureAwait(false);
                return true;
            }

            var bet = await BetAsync(login, betOption - 1, amount).ConfigureAwait(false);
            await replyAsync(DescribeBet(cfg, bet, display)).ConfigureAwait(false);
            return true;
        }

        // A poll option is always addressed by its 1-based number in chat. Rejects blank /
        // non-numeric outright rather than defaulting to option 1 — a typo must not be a
        // vote, and with betting on it must certainly not be a stake.
        internal static bool TryParseOptionToken(string raw, out int oneBased)
        {
            oneBased = 0;
            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0) return false;
            // "#2" is what people type when the overlay prints "#2".
            if (raw[0] == '#') raw = raw[1..].Trim();
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out oneBased) && oneBased > 0;
        }

        // A stake is a positive whole number of points. No "all" / "50%" shorthand here:
        // those resolve against the live balance INSIDE the Loyalty settlement transaction,
        // and a poll stake is charged up front through ChargeAsync instead — so accepting
        // them would mean resolving a balance outside the transaction that spends it.
        internal static bool TryParseAmountToken(string raw, out long amount)
        {
            amount = 0;
            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0) return false;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount)) return amount > 0;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                && double.IsFinite(d) && d >= 1 && d < long.MaxValue)
            {
                amount = (long)Math.Floor(d);
                return amount > 0;
            }
            amount = 0;
            return false;
        }

        // ── Chat-line budgets ───────────────────────────────────────────────
        // SendTwitchChatCore DROPS a message over Twitch's 500-character limit outright —
        // one CriticalError in the System Log and no reply at all — and NOTHING upstream
        // bounds what goes into these lines. MaxOptions clamps the option COUNT to [2,20],
        // but an option label and the poll title are free text a streamer (or a graph) hands
        // over at whatever length they like: the default eight options at ~50 characters
        // each already clears 500 on their own.
        //
        // That makes this the worst failure this tool has. The opening announcement is
        // dropped, so the poll opens INVISIBLY — chat never learns it exists, nobody votes,
        // and it closes "nobody voted" while the streamer watches an empty overlay.
        //
        // Same treatment as RanksService.BoardMaxChars: bound the unbounded parts and leave
        // the rest of the 500 to the fixed prose around them. The first option always
        // prints however long it is — a menu that names nothing is no better than the
        // dropped message this prevents — and the options that do not fit are COUNTED
        // rather than silently lost.
        //
        // The configured verbs are deliberately NOT clipped: advertising "!vot" would be
        // worse than a long line, and a verb that long is a config the streamer can see and
        // fix, unlike a label buried in a poll they opened from a graph.
        private const int OptionMenuMaxChars = 300;
        private const int TitleMaxChars = 80;

        /// <summary>Trims free text to a chat-safe length. Used for the poll TITLE and for
        /// the single option label a vote/bet reply names — both arrive unbounded.</summary>
        internal static string Clip(string? text, int max)
        {
            string t = (text ?? string.Empty).Trim();
            return t.Length <= max ? t : t[..max].TrimEnd() + "...";
        }

        /// <summary>"1) Yes · 2) No" — the numbered menu, capped at
        /// <see cref="OptionMenuMaxChars"/> with the overflow counted rather than
        /// dropped.</summary>
        internal static string OptionMenu(IReadOnlyList<PollOptionView> options)
        {
            if (options is null || options.Count == 0) return "";
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var o in options)
            {
                string entry = $"{(o.Index + 1).ToString(CultureInfo.InvariantCulture)}) {o.Label}";
                if (shown > 0 && sb.Length + 3 + entry.Length > OptionMenuMaxChars) break;
                if (shown > 0) sb.Append(" · ");
                sb.Append(entry);
                shown++;
            }
            int hidden = options.Count - shown;
            if (hidden > 0)
                sb.Append(" and ").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append(" more");
            return sb.ToString();
        }

        /// <summary>"1) Yes · 2) No" — the option menu every usage reply ends with, so a
        /// viewer who mistyped can see what the numbers mean without asking.</summary>
        internal string OptionLine() => OptionMenu(Snapshot().Options);

        internal static string DescribeVote(PollVoteResult res, string display) => res.Outcome switch
        {
            // The label is clipped for the same reason the menu is capped: it is free text,
            // and a reply over 500 characters is dropped rather than shortened.
            PollVoteOutcome.Counted => $"{display} voted for {Clip(res.Label, TitleMaxChars)} ({res.Votes.ToString(CultureInfo.InvariantCulture)}).",
            PollVoteOutcome.Changed => $"{display} moved their vote to {Clip(res.Label, TitleMaxChars)} ({res.Votes.ToString(CultureInfo.InvariantCulture)}).",
            PollVoteOutcome.AlreadyVoted => $"{display}, you already voted — this poll does not allow changing it.",
            PollVoteOutcome.BadOption => $"{display}, that is not one of the options.",
            PollVoteOutcome.NoPoll => $"{display}, no poll is running right now.",
            _ => $"{display}, voting is unavailable right now.",
        };

        internal static string DescribeBet(PollsConfig cfg, PollBetResult res, string display) => res.Outcome switch
        {
            PollBetOutcome.Placed =>
                $"{display} staked {res.Staked.ToString(CultureInfo.InvariantCulture)} on {Clip(res.Label, TitleMaxChars)}. " +
                $"Pot: {res.Pot.ToString(CultureInfo.InvariantCulture)}.",
            PollBetOutcome.AlreadyBet => $"{display}, you have already staked on this poll.",
            PollBetOutcome.BadOption => $"{display}, that is not one of the options.",
            PollBetOutcome.BadAmount => $"{display}, stake a positive whole number of points.",
            // The limits come off the SAME config snapshot the refusal was decided against,
            // not off _config: a save landing between the two would otherwise quote a number
            // the viewer was never actually measured by.
            PollBetOutcome.BelowMin => $"{display}, the minimum stake is {cfg.MinBet.ToString(CultureInfo.InvariantCulture)}.",
            PollBetOutcome.AboveMax => $"{display}, the maximum stake is {cfg.MaxBet.ToString(CultureInfo.InvariantCulture)}.",
            PollBetOutcome.NoFunds => $"{display}, you do not have that many points.",
            PollBetOutcome.EconomyOff => $"{display}, betting is unavailable — the points economy is switched off.",
            PollBetOutcome.BettingOff => $"{display}, this poll does not take bets.",
            PollBetOutcome.NoPoll => $"{display}, no poll is running right now.",
            _ => $"{display}, that bet could not be placed.",
        };

        // One unprompted chat line, through the injected seam. Never throws into the caller:
        // a poll must still open and settle when the send path is down.
        internal void AnnounceLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try { Announce?.Invoke(line); }
            catch (Exception ex) { GlobalLogger.Error("PollsService", "announce failed", ex); }
        }

        /// <summary>The opening line: the question, the numbered options, and how to take
        /// part. It names the CONFIGURED verbs rather than "!vote" / "!bet", because a
        /// streamer who renamed them would otherwise have the tool advertise words that do
        /// nothing.</summary>
        internal string DescribeOpen(PollsConfig cfg, PollSnapshot snap)
        {
            string title = Clip(snap.Title, TitleMaxChars);
            string head = title.Length > 0 ? $"Poll: {title} — " : "Poll — ";
            string how = $"!{cfg.VoteCommand} <number> ({snap.DurationSeconds.ToString(CultureInfo.InvariantCulture)}s)";
            if (snap.BettingEnabled)
                how += $" · stake with !{cfg.BetCommand} <number> <amount> " +
                       $"(min {cfg.MinBet.ToString(CultureInfo.InvariantCulture)})";
            return head + OptionMenu(snap.Options) + " — " + how;
        }

        /// <summary>The line for a poll ended with no winner — a mid-stream abort, a
        /// switched-off tool, or shutdown. It says the stakes came back, because the one
        /// thing a viewer needs to know is where their points went.</summary>
        internal static string DescribeCancel(PollSnapshot snap, PollSettlement settlement, string currency)
        {
            string title = Clip(snap.Title, TitleMaxChars);
            string head = title.Length > 0
                ? $"Poll cancelled: \"{title}\"."
                : "Poll cancelled.";
            return settlement.Outcome == PollSettlementOutcome.Refunded
                ? head + $" Every stake was returned ({settlement.Pot.ToString(CultureInfo.InvariantCulture)} {currency})."
                : head;
        }

        /// <summary>The chat line announcing a closed poll, when
        /// <see cref="PollsConfig.AnnounceInChat"/> is on. A cancelled poll says its own
        /// thing (<see cref="DescribeCancel"/>).</summary>
        internal static string DescribeResult(PollSnapshot snap, PollSettlement settlement, string currency)
        {
            string title = Clip(snap.Title, TitleMaxChars);
            if (snap.TotalVotes == 0) return $"Poll closed: \"{title}\" — nobody voted.";
            string head = snap.Leader.Length == 0
                ? $"Poll closed: \"{title}\" — it is a tie at {snap.LeaderVotes.ToString(CultureInfo.InvariantCulture)} vote(s)."
                : $"Poll closed: \"{title}\" — {Clip(snap.Leader, TitleMaxChars)} wins with " +
                  $"{snap.LeaderVotes.ToString(CultureInfo.InvariantCulture)}/{snap.TotalVotes.ToString(CultureInfo.InvariantCulture)} votes.";

            return settlement.Outcome switch
            {
                PollSettlementOutcome.Paid =>
                    head + $" {settlement.Pot.ToString(CultureInfo.InvariantCulture)} {currency} split between " +
                           $"{settlement.Payouts.Count.ToString(CultureInfo.InvariantCulture)} backer(s).",
                PollSettlementOutcome.Refunded =>
                    head + $" Every stake was returned ({settlement.Pot.ToString(CultureInfo.InvariantCulture)} {currency}).",
                _ => head,
            };
        }
    }
}
