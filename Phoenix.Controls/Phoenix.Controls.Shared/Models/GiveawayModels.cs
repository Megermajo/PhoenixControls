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
        public string EntryCommand { get; set; } = "!enter";
        public int TicketsPerMessage { get; set; } = 1;
        public int SubscriberBonus { get; set; } = 0;

        /// <summary>Max tickets per user; 0 = unlimited.</summary>
        public int CapPerUser { get; set; } = 0;

        /// <summary>"weighted" (by ticket count) — the only method today.</summary>
        public string DrawMethod { get; set; } = "weighted";

        /// <summary>Comma-separated winner name(s), appended per draw.</summary>
        public string Winners { get; set; } = "";

        // ── Derived counts (populated by the service, not stored columns) ────
        public int Entrants { get; set; }
        public int Tickets { get; set; }
        public string LastEntry { get; set; } = "";
    }

    /// <summary>One row in a giveaway's ticket pool (GiveawayTickets table).</summary>
    public sealed class GiveawayEntrant
    {
        public string Username { get; set; } = "";

        /// <summary>"broadcaster" · "mod" · "vip" · "sub" · "viewer" — for the role badge.</summary>
        public string Role { get; set; } = "viewer";

        public int Tickets { get; set; }

        /// <summary>ISO-8601 UTC of this user's most recent entry.</summary>
        public string LastEntry { get; set; } = "";
    }
}
