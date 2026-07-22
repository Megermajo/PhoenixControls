using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// SchedulerService — fires named scripts on cron expressions, fixed intervals, or one-shot datetimes.
    ///
    /// Reads schedule entries from AppConfig.Schedules AND scans every <c>.phx</c> in the logic
    /// directory for <c>on_schedule(...)</c> / <c>on_schedule_once(...)</c> / <c>on_interval(...)</c>
    /// header blocks. Both sources are additive: a script that has BOTH a config entry and a header
    /// block fires twice — power users can deliberately overlap them.
    ///
    /// NOTE: a single .phx with multiple top-level on_schedule* / on_interval blocks fires the
    /// WHOLE file each time any of those triggers, because the script engine doesn't filter top-level
    /// blocks by which schedule fired. Out of scope for C8.
    /// </summary>
    public class SchedulerService
    {
        private static SchedulerService? _instance;
        public static SchedulerService Instance => _instance ??= new SchedulerService();

        private CancellationTokenSource _cts = new CancellationTokenSource();

        // Compiled once. Captures the cron / datetime / seconds argument for each header type.
        private static readonly Regex ScheduleHeaderRegex = new Regex(
            @"^[ \t]*(?<kind>on_schedule|on_schedule_once|on_interval)\(\s*""?(?<arg>[^""\)]+?)""?\s*\)\s*:",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private SchedulerService() { }

        /// <summary>Starts all enabled schedule entries (config + .phx headers) as background tasks.</summary>
        public void Start()
        {
            int configCount  = StartConfigSchedules(ConfigManager.Current.Schedules);
            int scriptCount  = LoadSchedulesFromScripts();

            int total = configCount + scriptCount;
            if (total > 0)
                GlobalLogger.Log($"SchedulerService: {configCount} config + {scriptCount} script-header schedule(s) active.", "Scheduler", LogLevel.System);
        }

        /// <summary>Cancels all running schedule loops. A subsequent <see cref="Start"/>
        /// call will rebuild the cancellation source.</summary>
        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
            try { _cts.Dispose(); } catch { }
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// Stop + Start in sequence. Wired to <see cref="LogicWatcher.OnRefresh"/>
        /// from HubBootstrapper so an edited <c>on_schedule</c> / <c>on_interval</c>
        /// header in a saved <c>.phx</c> takes effect without a Hub restart.
        /// </summary>
        public void Reload()
        {
            Stop();
            Start();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONFIG-DRIVEN SCHEDULES  (existing behavior, refactored out of Start)
        // ─────────────────────────────────────────────────────────────────────

        private int StartConfigSchedules(List<ScheduleEntry>? entries)
        {
            if (entries == null || entries.Count == 0) return 0;
            int count = 0;
            // Snapshot the token at queue time so each task observes the CTS that
            // was alive when its schedule was registered. Reading `_cts.Token` lazily inside
            // the lambda made each Reload() bind queued tasks to the FRESH (uncancelled)
            // token, so Stop() never actually stopped the previous-generation tasks and
            // they accumulated against every settings save.
            var token = _cts.Token;
            foreach (var entry in entries)
            {
                if (!entry.Enabled) continue;
                var captured = entry;
                _ = Task.Run(() => RunEntryAsync(captured, token));
                count++;
            }
            return count;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SCRIPT-HEADER SCHEDULES  (C8)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Scans every <c>.phx</c> in the configured logic directory for top-level
        /// <c>on_schedule(...)</c>, <c>on_schedule_once(...)</c>, <c>on_interval(...)</c>
        /// header lines and registers a synthesized scheduler entry for each match.
        /// Returns the number of entries started.
        /// </summary>
        private int LoadSchedulesFromScripts()
        {
            string logicPath = ResolveLogicPath();
            if (string.IsNullOrEmpty(logicPath) || !Directory.Exists(logicPath)) return 0;

            int count = 0;
            // Same snapshot rationale as StartConfigSchedules.
            var token = _cts.Token;
            string[] files;
            try { files = Directory.GetFiles(logicPath, "*.phx"); }
            catch (Exception ex)
            {
                GlobalLogger.Error("Scheduler", $"failed to enumerate logic dir '{logicPath}'", ex);
                return 0;
            }

            foreach (var file in files)
            {
                // Live-process TEMPLATES are NOT whole-file schedules — they run
                // per-instance (ProcessInstanceManager owns their timers). GetFiles is
                // non-recursive so a processes/ subfolder is already excluded; this is a
                // defensive guard in case the logic path is ever mis-pointed.
                if (ProcessTemplateRegistry.IsUnderProcessesFolder(file)) continue;

                string source;
                try { source = File.ReadAllText(file); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Scheduler", $"failed to read '{Path.GetFileName(file)}'", ex);
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(file);

                foreach (var entry in ParseScheduleHeaders(source, name))
                {
                    var captured = entry;
                    _ = Task.Run(() => RunEntryAsync(captured, token));
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Parses on_schedule / on_schedule_once / on_interval header blocks out of a
        /// script body into <see cref="ScheduleEntry"/> objects. Shared by the
        /// file-level scheduler and <see cref="ProcessInstanceManager"/>'s per-instance
        /// timers. Invalid on_interval values are logged + skipped.
        /// </summary>
        internal static IEnumerable<ScheduleEntry> ParseScheduleHeaders(string content, string name)
        {
            foreach (Match m in ScheduleHeaderRegex.Matches(content ?? string.Empty))
            {
                string kind = m.Groups["kind"].Value;
                string arg  = m.Groups["arg"].Value.Trim();

                var entry = new ScheduleEntry { Name = name, Enabled = true };
                switch (kind)
                {
                    case "on_schedule":
                        entry.CronExpression = arg;
                        break;
                    case "on_schedule_once":
                        entry.RunAt = arg;
                        break;
                    case "on_interval":
                    {
                        // on_interval(seconds[, minChatLines]) — the regex captures the
                        // whole "300, 5" as one group, so split off the seconds first.
                        // Single-arg form parses exactly as before.
                        string[] parts = arg.Split(',');
                        if (!int.TryParse(parts[0].Trim(), out int seconds) || seconds <= 0)
                        {
                            GlobalLogger.Log($"Scheduler: '{name}' has invalid on_interval('{arg}') — skipping.", "Scheduler", LogLevel.CriticalError);
                            continue;
                        }
                        entry.IntervalSeconds = seconds;
                        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int minChatLines) && minChatLines > 0)
                            entry.MinChatLines = minChatLines;
                        break;
                    }
                    default:
                        continue;
                }
                yield return entry;
            }
        }

        /// <summary>
        /// Resolves the logic directory: honors absolute paths in config, otherwise
        /// uses the solution-anchored Hub data folder via <see cref="Paths"/>.
        /// </summary>
        private static string ResolveLogicPath()
        {
            string rel = ConfigManager.Current.LogicDirectory ?? "data/logic";
            if (Path.IsPathRooted(rel)) return rel;

            string? sln = Paths.FindSolutionRoot();
            string root = sln != null
                ? Path.Combine(sln, Paths.HubProjectFolderName)
                : Paths.AppBase;
            return Path.Combine(root, rel);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ENTRY RUNNER
        // ─────────────────────────────────────────────────────────────────────

        // File-level scheduler entry — delegates to the shared loop with a closure
        // that fires the whole named script via FireScriptAsync.
        private async Task RunEntryAsync(ScheduleEntry entry, CancellationToken ct)
        {
            string mode = !string.IsNullOrWhiteSpace(entry.RunAt) ? "schedule_once"
                        : entry.IntervalSeconds > 0               ? "interval"
                        :                                           "cron";
            await RunScheduleLoopAsync(entry, ct, fireCount => FireScriptAsync(entry.Name, mode, fireCount));
        }

        /// <summary>
        /// The schedule wait/fire loop, parameterised on what to do per fire. Shared by
        /// the file-level scheduler (fires the named script) and
        /// <see cref="ProcessInstanceManager"/>'s per-instance timers (runs the instance
        /// template). <paramref name="fire"/> is invoked with the running fire count
        /// (1-based; always 1 for a one-shot RunAt). Cancellation via
        /// <paramref name="ct"/> unwinds the loop cleanly.
        /// </summary>
        internal static async Task RunScheduleLoopAsync(ScheduleEntry entry, CancellationToken ct, Func<int, Task> fire)
        {
            try
            {
                // ── Mode 1: RunAt (once at a specific time) ──────────────
                if (!string.IsNullOrWhiteSpace(entry.RunAt))
                {
                    // DateTimeOffset.Now folds the local offset in so a RunAt with
                    // a timezone designator resolves without DST skip/double-fire.
                    if (DateTimeOffset.TryParse(entry.RunAt, out DateTimeOffset fireAt))
                    {
                        TimeSpan delay = fireAt - DateTimeOffset.Now;
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, ct);
                        await fire(1);
                    }
                    return;
                }

                // ── Mode 2: Fixed interval ───────────────────────────────
                if (entry.IntervalSeconds > 0)
                {
                    int fireCount = 0;
                    // Chat-activity gate baseline (Schedule.Recurring's MinChatLines).
                    // Snapshot the running chat count; each interval only fires when at
                    // least MinChatLines new lines arrived since the LAST fire. A skipped
                    // interval does NOT advance the baseline or the fire count — it just
                    // waits another interval. MinChatLines == 0 (the default, and every
                    // non-interval caller such as ProcessInstanceManager) disables the
                    // gate entirely, so behaviour is unchanged there.
                    long lastFireLines = ChatActivityCounter.Current;
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(entry.IntervalSeconds), ct);
                        if (entry.MinChatLines > 0
                            && (ChatActivityCounter.Current - lastFireLines) < entry.MinChatLines)
                            continue; // not enough chat activity yet — wait another interval
                        await fire(++fireCount);
                        lastFireLines = ChatActivityCounter.Current;
                    }
                    return;
                }

                // ── Mode 3: Cron expression ──────────────────────────────
                if (!string.IsNullOrWhiteSpace(entry.CronExpression))
                {
                    int fireCount = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        // Compute next occurrence + wait-delta in UTC (DST safety).
                        DateTime next = GetNextCronOccurrence(entry.CronExpression, DateTime.UtcNow);
                        if (next == DateTime.MaxValue) return; // invalid cron, already logged
                        TimeSpan wait = next - DateTime.UtcNow;
                        if (wait > TimeSpan.Zero)
                            await Task.Delay(wait, ct);
                        await fire(++fireCount);
                    }
                }
            }
            catch (OperationCanceledException) { /* normal cancel/shutdown */ }
            catch (Exception ex)
            {
                GlobalLogger.Error("Scheduler", $"Scheduler error for '{entry.Name}'", ex);
            }
        }

        private static async Task FireScriptAsync(string scriptName, string mode, int fireCount = 0)
        {
            try
            {
                var fakeMsg = new ChatMessage
                {
                    Username = "scheduler",
                    Message  = $"#{scriptName}"
                };
                // Populate the Schedule node payload outputs: Timestamp
                // (all modes, ISO-8601 fire time) and Count (Recurring/interval running
                // fire number). Previously {event.timestamp}/{event.count} resolved empty.
                var extraVars = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["event.timestamp"] = DateTimeOffset.Now.ToString("o"),
                    ["event.count"]     = fireCount.ToString(),
                };
                await ScriptManager.Instance.ExecuteEventScriptAsync(scriptName, fakeMsg, extraVars);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Scheduler", $"'{scriptName}' execution error", ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CRON PARSER  (5-field: minute hour day month weekday)
        //  Extensions: named days/months, L (last day), # (nth weekday)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Normalizes a cron expression: substitutes named days/months with numbers.
        /// e.g. "0 12 * * MON-FRI" → "0 12 * * 1-5"
        /// </summary>
        private static string NormalizeCronExpression(string cron)
        {
            cron = Regex.Replace(cron, @"\bSUN\b", "0", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bMON\b", "1", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bTUE\b", "2", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bWED\b", "3", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bTHU\b", "4", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bFRI\b", "5", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bSAT\b", "6", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bJAN\b", "1",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bFEB\b", "2",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bMAR\b", "3",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bAPR\b", "4",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bMAY\b", "5",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bJUN\b", "6",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bJUL\b", "7",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bAUG\b", "8",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bSEP\b", "9",  RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bOCT\b", "10", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bNOV\b", "11", RegexOptions.IgnoreCase);
            cron = Regex.Replace(cron, @"\bDEC\b", "12", RegexOptions.IgnoreCase);
            return cron;
        }

        /// <summary>Returns the next DateTime at or after <paramref name="after"/> matching the cron expression.
        /// Returns DateTime.MaxValue if the expression is invalid.
        ///
        /// `internal` (was `private`) so the Phoenix.Controls.Tests project can
        /// pin DST-safety and cron-arithmetic contracts directly without a
        /// full SchedulerService bring-up. InternalsVisibleTo for Tests is
        /// declared in Phoenix.Controls.Hub.csproj line 23.</summary>
        internal static DateTime GetNextCronOccurrence(string cron, DateTime after)
        {
            cron = NormalizeCronExpression(cron);
            string[] fields = cron.Trim().Split(' ');
            if (fields.Length < 5)
            {
                WarnInvalidCronOnce(cron, "invalid cron expression (need 5 fields)");
                return DateTime.MaxValue;
            }

            // Advance by 1 minute (next occurrence is always strictly in the future)
            DateTime candidate = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0)
                .AddMinutes(1);

            string dayField = fields[2];
            string dowField = fields[4];

            // Vixie cron rule: when EITHER day-of-month OR
            // day-of-week is `*`, the candidate matches if the other one matches
            // (AND semantics). When BOTH fields are restricted, EITHER one matching
            // is enough (OR semantics). The previous code unconditionally AND-ed,
            // which made `0 9 1 * MON` only fire on Monday-the-1st instead of
            // "the 1st of every month OR every Monday".
            bool dayIsStar = dayField == "*";
            bool dowIsStar = dowField == "*";

            // Search up to 1 year ahead to find next match
            DateTime limit = after.AddYears(1);
            while (candidate < limit)
            {
                bool dayMatch = DayFieldMatches(dayField, candidate);
                bool dowMatch = DowFieldMatches(dowField, candidate);

                bool dayDowOk = (dayIsStar || dowIsStar)
                    ? (dayMatch && dowMatch)
                    : (dayMatch || dowMatch);

                if (FieldMatches(fields[1], candidate.Hour, fieldMin: 0)  &&
                    dayDowOk                                              &&
                    FieldMatches(fields[3], candidate.Month, fieldMin: 1))
                {
                    // Minute field: find the first matching minute in this hour
                    for (int m = candidate.Minute; m < 60; m++)
                    {
                        if (FieldMatches(fields[0], m, fieldMin: 0))
                            return new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, m, 0);
                    }
                    // No matching minute in this hour — advance to next hour
                    candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, 0, 0).AddHours(1);
                }
                else
                {
                    candidate = candidate.AddMinutes(1);
                }
            }

            WarnInvalidCronOnce(cron, "no occurrence within 1 year (unmatchable expression)");
            return DateTime.MaxValue;
        }

        // De-spam: a user-typed bad cron (e.g. "*15" instead of "*/15") otherwise logged a
        // CriticalError on EVERY scheduler reload — and the scheduler reloads on every Hub
        // startup and every Architect save (LOGIC_RELOAD), so one typo produced a flood. Log
        // each distinct bad cron ONCE per process, at System level (it's authoring content,
        // not a system fault). Re-saving a corrected value just stops producing the warning.
        private static readonly System.Collections.Generic.HashSet<string> s_warnedInvalidCrons = new();
        private static void WarnInvalidCronOnce(string cron, string reason)
        {
            bool first;
            lock (s_warnedInvalidCrons) first = s_warnedInvalidCrons.Add(cron ?? string.Empty);
            if (first)
                GlobalLogger.Log($"Scheduler: skipping a schedule — {reason}: '{cron}'. Fix the Schedule node's cron value and re-save.", "Scheduler", LogLevel.System);
        }

        /// <summary>Handles day-of-month field including L (last day of month).</summary>
        private static bool DayFieldMatches(string field, DateTime candidate)
        {
            if (field == "L")
                return candidate.Day == DateTime.DaysInMonth(candidate.Year, candidate.Month);
            // Day-of-month starts at 1 in Vixie cron, so `*/5` resolves to
            // 1, 6, 11, 16, 21, 26, 31 — not 5, 10, 15, 20, 25, 30.
            return FieldMatches(field, candidate.Day, fieldMin: 1);
        }

        /// <summary>Handles day-of-week field including # (nth weekday, e.g. 2#3 = 3rd Tuesday).</summary>
        private static bool DowFieldMatches(string field, DateTime candidate)
        {
            if (field == "*") return true;

            foreach (var part in field.Split(','))
            {
                // nth weekday: DOW#N  e.g. "2#3" = 3rd Tuesday
                if (part.Contains('#'))
                {
                    var hashParts = part.Split('#');
                    if (hashParts.Length == 2
                        && int.TryParse(hashParts[0], out int dow)
                        && int.TryParse(hashParts[1], out int nth))
                    {
                        if ((int)candidate.DayOfWeek == dow)
                        {
                            int occurrence = (candidate.Day - 1) / 7 + 1;
                            if (occurrence == nth) return true;
                        }
                    }
                    continue;
                }

                // Standard matching (ranges, steps, exact). DOW starts at 0 (Sunday).
                if (FieldMatches(part, (int)candidate.DayOfWeek, fieldMin: 0)) return true;
            }

            return false;
        }

        /// <summary>Returns true if <paramref name="value"/> satisfies a cron field expression (no # or L).</summary>
        /// <param name="fieldMin">
        /// Per-field minimum used as the start when the step expression is `*/n`.
        /// Minute/hour/dow start at 0; day-of-month and month start at 1. The previous code
        /// hard-coded 0, which made `*/5` on the day field match 5,10,15,20,25,30 instead of
        /// the standard 1,6,11,16,21,26,31. Same defect on the month field.
        /// </param>
        private static bool FieldMatches(string field, int value, int fieldMin = 0)
        {
            if (field == "*") return true;

            foreach (var part in field.Split(','))
            {
                // Step: */n or start/n
                if (part.Contains('/'))
                {
                    // Malformed steps with extra slashes (e.g. "1/2/3") are rejected rather
                    // than silently parsing only the first two components.
                    var stepParts = part.Split('/');
                    if (stepParts.Length == 2 && int.TryParse(stepParts[1], out int step) && step > 0)
                    {
                        int start = stepParts[0] == "*"
                            ? fieldMin
                            : (int.TryParse(stepParts[0], out int s) ? s : fieldMin);
                        if (value >= start && (value - start) % step == 0) return true;
                    }
                }
                // Range: a-b
                else if (part.Contains('-'))
                {
                    // Malformed ranges with extra hyphens (e.g. "1-5-9") are rejected rather
                    // than silently parsing only the first two components.
                    var rangeParts = part.Split('-');
                    if (rangeParts.Length == 2
                        && int.TryParse(rangeParts[0], out int lo)
                        && int.TryParse(rangeParts[1], out int hi))
                    {
                        // Normalize reversed ranges (e.g. "12-1") by swapping the bounds.
                        // Left as-is, value >= lo && value <= hi is never true so the cron
                        // would silently never fire.
                        if (lo > hi) (lo, hi) = (hi, lo);
                        if (value >= lo && value <= hi) return true;
                    }
                }
                // Exact value
                else if (int.TryParse(part, out int exact) && exact == value)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
