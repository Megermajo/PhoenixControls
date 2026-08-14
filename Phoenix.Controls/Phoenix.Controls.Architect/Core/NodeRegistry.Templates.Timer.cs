using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Timer band — the subathon-style countdown nodes ("Pre-Builds ▾ → Timer",
    // sibling to Giveaway). Architect-only authoring surface: the Hub's
    // TimerService runs the timers; Visualist never touches them.
    //
    // Three shapes, mirroring the Giveaway band + the platform-event bands:
    //   * Control nodes (category "Timer") — void, Flow-in → Done-out. Emit
    //     timer.* commands via SimpleEmitDescriptors (see
    //     ExporterRegistry.Handlers1.RegisterTimer). Empty Name = the default
    //     timer, exactly like Giveaway's empty selector.
    //   * Value nodes (category "Timer") — no flow, resolved inline as
    //     timer.get_*(...) call expressions (see ScriptExporter.ComputeInlineValue
    //     + the explicit inline-title route line). "Timer" is deliberately NOT a
    //     _pureDataCategory — the control nodes must emit flow — so each value
    //     node is routed by title instead.
    //   * Event nodes (category "Events") — output-only event roots that MIRROR
    //     Twitch.Subscription: category "Events" makes them entry points, the
    //     ProcessEventNode trigger-switch fallback emits `on_event(<title>):`
    //     (matching the phoenixEvent strings TimerService raises — Timer.OnZero /
    //     Timer.OnMilestone / Timer.OnAdd), and the src.Category=="Events"
    //     output mapping resolves each output socket to {event.<socket>} for the
    //     runtime vars TimerService supplies.
    public static partial class NodeRegistry
    {
        private static void RegisterTimerTemplates()
        {
            // Warm ember/coral header — distinct from Giveaway's Goldenrod while
            // keeping the two "Pre-Builds" siblings visually adjacent.
            Color timer = Color.Coral;

            // ── Control nodes (void, Flow-in → Done-out) ─────────────────────
            AddTemplate("Timer.Start", "Timer", timer,
                "Start (or restart) a countdown timer running toward zero. Duration accepts a bare number (seconds) or units like 90s / 5m / 1h30m / 2h / 1d — leave it empty to use the timer's configured start duration. Empty Name targets the default timer.",
                new[] { ("Flow", ColExec), ("Name", ColString), ("Duration", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Timer.Stop", "Timer", timer,
                "Stop the timer — the countdown freezes and its state becomes Stopped.",
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Timer.Pause", "Timer", timer,
                "Pause the countdown — remaining time stops draining until the timer is resumed.",
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Timer.Resume", "Timer", timer,
                "Resume a paused countdown from where it left off.",
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Timer.Toggle", "Timer", timer,
                "Toggle the countdown between paused and running.",
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Timer.Reset", "Timer", timer,
                "Reset the timer's remaining time back to its configured start duration.",
                new[] { ("Flow", ColExec), ("Name", ColString) },
                new[] { ("Done", ColExec) });

            // ★ SIGNED since the 2026-08 tool-node cut: this one node covers both
            // directions — a negative Amount subtracts (Timer.Subtract was retired).
            // The manual path applies neither the per-add cap nor the Happy-Hour
            // multiplier; those belong to event-sourced adds (ApplyStreamEventAsync).
            AddTemplate("Timer.Add", "Timer", timer,
                "Add time to the countdown — or remove it with a negative Amount (e.g. -90s). Amount accepts a bare number (seconds) or units like 90s / 5m / 1h30m. Removing time clamps the countdown at zero (and fires Timer.OnZero if it lands there).",
                new[] { ("Flow", ColExec), ("Name", ColString), ("Amount", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Timer.SetTime", "Timer", timer,
                "Set the remaining time to an absolute value. Amount accepts a bare number (seconds) or units like 5m / 1h30m / 2h.",
                new[] { ("Flow", ColExec), ("Name", ColString), ("Amount", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Timer.SetHappyHour", "Timer", timer,
                "Start a Happy-Hour window that multiplies the time added by incoming events. Multiplier is the boost (e.g. 2 = double time); Duration is how long the window lasts; Scope limits which events it applies to (all | subs | bits | tips | follows | raids).",
                new[] { ("Flow", ColExec), ("Name", ColString), ("Multiplier", ColString), ("Duration", ColString), ("Scope", ColString) },
                new[] { ("Done", ColExec) });

            // ── Value nodes (no flow — resolved inline) ──────────────────────
            // (Timer.GetFormatted / GetPaused / GetProgress were RETIRED in the
            // 2026-08 tool-node cut: the overlay live channel publishes
            // timer.<name>.short/.long/.clock/.paused/.progress every second, so
            // Overlay.Get covers them; GetPaused was additionally just
            // GetState == "Paused". GetRemaining and GetState stay because a graph
            // that mutates a timer and reads it back in the same run needs a fresh
            // read the 1 Hz channel cannot guarantee.)
            AddTemplate("Timer.GetRemaining", "Timer", timer,
                "Outputs the timer's remaining time in whole seconds. Empty Name reads the default timer.",
                new[] { ("Name", ColString) },
                new[] { ("Seconds", ColNumber) });

            AddTemplate("Timer.GetState", "Timer", timer,
                "Outputs the timer's lifecycle state: Stopped, Running, Paused or Ended.",
                new[] { ("Name", ColString) },
                new[] { ("State", ColString) });

            // ── Event nodes (category "Events" — output-only roots) ──────────
            // MIRROR Twitch.Subscription: null inputs, Flow output first. Category
            // "Events" makes them script entry points; the exporter emits
            // on_event(Timer.OnZero) / on_event(Timer.OnMilestone) /
            // on_event(Timer.OnAdd) via ProcessEventNode's trigger-switch fallback,
            // matching the phoenixEvent strings TimerService raises.
            AddTemplate("Timer.OnZero", "Events", timer,
                "Fires once when a timer reaches zero and ends. TimerName carries the timer that hit zero.",
                null,
                new[] { ("Flow", ColExec), ("TimerName", ColString) });

            AddTemplate("Timer.OnMilestone", "Events", timer,
                "Fires the first time a timer's remaining time crosses one of its milestone targets. Carries the milestone id and label.",
                null,
                new[] { ("Flow", ColExec), ("TimerName", ColString), ("MilestoneId", ColString), ("Label", ColString) });

            AddTemplate("Timer.OnAdd", "Events", timer,
                "Fires whenever time is added to a timer by an event or command. Source names what added it (e.g. sub, bits, manual); Seconds is how many seconds were added.",
                null,
                new[] { ("Flow", ColExec), ("TimerName", ColString), ("Source", ColString), ("Seconds", ColString) });

            // ── Socket-level hover help (canvas pin pop-ups + doc form) ──────
            // The Name/Amount/Style/Scope pills need the "empty = default" and
            // duration-string rules spelled out — the pill alone doesn't convey them.
            SetSocketDescriptions("Timer.Start", new()
            {
                { "Name",     "Which timer to control — matched by slug or name (case-insensitive). Leave empty to target the default timer." },
                { "Duration", "Optional start duration. Bare number = seconds; units 90s / 5m / 1h30m / 2h / 1d. Empty uses the timer's configured start duration." },
            });
            SetSocketDescriptions("Timer.Stop",   new() { { "Name", "Which timer to control — slug or name. Empty = the default timer." } });
            SetSocketDescriptions("Timer.Pause",  new() { { "Name", "Which timer to control — slug or name. Empty = the default timer." } });
            SetSocketDescriptions("Timer.Resume", new() { { "Name", "Which timer to control — slug or name. Empty = the default timer." } });
            SetSocketDescriptions("Timer.Toggle", new() { { "Name", "Which timer to control — slug or name. Empty = the default timer." } });
            SetSocketDescriptions("Timer.Reset",  new() { { "Name", "Which timer to control — slug or name. Empty = the default timer." } });
            SetSocketDescriptions("Timer.Add", new()
            {
                { "Name",   "Which timer to control — slug or name. Empty = the default timer." },
                { "Amount", "How much time to add. Bare number = seconds; units 90s / 5m / 1h30m / 2h / 1d. Negative removes time (e.g. -90s) and clamps the countdown at zero." },
            });
            SetSocketDescriptions("Timer.SetTime", new()
            {
                { "Name",   "Which timer to control — slug or name. Empty = the default timer." },
                { "Amount", "The absolute remaining time to set. Bare number = seconds; units 5m / 1h30m / 2h." },
            });
            SetSocketDescriptions("Timer.SetHappyHour", new()
            {
                { "Name",       "Which timer to control — slug or name. Empty = the default timer." },
                { "Multiplier", "Time-added multiplier while Happy Hour is active (e.g. 2 = double the seconds each event adds)." },
                { "Duration",   "How long the Happy-Hour window lasts. Bare number = seconds; units 5m / 1h / 30m." },
                { "Scope",      "Which events Happy Hour boosts: all, subs, bits, tips, follows or raids." },
            });
            SetSocketDescriptions("Timer.GetRemaining", new()
            {
                { "Name",    "Which timer to read — slug or name. Empty = the default timer." },
                { "Seconds", "The timer's remaining time in whole seconds." },
            });
            SetSocketDescriptions("Timer.GetState", new()
            {
                { "Name",  "Which timer to read — slug or name. Empty = the default timer." },
                { "State", "Stopped, Running, Paused or Ended." },
            });
            SetSocketDescriptions("Timer.OnZero", new()
            {
                { "TimerName", "The timer that reached zero." },
            });
            SetSocketDescriptions("Timer.OnMilestone", new()
            {
                { "TimerName",   "The timer that crossed the milestone." },
                { "MilestoneId", "Id of the milestone that was reached." },
                { "Label",       "Display label of the milestone that was reached." },
            });
            SetSocketDescriptions("Timer.OnAdd", new()
            {
                { "TimerName", "The timer that time was added to." },
                { "Source",    "What added the time (e.g. sub, bits, tip, raid, manual)." },
                { "Seconds",   "How many seconds were added." },
            });

            // Fuzzy spawn-search aliases — the gap between what a user types
            // ("countdown", "subathon") and the canonical Timer.* titles.
            SetKeywords("Timer.Start",       "timer", "countdown", "subathon", "start");
            SetKeywords("Timer.Stop",        "timer", "countdown", "stop");
            SetKeywords("Timer.Pause",       "timer", "countdown", "pause");
            SetKeywords("Timer.Resume",      "timer", "countdown", "resume", "unpause");
            SetKeywords("Timer.Toggle",      "timer", "countdown", "toggle");
            SetKeywords("Timer.Reset",       "timer", "countdown", "reset");
            SetKeywords("Timer.Add",         "timer", "countdown", "add", "raise", "subtract", "remove", "subathon");
            SetKeywords("Timer.SetTime",     "timer", "countdown", "set");
            SetKeywords("Timer.SetHappyHour","timer", "happyhour", "multiplier", "boost");
            SetKeywords("Timer.GetRemaining","timer", "countdown", "remaining", "seconds");
            SetKeywords("Timer.GetState",    "timer", "state", "paused");
            SetKeywords("Timer.OnZero",      "timer", "countdown", "zero", "ended", "finished");
            SetKeywords("Timer.OnMilestone", "timer", "milestone", "goal");
            SetKeywords("Timer.OnAdd",       "timer", "added", "raise");
        }
    }
}
