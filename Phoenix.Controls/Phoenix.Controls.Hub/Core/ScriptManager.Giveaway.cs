using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: giveaway.* command registrations.
    //
    // Four handlers backing the Architect giveaway nodes — all drive the shared
    // GiveawayService.Instance (the same instance the Hub Giveaway page binds to
    // via HubServices, so a node-driven entry shows up live in the page).
    //
    // Value outputs: close/ticket/winner receive a trailing "ResultBase" arg the
    // exporter injects (e.g. "_gw_ab12cd"); the handler writes each value output
    // under "{base}_<socket-lowercased>", and ScriptExporter resolves the node's
    // output sockets to {base}_<socket> so downstream nodes read them. Keep the
    // suffixes here in sync with ScriptExporter.ResolveOutputFromNode's
    // Giveaway.* special-case.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterGiveawayCommands()
        {
            // Pre-draw subscriber gather for the weighted draw's sub-bonus
            // factor. The service can't touch Streamer.bot itself (Hub's WS
            // bridge lives here), so it gets the lookup injected.
            GiveawayService.Instance.SubscriberStatusResolver = ResolveEntrantSubscribersAsync;

            // giveaway.create(Title, SetDefault) — opens a new giveaway; when
            // SetDefault is true it becomes the app-wide default.
            _engine.RegisterCommand("giveaway.create", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string title = StripBareQuotes(bound?.GetOrDefault<string>("Title", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                bool setDefault = (bound != null && bound.ContainsKey("SetDefault"))
                    ? bound.Get<bool>("SetDefault")
                    : ParseBoolArg(ArgOrEmpty(args, 1), true);
                if (string.IsNullOrWhiteSpace(title)) return null;
                var g = await GiveawayService.Instance.CreateAsync(title, "broadcaster");
                if (setDefault) await GiveawayService.Instance.SetDefaultAsync(g.Id, true);
                return null;
            });

            // giveaway.close(Giveaway, Public, ResultBase) → TotalTickets, EntrantCount
            _engine.RegisterCommand("giveaway.close", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string selector = StripBareQuotes(bound?.GetOrDefault<string>("Giveaway", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                bool isPublic = (bound != null && bound.ContainsKey("Public"))
                    ? bound.Get<bool>("Public")
                    : ParseBoolArg(ArgOrEmpty(args, 1), true);
                string baseVar = StripBareQuotes(bound?.GetOrDefault<string>("ResultBase", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2));

                long? id = await GiveawayService.Instance.ResolveTargetAsync(selector, isPublic);
                if (id is null) return null;
                var (total, entrants) = await GiveawayService.Instance.CloseAsync(id.Value);
                if (!string.IsNullOrEmpty(baseVar))
                {
                    _engine.SetLocalResultVar($"{baseVar}_totaltickets", total.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar($"{baseVar}_entrantcount", entrants.ToString(CultureInfo.InvariantCulture));
                }
                return null;
            });

            // giveaway.ticket(Giveaway, Public, User, Increment, IsSub, IsMod,
            //                 ResultBase, PriceTable, Price, IsAll) → Tickets, Purchased
            // Increment <= 0 (or a closed giveaway) makes this read-only — it returns
            // the user's current ticket count without adding. Writes
            // "{base}_limit" (per-user cap truncated the entry) and
            // "{base}_nofunds" (channel-point balance blocked it) — the exporter's
            // Limit/NoFunds flow branches read them braced. When PriceTable is
            // set, each ticket costs channel points from that user table
            // (columns name/currency). Price source follows the Public toggle:
            // Public = true → the giveaway's TicketPrice setting; Public = false
            // → the node's Price pill. IsAll buys the maximum the balance + cap
            // allow instead of the fixed increment.
            //
            // LEGACY SHIM — the pre-rework node emitted
            //   giveaway.ticket(g, pub, user, inc, ROLE, "base"[, table, price, isAll])
            // with a role STRING at position 4 and the result base at 5. Those
            // .phx files keep running unless re-exported, so the handler detects
            // the old shape from the RAW positional args (under the new manifest
            // the binder mis-names them) and translates: role → IsSub/IsMod
            // flags, positions 5-8 shift back onto base/table/price/isAll.
            _engine.RegisterCommand("giveaway.ticket", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string selector = StripBareQuotes(bound?.GetOrDefault<string>("Giveaway", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                bool isPublic = (bound != null && bound.ContainsKey("Public"))
                    ? bound.Get<bool>("Public")
                    : ParseBoolArg(ArgOrEmpty(args, 1), true);
                string user = StripBareQuotes(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2));
                int increment = (bound != null && bound.ContainsKey("Increment"))
                    ? bound.Get<int>("Increment")
                    : (int.TryParse(StripBareQuotes(ArgOrEmpty(args, 3)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var inc) ? inc : 1);

                string rawA4 = StripBareQuotes(ArgOrEmpty(args, 4));
                string rawA5 = StripBareQuotes(ArgOrEmpty(args, 5));

                string role;
                bool isSub, isMod;
                string baseVar, priceTable;
                int price;
                bool isAll;

                if (LooksLikeLegacyTicketRoleShape(rawA4, rawA5))
                {
                    role = string.IsNullOrWhiteSpace(rawA4) ? "viewer" : rawA4;
                    isSub = IsSubRoleToken(role);
                    isMod = IsModRoleToken(role);
                    baseVar = rawA5;
                    priceTable = StripBareQuotes(ArgOrEmpty(args, 6));
                    price = int.TryParse(StripBareQuotes(ArgOrEmpty(args, 7)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lpr) ? lpr : 0;
                    isAll = ParseBoolArg(ArgOrEmpty(args, 8), false);
                }
                else
                {
                    isSub = ParseBoolArg(StripBareQuotes(bound?.GetOrDefault<string>("IsSub", rawA4) ?? rawA4), false);
                    isMod = ParseBoolArg(StripBareQuotes(bound?.GetOrDefault<string>("IsMod", rawA5) ?? rawA5), false);
                    // The Role badge column keeps its viewer/sub semantics — the
                    // mod flag lives in its own GiveawayTickets.IsMod column so a
                    // subscriber-moderator keeps both facts.
                    role = isSub ? "sub" : "viewer";
                    baseVar = StripBareQuotes(bound?.GetOrDefault<string>("ResultBase", ArgOrEmpty(args, 6)) ?? ArgOrEmpty(args, 6));
                    priceTable = StripBareQuotes(bound?.GetOrDefault<string>("PriceTable", ArgOrEmpty(args, 7)) ?? ArgOrEmpty(args, 7));
                    price = (bound != null && bound.ContainsKey("Price"))
                        ? bound.Get<int>("Price")
                        : (int.TryParse(StripBareQuotes(ArgOrEmpty(args, 8)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pr) ? pr : 0);
                    isAll = (bound != null && bound.ContainsKey("IsAll"))
                        ? bound.Get<bool>("IsAll")
                        : ParseBoolArg(ArgOrEmpty(args, 9), false);
                }

                long? id = await GiveawayService.Instance.ResolveTargetAsync(selector, isPublic);
                if (id is null) return null;
                var (count, purchased, capped, noFunds) = await GiveawayService.Instance.PurchaseTicketAsync(
                    id.Value, user, role, increment, isAll, isPublic, price, priceTable, isMod);
                if (!string.IsNullOrEmpty(baseVar))
                {
                    _engine.SetLocalResultVar($"{baseVar}_tickets", count.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar($"{baseVar}_limit", capped ? "true" : "false");
                    _engine.SetLocalResultVar($"{baseVar}_nofunds", noFunds ? "true" : "false");
                    _engine.SetLocalResultVar($"{baseVar}_purchased", purchased.ToString(CultureInfo.InvariantCulture));
                }
                return null;
            });

            // giveaway.winner(Giveaway, Public, ResultBase) → WinnerName, WinnerTickets
            _engine.RegisterCommand("giveaway.winner", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string selector = StripBareQuotes(bound?.GetOrDefault<string>("Giveaway", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                bool isPublic = (bound != null && bound.ContainsKey("Public"))
                    ? bound.Get<bool>("Public")
                    : ParseBoolArg(ArgOrEmpty(args, 1), true);
                string baseVar = StripBareQuotes(bound?.GetOrDefault<string>("ResultBase", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2));

                long? id = await GiveawayService.Instance.ResolveTargetAsync(selector, isPublic);
                if (id is null) return null;
                var winner = await GiveawayService.Instance.DrawWinnerAsync(id.Value);
                if (!string.IsNullOrEmpty(baseVar))
                {
                    _engine.SetLocalResultVar($"{baseVar}_winnername", winner?.Name ?? "");
                    _engine.SetLocalResultVar($"{baseVar}_winnertickets",
                        (winner?.Tickets ?? 0).ToString(CultureInfo.InvariantCulture));
                    // OddsOneIn is service-side display data — the node's output
                    // sockets stay WinnerName/WinnerTickets.
                }
                return null;
            });

            // giveaway.default_id() → the numeric id of the current default
            // giveaway (the one public nodes target and that GiveawayTickets keys
            // tickets under), or "" when none is set. Inline pure-data: the
            // exporter emits `giveaway.default_id()` and the engine round-trips
            // this return value into the Giveaway.Id node's Id output (mirrors
            // queue.length()). No result-var base, no flow.
            _engine.RegisterCommand("giveaway.default_id", async (args) =>
            {
                var id = await GiveawayService.Instance.GetDefaultIdAsync().ConfigureAwait(false);
                return id?.ToString(CultureInfo.InvariantCulture) ?? "";
            });

            // giveaway.is_active(<selector>) → "true" while the targeted
            // giveaway is open (accepting entries), else "false". Empty selector
            // follows the app-wide default giveaway; a named selector resolves
            // by id/key/title. Inline pure-data: the exporter emits the call and
            // the engine round-trips this return value into the
            // Giveaway.IsActive node's Bool output (mirrors giveaway.default_id).
            // Missing giveaway / no default set = "false", never an error.
            _engine.RegisterCommand("giveaway.is_active", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string selector = StripBareQuotes(
                    bound?.GetOrDefault<string>("Giveaway", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                bool followDefault = string.IsNullOrWhiteSpace(selector);
                long? id = await GiveawayService.Instance
                    .ResolveTargetAsync(selector, followDefault).ConfigureAwait(false);
                if (id is null) return "false";
                bool open = await GiveawayService.Instance.IsOpenAsync(id.Value).ConfigureAwait(false);
                return open ? "true" : "false";
            });
        }

        // ── giveaway.ticket legacy-shape detection ──────────────────────────
        // New emissions carry a Bool literal ("true"/"false" — wired IsSub/IsMod
        // expressions are substituted to their values BEFORE the args reach the
        // handler) at position 4; legacy emissions carry a free-text role badge
        // there ("viewer", "sub", "mod", …) with the result base at position 5.
        // Empty a4 (hand-authored short calls) disambiguates via a5: a Bool
        // token there is the new IsMod, anything else non-empty is a legacy
        // result base. Both empty = new form with all defaults (harmless).
        //
        // UNRESOLVED {brace} tokens (a wired socket whose var never populated —
        // SubstituteVars passes unknown refs through literally) can appear at
        // position 4 in BOTH shapes: a new-form wired IsSub or a legacy wired
        // Role. a4 alone can't discriminate then, so fall to a5 — the legacy
        // shape carries the result base there ("_gw_…", never a bool or brace
        // token) while the new shape carries IsMod (bool/brace/empty). Getting
        // this wrong would silently shift the result base and starve the
        // Limit/NoFunds branches, so the tie-break errs on preserving it.
        internal static bool LooksLikeLegacyTicketRoleShape(string a4, string a5)
        {
            if (!string.IsNullOrEmpty(a4))
            {
                if (IsBoolToken(a4)) return false;
                if (IsBraceToken(a4))
                    return !(string.IsNullOrEmpty(a5) || IsBoolToken(a5) || IsBraceToken(a5));
                return true;
            }
            if (!string.IsNullOrEmpty(a5))
                return !IsBoolToken(a5) && !IsBraceToken(a5);
            return false;
        }

        private static bool IsBraceToken(string s) =>
            s.StartsWith("{", StringComparison.Ordinal);

        private static bool IsBoolToken(string s) =>
            s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            s is "1" or "0" ||
            s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("no", StringComparison.OrdinalIgnoreCase);

        internal static bool IsSubRoleToken(string role) =>
            role.Equals("sub", StringComparison.OrdinalIgnoreCase) ||
            role.Equals("subscriber", StringComparison.OrdinalIgnoreCase);

        internal static bool IsModRoleToken(string role) =>
            role.Equals("mod", StringComparison.OrdinalIgnoreCase) ||
            role.Equals("moderator", StringComparison.OrdinalIgnoreCase);

        // ── Pre-draw subscriber gather ───────────────────────────────────────
        // Resolves the CURRENT subscription status of the given entrants for
        // GiveawayService.DrawWinnerAsync's sub-bonus weighting. Two stages:
        //   1. ONE GetActiveViewers request — Streamer.bot's chatter list
        //      carries a `subscribed` flag per viewer, covering everyone still
        //      active in chat in a single round-trip.
        //   2. Entrants not in the active list fall back to a per-user
        //      "Phoenix: Get User" data-action round-trip (phx_user_sub). The
        //      per-user loop bails on the FIRST dead round-trip (action pack
        //      missing / SB stalled), and the whole stage runs under an
        //      aggregate wall-clock budget — even SUCCESSFUL round-trips take
        //      a few hundred ms each, so a big pool of long-gone entrants
        //      would otherwise stall the draw (and the shared _dataFetchLane
        //      every other data node queues behind) for minutes. Entrants
        //      left unresolved keep their recorded role; one log line says so.
        // Returns null when Streamer.bot is not connected at all.
        internal const int SubCheckBudgetMs = 30_000;

        internal async Task<IReadOnlyDictionary<string, bool>?> ResolveEntrantSubscribersAsync(
            IReadOnlyList<string> usernames)
        {
            if (!WS.Instance.IsConnected) return null;
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // Stage 1 — active-viewer sweep.
            string reqId = WS.NewRequestId("phx-gw-subcheck");
            string response = await WS.Instance.SendAndWaitAsync(
                $@"{{""request"":""GetActiveViewers"",""id"":""{reqId}""}}",
                reqId, 5000).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response))
            {
                try
                {
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("viewers", out var viewers)
                        && viewers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in viewers.EnumerateArray())
                        {
                            if (!v.TryGetProperty("login", out var login)
                                || login.GetString() is not { Length: > 0 } name)
                                continue;
                            map[name] = v.TryGetProperty("subscribed", out var sub) && JsonFlagIsTrue(sub);
                        }
                    }
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log($"Giveaway sub check: GetActiveViewers parse failed: {ex.Message}",
                        "Script", LogLevel.CriticalError);
                }
            }

            // Stage 2 — per-user fallback for entrants who left chat, under an
            // aggregate wall-clock budget.
            var budget = System.Diagnostics.Stopwatch.StartNew();
            int unresolved = 0;
            foreach (var user in usernames)
            {
                if (map.ContainsKey(user)) continue;
                if (budget.ElapsedMilliseconds >= SubCheckBudgetMs)
                {
                    unresolved++;
                    continue;
                }
                var g = await FetchActionGlobalsAsync("giveaway sub check", PhxSbActions.GetUser,
                    new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
                if (g is null)
                {
                    GlobalLogger.Log(
                        "Giveaway sub check: per-user lookup failed — remaining entrants use their recorded role.",
                        "Script", LogLevel.Communication);
                    break;
                }
                map[user] = g.TryGetValue("phx_user_sub", out var flag) && StringFlagIsTrue(flag);
            }
            if (unresolved > 0)
            {
                GlobalLogger.Log(
                    $"Giveaway sub check: time budget ({SubCheckBudgetMs / 1000}s) reached — {unresolved} entrant(s) " +
                    "not verified live; they use their recorded role.",
                    "Script", LogLevel.Communication);
            }
            return map;
        }

        // SB's "Auto Type" toggle decides whether flags arrive as JSON bools or
        // strings — accept both shapes.
        private static bool JsonFlagIsTrue(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.String => StringFlagIsTrue(el.GetString()),
            JsonValueKind.Number => el.TryGetInt64(out long n) && n != 0,
            _ => false,
        };

        private static bool StringFlagIsTrue(string? s)
            => s is not null && (s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || s.Trim() == "1");

        // Tolerant bool parse for the raw-arg fallback path (the typed binder
        // handles the normal case via bound.Get<bool>).
        private static bool ParseBoolArg(string? s, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim().Trim('"');
            if (bool.TryParse(s, out var b)) return b;
            return s is "1" or "yes" or "on" ? true
                 : s is "0" or "no" or "off" ? false
                 : fallback;
        }
    }
}
