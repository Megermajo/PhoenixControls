using System;
using System.Collections.Generic;
using System.Globalization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// LayerGraphMigrator — idempotent upgrade pass for legacy <c>.phxlayer</c>
    /// graphs: node attributes whose canonical form has since changed, plus the
    /// template back-fill that makes newly-appended sockets/attributes reachable
    /// on nodes that were saved before those pins existed.
    ///
    /// Runs at <see cref="LayerDocument.Open"/> time, before any code reads
    /// the nodes. Each attribute migration is intentionally narrow (one node title
    /// + one or two attribute keys) so the migrator stays auditable and the
    /// blast radius for a buggy migration is small. The template back-fill is the
    /// one deliberately GENERAL pass — see <see cref="BackfillFromTemplate"/>.
    ///
    /// RE-RUN SAFETY: <see cref="Migrate"/> is safe to call any
    /// number of times on the same <see cref="Layer"/> — every step
    /// checks for the canonical form FIRST (see <see cref="SplitCsvVectorAttr"/>:
    /// it returns early when the legacy key is absent, and only drops the
    /// legacy key without rewriting when the canonical pair already exists;
    /// and <see cref="BackfillFromTemplate"/>, which tests each socket name /
    /// attribute key for presence before adding it).
    /// On an already-canonical layer the pass is a pure O(n) read with no
    /// mutation, so a defensive re-migrate in a save path is a no-op on
    /// healthy data and a quiet repair on a layer that was mutated back into
    /// legacy shape post-load. It was historically described as "one-shot"
    /// only because <c>Open</c> is the single current call site — that is a
    /// scheduling fact, not a correctness constraint.
    ///
    /// CALL-SITE NOTE: the natural defensive re-migrate point is the Engine
    /// save path — <see cref="LayerDocument.Save"/> / its auto-save sibling,
    /// which can both see this type. It must NOT be wired into
    /// <c>LayerSerializer.Serialize</c>: that lives in
    /// <c>Phoenix.Controls.Shared</c>, which is referenced BY this Engine
    /// assembly and so cannot reference back into it (doing so would be a
    /// dependency-direction inversion that won't compile).
    ///
    /// <c>Particles.Emit</c> previously persisted its
    /// <c>Position</c> and <c>Velocity</c> Vector2 attributes as a single
    /// comma-CSV string ("0.5, 0.5"). That form didn't round-trip through
    /// <see cref="AnimatedPinRegistry.ReadComponentLiteral"/>, which expects
    /// a per-component scalar attribute (<c>PositionX</c> / <c>PositionY</c>
    /// matching <c>&lt;SocketName&gt;&lt;Axis&gt;</c>). The template now
    /// declares the per-component shape; this migrator splits the legacy
    /// CSV form into the canonical pair on load. Legacy graphs with neither
    /// form fall through unchanged so a half-edited file doesn't lose data.
    ///
    /// TEMPLATE BACK-FILL — why this class stopped being attribute-only.
    /// Architect re-syncs a loaded graph against the current catalog in
    /// <c>GraphSerializer.MigrateNodes</c>; Visualist had no equivalent, so a
    /// socket or attribute added to a widget template reached freshly-dropped nodes
    /// only. On every layer that already existed the new pin simply did not exist:
    /// the editor renders <c>node.Sockets</c> as serialised, and the WinUI detail
    /// panel builds its rows from <c>node.Attributes</c> rather than from the
    /// template, so a saved <c>Timer.Remaining</c> kept showing exactly Text +
    /// State no matter what the template grew. That is the whole sprint defeated in
    /// one line — the progress bar the new Progress pin exists to enable cannot be
    /// wired in any layer a streamer already authored, which is every layer that
    /// matters. <see cref="BackfillFromTemplate"/> closes that gap: additive only,
    /// output sockets plus attribute keys plus the title-allowlisted input append
    /// (<see cref="InputBackfillTitles"/>), and a node whose title is not in the
    /// catalog is left completely alone.
    /// </summary>
    public static class LayerGraphMigrator
    {
        /// <summary>
        /// The ONLY node titles whose missing template INPUT sockets are back-filled — see
        /// <see cref="BackfillFromTemplate"/> for why inputs are excluded everywhere else.
        ///
        /// V7 appended a wirable String <c>Path</c> input to the three local-file loaders, and
        /// that pin is the entire capability of the sprint: without a back-fill it exists only
        /// on nodes spawned from the palette AFTER the upgrade, so a streamer who opens the
        /// alerts layer they already authored selects its <c>Image.Load</c> and finds no Path
        /// pin to wire a <c>String.Select</c> into. Every layer that matters is a legacy layer.
        ///
        /// TITLE-ALLOWLISTED rather than general, following the same narrow per-title idiom
        /// <c>MigrateParticlesEmit</c> uses: the global input exclusion protects
        /// <c>WebOverlay.Custom</c>'s eight renameable String slots (their names are CSS
        /// custom-property names and the editor lets authors rename them, so appending
        /// "missing" template inputs would resurrect a phantom "slot3" beside an author's
        /// renamed "accent"). The loaders carry no rename affordance and exactly one input
        /// between them, so the blast radius is a single known pin per title.
        ///
        /// Adding a title here is a deliberate act: confirm the template's inputs are
        /// FIXED-NAME (never author-editable) before doing it.
        ///
        /// V13 admitted <c>Visual.Complete</c> against exactly that criterion. Its
        /// appended <c>Payload</c> String input is fixed-name — the template declares
        /// <c>In</c> + <c>Payload</c> and no surface in Visualist renames a widget-node
        /// socket (the rename affordance the exclusion exists for is
        /// <c>WebOverlay.Custom</c>'s and only <c>WebOverlay.Custom</c>'s) — so the same
        /// two-line append that reaches a legacy <c>Image.Load</c> is correct here.
        /// It is also the same whole-sprint-defeated shape V7 hit: the canvas renders
        /// <c>node.Sockets</c> as serialised and Hub/compositor read the <c>.phxlayer</c>
        /// graph RAW (this migrator never runs in Hub), so without the back-fill the
        /// completion payload — the headline of V13 — exists only on nodes spawned from
        /// the palette after the upgrade. Every layer that matters is a legacy layer.
        /// </summary>
        private static readonly HashSet<string> InputBackfillTitles =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Image.Load", "Video.Load", "Audio.Load",
                "Visual.Complete",
            };

        /// <summary>
        /// Walk every widget / trigger / node in <paramref name="layer"/> and
        /// apply registered migrations plus the template back-fill. Returns the
        /// number of repairs applied — attributes rewritten, sockets appended and
        /// attribute keys back-filled (useful for tests; callers can ignore).
        ///
        /// Idempotent: re-running on an already-canonical, already-back-filled layer
        /// changes nothing and returns 0 (every step checks the canonical form
        /// before touching anything). Safe to call defensively from a save
        /// path — see the class remarks for the correct (Engine-side) call
        /// site and why it can't live in the Shared serializer.
        /// </summary>
        public static int Migrate(Layer? layer)
        {
            if (layer is null || layer.Widgets is null) return 0;

            // The back-fill reads the widget catalog, so make sure it is populated.
            // RegisterAll is genuinely idempotent (it no-ops when the registry
            // already holds templates), so this is a flag test on every call after
            // Visualist startup — and on the paths that DON'T go through startup
            // (a headless load, a test, a future Hub-side repair tool) it is the
            // difference between back-filling and silently doing nothing. Deriving
            // the pass from an empty registry would fail exactly the way the missing
            // migration failed: quietly, on every already-saved layer.
            NodeTemplates.RegisterAll();

            int rewritten = 0;
            foreach (var widget in layer.Widgets)
            {
                if (widget?.Triggers is null) continue;
                foreach (var trigger in widget.Triggers)
                {
                    var graph = trigger?.Graph;
                    if (graph?.Nodes is null) continue;
                    foreach (var node in graph.Nodes)
                    {
                        if (node is null) continue;
                        // ORDER IS LOAD-BEARING: the narrow attribute migrations run
                        // FIRST, the template back-fill LAST. Reverse them and the
                        // back-fill stamps the template's default PositionX/PositionY
                        // onto a legacy CSV node, after which MigrateParticlesEmit
                        // sees the canonical pair already present and just drops the
                        // legacy key — silently replacing the author's emitter
                        // position with 0.5, 0.5. Any future migration that rewrites
                        // a key the template also declares belongs above this line
                        // for the same reason.
                        rewritten += MigrateParticlesEmit(node);
                        rewritten += BackfillFromTemplate(node);
                    }
                }
            }
            return rewritten;
        }

        /// <summary>
        /// Re-syncs one saved node against its current template — the Visualist
        /// counterpart of Architect's <c>GraphSerializer.MigrateNodes</c>, deliberately
        /// reduced to the two operations that cannot lose author data:
        ///
        ///   • append every template OUTPUT socket whose Name the node doesn't have;
        ///   • add every template attribute KEY the node doesn't have.
        ///
        /// Everything it does NOT do is the point:
        ///
        /// NEVER RENAMES OR REORDERS. Sockets are addressed by Id in
        /// <c>Graph.Links</c> and by NAME in both readers (compositor.js and the C#
        /// mirror dispatch on <c>socket.Name</c>). Renaming a socket therefore prunes
        /// the socket AND every wire attached to it in every saved .phxlayer, silently
        /// — Architect's socket-rename-prune trap, ported. Appending is the only shape
        /// of change that leaves existing Ids, existing order and existing links
        /// untouched, which is why link safety here needs no link bookkeeping at all:
        /// we add sockets, we never touch <c>Socket.Id</c>, and we never remove one, so
        /// no <c>Link.FromSocketId</c> / <c>ToSocketId</c> can be left dangling.
        ///
        /// NEVER TOUCHES INPUT SOCKETS — EXCEPT FOR AN EXPLICIT TITLE ALLOWLIST.
        /// <c>WebOverlay.Custom</c>'s eight String inputs are USER DATA: their names are the
        /// CSS custom-property names and the editor lets authors rename them, so appending
        /// missing template inputs in general would resurrect a phantom "slot3" next to the
        /// author's renamed "accent" on every overlay ever built. That is the reason for the
        /// exclusion — the ONLY reason. It is not that inputs are harder to append than
        /// outputs (the code below is the same three lines with a different SocketType), and
        /// it is not that this class is attribute-only (it back-fills output sockets and
        /// attribute keys, and did before V7).
        ///
        /// So the exclusion is scoped to what actually needs protecting: titles in
        /// <see cref="InputBackfillTitles"/> — the three local-file loaders, whose single
        /// <c>Path</c> input is fixed-name and un-renameable, plus V13's
        /// <c>Visual.Complete</c>, whose appended <c>Payload</c> input is likewise
        /// fixed-name — DO get their missing template inputs appended, and everything else
        /// still does not. Without that, V7's wirable Path and V13's completion Payload
        /// exist only on freshly spawned nodes and are unreachable on every layer a
        /// streamer already authored.
        ///
        /// NEVER OVERWRITES A VALUE. Only absent attribute keys are added, so an
        /// author's edited Format / PreviewText / Size survives untouched. The value
        /// added is the TEMPLATE default, which is authoritative — a browser reader's
        /// inline <c>attr(node, 'X', fallback)</c> default only ever existed to cover a
        /// key that isn't there.
        ///
        /// UNKNOWN TITLE ⇒ UNTOUCHED. A node whose Title is not in the catalog (a
        /// retired template, a hand-edited file, a graph from a newer build) returns
        /// immediately. There is nothing authoritative to sync against, and inventing
        /// sockets for it would be strictly worse than leaving it as saved.
        ///
        /// IDEMPOTENCE comes from the same check-canonical-form-first idiom the
        /// attribute migrations use: presence is tested before every single add, so the
        /// second run finds every name and key present and returns 0. Nothing here
        /// derives from a counter, a version stamp or a file flag — the graph itself is
        /// the state, which is what makes a defensive re-run free.
        ///
        /// Returns the number of sockets + attribute keys added.
        /// </summary>
        private static int BackfillFromTemplate(Node node)
        {
            if (string.IsNullOrWhiteSpace(node.Title)) return 0;
            var tmpl = WidgetNodeRegistry.Get(node.Title);
            if (tmpl is null) return 0;   // Not in the catalog → not ours to reshape.

            int added = 0;

            if (tmpl.Outputs is { Count: > 0 })
            {
                // Defensive: the model defaults to an empty list, but a hand-edited
                // .phxlayer carrying "sockets": null deserialises to a null here.
                if (node.Sockets is null) node.Sockets = new List<Socket>();
                foreach (var spec in tmpl.Outputs)
                {
                    if (string.IsNullOrEmpty(spec.Name)) continue;
                    if (HasSocketNamed(node, spec.Name)) continue;
                    // Mirrors WidgetNodeRegistry.Instantiate's output-socket shape
                    // exactly — fresh Guid Id (from the Socket initialiser), Type =
                    // Output, DataType straight off the template. DataType-at-creation
                    // is what drives the pin's shape/colour and its wire compatibility,
                    // so a back-filled pin must be indistinguishable from one on a
                    // freshly-dropped node.
                    node.Sockets.Add(new Socket
                    {
                        Name     = spec.Name,
                        Type     = SocketType.Output,
                        DataType = spec.DataType,
                    });
                    added++;
                }
            }

            // The allowlisted INPUT append — see InputBackfillTitles for the allowlist and
            // why the global exclusion stays. Same additive rules as the outputs above: name
            // absent ⇒ append, existing Socket.Ids never touched, nothing renamed or removed,
            // so no Link can be left dangling. Appending also keeps the pin LAST in the node's
            // input order, which is what the template itself does (V7 appended Path rather
            // than inserting it) — so a back-filled node and a freshly spawned one render
            // their pins in the same order.
            if (tmpl.Inputs is { Count: > 0 } && InputBackfillTitles.Contains(node.Title))
            {
                if (node.Sockets is null) node.Sockets = new List<Socket>();
                foreach (var spec in tmpl.Inputs)
                {
                    if (string.IsNullOrEmpty(spec.Name)) continue;
                    if (HasSocketNamed(node, spec.Name)) continue;
                    node.Sockets.Add(new Socket
                    {
                        Name     = spec.Name,
                        Type     = SocketType.Input,
                        DataType = spec.DataType,
                    });
                    added++;
                }
            }

            if (tmpl.DefaultAttributes is { Count: > 0 })
            {
                // Same defensive null as above ("attributes": null in a hand-edited file).
                if (node.Attributes is null) node.Attributes = new Dictionary<string, string>();
                foreach (var kv in tmpl.DefaultAttributes)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (node.Attributes.ContainsKey(kv.Key)) continue;   // canonical form wins
                    node.Attributes[kv.Key] = kv.Value;
                    added++;
                }
            }

            return added;
        }

        /// <summary>
        /// True when the node already carries a socket with this name — checked across
        /// ALL sockets, not just outputs, and case-insensitively.
        ///
        /// Both widenings are deliberate near-duplicate guards. A name is the reader's
        /// dispatch key, so two sockets sharing one name (an input "Out" plus a
        /// back-filled output "Out", or "state" plus "State") make dispatch ambiguous
        /// and are worse than a missing pin. No shipped widget template reuses a name
        /// across its own inputs and outputs — <c>LayerGraphMigratorBackfillTests</c>
        /// asserts that over the whole catalog — so on real templates this check is
        /// exactly "is this output present?", and the widening only ever suppresses an
        /// add on data that is already malformed. A differently-cased legacy socket
        /// stays as the author's data: this pass appends, it never renames.
        /// </summary>
        private static bool HasSocketNamed(Node node, string name)
        {
            if (node.Sockets is null) return false;
            foreach (var s in node.Sockets)
            {
                if (s is null) continue;
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Legacy "0.5, 0.5" CSV → PositionX="0.5", PositionY="0.5"
        // (and same for Velocity). The split only fires when:
        //   • The node title is Particles.Emit.
        //   • The old key still exists.
        //   • The new key does NOT already exist (so we never clobber a
        //     value an editor or test already wrote in canonical form).
        private static int MigrateParticlesEmit(Node node)
        {
            if (!string.Equals(node.Title, "Particles.Emit", StringComparison.OrdinalIgnoreCase))
                return 0;
            int rewritten = 0;
            rewritten += SplitCsvVectorAttr(node, "Position", "PositionX", "PositionY");
            rewritten += SplitCsvVectorAttr(node, "Velocity", "VelocityX", "VelocityY");
            return rewritten;
        }

        // Splits an "x, y" attribute into two per-component scalar attributes
        // and removes the legacy key. Tolerates missing whitespace / extra
        // whitespace; unparsable components default to 0. The legacy attribute
        // is dropped on success so the next Save writes only the canonical
        // form.
        private static int SplitCsvVectorAttr(Node node, string legacyKey, string xKey, string yKey)
        {
            if (node.Attributes is null) return 0;
            if (!node.Attributes.TryGetValue(legacyKey, out var raw) || string.IsNullOrEmpty(raw)) return 0;
            // Don't overwrite if either canonical key is already populated —
            // that means a downstream editor already wrote the new form and
            // the legacy key is residual; just drop the legacy key.
            bool hasNew = node.Attributes.ContainsKey(xKey) || node.Attributes.ContainsKey(yKey);
            if (hasNew)
            {
                node.Attributes.Remove(legacyKey);
                return 1;
            }
            var parts = raw.Split(',');
            double x = parts.Length > 0 && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var px) ? px : 0;
            double y = parts.Length > 1 && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var py) ? py : 0;
            node.Attributes[xKey] = x.ToString(CultureInfo.InvariantCulture);
            node.Attributes[yKey] = y.ToString(CultureInfo.InvariantCulture);
            node.Attributes.Remove(legacyKey);
            return 1;
        }
    }
}
