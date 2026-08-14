// Utilities band carved from ScriptEngine.cs.
// Owns: static parsing/recognition helpers —
//   IsBlockHeader, ShouldEnterBlock,
//   GetIndent, StripInlineComment, FindBlockEnd, ExtractArgs,
//   SplitArgs (two-pass, internal), ParseTruthy, SplitListWithEscape.

using System;
using System.Collections.Generic;
using System.Text;

namespace Phoenix.Controls.Shared.Core
{
    public partial class ScriptEngine
    {
        // ─────────────────────────────────────────────────────────────────
        // BLOCK HEADER RECOGNITION
        // ─────────────────────────────────────────────────────────────────

        // Centralized block-header recognizer. Iterates the static
        // BlockHeaderPrefixes list rather than open-coding the StartsWith
        // chain, so adding a new event family means appending one entry
        // to BlockHeaderPrefixes (and not touching this method). The "if "
        // case is special-cased because it requires Length > 4 (rules out
        // a bare "if :" that could otherwise sneak past).
        // Marked internal to allow targeted unit testing — see
        // BugFixSweep3_EnginePerf_Tests.R12_IsBlockHeader_*.
        internal static bool IsBlockHeader(string line)
        {
            if (string.IsNullOrEmpty(line) || !line.EndsWith(":")) return false;

            // Special-case "if " — must have at least one char of condition
            // body after the keyword, so a bare "if :" doesn't enter a block.
            if (line.StartsWith("if ") && line.Length > 4) return true;

            for (int i = 0; i < BlockHeaderPrefixes.Length; i++)
            {
                if (line.StartsWith(BlockHeaderPrefixes[i])) return true;
            }
            return false;
        }

