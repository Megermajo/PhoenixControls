using System.Globalization;
using System.Threading.Tasks;

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

            // giveaway.ticket(Giveaway, Public, User, Increment, Role, ResultBase) → Tickets
            // Increment <= 0 (or a closed giveaway) makes this read-only — it returns
            // the user's current ticket count without adding.
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
                string role = StripBareQuotes(bound?.GetOrDefault<string>("Role", ArgOrEmpty(args, 4)) ?? ArgOrEmpty(args, 4));
                string baseVar = StripBareQuotes(bound?.GetOrDefault<string>("ResultBase", ArgOrEmpty(args, 5)) ?? ArgOrEmpty(args, 5));

                long? id = await GiveawayService.Instance.ResolveTargetAsync(selector, isPublic);
                if (id is null) return null;
                int count = await GiveawayService.Instance.AddTicketAsync(id.Value, user, role, increment);
                if (!string.IsNullOrEmpty(baseVar))
                    _engine.SetLocalResultVar($"{baseVar}_tickets", count.ToString(CultureInfo.InvariantCulture));
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
        }

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
