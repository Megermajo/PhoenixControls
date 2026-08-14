using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Alerts pre-build tool — StreamElements-style event responses:
    // per event family (Follow / Sub / Gift Sub / Gift Bomb / Bits / Raid) the streamer
    // activates a response and authors one or more SIZE TIERS (e.g. a raid with 5, 10,
    // 25+ viewers each gets its own line). Each tier carries a templated chat message
    // (token args like {user}/{amount}) and an optional Visual.Trigger hookup
    // (layer + trigger) that rides the SAME Architect→Hub→Visualist VISUAL_TRIGGER
    // pipeline a graph uses. Raids additionally offer an automatic Twitch shoutout.
    //
    // Mirrors the Scheduling config shape: the WHOLE tool state is a single JSON blob
    // (AlertsConfig) persisted in the tool-private SYSTEM table "AlertsConfig". No open
    // user table — alert rules carry nothing a db.* script needs to read.
    //
    // Architect-first parity holds by construction: this is the no-code shortcut over
    // the classic alerts graph (event node → Logic.If size branches → Chat.Send +
    // Visual.Trigger + Twitch.Shoutout) — the exact flow the reference alerts.phxg
    // hand-builds today. The tool exposes no capability a graph can't reach.

    /// <summary>One size tier of an alert response. The tier fires when the event's
    /// size metric (bits / gift count / raid viewers / cumulative sub months) is at
    /// least <see cref="MinValue"/> and no higher-MinValue enabled tier also matches —
    /// i.e. the HIGHEST matching tier wins, so tiers form ranges without explicit
    /// upper bounds (0 / 5 / 25 → "0-4", "5-24", "25+").</summary>
    public sealed class AlertTier
    {
        /// <summary>Stable identity (GUID string), assigned once on create.</summary>
        public string Id { get; set; } = "";

        /// <summary>Per-tier on/off. A disabled tier is invisible to selection.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Minimum event size for this tier (inclusive). 0 = catch-all base
        /// tier. For events with no size metric (Follow, single Gift Sub) keep 0.</summary>
        public int MinValue { get; set; } = 0;

        /// <summary>The chat line posted for this tier. Empty = this tier posts nothing
        /// (useful for a visual-only tier). Tokens are documented per event in the tool
        /// UI ({user}, {amount}, {tier}, {months}, {gifter}, {recipient}, {count},
        /// {viewers}, {message}).</summary>
        public string Message { get; set; } = "";

        /// <summary>Optional Visual.Trigger hookup — the layer (OBS browser source) to
        /// fire on. Empty = no visual for this tier.</summary>
        public string LayerId { get; set; } = "";

        /// <summary>Optional Visual.Trigger hookup — the trigger name on that layer
        /// (with or without the "onTrigger:" prefix). Fires on every widget of the
        /// layer that owns the trigger, exactly like a graph's Visual.Trigger.</summary>
        public string TriggerName { get; set; } = "";
    }

    /// <summary>Configuration of one alert event family (e.g. Raid).</summary>
    public sealed class AlertEventConfig
    {
        /// <summary>Per-event response on/off. Default OFF — the streamer activates
        /// each response type individually (StreamElements style).</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Automatic Twitch shoutout for the acting user. Honored for the
        /// RAID family only (surfaced only on that card).</summary>
        public bool AutoShoutout { get; set; } = false;

        public List<AlertTier> Tiers { get; set; } = new();
    }

    public sealed class AlertsConfig
    {
        /// <summary>Master gate — the whole tool is dormant until enabled. Default OFF
        /// (opt-in, like every other pre-build tool).</summary>
        public bool Enabled { get; set; } = false;

        public AlertEventConfig Follow   { get; set; } = new();
        public AlertEventConfig Sub      { get; set; } = new();
        public AlertEventConfig GiftSub  { get; set; } = new();
        public AlertEventConfig GiftBomb { get; set; } = new();
        public AlertEventConfig Cheer    { get; set; } = new();
        public AlertEventConfig Raid     { get; set; } = new();

        // Families added with the Twitch event-surface completion. Adding a
        // property is backward-compatible on its own: a blob written before these
        // existed simply omits the key, the property stays at its `new()` default
        // (Enabled = false), and an upgraded install shows the new cards switched
        // off. No migration, no DDL.
        public AlertEventConfig Charity          { get; set; } = new();
        public AlertEventConfig ShoutoutReceived { get; set; } = new();
        public AlertEventConfig WatchStreak      { get; set; } = new();
        public AlertEventConfig Tip              { get; set; } = new();
    }
}
