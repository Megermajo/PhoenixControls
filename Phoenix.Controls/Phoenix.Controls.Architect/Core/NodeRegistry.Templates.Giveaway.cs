using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Giveaway band — the four giveaway.* logic nodes. Architect-only (the Hub
    // runs them; Visualist never touches the DB). All carry the shared "public"
    // bool: when true the node retargets to the app-wide default giveaway picked
    // in the Hub Giveaway page; when false it uses its own "Giveaway" selector
    // (matched by id, key, or title). create takes a Title + SetDefault instead.
    //
    // Value outputs (Close/Ticket/Winner) are real output sockets; the matching
    // exporter handlers inject a node-id-derived result-var base so downstream
    // nodes can read them — see ExporterRegistry.Giveaway.cs +
    // ScriptExporter.ResolveOutputFromNode's Giveaway.* special-case.
    public static partial class NodeRegistry
    {
        private static void RegisterGiveawayTemplates()
        {
            AddTemplate("Giveaway.Create", "Giveaway", Color.Goldenrod,
                "Open a new giveaway. When 'set default' is on it becomes the active giveaway every public node targets.",
                new[] { ("Flow", ColExec), ("Title", ColString), ("SetDefault", ColBool) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Title", "My Giveaway" }, { "SetDefault", "true" } });

            AddTemplate("Giveaway.Close", "Giveaway", Color.Goldenrod,
                "Close a giveaway (stops accepting entries). Outputs the combined ticket total and how many users entered.",
                new[] { ("Flow", ColExec), ("Giveaway", ColString), ("Public", ColBool) },
                new[] { ("Done", ColExec), ("TotalTickets", ColNumber), ("EntrantCount", ColNumber) },
                new Dictionary<string, string> { { "Giveaway", "" }, { "Public", "true" } });

            // Limit / NoFunds / Purchased and the price trio are appended AFTER
            // the original sockets — a socket RENAME (or reorder) silently
            // prunes links in every saved user graph, so the pre-existing
            // sockets must never move or change name.
            //
            // Price mechanics: PriceTable names the user table channel points
            // are charged from (columns name/currency; the ▾ picker offers a
            // one-click "Create 'ChannelPoints' table"). With Public = true the
            // node follows the targeted giveaway's Ticket-price SETTING; with
            // Public = false the node's own Price pill applies. Empty
            // PriceTable = free entry (pre-price behavior, byte-identical
            // export). IsAll buys as many tickets as the balance and the
            // per-user cap allow instead of the fixed Increment.
            // Role (String badge) was replaced by the IsSub/IsMod Bool pair —
            // wire them from Chat.Message's IsSub/IsMod outputs. They are
            // APPENDED after the price trio per the never-move rule above;
            // GraphSerializer.MigrateNodes drops the legacy Role socket and
            // translates a legacy Role pill into the new checkbox defaults.
            AddTemplate("Giveaway.Ticket", "Giveaway", Color.Goldenrod,
                "Add tickets for a user (or, with increment 0, just read their current count). Outputs the user's ticket total and how many this call purchased. When the per-user cap blocks the entry the Limit flow fires; when the channel-point balance blocks it, NoFunds fires instead. Set PriceTable (columns name/currency) to charge points per ticket — the price comes from the giveaway's settings when Public is on, else from the Price pill. IsAll buys as many tickets as points and cap allow. IsSub/IsMod record the entrant's badges for the draw-time bonus weighting. Adding is ignored once the giveaway is closed.",
                new[] { ("Flow", ColExec), ("Giveaway", ColString), ("Public", ColBool), ("User", ColString), ("Increment", ColNumber), ("PriceTable", ColString), ("Price", ColNumber), ("IsAll", ColBool), ("IsSub", ColBool), ("IsMod", ColBool) },
                new[] { ("Done", ColExec), ("Tickets", ColNumber), ("Limit", ColExec), ("NoFunds", ColExec), ("Purchased", ColNumber) },
                new Dictionary<string, string> { { "Giveaway", "" }, { "Public", "true" }, { "User", "" }, { "Increment", "1" }, { "PriceTable", "" }, { "Price", "0" }, { "IsAll", "false" }, { "IsSub", "false" }, { "IsMod", "false" } });

            AddTemplate("Giveaway.Winner", "Giveaway", Color.Goldenrod,
                "Draw a winner weighted by ticket count. Outputs the winner's name and their ticket total.",
                new[] { ("Flow", ColExec), ("Giveaway", ColString), ("Public", ColBool) },
                new[] { ("Done", ColExec), ("WinnerName", ColString), ("WinnerTickets", ColNumber) },
                new Dictionary<string, string> { { "Giveaway", "" }, { "Public", "true" } });

            // Giveaway.Id — pure-data probe (NO flow). Outputs the numeric id of
            // the current DEFAULT giveaway (the one public nodes target and that
            // tickets are stored under in GiveawayTickets.GiveawayId), as a String
            // so it wires straight into another node's "Giveaway" selector. Resolved
            // inline as giveaway.default_id() — see ScriptExporter.ComputeInlineValue
            // + the Giveaway.Id arm in ResolveOutputFromNode (mirrors Queue.Length).
            // Empty string when no default giveaway is set.
            AddTemplate("Giveaway.Id", "Giveaway", Color.Goldenrod,
                "Outputs the ID of the current default giveaway — the one public giveaway nodes target. Empty when no default is set.",
                null,
                new[] { ("Id", ColString) });

            // Giveaway.IsActive — pure-data probe (NO flow), the source of truth
            // for whether a giveaway is currently accepting entries. Empty
            // selector = the app-wide default giveaway; the ▾ picker on the
            // selector lists the existing giveaways. Resolved inline as
            // giveaway.is_active(<selector>) — mirrors Giveaway.Id (see
            // ScriptExporter.ComputeInlineValue + the shared inline arm in
            // ResolveOutputFromNode).
            AddTemplate("Giveaway.IsActive", "Giveaway", Color.Goldenrod,
                "Outputs true while the targeted giveaway is open and accepting entries. Leave the selector empty to follow the app-wide default giveaway, or pick/type a specific one. False when it is closed, already drawn, or doesn't exist.",
                new[] { ("Giveaway", ColString) },
                new[] { ("IsActive", ColBool) },
                new Dictionary<string, string> { { "Giveaway", "" } });

            // Socket-level hover help (canvas pin pop-ups + doc-form sub-text;
            // MigrateNodes backfills these into already-saved graphs on load).
            // The price trio especially needs explaining — the pills alone
            // don't convey the Public-toggle price-source rule or IsAll.
            SetSocketDescriptions("Giveaway.Create", new()
            {
                { "SetDefault", "On = this becomes the app-wide default giveaway that every node with Public on targets (same as the pin control in the Hub Giveaway page)." },
            });
            SetSocketDescriptions("Giveaway.Ticket", new()
            {
                { "Giveaway",   "Which giveaway to target — matched by numeric id, key (g-2026-…-a) or title. Ignored while Public is on and a default giveaway exists." },
                { "Public",     "On = follow the app-wide default giveaway from the Hub Giveaway page, including its Ticket-price setting. Off = use the Giveaway selector and this node's own Price pill." },
                { "User",       "Username the tickets are booked — and channel points charged — under. Must match the name column of the currency table when a price applies." },
                { "Increment",  "Tickets to add. 0 just reads the current count without adding. Ignored while IsAll is on." },
                { "IsSub",      "On = record the entrant as a subscriber — wire it from Chat.Message's IsSub output. Subscribers' tickets weigh ×factor at draw time (the Hub panel's Subscriber bonus; status is additionally re-checked live from Streamer.bot before the draw)." },
                { "IsMod",      "On = record the entrant as a moderator — wire it from Chat.Message's IsMod output. Moderators' tickets weigh ×factor at draw time (the Hub panel's Moderator bonus; uses the flag recorded here). Combines with IsSub for people who are both." },
                { "PriceTable", "Currency table channel points are charged from — needs the columns name and currency. The ▾ picker lists your databank tables and offers a one-click 'Create ChannelPoints table'. Leave empty for a free entry (the node then behaves exactly as before the price feature)." },
                { "Price",      "Channel points per ticket while Public is OFF. With Public on this pill is ignored — the targeted giveaway's Ticket-price setting from the Hub panel applies instead. 0 = free." },
                { "IsAll",      "On = ignore Increment and buy as many tickets as the user's points AND the per-user cap allow (the '!ticket all' pattern). Off = all-or-nothing: if the points don't cover the request, nothing is charged and NoFunds fires." },
                { "Done",       "Fires when the entry landed (or on a plain read). With a price set, the points have been charged at this moment." },
                { "Tickets",    "The user's ticket total after this call." },
                { "Limit",      "Fires instead of Done when the per-user cap blocked or truncated the entry. Only the tickets actually granted were charged." },
                { "NoFunds",    "Fires when a price applies and the user's points can't cover the entry — nothing charged, no tickets added. Wire it to answer with the missing amount." },
                { "Purchased",  "How many tickets THIS call actually bought — with IsAll or a cap clamp the granted amount isn't obvious from Increment." },
            });
            SetSocketDescriptions("Giveaway.Close", new()
            {
                { "Giveaway",     "Which giveaway to close — matched by numeric id, key or title. Ignored while Public is on and a default giveaway exists." },
                { "Public",       "On = close the app-wide default giveaway from the Hub Giveaway page. Off = use the Giveaway selector." },
                { "TotalTickets", "Combined ticket count across all entrants at the moment of closing." },
                { "EntrantCount", "How many unique users entered." },
            });
            SetSocketDescriptions("Giveaway.Winner", new()
            {
                { "Giveaway",      "Which giveaway to draw from — matched by numeric id, key or title. Ignored while Public is on and a default giveaway exists." },
                { "Public",        "On = draw on the app-wide default giveaway from the Hub Giveaway page. Off = use the Giveaway selector." },
                { "WinnerName",    "The drawn winner. Odds are weighted by ticket count; with a subscriber bonus set on the giveaway, subs' tickets weigh ×factor (status gathered live right before the draw)." },
                { "WinnerTickets", "The winner's ticket count at draw time." },
            });
            SetSocketDescriptions("Giveaway.Id", new()
            {
                { "Id", "Numeric id of the current default giveaway, as a String — wire it straight into another giveaway node's Giveaway selector. Empty when no default is set." },
            });
            SetSocketDescriptions("Giveaway.IsActive", new()
            {
                { "Giveaway", "Which giveaway to check — matched by numeric id, key (g-2026-…-a) or title. The ▾ picker lists your giveaways. Leave empty to follow the app-wide default giveaway from the Hub Giveaway page." },
                { "IsActive", "true while the giveaway is open and accepting entries; false once it is closed or drawn — or when no matching giveaway (or no default) exists." },
            });
        }
    }
}
