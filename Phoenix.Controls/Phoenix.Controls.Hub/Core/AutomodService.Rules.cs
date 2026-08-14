using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.Core
{
    // Pure detectors for the automod rules — no instance state, no side effects, so
    // each is trivially unit-testable in isolation. The stateful detectors (rate/flood
    // sliding window) and the user-regex matcher (compiled cache + log-once + P0
    // matchTimeout) live on AutomodService itself. Every threshold arrives from the
    // rule config; the caller has already gated on the rule's own Enabled flag.
    internal static class AutomodRules
    {
        // URL / bare-domain extraction. These patterns are TOOL-authored (not user
        // input) but still carry a match timeout as defense-in-depth against a
        // pathological message body. Explicit scheme first, then bare domains.
        private static readonly Regex UrlRegex = new(
            @"https?://\S+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        private static readonly Regex BareDomainRegex = new(
            @"\b(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        // A bare "word.word" token ending in one of these is almost always a filename /
        // abbreviation, not a link — excluded from BARE-domain extraction so no rule ever
        // reads "readme.md" / "photo.png" / "cool.py" as a host. UrlRegex requires an
        // explicit scheme, so a scheme'd link never reaches this cut and is always caught.
        // This is the ONLY extraction-time filter: the TLD allow-list below gates the
        // BlockAll heuristic alone, so a filename shape that is also a real gTLD (".zip",
        // ".mov") has to be stopped here or it would walk straight into that heuristic.
        private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "md","txt","csv","json","xml","html","htm","css","js","ts","cs","cpp","c","h","hpp",
            "java","rb","go","rs","php","py","sh","bat","ps1","yml","yaml","lua","sql","ini","cfg","log",
            "png","jpg","jpeg","gif","webp","bmp","svg","ico","psd","pdf","doc","docx","xls","xlsx","ppt","pptx",
            "exe","dll","zip","rar","gz","tar","7z","mp3","wav","ogg","mp4","mov","avi","mkv","webm","db",
        };

        // Trailing-label allow-list for the BlockAll HEURISTIC on bare domains. Without it
        // BareDomainRegex reads any "word.word" with a 2+-letter tail as a link, so
        // ordinary chat slang ("gg.ez", "np.bro", "ok.ok") is moderated — and with
        // Links.BlockAll defaulting ON that escalates real chatters toward a ban.
        //
        // ★ It is applied in LinksFired, NOT during extraction, and ONLY on the BlockAll
        // rung. The streamer's explicit allow-list and block-list must see EVERY extracted
        // host: a curated TLD list may never veto a domain the streamer named themselves,
        // or blocking "scam.tk" would silently stop protecting them — the worst failure an
        // allow-list can cause. That narrow blast radius is what lets this list stay
        // curated rather than exhaustive: an unlisted TLD costs at most a missed BARE
        // domain on the "any link at all" heuristic, and even there an explicit scheme or
        // a "www." prefix bypasses the gate outright.
        // Real TLDs that double as ordinary English words a chatter can type before a
        // missing space (.no "oh.no", .so "or.so", .my "oh.my", .is, .am, .at, .by, .im)
        // are LEFT OUT on the same reasoning; .be stays because youtu.be is everywhere.
        private static readonly HashSet<string> KnownTlds = new(StringComparer.OrdinalIgnoreCase)
        {
            // Legacy gTLDs
            "com","net","org","info","biz","edu","gov","mil","int","name","pro","mobi","asia",
            // New gTLDs a stream link actually lands on
            "app","dev","live","stream","video","tube","chat","fan","fans","art","blog","cloud",
            "club","digital","download","email","games","gift","host","link","media","news","one",
            "online","page","party","pics","shop","site","social","space","store","studio","team",
            "tech","today","tools","top","vip","watch","website","wiki","work","world","xyz","zone",
            "click","fun","icu","life","lol","moe","ninja","quest","rocks","run","win",
            // ccTLDs — the shortener / vanity favourites first (discord.gg, bit.ly, is.gd,
            // t.co, youtu.be, goo.gl, tiny.cc, rb.gy)
            "gg","tv","io","me","co","ly","to","cc","gd","gl","gy","be","fm","ai","eu",
            "us","uk","ca","au","nz","de","fr","es","nl","ch","se","dk","fi","ie","pt","pl",
            "cz","sk","hu","ro","bg","gr","tr","ru","ua","lt","lv","ee","jp","cn","kr","tw",
            "hk","sg","th","vn","ph","id","kz","in","it","br","mx","ar","cl","pe","za","il","ae","sa",
        };

        private static bool EndsWithFileExtension(string host)
        {
            string tail = TailLabel(host);
            return tail.Length > 0 && FileExtensions.Contains(tail);
        }

        private static bool HasKnownTld(string host)
        {
            string tail = TailLabel(host);
            return tail.Length > 0 && KnownTlds.Contains(tail);
        }

        // The label after the LAST dot, or "" when there is no usable tail.
        private static string TailLabel(string host)
        {
            int dot = host.LastIndexOf('.');
            if (dot < 0 || dot == host.Length - 1) return "";
            return host.Substring(dot + 1);
        }

        // ── Caps ────────────────────────────────────────────────────────────
        public static bool CapsFired(string text, AutomodCapsRule rule, out string reason)
        {
            reason = "";
            int letters = 0, upper = 0;
            foreach (char c in text)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (char.IsUpper(c)) upper++;
            }
            // Gate on the LETTER count (the "at least MinLength letters" contract) — NOT
            // text.Length — so a symbol-heavy short message like ">>>>>>>>>A" (1 letter)
            // can't read as 100% caps and trip a false timeout.
            if (letters < Math.Max(1, rule.MinLength)) return false;
            int pct = upper * 100 / letters;
            if (pct >= Math.Max(1, rule.Percent))
            {
                reason = $"{pct}% caps";
                return true;
            }
            return false;
        }

        // ── Max length ──────────────────────────────────────────────────────
        public static bool LengthFired(string text, AutomodLengthRule rule, out string reason)
        {
            reason = "";
            int max = Math.Max(1, rule.MaxLength);
            if (text.Length > max)
            {
                reason = $"{text.Length} chars (max {max})";
                return true;
            }
            return false;
        }

        // ── Repeated-character run ───────────────────────────────────────────
        public static bool RepeatFired(string text, AutomodRepeatRule rule, out string reason)
        {
            reason = "";
            int need = Math.Max(2, rule.MaxRun);
            int run = 0, best = 0;
            char prev = '\0';
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) { run = 0; prev = '\0'; continue; }
                if (c == prev) run++;
                else { run = 1; prev = c; }
                if (run > best) best = run;
            }
            if (best >= need)
            {
                reason = $"{best} of the same character in a row";
                return true;
            }
            return false;
        }

        // ── Symbol / emote ratio ─────────────────────────────────────────────
        public static bool SymbolsFired(string text, AutomodSymbolRule rule, out string reason)
        {
            reason = "";
            int total = 0, symbols = 0;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                total++;
                if (!char.IsLetterOrDigit(c)) symbols++;
            }
            if (total < Math.Max(1, rule.MinLength)) return false;   // min-length gate
            int pct = symbols * 100 / total;
            if (pct >= Math.Max(1, rule.Percent))
            {
                reason = $"{pct}% symbols/emotes";
                return true;
            }
            return false;
        }

        // ── Links (allow/block lists + permit grace) ─────────────────────────
        /// <summary>
        /// <paramref name="exemptHost"/> is a cross-tool waiver — today only the Song
        /// Request tool's own request line supplies one (SongRequestService.
        /// ExemptRequestLinkHost). It is deliberately the narrowest shape available: ONE
        /// host, waiving ONLY the BlockAll heuristic rung, and only for that host. It can
        /// never reach the block-list check above it, so a streamer's explicit UrlBlockList
        /// still fires on an exempt host, and every other host in the same message is judged
        /// untouched. Defaults to "" — no waiver — so a caller that says nothing gets the
        /// strict behaviour, which is the right direction to fail in a moderation tool.
        /// </summary>
        public static bool LinksFired(string text, AutomodLinksRule rule,
            IReadOnlyList<string> allowList, IReadOnlyList<string> blockList, bool permitted, out string reason,
            string exemptHost = "")
        {
            reason = "";
            var hosts = ExtractHosts(text);
            if (hosts.Count == 0) return false;
            if (permitted) return false;                    // !permit grace → links pass

            // Both sides lower-case and strip a trailing dot, so an ordinal compare is exact.
            string exempt = (exemptHost ?? "").Trim().TrimEnd('.').ToLowerInvariant();

            foreach (var (host, explicitLink) in hosts)
            {
                if (MatchesDomain(host, allowList)) continue;      // allow-list bypass
                if (MatchesDomain(host, blockList))
                {
                    // Checked BEFORE the waiver on purpose: a block-listed host is the
                    // streamer's own word and outranks any tool's convenience.
                    reason = $"blocked link ({host})";
                    return true;
                }
                // BlockAll is the HEURISTIC rung ("any link at all"), so it — and only it —
                // is gated on the TLD allow-list. The two lists above are the streamer's own
                // words and are never filtered. A scheme'd or "www."-prefixed host states
                // link intent outright, so it skips the gate.
                if (rule.BlockAll && (explicitLink || HasKnownTld(host)))
                {
                    if (exempt.Length > 0 && string.Equals(host, exempt, StringComparison.Ordinal))
                        continue;                                  // the exempt host, heuristic rung only
                    reason = $"posted a link ({host})";
                    return true;
                }
            }
            return false;
        }

        // Extract lowercased host names from any http(s) URL or bare domain in the text.
        // EVERY host is returned — filtering is the caller's rule decision, never a silent
        // prune here, so the streamer's allow/block lists always get the full set. Each
        // entry carries whether the message stated link intent OUTRIGHT (an explicit
        // "http(s)://" scheme or a "www." prefix — neither is typed by accident), which is
        // what lets the BlockAll heuristic waive its TLD gate.
        internal static List<(string Host, bool Explicit)> ExtractHosts(string text)
        {
            var hosts = new List<(string Host, bool Explicit)>();
            if (string.IsNullOrEmpty(text)) return hosts;
            var seen = new HashSet<string>(StringComparer.Ordinal);   // both paths emit lowercase
            try
            {
                foreach (Match m in UrlRegex.Matches(text))
                {
                    string h = HostOf(m.Value);
                    if (h.Length > 0 && seen.Add(h)) hosts.Add((h, true));
                }
            }
            catch (RegexMatchTimeoutException) { /* treat as no links */ }

            try
            {
                foreach (Match m in BareDomainRegex.Matches(text))
                {
                    string h = m.Value.ToLowerInvariant().TrimEnd('.');
                    // Skip bare filename-shaped tokens (readme.md, photo.png) so they never
                    // read as a host; scheme'd links above are unaffected. A bare host the
                    // scheme pass already emitted keeps its explicit flag (first write wins).
                    if (h.Length == 0 || EndsWithFileExtension(h)) continue;
                    if (seen.Add(h)) hosts.Add((h, h.StartsWith("www.", StringComparison.Ordinal)));
                }
            }
            catch (RegexMatchTimeoutException) { /* treat as no links */ }
            return hosts;
        }

        // Reduce a full URL to its host (strip scheme, credentials, port, path).
        private static string HostOf(string url)
        {
            string s = url ?? "";
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            int slash = s.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) s = s.Substring(0, slash);
            int at = s.IndexOf('@');
            if (at >= 0) s = s.Substring(at + 1);
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s.Substring(0, colon);
            return s.Trim().TrimEnd('.').ToLowerInvariant();
        }

        // A host matches a list entry when it equals it or is a subdomain of it
        // (host endsWith "." + entry). Entries are normalized to a bare host first.
        private static bool MatchesDomain(string host, IReadOnlyList<string> list)
        {
            if (list == null || list.Count == 0) return false;
            for (int i = 0; i < list.Count; i++)
            {
                string entry = NormalizeDomainEntry(list[i]);
                if (entry.Length == 0) continue;
                if (string.Equals(host, entry, StringComparison.OrdinalIgnoreCase)) return true;
                if (host.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string NormalizeDomainEntry(string raw)
        {
            string s = (raw ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return "";
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            int slash = s.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) s = s.Substring(0, slash);
            return s.Trim().TrimStart('.').TrimEnd('.');
        }

        // ── Blocklist words / phrases ────────────────────────────────────────
        public static bool WordsFired(string text, IReadOnlyList<string> words, out string reason)
        {
            reason = "";
            if (words == null || words.Count == 0) return false;
            string lower = (text ?? "").ToLowerInvariant();
            if (lower.Length == 0) return false;
            for (int i = 0; i < words.Count; i++)
            {
                string t = (words[i] ?? "").Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                bool phrase = t.IndexOf(' ') >= 0;
                if (phrase)
                {
                    if (lower.Contains(t)) { reason = "blocked phrase"; return true; }
                }
                else if (ContainsWholeWord(lower, t))
                {
                    reason = "blocked word";
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsWholeWord(string haystack, string needle)
        {
            int idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = idx == 0 || !IsWordChar(haystack[idx - 1]);
                int end = idx + needle.Length;
                bool rightOk = end >= haystack.Length || !IsWordChar(haystack[end]);
                if (leftOk && rightOk) return true;
                idx = end;
            }
            return false;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
