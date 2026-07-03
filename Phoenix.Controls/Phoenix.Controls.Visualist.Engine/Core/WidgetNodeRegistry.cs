using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// WidgetNodeRegistry — Phase 4 / Phase 5 catalog of node templates available
    /// inside the per-widget node editor.
    ///
    /// Pruned to **visual + math only** (no DB / Bus / Twitch / AI / HTTP / System nodes;
    /// those live in Architect's NodeRegistry and are off-limits for visual graphs).
    ///
    /// Phase 4 lands the registry skeleton + categories; Phase 5 populates with concrete templates.
    /// </summary>
    public static class WidgetNodeRegistry
    {
        public sealed record SocketSpec(string Name, SocketDataType DataType);
        public sealed record NodeTemplate(
            string Title,
            string Category,
            IReadOnlyList<SocketSpec> Inputs,
            IReadOnlyList<SocketSpec> Outputs,
            IReadOnlyDictionary<string, string> DefaultAttributes);

        // ─────────────────────────────────────────────────────────────────────
        // Allowed-category drift note.
        //
        // the project docs currently documents the visual-side spec as:
        //     AllowedCategories = { Inputs, Image, Math, Vector, Triggers, Sink, Debug }
        //
        // "Text" was added because Caption.LiveCaption + Text.Render need it
        // to render captioned/translated streams without forcing them through the
        // Inputs bucket. The category is intentional and stays. Update the project docs
        // spec accordingly in next doc pass; until then this comment is the
        // canonical justification so future sweeps don't quietly remove it.
        //
        // Caption.LiveCaption was inadvertently registered
        // under "Inputs" instead of "Text"; the registration has now been moved
        // to "Text" in NodeTemplates.cs so the catalog matches this docstring.
        // Text.Render / Text.Translate were already correctly "Text".
        //
        // Do NOT remove "Text" from this set without first revising the docs and
        // confirming no widget-side caption / text-render template depends on it.
        // ─────────────────────────────────────────────────────────────────────
        // Allowed categories — anything not in this set is rejected by Add().
        public static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "Inputs",
            "Image",
            "Math",
            "Vector",
            // "Triggers" is shared between Architect and Visualist by name only.
            // Architect: flow-driven — Triggers fire scripts.
            // Visualist: tab/pull model — Triggers are subscriptions widgets pull from.
            // Same category name, different runtime semantics. See the
            // allowed-category drift note above for the
            // companion rationale covering this set as a whole.
            "Triggers",
            "Sink",
            "Debug",
            "Text",
            // Widget-local pure-data bands. Strictly visual/math:
            //   "Time"    — design-time clock-driven scalars (Oscillator, Easing, …).
            //   "String"  — string transforms (Concat, Upper, Slice, …).
            //   "Convert" — type bridges (NumberToString, HexToColor, …).
            // None overlap ForbiddenCategories below; all stay widget-local with
            // no DB / Bus / Twitch / AI / HTTP / System reach.
            "Time",
            "String",
            "Convert",
        };

        // Categories that MUST NOT appear (Architect's domain).
        //
        // Aligned to the actual Architect category names — the prior list
        // ({DB, Database, Twitch, HTTP, Streamer.bot}) was authored against a different
        // naming scheme and only "Bus" / "AI" / "System" ever matched anything real.
        // Cross-referenced against `Category =` in NodeRegistry.Templates.*.cs:
        //   * "Databank"     — all DB.* (NodeRegistry.Templates.Databank.cs)
        //   * "Twitch Data"  — Twitch.Get*/Check*/LastActive (RemainingBands.cs)
        //   * "Platforms"    — Twitch actions, Discord, HTTP, File, Audio, Streamer.bot
        //   * "AI"           — AI.* (RemainingBands.cs)
        //   * "Bus" / "System" / "Events" — covered by their existing Architect names
        // The check stays inert today because Architect templates never reach the
        // Visualist WidgetNodeRegistry.Add() — but if a future unified-search ever
        // pipes them through, the safety net is now spelled correctly.
        public static readonly HashSet<string> ForbiddenCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "Databank", "Bus", "Twitch Data", "AI", "Platforms", "System", "Events",
        };

        private static readonly Dictionary<string, NodeTemplate> _templates =
            new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyDictionary<string, NodeTemplate> Templates => _templates;

        // ─────────────────────────────────────────────────────────────────────
        // Per-template tooltip overrides.
        //
        // The NodeTemplate record carries no Tooltip/Description field today, but
        // some templates need extra warnings surfaced to the editor (and to tests).
        // Renderers / detail panels can opt in by looking up the title here; tests
        // assert against this dictionary directly.
        //
        // Caption.LiveCaption is single-stream by construction (one global
        // LiveCaptionService feed). Stacking multiple Caption.LiveCaption widgets
        // in the same layer leads to undefined "which widget wins" behaviour.
        // Surface that as a hard warning so authors see it before they ship a
        // misconfigured layer to OBS.
        // ─────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> _tooltips =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ── Inputs ────────────────────────────────────────────────
                // Tooltip text moved to Localizer locale files. Alpha
                // guidance for Image.Load / Image.LoadUrl / Video.Load now lives
                // in those locale resources rather than inline strings — see the
                // visualist.node.bubble.* keys in the i18n bundle. Keep the
                // Localizer.T routing here so future translations stay clean.
                ["Image.Load"] = Localizer.T("visualist.node.bubble.image_load"),
                ["Image.LoadUrl"] = Localizer.T("visualist.node.bubble.image_loadurl"),
                ["Video.Load"] = Localizer.T("visualist.node.bubble.video_load"),
                ["Audio.Load"] = Localizer.T("visualist.node.bubble.audio_load"),
                ["Color.Constant"] = Localizer.T("visualist.node.bubble.color_constant"),
                ["Scalar.Constant"] = Localizer.T("visualist.node.bubble.scalar_constant"),
                ["Vector2.Constant"] = Localizer.T("visualist.node.bubble.vector2_constant"),
                ["Vector3.Constant"] = Localizer.T("visualist.node.bubble.vector3_constant"),
                ["Vector4.Constant"] = Localizer.T("visualist.node.bubble.vector4_constant"),
                ["Vector.Rect4"] = Localizer.T("visualist.node.bubble.vector_rect4"),
                ["Math.Resolution"] = Localizer.T("visualist.node.bubble.math_resolution"),

                // ── Image kernels (canonical pipeline order) ──────────────
                ["Image.Scale"] = Localizer.T("visualist.node.bubble.image_scale"),
                ["Image.Transform"] = Localizer.T("visualist.node.bubble.image_transform"),
                ["Image.ColorAdjust"] = Localizer.T("visualist.node.bubble.image_coloradjust"),
                ["Image.Mask"] = Localizer.T("visualist.node.bubble.image_mask"),
                ["Image.Blend"] = Localizer.T("visualist.node.bubble.image_blend"),
                ["Image.Combine"] = Localizer.T("visualist.node.bubble.image_combine"),
                ["Image.Crop"] = Localizer.T("visualist.node.bubble.image_crop"),
                ["Image.Tile"] = Localizer.T("visualist.node.bubble.image_tile"),

                // ── Procedural mask / shape generators ────────────────────
                ["Mask.Rectangle"] = Localizer.T("visualist.node.bubble.mask_rectangle"),
                ["Mask.Circle"] = Localizer.T("visualist.node.bubble.mask_circle"),
                ["Mask.Ellipse"] = Localizer.T("visualist.node.bubble.mask_ellipse"),
                ["Mask.LinearGradient"] = Localizer.T("visualist.node.bubble.mask_lineargradient"),
                ["Mask.RadialGradient"] = Localizer.T("visualist.node.bubble.mask_radialgradient"),
                ["Mask.Vignette"] = Localizer.T("visualist.node.bubble.mask_vignette"),
                ["Mask.Polygon"] = Localizer.T("visualist.node.bubble.mask_polygon"),
                ["Mask.Bezier"] = Localizer.T("visualist.node.bubble.mask_bezier"),
                ["Mask.Star"] = Localizer.T("visualist.node.bubble.mask_star"),

                // ── Math ──────────────────────────────────────────────────
                ["Math.Add"]    = Localizer.T("visualist.node.bubble.math_add"),
                ["Math.Sub"]    = Localizer.T("visualist.node.bubble.math_sub"),
                ["Math.Mul"]    = Localizer.T("visualist.node.bubble.math_mul"),
                ["Math.Div"]    = Localizer.T("visualist.node.bubble.math_div"),
                ["Math.Lerp"]   = Localizer.T("visualist.node.bubble.math_lerp"),
                ["Math.Clamp"]  = Localizer.T("visualist.node.bubble.math_clamp"),
                ["Math.LerpVector2"] = Localizer.T("visualist.node.bubble.math_lerpvector2"),
                ["Math.LerpVector3"] = Localizer.T("visualist.node.bubble.math_lerpvector3"),
                ["Math.LerpVector4"] = Localizer.T("visualist.node.bubble.math_lerpvector4"),

                // ── Vector packing / unpacking ────────────────────────────
                ["Vector.Split"]    = Localizer.T("visualist.node.bubble.vector_split"),
                ["Vector.Combine"]  = Localizer.T("visualist.node.bubble.vector_combine"),
                ["Vector3.Split"]   = Localizer.T("visualist.node.bubble.vector3_split"),
                ["Vector3.Combine"] = Localizer.T("visualist.node.bubble.vector3_combine"),
                ["Vector4.Split"]   = Localizer.T("visualist.node.bubble.vector4_split"),
                ["Vector4.Combine"] = Localizer.T("visualist.node.bubble.vector4_combine"),

                // ── Triggers / lifecycle ──────────────────────────────────
                ["Visual.OnStartup"] = Localizer.T("visualist.node.bubble.visual_onstartup"),
                ["Visual.OnTrigger"] = Localizer.T("visualist.node.bubble.visual_ontrigger"),
                ["Visual.Complete"] = Localizer.T("visualist.node.bubble.visual_complete"),
                ["Result.If"] = Localizer.T("visualist.node.bubble.result_if"),

                // ── Captions / Text ───────────────────────────────────────
                ["Caption.LiveCaption"] = Localizer.T("visualist.node.bubble.caption_livecaption"),
                ["Text.Translate"] = Localizer.T("visualist.node.bubble.text_translate"),
                ["Text.Render"] = Localizer.T("visualist.node.bubble.text_render"),

                // ── Sinks ─────────────────────────────────────────────────
                [DisplaySinkNode.Title] = Localizer.T("visualist.node.bubble.display"),
                [AudioSinkNode.Title] = Localizer.T("visualist.node.bubble.audio_play"),

                // ── Debug ─────────────────────────────────────────────────
                ["Viewer"] = Localizer.T("visualist.node.bubble.viewer"),
            };

        /// <summary>
        /// Optional human-readable tooltip / warning text keyed by template title.
        /// Returns <c>null</c> when no override is registered (most templates rely on
        /// the per-socket / per-attribute hints emitted by the renderer instead).
        /// </summary>
        public static IReadOnlyDictionary<string, string> Tooltips => _tooltips;

        /// <summary>Convenience accessor — returns null when no tooltip is registered.</summary>
        public static string? GetTooltip(string title) =>
            _tooltips.TryGetValue(title, out var t) ? t : null;

        // UE-Blueprints glyph titles for math operators. The
        // canonical Title (e.g. "Math.Div") stays the dictionary key for
        // serialisation and template lookup; the override is consulted only by
        // renderers via GetDisplayTitle. The actual templates live in
        // NodeTemplates.cs which uses short names (Math.Mul/Div/Sub) — this
        // override map matches those short names. Math.Mod is included for
        // forward-compat even though no Math.Mod template ships today.
        // FOLLOW-UP: when NodeTemplates.cs adds the canonical DisplayName field,
        // collapse this side-table.
        private static readonly Dictionary<string, string> _displayTitles =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Math.Add"] = "+",
                ["Math.Sub"] = "−",   // U+2212 MINUS SIGN, not the ASCII hyphen
                ["Math.Mul"] = "×",   // U+00D7 MULTIPLICATION SIGN
                ["Math.Div"] = "÷",   // U+00F7 DIVISION SIGN
                ["Math.Mod"] = "%",
            };

        /// <summary>
        /// Optional render-time display label keyed by canonical template title.
        /// Renderers prefer the override (clean glyphs for math operators);
        /// falls back to the canonical Title when no entry is registered.
        /// </summary>
        public static IReadOnlyDictionary<string, string> DisplayTitles => _displayTitles;

        /// <summary>Returns the override glyph when registered, otherwise echoes the title verbatim.</summary>
        public static string GetDisplayTitle(string title) =>
            _displayTitles.TryGetValue(title, out var d) ? d : title;

        public static void Add(NodeTemplate template)
        {
            if (ForbiddenCategories.Contains(template.Category))
                throw new InvalidOperationException(
                    $"Category '{template.Category}' is forbidden in WidgetNodeRegistry — that's Architect's domain.");
            if (!AllowedCategories.Contains(template.Category))
                throw new InvalidOperationException(
                    $"Category '{template.Category}' is not in AllowedCategories. Add it explicitly if intentional.");
            _templates[template.Title] = template;
        }

        public static NodeTemplate? Get(string title) =>
            _templates.TryGetValue(title, out var t) ? t : null;

        public static IEnumerable<NodeTemplate> GetByCategory(string category) =>
            _templates.Values.Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Build a Node instance from a registered template, placed at the given location.
        /// Sink nodes (Display, Audio.Play) route through their dedicated builders so the
        /// canonical socket/header layout stays in lockstep with the sink-specific contract.
        /// </summary>
        public static Node Instantiate(string title, Point location)
        {
            // Sink shortcuts: route through the dedicated builders so the
            // canonical socket/header layout stays in lockstep with the
            // sink-specific contract. The DefaultAttributes from the template
            // (notably "IsAutoInjected"=true on Display) are still copied across
            // so downstream consumers — editors / serialisers that hide
            // auto-injected nodes from the catalog or pin them in the graph —
            // can read the same metadata as a fully-template-built node.
            if (title == DisplaySinkNode.Title)
            {
                var d = DisplaySinkNode.Build();
                d.Location = location;
                if (Get(DisplaySinkNode.Title) is { } dtmpl)
                {
                    foreach (var kv in dtmpl.DefaultAttributes)
                        if (!d.Attributes.ContainsKey(kv.Key))
                            d.Attributes[kv.Key] = kv.Value;
                }
                return d;
            }
            if (title == AudioSinkNode.Title)
            {
                var a = AudioSinkNode.Build();
                a.Location = location;
                if (Get(AudioSinkNode.Title) is { } atmpl)
                {
                    foreach (var kv in atmpl.DefaultAttributes)
                        if (!a.Attributes.ContainsKey(kv.Key))
                            a.Attributes[kv.Key] = kv.Value;
                }
                return a;
            }

            var tmpl = Get(title)
                ?? throw new InvalidOperationException($"Unknown node template: '{title}'.");

            var node = new Node
            {
                Title    = tmpl.Title,
                Category = tmpl.Category,
                Location = location,
                Size     = new Size(180, Math.Max(60, 40 + Math.Max(tmpl.Inputs.Count, tmpl.Outputs.Count) * 22)),
            };
            foreach (var s in tmpl.Inputs)
                node.Sockets.Add(new Socket { Name = s.Name, Type = SocketType.Input,  DataType = s.DataType });
            foreach (var s in tmpl.Outputs)
                node.Sockets.Add(new Socket { Name = s.Name, Type = SocketType.Output, DataType = s.DataType });
            foreach (var kv in tmpl.DefaultAttributes)
                node.Attributes[kv.Key] = kv.Value;
            return node;
        }

        public static void Reset()
        {
            _templates.Clear();
            // Notify NodeTemplates so its idempotency flag re-arms; otherwise
            // a subsequent RegisterAll() call short-circuits and the registry
            // stays empty, breaking any test class that runs after a Reset().
            NodeTemplates.OnRegistryReset();
        }

        // True when at least one template has been registered. Used by
        // NodeTemplates.RegisterAll for genuine idempotency across resets.
        internal static bool HasTemplates => _templates.Count > 0;
    }
}
