using System;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Quotes pre-build tool (a databank-backed chat-command
    // quote store: add / get-random / get-by-number / delete). Mirrors the
    // Counters/Loyalty config shape: a single JSON blob (QuotesConfig) is the
    // tool-private SYSTEM table, while the quotes themselves live in an OPEN
    // "Quotes" user table (num/text/name/added_by/added_at) so db.* scripts keep
    // full read/write. Quotes are NEVER mirrored into config — the DB is the single
    // source of truth (a streamer's db.* write to the open table must not be
    // clobbered by a stale cache).

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber /
    /// VIP / Moderator / Broadcaster / Regular). Shared/Models is allowed; only
    /// Shared/UI is forbidden. Kept independent of CounterRoles/LoyaltyRoles so the
    /// serialized Quotes config never couples to another tool's naming.
    /// </summary>
    public sealed class QuoteRoles
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

        public QuoteRoles() { }
        public static QuoteRoles All() => new() { Everyone = true };
        public static QuoteRoles Mods() => new() { Moderator = true, Broadcaster = true };

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
    /// One stored quote. The rows live in the OPEN "Quotes" table; this is the
    /// in-memory projection the service / panel / nodes pass around.
    /// <see cref="Number"/> is the streamer-facing #N (stable, never reused).
    /// </summary>
    public sealed class QuoteEntry
    {
        /// <summary>Streamer-facing #N — MAX+1 at add time, stable, never reused on delete.</summary>
        public int Number { get; set; }

        /// <summary>The quote text (the remainder after the leading name token).</summary>
        public string Text { get; set; } = "";

        /// <summary>The quoted person's name (the leading one-word token of !addquote).</summary>
        public string Name { get; set; } = "";

        /// <summary>Chatter (or panel) who added the quote.</summary>
        public string AddedBy { get; set; } = "";

        /// <summary>ISO-8601 UTC add timestamp.</summary>
        public string AddedAt { get; set; } = "";
    }

    public sealed class QuotesConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no chat commands, no
        /// Quote.OnAdded event) until the streamer enables it. Default OFF.</summary>
        public bool Enabled { get; set; } = false;

        // ── Command words (matched without the leading '!') ──────────────────
        /// <summary>Add command word. Inline shape: !&lt;add&gt; &lt;name&gt; &lt;quote&gt;.</summary>
        public string AddCommand { get; set; } = "addquote";

        /// <summary>Get command word. !&lt;get&gt; → random, !&lt;get&gt; N → quote #N.</summary>
        public string GetCommand { get; set; } = "quote";

        /// <summary>Delete command word. !&lt;del&gt; N removes quote #N (no renumber).</summary>
        public string DeleteCommand { get; set; } = "delquote";

        // ── Role gating (independent-checkmark sets) ─────────────────────────
        /// <summary>Who may ADD via chat. Default: mods + broadcaster.</summary>
        public QuoteRoles AddRoles { get; set; } = QuoteRoles.Mods();

        /// <summary>Who may GET via chat. Default: everyone.</summary>
        public QuoteRoles GetRoles { get; set; } = QuoteRoles.All();

        /// <summary>Who may DELETE via chat. Default: mods + broadcaster.</summary>
        public QuoteRoles DeleteRoles { get; set; } = QuoteRoles.Mods();

        /// <summary>Optional get-reply template. {number}/{text}/{name} substituted;
        /// empty = "Quote #N: &lt;text&gt; — &lt;name&gt;".</summary>
        public string ReplyTemplate { get; set; } = "";
    }
}
