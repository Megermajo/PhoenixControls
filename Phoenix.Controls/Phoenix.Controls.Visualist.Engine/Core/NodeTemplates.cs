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
        // boolean. Kept for serializer round-trip only — WidgetGraphCanvas.HasBodyPreview,
        // which this used to feed, NO LONGER EXISTS; nothing reads this attribute today.
        // GetPreviewSource is the live mechanism. Add() still stamps it when
        // previewSource != None so an older reader round-trips unchanged.
        internal const string PreviewAttrKey = "__Preview";

        /// <summary>
        /// Row capacity of <c>String.Select</c> — the number of <c>Case&lt;i&gt;</c> /
        /// <c>Value&lt;i&gt;</c> attribute pairs the template ships (in addition to the
        /// mandatory <c>Default</c> row).
        ///
        /// TWELVE, and the number is derived rather than picked: the Alerts tool's
        /// <c>KindLabel</c> maps TEN labels today (FOLLOW, SUBSCRIPTION, GIFT SUB,
        /// GIFT SUBS, BITS, RAID, CHARITY, SHOUTOUT, WATCH STREAK, TIP) plus an
        /// "ALERT" fallback, so the eight-way fan-out every other node in the suite
        /// uses (Architect's <c>Logic.Switch</c> Case A..H, <c>Async.Parallel</c>
        /// Branch1..8, this pillar's <c>WebOverlay.Custom</c> slot1..slot8) would not
        /// fit the one graph this node exists to build. Twelve is ten plus two spare
        /// so a new alert family does not force a template change.
        ///
        /// A row whose <c>Case</c> is EMPTY is unconfigured and never matches — see
        /// the template registration for why that rule is load-bearing.
        ///
        /// Single source of truth: <c>NodeEvaluator</c> reads this constant and
        /// compositor.js pins its own <c>STRING_SELECT_ROWS</c> against it in
        /// <c>DynamicMediaSourceV7Tests</c>.
        /// </summary>
        public const int StringSelectRows = 12;

        // ─────────────────────────────────────────────────────────────────────
        //  V10 — the goal.* Overlay Live Channel contract, in code.
        //
        //  ★ THE PRODUCER HAS LANDED. The reader half shipped first, on purpose; Hub's
        //  GoalChannelProducer (Hub/Core/GoalChannelProducer.cs) now publishes into
        //  these keys from Twitch's three channel-goal events (Twitch.GoalBegin /
        //  GoalProgress / GoalEnd) and its three charity-CAMPAIGN events
        //  (Twitch.CharityStarted / CharityProgress / CharityCompleted), hooked into
        //  ScriptManager.ExecuteGenericEventAsync's always-on pre-guard region. So a
        //  Goal.Progress node with Kind = follower / sub / bits / charity now reads a
        //  live goal with zero scripts authored. goal.tip.* is still unproduced —
        //  it belongs to C1's donation ingestion, a SECOND publisher into this SAME
        //  key family, which is what "one goal model" means: one key contract, and
        //  publishers wherever the data lives.
        //
        //  This is the ONE definition of the goal key family, and it is deliberately
        //  here rather than only in prose: every publisher writes into these SAME keys,
        //  because a second data path (a bespoke channel.goal.* root, another
        //  X_UPDATE broadcast) would split one model in two. The producer is written
        //  against these names and GoalChannelProducerTests pins its literals against
        //  these constants, exactly as WidgetFamilyV10Tests pins compositor.js's
        //  mirrored literals — so the halves cannot drift into a permanently blank
        //  widget.
        //
        //  Every goal root carries all four fields:
        //
        //      goal.<kind>.current     number
        //      goal.<kind>.target      number
        //      goal.<kind>.progress    number, 0..1 clamped; 0 when target <= 0
        //      goal.<kind>.label       string, the author-facing display label
        //
        //  <kind> is one of GoalKinds below, or GoalCustomKindPrefix + an author slug
        //  (custom_subathon, custom_boss, …). The kind is NOT an enum on the reader
        //  node for exactly that reason — see the Kind attribute on the reader.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reserved Overlay Live Channel key prefix of the goal family. A goal root is
        /// <c>GoalKeyPrefix + kind + '.'</c>; the reader node subscribes that root plus
        /// <c>*</c> as ONE prefix rather than four exact keys, the same way the timer
        /// trio subscribes its 13-field family.
        /// </summary>
        public const string GoalKeyPrefix = "goal.";

        /// <summary>
        /// The four field suffixes every goal root publishes. Mirrored by
        /// <c>GOAL_FIELDS</c> in compositor.js; <c>WidgetFamilyV10Tests</c> pins the pair.
        /// </summary>
        public static readonly IReadOnlyList<string> GoalFields =
            new[] { "current", "target", "progress", "label" };

        /// <summary>
        /// The named goal kinds. NOT exhaustive by design — an author-defined kind is
        /// <see cref="GoalCustomKindPrefix"/> plus a slug, which is why the reader's
        /// Kind attribute ships no <c>__KnownValues</c> companion (that would render a
        /// dropdown and lock every custom goal out of the palette).
        /// <para>
        /// ★ THE READER'S CASE RULE IS SCOPED TO THESE FIVE. The reader trims the author's
        /// Kind text always, but case-folds it ONLY when the folded form is one of these
        /// five reserved kinds; anything else reaches the key verbatim. So every publisher,
        /// of either kind below, publishes THESE FIVE lower-cased. That half has no cases —
        /// it is the same for a script and for a platform producer.
        /// </para>
        /// <para>
        /// ★ PUBLISHERS — WHAT TO DO WITH ANY OTHER KIND DEPENDS ON WHO AUTHORED THE SLUG,
        /// and there are exactly TWO cases, spelled out on
        /// <see cref="GoalCustomKindPrefix"/>. The one-line version: a slug the AUTHOR typed
        /// is published EXACTLY as the author typed it (do not case-fold, do not sanitise),
        /// while a slug DERIVED FROM MACHINE DATA — Twitch's <c>GoalType</c> — is lower-cased
        /// and slugged by its producer, because there is no author text to preserve.
        /// <c>OverlayLiveStore</c> matches Ordinal and its <c>Norm()</c> only trims — it never
        /// folds — so a kind folded on one side and not the other is dropped by the
        /// publisher-side subscription gate: a blank bar, a running producer, and no error on
        /// either side. Which is why the rule is written out per case rather than as a slogan:
        /// a one-line version of either half, read by the wrong publisher, produces exactly
        /// that failure.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyList<string> GoalKinds =
            new[] { "follower", "sub", "bits", "tip", "charity" };

        /// <summary>
        /// Prefix a non-reserved goal kind carries, e.g. <c>custom_subathon</c>.
        /// <para>
        /// ★ PUBLISHERS — THE SLUG'S CASE RULE HAS TWO CASES, and which one applies is decided
        /// by WHO AUTHORED THE SLUG. Stating only one half is precisely how an earlier draft of
        /// this doc came to contradict the key contract (see §4h "RESOLVED CONTRADICTION"): the
        /// blanket "never fold" below was written for a script publisher and is unimplementable
        /// for the one producer that will actually read it. Both halves, therefore, and each
        /// labelled.
        /// </para>
        /// <para>
        /// CASE 1 — THE SLUG IS AUTHOR TEXT (a script publisher: <c>overlay.publish</c> from a
        /// <c>.phx</c>). The author types the Kind into the widget AND writes the key in their
        /// own publish call, so they own both ends of the match and there is real author text to
        /// preserve. DO NOT case-fold and do not sanitise it: publish EXACTLY as the author typed
        /// it, so <c>custom_BossHP</c> must be published as <c>goal.custom_BossHP.*</c>.
        /// Publishing <c>goal.custom_bosshp.*</c> instead means the widget's subscription never
        /// matches, every write is dropped at the gate, and the streamer sees a blank bar with a
        /// running producer and no error anywhere. This is the case the reader's scoped fold
        /// exists for — <c>liveGoalRoot</c> in compositor.js folds the five reserved kinds and
        /// leaves everything else alone precisely so this case works.
        /// </para>
        /// <para>
        /// CASE 2 — THE SLUG IS MACHINE-DERIVED (the Twitch goal producer, contract §4h). There
        /// is no author text at all: <c>&lt;kind&gt;</c> is folded out of EventSub's
        /// <c>GoalType</c> on a <c>channel.goal.*</c> event, and an author cannot be expected to
        /// guess Twitch's exact casing. So THAT producer — and only that one — lower-cases the
        /// type and slugs it to <c>[a-z0-9_.-]</c>, because a predictable key is the only kind an
        /// author can bind at all. This is the documented exception to Case 1, not a
        /// contradiction of it: Case 1 protects text the author wrote, and here there is none.
        /// </para>
        /// <para>
        /// Because the reader does not fold non-reserved kinds, Case 2 costs its producer a
        /// DOCUMENTATION duty rather than any code: the Kind attribute's author-facing bubble owes
        /// a sentence saying that an unrecognised Twitch goal type appears as
        /// <c>custom_&lt;lowercased_type&gt;</c> and must be typed in lower case. Without it the
        /// author types <c>custom_SomeNewType</c>, the producer publishes
        /// <c>custom_somenewtype</c>, <c>OverlayLiveStore</c> matches Ordinal, and every write is
        /// dropped silently — Case 1's failure re-entering from the producer end.
        /// ★ THAT SENTENCE IS NOW IN THE BUBBLE, in all four lang bundles: it landed with the
        /// producer commit, which is when it stopped being a claim about something that did not
        /// exist. <c>GoalChannelProducer.KindForGoalType</c> is the one place in the tree that
        /// folds a machine slug, and its own comment names Case 2 explicitly so the two cannot
        /// drift apart.
        /// </para>
        /// </summary>
        public const string GoalCustomKindPrefix = "custom_";

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
            // attribute (a serializer-round-trip remnant — its old consumer
            // WidgetGraphCanvas.HasBodyPreview no longer exists) and
            // records the enum in NodeTemplates._previewByTitle so the canvas
            // and NodeEvaluator can resolve the preview kind without sniffing
            // attribute strings. PreviewSource is the source of truth; the
            // attribute is its serialized companion. Same convention as
            // "__KnownValues" / "__Range": underscore-prefixed metadata.
            // ── V7 — the DYNAMIC MEDIA SOURCE ─────────────────────────────────
            //
            // Path is now BOTH an inline attribute AND a wirable String input, on all
            // three local-file loaders (Image.Load / Video.Load / Audio.Load). Before
            // V7 every loader bound one author-typed path, so a clip could not be
            // CHOSEN at trigger time — which is why an Alert Box could not express a
            // per-kind sound (six-plus alert kinds, and Audio.Play is a per-graph
            // singleton whose sink pass is not Result.If-gated) and why a soundboard
            // could not pick a clip at all.
            //
            // Resolution order is the established "wired socket wins, else the
            // same-named attribute" idiom — ResolveMediaPathInput in NodeEvaluator.cs,
            // _evalMediaPathSocket in compositor.js. Two consequences that are
            // load-bearing, not incidental:
            //
            //   • The socket is APPENDED and the attribute is untouched, so an existing
            //     saved .phxlayer resolves byte-identically: an UNWIRED Path reads the
            //     attribute exactly as it did before V7. And a legacy node DOES grow the
            //     new pin — LayerGraphMigrator.BackfillFromTemplate appends it on load via
            //     its InputBackfillTitles allowlist, which covers these three loaders plus
            //     V13's Visual.Complete (four titles in total — the allowlist is the
            //     source of truth; check it rather than trusting a count in a comment).
            //     (That class back-fills output sockets AND attribute keys for
            //     every catalogued title; the reason INPUTS are otherwise excluded is
            //     WebOverlay.Custom's eight renameable String slots, not any property of
            //     input sockets — these three carry one fixed-name pin and no rename
            //     affordance, hence the allowlist.) Without the back-fill the entire
            //     capability would be unreachable on every layer a streamer already
            //     authored, which is every layer that matters.
            //   • A wired path is attacker-INFLUENCED in a way an author-typed one never
            //     was, because Visual.Arg can carry a chat argument straight into it. So
            //     the two resolvers above enforce a PROVENANCE rule: an ATTRIBUTE path may
            //     still be absolute / http(s): / data: (the author is the streamer), while
            //     a WIRED path must be RELATIVE and is refused — with a trigger diagnostic
            //     naming the value — otherwise. Relative paths ride Hub's /media/ route
            //     and inherit its traversal guard.
            Add("Image.Load", "Inputs",
                inputs:  new[] { S("Path", SocketDataType.String) },
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
            //
            // V7 — wirable Path (see the Image.Load block above for the full
            // rationale). A dynamic source keeps the contain-fit: evalVideoLoad ends
            // at the same fitLoadedImageToFrame return regardless of where the path
            // came from, and ensureVideoElement already handled a changing src
            // cleanly (it resets the one-shot alpha probe). Without that fit a
            // dynamic 1920x1080 clip would be reported at native size and Display
            // would centre-crop it instead of fitting the widget.
            Add("Video.Load", "Inputs",
                inputs:  new[] { S("Path", SocketDataType.String) },
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
            //
            // V7 — wirable Path (see the Image.Load block above). This is THE node the
            // per-kind alert sound depends on: one Audio.Load + one Audio.Play, with
            // the CLIP chosen by value (String.Select) rather than by branch. Branch-
            // gated audio is not expressible and must not be attempted — the audio
            // sink pass evaluates EVERY Audio.Play in the graph unconditionally,
            // outside every Result.If arm, and Result.If is an Image-typed barrier
            // while Audio.Play's only input is Audio-typed, so the two type systems
            // cannot meet. Both limits are documented at their sites in compositor.js.
            //
            // The playing clip is LATCHED at the activation that started it — a path
            // change mid-activation does not restart or re-source it. See
            // ensureAudioElementAndPlay for why (a re-source per render tick is the
            // 2026-06-23 "Loop = false audio loops" bug at frame rate).
            Add("Audio.Load", "Inputs",
                inputs:  new[] { S("Path", SocketDataType.String) },
                outputs: O("Audio", SocketDataType.Audio),
                attrs:   new Dictionary<string, string> { ["Path"] = "\"\"" });

            // V11 — opts into the body swatch. PreviewSource.OwnColor was declared FOR this node
            // (see its enum doc) and ThumbnailHost has always carried the PreviewKind.Color arm that
            // paints PreviewSwatch — but nothing ever opted in, so the entire path was unreachable:
            // GetPreviewSource could never return OwnColor, EvaluatePreviews skipped the node, and no
            // Color.Constant has ever shown a swatch. A prior sweep documented that gap honestly
            // rather than closing it, and explicitly invited this ("if a future template opts into
            // OwnColor, add positive Color-swatch coverage then").
            //
            // Closing it is also what makes V11 non-vacuous: the preview path resolves a SOURCE
            // (Path / Url / Value-Color), and a colour is the only one of those anyone keyframes, so
            // this is the one node whose thumbnail can follow the playhead at all.
            Add("Color.Constant", "Inputs",
                inputs:  Empty(),
                outputs: O("Color", SocketDataType.Color),
                attrs:   new Dictionary<string, string> { ["Value"] = "\"#ffffff\"" },
                previewSource: PreviewSource.OwnColor);

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

            // ── Var.Live — the Overlay Live Channel binding node ───────────────
            //
            // Reads ONE literal key out of the Overlay Live Channel — the coalesced
            // 1 Hz key/value stream Hub pushes down the layer's own socket — and
            // exposes it three ways so the AUTHOR picks the type at the pin instead
            // of the publisher guessing it (overlay.publish always stores a JSON
            // string; sniffing "looks numeric" would turn "007" into 7):
            //   Text   : String  the value as text (a JSON string yields its content,
            //                    a JSON number / bool its literal text).
            //   Number : Scalar  invariant-culture parse, 0 on failure and never NaN,
            //                    so one malformed publish cannot poison a Math chain.
            //                    Tool keys keep their real JSON types, so this pin is
            //                    exact for them and best-effort for author strings.
            //   State  : String  Active / Stale / Missing, from the channel's own
            //                    provenance. This is what makes a missing key HONEST:
            //                    a key nobody published reads Missing with an empty
            //                    Text, so the graph renders nothing instead of the
            //                    design-time mock that used to paint onto a live
            //                    stream after every scene return.
            //
            // Category: "Inputs" — the band for nodes that inject a value the graph
            // did not compute (Image.Load pulls bytes off disk; the Constant nodes
            // carry a literal). Var.Live is that same role with an external, changing
            // source. Deliberately NOT "Triggers": Message.Read sits there because it
            // reads the trigger context, which exists only for the span of one
            // trigger, whereas a channel key is ambient and readable on every frame.
            // "Text" / "String" would misfile it too — it also produces a Scalar.
            //
            // Attributes:
            //   Key         : string  the literal channel key ("timer.main.progress",
            //                         "counter.deaths.count", or any key a script
            //                         published with overlay.publish). LITERAL is a
            //                         documented limit, not an oversight: the browser
            //                         derives its subscription from this attribute's
            //                         text when it scans the graph, so a computed key
            //                         ("score_{user.name}") is publishable but NOT
            //                         bindable — no frame would ever arrive for it.
            //   PreviewText : string  design-time placeholder ONLY. The browser never
            //                         reads it; an unbound node renders empty on
            //                         stream, by design. Left empty by default so the
            //                         canvas echoes {Key} instead (see NodeEvaluator).
            Add("Var.Live", "Inputs",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Text",   SocketDataType.String),
                    S("Number", SocketDataType.Scalar),
                    S("State",  SocketDataType.String),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["Key"]         = "\"\"",
                    ["PreviewText"] = "\"\"",
                });

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

            // ── V10 — Image.Solid: the colour plate, and the palette's only
            //    live-drivable fill geometry ──────────────────────────────────
            //
            // Emits a Colour-filled rectangle as an Image, sized to the WIDGET frame,
            // with the rectangle expressed as 0..1 fractions of that frame.
            //
            // WHY IT EXISTS. Two holes in the catalog met here, and every bar-shaped
            // member of the channel-fed widget family fell into both — a goal bar, a
            // tip-jar fill, a Stream-Boss HP bar, one row of a poll / bet result, a
            // credits or sponsor backdrop:
            //
            //   1. NOTHING PRODUCED A COLOUR. All nine Mask.* generators above emit
            //      white-on-transparent only (one hard-coded white fillStyle), and
            //      Image.ColorAdjust is a GRADE, not a paint — hue-rotating white
            //      yields white, so no chain of existing kernels can tint a mask. The
            //      only colour-fill surface in the whole catalog was Text.Render's
            //      Background, and that emits nothing at all unless the Text input has
            //      at least one character in it. NodeEvaluator's Colour-wired-into-an-
            //      Image-consumer error has been telling authors to route it "through a
            //      Color→Image converter" since before one existed; this IS that node.
            //   2. NOTHING LET A LIVE VALUE DRIVE A SHAPE. Every Mask.* generator
            //      declares zero input sockets, so its geometry can only be typed in or
            //      keyframed and a channel value cannot reach it. Image.Crop's Rect IS
            //      a wirable Vector4, but a crop returns a canvas of the CROPPED size
            //      and Display centre-draws it, so a cropped plate grows out of the
            //      widget centre in both directions — a symmetric wipe, not a bar.
            //
            // WHY A NEW NODE INSTEAD OF WIRABLE PINS ON Mask.Rectangle. Promoting that
            // node's X/Y/Width/Height to sockets looks like the smaller change and is
            // the wrong one: LayerGraphMigrator back-fills INPUT sockets only for the
            // titles on its allowlist (the three media loaders plus V13's Visual.Complete
            // — the allowlist itself is the source of truth), so the new pins would
            // appear on freshly dropped nodes and NOT on the ones already saved in every
            // .phxlayer a streamer has authored — a capability that exists or not
            // depending on when the node happened to be dropped, with no error on either
            // side. Mask.Rectangle is therefore untouched and keeps its white-mask role;
            // this is its coloured, wirable sibling.
            //
            // GEOMETRY SPACE — a deliberate divergence, called out because it will
            // otherwise be read as a bug. Mask.* fractions are of the LAYER; these are
            // of the WIDGET FRAME. The frame is the space Display draws 1:1 and the
            // space Text.Render already rasterises into, so Width 0.6 means "60% of MY
            // widget", which is the only reading under which a bar and its own label
            // compose. Fractions of the layer would make the same bar change length when
            // the author moved the widget.
            //
            // Each geometry pin is wired-socket-wins with the same-named attribute as the
            // fallback, and the attribute is read through the keyframeable path — which
            // is also how an author previews a bar while building, since a channel-fed
            // Scalar honestly reads 0 on a canvas with no channel behind it.
            //
            // CornerRadius stays attribute-only (fraction of the shorter frame axis,
            // like Mask.Rectangle's): a pill bar sets it once and never drives it from
            // data, so a socket would only widen the node for nothing.
            Add("Image.Solid", "Image",
                inputs:  new[]
                {
                    S("Color",  SocketDataType.Color),
                    S("X",      SocketDataType.Scalar),
                    S("Y",      SocketDataType.Scalar),
                    S("Width",  SocketDataType.Scalar),
                    S("Height", SocketDataType.Scalar),
                },
                outputs: O("Out", SocketDataType.Image),
                attrs:   new Dictionary<string, string>
                {
                    ["Color"]        = "\"#ffffff\"",
                    ["X"]            = "0", ["X__Range"]            = Range01,
                    ["Y"]            = "0", ["Y__Range"]            = Range01,
                    ["Width"]        = "1", ["Width__Range"]        = Range01,
                    ["Height"]       = "1", ["Height__Range"]       = Range01,
                    ["CornerRadius"] = "0", ["CornerRadius__Range"] = "0..0.5",
                },
                previewSource: PreviewSource.OwnColor);

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
            //
            // ── V10 — MULTI-LINE, and why it is a repair rather than a feature ──────
            //
            // This node used to call fillText ONCE, at the vertical centre of the frame.
            // Canvas fillText does not break lines, so any text containing a newline was
            // already rendered wrongly — the rows collapsed into one unreadable line and
            // everything after the first \n was effectively lost. That was not a
            // theoretical case: Loyalty.Leaderboard has always emitted its ranks
            // newline-joined, and every list-shaped member of the channel-fed widget
            // family (an event list, a top-donator board, a viewer queue, end credits)
            // emits rows the same way. So a multi-row readout could be produced but not
            // displayed, by any composition of existing nodes.
            //
            // The renderer therefore splits on newlines and draws each row. A ONE-line
            // string takes the identical old code path at the identical baseline, so every
            // saved single-line graph in existence renders byte-for-byte as before; only
            // text that was already broken changes, and it changes to correct.
            //
            // Two attributes come with it, both additive with safe defaults (attributes —
            // unlike input sockets — are back-filled onto saved nodes by
            // LayerGraphMigrator, and both readers pass the same default anyway, so an
            // un-migrated node behaves identically):
            //   LineHeight : Scalar  row pitch as a multiple of FontSize (default 1.25).
            //   Wrap       : bool    default FALSE, so nothing existing re-flows. When
            //                        true, a row wider than the frame is greedily broken
            //                        at spaces. Off by default because wrapping silently
            //                        changes the height of authored text, and because a
            //                        pre-broken list (List.Live, Loyalty.Leaderboard)
            //                        wants its own rows honoured, not re-flowed.
            // No new SOCKETS: neither is a value anyone drives from data — the whole
            // point of a row pitch is that it is constant for the widget.
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
                    // Optional background fill behind the text — fills the whole
                    // widget frame before the glyphs are drawn. Default fully
                    // transparent (#00000000 = no background, preserving the prior
                    // transparent-canvas behaviour); any non-zero alpha paints a
                    // solid box (a clock/countdown "plate"). compositor.js fillRect()s
                    // the frame when the alpha > 0. Color type ⇒ Inspector picker.
                    S("Background", SocketDataType.Color),
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
                    // Default transparent = no background box (no visual change to
                    // existing Text.Render nodes). 8-digit #rrggbbaa carries alpha.
                    ["Background"]           = "\"#00000000\"",
                    // V10 multi-line. 1.25 is the conventional body-text pitch and it
                    // is what a single row costs nothing to declare: with one row the
                    // renderer never consults it.
                    ["LineHeight"]           = "1.25",
                    ["LineHeight__Range"]    = "0.5..3",
                    // V10 word wrap, OFF by default so no authored text re-flows.
                    //
                    // A BARE true/false with NO __KnownValues companion, and the absence is
                    // load-bearing rather than an omission. A companion made this attribute
                    // UNREACHABLE from the Inspector: the control-kind precedence puts Enum
                    // above everything else (VisualistViewModel.ResolveParamKind), an Enum row
                    // commits through CommitText, which JSON-quotes the value, and
                    // compositor.js's _readBool lower-cases and trims but never strips quotes
                    // — so the stored value was the five-character "\"true\"", which matches
                    // none of true/1/yes. The dropdown read "true", the overlay wrapped
                    // nothing, forever, silently.
                    //
                    // Bare "false" infers NodeParamKind.Bool, whose CommitBool writes an
                    // unquoted true/false. That is the shape every other bool in this catalog
                    // already uses (Mask.* Inverted, Audio.Load Loop) and the only one
                    // _readBool can read, so the general rule is: a bool-valued attribute
                    // never carries a __KnownValues companion.
                    ["Wrap"]                 = "false",
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

            // WebOverlay.Custom — DOM-overlay sink (Path B). Full contract in
            // WebOverlaySinkNode. The 8 String inputs carry the values an Architect
            // script pushes over the EXISTING Visual.Trigger eventData channel; the
            // compositor injects each as a named CSS custom property (--<socketName>),
            // so the socket NAMES double as the CSS variable names and are renameable in
            // the editor pop-up (Visualist doesn't re-sync socket names from templates,
            // so renames persist — see LayerGraphMigrator). Html/Css hold the author's
            // markup + styles as RAW strings (NOT JSON-quoted — attr() reads them
            // verbatim, unlike String.Constant's quoted "Value"). No output socket: it is
            // a side-effecting terminal visited by title in compositor.js (evalWebOverlay),
            // exactly like Audio.Play. Instantiation goes through the generic template
            // path, which stamps DataType=String on every socket (correct pin shape +
            // wire-compat — the DataType-at-creation contract).
            Add(WebOverlaySinkNode.Title, WebOverlaySinkNode.Category,
                inputs:  new[]
                {
                    S("slot1", SocketDataType.String),
                    S("slot2", SocketDataType.String),
                    S("slot3", SocketDataType.String),
                    S("slot4", SocketDataType.String),
                    S("slot5", SocketDataType.String),
                    S("slot6", SocketDataType.String),
                    S("slot7", SocketDataType.String),
                    S("slot8", SocketDataType.String),
                },
                outputs: Empty(),
                attrs:   new Dictionary<string, string>
                {
                    [WebOverlaySinkNode.HtmlAttr] = WebOverlaySinkNode.DefaultHtml,
                    [WebOverlaySinkNode.CssAttr]  = WebOverlaySinkNode.DefaultCss,
                });

            // V15 — Player.Embed: the THIRD-PARTY iframe sink, second consumer of the
            // #dom-overlay track. Full contract in PlayerEmbedSinkNode. Terminal like
            // WebOverlay.Custom (outputs: Empty()), discovered and visited by title in
            // compositor.js (evalPlayerEmbed), mirrored design-time by a HasError stub in
            // NodeEvaluator because there is no browser at design time.
            //
            // ★ It owns the WHOLE widget rect and cannot be composited. A cross-origin
            // iframe is drawn by the browser, not by us: it cannot be read back into the
            // Image pipeline, faded, masked or z-ordered against canvas widgets, because
            // #dom-overlay is ONE layer at ONE z-index above every canvas widget. That is
            // the same wall WebSource hit (see its comment further down, which records why
            // iframe/HTML rasterisation was deliberately not built) — this node accepts the
            // wall instead of fighting it, which is exactly why it is a full-rect PRESET.
            //
            // Source picks the feed and is the only structural choice:
            //   songrequest — ordinary SUBSCRIBER to the songrequest.* live keys Hub's
            //                 SongRequestService publishes. Clip is ignored.
            //   clip        — one-shot, driven by the Clip input (wire a Visual.Arg into it
            //                 so a shoutout script pushes the slug over Visual.Trigger).
            // Quoted default + a raw-CSV __KnownValues companion: that pair is what makes
            // the Inspector row a dropdown, and the Enum control commits JSON-quoted, so the
            // default has to ship quoted too or the first edit would change its shape.
            //
            // Clip is the ONE input and it is String, not Any: what travels is a slug or a
            // URL, and the browser resolves it textually.
            //
            // It carries a JSON-quoted-empty DEFAULT ATTRIBUTE beside its socket, and that
            // pairing is load-bearing rather than decorative — Image.Load's
            // ["Path"] = "\"\"" alongside its String Path socket is the same shape for the
            // same reason. The attribute is what makes the pin REACHABLE WITHOUT A WIRE:
            // WidgetGraphNodeView.TryGetPillForSocket renders the editable value pill only
            // for a socket that has a matching attribute key, and the Inspector builds its
            // String row off the attribute bag too. Without it the socket is a bare pin, a
            // clip could only ever arrive over a wire, and compositor.js's own unwired
            // fall-through (_evalPlayerClip → ev._evalQuotedStringSocket →
            // stripQuotes(attr(node,'Clip',''))) would be a branch nothing could take.
            // Quoted rather than raw because the Inspector commits String rows through
            // NodeParamVm.CommitText, which JSON-quotes; the reader stripQuotes()es, so both
            // spellings round-trip, but the default must ship in the shape the first edit
            // writes or that edit silently changes its type.
            Add(PlayerEmbedSinkNode.Title, PlayerEmbedSinkNode.Category,
                inputs:  new[] { S(PlayerEmbedSinkNode.ClipSocket, SocketDataType.String) },
                outputs: Empty(),
                attrs:   new Dictionary<string, string>
                {
                    [PlayerEmbedSinkNode.SourceAttr] = "\"" + PlayerEmbedSinkNode.SourceSongRequest + "\"",
                    [PlayerEmbedSinkNode.SourceAttr + "__KnownValues"] = PlayerEmbedSinkNode.SourceKnownValues,
                    [PlayerEmbedSinkNode.ClipSocket] = "\"\"",
                });

            // Visual.Complete — graph-authored completion sink. When the trigger graph
            // reaches this node during evaluation, compositor.js fires VISUAL_COMPLETE back
            // to Hub so wait_for_visual scripts proceed on the Done branch. If a graph has
            // no Visual.Complete, the engine falls back to firing on first Display render.
            // The "In" input is for symmetry with Display; its value is not rendered. (This
            // line used to read "the Image input" — there are TWO inputs since V13 and only
            // "In" is the un-rendered symmetry pin, so naming it is no longer optional. The
            // author-facing bubble and the node-reference prose make the same split.)
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
            //
            // ── V13 / H1 — the Payload input ────────────────────────────────
            //
            // APPENDED after "In", never inserted before it and never renamed — but for
            // two DIFFERENT reasons, and neither of them is "a re-sort re-seats existing
            // wires". That was the reason recorded here and it is false: links resolve by
            // Socket.Id, and the one pass that does re-sort — Architect's
            // GraphSerializer.ReorderSocketsToTemplate — reorders the same Socket
            // INSTANCES ("Preserves the Socket instances (so links, which resolve by Id,
            // are untouched)", its own doc). It also never sees a widget graph: it runs on
            // .phxg load, and a .phxlayer's trigger graphs never go through it.
            //
            //   • RENAME is the wire hazard: it prunes the socket AND every wire attached
            //     to it in every .phxlayer already on the streamer's disk
            //     (project_socket_rename_prunes_links).
            //   • INSERT is an ORDER-DIVERGENCE hazard, and on this half it is permanent.
            //     Nothing re-sorts a widget graph at all, and the only pass that reaches
            //     already-authored layers — LayerGraphMigrator.BackfillFromTemplate — can
            //     only node.Sockets.Add, i.e. append. So declaring Payload anywhere but
            //     last would give ONE title two saved socket orders that nothing can ever
            //     reconcile: [In, Payload] on every legacy node (back-filled by append)
            //     and [Payload, In] on every node dropped after the upgrade. The editor
            //     renders node.Sockets AS SERIALISED and Hub/compositor read the
            //     .phxlayer raw, so that split is visible in the product and unfixable in
            //     the data. Append is what keeps the template's declared order and the
            //     back-fill's only possible order the same thing.
            //
            // Append only.
            //
            // Wire contract: when this pin is wired, compositor.js carries the
            // resolved string up as VISUAL_COMPLETE's `payload` field, which threads
            // NotifyTriggerComplete → NotifyComplete → ResolveVisualWait → the
            // script's `global._wait_payload` (the SAME var wait_for_event already
            // writes — there is deliberately no second var name). UNWIRED the field
            // is OMITTED from the frame, so the wire bytes and every exporter golden
            // stay byte-identical: that is the compatibility gate for this sprint.
            //
            // ★ FAN-OUT IS NONDETERMINISTIC, and it is accepted rather than fixed.
            // Every widget that owns the fired trigger on a layer shares ONE waitId,
            // so N OBS Browser Sources pointed at the same layer produce N acks for
            // it. FIRST ACK WINS; every later ack for an already-resolved waitId is
            // dropped and logged once per waitId (not once per ack — a fan-out of 8
            // must not write 7 log lines). Consequence for the author: with two OBS
            // sources on one layer, WHICH widget's Payload reaches the script is not
            // predictable. Use one source per layer when the payload must be
            // deterministic. (Already anticipated in WidgetTriggerQueue.NotifyComplete.)
            //
            // String, not Any: the receiving var is a script string var, and the
            // engine's substitution is textual — a typed pin here would promise a
            // fidelity the {global._wait_payload} round-trip cannot keep.
            Add("Visual.Complete", "Triggers",
                inputs:  new[]
                {
                    S("In",      SocketDataType.Any),
                    S("Payload", SocketDataType.String),
                },
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

            // Timer.Remaining — live subathon-countdown readout (spec §6). Text is the
            // formatted remaining time; State is the timer's RUN state
            // (Running/Paused/Stopped/Ended).
            //
            // WHERE THE VALUES COME FROM (this used to say TIMER_UPDATE, which no
            // longer exists): Hub's TimerService publishes the whole
            // timer.<slug|display-name|__default>.* key family into the Overlay Live
            // Channel (OverlayLiveStore). The store learns from each browser's
            // LIVE_HELLO which keys that layer actually reads and ships one coalesced
            // LIVE_PATCH per second carrying only those; the browser-side
            // evalTimerRemaining in compositor.js then reads ONE key per output pin.
            // The bespoke TIMER_UPDATE broadcast frame was RETIRED with that rework —
            // nothing produces it, so don't document it as this node's source.
            //
            // TWO STATUS PINS, answering two different questions:
            //   State : the timer's RUN state, i.e. the VALUE of timer.<root>.state —
            //           Running / Paused / Stopped / Ended. Unchanged meaning: this is
            //           what the pin has always carried and what every already-saved
            //           widget branches on.
            //   Live  : the CHANNEL's verdict on that same key — Active / Stale /
            //           Missing. Appended because a run state cannot say "nobody
            //           publishes this timer any more": a producer that dies mid-stream
            //           leaves State frozen on "Running" forever, and a widget has to be
            //           able to see that the value it is painting is a frozen lie.
            // The asymmetry across the tool readers is deliberate, and this is the
            // comment a reader hits first, so: "State" means "the most useful status
            // this node has". On the timer trio that is the run state, with liveness
            // split out onto Live. On Counter.Value / Loyalty.Leaderboard /
            // Loyalty.Balance / Var.Live there is no run state to report, so State IS
            // the liveness verdict and those nodes carry no Live pin.
            // (Caption.LiveCaption is NOT in that list — it has no State socket at all,
            // only Text and Translated. It reads a channel key like the others, but it
            // exposes no liveness pin of any kind.)
            //
            // Which liveness words are actually REACHABLE differs by key family, and
            // this is the part that is easy to get wrong in a widget graph:
            //   * timer.* keys        — Active / Stale / Missing. The Timer tool is the
            //                           ONLY publisher that declares an ExpectedInterval
            //                           (TimerService.LiveInterval, 1 s), and
            //                           OverlayLiveStore.ComputeState can only return
            //                           Stale for a key that declared one.
            //   * counter.* /         — Active / Missing only. These families are
            //     loyalty.* /           event-driven: they publish on change and promise
            //     caption.* /           no cadence, so ComputeState has nothing to time
            //     author keys from      out against and the value never decays. A widget
            //     overlay.publish       branching Equals="Stale" on one of them ships a
            //                           branch that can NEVER fire.
            // Loyalty.Leaderboard is the one exception to the vocabulary itself: it
            // reports Empty (not Missing) for a board with no rows, for back-compat with
            // widgets that branch on Empty to hide their frame. See
            // compositor.js evalLoyaltyLeaderboard for the full rule.
            // A Var.Live inherits whichever set its BOUND key belongs to — Stale is
            // reachable there if and only if the Key is a timer.* key.
            //
            // Attributes:
            //   TimerName   : string  empty (default) = the default timer; else
            //                         selects by slug then display name (resolved
            //                         browser-side in compositor.js evalTimerRemaining).
            //   Format      : string  short|long|clock — which formatted field the
            //                         Text output returns (the other pins ignore it).
            //   PreviewText : string  design-time placeholder shown until a live
            //                         timer frame arrives.
            //
            // ── The five pins appended for the Overlay Live Channel ────────────
            // Before the channel, every one of these numbers was only reachable by
            // re-parsing the formatted Text string in the graph ("01:23:45" back
            // into seconds), and progress was not reachable at all — which is why
            // no widget could drive a real progress bar. The channel publishes them
            // as JSON numbers under timer.<slug|__default>.*, so the pins carry the
            // exact value with no round trip:
            //   Progress : Scalar  0..1, clamped Hub-side. Wire it straight into an
            //                      Image.Scale Factor / Image.Crop rect to get a bar.
            //   Seconds  : Scalar  the mode-aware DISPLAY value in whole seconds
            //                      (channel field display_seconds) — elapsed for a
            //                      Stopwatch, remaining otherwise — so it always
            //                      agrees with whatever the Text pin is showing.
            //   Paused   : String  "true" / "false". String rather than Bool because
            //                      widget graphs branch through Result.If's
            //                      When/Equals attribute pair, which compares text.
            //   Mode     : String  Subathon / Countdown / Stopwatch, so one widget can
            //                      relabel itself for whichever timer it was pointed at.
            //   Live     : String  Active / Stale / Missing — the liveness of
            //                      timer.<root>.state, the one key that exists if and
            //                      only if a timer resolved under this node's root.
            //
            // APPEND-ONLY, and it has to stay that way: the pins are added after the
            // pre-existing Text/State pair and nothing above them is renamed or
            // reordered. A rename prunes the socket and every wire attached to it in
            // every already-saved .phxlayer, silently. (Live is last because it was
            // appended one pass after the other four — position carries no meaning,
            // the browser and the C# mirror both dispatch on the socket NAME.)
            Add("Timer.Remaining", "Time",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Text",     SocketDataType.String),
                    S("State",    SocketDataType.String),
                    S("Progress", SocketDataType.Scalar),
                    S("Seconds",  SocketDataType.Scalar),
                    S("Paused",   SocketDataType.String),
                    S("Mode",     SocketDataType.String),
                    S("Live",     SocketDataType.String),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["TimerName"]   = "\"\"",
                    ["Format"]      = "\"short\"",
                    ["PreviewText"] = "\"01:23:45\"",
                });

            // Clock.Now — a live digital wall-clock readout. Unlike Timer.Remaining
            // it is fully BROWSER-AUTONOMOUS: it needs no Hub timer at all, reading
            // the OBS machine's own clock each tick. Nothing pushes it a per-second
            // frame — a widget re-renders when a key it SUBSCRIBED changes, and this
            // node subscribes nothing — so compositor.js runs a dedicated 1 Hz clock
            // heartbeat that re-renders any widget carrying a Clock.Now node. (The
            // pre-channel wording blamed TIMER_UPDATE for only re-rendering
            // Timer-consuming widgets; that frame no longer exists, but the
            // conclusion is unchanged and now follows from the subscription model.)
            // The C# design-time mirror returns PreviewText so the canvas thumbnail
            // isn't blank.
            //
            // Attributes:
            //   UtcOffset : number  hours offset from UTC (default 0 = UTC). Whole
            //                       or fractional (e.g. 5.5). Range −12..14.
            //   Format    : enum    HH:mm:ss | HH:mm | hh:mm:ss A | hh:mm A —
            //                       24-hour or 12-hour (A = AM/PM), with/without
            //                       seconds. Rendered browser-side in evalClockNow.
            //   PreviewText : string design-time placeholder.
            Add("Clock.Now", "Time",
                inputs:  Empty(),
                outputs: O("Text", SocketDataType.String),
                attrs:   new Dictionary<string, string>
                {
                    ["UtcOffset"]         = "0",
                    ["UtcOffset__Range"]  = "-12..14",
                    ["Format"]            = "\"HH:mm:ss\"",
                    ["Format__KnownValues"] = "HH:mm:ss,HH:mm,hh:mm:ss A,hh:mm A",
                    ["PreviewText"]       = "\"12:34:56\"",
                });

            // Countdown.Remaining — reads a Hub-backed Countdown timer (TimerMode
            // .Countdown) by name and shows its remaining time. Identical wiring to
            // Timer.Remaining: both read the mode-aware short/long/clock field out of
            // the SAME Overlay Live Channel key family (timer.<root>.*) through the
            // same browser reader. It exists as its own palette entry so a "countdown
            // widget" is discoverable and can default to countdown-friendly values.
            // The duration + "Default Time" live on the Hub timer (set in the
            // Pre-Builds panel / by an Architect Timer.Start Duration); this node
            // only displays.
            //
            // Carries the same appended channel pins as Timer.Remaining above
            // (Progress / Seconds / Paused / Mode / Live) — all three timer readers
            // share one key family and one browser reader, so they must expose the same
            // pins or an author would have to swap node types to get a progress bar.
            // Same two-status-pin split, documented in full on Timer.Remaining:
            // State = the RUN state (Running/Paused/Stopped/Ended), read as the value
            // of timer.<root>.state; Live = that key's Active/Stale/Missing liveness.
            Add("Countdown.Remaining", "Time",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Text",     SocketDataType.String),
                    S("State",    SocketDataType.String),
                    S("Progress", SocketDataType.Scalar),
                    S("Seconds",  SocketDataType.Scalar),
                    S("Paused",   SocketDataType.String),
                    S("Mode",     SocketDataType.String),
                    S("Live",     SocketDataType.String),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["TimerName"]   = "\"\"",
                    ["Format"]      = "\"clock\"",
                    ["Format__KnownValues"] = "short,long,clock",
                    ["PreviewText"] = "\"05:00\"",
                });

            // Stopwatch.Elapsed — reads a Hub-backed Stopwatch timer (TimerMode
            // .Stopwatch) by name and shows its count-UP elapsed time. Hub publishes
            // the elapsed value into the same short/long/clock channel field, so the
            // browser reader is shared with Timer.Remaining / Countdown.Remaining.
            // Start/pause/stop are driven by Architect Timer.Start / Timer.Pause /
            // Timer.Stop on the named stopwatch.
            //
            // Same appended channel pins as the two readers above (Progress / Seconds /
            // Paused / Mode / Live), and the same State-vs-Live split documented on
            // Timer.Remaining: State = the RUN state value of timer.<root>.state,
            // Live = that key's Active/Stale/Missing liveness. Note that Seconds counts
            // UP here without any special casing in the graph: the channel's
            // display_seconds field is already mode-aware Hub-side, so a Stopwatch
            // publishes elapsed into the same key a Countdown publishes remaining into.
            Add("Stopwatch.Elapsed", "Time",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Text",     SocketDataType.String),
                    S("State",    SocketDataType.String),
                    S("Progress", SocketDataType.Scalar),
                    S("Seconds",  SocketDataType.Scalar),
                    S("Paused",   SocketDataType.String),
                    S("Mode",     SocketDataType.String),
                    S("Live",     SocketDataType.String),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["TimerName"]   = "\"\"",
                    ["Format"]      = "\"clock\"",
                    ["Format__KnownValues"] = "short,long,clock",
                    ["PreviewText"] = "\"00:00:00\"",
                });

            // Loyalty.Leaderboard — live viewer-points leaderboard readout (Loyalty
            // tool, Layer 5). Structural twin of Timer.Remaining: Text is the formatted,
            // newline-joined ranked board; State is this node's status verdict —
            // Active / Stale / Missing, plus EMPTY for a board that is being published
            // but has no rows yet. Empty is load-bearing back-compat: widgets already
            // branch on it to show a "no scores yet" state, so it must survive every
            // rework of this reader.
            //
            // WHERE THE VALUES COME FROM (this used to say LOYALTY_UPDATE, a broadcast
            // frame the channel rework RETIRED): Hub's LoyaltyService publishes the
            // standings as a real JSON array under the Overlay Live Channel key
            // loyalty.leaderboard, plus the streamer's currency name under
            // loyalty.currency. The store ships them in the same coalesced 1 Hz
            // LIVE_PATCH as every other subscribed key, and the browser-side
            // evalLoyaltyLeaderboard in compositor.js formats them.
            //
            // The C# design-time mirror (NodeEvaluator.EvalStringNode) has no channel,
            // so it previews from PreviewText and reports State from it: a mock board
            // present reads Active, a cleared PreviewText reads Empty — both values the
            // browser really can emit. Note this node has NO Live pin: unlike the timer
            // trio it has no run state to report, so State IS the liveness verdict (the
            // asymmetry is documented in full on Timer.Remaining). Lives in the "Text"
            // allowed category (loyalty data is NOT an allowed data category — the
            // readout is a pure widget-local text producer, like Timer.Remaining /
            // Text.Render).
            //
            // Attributes:
            //   Size        : int     how many ranks the board renders (default 10).
            //   Format      : string  per-line template; {rank}/{name}/{balance} and
            //                         {currency} tokens. {currency} resolves from the
            //                         channel's loyalty.currency key (the streamer's
            //                         own plural currency name, e.g. "Feathers"), so a
            //                         board no longer prints a bare number with the
            //                         unit hard-coded into the format string by hand.
            //   Index       : int     which row the per-row pins below read, 1-BASED —
            //                         Index 1 is the top of the board, so it lines up
            //                         with the {rank} the same row prints. Out of range
            //                         (board shorter than Index) yields Rank 0 /
            //                         Name "" / Balance 0, never a stale previous row.
            //   PreviewText : string  design-time multi-line placeholder shown until a
            //                         live board arrives. DESIGN TIME ONLY — the browser
            //                         must never fall back to it (the shipped default is
            //                         a mock ranking of "viewer_one / viewer_two", and
            //                         rendering that on a live stream after every scene
            //                         return is the exact bug the channel exists to end).
            //
            // Appended pins (after the pre-existing Text/State pair; never reordered):
            //   Rank    : Scalar  the row's rank number.
            //   Name    : String  the row's viewer name.
            //   Balance : Scalar  the row's balance as an exact JSON number.
            // They exist because the joined Text pin is one opaque block: a "top 3"
            // built from three separately-styled widgets, a crown image on rank 1, or
            // a bar scaled by the leader's balance all need the row's parts, not its
            // rendered line. Balance is a Scalar so the bar math needs no string parse.
            Add("Loyalty.Leaderboard", "Text",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Text",    SocketDataType.String),
                    S("State",   SocketDataType.String),
                    S("Rank",    SocketDataType.Scalar),
                    S("Name",    SocketDataType.String),
                    S("Balance", SocketDataType.Scalar),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["Size"]        = "10",
                    ["Index"]       = "1",
                    ["Format"]      = "\"{rank}. {name} — {balance} {currency}\"",
                    ["PreviewText"] = "\"1. viewer_one — 12,400\n2. viewer_two — 9,830\n3. viewer_three — 7,215\n4. viewer_four — 5,140\n5. viewer_five — 3,905\"",
                });

            // Loyalty.Balance — single-viewer points readout (Loyalty tool, Layer 5).
            // Twin of Loyalty.Leaderboard but scoped to one user. Text output = the
            // formatted "{name}: {balance}" line for the User attribute; State =
            // Active / Stale / Missing (no Empty here — a one-row read either resolves
            // or it doesn't; and no Live pin, for the same reason the leaderboard has
            // none: there is no run state, so State IS the liveness verdict). The
            // runtime looks the user up in the SAME loyalty.leaderboard channel array
            // the board reads (compositor.js evalLoyaltyBalance) — per-user balance keys
            // are deliberately not published, and the LOYALTY_UPDATE payload this
            // comment used to name was retired with the channel rework. Design-time
            // returns the PreviewText mock. Same "Text" allowed-category rationale as
            // Loyalty.Leaderboard above.
            //
            // Attributes:
            //   User        : string  the viewer whose balance to show (empty = mock).
            //   Format      : string  line template; {name}/{balance}/{rank} tokens.
            //   PreviewText : string  design-time placeholder.
            //
            // Appended pin: Balance (Scalar) — the user's balance as an exact number,
            // so "scale this bar / this crown by how many points they have" needs no
            // string parse. It is derived browser-side from the loyalty.leaderboard
            // key's byName index, exactly as the Text pin already is: per-user balance
            // keys are deliberately NOT published, because the key set would grow with
            // the viewer list and the channel is a bounded temp store.
            Add("Loyalty.Balance", "Text",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Text",    SocketDataType.String),
                    S("State",   SocketDataType.String),
                    S("Balance", SocketDataType.Scalar),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["User"]        = "\"\"",
                    ["Format"]      = "\"{name}: {balance}\"",
                    ["PreviewText"] = "\"viewer_one: 12,400\"",
                });

            // Counter.Value — live named-counter readout (Counters tool). Structural
            // twin of Loyalty.Balance: Text is the formatted count, State is the
            // liveness verdict — Active / Stale / Missing (no Live pin; no run state
            // exists here, so State carries liveness, per the asymmetry documented on
            // Timer.Remaining). Hub's CountersService publishes the value under the
            // Overlay Live Channel key counter.<name>.count — the COUNTER_UPDATE
            // broadcast this comment used to name was retired with the channel rework —
            // and the browser-side evalCounterValue in compositor.js reads that one key.
            // Because the key IS the counter, key-liveness and counter-existence are the
            // same question: Missing covers both "no such counter" and "this node names
            // none". The C# design-time mirror (NodeEvaluator.EvalStringNode) returns
            // "Active" / PreviewText so the canvas thumbnail isn't blind before a live
            // frame arrives. Lives in the "Text" allowed category (counter data is NOT
            // an allowed data category — the readout is a pure widget-local text
            // producer, like Timer.Remaining / Loyalty.Balance / Text.Render).
            //
            // Attributes:
            //   Name        : string  which counter to show (empty = mock).
            //   Format      : string  line template; {name}/{count} tokens.
            //   PreviewText : string  design-time placeholder shown until the first live
            //                         counter.<name>.count value arrives.
            //
            // Appended pin: Value (Scalar) — the count as the exact JSON number the
            // channel publishes under counter.<name>.count. The Text pin runs the
            // count through Format first, so anything numeric downstream (a death
            // counter driving a shake amplitude, a goal bar) previously had to
            // Convert.StringToNumber a formatted string back into a number.
            Add("Counter.Value", "Text",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Text",  SocketDataType.String),
                    S("State", SocketDataType.String),
                    S("Value", SocketDataType.Scalar),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["Name"]        = "\"\"",
                    ["Format"]      = "\"{count}\"",
                    ["PreviewText"] = "\"0\"",
                });

            // ══ V10 — the channel-fed widget family, and the key map it reads ══════
            //
            // The family is goals, stat labels, an event list, tip jar / ticker /
            // top-donator board, Stream Boss, chat highlight, emote wall, end credits,
            // sponsor rotator, next-stream countdown, viewer queue and poll / bet result
            // bars. Every one of them is PUBLISHED KEYS PLUS A READER: no new message
            // type, no new render pass, no per-tool special case. That is the whole
            // no-special-treatment rule — a family member that needed its own broadcast
            // would be a second data path onto the same canvas.
            //
            // Only TWO readers were missing, and both are generic rather than per-tool:
            // the goal family reader below (a four-key root, exactly the shape the timer
            // trio already has) and the LIST reader after it (the array analogue of
            // Var.Live). Everything else in the family reads through nodes that already
            // exist — Var.Live for any single value, the timer trio for a countdown,
            // Image.Load's wirable Path for a per-row image — and adding a
            // Stat.LatestFollower or a TipJar.Total would be the per-tool special-casing
            // the channel exists to abolish.
            //
            // KEY MAP. Only the goal.* roots are RESERVED (the constants at the top of
            // this file — reserved FOR Hub's Twitch goal / charity ingestion, which has
            // not landed yet, so nothing fills them today). Everything below
            // is a CONVENTION for author space — the channel enforces no prefixes and no
            // reservations, last write wins, and provenance makes any same-key fight
            // visible. These names are written down so a tool, a script and a widget
            // pick the same spelling by default, NOT because anything rejects others:
            //
            //   latest follower / sub    stat.latest_follower  stat.latest_sub
            //   top tipper               stat.top_tipper
            //   session totals           stat.session.<what>       (…followers, …bits, …tips)
            //   viewer count             stat.viewers
            //   tip jar total            tip.session.total
            //   event list               list.events           (array, newest first)
            //   tip ticker / top board   list.tips             (array)
            //   viewer queue             list.queue            (array)
            //   emote wall               list.emotes           (array of relative paths)
            //   end credits              list.credits          (array)
            //   sponsor rotator          list.sponsors         (array)
            //   poll / bet results       list.poll             (array of { label, votes })
            //   chat highlight           chat.highlight.user   chat.highlight.text
            //   Stream Boss              goal.custom_boss.*    (the goal contract, reused)
            //   next-stream countdown    the timer trio, or schedule.next.<field>
            //
            // A list key holds a JSON ARRAY. The reader addresses rows by index and
            // formats them by FIELD NAME, so a publisher chooses its own row shape and no
            // row schema is baked into the palette.

            // ── V10 — Goal.Progress: the goal.<kind>.* family reader ───────────────
            //
            // Reads one goal root — current / target / progress / label — off the Overlay
            // Live Channel and exposes it as the pins a bar and its caption need.
            //
            // WHY A NODE RATHER THAN FOUR Var.Live NODES. Var.Live can already bind any
            // one of the four keys, so this node buys nothing an author could not wire by
            // hand; what it removes is the four chances to mistype a dotted key. The
            // browser derives its subscription from attribute TEXT at graph-scan time, so
            // a typo'd key is a permanently blank pin with a valid graph, a running
            // publisher and no error on either side — the exact failure mode the timer
            // family's three-root namespace exists to avoid, and the same reason the
            // timer trio is one node with six pins instead of thirteen bindings. One
            // Kind attribute, one prefix subscription, four pins that cannot drift.
            //
            // It is also what makes the contract DISCOVERABLE. A key family nobody can
            // find in the Add-Node menu is a key family nobody publishes into.
            //
            // Attributes:
            //   Kind        : string  the goal kind — follower / sub / bits / tip /
            //                         charity, or custom_<slug> for an author goal.
            //                         ALWAYS trimmed. Case-folded ONLY when the folded
            //                         form is one of the five RESERVED kinds above; a
            //                         custom_<slug> reaches the key EXACTLY as typed.
            //                         The scope is the whole point: OverlayLiveStore
            //                         matches Ordinal and its Norm() only trims.
            //                         WHICH CASE THAT PUTS A PUBLISHER IN — two, and
            //                         this line covers both so neither is read alone:
            //                         CASE 1, an AUTHOR-typed slug (a script's
            //                         overlay.publish), must be spelled
            //                         character-for-character the way the author did;
            //                         CASE 2, a MACHINE-derived slug (the Twitch goal
            //                         producer, which has no author text to preserve),
            //                         is lower-cased + slugged by that producer, so the
            //                         author has to type the kind in lower case to match
            //                         it. Both cases in full on GoalCustomKindPrefix.
            //                         Fold on one side only and the publisher-side
            //                         subscription gate drops every write — a blank bar
            //                         with a running producer and no error anywhere.
            //                         Deliberately NO __KnownValues companion: that
            //                         renders a dropdown, and a dropdown cannot express
            //                         custom_subathon. Same call Visual.Arg made about
            //                         its Key for the same reason.
            //   Format      : string  the Text pin's template. Tokens {current} {target}
            //                         {progress} {percent} {label} {kind}; current and
            //                         target are thousands-grouped, matching the Counter
            //                         and Loyalty formatters.
            //   PreviewText : string  design-time placeholder. DESIGN TIME ONLY — on
            //                         stream an unpublished goal renders nothing, which
            //                         is the fake-data-on-air bug this rework removed.
            //
            // Pins:
            //   Text     : String  Format, rendered.
            //   State    : String  Active / Stale / Missing — liveness, as on every
            //                      channel reader except the timer trio (which needed a
            //                      second pin because it also has a RUN state).
            //   Progress : Scalar  0..1, clamped HERE as well as at the publisher: an
            //                      author can publish this key by hand with
            //                      overlay.publish, and a stray 5 must not make a bar
            //                      500% wide.
            //   Current  : Scalar  exact published number.
            //   Target   : Scalar  exact published number.
            //   Label    : String  the publisher's display label, empty when unpublished.
            //                      It must NOT fall back to PreviewText — that attribute
            //                      holds a whole formatted line, so a pin carrying one
            //                      label would hand its consumer the entire mock.
            //
            // Partial publishers are supported on purpose: a script that publishes only
            // current and target (the common case for a custom_<slug> goal) still gets a
            // working Progress, derived by the contract's own formula. See the browser
            // reader — the derivation exists as a FALLBACK, never as an override, so a
            // published progress always wins and the two halves cannot disagree.
            Add("Goal.Progress", "Text",
                inputs:  Empty(),
                outputs: new[]
                {
                    S("Text",     SocketDataType.String),
                    S("State",    SocketDataType.String),
                    S("Progress", SocketDataType.Scalar),
                    S("Current",  SocketDataType.Scalar),
                    S("Target",   SocketDataType.Scalar),
                    S("Label",    SocketDataType.String),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["Kind"]        = "\"follower\"",
                    ["Format"]      = "\"{current} / {target}\"",
                    ["PreviewText"] = "\"120 / 250\"",
                });

            // ── V10 — List.Live: the channel ARRAY reader ──────────────────────────
            //
            // Binds one literal channel key whose value is a JSON array and exposes the
            // rows four ways: all of them joined, one of them formatted, one FIELD of one
            // of them raw, and that field as a number.
            //
            // WHY IT EXISTS. Var.Live handles any single value; on an array its Text pin
            // yields compact JSON, which is unpaintable. The only list reader in the
            // catalog was Loyalty.Leaderboard, and it is hardwired to one key with three
            // loyalty tokens. Eight members of the widget family are list-shaped — event
            // list, tip ticker, top-donator board, viewer queue, emote wall, end credits,
            // sponsor rotator, poll / bet result rows — so this is ONE node standing in
            // for eight per-tool readers, which is precisely the trade the channel model
            // asks for.
            //
            // FORMAT TOKENS ARE FIELD NAMES. {index} is the 1-based row position and
            // {value} is the row itself when the array holds bare strings or numbers;
            // every other {token} is looked up as a field of the row object,
            // case-insensitively. So a publisher picks its own row shape — { name, amount }
            // or { label, votes } or { user, months } — and no row schema is baked into
            // the palette.
            //
            // ★ ONLY A FIELD THAT ARRIVES AS A JSON NUMBER IS THOUSANDS-GROUPED. A field
            // published as TEXT renders EXACTLY as published and is never rewritten: an
            // all-digit Twitch login stays 12345678 rather than gaining commas, and a
            // "5.00" amount keeps its cents rather than collapsing to 5. This deliberately
            // does NOT match formatCounterLine / formatLoyaltyLine — their row shapes are
            // FIXED, so each knows which single token ({count} / {balance}) is a number
            // and coerces that one while emitting the rest verbatim. Here the row shape is
            // PUBLISHER-CHOSEN, so the value's own JSON type is the only honest signal;
            // grouping whatever merely looked numeric corrupted real data with no opt-out.
            //
            // An unresolvable token renders empty rather than leaking
            // its own braces onto the canvas.
            //
            // ★ A KEY THAT DOES NOT HOLD A LIST IS REPORTED, and the report says which of
            // three things went wrong so the author is not sent hunting the wrong one:
            // list_not_json_array (the published text is not JSON at all — an unquoted key,
            // a trailing comma, a single-quoted string), list_json_not_array (valid JSON of
            // the wrong shape — an object where an array was meant, the commonest mistake)
            // and list_value_not_array (a JSON number / bool / object was published under
            // the key). All three land in Hub's System Log once per key, because an OBS
            // Browser Source has no console anyone reads. A key nobody has published yet is
            // NOT reported — that is Missing, the honest state of every overlay before its
            // producer's first tick.
            //
            // Index is a wirable Scalar with the attribute as its fallback, and that one
            // pin is what turns this node into a rotator: a Time.Sawtooth scaled by the
            // Count pin and floored drives a tip ticker or a sponsor carousel with no
            // further nodes and no new kernel. It is 1-BASED, matching
            // Loyalty.Leaderboard's Index for the same reason — the rows a board prints
            // are 1-based, so any other choice puts the node's own two numbers in
            // contradiction. Out of range yields empty / 0, never a wrapped row: showing
            // row 1 where the author asked for row 12 is a wrong answer dressed as a
            // right one.
            //
            // Attributes:
            //   Key         : string  the literal channel key holding the array. LITERAL
            //                         is the documented limit, exactly as on Var.Live: the
            //                         subscription is read out of this box when the graph
            //                         is scanned, so a computed key is publishable but
            //                         never bindable.
            //   Field       : string  which field the Value / Number pins read off the
            //                         addressed row.
            //   Format      : string  per-row template (see tokens above).
            //   Size        : int     how many rows the Text pin joins (default 10).
            //   Index       : int     1-based row the Row / Value / Number pins address.
            //   PreviewText : string  design-time placeholder, one mock row per line.
            //                         DESIGN TIME ONLY.
            //
            // Pins:
            //   Text   : String  the top Size rows, Format-templated, newline-joined.
            //                    Text.Render renders those rows as rows since V10.
            //   Row    : String  the single addressed row, Format-templated.
            //   Value  : String  row[Field], raw and unformatted — this is the pin that
            //                    feeds a wirable Image.Load Path for an emote wall or a
            //                    sponsor logo.
            //   Number : Scalar  row[Field] as a number, 0 on failure and never NaN, so a
            //                    poll bar's width needs no string parse.
            //   Count  : Scalar  how many rows the array holds.
            //   State  : String  Active / Stale / EMPTY. Empty rather than Missing for a
            //                    row-less list, matching Loyalty.Leaderboard: widgets
            //                    branch on that word to draw a "nothing yet" card, and a
            //                    never-published list and an empty one are the same thing
            //                    to a widget. Stale stays distinct — a stale list keeps
            //                    painting its last rows, so a widget hiding on Empty must
            //                    not hide on Stale.
            Add("List.Live", "Text",
                inputs:  new[] { S("Index", SocketDataType.Scalar) },
                outputs: new[]
                {
                    S("Text",   SocketDataType.String),
                    S("Row",    SocketDataType.String),
                    S("Value",  SocketDataType.String),
                    S("Number", SocketDataType.Scalar),
                    S("Count",  SocketDataType.Scalar),
                    S("State",  SocketDataType.String),
                },
                attrs:   new Dictionary<string, string>
                {
                    ["Key"]         = "\"\"",
                    ["Field"]       = "\"name\"",
                    ["Format"]      = "\"{index}. {name}\"",
                    ["Size"]        = "10",
                    ["Index"]       = "1",
                    ["PreviewText"] = "\"1. row_one\n2. row_two\n3. row_three\"",
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

            // ── V7 — Visual.Arg: one named field of the trigger payload, as a String ──
            //
            // Reads triggerContext.eventData[Key] browser-side. Same role Message.Read
            // above has, and the overlap is DELIBERATE rather than overlooked — the one
            // semantic difference is the whole reason this node exists:
            //
            //   Message.Read falls back to its MockValue attribute IN PRODUCTION. That
            //   is the fake-data-on-stream class this rework exists to kill (the shipped
            //   Loyalty.Leaderboard placeholder painting invented viewer names onto a
            //   live stream after every OBS scene return). Visual.Arg's PreviewText is
            //   DESIGN-TIME ONLY: on stream an unsupplied arg renders NOTHING, exactly
            //   like Var.Live and every channel reader.
            //
            // That matters concretely for a COMPILED graph (the Alert Box): its rows are
            // regenerated from settings, so a mock that leaks to air is not something the
            // author can be relied on to notice. Message.Read is left untouched — it is
            // in saved layers and silently blanking it would be a behaviour change to
            // authored work — but new graphs should use this node. Recommended follow-up:
            // fold Message.Read into Visual.Arg with a one-way migration.
            //
            // Attributes:
            //   Key         : the eventData field name. Hub's ScriptManager splits a
            //                 Visual.Trigger's Args="a,b,c" into Args1..Args3, and the
            //                 Alerts tool ships Args1=kind label, Args2=user, Args3=size.
            //                 "user" / "message" are also present on chat-fed triggers.
            //                 Deliberately NOT an enum (__KnownValues) — that would turn
            //                 the box into a dropdown and lock out Args4+.
            //   PreviewText : design-time placeholder. Never read in production.
            //
            // Category "Triggers": it reads the trigger context, which exists only for
            // the span of one trigger — the same argument that files Message.Read there
            // and that keeps the ambient Var.Live out of it.
            Add("Visual.Arg", "Triggers",
                inputs:  Empty(),
                outputs: O("Value", SocketDataType.String),
                attrs:   new Dictionary<string, string>
                {
                    ["Key"]         = "\"Args1\"",
                    ["PreviewText"] = "\"\"",
                });

            // ── V7 — String.Select: N-way value mapping, with a mandatory default ──
            //
            // The per-kind mapping a compiled Alert Box graph turns its settings into:
            // ONE Audio.Load + ONE Audio.Play, with the clip chosen by VALUE. This is
            // the shape the design lands on because branch-gated audio is not
            // expressible (see Audio.Load above), so multi-way selection has to happen
            // upstream of the loader, in the string world, rather than as N parallel
            // Result.If arms merged at Display.
            //
            // Semantics, mirrored byte-for-byte by compositor.js evalStringSelect and
            // NodeEvaluator's "String.Select" arm:
            //   • When   — the value being matched. WIRE Visual.Arg into it.
            //              ★ NOT an event-data KEY. Result.If's identically-named When
            //              attribute IS a key (it looks eventData[When] up); this one is
            //              the string itself. The names collide because both read
            //              naturally in English; the tooltip says so explicitly.
            //              Unwired, the same-named attribute is the fallback.
            //   • Case<i> / Value<i>, i = 1..StringSelectRows — rows, in order. The
            //              first Case that EXACTLY equals When (Ordinal, case-sensitive,
            //              matching Result.If) wins and the node emits its Value.
            //              Case-insensitive matching was rejected on purpose: JS
            //              toLowerCase() and .NET OrdinalIgnoreCase do not agree on
            //              every Unicode input, and a browser/mirror disagreement about
            //              which row won is the worst possible failure here.
            //   • A row whose Case is EMPTY is UNCONFIGURED and never matches. Without
            //              that rule an empty When (the normal state on an onStartup
            //              render, where no event data exists) would match the first
            //              blank row and emit its Value — i.e. a fresh node would
            //              silently pick row 1.
            //   • Default — emitted when nothing matched. MANDATORY, not a nicety: the
            //              Alerts tool's KindLabel has an "ALERT" fallback arm, so an
            //              unmapped family really does arrive, and a select without a
            //              default would render nothing at all for it.
            //
            // Rows are attributes, not sockets. That follows the only precedent in the
            // codebase for "N author rows" — Architect's Logic.Switch keeps one
            // attribute per case keyed by the case name — and it is what makes the node
            // round-trip through the serializer with no new persistence plumbing: node
            // attributes are a flat Dictionary<string,string>, and a variable-length
            // list has NO precedent anywhere in either registry. CSV-in-one-attribute
            // was rejected: Particles.Emit tried it for vectors and LayerGraphMigrator
            // exists purely to undo that.
            Add("String.Select", "String",
                inputs:  new[] { S("When", SocketDataType.String) },
                outputs: O("Value", SocketDataType.String),
                attrs:   BuildStringSelectAttrs());
        }

        /// <summary>
        /// Default attributes for <c>String.Select</c>: the When fallback, twelve
        /// Case/Value row pairs (<see cref="StringSelectRows"/>) and the mandatory
        /// Default. Built in a loop so the row count has exactly one definition.
        /// Insertion order is preserved by <see cref="Dictionary{TKey,TValue}"/> for an
        /// add-only dictionary, which is what keeps the Inspector rows in row order.
        /// All values are JSON-quoted string literals — the storage convention the
        /// Inspector's String/Enum/MediaPath commit path writes and every reader
        /// quote-strips.
        /// </summary>
        private static Dictionary<string, string> BuildStringSelectAttrs()
        {
            var attrs = new Dictionary<string, string>(2 + StringSelectRows * 2)
            {
                ["When"] = "\"\"",
            };
            for (int i = 1; i <= StringSelectRows; i++)
            {
                attrs[$"Case{i}"]  = "\"\"";
                attrs[$"Value{i}"] = "\"\"";
            }
            attrs["Default"] = "\"\"";
            return attrs;
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
            // opts into a body preview. Kept only so a serialized graph round-trips
            // unchanged — the consumer it was written for
            // (WidgetGraphCanvas.HasBodyPreview) no longer exists, and the live
            // mechanism is GetPreviewSource.
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
