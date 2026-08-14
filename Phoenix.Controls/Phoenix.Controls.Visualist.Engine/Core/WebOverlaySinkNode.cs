using System;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// WebOverlaySinkNode — identity + contract constants for the
    /// <c>WebOverlay.Custom</c> node: a side-effecting DOM-overlay sink, sibling to
    /// <see cref="DisplaySinkNode"/> / <see cref="AudioSinkNode"/>.
    ///
    /// It mounts author-written HTML+CSS as a live, natively-animated element in
    /// compositor.js's <c>#dom-overlay</c> track, positioned over the widget rect
    /// (Path B — the browser composites/animates the DOM over the canvas; nothing is
    /// rasterised into the Image pipeline, which is why real CSS animation works and
    /// why it cannot be Blend/Mask-composited with canvas image nodes). Its
    /// <see cref="SlotCount"/> String inputs are injected as named CSS custom
    /// properties (<c>--&lt;socketName&gt;</c>) so an Architect script can drive the
    /// styling at runtime through the EXISTING <c>Visual.Trigger</c> eventData channel
    /// — no Architect-side command is added, so the pillar-separation rule is intact.
    ///
    /// Unlike Display it is NOT auto-injected, has NO output socket (it is terminal),
    /// and is discovered/visited by title in compositor.js — mirroring the
    /// <c>Audio.Play</c> side-effect-sink pass. The runtime lives entirely in
    /// compositor.js (<c>evalWebOverlay</c>); the C# side only registers the template
    /// + this identity so the Add-Node catalog, the one-per-trigger guard, and the
    /// design-time <see cref="NodeEvaluator"/> mirror all agree on the same contract.
    ///
    /// The <see cref="SlotCount"/> input socket NAMES are user data (the CSS variable
    /// names) — renameable in the editor's pop-up. Visualist's load path does NOT
    /// re-sync socket names from the template (see <see cref="LayerGraphMigrator"/>,
    /// which only migrates narrow attributes), so renamed slots persist as serialised
    /// — the Architect socket-rename-prune trap does not apply here.
    /// </summary>
    public static class WebOverlaySinkNode
    {
        public const string Title    = "WebOverlay.Custom";
        public const string Category = "Sink";

        /// <summary>Number of String input slots exposed as CSS custom properties.</summary>
        public const int SlotCount = 8;

        /// <summary>Attribute key holding the author's HTML fragment (raw string, not JSON-quoted).</summary>
        public const string HtmlAttr = "Html";

        /// <summary>Attribute key holding the author's CSS text (raw string, not JSON-quoted).</summary>
        public const string CssAttr = "Css";

        /// <summary>Default name for slot N (1-based): "slot1".."slot8". Also the default CSS var name.</summary>
        public static string DefaultSlotName(int oneBasedIndex) => "slot" + oneBasedIndex;

        /// <summary>Starter HTML seeded on a freshly-dropped node (author-editable in the pop-up).</summary>
        public const string DefaultHtml = "<div class=\"phx-overlay\">WebOverlay</div>";

        /// <summary>
        /// Starter CSS seeded on a freshly-dropped node — centres the content and colours it
        /// by the first slot so a bare drop renders something immediately and demonstrates the
        /// <c>--slot</c> injection. Authors replace it wholesale.
        /// </summary>
        public const string DefaultCss =
@".phx-overlay {
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  font: 700 40px/1.1 system-ui, sans-serif;
  color: var(--slot1, #ffffff);
}";

        public static bool Is(Node? node) =>
            node is not null
            && string.Equals(node.Title, Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.Category, Category, StringComparison.OrdinalIgnoreCase);
    }
}
