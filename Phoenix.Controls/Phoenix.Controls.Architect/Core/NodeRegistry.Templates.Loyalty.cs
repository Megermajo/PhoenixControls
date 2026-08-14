using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Loyalty band — reduced to its event roots. The six imperative/value nodes
    // (Loyalty.GetBalance / Top / Add / Deduct / Set / Transfer) were RETIRED in the
    // 2026-08 tool-node cut: the balance and ledger live in OPEN user tables, so a
    // graph reads and mutates them with the generic DB.* band (DB.FindRow /
    // DB.GetCell / DB.Increment / DB.InsertRow, and DB.Top for the leaderboard) —
    // including hand-rolled ledger rows where a graph wants an audit trail. Retired
    // titles are dropped from loaded graphs by GraphSerializer.DropRetiredToolNodes,
    // and the points.* script commands answer through ScriptManager.RetiredCommands
    // shims so hand-authored .phx files degrade with a hint instead of a red log row.
    // (The tool's own chat commands, games and store are untouched — only the
    // Architect wrapper nodes went.)
    //
    // The event nodes stay: they are the only way a graph can react to the loyalty
    // economy, and no generic node can reproduce a trigger.
    //
    //   * Event nodes (category "Events") — output-only event roots that MIRROR
    //     Timer.OnAdd: category "Events" makes them entry points, the
    //     ProcessEventNode trigger-switch fallback emits `on_event(<title>):`
    //     (matching the phoenixEvent strings LoyaltyService raises —
    //     Loyalty.OnEarn / Loyalty.OnPayout / Loyalty.OnRedeem / Loyalty.OnRaffle),
    //     and the dedicated Loyalty arm in ScriptExporter.ResolveOutputFromNode
    //     maps each output socket to the EXACT {event.*} token the matching
    //     RaiseScript(...) call sets (e.g. Reward → {event.reward}, Count →
    //     {event.count}). The generic Events switch would otherwise collapse
    //     Reward → {user.reward} / Count → {user.count} — vars nothing binds on
    //     a loyalty run, so a wired pin would leak an empty token downstream.
    public static partial class NodeRegistry
    {
        private static void RegisterLoyaltyTemplates()
        {
            // Money-green header — distinct from Timer's Coral and Giveaway's
            // Goldenrod while reading as "currency". MediumSeaGreen is unused by
            // any other band (Twitch events use ForestGreen).
            Color loyalty = Color.MediumSeaGreen;

            // ── Event nodes (category "Events" — output-only roots) ──────────
            // (Loyalty.OnSpend was removed in T11-2: nothing in the money layer
            // raised it, so it was a dead trigger — the node set matches the
            // four events LoyaltyService actually raises.)
            AddTemplate("Loyalty.OnEarn", "Events", loyalty,
                "Fires whenever a user earns points (watch-time tick, chat activity, a manual grant, etc.). User is who earned them; Amount is how many; Reason names the source.",
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Amount", ColNumber), ("Reason", ColString) });

            AddTemplate("Loyalty.OnPayout", "Events", loyalty,
                "Fires when a watch-time payout tick credits active viewers in bulk. Count is how many viewers were paid this tick; Total is the total points handed out; Amount is the per-viewer base award; Currency names the points unit.",
                null,
                new[] { ("Flow", ColExec), ("Count", ColNumber), ("Total", ColNumber), ("Amount", ColNumber), ("Currency", ColString) });

            AddTemplate("Loyalty.OnRedeem", "Events", loyalty,
                "Fires when a user redeems a points reward from the loyalty store. User is who redeemed; Reward names it; Cost is the points charged.",
                null,
                new[] { ("Flow", ColExec), ("User", ColString), ("Reward", ColString), ("Cost", ColNumber) });

            AddTemplate("Loyalty.OnRaffle", "Events", loyalty,
                "Fires when a loyalty raffle is drawn. Winners is a comma-separated list of the winning names; Count is how many won; Pot is the total points prize; Entrants is how many entered; Currency names the points unit.",
                null,
                new[] { ("Flow", ColExec), ("Winners", ColString), ("Count", ColNumber), ("Pot", ColNumber), ("Entrants", ColNumber), ("Currency", ColString) });

            // ── Socket-level hover help (canvas pin pop-ups + doc form) ──────
            SetSocketDescriptions("Loyalty.OnEarn", new()
            {
                { "User",   "The user who earned points." },
                { "Amount", "How many points were earned." },
                { "Reason", "What earned them (e.g. watchtime, chat, manual)." },
            });
            SetSocketDescriptions("Loyalty.OnPayout", new()
            {
                { "Count",    "How many viewers were paid in this watch-time payout tick." },
                { "Total",    "Total points handed out across all viewers this tick." },
                { "Amount",   "The per-viewer base award for this tick." },
                { "Currency", "The name of the points unit (plural)." },
            });
            SetSocketDescriptions("Loyalty.OnRedeem", new()
            {
                { "User",   "The user who redeemed the reward." },
                { "Reward", "The redeemed reward's name." },
                { "Cost",   "How many points the reward cost." },
            });
            SetSocketDescriptions("Loyalty.OnRaffle", new()
            {
                { "Winners",  "Comma-separated list of the winning usernames — feed it through Array.Get / Flow.ForEach." },
                { "Count",    "How many winners were drawn." },
                { "Pot",      "The total points prize pool for the raffle." },
                { "Entrants", "How many users entered the raffle." },
                { "Currency", "The name of the points unit (plural)." },
            });

            // Fuzzy spawn-search aliases — the gap between what a user types
            // ("points", "currency", "economy", "coins") and the Loyalty.* titles.
            SetKeywords("Loyalty.OnEarn",   "loyalty", "points", "currency", "earn", "earned");
            SetKeywords("Loyalty.OnPayout", "loyalty", "points", "currency", "payout", "watchtime", "tick", "drip");
            SetKeywords("Loyalty.OnRedeem", "loyalty", "points", "currency", "redeem", "reward", "store", "shop");
            SetKeywords("Loyalty.OnRaffle", "loyalty", "points", "currency", "raffle", "draw", "winners", "lottery");
        }
    }
}
