using System;
using System.Collections.Generic;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// Declares how a Visualist template participates in the body-preview strip
    /// (manifesto §4 differentiator: "the node IS the image"). The Fusion-style
    /// thumbnail painted inside the node body needs to know where its pixels
    /// come from — own attribute, own URL, upstream Image chain, or own colour
    /// swatch. Templates opt in via <see cref="NodeTemplates.Add"/>'s
    /// <c>previewSource</c> argument and the canvas reads back the enum via
    /// <see cref="NodeTemplates.GetPreviewSource"/> instead of re-deriving it
    /// from the title at paint time.
    /// </summary>
    public enum PreviewSource
    {
        /// <summary>No preview rect — node renders only the socket rows.</summary>
        None = 0,
        /// <summary>Bitmap from this node's <c>Path</c> attribute (Image.Load, Video.Load).</summary>
        OwnPath,
        /// <summary>Bitmap from this node's <c>Url</c> attribute (Image.LoadUrl).</summary>
        OwnUrl,
        /// <summary>Walk upstream via the canonical "In" socket to the nearest Image source (Viewer, Image.Scale).</summary>
        UpstreamImage,
        /// <summary>Solid-colour swatch from this node's colour-bearing attribute (Color.Constant).</summary>
        OwnColor,
    }

    /// <summary>
    /// NodeTemplates — Phase 5 catalog registration. Call <see cref="RegisterAll"/> at
    /// Visualist startup to populate <see cref="WidgetNodeRegistry"/> with the visual + math
    /// node palette. Templates are intentionally narrow: matches the JS-side evaluator in
    /// <c>data/overlay/compositor.js</c>.
    /// </summary>
    /// <remarks>
    /// Convention notes:
    ///
    /// * <b>Scalar range convention.</b> Per the manifesto §4 "Visual data types", a
    ///   <see cref="SocketDataType.Scalar"/> socket is conventionally a normalised 0..1 value
    ///   unless otherwise noted in the per-template comment. The C# evaluator and
    ///   <c>compositor.js</c> are responsible for clamping when behaviour requires it; the
    ///   template only documents the contract.
    ///
    /// * <b>KnownValues attribute convention.</b> A template attribute named <c>X</c> with a
    ///   companion attribute <c>X__KnownValues</c> (CSV) hints to the Architect detail panel
    ///   and the Visualist canvas that <c>X</c> should render as a dropdown / autocomplete.
    ///   The double-underscore suffix is intentional: it keeps the catalog free of any new
    ///   metadata-class plumbing (until inline pills land) while staying machine-readable
    ///   for the editor. Renderers strip the suffix when displaying the attribute name.
    ///
    /// * <b>Composition order.</b> The Image.* kernels listed below run in the order they
    ///   would naturally chain in a graph; each template comment lists where it sits in the
    ///   canonical pipeline so authors can reason about result vs. attribute drift.
    /// </remarks>
    public static class NodeTemplates
    {
        // Standard CSS blend modes accepted by canvas globalCompositeOperation.
        // Mirrors the validation list in NodeEvaluator._validBlendModes and compositor.js.
        // Renderers reading "Mode__KnownValues" pick this up to drive the dropdown.
        private const string BlendModeKnownValues =
            "normal,multiply,screen,overlay,darken,lighten," +
            "color-dodge,color-burn,hard-light,soft-light," +
            "difference,exclusion,hue,saturation,color,luminosity";

        // Scalar range hints. A "<Attribute>__Range" companion attribute documents the
        // expected numeric range so editors can render appropriate sliders/spinners. The
        // evaluator does NOT enforce these — clamping is kernel-side work (see Math.Clamp).
        private const string Range01            = "0..1";
        private const string RangeBipolar       = "-1..1";
        private const string RangeHueDegrees    = "0..360";
        private const string RangeRotateDegrees = "-360..360";

        private static bool _registered;

        // Title → PreviewSource lookup. Populated by Add() at registration time.
        // The canvas reads back via GetPreviewSource(node.Title); NodeEvaluator
        // also walks this map to populate PreviewSnapshot per node id.
        // OrdinalIgnoreCase mirrors how WidgetNodeRegistry resolves titles.
        private static readonly Dictionary<string, PreviewSource> _previewByTitle =
            new(StringComparer.OrdinalIgnoreCase);

        // Called by WidgetNodeRegistry.Reset() so subsequent RegisterAll()
        // calls actually re-populate instead of short-circuiting on the flag.
        internal static void OnRegistryReset()
        {
            _registered = false;
            _previewByTitle.Clear();
        }

        /// <summary>
        /// Returns the <see cref="PreviewSource"/> declared by the template with
        /// the given title, or <see cref="PreviewSource.None"/> when the template
        /// did not opt in (or hasn't been registered yet). Safe to call from
        /// paint code — pure dictionary lookup.
        /// </summary>
        public static PreviewSource GetPreviewSource(string? title)
        {
            if (string.IsNullOrEmpty(title)) return PreviewSource.None;
            return _previewByTitle.TryGetValue(title, out var src) ? src : PreviewSource.None;
        }

        // Attribute key carrying the legacy "this template wants a preview rect"
        // boolean. Kept for serializer round-trip + WidgetGraphCanvas.HasBodyPreview;
        // Add() stamps it automatically when previewSource != None.
        internal const string PreviewAttrKey = "__Preview";

        public static void RegisterAll()
        {
            // Defence-in-depth: if the registry has been emptied without our
            // flag getting reset, force a re-register so dependent tests don't
            // silently see an empty catalog.
            if (_registered && WidgetNodeRegistry.HasTemplates) return;
            _registered = true;

            // ── Inputs ────────────────────────────────────────────────────────
            //
            // Templates that opt into a body preview rect pass `previewSource:`
            // to Add(). The helper stamps the legacy `__Preview = "true"`
            // attribute (consumed by WidgetGraphCanvas.HasBodyPreview) and
            // records the enum in NodeTemplates._previewByTitle so the canvas
            // and NodeEvaluator can resolve the preview kind without sniffing
            // attribute strings. PreviewSource is the source of truth; the
            // attribute is its serialized companion. Same convention as
            // "__KnownValues" / "__Range": underscore-prefixed metadata.
            Add("Image.Load", "Inputs",
                inputs:  Empty(),
                outputs: O("Image", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Path"] = "\"\"",
                },
                previewSource: PreviewSource.OwnPath);

            Add("Image.LoadUrl", "Inputs",
                inputs:  Empty(),
                outputs: O("Image", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Url"] = "\"\"",
                },
                previewSource: PreviewSource.OwnUrl);

            // Video.Load — local-file source. Path resolves through the central
            // media library (data/media/) at render time. Output is Image so
            // every Image.* kernel chains in unchanged. Compositor reuses a
            // single <video> element per node id and starts it muted+looped by
            // default (OBS browser-source autoplay constraint).
            Add("Video.Load", "Inputs",
                inputs:  Empty(),
                outputs: new[]
                {
                    new WidgetNodeRegistry.SocketSpec("Image",    SocketDataType.Image),
                    new WidgetNodeRegistry.SocketSpec("Duration", SocketDataType.Scalar),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["Path"]  = "\"\"",
                    ["Loop"]  = "true",
                    ["Muted"] = "true",
                },
                previewSource: PreviewSource.OwnPath);

            // Audio.Load — opaque audio source feeding Audio.Play. Not drawable;
            // wiring this into Display does nothing useful (Display only
            // understands Image-shaped values).
            Add("Audio.Load", "Inputs",
                inputs:  Empty(),
                outputs: O("Audio", SocketDataType.Audio),
                attrs:   new Dictionary<string, string> { ["Path"] = "\"\"" });

            Add("Color.Constant", "Inputs",
                inputs:  Empty(),
                outputs: O("Color", SocketDataType.Color),
                attrs:   new Dictionary<string, string> { ["Value"] = "\"#ffffff\"" });

            // String.Constant — pure String producer (no inputs). The Value
            // attribute is JSON-string-quoted (matching Color.Constant's "Value"
            // quoting) so the default round-trips as an escaped empty string, and
            // both evaluators strip the quotes when reading it: the C# mirror via
            // EvalStringNode's StripQuotes(attr "Value"), the browser via
            // compositor.js evalStringConstant => stripQuotes(attr(node,'Value','')).
            // An unwired downstream String input takes this value end-to-end through
            // the existing attr() fallback in _evalStringSocket (no new persistence
            // path — the inspector commits to node.Attributes["Value"]).
            Add("String.Constant", "Inputs",
                inputs:  Empty(),
                outputs: O("Value", SocketDataType.String),
                attrs:   new Dictionary<string, string> { ["Value"] = "\"\"" });

            // Scalar.Constant emits a normalised scalar. The convention is 0..1
            // (per-attribute "Value__Range") but values outside that band are accepted —
            // downstream kernels decide whether to clamp.
            Add("Scalar.Constant", "Inputs",
                inputs:  Empty(),
                outputs: O("Value", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string>
                {
                    ["Value"]          = "1.0",
                    ["Value__Range"]   = Range01,
                });

            Add("Vector2.Constant", "Vector",
                inputs:  Empty(),
                outputs: O("Value", SocketDataType.Vector2),
                attrs:   new Dictionary<string, string> { ["X"] = "0", ["Y"] = "0" });

            // Vector3 / Vector4 producers added so the new Math.LerpVectorN
            // and Image.Crop's Rect:Vector4 socket can be wired without an external author
            // baking the values in attributes. Vector.Rect4 is a friendly alias for the
            // common Crop-rect case (X/Y/W/H).
            Add("Vector3.Constant", "Vector",
                inputs:  Empty(),
                outputs: O("Value", SocketDataType.Vector3),
                attrs:   new Dictionary<string, string> { ["X"] = "0", ["Y"] = "0", ["Z"] = "0" });

            Add("Vector4.Constant", "Vector",
                inputs:  Empty(),
                outputs: O("Value", SocketDataType.Vector4),
                attrs:   new Dictionary<string, string> { ["X"] = "0", ["Y"] = "0", ["Z"] = "0", ["W"] = "0" });

            Add("Vector.Rect4", "Inputs",
                inputs:  Empty(),
                outputs: O("Rect", SocketDataType.Vector4),
                attrs:   new Dictionary<string, string> { ["X"] = "0", ["Y"] = "0", ["W"] = "0", ["H"] = "0" });

            Add("Math.Resolution", "Inputs",
                inputs:  Empty(),
                outputs: O("Size", SocketDataType.Vector2));

            // ── Image ops ─────────────────────────────────────────────────────
            //
            // Canonical pipeline order (when authors chain all kernels):
            //   Load/LoadUrl → Crop → ColorAdjust → Transform → Tile → Mask → Blend → Display
            //
            // ColorAdjust runs BEFORE Blend (per-source colour grading happens before
            // composition). Crop runs BEFORE ColorAdjust (rectangle clipping is a coordinate
            // op; doing it after grading wastes pixel work). Transform follows ColorAdjust
            // so rotation/scale don't bake into colour samples. Mask is conceptually a
            // per-pixel multiply — it applies before final Blend so the alpha is correct.
            //
            // The C# evaluator (NodeEvaluator.cs) only walks the metadata; the actual pixel
            // ordering lives in compositor.js. This convention keeps both sides predictable.

            // Uniform attribute dropped; single Scalar Factor is the only knob.
            // Pipeline order: applied wherever wired; Factor defaults to 1 (no-op).
            //   Factor: 0..* (1 = identity, <1 shrinks, >1 enlarges)
            //
            // Image.Scale opts into PreviewSource.UpstreamImage so the canvas
            // paints a Fusion-style passthrough thumbnail of the upstream image.
            // The thumbnail is intentionally NOT scaled by Factor at design time —
            // the body strip just shows "what's flowing through". Real scaling
            // happens browser-side in compositor.js.
            Add("Image.Scale", "Image",
                inputs:  new[] { S("In", SocketDataType.Image), S("Factor", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Factor"]        = "1",
                    ["Factor__Range"] = "0..8",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Transform
            // Pipeline order: AFTER Crop / ColorAdjust, BEFORE Tile / Mask / Blend.
            // Inputs (all optional — fall back to attribute defaults when unwired):
            //   Translate : Vector2  pixel offset (default 0,0)
            //   Rotate    : Scalar   degrees (default 0; range -360..360 by convention)
            //   Scale     : Vector2  per-axis multiplier (default 1,1)
            // Attribute fallbacks live as TranslateX/Y, ScaleX/Y, Rotation in NodeEvaluator
            // (see EvalImageTransform). When the socket is wired, the wired value wins.
            Add("Image.Transform", "Image",
                inputs:  new[]
                {
                    S("In",        SocketDataType.Image),
                    S("Translate", SocketDataType.Vector2),
                    S("Rotate",    SocketDataType.Scalar),
                    S("Scale",     SocketDataType.Vector2),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["TranslateX"]      = "0",
                    ["TranslateY"]      = "0",
                    ["Rotation"]        = "0",
                    ["Rotation__Range"] = RangeRotateDegrees,
                    ["ScaleX"]          = "1",
                    ["ScaleY"]          = "1",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.ColorAdjust
            // Pipeline order: AFTER Crop, BEFORE Transform. Per-source grading.
            //   Hue        : Scalar  degrees (default 0; range 0..360)
            //   Saturation : Scalar  (default 0 = no change; range -1..1, -1 = greyscale, +1 = boost)
            //   Brightness : Scalar  (default 0 = no change; range -1..1)
            //   Contrast   : Scalar  (default 0 = no change; range -1..1)
            // The evaluator (EvalImageColorAdjust) defaults missing attributes to "0".
            Add("Image.ColorAdjust", "Image",
                inputs:  new[]
                {
                    S("In",         SocketDataType.Image),
                    S("Hue",        SocketDataType.Scalar),
                    S("Saturation", SocketDataType.Scalar),
                    S("Brightness", SocketDataType.Scalar),
                    S("Contrast",   SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Hue"]               = "0",
                    ["Hue__Range"]        = RangeHueDegrees,
                    ["Saturation"]        = "0",
                    ["Saturation__Range"] = RangeBipolar,
                    ["Brightness"]        = "0",
                    ["Brightness__Range"] = RangeBipolar,
                    ["Contrast"]          = "0",
                    ["Contrast__Range"]   = RangeBipolar,
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Mask
            // Pipeline order: AFTER ColorAdjust / Transform, BEFORE Blend.
            // Both inputs are required — NodeEvaluator surfaces an explicit error when
            // Mask is unwired (silent passthrough to unmasked image misleads authors).
            //   Mode : "alpha" (default) | "luminance" — read by compositor.js
            Add("Image.Mask", "Image",
                inputs:  new[] { S("Image", SocketDataType.Image), S("Mask", SocketDataType.Image) },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Mode"]              = "\"alpha\"",
                    ["Mode__KnownValues"] = "alpha,luminance",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Blend
            // Pipeline order: LAST visual op before Display (composes A + B).
            // Both A (bottom) and B (top) are required — evaluator errors when B is unwired.
            //   Opacity : Scalar  (default 1; range 0..1, 0 = invisible top, 1 = full)
            //   Mode    : standard CSS globalCompositeOperation string (default "normal")
            // Mode__KnownValues drives the editor dropdown so users can't typo a
            // mode like "blurplease" and get a silent fall-through to source-over.
            Add("Image.Blend", "Image",
                inputs:  new[]
                {
                    S("A",       SocketDataType.Image),
                    S("B",       SocketDataType.Image),
                    S("Opacity", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Mode"]              = "\"normal\"",
                    ["Mode__KnownValues"] = BlendModeKnownValues,
                    ["Opacity"]           = "1",
                    ["Opacity__Range"]    = Range01,
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Combine — unified blend + key node. Wraps Image.Blend's
            // standard CSS modes (multiply, screen, overlay, etc.) PLUS three
            // key modes that derive alpha from the top input's pixel values:
            //   • alpha-key      — keeps top's existing alpha (passes B over A)
            //   • luminance-key  — uses top's luminance as alpha (black → transparent)
            //   • inv-luminance  — inverse of luminance-key (white → transparent)
            // Both inputs accept any Image producer including shape generators
            // (Mask.Rectangle, Mask.Circle, etc.) so authors can combine
            // shapes/gradients/images without juggling separate Blend + Mask
            // chains for the common cases.
            Add("Image.Combine", "Image",
                inputs:  new[]
                {
                    S("A",       SocketDataType.Image),
                    S("B",       SocketDataType.Image),
                    S("Opacity", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Mode"]              = "\"normal\"",
                    ["Mode__KnownValues"] = BlendModeKnownValues + ",alpha-key,luminance-key,inv-luminance",
                    ["Opacity"]           = "1",
                    ["Opacity__Range"]    = Range01,
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Crop
            // Pipeline order: FIRST after Load (rectangle clip is a coordinate op).
            //   Rect : Vector4  (X, Y, W, H) as 0..1 fractions of the source-image
            //                   bounds, NOT pixels. compositor.js (Image.Crop case in
            //                   evalNodeOutput) multiplies the rect by the source's
            //                   width/height to derive the actual pixel rect; the
            //                   C# evaluator mirrors that math in EvalImageCrop. The
            //                   attribute fallback default is "0,0,1,1" (full-image
            //                   passthrough). Previously the comment said "pixels"
            //                   which contradicted the actual semantics on both sides.
            //                   Same fraction convention is used by Mask.* shape
            //                   generators below.
            // Vector.Rect4 / Vector4.Constant added above so this socket is wirable.
            Add("Image.Crop", "Image",
                inputs:  new[] { S("In", SocketDataType.Image), S("Rect", SocketDataType.Vector4) },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    // Explicit "0..1 fractions, full passthrough" default so a fresh
                    // Image.Crop node renders the upstream unchanged until edited.
                    ["Rect"] = "0,0,1,1",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Blur
            // Pipeline order: AFTER ColorAdjust / Transform, BEFORE Mask / Blend.
            //   Radius : Scalar  (default 0; range 0..50 px)
            // Implemented browser-side as `ctx.filter = blur(<radius>px)`. A
            // radius of 0 is a no-op passthrough.
            Add("Image.Blur", "Image",
                inputs:  new[]
                {
                    S("In",     SocketDataType.Image),
                    S("Radius", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Radius"]        = "0",
                    ["Radius__Range"] = "0..50",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Gaussian
            // Pipeline order: AFTER ColorAdjust / Transform, BEFORE Mask / Blend.
            //   SigmaX : Scalar  (default 0; range 0..40 — horizontal stdDeviation)
            //   SigmaY : Scalar  (default 0; range 0..40 — vertical   stdDeviation)
            // Implemented browser-side via SVG `feGaussianBlur` with two-value
            // stdDeviation, giving authors a directional Gaussian Image.Blur
            // can't produce (canvas `filter: blur()` is isotropic-only). When
            // both sigmas are 0 the kernel short-circuits to passthrough.
            Add("Image.Gaussian", "Image",
                inputs:  new[]
                {
                    S("In",     SocketDataType.Image),
                    S("SigmaX", SocketDataType.Scalar),
                    S("SigmaY", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["SigmaX"]        = "0",
                    ["SigmaX__Range"] = "0..40",
                    ["SigmaY"]        = "0",
                    ["SigmaY__Range"] = "0..40",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Shadow
            // Pipeline order: AFTER ColorAdjust / Transform, BEFORE Mask / Blend.
            //   OffsetX : Scalar  (default 4;  range -64..64 px — horizontal shadow drop)
            //   OffsetY : Scalar  (default 4;  range -64..64 px — vertical   shadow drop)
            //   Blur    : Scalar  (default 6;  range  0..64  px — shadow softness)
            //   Color   : String  (default rgba(0,0,0,0.5) — CSS color literal)
            // Implemented browser-side via canvas2D `filter: drop-shadow()`. The
            // output canvas is the same size as the source; shadow that falls
            // outside the source rect gets clipped (matches CSS box-shadow
            // semantics for an in-rect compositor pipeline). Authors who need
            // overflow can chain Image.Transform with Translate first.
            Add("Image.Shadow", "Image",
                inputs:  new[]
                {
                    S("In",      SocketDataType.Image),
                    S("OffsetX", SocketDataType.Scalar),
                    S("OffsetY", SocketDataType.Scalar),
                    S("Blur",    SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["OffsetX"]        = "4",
                    ["OffsetX__Range"] = "-64..64",
                    ["OffsetY"]        = "4",
                    ["OffsetY__Range"] = "-64..64",
                    ["Blur"]           = "6",
                    ["Blur__Range"]    = "0..64",
                    ["Color"]          = "\"rgba(0,0,0,0.5)\"",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Glow
            // Pipeline order: AFTER ColorAdjust / Transform, BEFORE Mask / Blend.
            //   Radius    : Scalar  (default 12; range 0..64 px — halo softness)
            //   Intensity : Scalar  (default 1;  range 0..4 — number of stacked passes)
            //   Color     : String  (default rgba(255,255,200,0.85) — CSS color literal)
            // Outer glow via stacked drop-shadows: each pass is a zero-offset
            // drop-shadow with the configured Radius / Color, drawn through
            // the canvas filter pipeline. Stacking N passes produces an N-step
            // brighter halo without alpha-clipping a single bright pass would
            // hit. Radius=0 OR Intensity=0 short-circuits to passthrough.
            Add("Image.Glow", "Image",
                inputs:  new[]
                {
                    S("In",        SocketDataType.Image),
                    S("Radius",    SocketDataType.Scalar),
                    S("Intensity", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Radius"]           = "12",
                    ["Radius__Range"]    = "0..64",
                    ["Intensity"]        = "1",
                    ["Intensity__Range"] = "0..4",
                    ["Color"]            = "\"rgba(255,255,200,0.85)\"",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Distort
            // Pipeline order: AFTER ColorAdjust / Transform, BEFORE Mask / Blend.
            //   Mode      : String  (default "wave"; KnownValues "wave,ripple")
            //   Amplitude : Scalar  (default 8;  range 0..64 px — peak displacement)
            //   Frequency : Scalar  (default 4;  range 0..16   — full cycles across the source)
            // Geometric distortion via per-row pixel shifts on a copy canvas.
            // "wave" displaces each row horizontally by Amplitude * sin(2π * y *
            // Frequency / Height); "ripple" adds a matching column shift so the
            // image bows along both axes. Amplitude=0 short-circuits to
            // passthrough. Mode__KnownValues drives the editor dropdown so a
            // typo'd mode never silently falls through to "wave".
            Add("Image.Distort", "Image",
                inputs:  new[]
                {
                    S("In",        SocketDataType.Image),
                    S("Amplitude", SocketDataType.Scalar),
                    S("Frequency", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Mode"]              = "\"wave\"",
                    ["Mode__KnownValues"] = "wave,ripple",
                    ["Amplitude"]         = "8",
                    ["Amplitude__Range"]  = "0..64",
                    ["Frequency"]         = "4",
                    ["Frequency__Range"]  = "0..16",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Mosaic
            // Pipeline order: AFTER ColorAdjust / Transform, BEFORE Mask / Blend.
            //   TileSize : Scalar  (default 8; range 1..128 — tile edge in source pixels)
            // Pixelates the image by downscaling to (W/TileSize, H/TileSize) with
            // imageSmoothingEnabled=false, then upscaling back to (W, H) the same
            // way. Result is the classic "8-bit" / privacy-blur look. TileSize=1
            // is identity so the kernel short-circuits to passthrough.
            Add("Image.Mosaic", "Image",
                inputs:  new[]
                {
                    S("In",       SocketDataType.Image),
                    S("TileSize", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["TileSize"]        = "8",
                    ["TileSize__Range"] = "1..128",
                },
                previewSource: PreviewSource.UpstreamImage);

            // Image.Tile
            // Pipeline order: AFTER Transform, BEFORE Mask / Blend.
            // Output dims are forced to widget rect (tile fills the widget bounds).
            //   Repeat : Vector2  (default 1,1; non-integer counts wrap fractionally)
            // Attribute fallbacks RepeatX / RepeatY default to "1" in EvalImageTile.
            // Wrap: per-edge tiling behaviour (repeat / mirror / clamp). The
            // browser kernel (compositor.js evalImageTile) already reads this attr;
            // the C# mirror was the laggard. Stored JSON-string-encoded so the
            // default round-trips identically to the JS `attr(node,'Wrap','"repeat"')`.
            Add("Image.Tile", "Image",
                inputs:  new[] { S("In", SocketDataType.Image), S("Repeat", SocketDataType.Vector2) },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["RepeatX"]          = "1",
                    ["RepeatY"]          = "1",
                    ["Wrap"]             = "\"repeat\"",
                    ["Wrap__KnownValues"] = "repeat,mirror,clamp",
                },
                previewSource: PreviewSource.UpstreamImage);

            // ── Procedural mask shape generators ────────────────────
            // Six pure generator nodes (zero inputs, one Image output) feeding
            // into Image.Mask's Mask socket. All coordinates / radii are
            // normalised 0..1 so a shape is portable across layer resolutions.
            // Animation: every param is a scalar so the keyframe
            // pipeline picks them up via attrAnimated() in compositor.js.
            // Bezier/Polygon + ShapeEditor modal land in a later sweep.

            Add("Mask.Rectangle", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["X"]                   = "0",      ["X__Range"]            = Range01,
                    ["Y"]                   = "0",      ["Y__Range"]            = Range01,
                    ["Width"]               = "1",      ["Width__Range"]        = Range01,
                    ["Height"]              = "1",      ["Height__Range"]       = Range01,
                    ["CornerRadius"]        = "0",      ["CornerRadius__Range"] = "0..0.5",
                    ["Feather"]             = "0",      ["Feather__Range"]      = "0..0.5",
                    ["Inverted"]            = "false",
                });

            Add("Mask.Circle", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["CX"]       = "0.5",  ["CX__Range"]      = Range01,
                    ["CY"]       = "0.5",  ["CY__Range"]      = Range01,
                    ["Radius"]   = "0.25", ["Radius__Range"]  = Range01,
                    ["Feather"]  = "0",    ["Feather__Range"] = Range01,
                    ["Inverted"] = "false",
                });

            Add("Mask.Ellipse", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["CX"]       = "0.5", ["CX__Range"]       = Range01,
                    ["CY"]       = "0.5", ["CY__Range"]       = Range01,
                    ["RadiusX"]  = "0.3", ["RadiusX__Range"]  = Range01,
                    ["RadiusY"]  = "0.2", ["RadiusY__Range"]  = Range01,
                    ["Rotation"] = "0",   ["Rotation__Range"] = "-180..180",
                    ["Feather"]  = "0",   ["Feather__Range"]  = Range01,
                    ["Inverted"] = "false",
                });

            Add("Mask.LinearGradient", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["FromX"]     = "0",   ["FromX__Range"]     = Range01,
                    ["FromY"]     = "0.5", ["FromY__Range"]     = Range01,
                    ["ToX"]       = "1",   ["ToX__Range"]       = Range01,
                    ["ToY"]       = "0.5", ["ToY__Range"]       = Range01,
                    ["FromAlpha"] = "1",   ["FromAlpha__Range"] = Range01,
                    ["ToAlpha"]   = "0",   ["ToAlpha__Range"]   = Range01,
                });

            Add("Mask.RadialGradient", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["CX"]          = "0.5", ["CX__Range"]          = Range01,
                    ["CY"]          = "0.5", ["CY__Range"]          = Range01,
                    ["InnerRadius"] = "0",   ["InnerRadius__Range"] = Range01,
                    ["OuterRadius"] = "0.5", ["OuterRadius__Range"] = Range01,
                    ["InnerAlpha"]  = "1",   ["InnerAlpha__Range"]  = Range01,
                    ["OuterAlpha"]  = "0",   ["OuterAlpha__Range"]  = Range01,
                });

            // Vignette is a one-knob preset — internally renders the same path
            // as Mask.RadialGradient with canonical defaults driven by Strength
            // (1.0 inner alpha, 1-Strength outer alpha; 0.3 / 0.85 radii).
            Add("Mask.Vignette", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Strength"]        = "0.5",
                    ["Strength__Range"] = Range01,
                });

            // ── Bezier / Polygon / Star mask shapes ─────────────────
            // Polygon and Bezier carry a JSON `Vertices` attribute holding the
            // vertex list (variable length up to ShapeData.MaxVertices). The
            // ShapeEditor modal is the only sane editing surface; the inline
            // pill is intentionally suppressed for these by WidgetGraphCanvas.
            // Per-vertex animation lives in WidgetTimeline.Keyframes with the
            // parameterPath grammar `vertex[N].<axis>`.
            //
            // Star is parameterised (no vertex list) so it animates via the
            // existing scalar pipeline.

            // Vertices default sourced from ShapeData.DefaultPolygon /
            // DefaultBezier and serialised through ShapeData.Serialize. The
            // template now shares a single source of truth with the runtime
            // parser, instead of duplicating a hand-typed JSON literal that
            // could drift from the C# Vertex shape during a refactor. The
            // serialised JSON is byte-identical to the prior literal — this is
            // a hardening change, not a value change — so existing graphs
            // round-trip unchanged.
            Add("Mask.Polygon", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Vertices"] = ShapeData.Serialize(ShapeData.DefaultPolygon()),
                    ["Closed"]   = "true",
                    ["Feather"]  = "0",  ["Feather__Range"] = Range01,
                    ["Inverted"] = "false",
                });

            Add("Mask.Bezier", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Vertices"] = ShapeData.Serialize(ShapeData.DefaultBezier()),
                    ["Closed"]   = "true",
                    ["Feather"]  = "0",  ["Feather__Range"] = Range01,
                    ["Inverted"] = "false",
                });

            Add("Mask.Star", "Image",
                inputs:  Empty(),
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["CX"]          = "0.5",  ["CX__Range"]          = Range01,
                    ["CY"]          = "0.5",  ["CY__Range"]          = Range01,
                    ["OuterRadius"] = "0.4",  ["OuterRadius__Range"] = Range01,
                    ["InnerRadius"] = "0.18", ["InnerRadius__Range"] = Range01,
                    ["Points"]      = "5",    ["Points__Range"]      = "3..16",
                    ["Rotation"]    = "0",    ["Rotation__Range"]    = "-180..180",
                    ["Feather"]     = "0",    ["Feather__Range"]     = Range01,
                    ["Inverted"]    = "false",
                });

            // ── Math ops ──────────────────────────────────────────────────────
            // Scalar inputs are conventionally 0..1 normalised; outputs may exceed
            // the band when the operation produces it (Add, Mul). Authors clamp downstream
            // with Math.Clamp when needed.
            Add("Math.Add",    "Math", inputs: new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar) }, outputs: O("Out", SocketDataType.Scalar));
            Add("Math.Sub",    "Math", inputs: new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar) }, outputs: O("Out", SocketDataType.Scalar));
            Add("Math.Mul",    "Math", inputs: new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar) }, outputs: O("Out", SocketDataType.Scalar));
            Add("Math.Div",    "Math", inputs: new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar) }, outputs: O("Out", SocketDataType.Scalar));
            // Math.Lerp — Scalar overload (existing behaviour). T is conventionally 0..1.
            Add("Math.Lerp",   "Math", inputs: new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar), S("T", SocketDataType.Scalar) }, outputs: O("Out", SocketDataType.Scalar));
            Add("Math.Clamp",  "Math", inputs: new[] { S("V", SocketDataType.Scalar), S("Min", SocketDataType.Scalar), S("Max", SocketDataType.Scalar) }, outputs: O("Out", SocketDataType.Scalar));

            // Math.Lerp overloads for Vector2/3/4. Both sides resolve these: the C#
            // NodeEvaluator implements the per-component lerp via LerpVectorN (see the
            // Math.LerpVector2/3/4 cases in NodeEvaluator.cs), and compositor.js is the
            // live browser-side renderer. The math mirrors the scalar Math.Lerp path
            // (a + (b - a) * t) applied per component, with T shared across all components.
            Add("Math.LerpVector2", "Math",
                inputs: new[] { S("A", SocketDataType.Vector2), S("B", SocketDataType.Vector2), S("T", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Vector2),
                attrs: new Dictionary<string, string> { ["T__Range"] = Range01 });
            Add("Math.LerpVector3", "Math",
                inputs: new[] { S("A", SocketDataType.Vector3), S("B", SocketDataType.Vector3), S("T", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Vector3),
                attrs: new Dictionary<string, string> { ["T__Range"] = Range01 });
            Add("Math.LerpVector4", "Math",
                inputs: new[] { S("A", SocketDataType.Vector4), S("B", SocketDataType.Vector4), S("T", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Vector4),
                attrs: new Dictionary<string, string> { ["T__Range"] = Range01 });

            // Vector.Split / Vector.Combine — the existing Vector2 pair plus new
            // Vector3 and Vector4 variants. Naming follows the existing "Vector.Split"
            // pattern with the dimensionality suffixed (Vector3.Split etc.) so they
            // group cleanly in the catalog and don't clash with the Vector2 default.
            Add("Vector.Split",   "Vector", inputs: new[] { S("V", SocketDataType.Vector2) }, outputs: new[] { S("X", SocketDataType.Scalar), S("Y", SocketDataType.Scalar) });
            Add("Vector.Combine", "Vector", inputs: new[] { S("X", SocketDataType.Scalar), S("Y", SocketDataType.Scalar) }, outputs: O("V", SocketDataType.Vector2));

            Add("Vector3.Split",   "Vector",
                inputs:  new[] { S("V", SocketDataType.Vector3) },
                outputs: new[] { S("X", SocketDataType.Scalar), S("Y", SocketDataType.Scalar), S("Z", SocketDataType.Scalar) });
            Add("Vector3.Combine", "Vector",
                inputs:  new[] { S("X", SocketDataType.Scalar), S("Y", SocketDataType.Scalar), S("Z", SocketDataType.Scalar) },
                outputs: O("V", SocketDataType.Vector3));

            Add("Vector4.Split",   "Vector",
                inputs:  new[] { S("V", SocketDataType.Vector4) },
                outputs: new[] { S("X", SocketDataType.Scalar), S("Y", SocketDataType.Scalar), S("Z", SocketDataType.Scalar), S("W", SocketDataType.Scalar) });
            Add("Vector4.Combine", "Vector",
                inputs:  new[] { S("X", SocketDataType.Scalar), S("Y", SocketDataType.Scalar), S("Z", SocketDataType.Scalar), S("W", SocketDataType.Scalar) },
                outputs: O("V", SocketDataType.Vector4));

            // ── Triggers ──────────────────────────────────────────────────────
            // Visual.OnStartup is repurposed as an event-data source. Outputs
            // static metadata when the layer activates (LayerId, Timestamp). Flow remains
            // for graphs that wire to flow-aware downstream nodes (none today, but the
            // socket is reserved for future timeline / sequencer integration).
            Add("Visual.OnStartup", "Triggers",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Flow",      SocketDataType.Flow),
                    S("LayerId",   SocketDataType.String),
                    S("Timestamp", SocketDataType.Float),
                });

            // Visual.OnTrigger exposes the eventData payload that arrives in
            // RUN_TRIGGER from Hub. compositor.js stashes the live trigger context into
            // module state at trigger start; these outputs read from that.
            Add("Visual.OnTrigger", "Triggers",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Flow",        SocketDataType.Flow),
                    S("TriggerName", SocketDataType.String),
                    S("EventData",   SocketDataType.String),  // JSON-encoded payload
                    S("UserName",    SocketDataType.String),
                    S("Message",     SocketDataType.String),
                },
                attrs:   new Dictionary<string, string> { ["Name"] = "\"\"" });

            // ── Captions / Text ───────────────────────────────────────────────
            // Caption.LiveCaption — outputs the latest live-caption text from Hub's
            // LiveCaptionService. Two outputs: Original (raw) and Translated (post-translator,
            // may equal Original when no translator is wired or target lang is empty).
            //
            // Attributes:
            //   TargetLang  : string  empty (default) = pass-through original; non-empty
            //                         requests TranslationService at the layer level.
            //                         compositor.js read of this attribute is a follow-up
            //                         (separate ticket — JS lives in another agent's file).
            //   PreviewText : string  design-time placeholder. Used by the canvas thumbnail
            //                         so the widget shows something instead of an empty box
            //                         until a real caption arrives.
            // Moved from "Inputs" → "Text" so the registration matches the
            // AllowedCategories docstring in WidgetNodeRegistry (and the project docs's "Text covers caption
            // rendering" rationale). Caption.LiveCaption is a text-producing node, not
            // a passive Inputs-style constant — keeping it under Inputs created drift
            // between the documented spec and the actual catalog bucket. The "Text"
            // category is already in AllowedCategories (see the AllowedCategories rationale block), so this swap
            // is a pure recategorisation; downstream consumers key by Title, not
            // Category, so existing wires + serialised graphs round-trip unchanged.
            Add("Caption.LiveCaption", "Text",
                inputs:  Empty(),
                outputs: new[] { S("Text", SocketDataType.String), S("Translated", SocketDataType.String) },
                attrs:   new Dictionary<string, string>
                {
                    ["TargetLang"]  = "\"\"",
                    ["PreviewText"] = "\"\"",
                });

            // Text.Translate — explicit translation node. compositor.js sends a TRANSLATE_REQUEST
            // over the layer's WebSocket and resolves on TRANSLATE_RESPONSE. Cached client-side
            // by (text, targetLang) so repeated frames don't refire round-trips.
            Add("Text.Translate", "Text",
                inputs:  new[] { S("Text", SocketDataType.String), S("TargetLang", SocketDataType.String) },
                outputs: O("Translated", SocketDataType.String),
                attrs:   new Dictionary<string, string> { ["TargetLang"] = "\"\"" });

            // Text.Render — rasterises a string into an Image socket via OffscreenCanvas + fillText.
            // Drives the standard Display sink so any caption / text widget can flow through the
            // existing Image pipeline.
            Add("Text.Render", "Text",
                inputs:  new[]
                {
                    S("Text",       SocketDataType.String),
                    // FontSize is a raw pixel size (default 32), not a
                    // 0..1 normalized value. §4.6 defines Scalar as "0..1 normalized",
                    // so Float is the honest type (both render as a circle pin).
                    S("FontSize",   SocketDataType.Float),
                    S("FontFamily", SocketDataType.String),
                    S("Color",      SocketDataType.Color),
                    S("Alignment",  SocketDataType.String),
                    // Optional outline. StrokeWidth is a
                    // raw pixel width (Float, default 0 = no outline); StrokeColor
                    // gets the Inspector colour picker automatically (Color type).
                    // compositor.js strokeText()s before fillText() when width > 0.
                    S("StrokeColor", SocketDataType.Color),
                    S("StrokeWidth", SocketDataType.Float),
                },
                outputs: O("Image", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["FontSize"]              = "32",
                    ["FontFamily"]            = "\"Inter\"",
                    ["Color"]                 = "\"#ffffff\"",
                    ["Alignment"]             = "\"center\"",
                    ["Alignment__KnownValues"] = "left,center,right",
                    // Default off (width 0). Black is the
                    // conventional outline colour; only used when width > 0.
                    ["StrokeColor"]          = "\"#000000\"",
                    ["StrokeWidth"]          = "0",
                    ["StrokeWidth__Range"]   = "0..16",
                });

            // ── Sinks ─────────────────────────────────────────────────────────
            // Display registered in the catalog so authors can discover it via the
            // Add-Node menu. Instantiation is short-circuited inside WidgetNodeRegistry.Instantiate,
            // which always returns a fresh DisplaySinkNode.Build() regardless of the template
            // sockets registered here — so the template fields exist purely for catalog
            // listing + per-socket-type colour-coding.
            //
            // The "IsAutoInjected" attribute is informational metadata: editors that grow
            // a Hidden/AutoInjected flag in WidgetNodeRegistry.NodeTemplate (out-of-scope —
            // touching that file would cross another agent's boundary) can read it to
            // hide Display from the catalog while keeping it available in the dictionary
            // for round-trip serialisation.
            Add(DisplaySinkNode.Title, DisplaySinkNode.Category,
                inputs:  new[] { S("Image", SocketDataType.Image) },
                outputs: Empty(),
                attrs:   new Dictionary<string, string>
                {
                    ["IsAutoInjected"] = "true",
                });

            // Audio.Play — second sink kind. Consumes an Audio value (typically
            // wired from Audio.Load) and triggers playback in compositor.js.
            // Optional — graphs that don't author it produce no audio.
            // Volume defaults to full; Loop off (one-shot SFX is the common case;
            // music/ambience flips Loop on).
            Add(AudioSinkNode.Title, AudioSinkNode.Category,
                inputs:  new[] { S("Audio", SocketDataType.Audio) },
                outputs: Empty(),
                attrs:   new Dictionary<string, string>
                {
                    ["Volume"]        = "1.0",
                    ["Volume__Range"] = Range01,
                    ["Loop"]          = "false",
                });

            // Visual.Complete — graph-authored completion sink. When the trigger graph
            // reaches this node during evaluation, compositor.js fires VISUAL_COMPLETE back
            // to Hub so wait_for_visual scripts proceed on the Done branch. If a graph has
            // no Visual.Complete, the engine falls back to firing on first Display render.
            // The Image input is for symmetry with Display; the value is not rendered.
            //
            // Originally categorised as "Sink" alongside Display, but the semantics
            // differ: Display draws pixels, Visual.Complete signals lifecycle completion.
            // The ideal category is "Signal" or "Lifecycle", but WidgetNodeRegistry's
            // AllowedCategories is the single source of truth for permitted strings and
            // expanding it would require touching that file (forbidden in this batch — see
            // commit message). Best fit within the current set is "Triggers": Visual.Complete
            // is the lifecycle counterpart to Visual.OnTrigger / Visual.OnStartup, so the
            // editor catalog now groups the trigger lifecycle nodes together. When a future
            // patch adds "Signal" / "Lifecycle" to AllowedCategories, retarget here.
            Add("Visual.Complete", "Triggers",
                inputs:  new[] { S("In", SocketDataType.Any) },
                outputs: Empty());

            // Result.If — barrier between an Image source and Display (or any downstream
            // image consumer). Reads eventData[When] (e.g. "Args1") and compares to Equals.
            // On match, In passes through to Out. On mismatch — or if the named arg was
            // never supplied by the script-side Visual.Trigger — the branch is blocked
            // (Out emits no image), and a once-per-fire LogicExecution log entry surfaces
            // the missing-arg case so authors notice mis-typed When attributes. Compose
            // multiple Result.If instances in parallel upstream of Display for mutually-
            // exclusive routes (e.g. gamble outcome → win/loss/draw image).
            Add("Result.If", "Triggers",
                inputs:  new[]
                {
                    S("In",     SocketDataType.Image),
                    S("Equals", SocketDataType.String),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["When"]   = "\"Args1\"",
                    ["Equals"] = "\"\"",
                });

            // ── Debug ─────────────────────────────────────────────────────────
            // Viewer is a passthrough: any value flowing in goes straight
            // to Out, so it can be dropped on a wire without breaking the chain. The
            // canvas paints a Fusion-style live thumbnail in the body by walking
            // upstream from the Viewer's "In" socket to the nearest resolvable Image
            // source (PreviewSource.UpstreamImage) — both Image.Load file paths and
            // Image.LoadUrl urls terminate the walk. When no such source exists the
            // body shows the unloaded placeholder (empty card + hint label) instead
            // of a busy checker pattern, so the node reads as "preview-capable,
            // awaiting input" rather than "broken". Compositor.js still drives the
            // runtime render — the thumbnail is design-time only.
            Add("Viewer", "Debug",
                inputs:  new[] { S("In",  SocketDataType.Any) },
                outputs: new[] { S("Out", SocketDataType.Any) },
                previewSource: PreviewSource.UpstreamImage);

            // Particles.Emit emitter. The compositor.js runtime is
            // live: a tick-based per-widget emitter renders the sprite field
            // and hooks the widget into a continuous rAF loop
            // (requestWidgetAnimator) so particles keep flowing between
            // triggers.
            //
            // Shape choice: 2D-sprite emitter. Simpler than a 3D engine,
            // doesn't pull in an external dep, composes via the same Image
            // kernel pipeline every other Image.* node uses. Inputs are
            // Position + Velocity (Vector2 each, unit space), Lifetime
            // (seconds), Rate (particles per second), Color. Output is the
            // rendered particle field as an Image. compositor.js binds the
            // canvas size at render time the same way Image.Mosaic does.
            //
            // 3D-particle / CSS-keyframe alternatives stay open for a future
            // sweep — the template Title is the contract; swapping the
            // runtime kernel is non-disruptive to authored graphs as long
            // as the socket shape stays stable.
            // Vector2 attributes now persist as the canonical
            // per-component scheme (PositionX/PositionY, VelocityX/VelocityY)
            // the rest of the Visualist catalog uses for Vector2 sockets
            // (compare Image.Transform: TranslateX/Y, ScaleX/Y). The previous
            // comma-CSV form ("0.5, 0.5") didn't round-trip through
            // AnimatedPinRegistry.ReadComponentLiteral — that helper expects
            // a single scalar per attribute key matching <SocketName><Axis>.
            // CSV-form legacy graphs are migrated on load in
            // LayerDocument.Open (Position/Velocity → PositionX/Y, VelocityX/Y).
            Add("Particles.Emit", "Image",
                inputs: new[]
                {
                    S("Position", SocketDataType.Vector2),
                    S("Velocity", SocketDataType.Vector2),
                    S("Lifetime", SocketDataType.Scalar),
                    S("Rate",     SocketDataType.Scalar),
                    S("Color",    SocketDataType.Color),
                },
                outputs: O("Image", SocketDataType.Image),
                attrs: new Dictionary<string, string>
                {
                    // Position: unit-space centre (0.5, 0.5).
                    { "PositionX", "0.5" },
                    { "PositionY", "0.5" },
                    { "PositionX__Range", Range01 },
                    { "PositionY__Range", Range01 },
                    // Velocity: gentle upward drift (0, -0.2).
                    { "VelocityX", "0" },
                    { "VelocityY", "-0.2" },
                    { "VelocityX__Range", RangeBipolar },
                    { "VelocityY__Range", RangeBipolar },
                    { "Lifetime", "1.5" },      // seconds
                    { "Rate",     "20" },       // particles per second
                    { "Color",    "#ffffffff" },
                });

            // WebSource — url-driven image source with a refresh knob.
            // The compositor.js runtime is live: it fetches the Url through
            // the Hub-side /asset/url proxy (same path Image.LoadUrl uses)
            // and renders image content-types only, re-fetching every
            // RefreshSeconds via a cache-busting bucket param. A non-image
            // URL (an HTML page) is rejected at proxy validation and the
            // runtime surfaces a clear WebSource-specific console.warn so
            // authors see "the URL didn't return an image" rather than a
            // generic load failure. Full iframe/HTML rasterisation is
            // deliberately NOT built — browsers cannot paint a cross-origin
            // iframe to a canvas, so there is no honest drawImage path for
            // arbitrary pages.
            //
            // The node carries a Url attribute (the resource to fetch) and a
            // RefreshSeconds attribute (how often the runtime re-fetches).
            // Output is the fetched surface as an Image — same shape every
            // other Image.* source node returns. Preview thumbnail is OwnUrl:
            // the design-time canvas loads the Url directly as a bitmap; an
            // image URL previews in the node body, and a non-image URL (an
            // HTML page) fails the async decode and flips the thumbnail host
            // to its "(image unavailable)" hint via ImageFailed.
            Add("WebSource", "Inputs",
                inputs:  Empty(),
                outputs: O("Image", SocketDataType.Image),
                attrs: new Dictionary<string, string>
                {
                    { "Url",            "https://example.com" },
                    { "RefreshSeconds", "5" },
                },
                previewSource: PreviewSource.OwnUrl);

            // ── Math expansion ────────────────────────────────────────────────
            // Numeric scalar kernels extending the Math.Add/Sub/Mul/Div/Lerp/Clamp
            // core above. Each carries its operands as keyframeable scalar attributes
            // (read via attrAnimated in compositor.js); a wired input on the same-named
            // socket overrides the attribute default, matching Image.Scale's "Factor".
            // Scalars are conventionally 0..1 normalised, but these operators accept and
            // produce values outside the band — clamp downstream with Math.Clamp.
            Add("Math.Mod", "Math",
                inputs:  new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["A"] = "0", ["B"] = "1" });

            Add("Math.Pow", "Math",
                inputs:  new[] { S("Base", SocketDataType.Scalar), S("Exp", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["Base"] = "1", ["Exp"] = "2" });

            Add("Math.Min", "Math",
                inputs:  new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["A"] = "0", ["B"] = "0" });

            Add("Math.Max", "Math",
                inputs:  new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["A"] = "0", ["B"] = "0" });

            Add("Math.Abs", "Math",
                inputs:  new[] { S("V", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["V"] = "0" });

            Add("Math.Sqrt", "Math",
                inputs:  new[] { S("V", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["V"] = "0" });

            Add("Math.Floor", "Math",
                inputs:  new[] { S("V", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["V"] = "0" });

            Add("Math.Ceil", "Math",
                inputs:  new[] { S("V", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["V"] = "0" });

            Add("Math.Round", "Math",
                inputs:  new[] { S("V", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["V"] = "0" });

            // Sign returns -1 / 0 / 1.
            Add("Math.Sign", "Math",
                inputs:  new[] { S("V", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["V"] = "0" });

            Add("Math.Negate", "Math",
                inputs:  new[] { S("V", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["V"] = "0" });

            // Trig inputs are in DEGREES (converted to radians kernel-side).
            Add("Math.Sin", "Math",
                inputs:  new[] { S("Degrees", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["Degrees"] = "0" });

            Add("Math.Cos", "Math",
                inputs:  new[] { S("Degrees", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["Degrees"] = "0" });

            Add("Math.Tan", "Math",
                inputs:  new[] { S("Degrees", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["Degrees"] = "0" });

            // Remap rescales V from [InMin,InMax] to [OutMin,OutMax]; a zero input
            // span (InMax==InMin) yields t=0 so the result pins to OutMin.
            Add("Math.Remap", "Math",
                inputs:  new[]
                {
                    S("V",      SocketDataType.Scalar),
                    S("InMin",  SocketDataType.Scalar),
                    S("InMax",  SocketDataType.Scalar),
                    S("OutMin", SocketDataType.Scalar),
                    S("OutMax", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string>
                {
                    ["V"]      = "0",
                    ["InMin"]  = "0",
                    ["InMax"]  = "1",
                    ["OutMin"] = "0",
                    ["OutMax"] = "1",
                });

            // Compare returns 1.0 when the comparison holds, else 0.0. Equal/NotEqual
            // use a small epsilon (1e-6) so float drift doesn't flip the result.
            Add("Math.Compare", "Math",
                inputs:  new[] { S("A", SocketDataType.Scalar), S("B", SocketDataType.Scalar) },
                outputs: O("Result", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string>
                {
                    ["A"]                 = "0",
                    ["B"]                 = "0",
                    ["Mode"]              = "\"GreaterThan\"",
                    ["Mode__KnownValues"] = "GreaterThan,LessThan,GreaterOrEqual,LessOrEqual,Equal,NotEqual",
                });

            // ── Time / Animation ──────────────────────────────────────────────
            // Clock-driven scalar sources. compositor.js reads the live clock from
            // triggerContext.timeMs (ms → seconds); the C# design-time mirror has no
            // clock, so it evaluates at t=0. Keyframeable scalar attributes use
            // attrAnimated in JS; Mode/enum attrs use plain attr.
            Add("Time.Elapsed", "Time",
                inputs:  Empty(),
                outputs: O("Seconds", SocketDataType.Scalar));

            // Oscillator: Offset + Amplitude * sin(2π·(Frequency·t + Phase)).
            Add("Time.Oscillator", "Time",
                inputs:  new[]
                {
                    S("Frequency", SocketDataType.Scalar),
                    S("Amplitude", SocketDataType.Scalar),
                    S("Phase",     SocketDataType.Scalar),
                    S("Offset",    SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string>
                {
                    ["Frequency"] = "1",
                    ["Amplitude"] = "1",
                    ["Phase"]     = "0",
                    ["Offset"]    = "0",
                });

            // Sawtooth: ramps 0..1 over each Period (seconds), then resets.
            Add("Time.Sawtooth", "Time",
                inputs:  new[] { S("Period", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string>
                {
                    ["Period"]        = "1",
                    ["Period__Range"] = "0.01..60",
                });

            // Easing: clamps T to 0..1 and applies the named curve. The C# mirror
            // reuses KeyframeInterpolation.ApplyCurve; compositor.js mirrors the
            // same formulas (Linear=t, EaseIn=t², EaseOut=1-(1-t)², EaseInOut
            // piecewise, Step=0).
            Add("Time.Easing", "Time",
                inputs:  new[] { S("T", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string>
                {
                    ["T"]                 = "0",
                    ["T__Range"]          = Range01,
                    ["Mode"]              = "\"EaseInOut\"",
                    ["Mode__KnownValues"] = "Linear,EaseIn,EaseOut,EaseInOut,Step",
                });

            // ── String ────────────────────────────────────────────────────────
            // Pure string transforms — no Flow socket. String attrs use plain attr
            // (not keyframeable); a wired same-named input overrides the attribute.
            Add("String.Concat", "String",
                inputs:  new[] { S("A", SocketDataType.String), S("B", SocketDataType.String) },
                outputs: O("Out", SocketDataType.String),
                attrs:   new Dictionary<string, string> { ["A"] = "\"\"", ["B"] = "\"\"" });

            Add("String.Upper", "String",
                inputs:  new[] { S("In", SocketDataType.String) },
                outputs: O("Out", SocketDataType.String),
                attrs:   new Dictionary<string, string> { ["In"] = "\"\"" });

            Add("String.Lower", "String",
                inputs:  new[] { S("In", SocketDataType.String) },
                outputs: O("Out", SocketDataType.String),
                attrs:   new Dictionary<string, string> { ["In"] = "\"\"" });

            Add("String.Length", "String",
                inputs:  new[] { S("In", SocketDataType.String) },
                outputs: O("Length", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["In"] = "\"\"" });

            // Slice: substring from Start for Count chars. Start clamps to [0,len];
            // a negative Count means "to the end" (len-Start).
            Add("String.Slice", "String",
                inputs:  new[]
                {
                    S("In",    SocketDataType.String),
                    S("Start", SocketDataType.Scalar),
                    S("Count", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.String),
                attrs:   new Dictionary<string, string>
                {
                    ["In"]    = "\"\"",
                    ["Start"] = "0",
                    ["Count"] = "-1",
                });

            // Replace: swaps ALL occurrences of Find with With. An empty Find
            // returns In unchanged.
            Add("String.Replace", "String",
                inputs:  new[]
                {
                    S("In",   SocketDataType.String),
                    S("Find", SocketDataType.String),
                    S("With", SocketDataType.String),
                },
                outputs: O("Out", SocketDataType.String),
                attrs:   new Dictionary<string, string>
                {
                    ["In"]   = "\"\"",
                    ["Find"] = "\"\"",
                    ["With"] = "\"\"",
                });

            // ── Convert ───────────────────────────────────────────────────────
            // Type bridges between Scalar / String / Color. Scalar inputs are
            // keyframeable (attrAnimated in JS); String/enum attrs use plain attr.
            // NumberToString formats V with the given decimal places.
            Add("Convert.NumberToString", "Convert",
                inputs:  new[] { S("V", SocketDataType.Scalar) },
                outputs: O("Out", SocketDataType.String),
                attrs:   new Dictionary<string, string>
                {
                    ["V"]                 = "0",
                    ["Decimals"]          = "0",
                    ["Decimals__Range"]   = "0..6",
                });

            // StringToNumber: parseFloat; NaN / empty resolves to 0.
            Add("Convert.StringToNumber", "Convert",
                inputs:  new[] { S("In", SocketDataType.String) },
                outputs: O("Out", SocketDataType.Scalar),
                attrs:   new Dictionary<string, string> { ["In"] = "\"\"" });

            // ColorFromRGBA: each channel clamps 0..255 → int; output is a
            // #rrggbbaa hex string (alpha last).
            Add("Convert.ColorFromRGBA", "Convert",
                inputs:  new[]
                {
                    S("R", SocketDataType.Scalar),
                    S("G", SocketDataType.Scalar),
                    S("B", SocketDataType.Scalar),
                    S("A", SocketDataType.Scalar),
                },
                outputs: O("Color", SocketDataType.Color),
                attrs:   new Dictionary<string, string>
                {
                    ["R"]        = "255", ["R__Range"] = "0..255",
                    ["G"]        = "255", ["G__Range"] = "0..255",
                    ["B"]        = "255", ["B__Range"] = "0..255",
                    ["A"]        = "255", ["A__Range"] = "0..255",
                });

            // HexToColor: normalises / validates #rgb, #rrggbb, #rrggbbaa; an
            // invalid hex falls back to #ffffff.
            Add("Convert.HexToColor", "Convert",
                inputs:  new[] { S("Hex", SocketDataType.String) },
                outputs: O("Color", SocketDataType.Color),
                attrs:   new Dictionary<string, string> { ["Hex"] = "\"#ffffff\"" });

            // ── Read-out / Trigger ────────────────────────────────────────────
            // Message.Read — the key gap closer: reads a named field out of the
            // transmitted trigger payload so a graph can render the string a
            // Visual.Trigger sent. compositor.js reads triggerContext.eventData[Key];
            // the C# design-time mirror returns MockValue so the canvas/preview is
            // not blind to the transmitted string. Pure-data String producer (no
            // Flow socket). Key/MockValue are plain string attrs.
            Add("Message.Read", "Triggers",
                inputs:  Empty(),
                outputs: O("Value", SocketDataType.String),
                attrs:   new Dictionary<string, string>
                {
                    ["Key"]       = "\"Args1\"",
                    ["MockValue"] = "\"\"",
                });
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static void Add(string title, string category,
            WidgetNodeRegistry.SocketSpec[] inputs,
            WidgetNodeRegistry.SocketSpec[] outputs,
            Dictionary<string, string>? attrs = null,
            PreviewSource previewSource = PreviewSource.None)
        {
            attrs ??= new Dictionary<string, string>();
            // Stamp the legacy `__Preview = "true"` attribute when the template
            // opts into a body preview. Keeps WidgetGraphCanvas.HasBodyPreview
            // working without forcing every template to repeat the flag.
            if (previewSource != PreviewSource.None)
                attrs[PreviewAttrKey] = "true";

            WidgetNodeRegistry.Add(new WidgetNodeRegistry.NodeTemplate(
                Title: title,
                Category: category,
                Inputs: inputs,
                Outputs: outputs,
                DefaultAttributes: attrs));

            _previewByTitle[title] = previewSource;
        }

        private static WidgetNodeRegistry.SocketSpec S(string name, SocketDataType dt) => new(name, dt);
        private static WidgetNodeRegistry.SocketSpec[] O(string name, SocketDataType dt) => new[] { new WidgetNodeRegistry.SocketSpec(name, dt) };
        private static WidgetNodeRegistry.SocketSpec[] Empty() => System.Array.Empty<WidgetNodeRegistry.SocketSpec>();
    }
}
