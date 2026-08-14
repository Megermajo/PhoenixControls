using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Quotes band — reduced to its event root. The five imperative/value nodes
    // (Quote.Get / Count / Add / Edit / Delete) were RETIRED in the 2026-08 tool-node
    // cut: the Quotes store is the OPEN "Quotes" table, so a graph reads and writes
    // it with the generic DB.* band (DB.FindRow / DB.GetCell / DB.InsertRow /
    // DB.DeleteRow / DB.RowCount / DB.Top) instead of a per-tool wrapper. Retired
    // titles are dropped from loaded graphs by GraphSerializer.DropRetiredToolNodes,
    // and the quote.* script commands answer through ScriptManager.RetiredCommands
    // shims so hand-authored .phx files degrade with a hint instead of a red log row.
    //
    // The event node stays: it is the only way a graph can react to a quote being
    // added, and no generic node can reproduce a trigger.
    public static partial class NodeRegistry
    {
        private static void RegisterQuotesTemplates()
        {
            // MediumPurple header — distinct from Timer's Coral, Loyalty's
            // MediumSeaGreen and Giveaway's Goldenrod while keeping the "Pre-Builds"
            // siblings visually adjacent. Unused by any other band.
            Color quotes = Color.MediumPurple;

            // ── Event node (category "Events" — output-only root) ────────────
            // MIRROR Counter.OnChanged: null inputs, Flow output first. Category
            // "Events" makes it a script entry point; the exporter emits
            // on_event(Quote.OnAdded) via ProcessEventNode's trigger-switch fallback,
            // matching the phoenixEvent string QuotesService raises.
            AddTemplate("Quote.OnAdded", "Events", quotes,
                "Fires whenever a quote is added (via the !addquote chat command or the Quotes panel). Number/Text/Name describe the new quote.",
                null,
                new[] { ("Flow", ColExec), ("Number", ColNumber), ("Text", ColString), ("Name", ColString) });

            SetSocketDescriptions("Quote.OnAdded", new()
            {
                { "Number", "The new quote's number." },
                { "Text",   "The new quote's text." },
                { "Name",   "The quoted person's name." },
            });

            SetKeywords("Quote.OnAdded", "quote", "quotes", "added", "onadd", "event", "trigger");
        }
    }
}
