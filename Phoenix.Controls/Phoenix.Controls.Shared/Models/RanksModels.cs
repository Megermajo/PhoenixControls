using System;
using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Ranks pre-build tool (watch-time / points thresholds →
    // named ranks, a live leaderboard, and an optional User-Management group grant).
    // Mirrors the Loyalty / Counters / Alerts config shape: a single JSON blob
    // (RanksConfig) is the tool-private SYSTEM table, while the DATA lives in OPEN
    // user tables so db.* scripts keep full access —
    //
    //   * "WatchTime" (name/minutes)  — accrued watch minutes. This tool READS the table;
    //     it does not fill it and no longer owns the switch that decides whether anything
    //     does. Recording hours is a background data source (ViewerPresenceService, always
    //     on, gated by AppConfig.WatchTimeTrackingEnabled / WatchTimeOnlyWhenLive), because
    //     hours are worth having on record whether or not a streamer ever opens a ladder —
    //     and while the accrual lived here, a default install recorded nothing at all.
    //   * "Ranks" (name/rank/value)   — the LAST rank each viewer was seen at, which is
    //     what turns "their value crossed a threshold" into a one-shot Rank.OnRankUp
    //     instead of an announcement on every tick, and what survives a Hub restart.
    //
    // Neither table is auto-populated for viewers nobody has evaluated; a missing row
    // reads as 0 / unranked, which is the honest answer.

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber /
    /// VIP / Moderator / Broadcaster / Regular). Kept independent of LoyaltyRoles /
    /// CounterRoles so the serialized Ranks config never couples to another tool's
    /// naming. All six properties are mandatory: RoleCheckRow binds by NAME against an
    /// <c>object?</c>, so a missing one renders, ticks and silently never persists.
    /// </summary>
    public sealed class RankRoles
    {
        public bool Everyone { get; set; }
        public bool Subscriber { get; set; }
        public bool Vip { get; set; }
        public bool Moderator { get; set; }
        public bool Broadcaster { get; set; }

        /// <summary>The User-Management Regular group — a community-trust tier with no
        /// platform equivalent. The whole config is one JSON blob, so a blob written
        /// before this box existed simply lacks the key and deserializes to false.</summary>
        public bool Regular { get; set; }

        public RankRoles() { }
        public static RankRoles All() => new() { Everyone = true };
        public static RankRoles Mods() => new() { Moderator = true, Broadcaster = true };

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

    /// <summary>
    /// Which number the ladder compares against. A plain string rather than an enum
    /// because the whole config round-trips through JSON and an unrecognised value
    /// must degrade to a working default instead of throwing — the same reason
    /// <c>LoyaltyEarnConfig.MultiplierStacking</c> is a string.
    /// </summary>
    public static class RankMetrics
    {
        /// <summary>Accrued watch MINUTES from the open "WatchTime" table.</summary>
        public const string WatchTime = "watchtime";
        /// <summary>The viewer's Loyalty balance, read from that tool's OPEN balance table.</summary>
        public const string Points = "points";

        /// <summary>Normalizes any stored spelling onto one of the two constants;
        /// anything unrecognised (including null) reads as watch time.</summary>
        public static string Normalize(string? raw)
            => string.Equals((raw ?? "").Trim(), Points, StringComparison.OrdinalIgnoreCase)
                ? Points
                : WatchTime;

        /// <summary>The <c>{unit}</c> token's value for a metric — "minutes" for watch
        /// time, and the streamer's own currency noun for points (supplied by the
        /// caller, because only the Hub knows what they named it).</summary>
        public static string UnitFor(string metric, string currencyPlural)
            => string.Equals(metric, Points, StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(currencyPlural) ? "points" : currencyPlural)
                : "minutes";
    }

    /// <summary>
    /// One rung of the ladder. <see cref="Threshold"/> is expressed in the METRIC's own
    /// unit — minutes for watch time, points for points — so there is exactly one number
    /// per row and no hidden conversion; the panel labels the column with the live unit
    /// and shows the hours equivalent beside a watch-time threshold.
    /// </summary>
    public sealed class RankDef
    {
        /// <summary>The rank's display name — what chat and the overlay print.</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Minimum metric value for this rank (inclusive). The highest ENABLED threshold
        /// &lt;= the viewer's value wins; ties resolve to the first in list order, exactly
        /// like <c>AlertsService.PickTier</c>.
        ///
        /// ★ A viewer whose value is 0 is UNRANKED whatever this says. Zero means "no row in
        /// the metric table" — a viewer the tool has never recorded, which is also what any
        /// name somebody merely typed into chat reads as — so a rung left at 0 is not a rank
        /// everybody silently holds. See <c>RanksService.ResolveStanding</c>. A rung at 0 is
        /// still perfectly usable as the bottom label; it just needs one minute (or one
        /// point) on the clock to be awarded.
        /// </summary>
        public long Threshold { get; set; }

        /// <summary>A disabled rung is skipped by the ladder entirely (it neither
        /// matches nor blocks a lower one).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Optional User-Management GROUP a viewer is added to when they reach this rank.
        /// Empty means no grant. Matched against the four standard groups (Moderator /
        /// VIP / Subscriber / Regular) and then the streamer's custom groups; a name that
        /// resolves to no group is a logged no-op, never an auto-created group — creating
        /// another tool's config behind the streamer's back is exactly the line every
        /// pre-build node holds.
        ///
        /// The grant is ADDITIVE ONLY and is never taken back: a viewer who spends their
        /// points keeps the right, because silently stripping a Moderator right the moment
        /// a balance dipped is a far worse surprise than a right that outlives its rank.
        /// </summary>
        public string GrantGroup { get; set; } = "";
    }

    /// <summary>
    /// Where the ladder reads its number from: an OPEN user table plus the column inside
    /// it. Both metrics point at a table a <c>db.*</c> script can also write, which is
    /// exactly why this is passed around as data rather than branched on at every read —
    /// the persistence layer validates the identifier and coerces the cell once, in one
    /// place, for both. Built by the <c>DB.WatchTimeSource</c> / <c>DB.PointsSource</c>
    /// factories so no caller outside the persistence layer ever spells a column name.
    /// </summary>
    public readonly record struct RankMetricSource(string Table, string Column);

    /// <summary>One row of the rank leaderboard. <c>Position</c>, deliberately NOT
    /// "Rank": in this tool "rank" is the NAME of a tier, while the ladder position is a
    /// number — the two words already mean different things across the suite (a
    /// LoyaltyStanding's Rank is a position, the Loyalty.Leaderboard widget's Rank pin is
    /// a position), and reusing it here would make a Ranks graph read two ways.</summary>
    public readonly record struct RankStanding(int Position, string Name, long Value, string Rank);

    public sealed class RanksConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no chat commands, no ladder
        /// evaluation, no rank-up events, no group grants, no overlay push) until the
        /// streamer enables it. Default OFF. It no longer reaches watch-time RECORDING:
        /// minutes accrue in the background whatever this says, which is the point of the
        /// move — see the note above the announcement block.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Which number the ladder compares against — see <see cref="RankMetrics"/>.</summary>
        public string Metric { get; set; } = RankMetrics.WatchTime;

        /// <summary>The ladder, in the streamer's own order. Empty = the tool has nothing
        /// to award and every viewer reads as unranked.</summary>
        public List<RankDef> Ranks { get; set; } = new();

        // ── Watch-time accrual is NOT configured here ───────────────────────
        // `TrackWatchTime` and `WatchTimeOnlineOnly` used to live on this config. They are
        // now AppConfig.WatchTimeTrackingEnabled and AppConfig.WatchTimeOnlyWhenLive,
        // because recording hours stopped being something a tool owns: the accrual runs in
        // the always-on ViewerPresenceService and feeds the User-Management watch-hour
        // rules and this ladder alike, so a switch that lived inside one pre-build tool
        // decided for all of them — and left a default install (Ranks OFF) recording
        // nothing whatsoever.
        //
        // No migration is needed and none is provided. The config is one JSON blob: a blob
        // written before this move simply carries two keys nothing deserializes into, which
        // System.Text.Json ignores, and the next save drops them. The streamer's intent is
        // not silently carried over either — the AppConfig defaults (tracking ON, live-only
        // ON) are the shipped answer for everyone, which is strictly MORE recording than
        // the old default-OFF tool gate ever produced.

        // ── Rank-up announcement ────────────────────────────────────────────
        public bool AnnounceEnabled { get; set; } = true;

        /// <summary>Tokens: {user} {rank} {value} {unit}. Empty posts nothing (the
        /// Rank.OnRankUp event and the group grant still happen).</summary>
        public string AnnounceMessage { get; set; } = "{user} just reached {rank}!";

        // ── Built-in chat commands ──────────────────────────────────────────
        public bool ChatEnabled { get; set; } = true;

        /// <summary>Command word for "what am I?" — <c>!rank</c> (optionally
        /// <c>!rank &lt;user&gt;</c>). Deliberately not <c>!top</c>: the Loyalty tool owns
        /// that word and runs first in the built-in dispatch, so a Ranks <c>!top</c> would
        /// be swallowed with no log anywhere.</summary>
        public string RankCommand { get; set; } = "rank";

        /// <summary>Command word for the ladder leaderboard — <c>!ranks</c>.</summary>
        public string TopCommand { get; set; } = "ranks";

        /// <summary>Who may use the two read commands. Default: everyone.</summary>
        public RankRoles ViewRoles { get; set; } = RankRoles.All();

        /// <summary>Per-user cooldown (seconds) across both commands; 0 disables.</summary>
        public int CooldownSeconds { get; set; } = 5;

        /// <summary>Reply when the viewer holds a rank and a higher one exists.
        /// Tokens: {user} {rank} {value} {unit} {next} {needed}.</summary>
        public string RankReplyMessage { get; set; }
            = "{user} is {rank} with {value} {unit} — {needed} more {unit} to reach {next}.";

        /// <summary>Reply when the viewer holds the HIGHEST rank (there is no {next},
        /// so a template naming one would print an empty word). Tokens: {user} {rank}
        /// {value} {unit}.</summary>
        public string TopRankReplyMessage { get; set; }
            = "{user} is {rank} with {value} {unit} — the top rank.";

        /// <summary>Reply when the viewer is below the lowest rung.
        /// Tokens: {user} {value} {unit} {next} {needed}.</summary>
        public string UnrankedReplyMessage { get; set; }
            = "{user} has {value} {unit} — {needed} more to reach {next}.";

        /// <summary>
        /// Reply when there is no rung to talk about at all — a freshly enabled tool whose
        /// ladder is still empty, or one whose only rungs are disabled. Its own branch
        /// because the unranked template names a {next} rank: with nothing above the viewer
        /// it renders as "majo has 0 minutes — 0 more to reach ." and a sentence trailing off
        /// into a missing name reads as a broken tool rather than an unfinished one.
        /// Tokens: {user} {value} {unit}.
        /// </summary>
        public string NoLadderReplyMessage { get; set; }
            = "{user} has {value} {unit}, but no ranks have been set up yet.";

        /// <summary>Reply for the ladder command. Tokens: {count} {list}.</summary>
        public string TopReplyMessage { get; set; } = "Top {count}: {list}";

        /// <summary>Reply for the ladder command when nobody has a value yet.</summary>
        public string TopEmptyMessage { get; set; } = "Nobody is on the ladder yet.";

        /// <summary>How many places the <c>!ranks</c> reply and the Rank.Top node print.</summary>
        public int LeaderboardSize { get; set; } = 5;

        // ── Overlay ─────────────────────────────────────────────────────────
        /// <summary>Publish <c>rank.leaderboard</c> / <c>rank.metric</c> into the Overlay
        /// Live Channel. Gated by the master toggle too — see
        /// <c>RanksService.PublishOverlay</c> for why a dormant tool must publish nothing
        /// rather than a frozen board.</summary>
        public bool OverlayEnabled { get; set; } = true;

        /// <summary>How many places the overlay board carries (1..100).</summary>
        public int OverlaySize { get; set; } = 5;
    }
}
