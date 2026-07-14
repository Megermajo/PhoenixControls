using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: text.* command registrations.
    // Lifts the 9 string-manipulation handlers (contains / to_upper /
    // join_list / format / replace / split / length / to_lower /
    // parse_command) out of RegisterHubCommands. They lived across three
    // non-contiguous source clusters; collapsing them into one
    // RegisterTextCommands() call is safe because RegisterCommand stores
    // by name — registration order has no runtime effect.
    //
    // Behaviour-preserving carry-overs:
    //   * H4 — text.format replaces BOTH indexed ({0}, {1}, ...) and named
    //     ({A}, {B}, ...) placeholder forms so exporters and hand-authored
    //     scripts both work without translation at the seam.
    //   * H5 — text.replace returns the source unchanged when Find is empty
    //     (string.Replace("") throws ArgumentException; the empty arg comes
    //     from chat / vars and shouldn't blow up the script).
    //   * H6 — text.to_upper / text.to_lower use InvariantCulture so locale
    //     edge cases (Turkish-i, etc.) stay deterministic.
    //   * L2 — text.length counts grapheme clusters via StringInfo so emoji
    //     and combining marks count as 1 (string.Length reports a surrogate
    //     pair like "🎉" as 2, which is surprising for chat/overlay use).
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterTextCommands()
        {
            // text.contains(source, search) — returns "true"/"false"
            _engine.RegisterCommand("text.contains", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string source = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string search = bound?.GetOrDefault<string>("Search", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                // Always return a proper "true"/"false" — never null. source is never
                // null here (ArgOrEmpty / GetOrDefault both fall back to ""), and an
                // empty source containing an empty search reports true (string.Contains("")
                // is true), while empty-source / non-empty-search reports false. Returning
                // null when both were empty (the old guard) left downstream Bool consumers
                // with a missing value instead of a usable flag.
                return source.Contains(search, StringComparison.OrdinalIgnoreCase).ToString().ToLower();
            });

            // text.to_upper(value) — returns uppercased string
            _engine.RegisterCommand("text.to_upper", async (args) => {
                // H6 — InvariantCulture so behavior is deterministic across locales
                // (Turkish-i hazard etc.).
                string source = _engine.CurrentBoundArgs?.GetOrDefault<string>("Source", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                return source.ToUpperInvariant();
            });

            // text.join_list(list, separator) — returns joined string
            _engine.RegisterCommand("text.join_list", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list      = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string separator = bound?.GetOrDefault<string>("Separator", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                return string.Join(separator, list.Split(','));
            });

            // ── Text ops — return result ─────────────────────────────────
            _engine.RegisterCommand("text.format", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string template = bound?.GetOrDefault<string>("Template", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(template) && args.Length == 0) return null;

                // Variadic Args is a typed list of placeholder values.
                // Bound path pulls the IReadOnlyList<string>; legacy fallback skips
                // args[0] (Template) and treats the rest as the placeholder list.
                IReadOnlyList<string> rest = bound?.GetOrDefault<IReadOnlyList<string>>("Args", Array.Empty<string>())
                    ?? (args.Length > 1
                        ? new ArraySegment<string>(args, 1, args.Length - 1).ToArray()
                        : (IReadOnlyList<string>)Array.Empty<string>());

                // H4 — node template advertises {A} / {B} / {C} ... placeholders, but
                // the previous runtime only replaced indexed forms ({0}, {1}, ...).
                // Replace BOTH the indexed ({0}...) and named ({A}/{B}/...) forms so
                // exporters and hand-authored scripts both work without a translation
                // step at the seam. ASCII A..Z covers up to 26 args which is well past
                // anything the manifesto contemplates.
                string result = template;
                for (int i = 0; i < rest.Count; i++)
                {
                    string val = rest[i] ?? string.Empty;
                    result = result.Replace($"{{{i}}}", val);
                    if (i < 26)
                    {
                        char letter = (char)('A' + i);
                        result = result.Replace("{" + letter + "}", val);
                    }
                }
                return result;
            });
            _engine.RegisterCommand("text.replace", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string source      = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string find        = bound?.GetOrDefault<string>("Find", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string replaceWith = bound?.GetOrDefault<string>("ReplaceWith", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                // H5 — string.Replace("") throws ArgumentException. Empty Find is
                // user-supplied (chat input, vars, etc.) — fail soft and return the
                // original rather than blowing up the script.
                if (string.IsNullOrEmpty(find)) return source;
                return source.Replace(find, replaceWith);
            });
            _engine.RegisterCommand("text.split", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string source    = bound?.GetOrDefault<string>("Source", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string delimiter = bound?.GetOrDefault<string>("Delimiter", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(delimiter)) return null;
                return string.Join(",", source.Split(delimiter));
            });
            // L2 — Text.Length must count grapheme clusters, not UTF-16 code units.
            // String.Length reports a surrogate pair (e.g. an emoji like "🎉") as 2,
            // which is surprising for stream chat / overlay use where users expect a
            // visible-character count. StringInfo.LengthInTextElements walks the
            // string by text element so emoji and combining marks count as 1.
            _engine.RegisterCommand("text.length", async (args) => {
                string source = _engine.CurrentBoundArgs?.GetOrDefault<string>("Source", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                return new StringInfo(source ?? string.Empty)
                    .LengthInTextElements
                    .ToString(CultureInfo.InvariantCulture);
            });
            _engine.RegisterCommand("text.to_lower", async (args) => {
                // H6 — InvariantCulture so behavior is deterministic across locales
                // (Turkish-i hazard etc.).
                string source = _engine.CurrentBoundArgs?.GetOrDefault<string>("Source", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                return source.ToLowerInvariant();
            });

            // ── Text parsing ─────────────────────────────────────────────────
            // text.parse_command(message, n) — returns comma-joined segments
            _engine.RegisterCommand("text.parse_command", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string msg = bound?.GetOrDefault<string>("Message", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int n = (bound != null && bound.ContainsKey("Segments"))
                    ? bound.Get<int>("Segments")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nx) ? nx : 0);
                return ParseCommandSegments(msg, n);
            });
        }

        // CONTRACT: for n > 0 the result is ALWAYS exactly n comma-separated
        // segments. Present tokens keep the prior semantics — leading '!'
        // stripped, split on spaces with RemoveEmptyEntries, the final segment
        // soaks up the remainder — and missing trailing segments are padded
        // with "" so array.get(list, i) on an unsupplied argument yields an
        // empty string (and convert.to_int yields 0) instead of array.get's
        // out-of-range clamp echoing the command word ("!raffle" with n=4
        // must read as "raffle,,," — not "raffle" from every index).
        // n <= 0 stays null (invalid Segments count).
        // Internal static so tests hit the pure logic directly via
        // InternalsVisibleTo, same as ArgOrEmpty / ExpandArgsList.
        internal static string? ParseCommandSegments(string msg, int n)
        {
            if (n <= 0) return null;
            var parts = (msg ?? string.Empty).TrimStart('!')
                .Split(' ', n, StringSplitOptions.RemoveEmptyEntries);
            var segments = new string[n];
            for (int i = 0; i < n; i++)
                segments[i] = i < parts.Length ? parts[i] : string.Empty;
            return string.Join(",", segments);
        }
    }
#pragma warning restore CS1998
}
