// Variables band carved from ScriptEngine.cs ().
// Owns: variable substitution and resolution —
//   SubstituteVars (single-pass {var} substitution, L68/R22/R23/M16),
//   ResolveSystemVar ({system.*} lazy resolver, M16/M17),
//   IsPositionalPlaceholder, ResolveVar.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Core
{
    public partial class ScriptEngine
    {
        // ─────────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────────

        // L68 + R22 + R23 — single-pass {var} substitution.
        // Previously this method ran a foreach over `vars` invoking
        // Regex.Replace per key, making total work O(n*m) in (input length
        // × var-count). It also allocated a per-call systemVars dict whose
        // contents are all clock-derived. The refactor:
        //   * One match-evaluator pass over `text` via the static VarRefRegex.
        //   * Lookup precedence preserved (user/local vars > system vars).
        //   * System vars are computed lazily (ResolveSystemVar) only when
        //     the regex actually finds {system.*}, so a script that never
        //     references the clock pays nothing.
        //   * The bare-name word-boundary pass is kept (legacy contract) but
        //     only fires for vars whose key actually appears in text — the
        //     fast IndexOf gate avoids the per-key compiled-regex churn.
        // Marked internal so BugFixSweep3_EnginePerf_Tests can exercise edge
        // cases (missing keys, brace literals in values, system-var freshness)
        // without changing the public API surface.
        internal string SubstituteVars(string text, Dictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            // Surface unmatched-brace shapes before substitution. A
            // brace segment that contains characters VarRefRegex can't
            // accept (`[\$\w\.]`) — e.g. `{global.score:default}`,
            // `{global.list{0}}` — silently passed through with no warning,
            // making it look like a missing variable instead of a parser
            // miss. Only fires when the text has at least one `{` (cheap
            // IndexOf gate); the regex is bounded by `[^{}]*` so it can't
            // backtrack on long inputs. Logging at Communication tier so it
            // shows up in the script-author's feed but doesn't elevate to
            // CriticalError (the engine still ships the raw token through).
            if (text.IndexOf('{') >= 0)
            {
                foreach (System.Text.RegularExpressions.Match braceMatch in SuspiciousBraceRegex.Matches(text))
                {
                    string seg = braceMatch.Value;
                    // Skip well-formed var references — VarRefRegex matches
                    // exactly the `{[$\w.]+}` shape; anything else with
                    // surrounding braces is suspicious.
                    if (VarRefRegex.IsMatch(seg)) continue;
                    // Empty `{}` and braces that are part of a JSON literal
                    // (chat payloads can carry `{"foo":1}`) are not parser
                    // misses — skip them. The contains-quote check is a
                    // good-enough heuristic for JSON-shaped content.
                    if (seg.Length <= 2) continue;
                    if (seg.IndexOf('"') >= 0) continue;
                    GlobalLogger.Log(
                        $"parse: unmatched brace token '{seg}' in {ScriptFile} — not a recognized variable reference",
                        "ScriptEngine", LogLevel.Communication);
                }
            }

            // Cache BOTH UTC and local "now" once per call so all
            // {system.time}/{system.unix} substitutions inside the SAME call
            // see consistent values. Default {system.*} now uses UTC so
            // {system.unix} and {system.hours} report the same clock; the
            // local-time mirror (system.local_*) preserves the legacy reading.
            // (Across calls the timestamps advance, satisfying R22.)
            DateTime? utcNowCache   = null;
            DateTime? localNowCache = null;

            text = VarRefRegex.Replace(text, m =>
            {
                string key = m.Groups[1].Value;
                if (IsPositionalPlaceholder(key)) return m.Value;
                if (vars.TryGetValue(key, out var v)) return v;
                if (StaticSystemVars.TryGetValue(key, out var sv)) return sv;
                // Lazy system-var resolution — only computes DateTime.UtcNow /
                // DateTime.Now if the script references {system.*}.
                if (key.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
                {
                    utcNowCache   ??= DateTime.UtcNow;
                    localNowCache ??= DateTime.Now;
                    var resolved = ResolveSystemVar(key, utcNowCache.Value, localNowCache.Value);
                    if (resolved != null) return resolved;
                }
                // {stream.*} family, anchored at HubProcessStartedAtUtc
                // by default. The "stream start" signal is configurable via
                // SetStreamStartedAt; the default of "Hub uptime" matches
                // system.* and is changeable later (e.g. to first-chat) without
                // rewriting the consumer scripts.
                if (key.StartsWith("stream.", StringComparison.OrdinalIgnoreCase))
                {
                    utcNowCache ??= DateTime.UtcNow;
                    var resolved = ResolveStreamVar(key, utcNowCache.Value);
                    if (resolved != null) return resolved;
                }
                // event.ret.* contracts: the inner Internal-event handler MAY
                // assign event.ret.<name>; if its branch path didn't, the
                // caller should see an empty string (not the literal
                // "{event.ret.<name>}" leaking into chat / DB / send_chat).
                // Other unresolved keys keep the legacy literal-passthrough
                // contract that BugFixSweep3_EnginePerf_Tests pins, since
                // text.format and similar still need to see the raw token.
                if (key.StartsWith("event.ret.", StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
                return m.Value;
            });
            // Replace bare variable names (word-boundary match) — preserved
            // for legacy scripts that reference vars unwrapped. Gated by a
            // cheap IndexOf so we only build a per-key regex when the key
            // actually appears in `text`, avoiding the O(n*m) trap when the
            // vars dict is large.
            foreach (var kv in vars)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (text.IndexOf(kv.Key, StringComparison.Ordinal) < 0) continue;
                // Look-around MUST exclude '.' on both sides too.
                // Previously the pattern only excluded ["'\w], so a local var
                // named `foo` would rewrite the trailing `foo` in dotted
                // references like `user.foo` / `global.foo` / `event.foo` /
                // `result.foo` (the `.` is not a word char, so the look-behind
                // accepted it). That silently corrupted every cross-namespace
                // lookup whose suffix collided with any short local-var name.
                var rx = BareNameRegexCache.GetOrAdd(kv.Key,
                    k => new Regex($@"(?<![""'\w\.]){Regex.Escape(k)}(?![""'\w\.])", HotRegex));
                text = rx.Replace(text, kv.Value);
            }
            return text;
        }

        // R22 / M16 / M17 — Lazy resolver for the time-sensitive {system.*}
        // family. Returns null for unknown keys so the caller can log + leave
        // the raw token intact.
        //
        // Every "system.*" key without a `local_` prefix now reports UTC
        //      so {system.unix} and {system.hours} can no longer disagree
        //      across the daylight-savings boundary. The legacy local-time
        //      readings live on as `system.local_*` mirrors.
        // Every culture-sensitive `ToString` formatter is pinned to
        //      CultureInfo.InvariantCulture so a non-EN host (de-DE, fr-FR…)
        //      reports the same MonthName / Date strings the script authors
        //      wrote against on their EN dev box. Numeric .ToString() calls
        //      pin the invariant culture too — defensive, since an EN-locale
        //      number formatter drifts on hosts that override NumberFormat.
        private static string? ResolveSystemVar(string key, DateTime utcNow, DateTime localNow)
        {
            var inv = CultureInfo.InvariantCulture;
            return key.ToLowerInvariant() switch
            {
                // UTC-based defaults (M16)
                "system.time"           => utcNow.ToString("HH:mm:ss", inv),
                "system.hours"          => utcNow.Hour.ToString(inv),
                "system.minutes"        => utcNow.Minute.ToString(inv),
                "system.seconds"        => utcNow.Second.ToString(inv),
                "system.unix"           => new DateTimeOffset(utcNow, TimeSpan.Zero).ToUnixTimeSeconds().ToString(inv),
                "system.date"           => $"{utcNow.Day.ToString(inv)}.{utcNow.ToString("MMMM", inv)}.{utcNow.Year.ToString(inv)}",
                "system.day"            => utcNow.Day.ToString(inv),
                "system.month"          => utcNow.Month.ToString(inv),
                "system.monthname"      => utcNow.ToString("MMMM", inv),
                "system.dayname"        => utcNow.ToString("dddd", inv),
                "system.year"           => utcNow.Year.ToString(inv),

                // Local-time mirrors (M16) — preserved for scripts that need
                // the streamer's wall-clock reading (e.g. "Good morning"
                // banners that key off local hours).
                "system.local_time"     => localNow.ToString("HH:mm:ss", inv),
                "system.local_hours"    => localNow.Hour.ToString(inv),
                "system.local_minutes"  => localNow.Minute.ToString(inv),
                "system.local_seconds"  => localNow.Second.ToString(inv),
                "system.local_date"     => $"{localNow.Day.ToString(inv)}.{localNow.ToString("MMMM", inv)}.{localNow.Year.ToString(inv)}",
                "system.local_day"      => localNow.Day.ToString(inv),
                "system.local_month"    => localNow.Month.ToString(inv),
                "system.local_monthname"=> localNow.ToString("MMMM", inv),
                "system.local_dayname"  => localNow.ToString("dddd", inv),
                "system.local_year"     => localNow.Year.ToString(inv),

                _                       => null,
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // {stream.*} family.
        //
        // Anchor: the moment the ScriptEngine module first loads
        // in the Hub process. That's "Hub uptime" — the simplest of the
        // three candidate signals (Hub uptime / Streamer.bot connect /
        // first chat). Hub uptime matches the existing {system.*}
        // contract: a single process-wide reference instant set at
        // startup, no external trigger needed.
        //
        // Swap mechanic: <see cref="SetStreamStartedAtUtc"/> lets the Hub
        // (or a future config-driven init path) re-anchor at runtime.
        // Existing scripts keep working because the var name doesn't
        // change — only the underlying timestamp does.
        // ─────────────────────────────────────────────────────────────────
        // Stored as ticks so Volatile.Read/Write give us atomic publishes
        // without boxing. DateTime is a struct; Interlocked.Exchange<T>
        // requires T : class, so we serialize via the underlying long.
        private static long _streamStartedAtUtcTicks = DateTime.UtcNow.Ticks;

        /// <summary>
        /// Re-anchors the {stream.*} var family. Call from Hub startup if
        /// the streamer wants Streamer.bot connect / first-chat / a custom
        /// signal as the "stream start" reference instead of Hub uptime.
        /// Thread-safe via Interlocked on the ticks field.
        /// </summary>
        public static void SetStreamStartedAtUtc(DateTime utc)
        {
            DateTime u = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
            System.Threading.Interlocked.Exchange(ref _streamStartedAtUtcTicks, u.Ticks);
        }

        /// <summary>Current "stream start" anchor. Defaults to Hub-uptime.</summary>
        public static DateTime GetStreamStartedAtUtc()
            => new DateTime(System.Threading.Interlocked.Read(ref _streamStartedAtUtcTicks), DateTimeKind.Utc);

        private static string? ResolveStreamVar(string key, DateTime utcNow)
        {
            var inv = CultureInfo.InvariantCulture;
            DateTime startedAt = GetStreamStartedAtUtc();
            var elapsed = utcNow - startedAt;
            if (elapsed.Ticks < 0) elapsed = TimeSpan.Zero; // re-anchor in the future is treated as "0"
            return key.ToLowerInvariant() switch
            {
                "stream.uptime"           => FormatStreamUptime(elapsed),
                "stream.uptime_seconds"   => ((long)elapsed.TotalSeconds).ToString(inv),
                "stream.uptime_minutes"   => ((long)elapsed.TotalMinutes).ToString(inv),
                "stream.uptime_hours"     => ((long)elapsed.TotalHours).ToString(inv),
                "stream.uptime_formatted" => FormatStreamUptime(elapsed),
                "stream.started_at"       => startedAt.ToString("O", inv),
                _                         => null,
            };
        }

        // "1h 23m" / "45m" / "12s" depending on magnitude — matches the
        // streamer's mental model. Sub-minute reads as seconds; sub-hour
        // reads as minutes; longer reads as "Xh Ym".
        private static string FormatStreamUptime(TimeSpan elapsed)
        {
            var inv = CultureInfo.InvariantCulture;
            if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s";
            if (elapsed.TotalHours   < 1) return $"{(int)elapsed.TotalMinutes}m";
            int hours   = (int)elapsed.TotalHours;
            int minutes = elapsed.Minutes;
            return $"{hours.ToString(inv)}h {minutes.ToString(inv)}m";
        }

        private static bool IsPositionalPlaceholder(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            for (int i = 0; i < key.Length; i++)
                if (key[i] < '0' || key[i] > '9') return false;
            return true;
        }

        private async Task<string> ResolveVar(string key, Dictionary<string, string> vars)
        {
            if (vars.TryGetValue(key, out var local)) return local;
            if (key.StartsWith("user.") || key.StartsWith("global."))
                return await _db.GetVariableAsync(key, "0");
            return key;
        }
    }
}
