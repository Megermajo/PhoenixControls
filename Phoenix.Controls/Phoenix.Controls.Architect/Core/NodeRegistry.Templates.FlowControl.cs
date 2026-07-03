using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Second per-band carve from NodeRegistry.Templates.cs.
    // Owns the FLOW CONTROL band: Logic.Branch / Logic.Switch /
    // Logic.Sequence / Logic.If, the Flow.* family (FlipFlop / DoOnce /
    // DoN / ForLoop / WhileLoop / Cooldown / Select / IsValid / Delay /
    // Reroute), and Logic.EnumMatch (a value-comparison/branch node in the
    // "Logic" category — its EnumMatchHandler still owns the emit, so the
    // category never routes it through the pure-data inline path). Helpers from the parent
    // partial (ComparisonOperators) resolve through partial-class state.
    public static partial class NodeRegistry
    {
        private static void RegisterFlowControlTemplates()
        {
            AddTemplate("Logic.Branch",    "Flow Control", Color.RoyalBlue,
                "Splits the flow based on a boolean. True fires when Condition is true, False fires otherwise. Only one branch fires per call.",
                new[] { ("Flow", ColExec), ("Condition", ColBool) },
                new[] { ("True",  ColExec), ("False", ColExec) });

            // Cases raised from 2 → 8 (A..H). The exporter (SwitchHandler) iterates
            // every non-Default output socket on the live node, so simply adding sockets +
            // matching attribute defaults expands capacity. Existing 2-arm graphs still load
            // unchanged: GraphSerializer.MigrateNodes back-fills any missing sockets from
            // this template, so the extra Case C..H sockets just appear (empty value, no
            // wire) and stay silent until the user configures them.
            AddTemplate("Logic.Switch",    "Flow Control", Color.RoyalBlue,
                "Routes flow to the first case whose configured string is exactly equal to Value. Set the expected string for each Case A..H inline on the node body; Default fires when nothing matches. Comparison is exact string equality (case-sensitive). Up to 8 case arms plus Default.",
                new[] { ("Flow", ColExec), ("Value", ColObject) },
                new[] {
                    ("Case A", ColExec), ("Case B", ColExec), ("Case C", ColExec), ("Case D", ColExec),
                    ("Case E", ColExec), ("Case F", ColExec), ("Case G", ColExec), ("Case H", ColExec),
                    ("Default", ColExec)
                },
                new Dictionary<string, string>
                {
                    { "Case A", "" }, { "Case B", "" }, { "Case C", "" }, { "Case D", "" },
                    { "Case E", "" }, { "Case F", "" }, { "Case G", "" }, { "Case H", "" }
                });

            // Arms raised from 3 → 8. SequenceHandler orders by the leading-integer
            // in the socket name and ignores empty arms (no downstream wire), so adding
            // arms 4..8 is purely additive and existing 3-arm graphs still emit identically.
            AddTemplate("Logic.Sequence",  "Flow Control", Color.SlateGray,
                "Fires every wired output in order from 1 to 8. Each branch runs to completion before the next one starts. Unwired arms are silently skipped.",
                new[] { ("Flow", ColExec) },
                new[] {
                    ("1", ColExec), ("2", ColExec), ("3", ColExec), ("4", ColExec),
                    ("5", ColExec), ("6", ColExec), ("7", ColExec), ("8", ColExec)
                });

            // R13 — operator symbols come from ComparisonOperators (single source of truth).
            string supportedOps = string.Join(", ", ComparisonOperators.Values);
            string defaultOp    = ComparisonOperators["Logic.Equals"]; // "=="
            AddTemplate("Logic.If",        "Flow Control", Color.RoyalBlue,
                $"Compares A against B using the chosen Operator and routes to True or False. Supported operators: {supportedOps}. Numeric operators ({ComparisonOperators["Logic.GreaterThan"]} {ComparisonOperators["Logic.LessThan"]} {ComparisonOperators["Logic.GreaterEqual"]} {ComparisonOperators["Logic.LessEqual"]}) only fire True when both sides parse as numbers. A and B share a wildcard type — wire either side first and the other matches.",
                new[] { ("Flow", ColExec), ("A", ColObject), ("B", ColObject) },
                new[] { ("True", ColExec), ("False", ColExec) },
                new Dictionary<string, string> { { "Operator", defaultOp } });

            AddTemplate("Flow.FlipFlop",   "Flow Control", Color.SteelBlue,
                "Alternates between A and B on every call. First call fires A, second fires B, third fires A again, and so on. The state is remembered across the whole Hub session for this node.",
                new[] { ("Flow", ColExec) },
                new[] { ("A", ColExec), ("B", ColExec) });

            AddTemplate("Flow.DoOnce",     "Flow Control", Color.SteelBlue,
                "Fires Out the very first time it is reached, then silently swallows every later call. State is remembered across the whole Hub session for this node — restart Hub to reset.",
                new[] { ("Flow", ColExec) },
                new[] { ("Out", ColExec) });

            AddTemplate("Flow.DoN",        "Flow Control", Color.SteelBlue,
                "Counts every call. Fires Loop Body for the first N calls, then fires Completed on the (N+1)-th and every later call. The counter is remembered across the whole Hub session for this node — restart Hub to reset.",
                new[] { ("Flow", ColExec), ("N", ColNumber) },
                new[] { ("Loop Body", ColExec), ("Completed", ColExec) });

            AddTemplate("Flow.ForLoop",    "Flow Control", Color.DarkOrange,
                "Counted loop from First to Last, inclusive on both ends. Loop Body fires once per iteration with the current counter on the Index output (also available everywhere downstream as {loop.index}). Completed fires once after the last iteration finishes.",
                new[] { ("Flow", ColExec), ("First", ColNumber), ("Last", ColNumber) },
                new[] { ("Loop Body", ColExec), ("Index", ColNumber), ("Completed", ColExec) });

            AddTemplate("Flow.WhileLoop",  "Flow Control", Color.DarkOrange,
                "Repeats Loop Body while Condition is true; Completed fires once Condition becomes false. As a runaway-loop safety net the engine caps total loop iterations at 500 across the whole script (the aggregate budget shared by every loop in the run) — exhausting that budget logs a CriticalError and aborts. Re-evaluate Condition somewhere inside the body so the loop can end before it eats the budget.",
                new[] { ("Flow", ColExec), ("Condition", ColBool) },
                new[] { ("Loop Body", ColExec), ("Completed", ColExec) });

            AddTemplate("Flow.Cooldown",   "Flow Control", Color.OrangeRed,
                "Rate-limits a flow with a global and/or per-user cooldown (both in seconds). Ready fires when enough time has passed since the last Ready; Blocked fires when the cooldown is still active. Set GlobalCooldown or UserCooldown to 0 to disable that check. Per-user is keyed by the User input — wire it from the chat user to give each viewer their own cooldown.",
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Ready", ColExec), ("Blocked", ColExec) },
                new Dictionary<string, string> { { "GlobalCooldown", "0" }, { "UserCooldown", "0" } });

            AddTemplate("Flow.Select",     "Flow Control", Color.RoyalBlue,
                "Returns one of A/B/C/D based on Index (0=A, 1=B, 2=C, 3=D). Pure data — no flow pin. A, B, C, D and Value share a wildcard type, so wire any one and the rest match. Out-of-range Index returns an empty value.",
                new[] { ("Index", ColNumber), ("A", ColObject), ("B", ColObject), ("C", ColObject), ("D", ColObject) },
                new[] { ("Value", ColObject) });

            AddTemplate("Flow.IsValid",    "Flow Control", Color.RoyalBlue,
                "Branches on whether Value is filled in. True fires when Value has content; False fires when it is empty, missing, or null. Accepts any type — useful for guarding optional inputs (e.g. \"only proceed if a Twitch.GetUser lookup succeeded\").",
                new[] { ("Flow", ColExec), ("Value", ColObject) },
                new[] { ("True", ColExec), ("False", ColExec) });

            AddTemplate("Flow.Delay",      "Flow Control", Color.RoyalBlue,
                "Pauses this flow for the given number of seconds, then continues out of Then. Set Seconds inline on the node body, or wire the Seconds input to override. Fractional seconds are allowed (e.g. 0.5).",
                new[] { ("Flow", ColExec), ("Seconds", ColFloat) },
                new[] { ("Then", ColExec) },
                new Dictionary<string, string> { { "Seconds", "1.0" } });

            // Flow.Reroute is INDEPENDENT — not a member of any category. The
            // internal Title stays "Flow.Reroute" so the exporter handler
            // (RerouteHandler.NodeTitle), the wildcard group, IsReroute, and
            // every existing .phxg file keep working. DisplayName="Reroute"
            // strips the "Flow." prefix from the picker row title; the
            // "Reroute" sentinel in the Category field tells the spawn palette
            // to hoist this row as a standalone top-level point — no category
            // header above it, no category subtitle below it (see
            // SpawnPaletteFlyout.IsTopLevelStandalone + CategoryLabelVis).
            AddTemplate("Flow.Reroute",    "Reroute", Color.DimGray,
                "Tiny pass-through diamond used to keep wires tidy. Has no effect on data or flow — just routes the wire through a midpoint. Set Label to leave a note on the wire for yourself.",
                new[] { ("In",  ColExec) },
                new[] { ("Out", ColExec) },
                new Dictionary<string, string> { { "Label", "" } },
                displayName: "Reroute");

            AddTemplate("Logic.EnumMatch", "Logic", Color.DarkSlateBlue,
                "Checks if Value matches any entry in a list of allowed values. Wire a list into List, or fall back to the comma-separated Entries attribute. Match fires if found and MatchedKey carries the matched entry; NoMatch fires otherwise. Comparison is exact string equality (case-sensitive).",
                new[] { ("Flow", ColExec), ("Value", ColObject), ("List", ColList) },
                new[] { ("Match", ColExec), ("NoMatch", ColExec), ("MatchedKey", ColString) },
                new Dictionary<string, string> { { "Entries", "Alpha, Beta, Gamma" } });
        }
    }
}
