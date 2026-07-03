using System.Collections.Generic;
using System.Drawing;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.Core
{
    // Data-utilities carve. Six small-to-medium bands that
    // together cover the pure-data side of the palette:
    //   * SYSTEM      — System.Log / GetTime / GetDate / Time.SecondsSinceLastFire.
    //   * MATH & RNG  — Math.Random / Chance, the five operator-glyphed
    //     binary ops (Add / Subtract / Multiply / Divide / Modulo per L43),
    //     Math.Clamp / Abs / Min / Max / Floor / Ceil.
    //   * TEXT        — Text.Builder / Format / Contains / Replace / Split /
    //     JoinList / Length / ToUpper / ToLower / ParseCommand. Templates
    //     respect the L3 Template-attr-vs-socket harmonisation.
    //   * COLLECTIONS — Array.Make (L15 8-input fan-out), Array.Literal,
    //     Array.Unpack (5 slots + Rest), Get / Length / Contains / Push /
    //     Filter / Sort / Shuffle / Reverse / Unique.
    //   * VALUES      — Value.String / Int / Float / Bool literal nodes
    //     (L6 inline-editable Value attribute, wired socket overrides).
    //   * CONVERT     — Convert.ToInt / ToString / ToBool / ToFloat with
    //     L6 inline Default fallback attributes and the M8/M9 In→Any
    //     widening on ToString / ToFloat.
    //
    // Larger-than-typical carve to compress sprint count on small bands;
    // each band remains a separate `// ── Header ──` block inside the
    // helper for at-a-glance navigation.
    public static partial class NodeRegistry
    {
        private static void RegisterDataUtilityTemplates()
        {
            // ── SYSTEM ───────────────────────────────────────────────────
            AddTemplate("System.Log",      "System", Color.DimGray,
                Localizer.T("architect.node.bubble.system_log"),
                new[] { ("Flow", ColExec), ("Message", ColString), ("Level", ColString) },
                new[] { ("Flow", ColExec) },
                new Dictionary<string, string> { { "Level", "LogicExecution" } });

            AddTemplate("System.GetTime",  "System", Color.DimGray,
                Localizer.T("architect.node.bubble.system_gettime"),
                null,
                new[] { ("Time", ColString), ("Hours", ColNumber), ("Minutes", ColNumber), ("Seconds", ColNumber), ("UnixTimestamp", ColNumber) });

            AddTemplate("System.GetDate",  "System", Color.DimGray,
                Localizer.T("architect.node.bubble.system_getdate"),
                null,
                new[] { ("Date", ColString), ("Day", ColNumber), ("Month", ColNumber), ("MonthName", ColString), ("Year", ColNumber) });

            AddTemplate("Time.SecondsSinceLastFire", "System", Color.DimGray,
                Localizer.T("architect.node.bubble.time_secondssincelastfire"),
                new[] { ("Key", ColString) },
                new[] { ("Seconds", ColNumber) },
                displayName: "Time Since Last Fire");

            // Time.StreamUptime. Pure-data probe: outputs
            // resolve to {stream.*} tokens at exec time. The anchor
            // ("stream start" instant) defaults to Hub-uptime — i.e. the
            // moment the ScriptEngine module first loaded — and
            // can be re-anchored at runtime via
            // ScriptEngine.SetStreamStartedAtUtc(...) for a
            // future config-driven choice (Streamer.bot connect /
            // first-chat / custom signal). Existing scripts never need
            // to change because the var names are stable.
            AddTemplate("Time.StreamUptime", "System", Color.DimGray,
                "Returns how long the stream has been live, anchored at the configured \"stream start\" signal (default: Hub startup). Uptime is a human-readable form (\"45m\", \"1h 23m\"); UptimeSeconds / Minutes / Hours expose the raw integer counts; Formatted mirrors Uptime; StartedAt is the anchor instant in ISO-8601 UTC.",
                null,
                new[] {
                    ("Uptime",        ColString),
                    ("UptimeSeconds", ColNumber),
                    ("UptimeMinutes", ColNumber),
                    ("UptimeHours",   ColNumber),
                    ("Formatted",     ColString),
                    ("StartedAt",     ColString)
                },
                displayName: "Stream Uptime");

            // ── MATH & RNG ───────────────────────────────────────────────
            // L1 — clarify the half-open interval. Underneath this is `Min + floor(rand() * (Max-Min))`.
            // The rare normalized==1.0 edge case (a uniform that returns exactly 1.0) is impossible
            // by definition of the underlying RNG (it returns [0.0, 1.0) — strictly less than 1),
            // so the result is always < Max even at the limit. This sentence exists because users
            // routinely ask "can it ever return Max?" — answer: no, never.
            AddTemplate("Math.Random",   "Math", Color.MediumPurple,
                Localizer.T("architect.node.bubble.math_random"),
                new[] { ("Min", ColNumber), ("Max", ColNumber) },
                new[] { ("Result", ColNumber) },
                new Dictionary<string, string> { { "Min", "1" }, { "Max", "100" } });

            // Inline default for the % Rate input
            // pill. Exporter (MathChanceHandler in ExporterRegistry.Handlers2.cs)
            // already falls back to "50" via ctx.Resolve(node, "% Rate", "50"),
            // so seeding the template default matches the exporter's behaviour
            // byte-for-byte while making the value visible inline on a fresh
            // node. GraphSerializer.MigrateNodes.TryAdd backfills the key on
            // old graphs so existing .phxg files heal to the same literal the
            // exporter was already emitting — no .phx output change.
            AddTemplate("Math.Chance",   "Math", Color.MediumPurple,
                Localizer.T("architect.node.bubble.math_chance"),
                new[] { ("% Rate", ColFloat) },
                new[] { ("Success", ColExec), ("Fail", ColExec) },
                new Dictionary<string, string> { { "% Rate", "50" } });

            // L43 — UE-Blueprints-style operator glyphs in the canvas chrome.
            // Title stays as the canonical "Math.Add" / "Math.Subtract" / ... key
            // (every exporter switch, every test, every saved .phxg pins those),
            // while DisplayName carries the hybrid "Add  +" form for the node
            // header. Five operators get the treatment for visual consistency:
            //   Math.Add       → "Add  +"
            //   Math.Subtract  → "Subtract  −"   (U+2212 minus, not U+002D hyphen)
            //   Math.Multiply  → "Multiply  ×"   (U+00D7 multiplication sign)
            //   Math.Divide    → "Divide  ÷"    (U+00F7 division sign)
            //   Math.Modulo    → "Modulo  %"
            // Inline-default values mirror the ScriptExporter's per-op fallbacks
            // so the unwired-export path is byte-identical (the Migrate pass at
            // load time will backfill the new attributes onto existing graphs;
            // each one resolves to the same literal the exporter was already
            // emitting). Divide / Modulo use B=1 to avoid div-by-zero on a
            // freshly-spawned node; everyone else uses 0.
            AddBinaryOp("Math.Add",      "Math", Color.MediumPurple, ColNumber, ColNumber, ColNumber, displayName: "Add  +",      aDefault: "0", bDefault: "0");
            AddBinaryOp("Math.Subtract", "Math", Color.MediumPurple, ColNumber, ColNumber, ColNumber, displayName: "Subtract  −", aDefault: "0", bDefault: "0");
            AddBinaryOp("Math.Multiply", "Math", Color.MediumPurple, ColNumber, ColNumber, ColNumber, displayName: "Multiply  ×", aDefault: "0", bDefault: "0");
            AddBinaryOp("Math.Divide",   "Math", Color.MediumPurple, ColNumber, ColNumber, ColFloat,  displayName: "Divide  ÷",   aDefault: "0", bDefault: "1");
            AddBinaryOp("Math.Modulo",   "Math", Color.MediumPurple, ColNumber, ColNumber, ColNumber, displayName: "Modulo  %",   aDefault: "0", bDefault: "1");

            AddTemplate("Math.Clamp",    "Math", Color.MediumPurple,
                Localizer.T("architect.node.bubble.math_clamp"),
                new[] { ("Val", ColNumber), ("Min", ColNumber), ("Max", ColNumber) },
                new[] { ("Result", ColNumber) },
                new Dictionary<string, string> { { "Val", "0" }, { "Min", "0" }, { "Max", "100" } });

            AddUnaryOp ("Math.Abs",      "Math", Color.MediumPurple, ColNumber, ColNumber, valDefault: "0");
            AddBinaryOp("Math.Min",      "Math", Color.MediumPurple, ColNumber, ColNumber, ColNumber, aDefault: "0", bDefault: "0");
            AddBinaryOp("Math.Max",      "Math", Color.MediumPurple, ColNumber, ColNumber, ColNumber, aDefault: "0", bDefault: "0");
            AddUnaryOp ("Math.Floor",    "Math", Color.MediumPurple, ColNumber, ColNumber, valDefault: "0");
            AddUnaryOp ("Math.Ceil",     "Math", Color.MediumPurple, ColNumber, ColNumber, valDefault: "0");

            // ── TEXT & STRINGS ───────────────────────────────────────────
            // L3 — Template harmonisation. Both Text.Builder and Text.Format now expose Template as
            // an inline editable attribute on the node body AND a wired socket override:
            // if the Template socket is wired, the wired value wins; otherwise the inline
            // attribute value is used. This is the same pattern Value.* / Convert.* use for
            // their Default/Value attribute pairings, and matches the UE-Blueprints rule.
            AddTemplate("Text.Builder",  "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_builder"),
                new[] { ("Template", ColString), ("Arg1", ColString), ("Arg2", ColString), ("Arg3", ColString) },
                new[] { ("Result", ColString) },
                new Dictionary<string, string> { { "Template", "Hello {Arg1}" } });

            // Additive C/D placeholder slots. The runtime handler is variadic
            // ({A}..{Z}) and the inline exporter resolver now walks every present Arg
            // slot, so {C}/{D} in the template reach the emitted text.format call.
            // A/B are unchanged (renaming/removing a socket would prune wires in
            // existing graphs); C/D are appended, carry inline default pills, and
            // GraphSerializer.MigrateNodes back-fills them onto old graphs as unwired
            // sockets — no change to emitted output for graphs that never wire C/D.
            AddTemplate("Text.Format",   "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_format"),
                new[] { ("Template", ColString), ("A", ColString), ("B", ColString), ("C", ColString), ("D", ColString) },
                new[] { ("Result", ColString) },
                new Dictionary<string, string> { { "Template", "{A} {B}" }, { "A", "" }, { "B", "" }, { "C", "" }, { "D", "" } });

            AddTemplate("Text.Contains", "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_contains"),
                new[] { ("Source", ColString), ("Search", ColString) },
                new[] { ("Result", ColBool) });

            AddTemplate("Text.Replace",  "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_replace"),
                new[] { ("Source", ColString), ("Find", ColString), ("With", ColString) },
                new[] { ("Result", ColString) });

            // L4 — inline editable Delimiter / Separator attributes (sweep-3's socket-pill pattern).
            // The wired socket still wins when connected; the attribute is the unwired fallback.
            AddTemplate("Text.Split",    "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_split"),
                new[] { ("Source", ColString), ("Delimiter", ColString) },
                new[] { ("Parts", ColList) },
                new Dictionary<string, string> { { "Delimiter", "," } });

            AddTemplate("Text.JoinList", "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_joinlist"),
                new[] { ("List", ColList), ("Separator", ColString) },
                new[] { ("Result", ColString) },
                new Dictionary<string, string> { { "Separator", ", " } });

            AddTemplate("Text.Length",   "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_length"),
                new[] { ("In", ColString) },
                new[] { ("Count", ColNumber) });

            AddTemplate("Text.ToUpper",  "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_toupper"),
                new[] { ("In", ColString) },
                new[] { ("Out", ColString) });

            AddTemplate("Text.ToLower",  "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_tolower"),
                new[] { ("In", ColString) },
                new[] { ("Out", ColString) });

            // Segments carries an inline default so users can hardcode "how many words
            // to split into" right on the node body — matches the wider sweep of giving
            // every Number/Bool intake a typeable bubble. Default 2 mirrors the
            // ScriptExporter fallback, so existing graphs that never set the attribute
            // emit identical output.
            AddTemplate("Text.ParseCommand", "Text", Color.Teal,
                Localizer.T("architect.node.bubble.text_parsecommand"),
                new[] { ("Message", ColString), ("Segments", ColNumber) },
                new[] { ("Parts", ColList) },
                new Dictionary<string, string> { { "Segments", "2" } });

            // ── COLLECTIONS / ARRAYS ─────────────────────────────────────
            // L15 — input cap raised from 3 → 8 (A..H), mirroring sweep 7's M10 fan-out
            // for Logic.Switch / Logic.Sequence. GraphSerializer.MigrateNodes back-fills
            // the new D..H sockets onto existing 3-input graphs at load time, so old
            // .phxg files keep working — the extra sockets just appear unwired.
            // Each slot carries an empty-string attribute default so the inline value
            // editor surfaces every slot in the property panel, even when unwired.
            // ScriptExporter inlines all eight slots (A..H): it walks every slot, then
            // trims trailing empty fallbacks so a 3-wire Array.Make doesn't pad out to
            // length 8 (see the Array.Make case in ScriptExporter.ComputeInlineValue).
            AddTemplate("Array.Make",     "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_make"),
                new[] {
                    ("A", ColString), ("B", ColString), ("C", ColString), ("D", ColString),
                    ("E", ColString), ("F", ColString), ("G", ColString), ("H", ColString)
                },
                new[] { ("List", ColList) },
                new Dictionary<string, string>
                {
                    { "A", "" }, { "B", "" }, { "C", "" }, { "D", "" },
                    { "E", "" }, { "F", "" }, { "G", "" }, { "H", "" }
                });

            AddTemplate("Array.Literal",  "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_literal"),
                null,
                new[] { ("Array", ColList) },
                new Dictionary<string, string> { { "Items", "alpha, beta, gamma" } });

            // L15 — Array.Unpack contract intentionally left at 5 slots + Rest. Changing
            // the slot count would break every script that currently destructures into
            // Item 0..Item 4. Users who need more slots can chain a second Array.Unpack
            // off the Rest output (the Rest slot carries everything beyond Item 4 as a
            // list, ready to feed into another Array.Unpack for slots 5..9, etc.).
            AddTemplate("Array.Unpack",   "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_unpack"),
                new[] { ("Array", ColList) },
                new[] { ("Item 0", ColString), ("Item 1", ColString), ("Item 2", ColString), ("Item 3", ColString), ("Item 4", ColString), ("Rest", ColList) });

            AddTemplate("Array.Get",      "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_get"),
                new[] { ("List", ColList), ("Index", ColNumber) },
                new[] { ("Value", ColString) },
                new Dictionary<string, string> { { "Index", "0" } });

            AddTemplate("Array.Length",   "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_length"),
                new[] { ("List", ColList) },
                new[] { ("Count", ColNumber) });

            AddTemplate("Array.Contains", "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_contains"),
                new[] { ("List", ColList), ("Value", ColString) },
                new[] { ("Result", ColBool) });

            AddTemplate("Array.Push",     "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_push"),
                new[] { ("Flow", ColExec), ("List", ColList), ("Value", ColString) },
                new[] { ("Done", ColExec), ("List", ColList) });

            AddTemplate("Array.Filter",   "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_filter"),
                new[] { ("List", ColList), ("Contains", ColString) },
                new[] { ("Filtered", ColList) });

            AddTemplate("Array.Sort",     "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_sort"),
                new[] { ("List", ColList), ("Numeric", ColBool) },
                new[] { ("Sorted", ColList) },
                new Dictionary<string, string> { { "Numeric", "false" } });

            AddTemplate("Array.Shuffle",  "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_shuffle"),
                new[] { ("List", ColList) },
                new[] { ("Shuffled", ColList) });

            AddTemplate("Array.Reverse",  "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_reverse"),
                new[] { ("List", ColList) },
                new[] { ("Reversed", ColList) });

            AddTemplate("Array.Unique",   "Collections", Color.SaddleBrown,
                Localizer.T("architect.node.bubble.array_unique"),
                new[] { ("List", ColList) },
                new[] { ("Unique", ColList) });

            // ── VALUES (literal constants) ───────────────────────────────
            // L6 — Value.* nodes already carry an inline editable "Value" attribute on the
            // node body (matches the UE-Blueprints rule). Tooltip text is reworded so it's
            // clear the attribute lives on the body, not in a side panel — the wired Input
            // socket still wins as a runtime override.
            AddTemplate("Value.String", "Values", Color.FromArgb(30, 120, 100),
                Localizer.T("architect.node.bubble.value_string"),
                new[] { ("Input", ColString) },
                new[] { ("Value", ColString) },
                new Dictionary<string, string> { { "Value", "" } });

            AddTemplate("Value.Int",    "Values", Color.FromArgb(30, 90, 140),
                Localizer.T("architect.node.bubble.value_int"),
                new[] { ("Input", ColNumber) },
                new[] { ("Value", ColNumber) },
                new Dictionary<string, string> { { "Value", "0" } });

            AddTemplate("Value.Float",  "Values", Color.FromArgb(30, 110, 105),
                Localizer.T("architect.node.bubble.value_float"),
                new[] { ("Input", ColFloat) },
                new[] { ("Value", ColFloat) },
                new Dictionary<string, string> { { "Value", "0.0" } });

            AddTemplate("Value.Bool",   "Values", Color.FromArgb(40, 110, 60),
                Localizer.T("architect.node.bubble.value_bool"),
                new[] { ("Input", ColBool) },
                new[] { ("Value", ColBool) },
                new Dictionary<string, string> { { "Value", "true" } });

            // ── TYPE CONVERSION ──────────────────────────────────────────
            // L6 — Convert.* templates carry an inline Default attribute on the node body.
            // The handler falls back to this Default when the In socket is unwired or the
            // upstream value is empty/unparseable — matches the UE-Blueprints inline-attribute
            // rule and saves users from having to wire a Value.* literal for the trivial case.
            AddTemplate("Convert.ToInt",    "Convert", Color.DarkCyan,
                Localizer.T("architect.node.bubble.convert_toint"),
                new[] { ("In", ColString) },
                new[] { ("Out", ColNumber) },
                new Dictionary<string, string> { { "Default", "0" } });

            // Convert.ToString In was Int-only, mismatching the sibling Convert.ToFloat
            // which already accepts Any. Promoting In → Any (ColObject) lets the same node
            // serialize floats, bools, lists and strings without an upstream coercion.
            // AreCompatible widens Int↔Float silently, so a Float wired into the old
            // Int-only socket would have been truncated invisibly. The lighter-touch fix
            // (vs editing engine-wide AreCompatible) lives in the tooltip below: an explicit
            // Float→String suggestion plus a note about silent widening loss.
            AddTemplate("Convert.ToString", "Convert", Color.DarkCyan,
                Localizer.T("architect.node.bubble.convert_tostring"),
                new[] { ("In", ColObject) },
                new[] { ("Out", ColString) },
                new Dictionary<string, string> { { "Default", "" } });

            AddTemplate("Convert.ToBool",   "Convert", Color.DarkCyan,
                Localizer.T("architect.node.bubble.convert_tobool"),
                new[] { ("In", ColString) },
                new[] { ("Out", ColBool) },
                new Dictionary<string, string> { { "Default", "false" } });

            AddTemplate("Convert.ToFloat",  "Convert", Color.DarkCyan,
                Localizer.T("architect.node.bubble.convert_tofloat"),
                new[] { ("In", ColObject) },
                new[] { ("Out", ColFloat) },
                new Dictionary<string, string> { { "Default", "0.0" } });
        }
    }
}
