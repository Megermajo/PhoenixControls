using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.Core
{
    // DATABANK band carve. Every DB.* template (the
    // Vars + User_* row CRUD surface that scripts use to talk
    // to DB at runtime).
    //
    // Notable per-template invariants preserved verbatim:
    //   * DB.Increment — uses the DB.SetCell socket shape (TableName /
    //     RowId / Column / Amount input sockets, with matching attribute
    //     defaults feeding the inline pills); ships Amount=1 by default
    //     so a freshly-dropped node increments by 1 without any wiring.
    //   * DB.InsertRow / DB.FetchRow — deliberately omits a
    //     "NewRowId" / "Row" default; the matching exporter handlers
    //     fall back to a node-id-suffixed local var so two of the same
    //     node don't clobber each other's results.
    //   * DB.FetchRow — KnownColumns hint is purely a
    //     design-time autocomplete contributor; the exporter ignores it.
    public static partial class NodeRegistry
    {
        private static void RegisterDatabankTemplates()
        {
            // Inline default-attribute seeds — the node-UI rule
            // (UE-Blueprints style, inline socket renaming, slim inspector)
            // makes the inline pills the primary editing affordance. Empty
            // string seeds materialise an empty-but-clickable pill on the
            // node body (no `(empty)` placeholder text).
            AddTemplate("DB.GetVariable",  "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_getvariable"),
                new[] { ("Key", ColString) },
                new[] { ("Value", ColString) },
                new Dictionary<string, string> { { "Key", "" }, { "Default", "0" } });

            AddTemplate("DB.SetVariable",  "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_setvariable"),
                new[] { ("Flow", ColExec), ("Key", ColString), ("Value", ColString) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Key", "" }, { "Value", "" } });

            // DB.Increment uses the DB.SetCell socket shape (TableName /
            // RowId / Column input sockets, Amount in place of Value) so its TableName +
            // Column pills get the databank dropdown pickers (DatabankPickerKind keys off
            // those socket names) and it matches the other cell DB nodes' styling. The
            // pre-restructure hybrid (TableName attribute + Key/Row sockets) gave it no
            // picker and an off layout (Majo flagged). Existing graphs are migrated
            // in GraphSerializer.MigrateNodes (Key→Column, Row→RowId, TableName attr→
            // socket) so wires + values carry over and the exported .phx is unchanged.
            // Amount defaults to "1" so a freshly-dropped node increments by 1.
            AddTemplate("DB.Increment",    "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_increment"),
                new[] { ("Flow", ColExec), ("TableName", ColString), ("RowId", ColNumber), ("Column", ColString), ("Amount", ColNumber) },
                new[] { ("Done", ColExec), ("NewValue", ColNumber) },
                new Dictionary<string, string> { { "TableName", "User_Counter" }, { "RowId", "" }, { "Column", "" }, { "Amount", "1" } });

            AddTemplate("DB.CheckExists",  "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_checkexists"),
                new[] { ("Flow", ColExec), ("Key", ColString) },
                new[] { ("True", ColExec), ("False", ColExec) },
                new Dictionary<string, string> { { "Key", "" } });

            AddTemplate("DB.DeleteVar", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_deletevar"),
                new[] { ("Flow", ColExec), ("Key", ColString) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "Key", "" } });

            AddTemplate("DB.RowCount", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_rowcount"),
                new[] { ("TableName", ColString) },
                new[] { ("Count", ColNumber) },
                new Dictionary<string, string> { { "TableName", "" } });

            AddTemplate("DB.FindRow", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_findrow"),
                new[] { ("Flow", ColExec), ("TableName", ColString), ("Column", ColString), ("Value", ColString) },
                new[] { ("Found", ColExec), ("NotFound", ColExec), ("RowId", ColNumber) },
                new Dictionary<string, string> { { "TableName", "" }, { "Column", "" }, { "Value", "" } });

            AddTemplate("DB.GetCell", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_getcell"),
                new[] { ("TableName", ColString), ("RowId", ColNumber), ("Column", ColString) },
                new[] { ("Value", ColString) },
                new Dictionary<string, string> { { "TableName", "" }, { "RowId", "" }, { "Column", "" } });

            AddTemplate("DB.SetCell", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_setcell"),
                new[] { ("Flow", ColExec), ("TableName", ColString), ("RowId", ColNumber), ("Column", ColString), ("Value", ColString) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "TableName", "" }, { "RowId", "" }, { "Column", "" }, { "Value", "" } });

            AddTemplate("DB.InsertRow", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_insertrow"),
                new[] { ("Flow", ColExec), ("TableName", ColString), ("Column", ColString), ("Value", ColString) },
                new[] { ("Done", ColExec), ("NewRowId", ColNumber) },
                // No shared "NewRowId" default: DbInsertRowHandler falls back to a
                // node-id-suffixed local var so two InsertRow nodes don't clobber each other.
                // (Value IS seeded — it's a constant arg, not the per-node result var guarded here.)
                new Dictionary<string, string> { { "TableName", "" }, { "Column", "" }, { "Value", "" } });

            AddTemplate("DB.DeleteRow", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_deleterow"),
                new[] { ("Flow", ColExec), ("TableName", ColString), ("RowId", ColNumber) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "TableName", "" }, { "RowId", "" } });

            AddTemplate("DB.ClearTable", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_cleartable"),
                new[] { ("Flow", ColExec), ("TableName", ColString) },
                new[] { ("Done", ColExec) },
                new Dictionary<string, string> { { "TableName", "" } });

            AddTemplate("DB.GetColumn", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_getcolumn"),
                new[] { ("TableName", ColString), ("Column", ColString) },
                new[] { ("List", ColList) },
                new Dictionary<string, string> { { "TableName", "" }, { "Column", "" } });

            AddTemplate("DB.FetchRow", "Databank", Color.Gold,
                Localizer.T("architect.node.bubble.db_fetchrow"),
                new[] { ("Flow", ColExec), ("TableName", ColString), ("RowId", ColNumber) },
                new[] { ("Found", ColExec), ("NotFound", ColExec), ("Row", ColObject) },
                // No shared "Row" default: DbFetchRowHandler falls back to a
                // node-id-suffixed local var so two FetchRow nodes don't clobber each other.
                //
                // KnownColumns (optional) — comma-separated hint that
                // surfaces `<Row>.<col>` tokens in the inline-attr `{var}`
                // autocomplete popup downstream. Pure design-time hint: empty
                // means "no per-column suggestions", which keeps the existing
                // `<Row>` token still showing up via the upstream walk. The
                // exporter ignores this attribute entirely — runtime columns
                // are still whatever the live row actually carries.
                new Dictionary<string, string> { { "TableName", "" }, { "RowId", "" }, { "KnownColumns", "" } });
        }
    }
}
