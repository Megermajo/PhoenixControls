using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.Core
{
    // Small-band carve: LOGIC (comparison/gate adders) +
    // ASYNC/SYNC (Async.Delay / Timeout / Parallel / Join, Async wait
    // primitives, Chat.* peek/wait helpers). Two thematically-related
    // groups bundled into one partial since each on its own is too
    // small to warrant a dedicated file.
    //
    // The LOGIC band is purely a series of AddBinaryOp / AddUnaryOp
    // calls plus one Logic.Select template; the legacy Logic.Gate is
    // already removed from the palette but its handler still ships, per
    // the comment in the parent partial.
    public static partial class NodeRegistry
    {
        private static void RegisterLogicAndAsyncTemplates()
        {
            // ── LOGIC (comparisons / gates) ──────────────────────────────
            // R13 — descriptions reference the symbol from the
            // ComparisonOperators table (single source of truth) instead of
            // hardcoding it inline.
            // Logic.Equals / Logic.NotEquals stay attribute-less because A and B
            // are wildcard Object — picking a sane numeric/bool default would
            // type-leak into the wrong direction once the user wires either pin.
            // The numeric Greater/LessThan and Bool gates can carry inline
            // defaults safely (their pin types are fixed).
            AddBinaryOp("Logic.Equals",      "Logic", Color.RoyalBlue, ColObject, ColObject, ColBool);
            AddTemplate("Logic.Select",      "Logic", Color.RoyalBlue,
                Localizer.T("architect.node.bubble.logic_select"),
                new[] { ("Cond", ColBool), ("A", ColObject), ("B", ColObject) },
                new[] { ("Value", ColObject) });
            AddBinaryOp("Logic.NotEquals",   "Logic", Color.RoyalBlue, ColObject, ColObject, ColBool);
            AddBinaryOp("Logic.GreaterThan", "Logic", Color.RoyalBlue, ColNumber, ColNumber, ColBool, aDefault: "0",     bDefault: "0");
            AddBinaryOp("Logic.LessThan",    "Logic", Color.RoyalBlue, ColNumber, ColNumber, ColBool, aDefault: "0",     bDefault: "0");
            AddBinaryOp("Logic.And",         "Logic", Color.RoyalBlue, ColBool,   ColBool,   ColBool, aDefault: "false", bDefault: "false");
            AddBinaryOp("Logic.Or",          "Logic", Color.RoyalBlue, ColBool,   ColBool,   ColBool, aDefault: "false", bDefault: "false");
            AddUnaryOp ("Logic.Not",         "Logic", Color.RoyalBlue, ColBool,   ColBool,            valDefault: "false");

            // ── ASYNC / SYNC ─────────────────────────────────────────────
            // Async.* nodes get inline defaults for their Number intakes (MS,
            // TimeoutMS) so users can dial in a delay without dropping a
            // Value.Int upstream. Defaults are aligned with each handler's own
            // fallback string in ExporterRegistry.Handlers* — backfill on graph
            // load is therefore byte-for-byte invisible to existing scripts.
            AddTemplate("Async.Delay",         "Async", Color.Gray,
                Localizer.T("architect.node.bubble.async_delay"),
                new[] { ("Flow", ColExec), ("MS", ColNumber) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "MS", "1000" } });

            AddTemplate("Async.WaitForVisual", "Async", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.async_waitforvisual"),
                new[] { ("Flow", ColExec), ("LayerID", ColString), ("WidgetID", ColString), ("TriggerName", ColString), ("TimeoutMS", ColNumber) },
                new[] { ("Done", ColExec), ("Timeout", ColExec) },
                new Dictionary<string, string> { { "TimeoutMS", "10000" } });

            AddTemplate("Async.WaitForEvent",  "Async", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.async_waitforevent"),
                new[] { ("Flow", ColExec), ("EventType", ColString), ("TimeoutMS", ColNumber) },
                new[] { ("Received", ColExec), ("Payload", ColString), ("Timeout", ColExec) },
                new Dictionary<string, string> { { "TimeoutMS", "10000" } });

            AddTemplate("Async.Timeout",       "Async", Color.Gray,
                Localizer.T("architect.node.bubble.async_timeout"),
                new[] { ("Flow", ColExec), ("MS", ColNumber) },
                new[] { ("OnTime", ColExec), ("Late", ColExec) },
                new Dictionary<string, string> { { "MS", "5000" } });

            // Branch fan-out raised from 3 → 8 (Branch1..Branch8), mirroring
            // L15's Array.Make and sweep 7's Logic.Switch / Logic.Sequence raise.
            // GraphSerializer.MigrateNodes back-fills the new Branch4..Branch8
            // sockets on load, so saved graphs keep working — extra branches are
            // simply unwired. Engine `parallel_begin` already supports N branches;
            // this is purely a UX cap removal.
            AddTemplate("Async.Parallel",      "Async", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.async_parallel"),
                new[] { ("Flow", ColExec) },
                new[] {
                    ("Branch1", ColExec), ("Branch2", ColExec), ("Branch3", ColExec), ("Branch4", ColExec),
                    ("Branch5", ColExec), ("Branch6", ColExec), ("Branch7", ColExec), ("Branch8", ColExec)
                });

            AddTemplate("Chat.WaitForNext",    "Async", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.chat_waitfornext"),
                new[] { ("Flow", ColExec), ("User", ColString), ("Command", ColString), ("TimeoutMS", ColNumber) },
                new[] { ("Got", ColExec), ("TimedOut", ColExec), ("Username", ColString), ("Message", ColString) });

            AddTemplate("Chat.PeekRecent",     "Async", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.chat_peekrecent"),
                new[] { ("Flow", ColExec), ("N", ColNumber) },
                new[] { ("Done", ColExec), ("Usernames", ColList), ("Messages", ColList) });

            AddTemplate("Async.Join",          "Async", Color.MediumOrchid,
                Localizer.T("architect.node.bubble.async_join"),
                new[] { ("Flow", ColExec) },
                new[] { ("Flow", ColExec) });
        }
    }
}
