using System;
using System.Globalization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// LayerGraphMigrator — idempotent upgrade pass for legacy <c>.phxlayer</c>
    /// node attributes whose canonical form has since changed.
    ///
    /// Runs at <see cref="LayerDocument.Open"/> time, before any code reads
    /// the nodes. Each migration is intentionally narrow (one node title +
    /// one or two attribute keys) so the migrator stays auditable and the
    /// blast radius for a buggy migration is small.
    ///
    /// RE-RUN SAFETY: <see cref="Migrate"/> is safe to call any
    /// number of times on the same <see cref="Layer"/> — every migration
    /// checks for the canonical form FIRST (see <see cref="SplitCsvVectorAttr"/>:
    /// it returns early when the legacy key is absent, and only drops the
    /// legacy key without rewriting when the canonical pair already exists).
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
    /// </summary>
    public static class LayerGraphMigrator
    {
        /// <summary>
        /// Walk every widget / trigger / node in <paramref name="layer"/> and
        /// apply registered migrations. Returns the number of attributes
        /// rewritten (useful for tests; callers can ignore).
        ///
        /// Idempotent: re-running on an already-canonical layer rewrites
        /// nothing and returns 0 (each migration checks the canonical form
        /// before touching attributes). Safe to call defensively from a save
        /// path — see the class remarks for the correct (Engine-side) call
        /// site and why it can't live in the Shared serializer.
        /// </summary>
        public static int Migrate(Layer? layer)
        {
            if (layer is null || layer.Widgets is null) return 0;
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
                        rewritten += MigrateParticlesEmit(node);
                    }
                }
            }
            return rewritten;
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