        private bool ShouldEnterBlock(string line, Dictionary<string, string> vars)
        {
            if (line.StartsWith("on_event("))
            {
                // format: on_event(name): — prefix is 9 chars, suffix is "):" which is 2 chars
                int innerLen = line.Length - 11;
                string expected = innerLen > 0 ? line.Substring(9, innerLen) : string.Empty;
                if (string.IsNullOrEmpty(EventType) || EventType.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
                // A resub IS a subscription. Streamers author a single on_event(Twitch.Sub)
                // alert meant to cover renewals too (its month-milestone messaging only makes
                // sense for resubs), so a Twitch.Resub event also enters on_event(Twitch.Sub)
                // blocks. A dedicated on_event(Twitch.Resub) block still matches above. Pairs
                // with the script-selection alias in ScriptManager.ExecuteGenericEventAsync —
                // that picks the script, this enters the block inside it.
                return EventType.Equals("Twitch.Resub", StringComparison.OrdinalIgnoreCase)
                    && expected.Equals("Twitch.Sub", StringComparison.OrdinalIgnoreCase);
            }
            if (line.StartsWith("on_bus("))
            {
                // format: on_bus(name): — prefix is 7 chars, suffix is "):" which is 2 chars
                int innerLen = line.Length - 9;
                string expected = innerLen > 0 ? line.Substring(7, innerLen) : string.Empty;
                return string.IsNullOrEmpty(BusEventType) || BusEventType.Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            if (line.StartsWith("on_state_change("))
            {
                // format: on_state_change(name): — prefix is 16 chars, suffix is "):" (2 chars)
                int innerLen = line.Length - 18;
                string expected = innerLen > 0 ? line.Substring(16, innerLen).Trim().Trim('"') : string.Empty;
                if (string.IsNullOrEmpty(EventType)) return true;
                // EventType is "StateChange.<name>"; strip prefix to compare.
                string actual = EventType.StartsWith("StateChange.", StringComparison.OrdinalIgnoreCase)
                    ? EventType.Substring("StateChange.".Length)
                    : EventType;
                return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            // on_webhook fires only on a webhook dispatch. ExecuteOnWebhookScriptsAsync
            // selects scripts by WebhookNames, so a non-webhook run never reaches a
            // webhook-only file; left ungated. (A file mixing on_chat + on_webhook would
            // still cross-fire on chat — a pre-existing sibling of the schedule bug below,
            // out of scope for this fix.)
            if (line.StartsWith("on_webhook("))
                return true;
            // Schedule family — only enter on an actual scheduler fire. The scheduler
            // dispatch (ScriptManager.ExecuteEventScriptAsync, its sole caller) tags the
            // run with EventType="Schedule"; a chat/event run carries a different
            // EventType and MUST NOT run these blocks. Before this gate the family
            // returned true unconditionally, so a file containing BOTH an on_chat: block
            // and an on_interval(...) / on_schedule(...) block ran the schedule body on
            // EVERY chat message (the "15-minute reminder fires per chat" bug). Empty
            // EventType stays permissive — matching on_chat / on_event — for direct or
            // test invocations that don't set a discriminator. (A single file with
            // multiple schedule blocks still fires them all on any schedule tick — the
            // schedule-vs-schedule case noted in SchedulerService is unchanged here.)
            if (line.StartsWith("on_schedule(")      ||
                line.StartsWith("on_schedule_once(") ||
                line.StartsWith("on_interval("))
                return string.IsNullOrEmpty(EventType)
                    || EventType.Equals("Schedule", StringComparison.OrdinalIgnoreCase);
            // on_websocket("name"): the executor only enters the
            // block when the engine's EventType matches "WebSocket.<name>".
            // ScriptManager.ExecuteOnWebSocketScriptsAsync sets EventType
            // before TimedExecuteAsync, mirroring the on_webhook plumbing.
            if (line.StartsWith("on_websocket("))
            {
                // format: on_websocket(name): — prefix is 13 chars, suffix is "):" (2 chars)
                int innerLen = line.Length - 15;
                string expected = innerLen > 0 ? line.Substring(13, innerLen).Trim().Trim('"') : string.Empty;
                if (string.IsNullOrEmpty(EventType)) return true;
                string actual = EventType.StartsWith("WebSocket.", StringComparison.OrdinalIgnoreCase)
                    ? EventType.Substring("WebSocket.".Length)
                    : EventType;
                return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            // on_hotkey("Ctrl+Shift+P"): EventType is "Hotkey.<combo>".
            // HotkeyService normalises the combo string before dispatch, so
            // an exact-string equality against the declaration is sufficient.
            if (line.StartsWith("on_hotkey("))
            {
                // format: on_hotkey(combo): — prefix is 10 chars, suffix is "):" (2 chars)
                int innerLen = line.Length - 12;
                string expected = innerLen > 0 ? line.Substring(10, innerLen).Trim().Trim('"') : string.Empty;
                if (string.IsNullOrEmpty(EventType)) return true;
                string actual = EventType.StartsWith("Hotkey.", StringComparison.OrdinalIgnoreCase)
                    ? EventType.Substring("Hotkey.".Length)
                    : EventType;
                return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            // on_clipboard: fires on every clipboard change. No
            // discriminator (the OS doesn't differentiate by handler), so the
            // header check is "EventType starts with Clipboard." or empty.
            // ClipboardService sets EventType="Clipboard.Update" before dispatch.
            if (line.StartsWith("on_clipboard"))
                return string.IsNullOrEmpty(EventType)
                    || EventType.StartsWith("Clipboard.", StringComparison.OrdinalIgnoreCase);
            // on_chat — chat is multi-platform (Twitch / YouTube / Kick), but a BARE
            // `on_chat:` keeps its historical meaning of Twitch chat only, so every
            // graph and hand-authored script written before the multi-platform chat
            // pipeline behaves exactly as it always did. A platform list widens it:
            // `on_chat(twitch, kick):` enters for the listed platforms' chat events.
            // Empty EventType stays permissive (direct/test invocations).
            if (line.StartsWith("on_chat"))
            {
                if (string.IsNullOrEmpty(EventType)) return true;
                string? chatPlatform = ChatPlatforms.FromEventType(EventType);
                if (chatPlatform is null) return false;

                int open = line.IndexOf('(');
                if (open < 0)
                    return chatPlatform == ChatPlatforms.Twitch;
                int close = line.LastIndexOf(')');
                string list = close > open ? line.Substring(open + 1, close - open - 1) : string.Empty;
                if (string.IsNullOrWhiteSpace(list))
                    return chatPlatform == ChatPlatforms.Twitch;

                foreach (var token in list.Split(','))
                {
                    if (token.Trim().Trim('"').Equals(chatPlatform, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            // on_go_live(...) / on_session_end(...) — unified stream-lifecycle
            // triggers. Enter when the firing event's platform is in the header's
            // list AND the event kind matches the header (a going-live event never
            // enters a session-end block, and vice versa). Empty EventType stays
            // permissive (direct/test invocations). The per-instance 10s debounce
            // is enforced separately in ExecuteBlock — this method only decides
            // platform/kind eligibility. StreamLifecycle owns the event↔platform↔kind map.
            if (line.StartsWith(StreamLifecycle.GoLiveHeader) || line.StartsWith(StreamLifecycle.SessionEndHeader))
            {
                if (string.IsNullOrEmpty(EventType)) return true;
                var kind = StreamLifecycle.KindForEvent(EventType);
                if (kind == StreamLifecycle.Kind.None) return false;
                bool wantGoLive = line.StartsWith(StreamLifecycle.GoLiveHeader);
                if ((kind == StreamLifecycle.Kind.GoingLive) != wantGoLive) return false;

                string? platform = StreamLifecycle.PlatformForEvent(EventType);
                if (platform is null) return false;

                int open = line.IndexOf('(');
                if (open < 0) return false; // always emitted with an explicit list
                int close = line.LastIndexOf(')');
                string list = close > open ? line.Substring(open + 1, close - open - 1) : string.Empty;
                foreach (var token in list.Split(','))
                    if (token.Trim().Equals(platform, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            if (line.StartsWith("on_startup"))
                return string.IsNullOrEmpty(EventType) || EventType.Equals("Startup", StringComparison.OrdinalIgnoreCase);
            // Live processes — on_process_start / on_process_stop run a process
            // template's lifecycle blocks. The instance manager sets EventType to
            // "ProcessStart" / "ProcessStop" when running them, so they never enter
            // during a normal event fan-out (EventType="Twitch.ChatMessage" etc.),
            // and the template's other blocks (on_chat / on_interval / …) never
            // enter during the start/stop run. Checked BEFORE the bare "on " /
            // permissive fall-through. (on_process_stop must precede on_process_start
            // is not required — the StartsWith targets are disjoint.)
            if (line.StartsWith("on_process_start"))
                return string.IsNullOrEmpty(EventType) || EventType.Equals("ProcessStart", StringComparison.OrdinalIgnoreCase);
            if (line.StartsWith("on_process_stop"))
                return string.IsNullOrEmpty(EventType) || EventType.Equals("ProcessStop", StringComparison.OrdinalIgnoreCase);
            if (line.StartsWith("on "))
                return true;

            if ((line.StartsWith("if ") || line.StartsWith("elif ")) && line.EndsWith(":"))
                return EvaluateCondition(line, vars);

            return true;
        }

        private static int GetIndent(string line)
        {
            int sp = 0;
            foreach (char c in line) { if (c == ' ') sp++; else if (c == '\t') sp += 4; else break; }
            return sp / 4;
        }

        private static string StripInlineComment(string line)
        {
            // Quote-aware scan so a hashtag inside a string literal
            // (very common in send_chat("hello #channel")) is not mistaken for
            // an inline comment marker. Mirrors the inQuote tracking used by
            // SplitArgs. Backslash-escape on quotes is intentionally NOT
            // honored — the rest of the engine treats `"` as an unescaped
            // quote toggle (see SplitArgs, FindOperatorIndexQuoteAware).
            // Single-quote literals are not part of the script surface so we
            // only track double-quote state.
            int idx = IndexOfOutsideQuotes(line, " #");
            return idx > 0 ? line.Substring(0, idx).TrimEnd() : line;
        }

        /// <summary>
        /// Returns the first index of <paramref name="needle"/>
        /// in <paramref name="haystack"/> that lies outside any double-quoted
        /// segment, or -1 if no such occurrence exists. Single-character match
        /// returns the position of the character; multi-character match returns
        /// the start position of the match. Quote state toggles only on raw
        /// '"' characters — escape sequences are not recognized, matching the
        /// rest of the engine's parsing surface (SplitArgs, etc.).
        /// </summary>
        internal static int IndexOfOutsideQuotes(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return -1;
            if (needle.Length > haystack.Length) return -1;
            bool inQuote = false;
            int  limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                char c = haystack[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }
                if (inQuote) continue;
                if (MatchesAt(haystack, i, needle)) return i;
            }
            return -1;
        }

        private static bool MatchesAt(string haystack, int idx, string needle)
        {
            for (int j = 0; j < needle.Length; j++)
                if (haystack[idx + j] != needle[j]) return false;
            return true;
        }

        /// <summary>
        /// Quote-aware split: walks <paramref name="haystack"/> and
        /// breaks it on every occurrence of <paramref name="separator"/> that
        /// lies outside a double-quoted segment. Returns the original string
        /// as a single-element array when the separator is absent or only
        /// appears inside quotes.
        /// </summary>
        internal static string[] SplitOutsideQuotes(string haystack, string separator)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(separator))
                return new[] { haystack ?? string.Empty };
            var parts = new System.Collections.Generic.List<string>();
            int    start  = 0;
            bool   inQuote = false;
            int    limit = haystack.Length - separator.Length;
            for (int i = 0; i <= limit; i++)
            {
                char c = haystack[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }
                if (inQuote) continue;
                if (MatchesAt(haystack, i, separator))
                {
                    parts.Add(haystack.Substring(start, i - start));
                    i += separator.Length - 1;
                    start = i + 1;
                }
            }
            parts.Add(haystack.Substring(start));
            return parts.ToArray();
        }

        private static int FindBlockEnd(string[] lines, int headerIdx, int headerIndent)
        {
            int i = headerIdx + 1;
            while (i < lines.Length)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
                if (GetIndent(lines[i]) <= headerIndent) return i - 1;
                i++;
            }
            return lines.Length - 1;
        }

        private static string[] ExtractArgs(string line, string funcName)
        {
            int start = funcName.Length + 1;
            int end = line.LastIndexOf(')');
            if (end <= start) return Array.Empty<string>();
            return SplitArgs(line.Substring(start, end - start));
        }

        // Two-pass split to drop the per-call List<string> allocation.
        // Pass 1 walks the string counting paren-/quote-respecting splits to
        // size the result array exactly. Pass 2 fills it. This is the same
        // O(n) work as before (no extra allocations beyond the final array
        // and its trimmed substrings) but no transient list growth/copy.
        // Behavior is preserved bit-for-bit with the legacy List<string>
        // implementation, including the trailing-comma quirk: "a,"  → ["a"],
        // ","  → [""], "a,b," → ["a","b"]. The legacy code only added a tail
        // entry when start < length, so a trailing comma drops the empty
        // tail rather than producing a phantom slot.
        // Marked internal so BugFixSweep3_EnginePerf_Tests can exercise the
        // edge cases that pin pre-/post-change behavioral equivalence.
        internal static string[] SplitArgs(string argsStr)
        {
            // Edge case: null/empty input — preserve legacy single-element
            // fallback (return [""] for "", consistent with the old code's
            // result.Count == 0 branch which fell through to argsStr.Trim()).
            if (string.IsNullOrEmpty(argsStr)) return new[] { string.Empty };

            // Pass 1: count splits.
            int splits = 0;
            {
                int depth = 0; bool inQuote = false;
                for (int i = 0; i < argsStr.Length; i++)
                {
                    char c = argsStr[i];
                    if (c == '"') inQuote = !inQuote;
                    else if (!inQuote && c == '(') depth++;
                    else if (!inQuote && c == ')') depth--;
                    else if (!inQuote && depth == 0 && c == ',') splits++;
                }
            }

            // Determine whether the legacy code would have appended a tail
            // entry. It only did so when `start < argsStr.Length` AFTER the
            // last comma, i.e. there's at least one char past the final
            // comma. If the string ends with ',' the legacy code dropped
            // the tail; we replicate that to keep behavior bit-identical.
            bool hasTail = !argsStr.EndsWith(",");
            int  total   = splits == 0 ? 1 : (splits + (hasTail ? 1 : 0));

            // Pass 2: fill exact-sized result.
            var result = new string[total];
            int slot = 0;
            int start = 0;
            {
                int depth = 0; bool inQuote = false;
                for (int i = 0; i < argsStr.Length; i++)
                {
                    char c = argsStr[i];
                    if (c == '"') inQuote = !inQuote;
                    else if (!inQuote && c == '(') depth++;
                    else if (!inQuote && c == ')') depth--;
                    else if (!inQuote && depth == 0 && c == ',')
                    {
                        result[slot++] = argsStr.Substring(start, i - start).Trim();
                        start = i + 1;
                    }
                }
            }
            if (slot < total) result[slot] = argsStr.Substring(start).Trim();
            return result;
        }

        /// <summary>
        /// Single source of truth for "is this string truthy?" logic. Used by
        /// the engine's inline convert.to_bool wrapper and by ScriptManager's
        /// runtime convert.to_bool command. Accepts the canonical truthy set
        /// case-insensitively: "true" / "1" / "yes" / "on" / "y" / "t".
        /// </summary>
        public static bool ParseTruthy(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            string s = input.Trim();
            return s.Equals("true", StringComparison.OrdinalIgnoreCase)
                || s.Equals("1", StringComparison.OrdinalIgnoreCase)
                || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || s.Equals("on", StringComparison.OrdinalIgnoreCase)
                || s.Equals("y", StringComparison.OrdinalIgnoreCase)
                || s.Equals("t", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Splits a comma-delimited list with `\,` escaping for literal commas.
        /// Used by for_each so values containing commas (chat messages, JSON-ish payloads)
        /// don't corrupt iteration. The escape sequence is decoded in the returned items.
        /// Empty / whitespace-only entries are dropped, matching prior behavior.
        /// </summary>
        public static string[] SplitListWithEscape(string listVal)
        {
            if (string.IsNullOrEmpty(listVal)) return Array.Empty<string>();
            var result = new System.Collections.Generic.List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < listVal.Length; i++)
            {
                char c = listVal[i];
                if (c == '\\' && i + 1 < listVal.Length && listVal[i + 1] == ',')
                {
                    sb.Append(',');
                    i++;
                    continue;
                }
                if (c == ',')
                {
                    string entry = sb.ToString().Trim();
                    sb.Clear();
                    if (!string.IsNullOrWhiteSpace(entry)) result.Add(entry);
                    continue;
                }
                sb.Append(c);
            }
            string tail = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(tail)) result.Add(tail);
            return result.ToArray();
        }
    }
}
