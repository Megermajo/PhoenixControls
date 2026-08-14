using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: array.* command registrations.
    // Comma-separated string lists are the engine-wide wire format for
    // "array of strings" — all 11 handlers (make/get/length/contains/push/
    // filter, plus slice/sort/shuffle/reverse/unique) read CSV in and write
    // CSV out. RegisterArrayCommands() is split into the original two cluster
    // methods (RegisterArrayCoreCommands + RegisterArrayHelperCommands) so the
    // partial preserves the original registration ordering on RegisterHubCommands;
    // a future caller can collapse them once the order doesn't matter.
    //
    // Behaviour-preserving carry-overs:
    //   * M18 — out-of-range clamping in array.get and array.slice. Both set
    //     result._oob_clamped = "true"/"false" so downstream graphs can branch.
    //   * M19 — empty-list array.length returns "0" (vs Split-of-empty-string
    //     returning Length 1, which disagreed with for_each iterating 0 times).
    //   * M20 — case-sensitive ordinal compare default for array.contains and
    //     array.filter; opt-in case-insensitive via the 3rd "ignore_case" flag.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterArrayCoreCommands()
        {
            _engine.RegisterCommand("array.make", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                IReadOnlyList<string> items = bound?.GetOrDefault<IReadOnlyList<string>>("Items", Array.Empty<string>())
                    ?? (IReadOnlyList<string>)args;
                return items.Count >= 1 ? string.Join(",", items) : null;
            });
            // Clamp out-of-range index instead of silently swallowing it as
            // empty string. Sets result._oob_clamped = "true" when clamping kicks
            // in so downstream graphs can branch on the error pin if any. Empty
            // arrays bypass clamping (no valid index exists) and return "" with
            // the flag set for parity with the prior behaviour.
            _engine.RegisterCommand("array.get", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int? idxOrNull = (bound != null && bound.ContainsKey("Index"))
                    ? bound.Get<int>("Index")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : (int?)null);
                if (idxOrNull is null) return null;
                int idx = idxOrNull.Value;
                var parts = list.Split(',');
                if (parts.Length == 0)
                {
                    _engine.SetLocalResultVar("result._oob_clamped", "true");
                    return "";
                }
                if (idx < 0 || idx >= parts.Length)
                {
                    int clamped = Math.Clamp(idx, 0, parts.Length - 1);
                    _engine.SetLocalResultVar("result._oob_clamped", "true");
                    return parts[clamped];
                }
                _engine.SetLocalResultVar("result._oob_clamped", "false");
                return parts[idx];
            });
            // Empty list → length 0. The previous `Split(',').Length` returns
            // 1 for an empty string, which disagreed with for_each iterating 0 times.
            _engine.RegisterCommand("array.length", async (args) => {
                string list = _engine.CurrentBoundArgs?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                return string.IsNullOrEmpty(list) ? "0" : list.Split(',').Length.ToString(CultureInfo.InvariantCulture);
            });
            // Case-sensitive ordinal compare is the unified default across
            // array.contains / array.filter (and any future array.indexof). The
            // optional 3rd arg "ignore_case" / "true" opts in to a case-insensitive
            // compare for callers that need it. Previously array.contains used
            // the default string == (culture-sensitive) which gave inconsistent
            // results vs array.filter's Contains() substring semantics.
            _engine.RegisterCommand("array.contains", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string needle = (bound?.GetOrDefault<string>("Search", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1)).Trim();
                if (string.IsNullOrEmpty(needle) && string.IsNullOrEmpty(list)) return null;
                string ignoreFlag = bound?.GetOrDefault<string>("IgnoreCase", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                bool ignoreCase = !string.IsNullOrEmpty(ignoreFlag) && IsIgnoreCaseFlag(ignoreFlag);
                var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                bool found = list.Split(',').Any(x => string.Equals(x.Trim(), needle, cmp));
                return (found ? "true" : "false");
            });
            // array.push(list, value) — returns updated list
            _engine.RegisterCommand("array.push", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list  = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string value = bound?.GetOrDefault<string>("Value", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(list)) return null;
                return string.IsNullOrEmpty(list) ? value : $"{list},{value}";
            });
            // Case-sensitive ordinal substring match by default; opt-in
            // via 3rd-arg ignore_case flag. Previously this used the default
            // string.Contains (culture-sensitive) which disagreed with the
            // ordinal compare in array.contains.
            _engine.RegisterCommand("array.filter", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list   = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string search = bound?.GetOrDefault<string>("Search", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(list) && string.IsNullOrEmpty(search)) return null;
                string ignoreFlag = bound?.GetOrDefault<string>("IgnoreCase", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                bool ignoreCase = !string.IsNullOrEmpty(ignoreFlag) && IsIgnoreCaseFlag(ignoreFlag);
                var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var filtered = list.Split(',').Where(x => x.IndexOf(search, cmp) >= 0);
                return string.Join(",", filtered);
            });
        }

        private void RegisterArrayHelperCommands()
        {
            // ── Array slice ──────────────────────────────────────────────────
            // array.slice(array, start_index) — returns sliced array (comma-separated)
            // Clamp out-of-range start instead of silently returning empty.
            // Negative starts clamp to 0; starts past the end clamp to length (i.e.
            // an empty slice but with _oob_clamped=true so callers can detect it).
            _engine.RegisterCommand("array.slice", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                int? startOrNull = (bound != null && bound.ContainsKey("Start"))
                    ? bound.Get<int>("Start")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : (int?)null);
                if (startOrNull is null) return null;
                int start = startOrNull.Value;
                var parts = list.Split(',');
                int clamped = Math.Clamp(start, 0, parts.Length);
                if (clamped != start)
                {
                    _engine.SetLocalResultVar("result._oob_clamped", "true");
                }
                else
                {
                    _engine.SetLocalResultVar("result._oob_clamped", "false");
                }
                return string.Join(",", parts.Skip(clamped));
            });

            // ── Array helpers (Priority 2 quick wins) ────────────────────────
            // Comma-separated lists are the wire format used everywhere else in the
            // engine, so these stay symmetric: input is a CSV, output is a CSV. All
            // four are pure-data: ScriptExporter inlines them via ResolveOutputFromNode.

            _engine.RegisterCommand("array.sort", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string numericArg = bound?.GetOrDefault<string>("Numeric", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                bool numeric = string.Equals(numericArg, "true", StringComparison.OrdinalIgnoreCase);
                var parts = list.Split(',').Select(p => p.Trim()).ToArray();
                if (numeric)
                {
                    // Numeric items first (ascending), then everything that fails to parse
                    // (preserving original order among the unparseable tail).
                    var withParse = parts
                        .Select(p => (raw: p, val: double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null))
                        .ToList();
                    var sorted = withParse.Where(t => t.val.HasValue).OrderBy(t => t.val!.Value).Select(t => t.raw)
                        .Concat(withParse.Where(t => !t.val.HasValue).Select(t => t.raw));
                    return string.Join(",", sorted);
                }
                return string.Join(",", parts.OrderBy(p => p, StringComparer.Ordinal));
            });

            _engine.RegisterCommand("array.shuffle", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                var parts = list.Split(',').Select(p => p.Trim()).ToArray();
                // Fisher-Yates over a fresh local Random so we don't tax the rest of the
                // engine's RNG users (and the single-script-per-thread model means we
                // don't need cross-thread RNG safety).
                var rng = new Random();
                for (int i = parts.Length - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (parts[i], parts[j]) = (parts[j], parts[i]);
                }
                return string.Join(",", parts);
            });

            _engine.RegisterCommand("array.reverse", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                var parts = list.Split(',').Select(p => p.Trim()).Reverse();
                return string.Join(",", parts);
            });

            _engine.RegisterCommand("array.unique", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string list = bound?.GetOrDefault<string>("List", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var result = new List<string>();
                foreach (var raw in list.Split(','))
                {
                    string p = raw.Trim();
                    if (seen.Add(p)) result.Add(p);
                }
                return string.Join(",", result);
            });
        }
    }
#pragma warning restore CS1998
}
