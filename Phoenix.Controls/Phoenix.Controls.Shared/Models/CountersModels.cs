using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Counters pre-build tool (named databank-backed counters:
    // set / add / get / on-changed). Mirrors the Loyalty/Timer config shape: a single
    // JSON blob (CountersConfig) is the tool-private SYSTEM table, while the live
    // COUNTS live in an OPEN "Counters" user table (name/count) so db.* scripts keep
    // full read/write. Counts are NEVER mirrored into config — the DB is the single
    // source of truth (a streamer's db.* write to the open table must not be clobbered
    // by a stale cache).

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber /
    /// VIP / Moderator / Broadcaster / Regular). Shared/Models is allowed; only
    /// Shared/UI is forbidden. Kept independent of LoyaltyRoles so the serialized
    /// Counters config never couples to Loyalty naming.
    /// </summary>
    public sealed class CounterRoles
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

        public CounterRoles() { }
        public static CounterRoles All() => new() { Everyone = true };
        public static CounterRoles Mods() => new() { Moderator = true, Broadcaster = true };

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
    /// One managed counter. The live count is NOT stored here (the open "Counters"
    /// table is the truth) — this carries only the settings that shape reads/writes
    /// and the built-in chat command.
    /// </summary>
    public sealed class CounterDef
    {
        /// <summary>Counter key = the row name in the open Counters table. One word (e.g. "deaths").</summary>
        public string Name { get; set; } = "";

        /// <summary>Clamp Add/Set results at 0 (death/goal counters shouldn't dip below zero). Default on.</summary>
        public bool FloorAtZero { get; set; } = true;

        /// <summary>Increment applied by the +/- chat commands.</summary>
        public int Step { get; set; } = 1;

        // ── Built-in chat command ───────────────────────────────────────────
        /// <summary>Enables the built-in chat commands for this counter.</summary>
        public bool ChatEnabled { get; set; } = true;

        /// <summary>Command word: !&lt;cmd&gt; shows, !&lt;cmd&gt;+ / !&lt;cmd&gt;- step,
        /// !set&lt;cmd&gt; N sets, !reset&lt;cmd&gt; zeroes. Empty falls back to Name.</summary>
        public string Command { get; set; } = "";

        /// <summary>Who may READ the counter via !&lt;cmd&gt;. Default: everyone.</summary>
        public CounterRoles ViewRoles { get; set; } = CounterRoles.All();

        /// <summary>Who may MODIFY (+ / - / set / reset) via chat. Default: mods + broadcaster.</summary>
        public CounterRoles ModifyRoles { get; set; } = CounterRoles.Mods();

        /// <summary>Per-user cooldown (seconds) on this counter's chat commands; 0 disables.</summary>
        public int CooldownSeconds { get; set; } = 0;

        /// <summary>Optional chat reply template. {name}/{count} substituted; empty = "&lt;cmd&gt;: &lt;count&gt;".</summary>
        public string ReplyTemplate { get; set; } = "";

        public string EffectiveCommand
            => string.IsNullOrWhiteSpace(Command) ? (Name ?? "") : Command;
    }

    public sealed class CountersConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no chat commands, no
        /// overlay push) until the streamer enables it. Default OFF.</summary>
        public bool Enabled { get; set; } = false;

        public List<CounterDef> Counters { get; set; } = new();
    }
}
