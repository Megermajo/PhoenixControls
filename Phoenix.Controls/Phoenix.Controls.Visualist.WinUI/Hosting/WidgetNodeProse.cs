using System.Collections.Generic;

namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// Authored content layer for the in-app <b>Visualist</b> Widget Node Reference.
/// The structure of every node card (category, sockets, types, default chips) is
/// generated live from <c>WidgetNodeRegistry</c> by
/// <see cref="WidgetNodeReferenceData"/> so it can never drift from the widget
/// graph editor; this file supplies the human-written layer on top:
///
///   • <see cref="Categories"/> — the display order, accent colour and one-line
///     blurb per real registry category.
///   • <see cref="Nodes"/> — summary / description / "typical use" prose for every
///     node the widget palette can spawn, keyed by the REAL registry title. A node
///     without an entry falls back to the registry tooltip / its title.
///
/// Mirrors Architect's <c>NodeProse</c> by design (per-pillar copy — the two
/// editors keep their own content layers, see the chrome-independence rule).
/// Prose strings are HTML fragments (rendered via innerHTML) — keep them to the
/// small inline vocabulary the design uses: &lt;p&gt;, &lt;b&gt;, &lt;em&gt;, &lt;code&gt;.
///
/// Accuracy contract: every claim is checked against the live WidgetNodeRegistry
/// templates and the design-time NodeEvaluator (the C# mirror of the browser
/// compositor). Scalars are 0–1 by convention unless a node says otherwise.
/// </summary>
internal static class WidgetNodeProse
{
    internal readonly record struct CategoryMeta(string Category, string Color, string Blurb, string? Badge = null);

    /// <summary>Ordered category presentation. Categories present in the registry
    /// but absent here are appended after these alphabetically with a neutral accent.</summary>
    internal static readonly IReadOnlyList<CategoryMeta> Categories = new[]
    {
        new CategoryMeta("Triggers", "#7A2E2E",
            "Where a widget graph begins. <code>Visual.OnStartup</code> fires when the layer loads; <code>Visual.OnTrigger</code> fires when Architect sends a <code>Visual.Trigger</code>, exposing its event data. <code>Result.If</code> gates a branch on that data, and <code>Visual.Complete</code> tells Hub the effect has finished."),
        new CategoryMeta("Inputs", "#1F5F6B",
            "Sources — where pixels, colours and numbers enter the graph. Image / video / audio loaders, colour and number constants, and the layer resolution. Most have no inputs; you set their value inline on the node."),
        new CategoryMeta("Image", "#3A1F4E",
            "The compositing chain — every node takes an <code>Image</code> and hands one down. Procedural masks and shapes, blends, blurs, transforms and effects. Canonical order is <em>Load → Crop → ColorAdjust → Transform → effects → Mask → Blend → Display</em>."),
        new CategoryMeta("Text", "#2E5A4A",
            "Turn strings into pixels and captions into overlays. <code>Text.Render</code> rasterises a string to an <code>Image</code>; <code>Caption.LiveCaption</code> and <code>Text.Translate</code> feed live, optionally-translated captions."),
        new CategoryMeta("Math", "#4A5A2E",
            "Scalar arithmetic and trig — the maths behind every animated value. Pure data: wire a <code>Scalar</code> in, get a <code>Scalar</code> out. Drive these from <code>Time.*</code> to animate anything."),
        new CategoryMeta("Vector", "#5A4A2E",
            "Build, split and interpolate 2-, 3- and 4-component vectors — positions, scales, rectangles and colours-as-numbers. Combine scalars into a vector, or split one back into scalars."),
        new CategoryMeta("Time", "#7A5414",
            "Clock-driven sources — the heartbeat of motion. Elapsed seconds, oscillators, sawtooth ramps and easing curves. At design time they read <code>t = 0</code>; in OBS they advance with the trigger clock."),
        new CategoryMeta("String", "#2E4A5A",
            "String transforms — concat, case, slice, replace, length. Feed them from <code>Visual.OnTrigger</code> event fields and into <code>Text.Render</code>."),
        new CategoryMeta("Convert", "#4A3F57",
            "Type bridges — number↔string and the two ways to build a <code>Color</code> (from RGBA scalars or a hex string). Cheap and pure."),
        new CategoryMeta("Sink", "#3F4860",
            "Terminal nodes — where the graph ends. <code>Display</code> is the auto-injected image output (one per trigger); <code>Audio.Play</code> is the optional sound output. The compositor evaluates by pulling upstream from these."),
        new CategoryMeta("Debug", "#3A4257",
            "Inspection helpers. <code>Viewer</code> passes its input straight through while painting a live thumbnail of the image flowing through it — DaVinci-Fusion style."),
    };

