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

            AddTemplate("Giveaway.Ticket", "Giveaway", Color.Goldenrod,
                "Add tickets for a user (or, with increment 0, just read their current count). Outputs the user's ticket total. Adding is ignored once the giveaway is closed.",
                new[] { ("Flow", ColExec), ("Giveaway", ColString), ("Public", ColBool), ("User", ColString), ("Increment", ColNumber), ("Role", ColString) },
                new[] { ("Done", ColExec), ("Tickets", ColNumber) },
                new Dictionary<string, string> { { "Giveaway", "" }, { "Public", "true" }, { "User", "" }, { "Increment", "1" }, { "Role", "viewer" } });

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
        }
    }
}
