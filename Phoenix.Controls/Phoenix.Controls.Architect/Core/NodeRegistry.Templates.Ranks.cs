using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Ranks band — the watch-time / points rank ladder ("Pre-Builds ▾ → Ranks", sibling
    // to Loyalty / Counters / Timer). Architect-only authoring surface: the Hub's
    // RanksService owns the ladder, the OPEN "WatchTime" and "Ranks" tables and the
    // rank-up announcement; Visualist never touches them.
    //
    // Three shapes, mirroring the Counters band exactly:
    //   * Value node (category "Ranks") — no flow, resolved inline as a rank.get(...)
    //     call expression (see ScriptExporter.ComputeInlineValue + the explicit
    //     inline-title route). "Ranks" is deliberately NOT a _pureDataCategory — the
    //     control node below must emit flow — so it is routed by title. It is PURE:
    //     the Hub handler reads and returns, it never evaluates or announces, because
    //     the exporter may inline a value node's expression into a condition that is
    //     evaluated more than once.
    //     (Rank.Value and Rank.Top were RETIRED in the 2026-08 tool-node cut: both
    //     read the OPEN WatchTime / balance tables, so the generic DB.* band —
    //     DB.FindRow / DB.GetCell and DB.Top — covers them. Rank.Get STAYS because
    //     the value→rank-name ladder lives in the Ranks config, which no databank
    //     node can reach; a graph would otherwise have to duplicate the ladder.)
    //   * Control node (category "Ranks") — void, Flow-in → Done-out, emitted by a
    //     SimpleEmitDescriptor (see ExporterRegistry.Ranks.cs).
    //   * Event node (category "Events") — output-only root that MIRRORS
    //     Counter.OnChanged: category "Events" makes it an entry point, the
    //     ProcessEventNode trigger-switch fallback emits `on_event(Rank.OnRankUp):`
    //     (matching the phoenixEvent string RanksService raises), and a dedicated
    //     Rank.OnRankUp arm in ScriptExporter.ResolveOutputFromNode maps its outputs to
    //     their {event.*} tokens.
    //
    // ★ TWO NAMING RULES THIS BAND HOLDS, both of them collisions rather than taste:
    //
    //   "Rank" already means LEADERBOARD POSITION in five places (LoyaltyStanding.Rank,
    //   the loyalty.leaderboard JSON key, the Loyalty.Leaderboard widget's Rank pin and
    //   its compositor.js reader, the two panel row VMs). This band's Rank.* nodes are
    //   about a NAMED TIER instead, so the event's output socket is RankName — never
    //   "Rank" — and the ladder position, where one is needed, is called Position.
    //
    //   "Level" is likewise taken: the four hype-train catalog events each expose a
    //   Level socket bound to {event.level}. An output literally named Level here would
    //   share that token through the generic Events switch, so the word is avoided
    //   entirely.
    public static partial class NodeRegistry
    {
        private static void RegisterRanksTemplates()
        {
            // DarkCyan header — unused by any other band (Counters/Users hold SteelBlue,
            // Timer Coral, Loyalty MediumSeaGreen, Giveaway Goldenrod, Automod/SongRequest
            // IndianRed, Quotes/Polls MediumPurple, CustomCommands Tomato) while staying in
            // the muted "Pre-Builds" family.
            Color ranks = Color.DarkCyan;

            // ── Value nodes (no flow — resolved inline, side-effect free) ────
            // ★ User is a LOGIN on every node in this band. The ladder, the watch-minute
            // store and the group grants are all keyed on the platform login, so a graph
            // should pass {event.user_login} — never {user.name}, which is the display name.
            // Left empty the Hub resolves the triggering chatter's login itself, which is
            // why the exporter's default for these pins is "" rather than a name.
            AddTemplate("Rank.Get", "Ranks", ranks,
                "Outputs a viewer's current rank name. User is a login — pass {event.user_login}; leave it empty for the triggering chatter. A viewer below the lowest rank, or one with nothing recorded yet, reads an empty name.",
                new[] { ("User", ColString) },
                new[] { ("RankName", ColString) });

            // ── Control node (void, Flow-in → Done-out) ─────────────────────
            // The one rank.* command with side effects, and the reason it exists: the
            // Loyalty balance table is OPEN, so a graph that moves points with a Databank
            // node never touches a service hook and its viewer would keep the old rank
            // until the next watch-time tick. Evaluating explicitly closes that gap.
            AddTemplate("Rank.Evaluate", "Ranks", ranks,
                "Re-checks a viewer's rank right now and fires Rank.OnRankUp if they just climbed. Use it after a node changed their points directly — the ladder otherwise re-checks on its own watch-time tick. User is a login — pass {event.user_login}; leave it empty for the triggering chatter. A viewer with nothing recorded is left alone.",
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Done", ColExec) });

            // ── Event node (category "Events" — output-only root) ────────────
            // MIRROR Counter.OnChanged: null inputs, Flow output first. Category "Events"
            // makes it a script entry point; the exporter emits on_event(Rank.OnRankUp)
            // via ProcessEventNode's trigger-switch fallback, matching the phoenixEvent
            // string RanksService raises.
            // ★ Login is a first-class output, not a hidden {event.login} a graph author has
            // to know about. User is the DISPLAY name — what a chat line prints — while
            // every store this band touches (the open WatchTime / Ranks tables, the Loyalty
            // balance table, the User-Management group members) is keyed on the LOGIN. A
            // graph that feeds User into a databank row works perfectly for every viewer
            // whose display name is just their login re-cased, and silently mis-keys
            // everyone else, so the right identity has to be on the node where it can be
            // wired rather than typed.
            AddTemplate("Rank.OnRankUp", "Events", ranks,
                "Fires when a viewer climbs to a higher rank — once per promotion, not once per check. User is who climbed (their display name) and Login the same viewer's stable login for databank and group lookups; RankName is the rank they reached, Value the number that got them there and Next the rank above (empty at the top of the ladder).",
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Login", ColString),
                        ("RankName", ColString), ("Value", ColNumber), ("Next", ColString) });

            // ── Socket-level hover help (canvas pin pop-ups + doc form) ──────
            SetSocketDescriptions("Rank.Get", new()
            {
                { "User",     "Which viewer to read, as a LOGIN — pass {event.user_login}, not {user.name}. The ladder is keyed on the login, so a display name that is not simply the login re-cased reads nothing. Leave empty for the viewer who triggered the graph." },
                { "RankName", "The viewer's current rank name, or empty when they are below the lowest rank (or have nothing recorded yet)." },
            });
            SetSocketDescriptions("Rank.Evaluate", new()
            {
                { "User", "Which viewer to re-check, as a LOGIN — pass {event.user_login}, not {user.name}. Leave empty for the viewer who triggered the graph. A viewer with nothing recorded is left alone: no rank, no announcement, no group grant." },
            });
            SetSocketDescriptions("Rank.OnRankUp", new()
            {
                { "User",     "The viewer who climbed, as their display name — what a chat line should print." },
                { "Login",    "The same viewer's stable login. Use this one for a databank row, a group check or another Rank node: the ladder is keyed on the login, and a display name that is not simply the login re-cased would look up nothing." },
                { "RankName", "The rank they just reached." },
                { "Value",    "The number that got them there — watched minutes or points." },
                { "Next",     "The rank above the one they reached; empty when they are at the top." },
            });

            // Fuzzy spawn-search aliases — the gap between what a user types ("rank",
            // "level", "tier", "hours", "watchtime") and the Rank.* titles. "level" and
            // "tier" are keywords rather than titles precisely because both words are
            // taken elsewhere in the product (see the file header).
            SetKeywords("Rank.Get",      "rank", "ranks", "level", "tier", "get", "read", "loyalty", "regular");
            SetKeywords("Rank.Evaluate", "rank", "ranks", "evaluate", "recheck", "refresh", "promote", "update");
            SetKeywords("Rank.OnRankUp", "rank", "ranks", "rankup", "promotion", "level", "tier", "event", "trigger");
        }
    }
}
