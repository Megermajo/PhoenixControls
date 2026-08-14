using System;
using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Loyalty pre-build tool — a viewer points economy that
    // sits beside Giveaway and Timer. The whole configuration is serialized as
    // ONE JSON blob into the LoyaltyConfig system table (mirrors Timers.Json);
    // the BALANCE and LEDGER live in OPEN user tables (name/currency shape, the
    // same wallet the Giveaway ticket-price charges) so a streamer keeps full
    // db.* script access — those two tables are deliberately NOT in
    // DB._systemTables. See DB.Loyalty.cs / LoyaltyService.

    /// <summary>Outcome of an atomic balance move / bet.</summary>
    public enum LoyaltyOutcome
    {
        Ok,
        NoFunds,       // debit/transfer/bet: source can't cover the amount
        TableMissing,  // the currency table/column doesn't exist (config problem)
        Invalid,       // non-positive amount / bad identifier — rejected at the money layer
        BelowMin,      // bet under the game's minimum
        AboveMax,      // bet over the game's maximum
        // Runtime-service gate reasons (LoyaltyService, above the money layer — the
        // DB layer never produces these; they let a game/command result carry WHY
        // it was rejected before any DB call so the caller can build a chat line).
        Disabled,      // the tool, or this specific game/command, is switched off
        OnCooldown,    // a per-user or global cooldown is still active
        Offline,       // OnlineOnly is set and the stream is not live
    }

    /// <summary>Result of a Credit/Debit/Transfer/Set. NewBalance is the post-move
    /// balance of the primary account (receiver for credit, source for debit).</summary>
    public readonly record struct LoyaltyResult(LoyaltyOutcome Outcome, long NewBalance, long Amount)
    {
        public bool Ok => Outcome == LoyaltyOutcome.Ok;
        public static LoyaltyResult Fail(LoyaltyOutcome o, long balance = 0) => new(o, balance, 0);
    }

    /// <summary>How a bet amount is expressed by the player.</summary>
    public enum LoyaltyStakeKind { Absolute, Percent, All }

    /// <summary>A parsed bet request. Percent is 0..1; the absolute stake is
    /// resolved against the live balance inside the settlement transaction.</summary>
    public readonly record struct LoyaltyStake(LoyaltyStakeKind Kind, long Amount, double Percent)
    {
        public static LoyaltyStake Abs(long a) => new(LoyaltyStakeKind.Absolute, a, 0);
        public static LoyaltyStake Pct(double p) => new(LoyaltyStakeKind.Percent, 0, p);
        public static readonly LoyaltyStake AllIn = new(LoyaltyStakeKind.All, 0, 0);
    }

    /// <summary>Result of an atomic house-game settlement. Stake = the resolved
    /// absolute bet; Net = signed balance change (win: positive, loss: -stake).</summary>
    public readonly record struct LoyaltyBetResult(LoyaltyOutcome Outcome, long Stake, long Net, long NewBalance)
    {
        public bool Ok => Outcome == LoyaltyOutcome.Ok;
        public bool Won => Net > 0;
    }

    /// <summary>A single leaderboard / balance row.</summary>
    public readonly record struct LoyaltyStanding(string Name, long Balance, int Rank);

    /// <summary>An active viewer as reported by the platform sweep
    /// (GetActiveViewers). Login is lowercased; role flags are best-effort — the
    /// sweep's <c>subscribed</c> flag is reliable and <c>role</c> supplies mod/VIP
    /// where the platform sends it. <c>Display</c> is the name chat shows, kept so a
    /// viewer list can render what the streamer recognises rather than a login.</summary>
    /// <param name="RolesKnown">
    /// True only when the payload actually carried a role field. This is the difference
    /// between "the platform says this viewer is not a moderator" and "the platform did
    /// not say" — and it is load-bearing, not pedantry: a sweep that omits the field
    /// would otherwise report every viewer as a non-mod and CLOBBER the authoritative
    /// flags already learned from that viewer's own chat messages.
    /// </param>
    public sealed record ActiveViewer(string Login, bool Subscribed = false, bool IsMod = false,
                                      bool IsVip = false, string Display = "",
                                      bool RolesKnown = false);

    /// <summary>One applied wallet fold from <c>DB.LoyaltyMergeWalletsAsync</c> —
    /// <see cref="FromName"/>'s row was removed and its <see cref="FromBalance"/> added
    /// to <see cref="ToName"/>, whose post-merge balance is <see cref="ToBalance"/>.
    /// The opt-in repair for the duplicate rows a display-name-keyed wallet left
    /// behind.</summary>
    public readonly record struct LoyaltyWalletMerge(string FromName, string ToName, long FromBalance, long ToBalance);

    /// <summary>One banking-log entry (open PointsLedger table).</summary>
    public sealed class LoyaltyLedgerEntry
    {
        public long Id { get; set; }
        public string Time { get; set; } = "";
        public string Recipient { get; set; } = "";
        public string Sender { get; set; } = "";
        public long Amount { get; set; }
        public string Reason { get; set; } = "";
    }

    // ── Configuration (persisted as the LoyaltyConfig JSON blob) ─────────────

    /// <summary>Top-level Loyalty configuration. Everything the streamer tunes in
    /// the Pre-Builds window persists here.</summary>
    public sealed class LoyaltyConfig
    {
        /// <summary>Master switch. OFF by default — the tool is fully dormant
        /// (no earn tick, no commands, no games, no redemptions) until enabled.</summary>
        public bool Enabled { get; set; } = false;

        public LoyaltyCurrencyConfig Currency { get; set; } = new();
        public LoyaltyEarnConfig Earn { get; set; } = new();
        public LoyaltyCommandsConfig Commands { get; set; } = new();
        public LoyaltyGamesConfig Games { get; set; } = new();
        public LoyaltyAntiAbuseConfig AntiAbuse { get; set; } = new();
        public LoyaltyOverlayConfig Overlay { get; set; } = new();

        /// <summary>The reward store — redeemable items whose effect rides the
        /// shared Architect→Hub→Visualist VISUAL_TRIGGER pipeline.</summary>
        public List<LoyaltyReward> Rewards { get; set; } = new();
    }

    public sealed class LoyaltyCurrencyConfig
    {
        public string Name { get; set; } = "ChannelPoint";
        public string NamePlural { get; set; } = "ChannelPoints";
        public string Abbreviation { get; set; } = "CP";
        /// <summary>OPEN balance table (name/currency). Default shares the Giveaway
        /// ticket-price wallet so points buy tickets for free. Never a system table.</summary>
        public string BalanceTable { get; set; } = "ChannelPoints";
        public bool LedgerEnabled { get; set; } = true;
        /// <summary>OPEN ledger table (append-mostly audit).</summary>
        public string LedgerTable { get; set; } = "PointsLedger";
        /// <summary>Watch-time payouts are high volume — off by default to keep the ledger readable.</summary>
        public bool LogWatchTimePayouts { get; set; } = false;
    }

    public sealed class LoyaltyEarnConfig
    {
        // Watch-time
        public bool WatchTimeEnabled { get; set; } = true;
        public int WatchTimeIntervalMinutes { get; set; } = 5;   // 1..60
        public int WatchTimeAmount { get; set; } = 10;
        /// <summary>
        /// Ticked (default): the watch-time payout pays exactly the platform's
        /// ACTIVE-VIEWER sweep, which is what this tool has always done.
        ///
        /// <para>Unticked it ALSO pays viewers who typed in chat during the interval but
        /// did not come back in that sweep — the sweep is one snapshot per interval and
        /// can miss somebody who is demonstrably there. It only ever BROADENS: ticking it
        /// can never take away points a viewer would otherwise have earned, which is why
        /// the default keeps every existing install paying exactly what it paid before.
        /// The watch-MINUTE accrual (the Ranks tool's store) is deliberately NOT
        /// broadened by it — a switch on the Loyalty page must not quietly rewrite
        /// another tool's ledger. See LoyaltyService.WatchTimeTickAsync.</para>
        /// </summary>
        public bool ActiveViewersOnly { get; set; } = true;
        public bool OnlineOnly { get; set; } = true;

        // Per-event awards (0 = off)
        public bool FollowEnabled { get; set; } = true;
        public int FollowAmount { get; set; } = 0;

        public bool SubEnabled { get; set; } = true;
        public int SubTier1 { get; set; } = 500;
        public int SubTier2 { get; set; } = 1000;
        public int SubTier3 { get; set; } = 2500;
        public int SubPrime { get; set; } = 500;
        /// <summary>
        /// A resub pays the same as a new sub of that tier. This is the ONLY behaviour
        /// the runtime has: there is no separate "resub amount" to fall back to when it
        /// is unticked, so <c>ComputePointsForEvent</c> uses the tier amounts for Resub
        /// either way. Persisted (and left ticked) rather than silently honoured —
        /// giving the unticked state a meaning needs a resub-amount field and a panel
        /// row to fill it in.
        /// </summary>
        public bool ResubSameAsTier { get; set; } = true;
        public int GiftSubAmount { get; set; } = 500;  // per gifted sub (× count)

        public bool CheerEnabled { get; set; } = true;
        public int CheerPer100Bits { get; set; } = 100;

        public bool RaidEnabled { get; set; } = true;
        public int RaidFlat { get; set; } = 0;
        public int RaidPerViewer { get; set; } = 0;

        // ── Tips: default OFF, and deliberately so ───────────────────────────
        // These shipped ON while no tip event could ever arrive, which is exactly
        // what made them look harmless. Donation ingestion turns that inert config
        // into live money: the moment a broker is connected every tip would mint
        // points at 100/unit with nobody having asked for it. Both the gate and
        // the rate now start at zero, so paying out on tips is a decision the
        // streamer makes explicitly. Existing installs are corrected once by
        // TipDefaultsMigration — a default only protects a FRESH install, never a
        // config blob that survived an in-place upgrade.
        public bool TipEnabled { get; set; } = false;
        public int TipPerUnit { get; set; } = 0;

        /// <summary>
        /// Whether a tip may credit the donor name the payload supplied when that
        /// name is NOT a verified platform account. Default false.
        ///
        /// Donation brokers pass through whatever the donor typed into the tip
        /// form, so an unverified name is an impersonation vector: a €1 tip
        /// naming another viewer credits that viewer's wallet. With this off,
        /// tips whose payload carries no verified login simply do not pay points
        /// (the alert and the subathon time are unaffected). Turn it on only if
        /// you accept that donors can direct points at any name they choose.
        /// </summary>
        public bool TipCreditUnverifiedDonor { get; set; } = false;

        /// <summary>A once-per-stream bonus for a viewer's FIRST chat message of that
        /// stream. Awarded from the built-in chat tap (see
        /// <c>LoyaltyService.TryAwardFirstActivityAsync</c>), deduped per stream beside
        /// the follow dedupe and subject to the same exclusion list and anti-abuse caps
        /// as every other earn. <see cref="FirstActivityAmount"/> 0 = off, which is the
        /// shipped default — ticking the box alone pays nothing.</summary>
        public bool FirstActivityEnabled { get; set; } = true;
        public int FirstActivityAmount { get; set; } = 0;

        // Multipliers
        public double SubMultiplier { get; set; } = 2.0;
        public double ModMultiplier { get; set; } = 1.0;
        public double VipMultiplier { get; set; } = 1.0;
        /// <summary>Applied to a viewer in the User-Management REGULAR group (the same
        /// overlay every other role gate consults) — see LoyaltyService.MultiplierFor.</summary>
        public double RegularMultiplier { get; set; } = 1.0;
        /// <summary>The streamer's own "hours before I call someone a Regular" note.
        /// The runtime does NOT auto-promote on it: the suite keeps no per-viewer
        /// watch-hour ledger, so Regular membership stays the manual User-Management
        /// group. Persisted so the number survives a restart.</summary>
        public int RegularThresholdHours { get; set; } = 10;
        /// <summary>"highest" = apply the single largest applicable multiplier; "multiply" = stack them.</summary>
        public string MultiplierStacking { get; set; } = "highest";
    }

    public sealed class LoyaltyCommandsConfig
    {
        public bool AutoHandle { get; set; } = true;               // master command parser gate
        public bool SuppressAuthorDispatchWhenHandled { get; set; } = true;

        // Logical role defaults: user commands + queries = Everyone; the admin
        // adjust commands = Moderator + Broadcaster; the destructive wipe = Broadcaster only.
        public LoyaltyCommand Balance { get; set; } = new("points", true, LoyaltyRoles.All(), 0);
        public LoyaltyCommand Give { get; set; } = new("give", true, LoyaltyRoles.All(), 5);
        public LoyaltyCommand Top { get; set; } = new("top", true, LoyaltyRoles.All(), 0);
        public LoyaltyCommand Watchtime { get; set; } = new("watchtime", true, LoyaltyRoles.All(), 0);
        public LoyaltyCommand AddPoints { get; set; } = new("addpoints", true, LoyaltyRoles.Mods(), 0);
        public LoyaltyCommand SetPoints { get; set; } = new("setpoints", true, LoyaltyRoles.Mods(), 0);
        public LoyaltyCommand RemovePoints { get; set; } = new("removepoints", true, LoyaltyRoles.Mods(), 0);
        public LoyaltyCommand Wipe { get; set; } = new("pointswipe", true, LoyaltyRoles.Owner(), 0);
        public LoyaltyCommand Redeem { get; set; } = new("redeem", true, LoyaltyRoles.All(), 0);
    }

    /// <summary>Per-command / per-game role CHECKMARK set — the streamer ticks
    /// which roles may use it, each command configured independently (not a single
    /// gate level). A chatter passes if <see cref="Everyone"/> is ticked, OR they
    /// hold any other ticked role. Broadcaster/Moderator are the channel's own
    /// moderation roles.</summary>
    public sealed class LoyaltyRoles
    {
        public bool Everyone { get; set; }
        public bool Subscriber { get; set; }
        public bool Vip { get; set; }
        public bool Moderator { get; set; }
        public bool Broadcaster { get; set; }

        /// <summary>The User-Management Regular group — a community-trust tier with no
        /// platform equivalent. Added after the other five; the whole config is one JSON
        /// blob, so a blob written before this box existed simply lacks the key and
        /// deserializes to false — the streamer never opted in, the gate is unchanged.</summary>
        public bool Regular { get; set; }

        public LoyaltyRoles() { }
        public static LoyaltyRoles All() => new() { Everyone = true };
        public static LoyaltyRoles Mods() => new() { Moderator = true, Broadcaster = true };
        public static LoyaltyRoles Owner() => new() { Broadcaster = true };

        /// <summary>True when a chatter carrying these role flags may use the command/game.</summary>
        public bool Allows(bool isSub, bool isVip, bool isMod, bool isBroadcaster, bool isRegular)
            => Everyone
            || (Subscriber && isSub)
            || (Vip && isVip)
            || (Moderator && isMod)
            || (Broadcaster && isBroadcaster)
            || (Regular && isRegular);
    }

    public sealed class LoyaltyCommand
    {
        public bool Enabled { get; set; } = true;
        public string Trigger { get; set; } = "";
        /// <summary>Which roles may use this command (independent checkmarks).</summary>
        public LoyaltyRoles Roles { get; set; } = LoyaltyRoles.All();
        public int CooldownSeconds { get; set; } = 0;

        public LoyaltyCommand() { }
        public LoyaltyCommand(string trigger, bool enabled, LoyaltyRoles roles, int cooldown)
        { Trigger = trigger; Enabled = enabled; Roles = roles ?? LoyaltyRoles.All(); CooldownSeconds = cooldown; }
    }

    public sealed class LoyaltyGamesConfig
    {
        public LoyaltyGambleConfig Gamble { get; set; } = new();
        public LoyaltySlotsConfig Slots { get; set; } = new();
        public LoyaltyDuelConfig Duel { get; set; } = new();
        public LoyaltyRaffleConfig Raffle { get; set; } = new();
        public LoyaltyRouletteConfig Roulette { get; set; } = new();
    }

    /// <summary>Shared fields for every minigame.</summary>
    public abstract class LoyaltyGameBase
    {
        public bool Enabled { get; set; } = true;
        public string Command { get; set; } = "";
        public int MinBet { get; set; } = 10;
        public int MaxBet { get; set; } = 10000;    // 0 = no cap
        public int UserCooldownSeconds { get; set; } = 30;
        public int GlobalCooldownSeconds { get; set; } = 0;
        /// <summary>Which roles may play this game (independent checkmarks).</summary>
        public LoyaltyRoles WhoCanPlay { get; set; } = LoyaltyRoles.All();
        public bool OnlineOnly { get; set; } = false;
    }

    public sealed class LoyaltyGambleConfig : LoyaltyGameBase
    {
        public double WinChance { get; set; } = 0.5;      // 0..1
        public double PayoutMultiplier { get; set; } = 2.0;
        /// <summary>Whether <c>!gamble all</c> (also "allin" / "all-in" / "max") is a
        /// legal stake. Unticked, the word is not a bet shape at all — the parser yields
        /// an invalid stake and the player is told so. Gamble-only: slots and roulette
        /// have no such switch and keep accepting both shapes.</summary>
        public bool AllowAll { get; set; } = true;
        /// <summary>Whether <c>!gamble 50%</c> is a legal stake. Unticked, the percent
        /// shape yields an invalid stake (see <see cref="AllowAll"/>).</summary>
        public bool AllowPercent { get; set; } = true;
        public string WinMessage { get; set; } = "{user} rolled and WON {won} {currency}! New balance: {balance}.";
        public string LoseMessage { get; set; } = "{user} rolled and lost {bet} {currency}. Balance: {balance}.";
        public LoyaltyGambleConfig() { Command = "gamble"; }
    }

    public sealed class LoyaltySlotsConfig : LoyaltyGameBase
    {
        public List<string> Symbols { get; set; } = new() { "🍒", "🍋", "🔔", "⭐", "💎" };
        /// <summary>Payout multiplier when all three reels match a symbol (by index).</summary>
        public List<double> TripleMultipliers { get; set; } = new() { 3, 4, 6, 10, 20 };
        public double AnyTwoMultiplier { get; set; } = 1.5;
        public string WinMessage { get; set; } = "{user} spun {reels} — WON {won} {currency}! Balance: {balance}.";
        public string LoseMessage { get; set; } = "{user} spun {reels} — no luck, lost {bet}. Balance: {balance}.";
        public LoyaltySlotsConfig() { Command = "slots"; }
    }

    public sealed class LoyaltyDuelConfig : LoyaltyGameBase
    {
        public double WinChance { get; set; } = 0.5;
        public int AcceptTimeoutSeconds { get; set; } = 60;

        /// <summary>The word a challenged viewer types to take the duel. Was a
        /// hard-coded literal, which made it the one duel word the streamer could
        /// neither see in the panel nor change — and it is a very common word for a
        /// channel to already use for something else.</summary>
        public string AcceptCommand { get; set; } = "accept";

        /// <summary>The word a challenged viewer types to refuse the duel.</summary>
        public string DeclineCommand { get; set; } = "decline";

        public string ChallengeMessage { get; set; } = "{challenger} challenges {target} to a duel for {bet} {currency}! Type {accept} within {timeout}s.";
        public string WinMessage { get; set; } = "{winner} beat {loser} and takes {won} {currency}!";
        public LoyaltyDuelConfig() { Command = "duel"; MaxBet = 5000; }
    }

    public sealed class LoyaltyRaffleConfig : LoyaltyGameBase
    {
        public int DefaultWinners { get; set; } = 1;
        public int DefaultDurationSeconds { get; set; } = 60;
        public int EntryFee { get; set; } = 0;             // 0 = free
        /// <summary>FixedEach = each winner gets Prize; SplitPot = pot split among winners; PotToOne = whole pot to one.</summary>
        public string PrizeMode { get; set; } = "SplitPot";
        public int FixedPrize { get; set; } = 100;
        public LoyaltyRoles WhoCanStart { get; set; } = LoyaltyRoles.Mods();
        public string JoinCommand { get; set; } = "join";

        // The two management SUB-verbs, typed after the raffle command
        // ("!raffle draw"). Literals in the parser until now, so a streamer could
        // neither see them in the panel nor move them out of the way.
        /// <summary>Sub-verb that closes entry and picks the winners.</summary>
        public string DrawSubCommand { get; set; } = "draw";
        /// <summary>Sub-verb that abandons the raffle and refunds every entrant.</summary>
        public string CancelSubCommand { get; set; } = "cancel";
        public string OpenMessage { get; set; } = "A raffle has started! Type {join} to enter ({fee} {currency}). {winners} winner(s), {duration}s.";
        public string WinMessage { get; set; } = "Raffle over! Winners: {winners} ({each} {currency} each).";
        public LoyaltyRaffleConfig() { Command = "raffle"; MinBet = 0; MaxBet = 0; UserCooldownSeconds = 0; }
    }

    public sealed class LoyaltyRouletteConfig : LoyaltyGameBase
    {
        // European single-zero wheel. Payouts are net multipliers on the bet.
        public double StraightPayout { get; set; } = 35.0;   // single number
        public double ColorPayout { get; set; } = 1.0;       // red/black, even/odd, high/low
        public string WinMessage { get; set; } = "{user} bet {betType} — landed {result}, WON {won} {currency}! Balance: {balance}.";
        public string LoseMessage { get; set; } = "{user} bet {betType} — landed {result}, lost {bet}. Balance: {balance}.";
        public LoyaltyRouletteConfig() { Command = "roulette"; }
    }

    public sealed class LoyaltyAntiAbuseConfig
    {
        /// <summary>Extra excluded accounts on top of the live Hub Bot Accounts list.</summary>
        public List<string> ExtraExclusions { get; set; } = new();
        public int PerEventMax { get; set; } = 0;   // 0 = unlimited
        public int DailyCapPerUser { get; set; } = 0;
        public bool DedupeFollows { get; set; } = true;
    }

    public sealed class LoyaltyOverlayConfig
    {
        public bool Enabled { get; set; } = true;
        public int LeaderboardSize { get; set; } = 10;
        public string RowFormat { get; set; } = "{rank}. {name} — {balance}";
    }

    /// <summary>A redeemable reward. On redemption the Hub deducts <see cref="Cost"/>,
    /// logs it, raises Loyalty.OnRedeem, and fires the effect through the SAME
    /// VISUAL_TRIGGER pipeline Architect uses — never a bespoke media path.</summary>
    public sealed class LoyaltyReward
    {
        public string Id { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Name { get; set; } = "";
        public long Cost { get; set; } = 100;
        public int Quantity { get; set; } = 0;          // 0 = unlimited (per stream)
        public int PerUserCooldownSeconds { get; set; } = 0;
        public int GlobalCooldownSeconds { get; set; } = 0;
        /// <summary>The Visualist layer this reward's effect triggers (the OBS browser source id).</summary>
        public string LayerId { get; set; } = "";
        /// <summary>The widget/trigger name within that layer (onTrigger:&lt;x&gt;).</summary>
        public string TriggerName { get; set; } = "";
        public string AnnounceMessage { get; set; } = "{user} redeemed {reward} for {cost} {currency}!";
    }

    // ── Runtime-service result records (LoyaltyService returns these; the command
    //    / UI layer turns them into a chat line or a page update). They live in
    //    Shared so every layer can reference the SAME shapes. ─────────────────

    /// <summary>Why a reward redemption resolved the way it did.</summary>
    public enum LoyaltyRedeemOutcome
    {
        Ok,
        Inactive,   // the whole tool is disabled
        NotFound,   // no reward matched the id/name
        Disabled,   // the reward exists but is switched off
        SoldOut,    // per-stream quantity exhausted
        OnCooldown, // per-user or global reward cooldown still active
        NoFunds,    // the user can't cover the cost (or the currency table is missing)
    }

    /// <summary>Result of <c>LoyaltyService.RedeemAsync</c>. <see cref="Message"/>
    /// is the ready-to-announce chat line on success (or a short reason otherwise).</summary>
    public readonly record struct LoyaltyRedeemResult(
        LoyaltyRedeemOutcome Outcome, string Message, string RewardId, string RewardName, long Cost, long NewBalance)
    {
        public bool Ok => Outcome == LoyaltyRedeemOutcome.Ok;
        public static LoyaltyRedeemResult Fail(LoyaltyRedeemOutcome o, string message = "")
            => new(o, message, "", "", 0, 0);
    }

    /// <summary>Why a duel challenge was (not) opened.</summary>
    public enum DuelStartOutcome
    {
        Ok,
        Inactive,        // tool disabled
        Disabled,        // duel game disabled
        Offline,         // OnlineOnly and stream not live
        SelfChallenge,   // challenger == target
        InvalidTarget,   // blank challenger/target
        ChallengerBusy,  // the challenger already has a pending duel
        TargetBusy,      // the target already has a pending duel
        BelowMin,        // wager under the game minimum
        AboveMax,        // wager over the game maximum
        OnCooldown,      // the challenger is on cooldown
    }

    /// <summary>Result of <c>LoyaltyService.ChallengeAsync</c> — a pending challenge
    /// was recorded (Ok) or rejected. No funds move until the target accepts.</summary>
    public readonly record struct DuelStart(
        DuelStartOutcome Outcome, string Challenger, string Target, long Wager, int TimeoutSeconds)
    {
        public bool Ok => Outcome == DuelStartOutcome.Ok;
        public static DuelStart Fail(DuelStartOutcome o) => new(o, "", "", 0, 0);
    }

    /// <summary>Why a duel accept resolved the way it did.</summary>
    public enum DuelResultOutcome
    {
        Ok,
        NoPending,  // no live challenge for this target (or the tool is disabled)
        NoFunds,    // the loser could not cover the wager at settle time — nobody charged
    }

    /// <summary>Result of <c>LoyaltyService.AcceptAsync</c>. On NoFunds
    /// <see cref="BrokeUser"/> names the side that could not pay.</summary>
    public readonly record struct DuelResult(
        DuelResultOutcome Outcome, string Winner, string Loser, long Wager, long WinnerBalance, string BrokeUser)
    {
        public bool Ok => Outcome == DuelResultOutcome.Ok;
    }

    /// <summary>Why a raffle join resolved the way it did.</summary>
    public enum RaffleJoinOutcome
    {
        Ok,
        NoRaffle,       // no live raffle
        AlreadyJoined,  // this user is already entered
        NoFunds,        // couldn't pay the entry fee — no entry added
        Inactive,       // tool disabled
    }

    /// <summary>Result of <c>LoyaltyService.RaffleJoinAsync</c>.</summary>
    public readonly record struct RaffleJoin(
        RaffleJoinOutcome Outcome, long Fee, long NewBalance, int EntrantCount)
    {
        public bool Ok => Outcome == RaffleJoinOutcome.Ok;
    }

    /// <summary>Why a raffle draw resolved the way it did.</summary>
    public enum RaffleDrawOutcome
    {
        Ok,
        NoRaffle,   // nothing live to draw
        NoEntries,  // the raffle had no entrants — nothing paid out
        /// <summary>Closed without prizes. Every entrant whose fee had LANDED was
        /// refunded in full — a cancel must never keep a viewer's stake.</summary>
        Cancelled,
    }

    /// <summary>A single raffle winner and the amount they were awarded.</summary>
    public readonly record struct RaffleWinner(string Name, long Amount);

    /// <summary>Result of <c>LoyaltyService.RaffleDrawAsync</c> — the winners and
    /// their per-winner payouts (the caller turns this into an announce line).</summary>
    public sealed class RaffleOutcome
    {
        public RaffleDrawOutcome Outcome { get; set; } = RaffleDrawOutcome.NoRaffle;
        public List<RaffleWinner> Winners { get; set; } = new();
        public long TotalPot { get; set; }
        public int EntrantCount { get; set; }
        public bool Ok => Outcome == RaffleDrawOutcome.Ok;
    }
}
