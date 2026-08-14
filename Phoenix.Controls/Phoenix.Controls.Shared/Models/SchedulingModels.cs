using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Scheduling pre-build tool — automatic, recurring timed chat
    // messages (StreamElements "Timers" equivalent). Mirrors the Loyalty/Counters config
    // shape: the WHOLE tool state is a single JSON blob (SchedulingConfig) persisted in
    // the tool-private SYSTEM table "SchedulingConfig". Unlike Counters/Loyalty there is
    // NO open user table — a schedule carries no data a db.* script needs to read; the
    // list of schedules lives entirely in the config blob.
    //
    // Runtime posting + the per-schedule chat-line counters are held in SchedulingService
    // (Hub) and are NOT serialized here (ephemeral — a fresh Hub start re-arms every
    // interval from zero, which is the sane subathon/timer behaviour and avoids a burst
    // of catch-up posts on launch).
    //
    // Distinct from the Architect Schedule.* nodes + the script-firing SchedulerService:
    // this is the no-code turnkey shortcut for the common "post this line every N minutes"
    // case. The same result is fully reachable in Architect via a Schedule.Recurring node
    // (optionally with its MinChatLines gate) wired to a Chat.Send node — Architect-first
    // parity holds (the tool exposes no capability a graph can't reach).

    /// <summary>
    /// One scheduled recurring message. The three headline fields match the tool's
    /// per-row UI: <see cref="Enabled"/> (the slider), <see cref="Message"/> (the text),
    /// <see cref="IntervalMinutes"/> (every-N-minutes). <see cref="MinChatLines"/> is the
    /// optional StreamElements-style anti-dead-chat gate (0 = pure time-based).
    /// </summary>
    public sealed class ScheduleItem
    {
        /// <summary>Stable identity (GUID string), assigned once on create. Used as the
        /// dictionary key for this schedule's ephemeral runtime state in SchedulingService.
        /// Never reused / never renumbered so runtime state survives a list re-order.</summary>
        public string Id { get; set; } = "";

        /// <summary>Optional display label for the list row. Empty falls back to a preview
        /// of <see cref="Message"/> in the UI. Not used at runtime.</summary>
        public string Name { get; set; } = "";

        /// <summary>Per-schedule on/off (the row slider). A disabled schedule never posts,
        /// even when the master tool toggle is on.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The chat text posted on each fire. Tokens are resolved at post time
        /// ({count} = this schedule's fire number, {random}/{random.N}/{random.A-B},
        /// {uptime}, {channel}). Empty message = the schedule is skipped.</summary>
        public string Message { get; set; } = "";

        /// <summary>Fire cadence in whole minutes. Clamped to >= 1 at runtime; the first
        /// fire is one full interval after the schedule is armed (on enable / Hub start),
        /// so enabling never posts immediately.</summary>
        public int IntervalMinutes { get; set; } = 15;

        /// <summary>StreamElements-style dead-chat gate: require at least this many chat
        /// messages since this schedule's last post before it fires again. 0 disables the
        /// gate (pure time-based). Prevents the bot talking to an empty chat.</summary>
        public int MinChatLines { get; set; } = 0;
    }

    public sealed class SchedulingConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no posting at all) until the
        /// streamer enables it. Default OFF (opt-in, like every other pre-build tool).</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Post only while the stream is detected LIVE. Default ON (matches
        /// StreamElements — recurring promos don't fire while offline). The live signal
        /// defaults to "live" until StreamOnline/Offline events wire it, so with no
        /// live-detection this degrades to "post whenever connected".</summary>
        public bool OnlyWhenLive { get; set; } = true;

        public List<ScheduleItem> Schedules { get; set; } = new();
    }
}
