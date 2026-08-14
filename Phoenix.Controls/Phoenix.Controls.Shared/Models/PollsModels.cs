using System;
using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Polls & Betting pre-build tool — a chat poll (!vote <n>) with an
    // optional points side-bet (!bet <outcome> <amount>) settled out of the Loyalty wallet,
    // and an optional per-poll mirror onto Twitch's own poll / prediction surface.
    //
    // PERSISTENCE SPLIT, and it is deliberate:
    //   * The tool CONFIG (command words, role gates, durations, betting limits, overlay
    //     switch) is ONE JSON blob in the private PollsConfig SYSTEM table — the
    //     Scheduling / Alerts / SongRequest shape.
    //   * The live POLL is NOT persisted. It lives only in PollsService for the lifetime of
    //     the Hub process, exactly like the Song Request queue and the Loyalty raffle. A
    //     poll is a countdown against wall-clock, and a restart cannot resume one — a
    //     reloaded poll would report seconds remaining that stopped ticking while the Hub
    //     was down, and its overlay bar would be a lie from the first frame.
    //
    //     ★ MONEY CONSEQUENCE, stated plainly because it is the only one that matters:
    //     stakes are debited the moment a bet lands, so an open betting poll holds real
    //     points. A GRACEFUL shutdown cancels the poll and refunds every stake
    //     (PollsService.ShutdownAsync → CancelAsync), which is the same guarantee the
    //     Loyalty raffle gives via ResolveOpenRaffleOnShutdownAsync. A hard KILL (power
    //     loss, Task Manager) is not recoverable and the stakes stay debited — the same
    //     exposure the raffle has carried since it shipped. Betting defaults OFF.
    //
    // NOTHING here is a secret store, and nothing here is a wallet: every points move goes
    // through LoyaltyService (ChargeAsync / RefundAsync / PayoutManyAsync) so the
    // non-negativity guarantees live in one place. See DB.Polls.cs / PollsService.

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber / VIP /
    /// Moderator / Broadcaster / Regular). Kept independent of SongRoles / QuoteRoles /
    /// CounterRoles / LoyaltyRoles — the standing pattern — so the serialized Polls config
    /// can never be broken by another tool's change.
    /// </summary>
    public sealed class PollRoles
    {
        public bool Everyone { get; set; }
        public bool Subscriber { get; set; }
        public bool Vip { get; set; }
        public bool Moderator { get; set; }
        public bool Broadcaster { get; set; }

        /// <summary>The User-Management Regular group — a community-trust tier with no
        /// platform equivalent. Ticking it is opt-in; a blob written before the box
        /// existed simply lacks the key and deserializes to false.</summary>
        public bool Regular { get; set; }

        public PollRoles() { }
        public static PollRoles All() => new() { Everyone = true };
        public static PollRoles Mods() => new() { Moderator = true, Broadcaster = true };

        /// <summary>True when a chatter carrying these role flags passes the gate:
        /// Everyone OR any individually-ticked role they hold.</summary>
        public bool Allows(bool isSub, bool isVip, bool isMod, bool isBroadcaster, bool isRegular)
            => Everyone
            || (Subscriber && isSub)
            || (Vip && isVip)
            || (Moderator && isMod)
            || (Broadcaster && isBroadcaster)
            || (Regular && isRegular);
    }

    /// <summary>Where the tool is in its cycle. Mirrors the lowercase tokens published on
    /// the <c>poll.state</c> live-channel key.</summary>
    public enum PollState
    {
        /// <summary>No poll has run, or the last result has been retracted.</summary>
        Idle,
        /// <summary>Accepting votes (and bets, when betting is on).</summary>
        Open,
        /// <summary>Voting is over and the result is still being shown.</summary>
        Closed,
    }

    /// <summary>How the native-platform mirror should be driven for one poll.</summary>
    public enum PollMirrorMode
    {
        /// <summary>Chat-only — nothing is mirrored.</summary>
        None,
        /// <summary>A native Twitch POLL (a vote-only poll mirrors here).</summary>
        Poll,
        /// <summary>A native Twitch PREDICTION (a betting poll mirrors here, because a
        /// prediction is the platform's own betting surface).</summary>
        Prediction,
    }

    /// <summary>Which half of the mirror a request drives.</summary>
    public enum PollMirrorAction { OpenPoll, ClosePoll, OpenPrediction, ResolvePrediction }

    /// <summary>What the mirror seam actually did.</summary>
    public enum PollMirrorOutcome
    {
        /// <summary>The action was dispatched to the platform.</summary>
        Mirrored,
        /// <summary>The streamer never asked for a mirror on this poll.</summary>
        NotRequested,
        /// <summary>Streamer.bot is not connected, so nothing can be dispatched.</summary>
        NotConnected,
        /// <summary>The platform wrapper action this mirror needs is not available on the
        /// connected Streamer.bot (or its closing half is not), so the mirror is refused
        /// rather than half-performed.</summary>
        Unavailable,
        /// <summary>The poll's shape does not fit the platform surface (option count or
        /// duration outside what Twitch accepts).</summary>
        Unsupported,
    }

    /// <summary>One mirror instruction handed to the Hub-side seam.</summary>
    public readonly record struct PollMirrorRequest(
        PollMirrorAction Action,
        string Title,
        IReadOnlyList<string> Options,
        int DurationSeconds,
        int WinningIndex);

    /// <summary>One poll option, as the panel / overlay / nodes see it.</summary>
    public sealed class PollOptionView
    {
        /// <summary>Zero-based option index. <c>!vote 1</c> selects index 0 — the chat
        /// numbering is one-based because viewers count from one.</summary>
        public int Index { get; init; }
        public string Label { get; init; } = "";
        public int Votes { get; init; }
        /// <summary>Total points staked on this option (0 when betting is off).</summary>
        public long Stake { get; init; }
        /// <summary>Share of the total vote, 0-100, rounded to one decimal. 0 when no
        /// votes have been cast at all — never a divide-by-zero NaN.</summary>
        public double Percent { get; init; }
    }

    /// <summary>
    /// A read-only snapshot of the poll, taken under the service's lock so the panel, the
    /// read nodes and the live-channel publish all describe ONE instant rather than three
    /// interleaved ones.
    /// </summary>
    public sealed class PollSnapshot
    {
        public PollState State { get; init; } = PollState.Idle;
        public string Title { get; init; } = "";
        public IReadOnlyList<PollOptionView> Options { get; init; } = Array.Empty<PollOptionView>();
        public int TotalVotes { get; init; }
        /// <summary>The leading option's label while open, and the settled winner once
        /// closed. Empty on a tie or with no votes at all — never an arbitrary pick,
        /// because points ride on it.</summary>
        public string Leader { get; init; } = "";
        public int LeaderVotes { get; init; }
        public bool BettingEnabled { get; init; }
        /// <summary>Everything staked across every option.</summary>
        public long Pot { get; init; }
        /// <summary>Distinct viewers who have staked.</summary>
        public int BettorCount { get; init; }
        /// <summary>Whole seconds still to run, 0 once closed.</summary>
        public int SecondsLeft { get; init; }
        public int DurationSeconds { get; init; }
        public PollMirrorMode Mirror { get; init; } = PollMirrorMode.None;
        /// <summary>True only when the mirror was actually accepted by the platform seam —
        /// a requested-but-refused mirror reads false, which is what keeps the panel from
        /// claiming a Twitch poll that never existed.</summary>
        public bool MirrorEngaged { get; init; }
    }

    /// <summary>Why opening a poll was refused.</summary>
    public enum PollOpenOutcome
    {
        Opened,
        /// <summary>The tool is switched off.</summary>
        Disabled,
        /// <summary>A poll is already running.</summary>
        AlreadyOpen,
        /// <summary>Fewer than two distinct options were supplied.</summary>
        NotEnoughOptions,
        /// <summary>More options than the tool accepts (see <see cref="PollsConfig.MaxOptions"/>).</summary>
        TooManyOptions,
        /// <summary>Betting was asked for but the points economy cannot collect stakes
        /// (the Loyalty tool is off, or its currency table is missing).</summary>
        EconomyOff,
    }

    /// <summary>Result of opening a poll.</summary>
    public sealed class PollOpenResult
    {
        public PollOpenOutcome Outcome { get; init; } = PollOpenOutcome.Opened;
        public bool Ok => Outcome == PollOpenOutcome.Opened;
        public PollSnapshot? Snapshot { get; init; }
        /// <summary>What the mirror seam reported. <see cref="PollMirrorOutcome.NotRequested"/>
        /// when the streamer did not ask for one.</summary>
        public PollMirrorOutcome Mirror { get; init; } = PollMirrorOutcome.NotRequested;

        public static PollOpenResult Fail(PollOpenOutcome outcome) => new() { Outcome = outcome };
    }

    /// <summary>Why a vote was refused.</summary>
    public enum PollVoteOutcome
    {
        Counted,
        /// <summary>The vote replaced this viewer's earlier one (only with
        /// <see cref="PollsConfig.AllowVoteChange"/> on).</summary>
        Changed,
        /// <summary>The tool is switched off.</summary>
        Disabled,
        /// <summary>No poll is running.</summary>
        NoPoll,
        /// <summary>No voter could be resolved — a graph called the node with a blank User
        /// on a trigger that carries no chatter.</summary>
        NoUser,
        /// <summary>The option number does not name one of this poll's options.</summary>
        BadOption,
        /// <summary>This viewer already voted and changing a vote is not allowed.</summary>
        AlreadyVoted,
    }

    /// <summary>Result of one vote.</summary>
    public readonly record struct PollVoteResult(PollVoteOutcome Outcome, int Index, string Label, int Votes)
    {
        public bool Ok => Outcome is PollVoteOutcome.Counted or PollVoteOutcome.Changed;
        public static PollVoteResult Fail(PollVoteOutcome outcome) => new(outcome, -1, "", 0);
    }

    /// <summary>Why a bet was refused.</summary>
    public enum PollBetOutcome
    {
        Placed,
        /// <summary>The tool is switched off.</summary>
        Disabled,
        /// <summary>No poll is running.</summary>
        NoPoll,
        /// <summary>No bettor could be resolved — a graph called the node with a blank User
        /// on a trigger that carries no chatter.</summary>
        NoUser,
        /// <summary>This poll was opened without betting.</summary>
        BettingOff,
        /// <summary>The option number does not name one of this poll's options.</summary>
        BadOption,
        /// <summary>Not a positive whole number of points.</summary>
        BadAmount,
        /// <summary>Under the configured minimum stake.</summary>
        BelowMin,
        /// <summary>Over the configured maximum stake.</summary>
        AboveMax,
        /// <summary>This viewer has already staked on this poll — one bet each.</summary>
        AlreadyBet,
        /// <summary>The points economy cannot take the stake (Loyalty off / table missing).</summary>
        EconomyOff,
        /// <summary>The viewer cannot afford the stake.</summary>
        NoFunds,
        /// <summary>The charge threw. Treated as a refusal, and logged.</summary>
        Error,
    }

    /// <summary>Result of one bet. <see cref="Staked"/> is what was actually taken, which
    /// is what a refund must give back.</summary>
    public readonly record struct PollBetResult(PollBetOutcome Outcome, int Index, string Label, long Staked, long Pot)
    {
        public bool Ok => Outcome == PollBetOutcome.Placed;
        public static PollBetResult Fail(PollBetOutcome outcome) => new(outcome, -1, "", 0, 0);
    }

    /// <summary>How a betting poll's pot was resolved.</summary>
    public enum PollSettlementOutcome
    {
        /// <summary>Nothing to settle — betting was off, or nobody staked.</summary>
        NoStakes,
        /// <summary>The pot was paid out to the viewers who backed the winning option.</summary>
        Paid,
        /// <summary>Every stake was handed back: the vote tied, nobody voted at all, nobody
        /// backed the winning option, or the poll was cancelled.</summary>
        Refunded,
    }

    /// <summary>One viewer's share of a settled pot.</summary>
    public readonly record struct PollPayout(string User, long Amount);

    /// <summary>What a close / cancel did to the money.</summary>
    public sealed class PollSettlement
    {
        public PollSettlementOutcome Outcome { get; init; } = PollSettlementOutcome.NoStakes;
        /// <summary>Everything that was staked on the poll.</summary>
        public long Pot { get; init; }
        public IReadOnlyList<PollPayout> Payouts { get; init; } = Array.Empty<PollPayout>();
        /// <summary>How many of <see cref="Payouts"/> the money layer confirmed. A short
        /// count is a real failure and is logged at CriticalError — never swallowed.</summary>
        public int Applied { get; init; }
        /// <summary>Lowercase token published / raised: "none" | "paid" | "refunded".</summary>
        public string Token => Outcome switch
        {
            PollSettlementOutcome.Paid => "paid",
            PollSettlementOutcome.Refunded => "refunded",
            _ => "none",
        };
    }

    /// <summary>Why closing a poll did nothing.</summary>
    public enum PollCloseOutcome
    {
        Closed,
        /// <summary>The tool is switched off.</summary>
        Disabled,
        /// <summary>No poll was running (or one was already closed — close is idempotent).</summary>
        NoPoll,
    }

    /// <summary>Result of a close / cancel.</summary>
    public sealed class PollCloseResult
    {
        public PollCloseOutcome Outcome { get; init; } = PollCloseOutcome.Closed;
        public bool Ok => Outcome == PollCloseOutcome.Closed;
        public PollSnapshot? Snapshot { get; init; }
        public PollSettlement Settlement { get; init; } = new();
        /// <summary>True when this close was a cancel — no winner, every stake back.</summary>
        public bool Cancelled { get; init; }

        public static PollCloseResult Fail(PollCloseOutcome outcome) => new() { Outcome = outcome };
    }

    /// <summary>How a stake charge ended. Mirrors SongChargeOutcome — same economy, same
    /// three refusal reasons, kept per-tool so neither tool's vocabulary drifts into the
    /// other's.</summary>
    public enum PollChargeOutcome { Ok, EconomyOff, NoFunds, Error }

    /// <summary>Result of one stake charge. <see cref="Amount"/> is what was taken.</summary>
    public readonly record struct PollChargeResult(PollChargeOutcome Outcome, long Amount)
    {
        public bool Ok => Outcome == PollChargeOutcome.Ok;
        public static PollChargeResult Charged(long amount) => new(PollChargeOutcome.Ok, amount);
        public static PollChargeResult Fail(PollChargeOutcome outcome) => new(outcome, 0);
    }

    public sealed class PollsConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no chat commands, no poll, no
        /// live-channel publish, no Poll.On* events) until the streamer enables it.
        /// Default OFF.</summary>
        public bool Enabled { get; set; } = false;

        // ── Viewer command words (matched without the leading '!') ───────────
        /// <summary>Cast a vote: <c>!&lt;word&gt; &lt;number&gt;</c>.</summary>
        public string VoteCommand { get; set; } = "vote";

        /// <summary>Stake points on an outcome: <c>!&lt;word&gt; &lt;number&gt; &lt;amount&gt;</c>.</summary>
        public string BetCommand { get; set; } = "bet";

        // ── Role gating (independent-checkmark sets) ─────────────────────────
        /// <summary>Who may vote. Default: everyone.</summary>
        public PollRoles VoteRoles { get; set; } = PollRoles.All();

        /// <summary>Who may stake points. Default: everyone.</summary>
        public PollRoles BetRoles { get; set; } = PollRoles.All();

        // ── Poll shape ──────────────────────────────────────────────────────
        /// <summary>Length of a poll opened without an explicit duration, in seconds.</summary>
        public int DefaultDurationSeconds { get; set; } = 60;

        /// <summary>Most options one poll may carry. Twitch's own poll surface caps at 5,
        /// which is why the mirror refuses a wider poll — but a chat-only poll is free to
        /// be wider, so this is a separate number rather than a hard-coded 5.</summary>
        public int MaxOptions { get; set; } = 8;

        /// <summary>When true a viewer may re-vote and their earlier vote is moved. When
        /// false the first vote stands and a second one is refused.</summary>
        public bool AllowVoteChange { get; set; } = false;

        /// <summary>Post the poll's opening line (question + numbered options) and its
        /// result in chat. Off suits a streamer driving the whole thing from an overlay
        /// widget, and is the only way to run a poll silently.</summary>
        public bool AnnounceInChat { get; set; } = true;

        // ── Betting ─────────────────────────────────────────────────────────
        /// <summary>Whether a poll may be opened with betting at all. A second gate above
        /// the per-poll flag, so a streamer can keep polls while switching the money off in
        /// one place. Default OFF — the tool is useful as a plain poll.</summary>
        public bool BettingEnabled { get; set; } = false;

        /// <summary>Smallest accepted stake, in points.</summary>
        public long MinBet { get; set; } = 10;

        /// <summary>Largest accepted stake, in points. 0 or less = no cap.</summary>
        public long MaxBet { get; set; } = 10000;

        // ── Native platform mirror ──────────────────────────────────────────
        /// <summary>Default for the per-poll mirror toggle: whether a poll opened without
        /// an explicit choice also drives Twitch's own poll / prediction surface. The
        /// mirror is capability-gated at RUNTIME — see PollsService — so turning this on
        /// can never break the chat poll.</summary>
        public bool MirrorToNative { get; set; } = false;

        // ── Overlay ─────────────────────────────────────────────────────────
        /// <summary>Publish the <c>poll.*</c> Overlay Live Channel keys a Var.Live /
        /// List.Live widget graph reads. Off makes the tool chat-only.</summary>
        public bool PublishOverlay { get; set; } = true;

        /// <summary>How long the closed result keeps painting before the <c>poll.*</c> keys
        /// are RETRACTED (published as null and left to decay). 0 retracts immediately.
        /// A finished poll must not paint its last result forever.</summary>
        public int ResultLingerSeconds { get; set; } = 20;
    }
}
