using System;
using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Song Request pre-build tool — a YouTube-backed viewer request
    // queue (!sr <link | id | search phrase>) with caps, an optional points price and an
    // optional pending-approval lane.
    //
    // PERSISTENCE SPLIT, and it is deliberate:
    //   * The tool CONFIG (command words, role gates, caps, price, approval) is one JSON
    //     blob in the private SongRequestConfig SYSTEM table — the Scheduling/Alerts shape.
    //   * The QUEUE is NOT persisted at all. It lives only in SongRequestService for the
    //     lifetime of the Hub process. A queue entry names a track the overlay player is
    //     part-way through, and a restart cannot restore playback position — so a
    //     persisted queue would reload with an entry marked Playing that nothing is
    //     playing, and the honest live-channel state would be a lie from the first tick.
    //     Every competitor clears the request queue on restart for the same reason.
    //     Graphs still reach the queue through the Song.* read nodes, which is the
    //     Architect-first reachability that actually matters here.
    //
    // NOTHING in this file is a secret store: the optional YouTube Data API key lives on
    // AppConfig behind DpapiProtectedStringConverter, because this blob is written to
    // SQLite unwrapped.

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber / VIP /
    /// Moderator / Broadcaster / Regular). Kept independent of QuoteRoles / CounterRoles /
    /// LoyaltyRoles — the standing pattern — so the serialized Song Request config can
    /// never be broken by another tool's change.
    /// </summary>
    public sealed class SongRoles
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

        public SongRoles() { }
        public static SongRoles All() => new() { Everyone = true };
        public static SongRoles Mods() => new() { Moderator = true, Broadcaster = true };

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

    /// <summary>Where one request sits in the tool's lifecycle.</summary>
    public enum SongRequestStatus
    {
        /// <summary>Waiting for a moderator verdict (only reachable with RequireApproval on).</summary>
        Pending,
        /// <summary>Approved and waiting its turn.</summary>
        Queued,
        /// <summary>The track the player has been told to play.</summary>
        Playing,
    }

    /// <summary>Transport state of the (overlay-side) player, as Hub understands it.</summary>
    public enum SongPlayerState
    {
        /// <summary>Nothing selected — no track has been handed to the player.</summary>
        Idle,
        /// <summary>A track is selected and the player is meant to be playing it.</summary>
        Playing,
        /// <summary>A track is selected and the player is meant to be holding it.</summary>
        Paused,
    }

    /// <summary>
    /// One request. Never persisted (see the file header) — this is the in-memory
    /// projection the service, the panel, the chat verbs and the Song.* nodes pass around.
    /// </summary>
    public sealed class SongRequestEntry
    {
        /// <summary>Stable per-session id, so a panel row and a chat verb address the same
        /// entry even as positions shift underneath them.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The 11-character YouTube video id. The ONLY identifier the overlay
        /// player needs, and the only one Phoenix can resolve without an API key.</summary>
        public string VideoId { get; set; } = "";

        /// <summary>Video title, or "" when no YouTube Data API key was configured and the
        /// request came in as a bare link/id (Phoenix will not scrape for it).</summary>
        public string Title { get; set; } = "";

        /// <summary>Length in seconds, or 0 when unknown (same no-API-key case as
        /// <see cref="Title"/>). A 0 means the max-duration cap cannot be applied — it is
        /// skipped rather than guessed.</summary>
        public int DurationSeconds { get; set; }

        /// <summary>Requester's display name — what chat and the overlay show.</summary>
        public string RequestedBy { get; set; } = "";

        /// <summary>Requester's platform login, lower-cased. The identity every per-user
        /// cap, !when lookup, !wrongsong removal and points refund keys on, because the
        /// display name is operator-styled and can differ from the login entirely.</summary>
        public string RequestedByLogin { get; set; } = "";

        /// <summary>Unix ms at request time (queue ordering is insertion order; this is
        /// for the panel's "how long has this been waiting").</summary>
        public long RequestedAtUnixMs { get; set; }

        public SongRequestStatus Status { get; set; } = SongRequestStatus.Queued;

        /// <summary>Points actually charged for this request (0 when the tool is free).
        /// Carried on the entry so a removal / denial refunds exactly what was taken, even
        /// if the streamer changed the price in between.</summary>
        public long PointsPaid { get; set; }

        /// <summary>What chat and the panel print: the real title when one is known, the
        /// bare video id otherwise. Never invents a title.</summary>
        public string DisplayTitle => Title.Length > 0 ? Title : VideoId;
    }

    public sealed class SongRequestConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no chat commands, no queue
        /// mutation, no live-channel publish, no Song.On* events) until the streamer
        /// enables it. Default OFF.</summary>
        public bool Enabled { get; set; } = false;

        // ── Viewer command words (matched without the leading '!') ───────────
        /// <summary>Request a song: <c>!&lt;word&gt; &lt;link | video id | search phrase&gt;</c>.</summary>
        public string RequestCommand { get; set; } = "sr";

        /// <summary>What is playing right now.</summary>
        public string CurrentCommand { get; set; } = "song";

        /// <summary>What is up next.</summary>
        public string NextCommand { get; set; } = "next";

        /// <summary>Where the caller's own request sits in the queue.</summary>
        public string WhenCommand { get; set; } = "when";

        /// <summary>Drop the caller's most recent still-waiting request (mis-typed link).</summary>
        public string WrongSongCommand { get; set; } = "wrongsong";

        /// <summary>Vote to skip the current track.</summary>
        public string VoteSkipCommand { get; set; } = "voteskip";

        /// <summary>Read the player volume; with a number, set it (mod-gated).</summary>
        public string VolumeCommand { get; set; } = "volume";

        // ── Moderator command words ─────────────────────────────────────────
        /// <summary>Skip the current track and move to the next one.</summary>
        public string SkipCommand { get; set; } = "skip";

        /// <summary>Hold the current track.</summary>
        public string PauseCommand { get; set; } = "pause";

        /// <summary>Resume the held track — or start the queue when nothing is selected.</summary>
        public string PlayCommand { get; set; } = "play";

        /// <summary>Remove one waiting request by its queue position.</summary>
        public string RemoveCommand { get; set; } = "removesong";

        /// <summary>Empty the queue (the current track keeps playing).</summary>
        public string ClearCommand { get; set; } = "srclear";

        // ── Role gating (independent-checkmark sets) ─────────────────────────
        /// <summary>Who may REQUEST — the tool's "min user level". Default: everyone.</summary>
        public SongRoles RequestRoles { get; set; } = SongRoles.All();

        /// <summary>Who may ask the read-only questions (!song / !next / !when / !volume
        /// with no argument). Default: everyone.</summary>
        public SongRoles ViewRoles { get; set; } = SongRoles.All();

        /// <summary>Who may vote to skip. Default: everyone.</summary>
        public SongRoles VoteSkipRoles { get; set; } = SongRoles.All();

        /// <summary>Who may drive the player and the queue (skip / pause / play /
        /// removesong / srclear / approve / deny, and !volume WITH a number).
        /// Default: mods + broadcaster.</summary>
        public SongRoles ModRoles { get; set; } = SongRoles.Mods();

        // ── Caps ────────────────────────────────────────────────────────────
        /// <summary>Maximum waiting requests (the playing track is not counted).
        /// 0 or less = unlimited.</summary>
        public int MaxQueueLength { get; set; } = 25;

        /// <summary>Maximum waiting requests ONE viewer may hold at a time.
        /// 0 or less = unlimited.</summary>
        public int MaxPerUser { get; set; } = 2;

        /// <summary>Longest track accepted, in seconds. 0 or less = no cap.
        /// ★ Only enforceable when a YouTube Data API key is configured — without one
        /// Phoenix cannot read a video's length and the cap is SKIPPED rather than
        /// guessed. The panel says so.</summary>
        public int MaxDurationSeconds { get; set; } = 600;

        /// <summary>Per-viewer cooldown between requests, in seconds. 0 = none.</summary>
        public int RequestCooldownSeconds { get; set; } = 0;

        // ── Price ───────────────────────────────────────────────────────────
        /// <summary>Loyalty points charged per accepted request. 0 = free.
        /// A price the points economy cannot collect (Loyalty tool switched off) REFUSES
        /// the request with a reply that names the reason — it never quietly gives the
        /// request away for free against the streamer's setting.</summary>
        public long PointCost { get; set; } = 0;

        /// <summary>Percentage taken off <see cref="PointCost"/> for subscribers, 0-100.
        /// Rounded up, so a 33% discount on 10 costs 7 rather than 6.7.</summary>
        public int SubDiscountPercent { get; set; } = 0;

        // ── Moderation ──────────────────────────────────────────────────────
        /// <summary>When true, an accepted request lands as Pending and waits for a
        /// moderator's approve/deny before it can be played. Points are charged at request
        /// time and refunded on a denial.</summary>
        public bool RequireApproval { get; set; } = false;

        /// <summary>Distinct !voteskip voters needed to skip the current track.
        /// 0 or less disables vote-skipping.</summary>
        public int VoteSkipThreshold { get; set; } = 5;

        // ── Player ──────────────────────────────────────────────────────────
        /// <summary>Volume 0-100 the overlay player is told to use. Persisted so it
        /// survives a restart; !volume and the panel both write it.</summary>
        public int Volume { get; set; } = 50;
    }

    /// <summary>What the Hub-side YouTube resolver was asked for. Exactly one of the two
    /// is set: a <see cref="VideoId"/> already parsed out of a link/id (metadata lookup),
    /// or a free-text <see cref="Query"/> (search).</summary>
    public readonly record struct SongLookupRequest(string VideoId, string Query);

    /// <summary>How a YouTube resolve attempt ended.</summary>
    public enum SongLookupOutcome
    {
        /// <summary>Resolved — VideoId / Title / DurationSeconds are populated.</summary>
        Ok,
        /// <summary>No YouTube Data API key is configured. A metadata lookup degrades to
        /// id-only; a SEARCH cannot proceed at all (Phoenix never scrapes).</summary>
        NoApiKey,
        /// <summary>The API answered, and there is no such video / no search hit.</summary>
        NotFound,
        /// <summary>The video exists but the uploader disabled embedding, so an overlay
        /// player can never play it.</summary>
        NotEmbeddable,
        /// <summary>The lookup failed (network, quota, malformed answer). A metadata
        /// lookup degrades to id-only; a search is refused.</summary>
        Error,
    }

    /// <summary>Result of one YouTube resolve.</summary>
    public readonly record struct SongLookupResult(
        SongLookupOutcome Outcome, string VideoId, string Title, int DurationSeconds)
    {
        public static SongLookupResult Ok(string id, string title, int seconds)
            => new(SongLookupOutcome.Ok, id ?? "", title ?? "", seconds < 0 ? 0 : seconds);
        public static SongLookupResult Fail(SongLookupOutcome outcome)
            => new(outcome, "", "", 0);
    }

    /// <summary>How a points charge for a request ended.</summary>
    public enum SongChargeOutcome
    {
        /// <summary>Charged (or the price was 0).</summary>
        Ok,
        /// <summary>A price is configured but the points economy cannot collect it —
        /// the Loyalty tool is switched off, or its currency table is missing.</summary>
        EconomyOff,
        /// <summary>The viewer cannot afford it.</summary>
        NoFunds,
        /// <summary>The charge threw. Treated exactly like NoFunds by the caller (the
        /// request is refused), but logged.</summary>
        Error,
    }

    /// <summary>Result of one points charge. <see cref="Amount"/> is what was actually
    /// taken, which is what a later refund must give back.</summary>
    public readonly record struct SongChargeResult(SongChargeOutcome Outcome, long Amount)
    {
        public bool Ok => Outcome == SongChargeOutcome.Ok;
        public static SongChargeResult Charged(long amount) => new(SongChargeOutcome.Ok, amount);
        public static SongChargeResult Fail(SongChargeOutcome outcome) => new(outcome, 0);
    }

    /// <summary>Why a request was refused, so chat, the panel and a node can all render
    /// the same verdict without re-deriving it from a string.</summary>
    public enum SongRequestOutcome
    {
        /// <summary>Queued (or parked as Pending when approval is on).</summary>
        Accepted,
        /// <summary>The tool is switched off.</summary>
        Disabled,
        /// <summary>Nothing usable in the argument.</summary>
        EmptyRequest,
        /// <summary>Not a YouTube link/id, and searching needs an API key that isn't set.</summary>
        SearchNeedsApiKey,
        /// <summary>The link/id/phrase resolved to nothing.</summary>
        NotFound,
        /// <summary>Embedding is disabled on that video.</summary>
        NotEmbeddable,
        /// <summary>Longer than the configured maximum.</summary>
        TooLong,
        /// <summary>The queue is full.</summary>
        QueueFull,
        /// <summary>The requester already holds their maximum number of waiting requests.</summary>
        UserLimit,
        /// <summary>The requester is still on their per-viewer cooldown.</summary>
        OnCooldown,
        /// <summary>Already in the queue (or currently playing).</summary>
        Duplicate,
        /// <summary>A price is set but the points economy can't collect it.</summary>
        EconomyOff,
        /// <summary>The requester can't afford the price.</summary>
        NoFunds,
    }

    /// <summary>Everything a caller needs after asking for a song.</summary>
    public sealed class SongRequestResult
    {
        public SongRequestOutcome Outcome { get; init; } = SongRequestOutcome.Accepted;
        public bool Ok => Outcome == SongRequestOutcome.Accepted;

        /// <summary>The accepted entry (null on every refusal).</summary>
        public SongRequestEntry? Entry { get; init; }

        /// <summary>1-based place in the waiting queue, or 0 when not queued.</summary>
        public int Position { get; init; }

        /// <summary>Points actually charged.</summary>
        public long Charged { get; init; }

        /// <summary>Seconds still to wait on the per-viewer cooldown (OnCooldown only).</summary>
        public int CooldownRemaining { get; init; }

        public static SongRequestResult Fail(SongRequestOutcome outcome, int cooldownRemaining = 0)
            => new() { Outcome = outcome, CooldownRemaining = cooldownRemaining };
    }

    /// <summary>Result of one !voteskip.</summary>
    public readonly record struct SongVoteSkipResult(bool Counted, bool Skipped, int Votes, int Needed);

    /// <summary>A read-only snapshot of the player + queue, taken under the service's lock
    /// so the panel, the read nodes and the live-channel publish all describe ONE instant
    /// rather than three interleaved ones.</summary>
    public sealed class SongPlayerSnapshot
    {
        public SongPlayerState State { get; init; } = SongPlayerState.Idle;
        public SongRequestEntry? Current { get; init; }
        public int Volume { get; init; }
        /// <summary>Increments every time a DIFFERENT track is handed to the player. The
        /// overlay player uses it to tell "load this video" from "resume what you have",
        /// which a video-id compare cannot do when the same song is requested twice in a
        /// row.</summary>
        public long PlayToken { get; init; }
        public IReadOnlyList<SongRequestEntry> Queue { get; init; } = Array.Empty<SongRequestEntry>();
        public int SkipVotes { get; init; }
    }
}
