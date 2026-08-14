using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    // Exporter registrations for the Ranks band's ONE control node (void, Flow-in →
    // Done-out). Mirrors the Loyalty control-node descriptors next door.
    //
    //   Rank.Evaluate → rank.evaluate(<user>)
    //
    // SocketArg order == CommandManifest arg order (the locked three-way contract).
    //
    // ★ An unwired User emits "" — NOT {user.name}. Every store this band touches (the open
    // "WatchTime" and "Ranks" tables, the Loyalty balance table, the User-Management group
    // members) is keyed on the platform LOGIN, and {user.name} is the DISPLAY name. Baking
    // the display name in as the default would mis-key every viewer whose two spellings
    // differ — login "neko_chan" shown as "NekoChan!" — while looking correct on every test
    // account whose display name is just their login re-cased. The Hub resolves the empty
    // string through ResolveRankUser: {event.user_login} first, {user.name} only as the last
    // resort for a non-chat trigger that parks no login. Same convention, and the same
    // reason, as Song.Request / Song.RemoveLast.
    //
    // The ONE surviving VALUE node (Rank.Get) has no descriptor here: it is inline
    // pure-data, resolved in ScriptExporter.ComputeInlineValue via the explicit
    // inline-title route, and is listed in NodeCoverageTests.InlinePureDataTitles
    // + CommandManifestTests.KnownImperativeOnlyCommands for exactly that reason.
    // Rank.Get stays because the value→rank-name ladder lives in the Ranks CONFIG,
    // which no databank node can reach. (Rank.Value / Rank.Top were RETIRED in the
    // 2026-08 tool-node cut — both read OPEN tables, so DB.GetCell / DB.Top cover
    // them.) "Ranks" is intentionally NOT a _pureDataCategory, so the void node
    // below still emits flow. Rank.OnRankUp is an Events root (generic on_event
    // fallback), also no descriptor.
    public static partial class ExporterRegistrations
    {
        private static void RegisterRanks(ExporterRegistry r)
        {
            r.RegisterSimple(new SimpleEmitDescriptor(
                "Rank.Evaluate", "rank.evaluate",
                new[]
                {
                    new SocketArg("User", "\"\""),
                },
                FollowNamedOutput: "Done"));
        }
    }
}
