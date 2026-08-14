using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Custom Chat Commands pre-build tool — a no-code layer over
    // "on !cmd → send templated text". TEXT / VARIABLE ONLY: a custom command never
    // calls an API; it renders a response template (with a variable-substitution
    // library) and sends it. Mirrors the Counters/Quotes config shape: a single JSON
    // blob (CustomCommandsConfig) is the tool-private SYSTEM table ("CustomCommands").
    //
    // There is NO open data table. The {count}/{count.next} tokens CONSUME the already
    // built Counters tool (CountersService + its OPEN "Counters" table) — this tool
    // creates no counter table and no counter nodes of its own.

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber /
    /// VIP / Moderator / Broadcaster / Regular). Shared/Models is allowed; only
    /// Shared/UI is forbidden. Kept independent of CounterRoles/QuoteRoles/LoyaltyRoles
    /// so the serialized Custom-Commands config never couples to another tool's naming.
    /// </summary>
    public sealed class CommandRoles
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

        public CommandRoles() { }
        public static CommandRoles All() => new() { Everyone = true };
        public static CommandRoles Mods() => new() { Moderator = true, Broadcaster = true };

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
    /// One custom chat command: a trigger word (+ optional aliases), a response
    /// template rendered through the variable library, a role checkmark set, and a
    /// dual-bucket cooldown (per-user + channel-wide global).
    /// </summary>
    public sealed class CustomCommand
    {
        /// <summary>The command word typed after '!' (e.g. "discord"). Also the
        /// display name and, unless <see cref="CounterName"/> overrides it, the counter
        /// that {count}/{count.next} target. Matched case-insensitively, whole-token.</summary>
        public string Trigger { get; set; } = "";

        /// <summary>Response text sent when the command fires. Variable tokens
        /// (<c>{user}</c>, <c>{args}</c>, <c>{target}</c>, <c>{channel}</c>,
        /// <c>{random}</c>, <c>{count}</c>, <c>{count.next}</c>, <c>{uptime}</c>,
        /// <c>{game}</c>) are substituted at send time. Saved verbatim.</summary>
        public string Response { get; set; } = "";

        /// <summary>Alternate trigger words that fire the same command (matched
        /// case-insensitively, whole-token). Share the command's cooldown buckets.</summary>
        public List<string> Aliases { get; set; } = new();

        /// <summary>Who may run the command. Default: everyone.</summary>
        public CommandRoles Roles { get; set; } = CommandRoles.All();

        /// <summary>Per-user cooldown (seconds) on this command; 0 disables the per-user bucket.</summary>
        public int UserCooldownSeconds { get; set; } = 0;

        /// <summary>Channel-wide (global) cooldown (seconds) on this command; 0 disables the global bucket.</summary>
        public int GlobalCooldownSeconds { get; set; } = 0;

        /// <summary>Per-command enable toggle. A disabled command is skipped (falls
        /// through to author scripts) even while the tool is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Optional Counters-tool counter name that {count}/{count.next}
        /// target. Empty falls back to <see cref="Trigger"/> — so a "deaths" command's
        /// {count} tracks the "deaths" counter out of the box.</summary>
        public string CounterName { get; set; } = "";

        /// <summary>The counter key {count}/{count.next} resolve against (CounterName
        /// when set, else the Trigger word).</summary>
        public string EffectiveCounterName
            => string.IsNullOrWhiteSpace(CounterName) ? (Trigger ?? "") : CounterName;
    }

    public sealed class CustomCommandsConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no chat commands, no
        /// Command.OnCustom event) until the streamer enables it. Default OFF.</summary>
        public bool Enabled { get; set; } = false;

        public List<CustomCommand> Commands { get; set; } = new();
    }
}
