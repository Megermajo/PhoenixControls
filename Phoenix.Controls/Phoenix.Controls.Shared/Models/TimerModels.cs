namespace Phoenix.Controls.Shared.Models
{
    // Runtime timer models — the DB-/engine-facing representation used by
    // DB.Timer.cs, the Hub TimerService, and ScriptManager.Timer.cs. One
    // StreamTimer is a single subathon-style countdown that ticks DOWN toward
    // zero; stream events (subs / bits / follows / raids / …) ADD time. The
    // whole StreamTimer is persisted as JSON in the Timers table (see
    // DB.Timer.cs) so the authoritative RemainingMs survives a Hub restart.
    //
    // Time is carried internally as milliseconds (long); the per-event
    // "seconds added" config below is stored as seconds (int) because it is
    // human-authored in the Timer settings UI.

    /// <summary>Lifecycle state of a StreamTimer. Ended = reached zero.</summary>
    public enum TimerRunState { Stopped, Running, Paused, Ended }

    /// <summary>
    /// What kind of timer this is — determines tick direction and which machinery
    /// applies. Default is <see cref="Subathon"/> so timers persisted before this
    /// field existed deserialize as the original subathon behaviour.
    ///   • <b>Subathon</b>  — counts DOWN toward zero; stream events ADD time
    ///                        (the full Actions / Happy-Hour / milestone machinery).
    ///   • <b>Countdown</b> — counts DOWN toward zero from a fixed start duration;
    ///                        stream events do NOT add time (a plain countdown).
    ///   • <b>Stopwatch</b> — counts UP via <see cref="StreamTimer.ElapsedMs"/>;
    ///                        no zero, no max cap, no event accretion.
    /// The Visualist overlay reads a mode-aware "display value" (RemainingMs for
    /// Subathon/Countdown, ElapsedMs for Stopwatch) from the TIMER_UPDATE payload.
    /// </summary>
    public enum TimerMode { Subathon, Countdown, Stopwatch }

    /// <summary>Per-event "seconds added" config (stored as seconds; 0 = that action is off).</summary>
    public sealed class TimerActionConfig
    {
        public int SubT1Seconds { get; set; } = 300;    // 5m
        public int SubT2Seconds { get; set; } = 600;    // 10m  (~2× T1)
        public int SubT3Seconds { get; set; } = 1500;   // 25m  (~5× T1)
        public int SubPrimeSeconds { get; set; } = 300;
        public int BitsPer100Seconds { get; set; } = 60;   // 60s per 100 bits
        // OFF by default. 0 is this class's only off-switch (there is no paired
        // bool), and it shipped at 60 while no tip event could reach it. Once
        // donation ingestion lands, a non-zero default would silently extend a
        // live subathon on the first tip the streamer ever receives. Existing
        // configs are zeroed once by TipDefaultsMigration.
        public int TipPerUnitSeconds { get; set; } = 0;
        public int FollowSeconds { get; set; } = 0;        // OFF by default (bot-abuse)
        public int RaidPerViewerSeconds { get; set; } = 0; // OFF by default
    }

    /// <summary>
    /// Chat + visual feedback for ONE timer event.
    ///
    /// <para>Default OFF, the opt-in posture every pre-build tool ships with (see
    /// <c>AlertsConfig.Enabled</c> / <c>AlertEventConfig.Enabled</c>). With
    /// <see cref="Enabled"/> on, an EMPTY <see cref="Message"/> means "visual only" and
    /// an empty <see cref="LayerId"/>/<see cref="TriggerName"/> pair means "chat only" —
    /// the same convention <c>AlertTier</c> uses, rather than a second checkbox each.</para>
    /// </summary>
    public sealed class TimerFeedbackConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Templated chat line; tokens are per-event and documented in the
        /// Timer panel. Empty = post nothing (visual only).</summary>
        public string Message { get; set; } = "";

        /// <summary>Optional visual hookup — the layer (OBS browser source) to fire on.
        /// Empty = no visual.</summary>
        public string LayerId { get; set; } = "";

        /// <summary>Optional visual hookup — the trigger name on that layer (with or
        /// without the "onTrigger:" prefix). Fires on every widget of the layer that
        /// owns it, exactly like a graph's Visual.Trigger.</summary>
        public string TriggerName { get; set; } = "";
    }

    /// <summary>
    /// Per-timer feedback config. Scoped to the THREE timer events that already have an
    /// Architect event node — <c>Timer.OnZero</c>, <c>Timer.OnMilestone</c>,
    /// <c>Timer.OnAdd</c> — so the tool stays a no-code shortcut over a graph a streamer
    /// could hand-build (event node → Chat.Send + Visual.Trigger) and Architect-first
    /// parity holds by construction. Subtract / start / stop / pause / resume / reset /
    /// happy-hour / cap-reached deliberately get NOTHING: they raise no script event, so
    /// feedback for them would be a tool-only capability no graph can reach.
    /// </summary>
    public sealed class TimerFeedbackSettings
    {
        /// <summary>Fires once when a count-down timer reaches zero.</summary>
        public TimerFeedbackConfig Zero { get; set; } = new();

        /// <summary>Master gate for milestone feedback. <see cref="TimerFeedbackConfig.Message"/>
        /// / <see cref="TimerFeedbackConfig.LayerId"/> / <see cref="TimerFeedbackConfig.TriggerName"/>
        /// here are the DEFAULTS every goal uses; a goal that fills its own fields
        /// (see <see cref="TimerMilestone.Message"/>) overrides them.</summary>
        public TimerFeedbackConfig Milestone { get; set; } = new();

        /// <summary>Fires for time ADDED. Bursty by nature — a gift bomb produces one
        /// add per recipient plus one for the aggregate — so the runtime coalesces a
        /// burst into ONE summary line rather than posting per event.</summary>
        public TimerFeedbackConfig Add { get; set; } = new();

        /// <summary>Minimum COALESCED seconds an add burst must total before it is
        /// announced. 0 = announce every add. The cheapest knob against a trickle of
        /// tiny adds; it gates both halves (chat and visual), because an add not worth
        /// a line is not worth an overlay pop either.</summary>
        public int AddMinSeconds { get; set; } = 0;
    }

    /// <summary>A goal/milestone: fires OnMilestone the first time RemainingMs crosses &gt;= Target.</summary>
    public sealed class TimerMilestone
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public long TargetSeconds { get; set; }
        public bool Reached { get; set; }

        // Per-goal feedback overrides. Empty = fall back to the timer-wide
        // TimerFeedbackSettings.Milestone defaults, so a streamer can write one line
        // with {label} once and still give a headline goal its own text and visual.
        public string Message { get; set; } = "";
        public string LayerId { get; set; } = "";
        public string TriggerName { get; set; } = "";
    }

    /// <summary>One named countdown timer. Persisted whole as JSON in the Timers table.</summary>
    public sealed class StreamTimer
    {
        public string Slug { get; set; } = "";     // stable id (e.g. "t-2026-07-16-a")
        public string Name { get; set; } = "";     // display name
        public bool IsDefault { get; set; }

        // Timer kind. Default Subathon keeps pre-existing persisted timers (and
        // JSON without this field) on the original behaviour. Countdown = plain
        // countdown (no event accretion); Stopwatch = count-UP via ElapsedMs.
        public TimerMode Mode { get; set; } = TimerMode.Subathon;

        // Live countdown state (persisted — survives Hub restart)
        public TimerRunState State { get; set; } = TimerRunState.Stopped;
        public long RemainingMs { get; set; }                 // authoritative (Subathon/Countdown)
        public long ElapsedMs { get; set; }                   // authoritative count-UP value (Stopwatch)
        public long StartDurationMs { get; set; } = 4L * 3600_000; // 4h default
        public long MaxCapMs { get; set; } = 72L * 3600_000;       // 72h; 0 = unlimited
        public long PerAddCapMs { get; set; } = 30L * 60_000;      // 30m; 0 = no per-add cap
        public bool PauseWhenOffline { get; set; } = true;
        public long TotalAddedMs { get; set; }                // cumulative added (stat/progress)

        public TimerActionConfig Actions { get; set; } = new();
        public System.Collections.Generic.List<TimerMilestone> Milestones { get; set; } = new();

        // Chat + visual responses for OnZero / OnMilestone / OnAdd. Adding a property is
        // backward-compatible on its own — the whole StreamTimer is one JSON blob in the
        // Timers table (DB.Timer.cs serializes the object and deserializes it back), so a
        // blob written before this existed simply omits the key, the property stays at
        // its `new()` default (every gate OFF), and an upgraded install shows the cards
        // switched off. No DDL, no migration.
        public TimerFeedbackSettings Feedback { get; set; } = new();

        // Happy Hour (timed multiplier on time added)
        public double HappyHourMultiplier { get; set; } = 1.0;
        public long HappyHourEndsAtUnixMs { get; set; }       // 0 = inactive
        public string HappyHourScope { get; set; } = "all";   // all|subs|bits|tips|follows|raids

        public long UpdatedAtUnixMs { get; set; }

        /// <summary>
        /// Deep-clone a StreamTimer by round-tripping through JSON — used to hand
        /// the UI a detached snapshot it can read/edit without touching the
        /// TimerService's authoritative in-memory copy. A null input clones a
        /// fresh default timer so callers never have to null-check the result.
        /// </summary>
        public static StreamTimer Clone(StreamTimer source)
        {
            if (source is null) return new StreamTimer();
            string json = System.Text.Json.JsonSerializer.Serialize(source);
            return System.Text.Json.JsonSerializer.Deserialize<StreamTimer>(json) ?? new StreamTimer();
        }
    }
}
