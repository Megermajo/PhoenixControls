using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    // Exporter registrations for the giveaway.* nodes.
    //
    //   Giveaway.Create  → simple emit (no value output).
    //   Giveaway.Close   → giveaway.close(<giveaway>, <public>, "<base>")   → TotalTickets, EntrantCount
    //   Giveaway.Ticket  → giveaway.ticket(<giveaway>, <public>, <user>, <inc>, <role>, "<base>") → Tickets, Limit
    //                      (+ ", <pricetable>, <price>, <isall>" ONLY when PriceTable is set —
    //                       the price trio trails "<base>" so pre-price .phx keep binding; adds
    //                       the NoFunds flow + Purchased value outputs)
    //   Giveaway.Winner  → giveaway.winner(<giveaway>, <public>, "<base>")  → WinnerName, WinnerTickets
    //
    // The trailing "<base>" literal is the result-var base (ScriptExporter
    // .GiveawayResultBase(node) = "_gw_<id6>"). The Hub handler writes each value
    // output under "{base}_<socket-key>" (SetLocalResultVar) and
    // ScriptExporter.ResolveOutputFromNode resolves the node's output sockets to
    // {base}_<socket-key> so downstream nodes read them. The socket-key mapping
    // is ScriptExporter.GiveawaySocketKey (letters/digits, lowercased) and MUST
    // match the suffixes written in ScriptManager.Giveaway.cs.
    public static partial class ExporterRegistrations
    {
        private static void RegisterGiveaway(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Giveaway.Create", "giveaway.create",
                new[]
                {
                    new SocketArg("Title",      "\"\""),
                    new SocketArg("SetDefault", "true"),
                },
                FollowNamedOutput: "Done"));

            r.Register(new GiveawayCloseHandler());
            r.Register(new GiveawayTicketHandler());
            r.Register(new GiveawayWinnerHandler());
        }
    }

    internal sealed class GiveawayCloseHandler : IExporterHandler
    {
        public string NodeTitle => "Giveaway.Close";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string giveaway = ctx.Materialize(node, "Giveaway", "\"\"");
            string isPublic = ctx.Resolve(node, "Public", "true");
            string baseVar  = ScriptExporter.GiveawayResultBase(node);
            ctx.Emit($"{prefix}giveaway.close({giveaway}, {isPublic}, \"{baseVar}\")");
            ctx.FollowNamed(node, "Done", indent);
        }
    }

    internal sealed class GiveawayTicketHandler : IExporterHandler
    {
        public string NodeTitle => "Giveaway.Ticket";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string giveaway  = ctx.Materialize(node, "Giveaway", "\"\"");
            string isPublic  = ctx.Resolve(node, "Public", "true");
            string user      = ctx.Materialize(node, "User", "\"\"");
            string increment = ctx.Resolve(node, "Increment", "1");
            // IsSub/IsMod replaced the legacy Role STRING (positions 4-5 of the
            // call). A Bool literal at position 4 is also what the engine's
            // legacy-shape shim keys on to tell new emissions from pre-rework
            // role-string .phx files — never emit anything else there.
            string isSub     = ctx.Resolve(node, "IsSub", "false");
            string isMod     = ctx.Resolve(node, "IsMod", "false");
            string baseVar   = ScriptExporter.GiveawayResultBase(node);

            // PriceTable is the price feature's master switch: empty (unwired,
            // blank pill) keeps the pre-price 6-arg emission byte-identical —
            // goldens and existing user graphs must not change. Materialize
            // resolves a wired upstream to a non-empty {var} reference, so any
            // wire also activates the feature.
            string priceTable = ctx.Materialize(node, "PriceTable", "\"\"");
            bool priceActive  = !IsEmptyLiteral(priceTable);

            var noFundsT = ctx.GetNamedTarget(node, "NoFunds");
            if (!priceActive)
            {
                if (noFundsT != null)
                    ctx.AddRuntimeWarning(
                        "Giveaway.Ticket: NoFunds is wired but PriceTable is empty — entry is free, the NoFunds branch can never fire.",
                        node.Id);

                ctx.Emit($"{prefix}giveaway.ticket({giveaway}, {isPublic}, {user}, {increment}, {isSub}, {isMod}, \"{baseVar}\")");

                // An unwired Limit keeps the legacy single-continuation emission
                // byte-identical. A wired Limit branches on the handler's
                // "{base}_limit" local result var — braced, so the engine
                // resolves the value written in this same execution instead of
                // comparing the literal identifier.
                if (ctx.GetNamedTarget(node, "Limit") == null)
                {
                    ctx.FollowNamed(node, "Done", indent);
                    return;
                }
                ctx.EmitConditional(node, $"{{{baseVar}_limit}}", "Limit", "Done", prefix, indent);
                return;
            }

            string price = ctx.Resolve(node, "Price", "0");
            string isAll = ctx.Resolve(node, "IsAll", "false");
            ctx.Emit($"{prefix}giveaway.ticket({giveaway}, {isPublic}, {user}, {increment}, {isSub}, {isMod}, \"{baseVar}\", {priceTable}, {price}, {isAll})");

            var limitT = ctx.GetNamedTarget(node, "Limit");
            if (noFundsT != null && limitT != null)
            {
                // Three-arm ladder (same shape as Logic.Switch): the handler
                // sets at most ONE of nofunds/limit per execution, so the arms
                // are mutually exclusive at runtime.
                ctx.Emit($"{prefix}if {{{baseVar}_nofunds}}:");
                ctx.ProcessNode(noFundsT, indent + 1);
                ctx.Emit($"{prefix}elif {{{baseVar}_limit}}:");
                ctx.ProcessNode(limitT, indent + 1);
                var doneT = ctx.GetNamedTarget(node, "Done");
                if (doneT != null)
                {
                    ctx.Emit($"{prefix}else:");
                    ctx.ProcessNode(doneT, indent + 1);
                }
                return;
            }
            if (noFundsT != null)
            {
                ctx.EmitConditional(node, $"{{{baseVar}_nofunds}}", "NoFunds", "Done", prefix, indent);
                return;
            }
            if (limitT != null)
            {
                ctx.EmitConditional(node, $"{{{baseVar}_limit}}", "Limit", "Done", prefix, indent);
                return;
            }
            ctx.FollowNamed(node, "Done", indent);
        }

        // Empty-value shapes MaterializeInput can produce for a blank pill:
        // "" (blank attribute), "\"\"" (quoted-empty fallback/literal).
        private static bool IsEmptyLiteral(string v)
            => string.IsNullOrWhiteSpace(v) || v == "\"\"";
    }

    internal sealed class GiveawayWinnerHandler : IExporterHandler
    {
        public string NodeTitle => "Giveaway.Winner";
        public void Emit(Node node, int indent, string prefix, ExporterContext ctx)
        {
            string giveaway = ctx.Materialize(node, "Giveaway", "\"\"");
            string isPublic = ctx.Resolve(node, "Public", "true");
            string baseVar  = ScriptExporter.GiveawayResultBase(node);
            ctx.Emit($"{prefix}giveaway.winner({giveaway}, {isPublic}, \"{baseVar}\")");
            ctx.FollowNamed(node, "Done", indent);
        }
    }
}
