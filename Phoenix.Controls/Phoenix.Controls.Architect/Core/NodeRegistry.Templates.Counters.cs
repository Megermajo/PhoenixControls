using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Counters band — reduced to its event root. The five imperative/value nodes
    // (Counter.Get / Set / Add / Reset / Delete) were RETIRED in the 2026-08
    // tool-node cut: the counts live in the OPEN "Counters" table, so a graph reads
    // and writes them with the generic DB.* band (DB.GetCell / DB.SetCell /
    // DB.Increment / DB.DeleteRow) instead of a per-tool wrapper. CountersService
    // gained a 15-second databank re-read heartbeat (the RanksService pattern) so a
    // db-written change still reaches the counter overlay widgets and still raises
    // Counter.OnChanged — without it the old service-routed nodes were the only
    // publisher and a raw db write would have silently frozen the overlay. Retired
    // titles are dropped from loaded graphs by GraphSerializer.DropRetiredToolNodes,
    // and the counter.* script commands answer through ScriptManager.RetiredCommands
    // shims so hand-authored .phx files degrade with a hint instead of a red log row.
    //
    // The event node stays: it is the only way a graph can react to a counter
    // changing, and no generic node can reproduce a trigger.
    public static partial class NodeRegistry
    {
        private static void RegisterCountersTemplates()
        {
            // SteelBlue header — distinct from Timer's Coral, Loyalty's
            // MediumSeaGreen and Giveaway's Goldenrod while keeping the "Pre-Builds"
            // siblings visually adjacent. Unused by any other band.
            Color counters = Color.SteelBlue;

            // ── Event node (category "Events" — output-only root) ────────────
            // MIRROR Timer.OnZero: null inputs, Flow output first. Category
            // "Events" makes it a script entry point; the exporter emits
            // on_event(Counter.OnChanged) via ProcessEventNode's trigger-switch
            // fallback, matching the phoenixEvent string CountersService raises.
            AddTemplate("Counter.OnChanged", "Events", counters,
                "Fires whenever a counter changes (via a chat command, the Counters panel, or a databank write picked up by the 15-second sweep). Counter names which one changed; Count is its new value — a delete reports 0.",
                null,
                new[] { ("Flow", ColExec), ("Counter", ColString), ("Count", ColNumber) });

            SetSocketDescriptions("Counter.OnChanged", new()
            {
                { "Counter", "The name of the counter that changed." },
                { "Count",   "The counter's new value after the change." },
            });

            SetKeywords("Counter.OnChanged", "counter", "counters", "changed", "onchange", "event", "trigger");
        }
    }
}
