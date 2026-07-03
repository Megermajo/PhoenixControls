namespace Phoenix.Controls.Shared.WinUI.Contracts;

public record LiveFeedEntry(
    DateTimeOffset Timestamp,
    LiveFeedKind   Kind,
    string         Who,
    string         Detail);

public enum LiveFeedKind { Sub, Chat, Redeem, Raid, Visual, Follow }

// Redeem + Follow added so the filter
// can target every kind LiveFeedKind emits — without them the feed
// surfaced Redeem / Follow rows the UI had no way to filter to. New
// values appended (not inserted) to preserve binary compatibility for
// any persisted filter state that round-trips the underlying int.
public enum LiveFeedFilter { All, Chat, Subs, Raids, Visual, Redeem, Follow }

// Role-vs-flags invariant — the model carries BOTH a single
// precedence-collapsed <see cref="ChatRole"/> AND the four original
// Twitch IRC tag-bag bools (IsBroadcaster / IsMod / IsVip / IsSubscriber).
// They are redundant by design, not a bug:
//
//   * The four bool flags are the CANONICAL source-of-truth — they map
//     1:1 to the Twitch payload and survive role combinations
//     (a Mod who is also a Sub, a Broadcaster who is also a VIP, etc.).
//     RoleColorBrush.Resolve / ResolveGeometry / ResolveLabel key off
//     them directly.
//
//   * <see cref="ChatRole"/> is a DERIVED precedence collapse
//     (Broadcaster > Mod > Vip > Sub > Viewer / Bot) used for coarse
//     single-axis classification (filter chip "show only mod messages",
//     legacy LiveFeed kind tagging). ChatSource.cs is the producer that
//     derives Role from the flags before constructing the record; do not
//     emit a ChatMessage whose Role disagrees with the flags
//     (i.e. ChatRole.Broadcaster requires IsBroadcaster=true, etc.) —
//     downstream rendering assumes flags ⇒ role consistency.
//
// If you need to add a new role category, add it to the flag set first,
// then extend ChatRole + ChatSource.ToRole. Never flip Role without
// flipping the matching flag.
// Distinct from `Phoenix.Controls.Shared.Models.ChatMessage`
// (a class with `Message`, used by the script runtime). THIS record is the WinUI-CHROME-FACING
// chat envelope — its `Body` field is what `ChatRowVm.Body` reads. The script runtime never
// sees this type; the Hub `ChatSource` adapter translates Models.ChatMessage → this record on
// the way to the panel VM. The dual-type definition has tripped readers up;
// this comment is preventive grep-bait so future spelunkers don't repeat the confusion.
public record ChatMessage(
    DateTimeOffset Timestamp,
    ChatRole       Role,
    string         Username,
    string         Body,
    bool           IsBroadcaster = false,
    bool           IsMod         = false,
    bool           IsVip         = false,
    bool           IsSubscriber  = false);

/// <summary>
/// Precedence-collapsed chat-role classification derived from the four
/// canonical role bools on <see cref="ChatMessage"/>. Order matters for
/// any code that compares roles as ints (none today, but the producer
/// in ChatSource.ToRole walks values in declaration order to pick
/// the highest-precedence flag). See the comment block above ChatMessage
/// for the canonical-vs-derived invariant.
/// </summary>
public enum ChatRole { Broadcaster, Mod, Vip, Sub, Viewer, Bot }

public record ScriptStatus(
    string      Path,
    string      Name,
    ScriptState State,
    bool        Enabled,
    TimeSpan    LastCpu,
    long        LastRamBytes,
    int         RunCount,
    string?     LastError);

public enum ScriptState { Idle, Running, Errored, Queued }

public record SystemLogEntry(
    DateTimeOffset Timestamp,
    SystemLogLevel Level,
    string         Source,
    string         Message,
    Exception?     Exception);

[Flags]
public enum SystemLogLevel
{
    None  = 0,
    Debug = 1,
    Info  = 2,
    Warn  = 4,
    Error = 8,
    All   = Debug | Info | Warn | Error,
}

public enum ConnectionState { Disconnected, Connecting, Connected, Degraded, Errored }

/// <summary>
/// Identifies which of the three transport channels tracked by
/// <see cref="IConnectionStatus"/> raised a transition.
/// </summary>
/// <remarks>
/// Mirrors the three live properties on the contract
/// (<see cref="IConnectionStatus.StreamerBot"/>,
/// <see cref="IConnectionStatus.HudOverlay"/>,
/// <see cref="IConnectionStatus.IpcBus"/>). Subscribers that only care
/// about one channel can route on this enum instead of re-snapshotting
/// the aggregator.
/// </remarks>
public enum ConnectionChannel
{
    StreamerBot,
    HudOverlay,
    IpcBus,
}

/// <summary>
/// Payload for <see cref="IConnectionStatus.StateChanged"/>. Carries the
/// channel that transitioned plus its previous and current state so
/// subscribers can diff without re-snapshotting the aggregator.
/// </summary>
/// <remarks>
/// Previously the event was a bare <c>EventHandler</c> with an
/// <see cref="EventArgs.Empty"/> payload; subscribers had to re-read all
/// three channels on every fire to figure out what changed. The new
/// payload makes the transition self-describing — and the producer now
/// raises one event per real per-channel transition (previously a single
/// fire coalesced multiple-channel ticks).
/// </remarks>
public sealed record ConnectionStateChange(
    ConnectionChannel Channel,
    ConnectionState   Previous,
    ConnectionState   Current);

// ── Giveaway (Hub Giveaway page) ────────────────────────────────────────
// UI-facing DTOs consumed by the Hub.WinUI Giveaway page. The runtime/DB
// representation is Phoenix.Controls.Shared.Models.Giveaway / GiveawayEntrant;
// HubServices maps runtime → these records on the way to the page VM — same
// split as Models.ChatMessage → Contracts.ChatMessage above.

public enum GiveawayStatus { Open, Closed, Drawn }

public record GiveawayInfo(
    long           Id,
    string         Key,
    string         Title,
    GiveawayStatus Status,
    string         OpenedAt,
    string?        ClosedAt,
    string         OpenedBy,
    bool           IsDefault,
    int            Entrants,
    int            Tickets,
    string         Avg,           // pre-formatted "tickets / entrant"
    string         LastEntry,     // pre-formatted relative-ish string
    string         EntryCommand,
    int            TicketsPerMessage,
    int            SubscriberBonus,
    int            CapPerUser,    // 0 = unlimited
    string         DrawMethod,
    string         Winners);

public record GiveawayEntrantInfo(
    string   Username,
    ChatRole Role,
    int      Tickets,
    string   LastEntry);

// Activity log row scoped to one giveaway. Kind is "INF" or "WIN" (WIN rows
// render in ember per the design's activity log).
public record GiveawayActivityEntry(
    string Time,
    string Kind,
    string Message);
