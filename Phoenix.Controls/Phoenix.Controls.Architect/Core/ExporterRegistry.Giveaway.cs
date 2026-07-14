using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    // Exporter registrations for the giveaway.* nodes.
    //
    //   Giveaway.Create  → simple emit (no value output).
    //   Giveaway.Close   → giveaway.close(<giveaway>, <public>, "<base>")   → TotalTickets, EntrantCount
    //   Giveaway.Ticket  → giveaway.ticket(<giveaway>, <public>, <user>, <inc>, <role>, "<base>") → Tickets, Limit
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
            string role      = ctx.Materialize(node, "Role", "\"viewer\"");
            string baseVar   = ScriptExporter.GiveawayResultBase(node);
            ctx.Emit($"{prefix}giveaway.ticket({giveaway}, {isPublic}, {user}, {increment}, {role}, \"{baseVar}\")");

            // An unwired Limit keeps the legacy single-continuation emission
            // byte-identical (goldens + existing user graphs must not change).
            // A wired Limit branches on the handler's "{base}_limit" local
            // result var — braced, so the engine resolves the value written in
            // this same execution instead of comparing the literal identifier.
            if (ctx.GetNamedTarget(node, "Limit") == null)
            {
                ctx.FollowNamed(node, "Done", indent);
                return;
            }
            ctx.EmitConditional(node, $"{{{baseVar}_limit}}", "Limit", "Done", prefix, indent);
        }
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
