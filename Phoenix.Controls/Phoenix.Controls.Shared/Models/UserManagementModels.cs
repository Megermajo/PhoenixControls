using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the User-Management pre-build tool — four parts:
    //
    //   1. WELCOMING — greet the FIRST chat message a user sends per stream (the
    //      welcomed-set resets when the stream goes live). A general template covers
    //      everyone; a personalized list overrides it per username, each row with an
    //      optional auto-shoutout.
    //   2. GREETING — welcome a brand-new chatter ONCE EVER (first message in the
    //      channel's lifetime, tracked in the never-cleared UserMgmtSeenEver set).
    //      Takes precedence over the general welcome on that one message; a
    //      personalized row still wins (hand-authored beats generic).
    //   3. GROUPS — the membership tiers every role check in Phoenix reads (script
    //      vars, node outputs, built-in tool gates). Moderator / VIP / Subscriber are
    //      still pickable groups, but they are PLATFORM-DEFINED: their membership is
    //      whatever Twitch / YouTube / Kick says, resolved from the live chat message
    //      or from the background presence sampler's platform-role cache. Only
    //      Regular and the user-defined custom groups keep member lists here, because
    //      only they have no platform equivalent — and both can additionally be earned
    //      through accrued watch time. Groups never change what the platform itself
    //      shows (chat badges stay platform truth).
    //   4. VIEWER QUEUE — a line viewers join from chat (!join / !leave / !position)
    //      and mods work off (!<queue> next / pick / remove / clear). Its store is NOT
    //      private: the queue IS a NAMED queue in the OPEN "Queues" table, the exact
    //      same one the generic Queue.* node band addresses. !join is byte-for-byte
    //      `queue.push(login, display, "<QueueName>", weight)`, so every verb this
    //      part exposes is already reachable from an Architect graph — parity by
    //      construction instead of a parallel node namespace.
    //
    // Mirrors the Scheduling config shape: the WHOLE tool state is a single JSON blob
    // (UserManagementConfig) persisted in the tool-private SYSTEM table "UserMgmtConfig".
    // The only other private persistence is the welcomed-set (SYSTEM table
    // "UserMgmtSeen") plus the lifetime known-chatters set (SYSTEM table
    // "UserMgmtSeenEver") so a Hub restart neither re-greets everyone nor forgets who
    // is brand-new. The queue's ROWS are deliberately outside that private set.
    //
    // Architect-first parity holds: the welcoming flow is fully reachable in Architect
    // (Chat.Message → DB seen-table → Chat.Send, the classic greeter graph), the
    // groups half SUPPLIES Architect with new reach — {user.is_regular}, IsRegular node
    // outputs, and the User.GetGroups lookup node ride the same group store this tool
    // edits — and the queue rides the generalised Queue.* band described above.

    /// <summary>One personalized-welcome row: a username that gets a custom first-message
    /// greeting instead of the general template, with an optional automatic shoutout.</summary>
    public sealed class WelcomeEntry
    {
        /// <summary>Stable identity (GUID string), assigned once on create.</summary>
        public string Id { get; set; } = "";

        /// <summary>Per-row on/off. A disabled row falls back to the general welcome.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The platform login this row matches (case-insensitive).</summary>
        public string Username { get; set; } = "";

        /// <summary>The personalized greeting. Supports the same tokens as the general
        /// message ({user}, {platform}). Empty = row is skipped (general applies).</summary>
        public string Message { get; set; } = "";

        /// <summary>Fire a Twitch shoutout for this user alongside the greeting.
        /// Twitch-only — ignored when the first message arrived on YouTube/Kick.</summary>
        public bool AutoShoutout { get; set; } = false;
    }

    /// <summary>A user-defined custom group (beyond the four standard ones). Grants no
    /// platform-role rights by itself — it exists so scripts can check membership via
    /// the User.GetGroups node / usermgmt.get_groups command.</summary>
    public sealed class UserGroup
    {
        /// <summary>Stable identity (GUID string), assigned once on create.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display name; also the node output socket label. Sanitized to
        /// [a-z0-9_] for the script-var key (see UserGroupKeys.Sanitize).</summary>
        public string Name { get; set; } = "";

        /// <summary>Member logins (case-insensitive at lookup time).</summary>
        public List<string> Members { get; set; } = new();

        /// <summary>Accrued watch HOURS after which a viewer joins this group
        /// automatically, on top of the manual list. 0 (the default) means no rule.
        /// Evaluated live against the open "WatchTime" table — never written back
        /// into <see cref="Members"/>, so the rule stays reversible.</summary>
        public int AutoWatchHours { get; set; } = 0;
    }

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber /
    /// VIP / Moderator / Broadcaster / Regular), gating the viewer-queue chat verbs.
    /// Structurally identical to QuoteRoles / CounterRoles / LoyaltyRoles and kept
    /// independent for the same reason they are: the serialized User-Management blob
    /// must never couple to another tool's naming.
    /// </summary>
    public sealed class QueueRoles
    {
        public bool Everyone { get; set; }
        public bool Subscriber { get; set; }
        public bool Vip { get; set; }
        public bool Moderator { get; set; }
        public bool Broadcaster { get; set; }

        /// <summary>The User-Management Regular group — a community-trust tier with no
        /// platform equivalent. Resolved off the same EffectiveRoles overlay every other
        /// tool gate consults, never off a second role model.</summary>
        public bool Regular { get; set; }

        public QueueRoles() { }
        public static QueueRoles All() => new() { Everyone = true };
        public static QueueRoles Mods() => new() { Moderator = true, Broadcaster = true };

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

    public sealed class UserManagementConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no greeting, no role
        /// overlay, group lookups all-false) until the streamer enables it. Default OFF
        /// (opt-in, like every other pre-build tool).</summary>
        public bool Enabled { get; set; } = false;

        // ── Welcoming ───────────────────────────────────────────────────────

        /// <summary>Section gate for the welcoming half (under the master toggle).</summary>
        public bool WelcomingEnabled { get; set; } = true;

        /// <summary>Send the general welcome to first-time chatters that have no
        /// personalized row. OFF = only personalized users get greeted.</summary>
        public bool GeneralWelcomeEnabled { get; set; } = true;

        /// <summary>The general first-message greeting. Tokens: {user}, {platform}.</summary>
        public string GeneralWelcomeMessage { get; set; } = "Welcome {user}! Enjoy your stay.";

        public List<WelcomeEntry> PersonalWelcomes { get; set; } = new();

        // ── Greeting (first message EVER, not per-stream) ───────────────────

        /// <summary>Send the first-time greeting when a chatter's message is their
        /// first EVER in this channel (per the lifetime UserMgmtSeenEver set).
        /// Default OFF — an upgraded install must not start posting a message the
        /// streamer never reviewed. The known-chatters baseline is recorded whenever
        /// the tool itself is enabled, so turning this on later only greets people
        /// who genuinely never chatted while the tool was active.</summary>
        public bool GreetingEnabled { get; set; } = false;

        /// <summary>The once-ever greeting for brand-new chatters. Same tokens as the
        /// welcome messages: {user}, {platform}.</summary>
        public string GreetingMessage { get; set; } =
            "Welcome to the channel, {user}! Great to have you here for the first time.";

        // ── Groups ──────────────────────────────────────────────────────────
        // Moderator / VIP / Subscriber are still GROUPS you pick everywhere a group is
        // picked — a role checkbox, a User.GetGroups socket, a check_role target — but
        // WHO IS IN THEM IS THE PLATFORM'S ANSWER, not a list kept here. Twitch,
        // YouTube and Kick already publish those three ranks; a second hand-maintained
        // copy could only ever disagree with them, and when it did it silently
        // outranked them suite-wide.
        //
        // Regular is the exception, and the reason the group system exists at all: it
        // is a community-trust tier no platform has a concept of. It keeps a manual
        // member list AND can be earned automatically through watch time. Custom
        // groups work the same way.

        /// <summary>Hand-picked members of the Regular group. Members granted by
        /// <see cref="RegularWatchHours"/> are NOT written here — that rule is
        /// evaluated live, so lowering the threshold includes everybody at once and
        /// raising it takes the grant back without rewriting a single row.</summary>
        public List<string> Regulars { get; set; } = new();

        /// <summary>Accrued watch HOURS after which a viewer counts as a Regular
        /// automatically, on top of the manual list above. 0 (the default) means no
        /// rule — a blank field can never promote the whole channel. Reads the same
        /// open "WatchTime" table the background presence sampler feeds.</summary>
        public int RegularWatchHours { get; set; } = 0;

        public List<UserGroup> CustomGroups { get; set; } = new();

        // ── Viewer queue ────────────────────────────────────────────────────
        // Every field here is also reachable from a graph, because the queue is a
        // NAMED queue in the shared open store: QueueName is the Name pin of every
        // Queue.* node, and the weights below are just the Priority pin of Queue.Push.

        /// <summary>Section gate for the viewer queue (under the master toggle).
        /// Default OFF, like the greeting: the join verb below defaults to "join", which
        /// the Loyalty raffle also claims, so enabling the whole tool must never silently
        /// change what !join does in a channel the streamer already runs raffles in.</summary>
        public bool QueueEnabled { get; set; } = false;

        /// <summary>The queue's name in the open "Queues" table — the value a graph puts
        /// on any Queue.* node's Name pin to address this exact line. Blank falls back to
        /// "viewers" at runtime; it must never resolve to empty, because an empty Name is
        /// what selects the LEGACY unnamed pipe-string queue.</summary>
        public string QueueName { get; set; } = "viewers";

        /// <summary>Chat trigger (without the leading '!') a viewer uses to join.</summary>
        public string QueueJoinCommand { get; set; } = "join";

        /// <summary>Chat trigger a viewer uses to drop out of the line.</summary>
        public string QueueLeaveCommand { get; set; } = "leave";

        /// <summary>Chat trigger that prints the line, and — for a moderator — carries the
        /// management sub-verbs (next / pick / remove / clear).</summary>
        public string QueueListCommand { get; set; } = "queue";

        /// <summary>Chat trigger that answers "where am I in the line".</summary>
        public string QueuePositionCommand { get; set; } = "position";

        // The four management SUB-verbs, typed after the list command
        // ("!queue next"). They were string literals in the parser, which made them
        // the only queue words a streamer could neither see nor change.
        /// <summary>Sub-verb that pulls the front of the line ("!&lt;queue&gt; next").</summary>
        public string QueueNextSubCommand { get; set; } = "next";
        /// <summary>Sub-verb that pulls a named viewer ("!&lt;queue&gt; pick &lt;user&gt;").</summary>
        public string QueuePickSubCommand { get; set; } = "pick";
        /// <summary>Sub-verb that drops a named viewer ("!&lt;queue&gt; remove &lt;user&gt;").</summary>
        public string QueueRemoveSubCommand { get; set; } = "remove";
        /// <summary>Sub-verb that empties the line ("!&lt;queue&gt; clear").</summary>
        public string QueueClearSubCommand { get; set; } = "clear";

        /// <summary>Hard cap on the number of waiting entries. 0 = unlimited.</summary>
        public int QueueMaxSize { get; set; } = 0;

        /// <summary>Priority added at join time for a subscriber (platform sub OR the
        /// Subscriber group). Higher sorts earlier; 0 = no advantage, which is the
        /// default so a fresh install is a plain first-come line.</summary>
        public int QueueSubPriority { get; set; } = 0;

        /// <summary>Priority added at join time for a VIP (platform VIP OR the VIP group).
        /// Stacks with the subscriber weight when the viewer is both.</summary>
        public int QueueVipPriority { get; set; } = 0;

        /// <summary>Per-user cooldown, in seconds, across the queue chat verbs. 0 = off.</summary>
        public int QueueCooldownSeconds { get; set; } = 0;

        /// <summary>Who may use the viewer verbs: join, leave, ask their position, and read
        /// the line. One gate rather than four, because a queue whose members can join but
        /// not see the line is a setup nobody asked for.</summary>
        public QueueRoles QueueJoinRoles { get; set; } = QueueRoles.All();

        /// <summary>Who may run the management sub-verbs (next / pick / remove / clear).</summary>
        public QueueRoles QueueModRoles { get; set; } = QueueRoles.Mods();

        /// <summary>Publish the line into the Overlay Live Channel as a JSON array under
        /// <c>queue.&lt;name&gt;.list</c>, for a List.Live widget. Default OFF — an overlay
        /// key is a thing on stream, so it is opted into.</summary>
        public bool QueueOverlayEnabled { get; set; } = false;

        /// <summary>How many entries the published array carries. Clamped to 1..100 at
        /// publish time; a board nobody can read is not worth an unbounded payload.</summary>
        public int QueueOverlaySize { get; set; } = 10;

        // Reply templates. Tokens: {user} display name, {position} 1-based place,
        // {count} entries in the line, {list} the comma-joined names.
        //
        // {list} is CAPPED and tailed ("… and 27 more") — Twitch drops a chat message over
        // 500 characters outright, and QueueMaxSize defaults to unlimited, so an uncapped
        // list would make the list command answer nothing at all on a busy queue. {count}
        // stays the TRUE length, so "Queue ({count}): {list}" still reports how many are
        // waiting even when it can only name the first few. The limits live with the
        // renderer (UserManagementService.QueueNamesCsv), not here: they are a property of
        // the chat transport, not something worth asking the streamer to tune.
        public string QueueJoinedMessage { get; set; } = "{user} joined the queue — you are #{position} of {count}.";
        public string QueueAlreadyMessage { get; set; } = "{user}, you are already in the queue at #{position}.";
        public string QueueFullMessage { get; set; } = "Sorry {user}, the queue is full ({count}).";
        public string QueueLeftMessage { get; set; } = "{user} left the queue. {count} still waiting.";
        public string QueueNotQueuedMessage { get; set; } = "{user}, you are not in the queue.";
        public string QueuePositionMessage { get; set; } = "{user}, you are #{position} of {count}.";
        public string QueueListMessage { get; set; } = "Queue ({count}): {list}";
        public string QueueEmptyMessage { get; set; } = "The queue is empty.";
        public string QueueNextMessage { get; set; } = "Up next: {user}! {count} still waiting.";
        public string QueueRemovedMessage { get; set; } = "{user} was removed from the queue. {count} waiting.";
        public string QueueClearedMessage { get; set; } = "The queue has been cleared.";
    }

    /// <summary>Shared key helpers for group names — the single place that turns a
    /// group display name into the script-var / socket key used by the exporter, the
    /// engine handler and autocomplete. Kept in Shared so Architect (exporter) and Hub
    /// (handler) can never drift.</summary>
    public static class UserGroupKeys
    {
        /// <summary>Lowercases and squashes a group display name to [a-z0-9_] so it is
        /// safe inside a script-var key ("Night Crew" → "night_crew"). Returns "" for
        /// names with no usable characters.</summary>
        public static string Sanitize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            bool lastUnderscore = false;
            foreach (var ch in name.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    sb.Append(ch);
                    lastUnderscore = false;
                }
                else if (!lastUnderscore && sb.Length > 0)
                {
                    sb.Append('_');
                    lastUnderscore = true;
                }
            }
            // trim a trailing separator
            while (sb.Length > 0 && sb[^1] == '_') sb.Length--;
            return sb.ToString();
        }

        /// <summary>The script-var key a group bool resolves under: "group.&lt;sanitized&gt;".
        /// The four standard groups use their fixed keys (group.moderator / group.vip /
        /// group.subscriber / group.regular).</summary>
        public static string VarKeyFor(string groupName) => "group." + Sanitize(groupName);

        public const string ModeratorKey  = "group.moderator";
        public const string VipKey        = "group.vip";
        public const string SubscriberKey = "group.subscriber";
        public const string RegularKey    = "group.regular";
    }
}
