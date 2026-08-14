using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: retired command names kept registered as explicit NO-OP SHIMS —
    // the five overlay-addressing commands V4 part C retired, plus the 22 tool-band
    // wrapper commands the 2026-08 tool-node cut retired (points.* / counter.* /
    // quote.* / timer.subtract + the three timer reads / rank.value + rank.top).
    //
    // ── Why they are still here at all ──────────────────────────────────────
    // Their nodes, exporter descriptors, palette entries, prose and lang bubbles are
    // gone and stay gone — a second per-widget addressing model beside the Overlay
    // Live Channel is exactly what this campaign removes. What could NOT go with them
    // is the COMMAND NAME, because a name already written into a `.phx` on disk
    // outlives the graph it came from:
    //
    //   * hand-authored `.phx` files. The manifesto documents hand-authoring, so there
    //     is no `.phxg` for the Architect-side migration to rewrite.
    //   * generated `.phx` files whose `.phxg` has not been re-opened since. The
    //     migration in GraphSerializer.DropRetiredVisualNodes only runs on graph LOAD
    //     and the `.phx` is only rewritten on graph SAVE, so a graph nobody opens
    //     keeps shipping its old `.phx` indefinitely.
    //
    // Without a registration, every such call falls into ScriptEngine's unknown-command
    // path, which logs at LogLevel.CriticalError — and LiveFeedViewModel filters on
    // exactly that level and appends a RED ERROR ROW. The streamer would get a red row
    // mid-stream, on every invocation, forever, for a script whose behaviour did not
    // change: all five commands had shipped INERT (compositor.js never had a handler
    // for STEP/chat_push, STEP/chat_clear, SET_TEXT, VISUAL_SET_VISIBLE or
    // VISUAL_SET_PROPERTY), so doing nothing is bit-for-bit what they already did.
    //
    // The precedent is the codebase's own: process.spawn / process.terminate stayed
    // registered as routed aliases (ScriptManager.System.cs) when their owners folded
    // away, rather than being hard-removed. Same shape here, minus the routing — there
    // is nothing to route to.
    //
    // ── Why a file of its own ───────────────────────────────────────────────
    // Not ScriptManager.Overlay.cs: that file documents the ONE supported data path
    // (overlay.publish / overlay.get + a widget-side Var.Live binding), and folding
    // five tombstones into it would blur the very distinction the campaign is drawing.
    // Not ScriptManager.Chat.cs or ScriptManager.Visual.cs either — the five span BOTH
    // of those former homes, so neither can hold all of them, and splitting them 2/3
    // would mean two copies of this comment and two once-per-file ledgers. A dedicated
    // file gives them one comment, one ledger, one greppable population — and makes the
    // eventual real removal a file delete plus five manifest lines.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        /// <summary>
        /// The retired command names, each paired with the sentence naming what replaced
        /// it. Order is the order they were declared in the manifest, so the two lists
        /// read against each other.
        /// </summary>
        private static readonly (string Command, string WasFor)[] RetiredOverlayCommands =
        {
            ("chat.overlay.push",  "pushing a chat line at an on-overlay chat widget"),
            ("chat.overlay.clear", "clearing an on-overlay chat widget"),
            ("visual.set_text",    "setting a widget's text"),
            ("visual.set_visible", "showing or hiding a widget"),
            ("visual.set_property","setting a widget parameter"),
        };

        /// <summary>
        /// The 22 tool-band wrapper commands the 2026-08 tool-node cut retired, each
        /// paired with the generic replacement recipe its hint names. Unlike the
        /// overlay five above, these DID work — they wrapped OPEN tables / service
        /// state the generic surface reaches directly — so the hint leads with the
        /// replacement rather than with "this never worked". A write shim doing
        /// nothing IS a behaviour change for a stale .phx, which is exactly why the
        /// hint is logged: re-exporting the graph from Architect (whose migration
        /// drops the retired nodes) or applying the named recipe is the fix.
        /// </summary>
        private static readonly (string Command, string Replacement)[] RetiredToolCommands =
        {
            // Loyalty — the balance + ledger are OPEN tables.
            ("points.add",         "db.find_row + db.increment_cell on the balance table (add a ledger row with db.insert_row if you want the audit trail)"),
            ("points.remove",      "db.find_row + db.increment_cell with a negative amount (clamp with logic.if if you need the floor at 0)"),
            ("points.set",         "db.find_row + db.set_cell on the balance table"),
            ("points.give",        "two db.increment_cell writes (debit + credit) on the balance table"),
            ("points.get_balance", "db.find_row + db.get_cell on the balance table"),
            ("points.top",         "db.top(balance table, currency, name, count)"),
            // Counters — the counts live in the OPEN \"Counters\" table; the 15 s
            // sweep keeps the overlay + Counter.OnChanged honest for db writes.
            ("counter.get",        "db.find_row + db.get_cell on the \"Counters\" table"),
            ("counter.set",        "db.find_row + db.set_cell on the \"Counters\" table"),
            ("counter.add",        "db.find_row + db.increment_cell on the \"Counters\" table"),
            ("counter.reset",      "db.find_row + db.set_cell(0) on the \"Counters\" table"),
            ("counter.delete",     "db.find_row + db.delete_row on the \"Counters\" table"),
            // Quotes — the OPEN "Quotes" table.
            ("quote.get",          "db.* reads on the \"Quotes\" table (db.get_column + array.shuffle for a random pick)"),
            ("quote.add",          "db.insert_row on the \"Quotes\" table"),
            ("quote.edit",         "db.find_row + db.set_cell on the \"Quotes\" table"),
            ("quote.delete",       "db.find_row + db.delete_row on the \"Quotes\" table"),
            ("quote.count",        "db.row_count(\"Quotes\")"),
            // Timer — timer.add is signed now; the reads ride the live channel.
            ("timer.subtract",     "timer.add with a negative amount (e.g. -90s)"),
            ("timer.get_formatted","overlay.get(\"timer.<name>.short\") (or .long / .clock)"),
            ("timer.get_paused",   "timer.get_state == \"Paused\""),
            ("timer.get_progress", "overlay.get(\"timer.<name>.progress\")"),
            // Ranks — the WatchTime / balance tables are OPEN. The hints name BOTH
            // metric tables because the ladder is configurable: a points-metric
            // streamer following a WatchTime-only recipe would board an empty table.
            ("rank.value",         "db.find_row + db.get_cell on the ladder's metric table — \"WatchTime\" (minutes) or the Loyalty balance table (currency), per the Ranks panel"),
            ("rank.top",           "db.top on the ladder's metric table — db.top(\"WatchTime\", minutes, name, count), or the balance table with its currency column, per the Ranks panel"),
        };

        /// <summary>
        /// The retired tool-command names as a set — the exemption ledger the reverse
        /// manifest audit (<see cref="VerifyHubCommandsAgainstManifest"/>) consults.
        /// These 22 are engine-registered (the shims above) but deliberately ABSENT
        /// from <see cref="CommandManifest"/>: the manifest is the contract for the
        /// LIVE command surface, and re-listing retired names there would resurrect
        /// them in every surface that enumerates it. The audit stays a hard-stop for
        /// every name outside this ledger. (The five overlay shims took the other
        /// route — they kept their manifest entries — which is why they need no
        /// exemption; two patterns, one per retirement wave, both load-bearing.)
        /// </summary>
        internal static readonly HashSet<string> RetiredToolCommandNames =
            new(RetiredToolCommands.Select(c => c.Command), StringComparer.Ordinal);

        private void RegisterRetiredCommands() => RegisterRetiredCommandsOn(_engine);

        /// <summary>
        /// Registers all five shims against an explicit engine. Internal so the test
        /// suite can bind its own engine — and, more importantly, so the once-per-script
        /// ledger is scoped to ONE registration call instead of to the process: each call
        /// creates its own ledger, which is why there is no static state here and no
        /// reset seam for tests to remember to call.
        /// </summary>
        internal static void RegisterRetiredCommandsOn(ScriptEngine engine)
        {
            if (engine is null) throw new ArgumentNullException(nameof(engine));

            // ONE ledger shared by all five closures, keyed "<command>\0<scriptFile>".
            //
            // Per (command, script) rather than per script: a script that calls two
            // different retired commands has two different migrations to do, and each
            // message names its own replacement. Per INVOCATION is the thing this fix
            // exists to avoid — a hint logged on every call is the same noise problem as
            // the CriticalError row, just one severity tier quieter.
            //
            // Locked because scripts execute concurrently (MaxConcurrentChatScripts /
            // MaxConcurrentWebhookScripts both admit several at once), so two chat
            // handlers can reach the same shim on different threads. The GlobalLogger
            // call is made OUTSIDE the lock — the logger takes its own locks and fans out
            // to synchronous subscribers, and holding ours across that is a lock-ordering
            // trap (same rule as OverlayLiveStore.ClaimOnceUnlocked).
            var notedGate = new object();
            var noted = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (command, wasFor) in RetiredOverlayCommands)
            {
                // Copy into locals: the tuple deconstruction is per-iteration in C# 5+,
                // but naming them makes the capture obvious to a reader.
                string name = command;
                string purpose = wasFor;

                engine.RegisterCommand(name, async (args) =>
                {
                    string script = ScriptDisplayNameFor(engine);

                    bool first;
                    lock (notedGate) first = noted.Add(name + "\0" + script);

                    if (first)
                        GlobalLogger.Log(
                            $"Script '{script}' calls the retired command '{name}' ({purpose}) — accepted and ignored. " +
                            "This command never reached the browser (compositor.js has no handler for its messages) and " +
                            "was deleted from the node palette; it is kept as a no-op so the script keeps running " +
                            "unchanged. Publish a key with Overlay.Publish and bind it widget-side (Var.Live) to drive a " +
                            "widget parameter from a script. Logged once per script file.",
                            "Script", LogLevel.System);

                    // No result var, no side effect, no fault: identical to what the real
                    // handlers achieved, and deliberately NOT an error return — a shim that
                    // sets result.* would hand a script a new branch to read, which is a
                    // behaviour change rather than the compatibility floor this is.
                    return null;
                });
            }

            foreach (var (command, replacement) in RetiredToolCommands)
            {
                string name = command;
                string fix = replacement;

                engine.RegisterCommand(name, async (args) =>
                {
                    string script = ScriptDisplayNameFor(engine);

                    bool first;
                    lock (notedGate) first = noted.Add(name + "\0" + script);

                    if (first)
                        GlobalLogger.Log(
                            $"Script '{script}' calls the retired command '{name}' — accepted and ignored. " +
                            $"The tool wrapper commands were removed in the tool-node cut; use {fix} instead. " +
                            "Re-opening and re-exporting the graph in Architect migrates it automatically. " +
                            "Logged once per script file.",
                            "Script", LogLevel.System);

                    // quote.get was the one retired MULTI-OUTPUT read: its exporter
                    // hoisted quote.get(<Number>, "_qg_<id6>") and the downstream sockets
                    // read {_qg_x_text} / {_qg_x_name} / {_qg_x_number}. A shim that only
                    // returned "" would leave those tokens UNSUBSTITUTED — a stale .phx
                    // would post the literal "{_qg_a1b2c3_text}" into chat on every
                    // !quote — so the shim honours the result-base contract with empty
                    // values, completing the promised empty degrade. (Every other retired
                    // read was single-output inline; the "" return covers them.)
                    if (name == "quote.get")
                    {
                        string baseVar = StripBareQuotes(args != null && args.Length > 1 ? args[1] : "").Trim();
                        if (baseVar.Length > 0)
                        {
                            engine.SetLocalResultVar($"{baseVar}_text", "");
                            engine.SetLocalResultVar($"{baseVar}_name", "");
                            engine.SetLocalResultVar($"{baseVar}_number", "");
                        }
                    }

                    // Reads return "" (the standard empty-answer degrade every db.* read
                    // uses) so an inline consumer gets an empty value instead of the
                    // literal call text; writes return null like every void command.
                    return name.EndsWith(".get", StringComparison.Ordinal)
                        || name.Contains(".get_", StringComparison.Ordinal)
                        || name is "points.top" or "rank.value" or "rank.top" or "quote.count"
                        ? "" : null;
                });
            }
        }

        /// <summary>
        /// The script identity used both as the ledger key and in the message. Falls back
        /// to a visible sentinel rather than an invented name when the engine has no
        /// ScriptFile (reachable only from a hand-driven engine — a test harness, or a
        /// command invoked outside ExecuteScriptAsync), on the same reasoning as
        /// <see cref="UnknownScriptSource"/>.
        /// </summary>
        private static string ScriptDisplayNameFor(ScriptEngine engine)
        {
            string file = (engine.ScriptFile ?? string.Empty).Trim();
            return file.Length == 0 ? "(unknown script)" : file;
        }
    }
#pragma warning restore CS1998
}
