namespace Phoenix.Controls.Shared.Models
{
    // Runtime giveaway models — the DB-/engine-facing representation used by
    // DB.Giveaway.cs, the Hub GiveawayService, and ScriptManager.Giveaway.cs.
    //
    // The WinUI page does NOT consume these directly; the Hub.WinUI HubServices
    // bridge maps these onto the UI-facing DTO records in
    // Phoenix.Controls.Shared.WinUI.Contracts (GiveawayInfo / GiveawayEntrantInfo).
    // This is the same runtime-model ↔ chrome-record split that
    // Models.ChatMessage ↔ Contracts.ChatMessage already follows — see the
    // C20 comment block in Shared.WinUI/Contracts/Records.cs.

    /// <summary>One row in the built-in <c>Giveaways</c> registry table.</summary>
    public sealed class Giveaway
    {
        public long Id { get; set; }

        /// <summary>Human-facing slug shown in the picker (e.g. "g-2026-05-27-a"). Unique.</summary>
        public string Key { get; set; } = "";

        /// <summary>Display title shown on stream and in the picker.</summary>
        public string Title { get; set; } = "";

        /// <summary>"open" (accepting entries) · "closed" (ended, no draw) · "drawn" (winner picked).</summary>
        public string Status { get; set; } = "open";

        /// <summary>ISO-8601 UTC open timestamp (DateTime.UtcNow.ToString("o")).</summary>
        public string OpenedAt { get; set; } = "";

        /// <summary>ISO-8601 UTC close timestamp; null while open.</summary>
        public string? ClosedAt { get; set; }

        public string OpenedBy { get; set; } = "";

        /// <summary>The default giveaway is the one public=true nodes retarget to.</summary>
        public bool IsDefault { get; set; }

        // ── Settings (per giveaway) ─────────────────────────────────────────
        // Entry itself is script-driven (a .phx file calls giveaway.ticket) —
        // there is no built-in entry command / per-message ticket amount.

        /// <summary>
        /// Subscriber ticket-weight factor applied at DRAW time: a subscriber's
        /// tickets count ×factor in the weighted draw. 1 = no bonus (default).
        /// Subscriber status is gathered fresh from Streamer.bot before the
        /// draw (recorded entrant role is the fallback).
        /// </summary>
        public double SubscriberBonusFactor { get; set; } = 1.0;

        /// <summary>
        /// Moderator ticket-weight factor applied at DRAW time: a moderator's
        /// tickets count ×factor in the weighted draw. 1 = no bonus (default).
        /// Mod status comes from the flags recorded at entry (the
        /// Giveaway.Ticket node's IsMod input, or a legacy "mod" role badge) —
        /// unlike sub status it is not re-gathered live before the draw.
        /// Multiplies with the subscriber factor for entrants who are both.
        /// </summary>
        public double ModBonusFactor { get; set; } = 1.0;

        /// <summary>Max tickets per user; 0 = unlimited.</summary>
        public int CapPerUser { get; set; } = 0;

        /// <summary>
        /// Channel-point price per ticket; 0 = free entry (default). Charged by
        /// Giveaway.Ticket nodes that carry a currency table (PriceTable) and
        /// follow this giveaway's settings (Public = true). A Public = false
        /// node uses its own Price pill instead of this setting.
        /// </summary>
        public int TicketPrice { get; set; } = 0;

        /// <summary>Comma-separated winner name(s), appended per draw.</summary>
        public string Winners { get; set; } = "";

        // ── Derived counts (populated by the service, not stored columns) ────
        public int Entrants { get; set; }
        public int Tickets { get; set; }
        public string LastEntry { get; set; } = "";
    }

    /// <summary>
    /// Outcome of one priced/free ticket purchase (DB.PurchaseGiveawayTicketsAsync).
    /// Exactly one of <see cref="Capped"/> / <see cref="NoFunds"/> /
    /// <see cref="TableMissing"/> is set when nothing was granted; all three are
    /// false on a successful (possibly cap-truncated w/ Capped) grant or a read.
    /// </summary>
    /// <param name="Tickets">The user's ticket total after the call.</param>
    /// <param name="Purchased">Tickets actually granted by this call.</param>
    /// <param name="Charged">Channel points deducted from the currency table.</param>
    /// <param name="Balance">Balance after the call; -1 when no balance was read (free entry).</param>
    /// <param name="Capped">The per-user cap blocked or truncated the request.</param>
    /// <param name="NoFunds">The balance blocked the (cap-clamped) request entirely.</param>
    /// <param name="TableMissing">The currency table (or its name/currency columns) is missing — nothing changed.</param>
    public readonly record struct GiveawayPurchaseResult(
        int Tickets,
        int Purchased,
        long Charged,
        long Balance,
        bool Capped,
        bool NoFunds,
        bool TableMissing);

    /// <summary>One row in a giveaway's ticket pool (GiveawayTickets table).</summary>
    public sealed class GiveawayEntrant
    {
        public string Username { get; set; } = "";

        /// <summary>"broadcaster" · "mod" · "vip" · "sub" · "viewer" — for the role badge.</summary>
        public string Role { get; set; } = "viewer";

        /// <summary>
        /// Moderator flag recorded at entry (Giveaway.Ticket's IsMod input).
        /// Separate from <see cref="Role"/> so a subscriber-moderator keeps
        /// both facts; the draw's mod bonus also honors a legacy Role == "mod".
        /// </summary>
        public bool IsMod { get; set; }

        public int Tickets { get; set; }

        /// <summary>ISO-8601 UTC of this user's most recent entry.</summary>
        public string LastEntry { get; set; } = "";
    }
}
