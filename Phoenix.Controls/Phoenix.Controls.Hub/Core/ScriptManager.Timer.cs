using System;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: timer.* command registrations (subathon countdown).
    //
    // 11 handlers backing the Architect Timer nodes — all drive the shared
    // TimerService.Instance (the same instance the Hub Timer page binds to
    // directly, so a node-driven add shows up live in the page and on the OBS
    // overlay). Nine void control/config commands (return null) + two inline
    // value reads (return the value string; the exporter evaluates those calls
    // as expressions, mirroring queue.length / giveaway.default_id).
    //
    // timer.subtract / get_formatted / get_paused / get_progress were RETIRED in
    // the 2026-08 tool-node cut: timer.add is signed (a negative amount routes
    // through the subtract path below) and the three reads are covered by
    // overlay.get on the 1 Hz timer.<name>.* live-channel keys. The old names
    // answer through ScriptManager.RetiredCommands shims.
    //
    // Seam wiring lives at the top of RegisterTimerCommands: TimerService can't
    // touch the Bus or the script dispatcher itself (those live on this side),
    // so it gets RaiseScriptEvent + BusEmit injected here — the mirror of
    // GiveawayService.SubscriberStatusResolver in ScriptManager.Giveaway.cs.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterTimerCommands()
        {
            // ── Seams ────────────────────────────────────────────────────────
            // Timer.On{Zero,Milestone,Add} script events flow back through the
            // generic-event dispatcher with pre-built vars (the presetVars
            // overload skips the JsonElement var-builder). Bus fan-out reuses the
            // same Target="*" broadcast shape as bus.broadcast.
            TimerService.Instance.RaiseScriptEvent =
                (phoenixEvent, vars) => ExecuteGenericEventAsync(phoenixEvent, default, vars);
            TimerService.Instance.BusEmit = (busType, payloadJson) =>
                Bus.Instance.BroadcastAsync(new BusMessage
                {
                    Type = busType,
                    Source = "Hub",
                    Target = "*",
                    Payload = string.IsNullOrEmpty(payloadJson) ? "{}" : payloadJson,
                });

            // Feedback seams for the per-timer OnZero / OnMilestone / OnAdd responses.
            // Chat rides the SAME core that chat.send / twitch.send_chat use — no new
            // send path — and that core owns the connectivity + chat-action guards and logs
            // its own drops, so there is no TrySend*-style wrapper here: unlike
            // Scheduling, the Timer has no per-fire counter that a silent drop would
            // make dishonest. The 500-char cap is applied SERVICE-side (a composed
            // summary line has to be clipped rather than dropped).
            TimerService.Instance.SendChat = message =>
            {
                if (!string.IsNullOrWhiteSpace(message)) SendTwitchChatCore(message, "timer");
                return Task.CompletedTask;
            };

            // Visuals ride the shared (layer, trigger) fan-out every pre-build tool
            // effect uses — Hub executes, VISUAL_TRIGGER crosses the bus, the Visualist
            // overlay reacts. No bespoke media path.
            TimerService.Instance.FireVisual = (layerId, triggerName, eventData) =>
                FireVisualTriggerFanOutAsync(layerId, triggerName, eventData, "TimerService");

            // ── Control (void → null) ────────────────────────────────────────
            _engine.RegisterCommand("timer.start", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string name = StripBareQuotes(bound?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string durArg = StripBareQuotes(bound?.GetOrDefault<string>("Duration", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                long? durationMs = string.IsNullOrWhiteSpace(durArg) ? (long?)null : ParseDurationToMs(durArg);
                await TimerService.Instance.StartAsync(name, durationMs);
                return null;
            });

            _engine.RegisterCommand("timer.stop", async (args) =>
            {
                await TimerService.Instance.StopAsync(ReadName(args));
                return null;
            });

            _engine.RegisterCommand("timer.pause", async (args) =>
            {
                await TimerService.Instance.PauseAsync(ReadName(args));
                return null;
            });

            _engine.RegisterCommand("timer.resume", async (args) =>
            {
                await TimerService.Instance.ResumeAsync(ReadName(args));
                return null;
            });

            _engine.RegisterCommand("timer.toggle", async (args) =>
            {
                await TimerService.Instance.ToggleAsync(ReadName(args));
                return null;
            });

            _engine.RegisterCommand("timer.reset", async (args) =>
            {
                await TimerService.Instance.ResetAsync(ReadName(args));
                return null;
            });

            _engine.RegisterCommand("timer.add", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string name = StripBareQuotes(bound?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string amount = StripBareQuotes(bound?.GetOrDefault<string>("Amount", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                long ms = ParseDurationToMs(amount);
                // Signed since the 2026-08 tool-node cut (timer.subtract retired):
                // a negative amount routes through the subtract path so it keeps that
                // command's exact semantics — clamp at zero, fire Timer.OnZero when
                // the countdown lands (or already sits) there, never raise OnAdd,
                // never re-arm an ended timer.
                if (ms > 0) await TimerService.Instance.AddMsAsync(name, ms, "manual");
                else if (ms < 0) await TimerService.Instance.SubtractMsAsync(name, -ms);
                return null;
            });

            _engine.RegisterCommand("timer.set_time", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string name = StripBareQuotes(bound?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string amount = StripBareQuotes(bound?.GetOrDefault<string>("Amount", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                await TimerService.Instance.SetTimeMsAsync(name, ParseDurationToMs(amount));
                return null;
            });

            _engine.RegisterCommand("timer.set_happy_hour", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string name = StripBareQuotes(bound?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string multArg = StripBareQuotes(bound?.GetOrDefault<string>("Multiplier", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                string durArg = StripBareQuotes(bound?.GetOrDefault<string>("Duration", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2));
                string scope = StripBareQuotes(bound?.GetOrDefault<string>("Scope", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3));
                double mult = double.TryParse(multArg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 1.0;
                long durationMs = ParseDurationToMs(durArg);
                if (string.IsNullOrWhiteSpace(scope)) scope = "all";
                await TimerService.Instance.SetHappyHourAsync(name, mult, durationMs, scope);
                return null;
            });

            // ── Value reads (inline expression → return the value string) ────
            _engine.RegisterCommand("timer.get_remaining", async (args) =>
            {
                long secs = TimerService.Instance.GetRemainingMs(ReadName(args)) / 1000;
                return secs.ToString(CultureInfo.InvariantCulture);
            });

            _engine.RegisterCommand("timer.get_state", async (args) =>
                TimerService.Instance.GetState(ReadName(args)).ToString());
        }

        // Shared "Name" read for the single-arg timer commands.
        private string ReadName(string[] args)
            => StripBareQuotes(_engine.CurrentBoundArgs?.GetOrDefault<string>("Name", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));

        // Duration parser for the timer.* string duration args. Bare number =
        // seconds ("300" → 300s); unit tokens d/h/m/s, combinable ("1h30m",
        // "90s", "2h", "1d"); colon clock forms ("1:30", "01:30:00") via the
        // shared TimerService grammar. Tolerant: quotes stripped, unknown
        // text → 0. A leading '-' negates the WHOLE amount ("-90s", "-1h30m",
        // "-30", "-1:30"): timer.add is the one signed increment command since
        // timer.subtract was retired (2026-08), and the unit loop below reads
        // digits only — without the explicit sign hoist it would skip the '-'
        // as a stray separator and silently turn "-90s" into +90 seconds.
        private static long ParseDurationToMs(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = StripBareQuotes(raw.Trim()).Trim();
            if (s.Length == 0) return 0;

            bool negative = s[0] == '-';
            if (negative) s = s.Substring(1).Trim();
            if (s.Length == 0) return 0;

            // Colon clock form — delegate to the ONE strict grammar
            // (TimerService.ParseDurationToMs, the reader the Timer panel
            // teaches). Without this, the tolerant digit loop below reads
            // "01:30:00" as 1 + skip + 30 + skip + 00 unit-less seconds = 31s
            // and calls it valid — a silently wrong subathon amount from the
            // exact spelling the panel tooltips advertise. Malformed colon
            // input maps to this parser's documented failure value (0).
            if (s.Contains(':'))
            {
                long clockMs = TimerService.ParseDurationToMs(s);
                if (clockMs < 0) return 0;
                return negative ? -clockMs : clockMs;
            }

            // Bare number → seconds.
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double bareSeconds))
                return (negative ? -1L : 1L) * (long)Math.Round(Math.Abs(bareSeconds) * 1000.0);

            long totalMs = 0;
            bool matched = false;
            int i = 0;
            while (i < s.Length)
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i == start) { i++; continue; } // skip stray separator
                if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    continue;
                char unit = i < s.Length ? char.ToLowerInvariant(s[i]) : 's';
                if (i < s.Length) i++;
                long unitMs = unit switch
                {
                    'd' => 86_400_000L,
                    'h' => 3_600_000L,
                    'm' => 60_000L,
                    's' => 1_000L,
                    _ => 1_000L,
                };
                totalMs += (long)Math.Round(num * unitMs);
                matched = true;
            }
            return matched ? (negative ? -totalMs : totalMs) : 0;
        }
    }
#pragma warning restore CS1998
}