    internal readonly record struct NodeInfo(
        string? Summary = null,
        string? Description = null,
        string? Example = null,
        string? Since = null,
        string? Badge = null);

    internal static readonly IReadOnlyDictionary<string, NodeInfo> Nodes = new Dictionary<string, NodeInfo>
    {
        // ════════════════════════════ TRIGGERS ════════════════════════════
        ["Visual.OnStartup"] = new(
            Summary: "Fires once when the layer loads — the entry point for a widget's resting state.",
            Description: "<p>Use the <code>onStartup</code> trigger for whatever the widget shows when nothing is happening. <code>LayerId</code> and <code>Timestamp</code> are available; the <code>Flow</code> pin is reserved for future sequencing.</p>",
            Example: "Show a logo image at rest until a trigger swaps it for an alert."),
        ["Visual.OnTrigger"] = new(
            Summary: "Fires when Architect sends a Visual.Trigger to this widget, exposing the event data.",
            Description: "<p>The bridge from logic to visuals. <code>TriggerName</code> is which trigger fired; <code>UserName</code> and <code>Message</code> are the common fields Hub forwards; <code>EventData</code> is the raw JSON for anything else. Positional <code>Args</code> arrive as <code>Args1</code>, <code>Args2</code>… readable via <code>Result.If</code>.</p>",
            Example: "On a <code>greet</code> trigger &rarr; render <code>\"Welcome, {Message}\"</code> with the user's name."),
        ["Result.If"] = new(
            Summary: "Passes an image through only when an event-data field matches a value.",
            Description: "<p>A branch barrier. It reads <code>eventData[When]</code> (e.g. <code>When = Args1</code>) and compares it to <code>Equals</code> (inline or wired). On a match the image flows through; on a mismatch the branch goes dark — wire two of these to show different visuals per outcome.</p>",
            Example: "<code>When=Args1</code>, <code>Equals=win</code> &rarr; show the victory burst; a second one for <code>lose</code>."),
        ["Visual.Complete"] = new(
            Summary: "Signals Hub that the effect has finished — closes the loop on Async.WaitForVisual.",
            Description: "<p>When evaluation reaches this node the browser sends <code>VISUAL_COMPLETE</code> back to Hub, so an Architect <code>Async.WaitForVisual</code> continues on its <code>Done</code> branch. If you don't place one, Hub falls back to the first <code>Display</code> render.</p>",
            Example: "End a multi-second entrance animation so the script can post the welcome line after."),
        ["Message.Read"] = new(
            Summary: "Reads the trigger's message text as a string.",
            Description: "<p>A convenience source for the message field carried by the firing trigger — wire it straight into <code>Text.Render</code> or a <code>String.*</code> transform.</p>",
            Example: "Pull the chat message into an on-screen caption."),

        // ═════════════════════════════ INPUTS ═════════════════════════════
        ["Image.Load"] = new(
            Summary: "Loads a local image file. The most common pixel source.",
            Description: "<p>Set the file path inline. The node shows a live thumbnail of the image on its body. Feed it into the image chain or straight into <code>Display</code>.</p>",
            Example: "Load your alert PNG, scale it, then blend it over a gradient."),
        ["Image.LoadUrl"] = new(
            Summary: "Loads an image from a URL, proxied through Hub so it works offline-of-CORS.",
            Description: "<p>Like <code>Image.Load</code> but from the web. Hub fetches and caches it; the browser decodes it at render time.</p>",
            Example: "Pull a viewer's Twitch avatar into a welcome card."),
        ["Video.Load"] = new(
            Summary: "Loads a video from the media library — outputs its frames and duration.",
            Description: "<p>Outputs an <code>Image</code> frame stream plus the clip <code>Duration</code> in seconds. Loops and starts muted by default (OBS autoplay rules). One video element is reused per node.</p>",
            Example: "Play a looping background video behind a transparent overlay."),
        ["Audio.Load"] = new(
            Summary: "Loads an audio file. Not drawable — feed it to the Audio.Play sink.",
            Description: "<p>Outputs an <code>Audio</code> handle. Wiring it into <code>Display</code> does nothing; it's meant for <code>Audio.Play</code>.</p>",
            Example: "Load an alert sting and play it when the widget triggers."),
        ["Color.Constant"] = new(
            Summary: "A solid colour, set inline as a hex value.",
            Description: "<p>Outputs a typed <code>Color</code>. Wiring a colour where an image is expected raises an explicit error rather than guessing — convert it first if you want a colour fill.</p>",
            Example: "A brand-colour swatch fed into a gradient or text stroke."),
        ["Scalar.Constant"] = new(
            Summary: "A fixed number, 0–1 by convention. The simplest animatable value.",
            Description: "<p>Outputs a <code>Scalar</code>. Values outside 0–1 are allowed; downstream nodes decide whether to clamp. Keyframe its value to animate anything wired to it.</p>",
            Example: "A 0.5 opacity you keyframe up to 1.0 for a fade-in."),
        ["String.Constant"] = new(
            Summary: "A fixed text value.",
            Description: "<p>Outputs a <code>String</code>. Feed it into <code>Text.Render</code> or a <code>String.*</code> transform.</p>",
            Example: "A static \"NOW LIVE\" caption."),
        ["Vector.Rect4"] = new(
            Summary: "A rectangle as a Vector4 (X, Y, W, H), in 0–1 fractions.",
            Description: "<p>A friendly alias for <code>Vector4.Constant</code> using rect names — made for <code>Image.Crop</code>'s <code>Rect</code> input. Fractions of the source, not pixels.</p>",
            Example: "Crop to the top-left quarter with <code>0, 0, 0.5, 0.5</code>."),
        ["Math.Resolution"] = new(
            Summary: "Outputs the layer resolution as a Vector2 (width, height).",
            Description: "<p>A constant source for the canvas size, so positions and scales can be resolution-independent. Collapses to width when read as a scalar.</p>",
            Example: "Centre something at <code>resolution / 2</code>."),
        ["WebSource"] = new(
            Summary: "Renders a web page as an image source (scaffold).",
            Description: "<p>Set a <code>Url</code> and a refresh interval; the page is snapshotted into an <code>Image</code>. A design-time scaffold — the live render path is still being wired.</p>",
            Example: "Embed a live web widget (timer, poll) as an overlay layer."),

        // ══════════════════════════════ IMAGE ═════════════════════════════
        ["Image.Scale"] = new(Summary: "Scales an image by a factor (1 = identity, <1 shrinks, >1 grows).", Description: "<p>A wired <code>Factor</code> wins over the inline value. Floored just above 0 to avoid a 0×0 image.</p>", Example: "Pulse a logo by keyframing <code>Factor</code> 1.0 → 1.1 → 1.0."),
        ["Image.Transform"] = new(Summary: "Moves, rotates and scales an image per-axis.", Description: "<p><code>Translate</code> in pixels, <code>Rotate</code> in degrees, <code>Scale</code> per axis. Each socket overrides its inline value. Sits after crop / grade, before tile / mask / blend.</p>", Example: "Slide an alert in from the left and settle it with an easing curve."),
        ["Image.Crop"] = new(Summary: "Clips an image to a rectangle, given as 0–1 fractions.", Description: "<p><code>Rect</code> is a Vector4 (x, y, w, h) in fractions of the source — not pixels. Default <code>0,0,1,1</code> passes the whole image through. The first coordinate op after load.</p>", Example: "Show just a face crop of a larger image."),
        ["Image.ColorAdjust"] = new(Summary: "Grades an image — hue shift, saturation, brightness, contrast.", Description: "<p>All inputs optional; wired sockets override the inline values. Sits after crop, before transform.</p>", Example: "Desaturate a background so the foreground pops."),
        ["Image.Blend"] = new(Summary: "Composes image B over image A with a CSS blend mode and opacity.", Description: "<p><code>Mode</code> is a standard CSS blend mode (normal, multiply, screen…); an invalid one falls back to normal. The last visual op before <code>Display</code>.</p>", Example: "Screen a glow layer over your base alert."),
        ["Image.Combine"] = new(Summary: "Blend plus keying — derive alpha from the top layer's pixels.", Description: "<p>Everything <code>Image.Blend</code> does, plus <code>alpha-key</code>, <code>luminance-key</code> and <code>inv-luminance</code> modes that cut out the top layer by its own values. Both inputs accept any image producer (shapes, gradients, photos).</p>", Example: "Luminance-key a white-on-black logo so only the logo shows."),
        ["Image.Mask"] = new(Summary: "Masks an image with another image's alpha or luminance.", Description: "<p>Both <code>Image</code> and <code>Mask</code> are required — leaving <code>Mask</code> unwired raises an explicit error. <code>Mode</code> chooses alpha-channel or luminance masking.</p>", Example: "Mask a video into a circle with <code>Mask.Circle</code>."),
        ["Image.Tile"] = new(Summary: "Tiles an image to fill the widget, with per-axis repeat and wrap.", Description: "<p><code>Repeat</code> per axis (fractional allowed); <code>Wrap</code> is repeat / mirror / clamp. Output matches the widget rect.</p>", Example: "Tile a small pattern across a banner background."),
        ["Image.Blur"] = new(Summary: "Isotropic blur by a pixel radius (0 = no-op).", Description: "<p>A fast canvas blur. For directional blur use <code>Image.Gaussian</code>.</p>", Example: "Soften a backdrop behind sharp text."),
        ["Image.Gaussian"] = new(Summary: "Directional Gaussian blur with separate X and Y sigma.", Description: "<p>Both 0 = passthrough. Lets you blur more on one axis than the other.</p>", Example: "A horizontal motion-blur streak."),
        ["Image.Shadow"] = new(Summary: "Drop-shadow with offset, blur and colour.", Description: "<p>Output stays the source size, so a shadow outside the bounds is clipped (CSS box-shadow semantics). Colour is set inline.</p>", Example: "Lift text off a busy background with a soft shadow."),
        ["Image.Glow"] = new(Summary: "Outer glow built from stacked zero-offset shadows.", Description: "<p><code>Radius</code> and <code>Intensity</code> (stacked passes) shape the halo; <code>Color</code> is inline. Radius 0 or intensity 0 = passthrough.</p>", Example: "A neon glow around a logo on a cheer alert."),
        ["Image.Distort"] = new(Summary: "Wave / ripple geometric distortion.", Description: "<p><code>wave</code> displaces rows horizontally; <code>ripple</code> bows both axes. <code>Amplitude</code> 0 = passthrough.</p>", Example: "A watery shimmer on a victory screen."),
        ["Image.Mosaic"] = new(Summary: "Pixelates an image by a tile size.", Description: "<p>Downscale-then-upscale with smoothing off, for an 8-bit / privacy-blur look. TileSize 1 = passthrough.</p>", Example: "Censor a region until a reveal moment."),
        ["Particles.Emit"] = new(
            Summary: "A 2D particle emitter — position, velocity, lifetime, rate and colour.",
            Description: "<p>Emits sprites in unit space (0–1). <code>Lifetime</code> in seconds, <code>Rate</code> in particles per second. Outputs the rendered field as an <code>Image</code> you can blend over your scene.</p>",
            Example: "Confetti burst on a sub alert, rate scaled to the tier."),
        ["Mask.Rectangle"] = new(Summary: "A procedural rectangle mask, with corner radius and feather.", Description: "<p>A pure generator (no inputs). Coordinates are 0–1 fractions, so it scales across resolutions. Feed it into <code>Image.Mask</code> or key it with <code>Image.Combine</code>.</p>", Example: "A rounded-rect window for a webcam feed."),
        ["Mask.Circle"] = new(Summary: "A procedural circle mask — centre, radius, feather.", Description: "<p>All parameters in 0–1 fractions; animatable via the scalar pipeline.</p>", Example: "Round-crop an avatar."),
        ["Mask.Ellipse"] = new(Summary: "A procedural ellipse mask with per-axis radii and rotation.", Example: "An angled spotlight oval."),
        ["Mask.LinearGradient"] = new(Summary: "A procedural linear alpha gradient.", Description: "<p>Runs from one point to another, fading alpha between two stops — a soft directional mask.</p>", Example: "Fade the bottom of a video into transparency."),
        ["Mask.RadialGradient"] = new(Summary: "A procedural radial alpha gradient — inner/outer radius and alpha.", Example: "A soft circular vignette around a focal point."),
        ["Mask.Vignette"] = new(Summary: "A one-knob vignette preset (a tuned radial gradient).", Description: "<p>Just a <code>Strength</code> — a quick darkened-edges look without wiring a gradient by hand.</p>", Example: "Frame the whole layer with subtle darkened corners."),
        ["Mask.Polygon"] = new(Summary: "A procedural polygon mask from a vertex list.", Description: "<p>Edit the points in the shape editor. Per-vertex animation is supported via keyframes.</p>", Example: "A custom badge shape for an emote."),
        ["Mask.Bezier"] = new(Summary: "A procedural Bézier-curve mask from a control-point list.", Description: "<p>Like <code>Mask.Polygon</code> but with smooth curves. Edit in the shape editor.</p>", Example: "A flowing ribbon mask."),
        ["Mask.Star"] = new(Summary: "A procedural star mask — points, inner/outer radius, rotation.", Example: "A spinning star wipe on a hype alert."),

        // ═══════════════════════════════ TEXT ═════════════════════════════
        ["Text.Render"] = new(
            Summary: "Rasterises a string to an image — font, size, colour, alignment and stroke.",
            Description: "<p>The bridge from text to pixels. <code>FontSize</code> is raw pixels (not a 0–1 scalar). An optional stroke (set <code>StrokeWidth</code> > 0) outlines the glyphs. Output is sized to the widget rect.</p>",
            Example: "Render <code>\"{Message}\"</code> from a trigger in your brand font with a black outline."),
        ["Caption.LiveCaption"] = new(
            Summary: "Live captions from Hub's caption service — raw and translated text.",
            Description: "<p><code>Text</code> is the live caption; <code>Translated</code> applies the layer's target language (equal to <code>Text</code> when no translation is set). Only one Live Caption widget per layer is supported.</p>",
            Example: "Burn-in live captions, translated to your audience's language."),
        ["Text.Translate"] = new(
            Summary: "Translates a string to a target language.",
            Description: "<p>Sends the text to the translation service and resolves with the result, cached by (text, language). Wire its output into <code>Text.Render</code>.</p>",
            Example: "Translate a chat shout-out into the streamer's language on screen."),

        // ══════════════════════════════ MATH ══════════════════════════════
        ["Math.Add"] = new(Summary: "Adds two scalars (A + B).", Example: "Offset an animated value by a base amount."),
        ["Math.Sub"] = new(Summary: "Subtracts two scalars (A − B).", Example: "Count down a remaining fraction."),
        ["Math.Mul"] = new(Summary: "Multiplies two scalars (A × B).", Example: "Scale an oscillator's amplitude."),
        ["Math.Div"] = new(Summary: "Divides two scalars (A ÷ B). Division by zero returns 0.", Example: "Normalise a value into a 0–1 range."),
        ["Math.Mod"] = new(Summary: "Remainder of A ÷ B. By zero returns 0.", Example: "Wrap a rotation back to 0–360."),
        ["Math.Pow"] = new(Summary: "Raises Base to the power Exp.", Example: "Shape an ease with a custom exponent."),
        ["Math.Min"] = new(Summary: "Returns the smaller of A and B.", Example: "Cap a value."),
        ["Math.Max"] = new(Summary: "Returns the larger of A and B.", Example: "Floor a value."),
        ["Math.Clamp"] = new(Summary: "Constrains V to the range [Min, Max].", Example: "Keep an opacity within 0–1 after maths."),
        ["Math.Abs"] = new(Summary: "Absolute value — distance from zero.", Example: "Turn a signed oscillation into a bounce."),
        ["Math.Sqrt"] = new(Summary: "Square root. Negatives return 0.", Example: "Ease motion with a square-root curve."),
        ["Math.Floor"] = new(Summary: "Rounds down to the nearest whole number.", Example: "Snap a value to integer steps."),
        ["Math.Ceil"] = new(Summary: "Rounds up to the nearest whole number.", Example: "Round a progress fraction up."),
        ["Math.Round"] = new(Summary: "Rounds to the nearest whole number.", Example: "Quantise a position to a grid."),
        ["Math.Sign"] = new(Summary: "Returns −1, 0 or +1 for the sign of V.", Example: "Flip a direction based on a value's sign."),
        ["Math.Negate"] = new(Summary: "Negates V (−V).", Example: "Mirror a translation."),
        ["Math.Lerp"] = new(Summary: "Linear interpolation — A at T=0, B at T=1.", Description: "<p>The workhorse of animation: drive <code>T</code> from a <code>Time.*</code> source or a keyframe to blend between two values.</p>", Example: "Lerp opacity 0 → 1 over a fade-in."),
        ["Math.Remap"] = new(Summary: "Rescales V from one range to another.", Description: "<p>Maps <code>[InMin, InMax]</code> onto <code>[OutMin, OutMax]</code>. A zero input span pins to <code>OutMin</code>.</p>", Example: "Turn an oscillator's −1…1 into 0…1 for an opacity."),
        ["Math.Compare"] = new(Summary: "Compares A and B, returning 1.0 (true) or 0.0 (false).", Description: "<p><code>Mode</code> picks the operator (greater / less / equal…); equality uses an epsilon for float safety. Use the result as a 0/1 gate.</p>", Example: "Fade something in only once a value crosses a threshold."),
        ["Math.Sin"] = new(Summary: "Sine of an angle in degrees (output −1…1).", Example: "A gentle bob with <code>sin(time)</code>."),
        ["Math.Cos"] = new(Summary: "Cosine of an angle in degrees (output −1…1).", Example: "Pair with sin for circular motion."),
        ["Math.Tan"] = new(Summary: "Tangent of an angle in degrees.", Example: "A sharper, unbounded oscillation."),

        // ═════════════════════════════ VECTOR ═════════════════════════════
        ["Vector2.Constant"] = new(Summary: "A fixed 2D vector (X, Y).", Example: "A fixed screen position."),
        ["Vector3.Constant"] = new(Summary: "A fixed 3D vector (X, Y, Z).", Example: "A position with depth, or an RGB triple."),
        ["Vector4.Constant"] = new(Summary: "A fixed 4D vector (X, Y, Z, W).", Example: "An RGBA value or a rectangle."),
        ["Vector.Combine"] = new(Summary: "Combines two scalars into a Vector2.", Description: "<p>Unwired inputs default to 0.</p>", Example: "Build a position from separate X and Y animations."),
        ["Vector.Split"] = new(Summary: "Splits a Vector2 into its X and Y scalars.", Example: "Animate only the X of a position."),
        ["Vector3.Combine"] = new(Summary: "Combines three scalars into a Vector3.", Example: "Assemble an RGB colour from channels."),
        ["Vector3.Split"] = new(Summary: "Splits a Vector3 into X, Y and Z scalars.", Example: "Read one component of a 3D value."),
        ["Vector4.Combine"] = new(Summary: "Combines four scalars into a Vector4.", Example: "Build a rect or an RGBA value from parts."),
        ["Vector4.Split"] = new(Summary: "Splits a Vector4 into X, Y, Z and W scalars.", Example: "Pull the alpha out of an RGBA vector."),
        ["Math.LerpVector2"] = new(Summary: "Per-component lerp between two Vector2s.", Example: "Move between two screen positions over T."),
        ["Math.LerpVector3"] = new(Summary: "Per-component lerp between two Vector3s.", Example: "Blend between two colours."),
        ["Math.LerpVector4"] = new(Summary: "Per-component lerp between two Vector4s.", Example: "Animate a rectangle or RGBA over time."),

        // ══════════════════════════════ TIME ══════════════════════════════
        ["Time.Elapsed"] = new(
            Summary: "Seconds since the trigger started — the clock that drives motion.",
            Description: "<p>At design time it reads 0; in OBS it advances each frame. Feed it into <code>Math.*</code> and <code>Time.*</code> to animate anything.</p>",
            Example: "Drive a <code>Math.Sin</code> bob off elapsed time."),
        ["Time.Oscillator"] = new(
            Summary: "A sine oscillator — frequency, amplitude, phase and offset.",
            Description: "<p><code>Offset + Amplitude × sin(2π(Frequency·t + Phase))</code>. Every input is keyframeable.</p>",
            Example: "A breathing glow that pulses once a second."),
        ["Time.Sawtooth"] = new(
            Summary: "A ramp that climbs 0→1 over a period, then resets.",
            Description: "<p>Linear repeating ramp of length <code>Period</code> seconds. Great for looping progress or scroll.</p>",
            Example: "Scroll a tiled texture continuously."),
        ["Time.Easing"] = new(
            Summary: "Applies a named easing curve to a 0–1 input.",
            Description: "<p>Curves: <code>Linear</code>, <code>EaseIn</code>, <code>EaseOut</code>, <code>EaseInOut</code>, <code>Step</code>. The same curve maths the timeline keyframes use, so motion matches.</p>",
            Example: "Ease a slide-in so it decelerates as it arrives."),

        // ═════════════════════════════ STRING ═════════════════════════════
        ["String.Concat"] = new(Summary: "Joins two strings (A + B).", Example: "Build <code>\"@\" + name</code> for a mention."),
        ["String.Upper"] = new(Summary: "Uppercases a string.", Example: "Shout a HYPE caption."),
        ["String.Lower"] = new(Summary: "Lowercases a string.", Example: "Normalise a name before display."),
        ["String.Length"] = new(Summary: "Returns the character count of a string as a scalar.", Example: "Shrink the font when a message is long."),
        ["String.Slice"] = new(Summary: "Extracts a substring by start and count.", Description: "<p>A negative <code>Count</code> means \"to the end\".</p>", Example: "Trim a long message to fit the overlay."),
        ["String.Replace"] = new(Summary: "Replaces all occurrences of Find with With.", Description: "<p>An empty <code>Find</code> returns the input unchanged.</p>", Example: "Swap an emote code for a symbol."),

        // ════════════════════════════ CONVERT ═════════════════════════════
        ["Convert.NumberToString"] = new(Summary: "Formats a scalar as text with a set number of decimals.", Example: "Show an animated counter as whole numbers."),
        ["Convert.StringToNumber"] = new(Summary: "Parses a string to a scalar (empty / invalid → 0).", Example: "Turn a numeric event field into an animatable value."),
        ["Convert.ColorFromRGBA"] = new(Summary: "Builds a Color from R, G, B, A channels (each 0–255).", Example: "Animate a colour by driving its channels."),
        ["Convert.HexToColor"] = new(Summary: "Validates and normalises a hex string into a Color.", Description: "<p>Accepts <code>#rgb</code>, <code>#rrggbb</code> and <code>#rrggbbaa</code>; invalid input falls back to white.</p>", Example: "Take a hex colour from an event field into a text fill."),

        // ══════════════════════════════ SINK ══════════════════════════════
        ["Display"] = new(
            Summary: "The widget's image output — auto-injected, one per trigger, can't be removed.",
            Description: "<p>Every trigger graph has exactly one Display. Whatever <code>Image</code> you wire into it is what renders into the widget rectangle. The compositor evaluates by walking <em>upstream</em> from here, so only nodes that feed Display do any work.</p>",
            Example: "Wire your finished image chain into Display to show it."),
        ["Audio.Play"] = new(
            Summary: "The widget's sound output — optional, one per trigger.",
            Description: "<p>Wire an <code>Audio</code> source (usually <code>Audio.Load</code>) here to play it when the trigger fires. <code>Volume</code> (0–1) and <code>Loop</code> are set inline. No Audio.Play means the widget is silent.</p>",
            Example: "Play an alert sting alongside the visual."),

        // ══════════════════════════════ DEBUG ═════════════════════════════
        ["Viewer"] = new(
            Summary: "Passes any value through unchanged while previewing the image on its body.",
            Description: "<p>An inspection tap — drop it on a wire to see what's flowing through at that point (DaVinci-Fusion style). The thumbnail is design-time only; it has no effect on the rendered output.</p>",
            Example: "Check the mask shape mid-chain before it reaches Display."),
    };
}
