using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// NodeEvaluator — design-time C# mirror of the browser-side graph evaluator in
    /// <c>data/overlay/compositor.js</c>. Walks a per-trigger graph upstream from the
    /// <c>Display</c> sink and computes metadata (output dimensions, source URLs) without
    /// performing actual image compositing.
    ///
    /// Used by Phase 5 tests to validate graph traversal + resolution-aware scaling.
    /// </summary>
    public static class NodeEvaluator
    {
        /// <summary>Result returned for the value flowing into the Display sink.</summary>
        public sealed class ImageMetadata
        {
            public string? Source         { get; set; } // file path or URL
            public int     Width          { get; set; }
            public int     Height         { get; set; }
            public bool    HasError       { get; set; }
            public string? ErrorMessage   { get; set; }

            /// <summary>
            /// Name of the kernel that produced this metadata (e.g.
            /// "Image.Mask", "Image.Blend"). Used by tests to assert graph
            /// traversal walked through the expected kernel.
            /// </summary>
            public string? Kernel         { get; set; }

            /// <summary>
            /// Kernel-specific attributes captured at evaluation time
            /// (e.g. Image.Blend's Mode, Image.Mask's Mode). Tests assert against
            /// these so the design-time mirror matches the JS-side behavior.
            /// </summary>
            public Dictionary<string, string> KernelAttributes { get; set; } = new();
        }

        /// <summary>
        /// Result of a whole-graph design-time evaluation.
        ///
        /// V14 removed a third member, <c>CompleteConnected</c>: it was written by the
        /// <c>Visual.Complete</c> sink walk and read by nothing, in production or in a
        /// test. Do NOT read that as "the Complete walk is dead" — the walk stays, and
        /// its product is <see cref="VisitedNodeIds"/> (see the walk itself for why).
        /// <see cref="DisplayConnected"/> is the near-name that looks identical in shape
        /// and is heavily asserted across the widget test suites — it stays.
        /// </summary>
        public sealed class EvalResult
        {
            public ImageMetadata? Display { get; set; }
            public bool           DisplayConnected { get; set; }
            public List<string>   VisitedNodeIds { get; } = new();
        }

        /// <summary>
        /// Body-preview snapshot kind. Drives how the canvas renders the per-node
        /// preview strip — bitmap, swatch, empty hint, or error tint. The live
        /// consumer chain is
        /// <c>WidgetGraphCanvas.RefreshPreviews()/RefreshPreviewsAtTime()</c> →
        /// <c>WidgetGraphNodeView.SetPreview</c> → <c>ThumbnailHost.SetSnapshot</c>
        /// (V11 comment fix: the <c>WidgetGraphCanvas.PaintBodyPreview</c> this line
        /// used to name has never existed — there is no canvas-side paint method for
        /// the strip, the snapshot is pushed into a child control).
        /// </summary>
        public enum PreviewKind
        {
            /// <summary>Template did not opt into a preview, or evaluator couldn't resolve one.</summary>
            Empty = 0,
            /// <summary>Resolved to a bitmap source (path / URL). Caller is responsible for loading the bytes.</summary>
            Image,
            /// <summary>Resolved to a solid colour (hex string).</summary>
            Color,
            /// <summary>Template wants a preview but no source could be resolved (unwired upstream, blank Path/Url, etc.).</summary>
            Unloaded,
            /// <summary>Evaluation surfaced an error (cycle, unknown upstream node, etc.).</summary>
            Error,
        }

        // PreviewSeam: wired during the live-thumbnail work.
        // EvaluatePreviews + PreviewSnapshot are
        // the engine-side surface for the canvas's per-node body preview
        // strip. WidgetGraphCanvas.RefreshPreviews() (Visualist.WinUI)
        // invokes EvaluatePreviews after Rebuild() and after every
        // graph-mutating commit (paste / wire add / wire delete / node
        // delete / attribute commit via MarkDirty) and pushes each
        // snapshot into WidgetGraphNodeView.SetPreview, which forwards to
        // the per-node ThumbnailHost (Visualist.WinUI/Controls/ThumbnailHost.xaml)
        // for bitmap / swatch / placeholder / error-tint rendering.
        /// <summary>
        /// Per-node body-preview snapshot. Computed by <see cref="EvaluatePreviews"/>
        /// at graph-load / graph-mutation time and consumed by the canvas paint
        /// pass — the paint code never re-walks the graph.
        /// </summary>
        public sealed class PreviewSnapshot
        {
            public PreviewKind Kind   { get; init; } = PreviewKind.Empty;
            /// <summary>File path or URL for <see cref="PreviewKind.Image"/>.</summary>
            public string?     Source { get; init; }
            /// <summary>Hex string ("#rrggbb" or "#rrggbbaa") for <see cref="PreviewKind.Color"/>.</summary>
            public string?     ColorHex { get; init; }
            /// <summary>Optional human-readable hint shown on Unloaded / Error states.</summary>
            public string?     Hint   { get; init; }
            /// <summary>The <see cref="PreviewSource"/> declared by the node's template.</summary>
            public PreviewSource Declared { get; init; } = PreviewSource.None;

            public static readonly PreviewSnapshot None = new();
        }

        // Per-call graph index + memo store threaded through the recursive walkers.
        // Built once at each public Evaluate* entry (never cached across calls —
        // attribute edits between calls must be seen) so node / link resolution is
        // an O(1) lookup per hop instead of a whole-graph FirstOrDefault scan,
        // which made a single evaluation quadratic in graph size. Both index maps
        // keep the first occurrence per key, preserving the prior FirstOrDefault
        // first-match semantics. The scalar / vector / string memos mirror
        // EvalImageMemoized so a pure-data chain feeding several downstream
        // sockets is walked once per evaluation instead of once per consumer.
        private sealed class EvalContext
        {
            public readonly Dictionary<string, double?>   ScalarMemo = new(StringComparer.Ordinal);
            public readonly Dictionary<string, double[]?> VectorMemo = new(StringComparer.Ordinal);
            public readonly Dictionary<string, string?>   StringMemo = new(StringComparer.Ordinal);

            private readonly Dictionary<string, Node> _nodeById;
            private readonly Dictionary<(string, string), Link> _linkByTarget;

            // V11 — playhead context. The timeline + time the caller wants this
            // evaluation sampled at. Carried on the context (rather than passed
            // through every walker) precisely because the context is ALREADY
            // threaded into all ~40 recursive call sites: adding a parameter to
            // each would be a large diff for zero behavioural gain. Defaults to
            // (null, 0) so every pre-V11 caller keeps its exact t=0 behaviour.
            public readonly WidgetTimeline? Timeline;
            public readonly double          TimeMs;

            // Lazy ParameterPath → keyframe-track index, built once per
            // evaluation from Timeline.SortedKeyframes (which is itself cached +
            // self-invalidating on the timeline). Built lazily because the vast
            // majority of evaluations never sample a keyframe at all.
            private Dictionary<string, List<Keyframe>>? _keyframesByPath;

            public EvalContext(Graph graph, WidgetTimeline? timeline = null, double timeMs = 0)
            {
                Timeline = timeline;
                TimeMs   = timeMs;
                _nodeById = new Dictionary<string, Node>(graph.Nodes.Count, StringComparer.Ordinal);
                foreach (var n in graph.Nodes)
                    if (n.Id is not null && !_nodeById.ContainsKey(n.Id)) _nodeById[n.Id] = n;
                _linkByTarget = new Dictionary<(string, string), Link>(graph.Links.Count);
                foreach (var l in graph.Links)
                {
                    if (l.ToNodeId is null || l.ToSocketId is null) continue;
                    var key = (l.ToNodeId, l.ToSocketId);
                    if (!_linkByTarget.ContainsKey(key)) _linkByTarget[key] = l;
                }
            }

            public Node? FindNode(string? id) =>
                id is not null && _nodeById.TryGetValue(id, out var n) ? n : null;

            /// <summary>First link wired into the (toNodeId, toSocketId) input socket.</summary>
            public Link? FindLink(string? toNodeId, string? toSocketId) =>
                toNodeId is not null && toSocketId is not null &&
                _linkByTarget.TryGetValue((toNodeId, toSocketId), out var l) ? l : null;

            /// <summary>
            /// Sample one animated parameter track at <see cref="TimeMs"/>.
            /// Returns false — leaving <paramref name="value"/> at 0 — when the
            /// context carries no timeline or that path has no keyframes, which
            /// is the caller's signal to fall back to the node's static literal.
            ///
            /// Deliberately NOT <c>KeyframeInterpolation.SampleScalar(timeline,
            /// timeMs)</c>: that overload feeds the WHOLE timeline into the
            /// sampler, and a timeline holds every parameter's track interleaved,
            /// so it only produces a correct answer for a single-track timeline.
            /// Per-parameter sampling has to split by ParameterPath first — which
            /// is exactly what compositor.js does
            /// (<c>keyframes.filter(k =&gt; k.parameterPath === path)</c>) — and then
            /// hand the single track to the list overload. The split is fed from
            /// <c>SortedKeyframes</c>, so each track comes out already ordered and
            /// already NaN/Infinity-filtered.
            /// </summary>
            public bool TrySampleParameter(string path, out double value)
            {
                value = 0;
                if (Timeline is null || string.IsNullOrEmpty(path)) return false;

                if (_keyframesByPath is null)
                {
                    _keyframesByPath = new Dictionary<string, List<Keyframe>>(StringComparer.Ordinal);
                    foreach (var kf in Timeline.SortedKeyframes)
                    {
                        if (kf is null || string.IsNullOrEmpty(kf.ParameterPath)) continue;
                        if (!_keyframesByPath.TryGetValue(kf.ParameterPath, out var track))
                        {
                            track = new List<Keyframe>();
                            _keyframesByPath[kf.ParameterPath] = track;
                        }
                        track.Add(kf);
                    }
                }

                if (!_keyframesByPath.TryGetValue(path, out var kfs) || kfs.Count == 0) return false;
                // SampleScalar clamps to the first/last keyframe outside the
                // track's range, so a playhead past either end is a hold, never
                // an exception.
                value = KeyframeInterpolation.SampleScalar(kfs, TimeMs);
                return true;
            }
        }

        // Thread-static holder for eventData + once-per-fire dedup. The evaluator is
        // synchronous and single-threaded per Evaluate() call (xUnit runs each test on
        // its own thread by default), so [ThreadStatic] keeps the current trigger's
        // arg snapshot reachable from Result.If's switch case without threading a new
        // parameter through every recursive image-eval method. Set in Evaluate() under
        // a try/finally and cleared on exit.
        [ThreadStatic] private static IReadOnlyDictionary<string, string>? _currentEventData;
        [ThreadStatic] private static HashSet<string>? _loggedMissingArgs;

        /// <summary>
        /// Evaluate a trigger graph against a layer + widget context. Returns metadata
        /// describing what would render into the widget's rect.
        /// </summary>
        public static EvalResult Evaluate(Graph graph, LayerResolution layerResolution, WidgetRect widgetRect)
            => Evaluate(graph, layerResolution, widgetRect, eventData: null);

        /// <summary>
        /// Overload that supplies the trigger's event-data snapshot. Args1..ArgsN keys
        /// (set by Hub's ScriptManager.ExpandArgsList from a Visual.Trigger node's Args
        /// Collection input) are read by Result.If's When attribute. A null or empty
        /// dictionary is equivalent to the no-args case — Result.If blocks any branch
        /// whose When references a missing key.
        /// </summary>
        public static EvalResult Evaluate(Graph graph, LayerResolution layerResolution, WidgetRect widgetRect,
            IReadOnlyDictionary<string, string>? eventData)
        {
            var prevEd  = _currentEventData;
            var prevLog = _loggedMissingArgs;
            _currentEventData  = eventData;
            _loggedMissingArgs = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                return EvaluateCore(graph, layerResolution, widgetRect);
            }
            finally
            {
                _currentEventData  = prevEd;
                _loggedMissingArgs = prevLog;
            }
        }

        /// <summary>
        /// Computes a <see cref="PreviewSnapshot"/> per node in the graph, keyed by
        /// node id. The canvas calls this once per graph mutation (load / wire /
        /// detach / attribute commit) and reads back during paint without re-walking
        /// the graph. Only nodes whose template declares a non-None
        /// <see cref="PreviewSource"/> are included in the result.
        ///
        /// The snapshot intentionally does NOT load bitmap bytes — that's the
        /// canvas's job (paint code owns the thumbnail cache + the async fetch
        /// pump). The snapshot only resolves "what source the preview rect should
        /// show" so the paint pass can short-circuit on Empty / Unloaded / Color
        /// without doing graph work on the UI thread.
        ///
        /// Caches and invalidation: the result is a fresh dictionary each call
        /// (no cross-call caching at this layer — the canvas owns staleness via
        /// its OnGraphMutated hook). Walks are bounded to 100 hops to mirror the
        /// MaxExecutionDepth philosophy of the script engine.
        ///
        /// V11 — playhead-following previews. <paramref name="timeline"/> +
        /// <paramref name="timeMs"/> are the trigger's animation track and the
        /// playhead position to resolve the snapshot at; both are optional and
        /// default to "no timeline, t = 0", which is byte-for-byte the pre-V11
        /// behaviour for every existing caller.
        ///
        /// ★ THE HONEST SCOPE LIMIT — this is a SOURCE resolver, not a renderer.
        /// The snapshot only answers "what source should the strip show", so the
        /// only attributes it reads are <c>Path</c>, <c>Url</c> and the colour
        /// literal (<c>Value</c>/<c>Color</c>). Of those three, a colour is the
        /// only thing anyone realistically keyframes — a file path and a URL are
        /// not animatable values. So passing a timeline in makes animated COLOUR
        /// swatches follow the playhead and changes nothing else: an
        /// Image.Load / Image.LoadUrl / upstream-image preview is identical at
        /// every time, by design and not by omission. Making the preview show
        /// animated Transform / ColorAdjust OUTPUT would mean rendering pixels
        /// design-time (a compositor port, not a plumbing change) and is
        /// explicitly out of V11's scope. Do not "fix" the image previews here.
        /// </summary>
        public static IReadOnlyDictionary<string, PreviewSnapshot> EvaluatePreviews(
            Graph graph, WidgetTimeline? timeline = null, double timeMs = 0)
        {
            var snapshots = new Dictionary<string, PreviewSnapshot>(StringComparer.Ordinal);
            if (graph is null) return snapshots;

            // One index build for the whole pass — the upstream walk below runs for
            // every preview node on every graph mutation, so a per-node index build
            // would be O(N·(N+M)) on the UI thread. The playhead context rides the
            // same object (see EvalContext.Timeline / TimeMs).
            var ctx = new EvalContext(graph, timeline, timeMs);
            foreach (var node in graph.Nodes)
            {
                var src = NodeTemplates.GetPreviewSource(node.Title);
                if (src == PreviewSource.None) continue;
                snapshots[node.Id] = ResolvePreviewSnapshot(ctx, node, src);
            }
            return snapshots;
        }

        private static PreviewSnapshot ResolvePreviewSnapshot(EvalContext ctx, Node node, PreviewSource src)
        {
            switch (src)
            {
                case PreviewSource.OwnPath:
                {
                    // V7 — Path may be WIRED now, so the body strip resolves the same way
                    // the evaluator does: upstream string first, own attribute second.
                    // Without this, every node in a compiled Alert Box graph (whose paths
                    // are all wired) would show "(no path set)" in its body — telling the
                    // author something that is not true and making a correct graph look
                    // broken. The two hints below are deliberately different so the
                    // author can tell "you have not set a path" from "the wire is there
                    // but resolves to nothing at design time", which is the NORMAL state
                    // for a path driven off live event data — and from the third case, a
                    // wired non-relative path the overlay will refuse to fetch.
                    string path = ResolvePathAttrOrWire(ctx, node, out bool pathHasWire, out bool pathRejected);
                    if (string.IsNullOrEmpty(path))
                        return new PreviewSnapshot
                        {
                            Kind     = PreviewKind.Unloaded,
                            Declared = src,
                            Hint     = EmptyPathHint(pathHasWire, pathRejected),
                        };
                    return new PreviewSnapshot { Kind = PreviewKind.Image, Source = path, Declared = src };
                }

                case PreviewSource.OwnUrl:
                {
                    string url = StripQuotes(node.Attributes.GetValueOrDefault("Url", string.Empty));
                    if (string.IsNullOrEmpty(url))
                        return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = src, Hint = "(no url set)" };
                    return new PreviewSnapshot { Kind = PreviewKind.Image, Source = url, Declared = src };
                }

                case PreviewSource.OwnColor:
                    return ResolveOwnColorSnapshot(ctx, node);

                case PreviewSource.UpstreamImage:
                    return ResolveUpstreamImageSnapshot(ctx, node);
            }

            return new PreviewSnapshot { Declared = src };
        }

        /// <summary>
        /// V7 — resolve a preview node's <c>Path</c> the way the evaluator resolves it:
        /// a wired String input wins, the (quote-stripped) attribute is the fallback, and a
        /// WIRED value must be relative (<see cref="ResolveMediaPathInput"/> holds that rule;
        /// the preview goes through it so the node body cannot advertise a file the overlay
        /// will refuse to fetch).
        ///
        /// <paramref name="hasWire"/> and <paramref name="rejected"/> let the caller tell the
        /// three empty results apart: nothing typed, wired but unresolvable at design time
        /// (the normal state for a live-data path), and wired to a non-relative string.
        ///
        /// ★ <paramref name="hasWire"/> is LINK EXISTENCE, deliberately NOT the resolver's
        /// provenance flag. The two differ for a dangling wire — one whose upstream resolves
        /// to null, so the resolver falls back to the attribute and correctly reports an
        /// ATTRIBUTE value — and for the author-facing hint, link existence is the useful
        /// answer: "you wired something and it is giving me nothing" rather than "no path
        /// set". The guard inside <see cref="ResolveMediaPathInput"/> needs the opposite
        /// (value provenance), which is why these are two separate questions and not one.
        ///
        /// The walk state is minted here rather than threaded in because the preview pass
        /// is not part of the image walk — it enters per node from
        /// <see cref="EvaluatePreviews"/>, so there is no outer <c>visiting</c> set to
        /// share.
        ///
        /// KNOWN LIMIT, stated plainly rather than justified away: the
        /// <see cref="LayerResolution"/> passed here is ZERO, so a Path derived from the
        /// layer size previews at zero — <c>Math.Resolution → Convert.NumberToString →
        /// String.Concat → Path</c> shows "bg-0" where the overlay will fetch "bg-1920".
        /// (An earlier version of this comment claimed nothing reachable from a Path reads a
        /// layer size. That was wrong: Math.Resolution is in the SCALAR walker too, and
        /// Convert.NumberToString crosses from the string walker into it.) Threading the real
        /// resolution means giving <see cref="EvaluatePreviews"/> a resolution parameter and
        /// feeding it from the canvas — a caller outside this file — so it is deliberately not
        /// done here; the resolution-derived path is a rare authoring shape and the render
        /// itself is unaffected.
        /// </summary>
        private static string ResolvePathAttrOrWire(
            EvalContext ctx, Node node, out bool hasWire, out bool rejected)
        {
            hasWire = false;
            foreach (var s in node.Sockets)
            {
                if (s.Type != SocketType.Input || s.Name != "Path") continue;
                if (ctx.FindLink(node.Id, s.Id) is not null) hasWire = true;
                break;
            }

            return ResolveMediaPathInput(
                ctx, node, new LayerResolution(),
                new HashSet<string>(StringComparer.Ordinal), new List<string>(),
                out _, out rejected);
        }

        /// <summary>Hint text for an empty resolved preview path — see
        /// <see cref="ResolvePathAttrOrWire"/> for the three cases.</summary>
        private static string EmptyPathHint(bool hasWire, bool rejected)
            => rejected ? "(wired path must be relative)"
             : hasWire  ? "(path is wired)"
                        : "(no path set)";

        /// <summary>
        /// Resolve the colour-swatch preview for a single node at a playhead
        /// position, independent of a graph walk.
        ///
        /// Public because it is the narrow per-node resolver: it answers for ONE
        /// node without a graph walk, which is what the Inspector swatch and the
        /// playhead-debounce path need. The graph-walk route into the same branch
        /// is live as well — <c>Color.Constant</c> declares
        /// <see cref="PreviewSource.OwnColor"/> (V11) and <c>Image.Solid</c>
        /// followed it, so <see cref="EvaluatePreviews"/> dispatches here through
        /// the walk too. Both entries share <c>ResolveOwnColorSnapshot</c>, so
        /// they cannot disagree.
        /// </summary>
        public static PreviewSnapshot ResolveColorSwatchPreview(
            Node node, WidgetTimeline? timeline = null, double timeMs = 0)
        {
            if (node is null) return PreviewSnapshot.None;
            // Empty graph: the colour resolver reads only the node's own
            // attributes + the timeline, never a link hop, so it needs no index.
            return ResolveOwnColorSnapshot(new EvalContext(new Graph(), timeline, timeMs), node);
        }

        // V11 — colour-swatch preview, sampled at the playhead.
        //
        // This is the ONE preview source a timeline can move (see the scope-limit
        // block on EvaluatePreviews): a colour literal decomposes into four 0–255
        // channel tracks at "<nodeId>.<colorKey>.R/.G/.B/.A" — the exact paths
        // AnimatedPinRegistry seeds and compositor.js's attrAnimatedColor samples —
        // so an author who keyframed a colour sees the node-body swatch travel with
        // the playhead instead of sitting frozen on the static literal.
        //
        // Mirrors attrAnimatedColor's contract arm for arm, so the design-time
        // swatch and the browser's rendered colour can't disagree:
        //   • no keyframes on ANY channel → return the static literal UNCHANGED
        //     (a non-hex CSS value therefore passes through untouched).
        //   • any channel keyframed       → sample the keyed channels, take the
        //     un-keyed ones from the static literal, recombine as #rrggbbaa
        //     (alpha LAST — the byte order ThumbnailHost.TryParseHex and
        //     AnimatedPinRegistry.TryParseHexColorChannels both expect).
        //   • static literal unparseable  → opaque white per channel, matching the
        //     JS fallback, instead of collapsing to a transparent-black swatch.
        private static PreviewSnapshot ResolveOwnColorSnapshot(EvalContext ctx, Node node)
        {
            const PreviewSource src = PreviewSource.OwnColor;
            var attrs = node.Attributes;

            // Colour-bearing attribute key: "Value" (Color.Constant) first, then
            // "Color". Which key won matters beyond reading the literal — it is
            // half of the parameter path the keyframes were seeded under, so it
            // can't be flattened into a single read-either lookup. Preserves the
            // pre-V11 precedence exactly, including "Value present but empty ⇒
            // Unloaded" (it does NOT fall through to "Color").
            string colorKey = attrs is not null && attrs.ContainsKey("Value") ? "Value" : "Color";
            string raw      = attrs is null ? string.Empty : attrs.GetValueOrDefault(colorKey, string.Empty);
            string hex      = StripQuotes(raw);

            // Static channel seeds. ReadComponentLiteral is the same helper the
            // animate gesture uses when it seeds a colour keyframe, so the preview
            // and the seeded keyframe agree on the channel values by construction.
            // When the literal isn't a parseable colour that helper returns 0 for
            // every channel (its numeric fallback) — detect that up front and use
            // opaque white instead, which is what the browser does.
            bool parseable = AnimatedPinRegistry.TryParseHexColorChannels(hex, out _, out _, out _, out _);
            var  channels  = AnimatedPinRegistry.GetColorChannelKeys(colorKey);

            bool anyAnimated = false;
            var  values      = new double[4];
            for (int i = 0; i < values.Length; i++)
            {
                string path = AnimatedPinRegistry.MakeParameterPath(node, channels[i]);
                if (ctx.TrySampleParameter(path, out double sampled))
                {
                    values[i]   = sampled;
                    anyAnimated = true;
                }
                else
                {
                    values[i] = parseable
                        ? AnimatedPinRegistry.ReadComponentLiteral(node, channels[i])
                        : 255.0;
                }
            }

            if (anyAnimated)
            {
                return new PreviewSnapshot
                {
                    Kind     = PreviewKind.Color,
                    ColorHex = "#" + HexByte(values[0]) + HexByte(values[1])
                                   + HexByte(values[2]) + HexByte(values[3]),
                    Declared = src,
                };
            }

            // No animation on this colour → pre-V11 behaviour, verbatim.
            if (string.IsNullOrEmpty(hex))
                return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = src, Hint = "(no color set)" };
            return new PreviewSnapshot { Kind = PreviewKind.Color, ColorHex = hex, Declared = src };
        }

        // 0–255 channel → two lowercase hex digits. Clamp + away-from-zero
        // rounding matches compositor.js's clampByte/hex2 pair so the C# swatch
        // and the browser's recombined colour land on the same byte.
        private static string HexByte(double channel)
        {
            if (double.IsNaN(channel)) channel = 0;
            int b = (int)Math.Round(Math.Clamp(channel, 0, 255), MidpointRounding.AwayFromZero);
            return b.ToString("x2", CultureInfo.InvariantCulture);
        }

        // Preferred image-input socket names, in priority order, for the
        // upstream-image hop below. Hoisted to a static field so the hop loop
        // doesn't re-allocate the array on every iteration.
        private static readonly string[] _preferredImageInputNames = { "In", "Image", "A" };

        // BFS-style walk from `node`'s canonical "In" socket to the nearest
        // resolvable Image source — Image.Load (file path) and Image.LoadUrl
        // (http/https url) both terminate the walk. Lives next to the rest of
        // the evaluator so the canvas can paint from a pre-computed snapshot
        // rather than re-walking the graph on every Invalidate.
        private static PreviewSnapshot ResolveUpstreamImageSnapshot(EvalContext ctx, Node start)
        {
            var inSock = start.Sockets.FirstOrDefault(s =>
                s.Type == SocketType.Input &&
                (s.DataType == SocketDataType.Image || s.DataType == SocketDataType.Any) &&
                string.Equals(s.Name, "In", StringComparison.OrdinalIgnoreCase));
            if (inSock is null)
                return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(no input)" };

            var firstLink = ctx.FindLink(start.Id, inSock.Id);
            if (firstLink is null)
                return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(no image upstream)" };

            var visited = new HashSet<string>(StringComparer.Ordinal) { start.Id };
            string nextNodeId = firstLink.FromNodeId;
            for (int hop = 0; hop < 100; hop++)
            {
                if (!visited.Add(nextNodeId))
                    return new PreviewSnapshot { Kind = PreviewKind.Error, Declared = PreviewSource.UpstreamImage, Hint = "(cycle in upstream chain)" };

                var n = ctx.FindNode(nextNodeId);
                if (n is null)
                    return new PreviewSnapshot { Kind = PreviewKind.Error, Declared = PreviewSource.UpstreamImage, Hint = "(broken link)" };

                if (string.Equals(n.Title, "Image.Load", StringComparison.OrdinalIgnoreCase))
                {
                    // V7 — same wired-Path resolution as the OwnPath branch, so a Viewer
                    // dropped downstream of a dynamic Image.Load previews what the loader
                    // will actually fetch instead of an empty attribute.
                    string p = ResolvePathAttrOrWire(ctx, n, out bool upHasWire, out bool upRejected);
                    if (string.IsNullOrEmpty(p))
                        return new PreviewSnapshot
                        {
                            Kind     = PreviewKind.Unloaded,
                            Declared = PreviewSource.UpstreamImage,
                            Hint     = EmptyPathHint(upHasWire, upRejected),
                        };
                    return new PreviewSnapshot { Kind = PreviewKind.Image, Source = p, Declared = PreviewSource.UpstreamImage };
                }

                // Image.LoadUrl is the second resolvable terminal: the snapshot
                // carries the raw Url and the WinUI ThumbnailHost loads http(s)
                // URIs through the same BitmapImage path it uses for absolute
                // file paths, so a LoadUrl-fed chain previews just like a
                // file-fed one.
                if (string.Equals(n.Title, "Image.LoadUrl", StringComparison.OrdinalIgnoreCase))
                {
                    string u = StripQuotes(n.Attributes.GetValueOrDefault("Url", string.Empty));
                    if (string.IsNullOrEmpty(u))
                        return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(no url set)" };
                    return new PreviewSnapshot { Kind = PreviewKind.Image, Source = u, Declared = PreviewSource.UpstreamImage };
                }

                // Walk through the canonical Image input — same priority order
                // the canvas uses for its passthrough preview. Bail when the
                // chain terminates in a non-Image source we can't decode at
                // design time (Video.Load, mask shape generator, Color.Constant,
                // etc.).
                Socket? upSock = null;
                foreach (var name in _preferredImageInputNames)
                {
                    upSock = n.Sockets.FirstOrDefault(s =>
                        s.Type == SocketType.Input &&
                        s.DataType == SocketDataType.Image &&
                        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (upSock is not null) break;
                }
                if (upSock is null)
                    return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(no decodable image upstream)" };

                var upLink = ctx.FindLink(n.Id, upSock.Id);
                if (upLink is null)
                    return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(chain broken)" };
                nextNodeId = upLink.FromNodeId;
            }
            return new PreviewSnapshot { Kind = PreviewKind.Error, Declared = PreviewSource.UpstreamImage, Hint = "(upstream chain too deep)" };
        }

        private static EvalResult EvaluateCore(Graph graph, LayerResolution layerResolution, WidgetRect widgetRect)
        {
            var result = new EvalResult();
            // Per-evaluation memoization. Mirrors compositor.js's per-render
            // Evaluator cache so the C# evaluator and the JS compositor agree on
            // diamond-graphs (a node feeding two downstream branches is computed
            // once, not twice).
            var memo = new Dictionary<string, ImageMetadata?>();
            var ctx  = new EvalContext(graph);
            var sink = graph.Nodes.FirstOrDefault(DisplaySinkNode.Is);
            if (sink is null) return result;

            var imageSocket = sink.Sockets.FirstOrDefault(s => s.Type == SocketType.Input && s.DataType == SocketDataType.Image);
            var visiting = new HashSet<string>();
            if (imageSocket is not null)
            {
                var upstreamLink = ctx.FindLink(sink.Id, imageSocket.Id);
                if (upstreamLink is not null)
                {
                    result.DisplayConnected = true;
                    result.Display = EvalImageMemoized(ctx, upstreamLink.FromNodeId, upstreamLink.FromSocketId, layerResolution, widgetRect, visiting, result.VisitedNodeIds, memo);
                }
            }

            // Visual.Complete sink: evaluate (and walk into VisitedNodeIds) any
            // upstream graph that ends at the Complete sink, mirroring compositor.js's
            // evalAnyInputOf(Complete) probe. Result isn't surfaced as ImageMetadata
            // (Complete is just a flow signal), but the visited-node bookkeeping must
            // include it so parity tests don't drift between sides.
            //
            // ★ V14 — this walk's ONLY product is now VisitedNodeIds. It used to also set
            // an EvalResult.CompleteConnected flag; that flag had no reader anywhere and
            // was deleted, the walk was not. Two things depend on the walk surviving:
            // WidgetNodeCoverageTests exempts Visual.Complete from needing a dispatch arm
            // precisely BECAUSE this sink lookup walks it, and the walk is what keeps the
            // C# mirror from drifting from compositor.js. Deleting it would silently drop
            // every node upstream of Complete out of VisitedNodeIds — a coverage hole that
            // reads as a passing test. If a caller ever needs "is Complete wired?", derive
            // it from the walk rather than resurrecting a write-only flag.
            var completeSink = graph.Nodes.FirstOrDefault(n =>
                string.Equals(n.Title, "Visual.Complete", System.StringComparison.OrdinalIgnoreCase));
            if (completeSink is not null)
            {
                foreach (var inSock in completeSink.Sockets.Where(s => s.Type == SocketType.Input))
                {
                    var link = ctx.FindLink(completeSink.Id, inSock.Id);
                    if (link is null) continue;
                    if (inSock.DataType == SocketDataType.Image)
                    {
                        EvalImageMemoized(ctx, link.FromNodeId, link.FromSocketId, layerResolution, widgetRect, visiting, result.VisitedNodeIds, memo);
                    }
                    else
                    {
                        EvalScalarNode(ctx, link.FromNodeId, link.FromSocketId, layerResolution, visiting, result.VisitedNodeIds);
                    }
                }
            }
            return result;
        }

        // Memoization wrapper around EvalImage.
        private static ImageMetadata? EvalImageMemoized(
            EvalContext ctx, string nodeId, string socketId,
            LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited,
            Dictionary<string, ImageMetadata?> memo)
        {
            string key = $"{nodeId}:{socketId}";
            if (memo.TryGetValue(key, out var cached)) return cached;
            var result = EvalImage(ctx, nodeId, socketId, layerRes, widgetRect, visiting, visited);
            memo[key] = result;
            return result;
        }

        private static ImageMetadata? EvalImage(
            EvalContext ctx, string nodeId, string socketId,
            LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            if (!visiting.Add(nodeId))
                return new ImageMetadata { HasError = true, ErrorMessage = $"cycle detected at node {nodeId}" };
            visited.Add(nodeId);

            // try/finally guarantees nodeId is removed from the visiting set even when a
            // recursive Eval* call in the switch below throws. Without it, an exception
            // would leave nodeId stuck in `visiting`, poisoning cycle detection on every
            // subsequent evaluation (matches the EvalScalarNode / EvalVectorNode pattern).
            try
            {
            var node = ctx.FindNode(nodeId);
            if (node is null)
                return new ImageMetadata { HasError = true, ErrorMessage = $"unknown node id {nodeId}" };

            ImageMetadata? r = node.Title switch
            {
                // V7 — Image.Load takes ctx + the walk state because its Path is now a
                // wirable String input, so resolving it can hop upstream through the
                // string walker. Image.LoadUrl keeps its attribute-only Url (V7 scoped
                // the dynamic source to the three LOCAL-FILE loaders; a dynamic remote
                // URL is a different security conversation).
                "Image.Load"        => EvalImageLoad(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.LoadUrl"     => EvalImageLoadUrl(node, widgetRect),
                "Image.Scale"       => EvalImageScale(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Transform"   => EvalImageTransform(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.ColorAdjust" => EvalImageColorAdjust(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Blur"        => EvalImageBlur(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Gaussian"    => EvalImageGaussian(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Mosaic"      => EvalImageMosaic(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Shadow"      => EvalImageShadow(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Glow"        => EvalImageGlow(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Distort"     => EvalImageDistort(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Mask"        => EvalImageMask(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Crop"        => EvalImageCrop(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Tile"        => EvalImageTile(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Blend"       => EvalImageBlend(ctx, node, layerRes, widgetRect, visiting, visited),
                "Image.Combine"     => EvalImageCombine(ctx, node, layerRes, widgetRect, visiting, visited),
                // Procedural mask shape generators. The C# mirror is a
                // metadata walker (no real canvas), so each kernel just stamps a
                // layer-sized ImageMetadata so downstream Image.Mask + Display
                // dimension propagation works during graph-traversal tests.
                "Mask.Rectangle"      => EvalMaskShapeStub(layerRes, "Mask.Rectangle"),
                "Mask.Circle"         => EvalMaskShapeStub(layerRes, "Mask.Circle"),
                "Mask.Ellipse"        => EvalMaskShapeStub(layerRes, "Mask.Ellipse"),
                "Mask.LinearGradient" => EvalMaskShapeStub(layerRes, "Mask.LinearGradient"),
                "Mask.RadialGradient" => EvalMaskShapeStub(layerRes, "Mask.RadialGradient"),
                "Mask.Vignette"       => EvalMaskShapeStub(layerRes, "Mask.Vignette"),
                // Vertex-list and parameterised shape generators.
                "Mask.Polygon"        => EvalMaskShapeStub(layerRes, "Mask.Polygon"),
                "Mask.Bezier"         => EvalMaskShapeStub(layerRes, "Mask.Bezier"),
                "Mask.Star"           => EvalMaskShapeStub(layerRes, "Mask.Star"),
                // V10 — Image.Solid. NOT a Mask.* stub: the masks emit at the LAYER
                // resolution from attribute-only geometry, while this one is a
                // WIDGET-FRAME-space generator whose four geometry pins are wirable, so
                // its extent is computed rather than stamped. See EvalImageSolid.
                "Image.Solid"         => EvalImageSolid(ctx, node, layerRes, widgetRect, visiting, visited),
                // Viewer passes the upstream image through unchanged.
                "Viewer"            => EvalImagePassthrough(ctx, node, "In",    layerRes, widgetRect, visiting, visited),
                // Result.If — barrier between an Image source and Display. Reads
                // eventData[When] and compares to Equals; passes In through on match,
                // emits null (branch blocked) on mismatch or when the named arg is
                // missing. Logs once per fire when the arg is missing so authoring
                // mistakes (mis-typed When attr) surface in GlobalLogger.
                "Result.If"         => EvalResultIf(ctx, node, layerRes, widgetRect, visiting, visited),

                // Design-time stubs for templates registered in
                // WidgetNodeRegistry that previously hit the HasError default.
                // The graph-walk needs to *propagate* dimensions through these
                // nodes during design-time tests; the actual pixel render happens
                // browser-side in compositor.js. Each stub stamps a sensible
                // size so downstream Display / dim-propagation tests don't
                // fail on legitimate templates. Source is empty (the runtime
                // resolves real bytes; design time is a metadata walker).

                // Color.Constant is a typed Color producer (its
                // template output socket is SocketDataType.Color). Previously
                // this evaluator returned a 1×1 ImageMetadata stub so a wire
                // that landed on an Image-shaped consumer wouldn't error the
                // graph traversal — but that papered over the real bug: the
                // typed-pin contract was violated by a Color→Image wire and
                // the laundered 1×1 source would silently confuse any
                // downstream sizing math (Image.Mask, Image.Blend).
                //
                // The compositor.js side never had this stub (Color.Constant
                // returns a {r,g,b,a} record; an Image consumer hits the
                // unsupported-shape branch). Surface a hard error here so
                // tests + the canvas preview both call out the violation
                // instead of silently rendering a degenerate 1×1 image.
                "Color.Constant"      => new ImageMetadata
                {
                    HasError     = true,
                    ErrorMessage = "Color.Constant wired into an Image consumer — wire it through a Color→Image converter (or a Text.Render's Color input) instead.",
                    Kernel       = "Color.Constant",
                },

                // Visual.OnStartup / Visual.OnTrigger — trigger entry nodes
                // (no image output). When reached as an Image upstream (rare —
                // user pulled a wire from one), return widget-sized passthrough
                // so dim propagation continues without erroring the graph.
                "Visual.OnStartup"    => new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = "Visual.OnStartup" },
                "Visual.OnTrigger"    => new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = "Visual.OnTrigger" },

                // Caption.LiveCaption / Text.Render — render text to image at
                // runtime. Design-time stub: widget-rect sized, no Source (the
                // browser renders into a canvas at trigger time).
                "Caption.LiveCaption" => new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = "Caption.LiveCaption" },
                "Text.Render"         => new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = "Text.Render" },

                // Text.Translate — text→text node. Pass through any wired Image
                // input (some authors wire an upstream image alongside text);
                // otherwise stamp widget-rect dims so dim-propagation continues.
                "Text.Translate"      => EvalImageOrStub(ctx, node, "In", layerRes, widgetRect, visiting, visited, "Text.Translate"),

                // Video.Load — outputs a video stream. Design-time stub returns
                // widget-rect dims with the Source attribute so authoring sees
                // the path even though decode happens browser-side.
                // V7 — Path resolves through the wired-socket-wins resolver plus the
                // wired-must-be-relative guard, same as Image.Load. Kept as an inline switch
                // arm (it was never a method) so the diff stays where the behaviour is.
                "Video.Load"          => new ImageMetadata
                {
                    Source = ResolveMediaPathInput(ctx, node, layerRes, visiting, visited, out _, out _),
                    Width  = widgetRect.Width,
                    Height = widgetRect.Height,
                    Kernel = "Video.Load",
                },

                // Audio.Load / Audio.Play — audio nodes have no image output.
                // The graph-walker only reaches them if the user wired audio
                // into a non-audio image-consumer; per the file-header "no
                // silent passthrough" rule, surface this as a loud
                // HasError so the author sees the type mismatch at design time
                // instead of an invisible 0×0 stub that the JS runtime would
                // also drop on the floor.
                "Audio.Load"          => new ImageMetadata
                {
                    HasError     = true,
                    ErrorMessage = $"Audio.Load reached via Image walker (node {node.Id}) — wire Audio output into an Audio.Play sink, not into an Image consumer.",
                    Kernel       = "Audio.Load",
                },
                "Audio.Play"          => new ImageMetadata
                {
                    HasError     = true,
                    ErrorMessage = $"Audio.Play is a sink, not an image source (node {node.Id}) — connect Audio.Load → Audio.Play directly without an Image path.",
                    Kernel       = "Audio.Play",
                },

                // WebOverlay.Custom — DOM-overlay sink (Path B). It has only String
                // inputs and NO output, so the image walker can never legitimately land
                // on it (nothing wires FROM it). This case exists purely so a mis-wire
                // surfaces a clear message instead of the generic "unsupported node"
                // fallthrough. The real runtime is compositor.js (evalWebOverlay); the
                // design-time C# mirror renders nothing (no browser), matching how the
                // Audio.Play side-effect sink is not visited by EvaluateCore. The title is
                // spelled as a string literal (not WebOverlaySinkNode.Title) so
                // WidgetNodeCoverageTests' source scan counts it as covered — same as the
                // sibling "Audio.Play" / "Video.Load" arms.
                "WebOverlay.Custom" => new ImageMetadata
                {
                    HasError     = true,
                    ErrorMessage = $"WebOverlay.Custom is a DOM-overlay sink, not an image source (node {node.Id}) — it renders its own HTML/CSS over the canvas and has no Image output to wire downstream.",
                    Kernel       = "WebOverlay.Custom",
                },

                // V15 — Player.Embed, the second DOM-overlay sink. Same shape and the same
                // reasoning as WebOverlay.Custom above: one String input, no output, so the
                // image walker can only land here on a mis-wire, and the message says so
                // rather than falling through to the generic "unsupported node".
                //
                // Stronger than the sibling case, though: this one mounts a CROSS-ORIGIN
                // iframe. A browser cannot read a cross-origin frame's pixels back at all,
                // so "give it an Image output later" is not deferred work — it is
                // impossible, and the error text says so instead of implying a gap.
                // The title is a quoted string literal (not PlayerEmbedSinkNode.Title) so
                // WidgetNodeCoverageTests' source scan counts it as covered — a const
                // pattern is invisible to that regex.
                "Player.Embed" => new ImageMetadata
                {
                    HasError     = true,
                    ErrorMessage = $"Player.Embed is a DOM-overlay sink, not an image source (node {node.Id}) — it mounts a cross-origin iframe over the whole widget rect, whose pixels a browser cannot read back, so it has no Image output to wire downstream.",
                    Kernel       = "Player.Embed",
                },

                // Particles.Emit and WebSource are
                // registered in WidgetNodeRegistry (Image / Inputs categories)
                // but the runtime kernels live in compositor.js. Stamp
                // widget-rect dimensions so design-time dim propagation
                // through Image.* downstream consumers doesn't error on a
                // legitimate template that simply has no C# evaluator yet.
                // Mirrors the Color.Constant / Caption.LiveCaption stubs above.
                "Particles.Emit"      => new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = "Particles.Emit" },
                "WebSource"           => new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = "WebSource" },

                _                   => new ImageMetadata { HasError = true, ErrorMessage = $"unsupported node '{node.Title}'" },
            };

            return r;
            }
            finally { visiting.Remove(nodeId); }
        }

        /// <summary>
        /// Image.Load width/height divergence between C# and JS.
        ///
        /// The C# design-time mirror has no real bitmap to inspect, so it returns the
        /// widget-rect dimensions as the "what the layout will reserve for this image"
        /// value. The compositor.js runtime, by contrast, returns the intrinsic
        /// <c>image.naturalWidth</c> / <c>image.naturalHeight</c> at render time once the
        /// real bitmap has decoded. Two runtimes, two contracts, and the values cannot
        /// be reconciled at design-time without actually loading the bitmap from disk
        /// (which the design-time mirror deliberately avoids — it's a metadata walker,
        /// not a renderer). An earlier attempt aligned by returning <c>(0, 0)</c> as an
        /// "intrinsic-pending" sentinel and reverted because the pre-existing
        /// <c>NodeEvaluationTests.Image_Load_Then_Display_Resolves_Source_And_WidgetSize</c>
        /// (and the cascade of Image.Scale tests it anchors) pins the widget-rect
        /// behavior as the C# contract. Closure path: parameterize the test contract
        /// — introduce an <c>EvaluatorOptions { ImageLoadDimsSource = WidgetRect | Intrinsic }</c>
        /// so design-time tests can opt into "intrinsic-pending" semantics while the
        /// existing tests keep their widget-rect assertions. Until that lands, this
        /// method intentionally returns widget-rect dims and the divergence is a known
        /// design-time vs. runtime gap (see also <see cref="EvalImageLoadUrl"/>).
        /// </summary>
        /// <remarks>
        /// V7 — Path is resolved through <see cref="ResolveMediaPathInput"/>: a wired
        /// String input wins, the (quote-stripped) attribute is the fallback, and a WIRED
        /// value must be a relative path. That is the contract compositor.js's
        /// <c>_evalMediaPathSocket</c> implements on the browser side, and the two have to
        /// agree or the design-time mirror would report a different file than OBS renders.
        /// An UNWIRED node resolves exactly as it did before V7, byte for byte — which is
        /// what keeps every saved <c>.phxlayer</c> unchanged.
        /// </remarks>
        private static ImageMetadata EvalImageLoad(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            string path = ResolveMediaPathInput(ctx, node, layerRes, visiting, visited, out _, out _);
            return new ImageMetadata
            {
                Source = path,
                Width  = widgetRect.Width,
                Height = widgetRect.Height,
                Kernel = "Image.Load",
            };
        }

        private static ImageMetadata EvalImageLoadUrl(Node node, WidgetRect widgetRect)
        {
            // Match compositor.js, which routes the URL through the Hub's
            // /asset/url?u= proxy endpoint instead of loading the bare URL directly.
            // Tests that assert on Source compare to the JS-shaped value, and a
            // graph-walk verifying "what URL will the OBS browser actually fetch"
            // sees the wrapped path here too.
            string url = StripQuotes(node.Attributes.GetValueOrDefault("Url", ""));
            string source = string.IsNullOrEmpty(url)
                ? string.Empty
                : $"/asset/url?u={System.Uri.EscapeDataString(url)}";
            return new ImageMetadata
            {
                Source = source,
                Width  = widgetRect.Width,
                Height = widgetRect.Height,
                Kernel = "Image.LoadUrl",
            };
        }

        // Common stub for Mask.* shape generators. Real rendering is
        // browser-side in compositor.js (Canvas2D); the C# mirror just stamps the
        // output dims so downstream Image.Mask / Display see a valid layer-sized
        // mask source. No params are validated here — graph-traversal tests don't
        // exercise pixel output, and the JS kernel uses safe attribute fallbacks.
        private static ImageMetadata EvalMaskShapeStub(LayerResolution layerRes, string kernel)
        {
            return new ImageMetadata
            {
                Width  = layerRes.Width,
                Height = layerRes.Height,
                Kernel = kernel,
            };
        }

        /// <summary>
        /// V10 — <c>Image.Solid</c>: the design-time mirror of compositor.js
        /// <c>evalImageSolid</c>. A colour-filled rectangle expressed as 0..1 fractions of the
        /// WIDGET FRAME, with all four geometry pins wirable.
        ///
        /// <para><b>Why this is not a Mask.* stub.</b> The mask generators emit at the LAYER
        /// resolution from attribute-only geometry, so a constant stamp is a faithful mirror of
        /// them. This node's extent is COMPUTED from its geometry, and the extent is the part a
        /// downstream consumer reasons about (<c>Image.Blend</c> / <c>Image.Combine</c> compose
        /// into the union extent and centre-align), so stamping a constant would make the
        /// design-time walk disagree with the browser for exactly the graphs this node exists
        /// for.</para>
        ///
        /// <para><b>The render contract, mirrored.</b> Compose in content-extent space centred
        /// on the widget centre; crop ONLY at <c>Display</c>. So the extent is the frame's
        /// half-size about the frame centre, GROWN to contain an overhanging rectangle and never
        /// shrunk below the frame:
        /// <c>half = max(0.5, |p0 - 0.5|, |p1 - 0.5|)</c> per axis, extent <c>= 2 * half *
        /// frame</c>, capped at 8x. A rectangle inside 0..1 — every normal bar — therefore
        /// reports exactly the widget rect, which is what keeps this consistent with the
        /// <c>Text.Render</c> / <c>Image.Load</c> stubs beside it.</para>
        ///
        /// <para><b>Known mirror divergence, stated rather than hidden:</b> the geometry
        /// attributes are read as static literals here, while the browser reads them through
        /// <c>attrAnimated</c>. The two therefore agree exactly for an un-keyframed node (the
        /// overwhelming majority, and every channel-driven bar — a live Scalar is wired, not
        /// keyframed) and can differ on the EXTENT of a keyframed overhang. That is the standing
        /// behaviour of every kernel in this walker, not something new here: the walker is a
        /// metadata mirror, and the only keyframe sampling it does is the colour-swatch preview.
        /// Do not "fix" it locally — it would put this one kernel on a different clock from its
        /// neighbours.</para>
        /// </summary>
        private static ImageMetadata EvalImageSolid(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            double fw = widgetRect.Width  > 0 ? widgetRect.Width  : layerRes.Width;
            double fh = widgetRect.Height > 0 ? widgetRect.Height : layerRes.Height;
            if (fw <= 0) fw = 1;
            if (fh <= 0) fh = 1;

            double x = ResolveScalarOrAttr(ctx, node, "X",      "X",      0, layerRes, visiting, visited);
            double y = ResolveScalarOrAttr(ctx, node, "Y",      "Y",      0, layerRes, visiting, visited);
            double w = ResolveScalarOrAttr(ctx, node, "Width",  "Width",  1, layerRes, visiting, visited);
            double h = ResolveScalarOrAttr(ctx, node, "Height", "Height", 1, layerRes, visiting, visited);

            // NaN / infinity guard, mirroring the browser's `fin` helper. A non-finite geometry
            // value would otherwise propagate into the extent and out through Width / Height as
            // a nonsense int cast.
            if (!double.IsFinite(x)) x = 0;
            if (!double.IsFinite(y)) y = 0;
            if (!double.IsFinite(w)) w = 1;
            if (!double.IsFinite(h)) h = 1;

            // Ordered span so a negative Width/Height describes the same rectangle rather than
            // an inverted extent (same rule as the browser).
            double x0 = System.Math.Min(x, x + w), x1 = System.Math.Max(x, x + w);
            double y0 = System.Math.Min(y, y + h), y1 = System.Math.Max(y, y + h);

            double halfX = System.Math.Max(0.5, System.Math.Max(System.Math.Abs(x0 - 0.5), System.Math.Abs(x1 - 0.5)));
            double halfY = System.Math.Max(0.5, System.Math.Max(System.Math.Abs(y0 - 0.5), System.Math.Abs(y1 - 0.5)));

            int cw = System.Math.Max(1, (int)System.Math.Round(System.Math.Min(8, 2 * halfX) * fw));
            int ch = System.Math.Max(1, (int)System.Math.Round(System.Math.Min(8, 2 * halfY) * fh));

            var meta = new ImageMetadata
            {
                Width  = cw,
                Height = ch,
                Kernel = "Image.Solid",
            };
            // The resolved fill colour is reported as a kernel attribute rather than as a
            // Source: there is no file behind it, and Source is what tests read as "which asset
            // will OBS fetch". The node-body swatch reads the same literal through
            // PreviewSource.OwnColor — the SECOND template to declare it, after Color.Constant
            // opted in with V11, so the branch was already live through the graph walk and this
            // node joins it rather than opening it.
            meta.KernelAttributes["Color"] = StripQuotes(node.Attributes.GetValueOrDefault("Color", "\"#ffffff\""));
            return meta;
        }

        private static ImageMetadata? EvalImageScale(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            // Find the image input.
            var imgSocket = node.Sockets.FirstOrDefault(s => s.Name == "In" && s.Type == SocketType.Input);
            if (imgSocket is null) return new ImageMetadata { HasError = true, ErrorMessage = "Image.Scale missing 'In' socket" };

            var imgLink = ctx.FindLink(node.Id, imgSocket.Id);
            if (imgLink is null) return new ImageMetadata { HasError = true, ErrorMessage = "Image.Scale 'In' socket not connected" };

            var upstream = EvalImage(ctx, imgLink.FromNodeId, imgLink.FromSocketId, layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            // Factor: scalar input or attribute fallback.
            // JS / C# parity. compositor.js falls back silently to the Factor
            // attribute when the Factor socket's Math chain returns null (cycle,
            // missing upstream), surfacing the error via node._errorGlyph rather
            // than blocking the render. C# previously hard-errored here, so a graph
            // that rendered fine in OBS would show a HasError stub at design time.
            // Mirror the JS behavior: warn via GlobalLogger and fall through to the
            // attribute value so the design-time preview stays in sync with runtime.
            var factorSock = node.Sockets.FirstOrDefault(s => s.Name == "Factor" && s.Type == SocketType.Input);
            bool factorWired = factorSock is not null && ctx.FindLink(node.Id, factorSock.Id) is not null;
            double? wired = factorWired
                ? ResolveScalarSocket(ctx, node, "Factor", layerRes, visiting, visited)
                : null;
            if (factorWired && wired is null)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    $"Image.Scale (node {node.Id}): Factor socket wired to an unresolved Math chain — falling back to Factor attribute (mirrors compositor.js).",
                    "NodeEvaluator",
                    Phoenix.Controls.Shared.Models.LogLevel.System);
            }
            double factor = wired ?? ParseDouble(node.Attributes.GetValueOrDefault("Factor", "1"));
            // Clamp Factor to a strictly positive minimum so an
            // Image.Scale with Factor=0 (or negative, which round() would
            // also collapse to 0) doesn't emit a 0×0 metadata that downstream
            // Display / dim-propagation tests then interpret as "no image".
            // 0.001 is the lower bound; the final width/height also floors
            // at 1 so a small source times a tiny factor doesn't round to 0
            // and re-introduce the same problem.
            if (factor < 0.001) factor = 0.001;
            int scaledW = System.Math.Max(1, (int)System.Math.Round(upstream.Width  * factor));
            int scaledH = System.Math.Max(1, (int)System.Math.Round(upstream.Height * factor));
            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = scaledW,
                Height = scaledH,
                Kernel = "Image.Scale",
            };
        }

        // layerRes threaded through so Math.Resolution can return the layer's
        // dimensions when reached via an upstream scalar walk.
        private static double? ResolveScalarSocket(EvalContext ctx, Node node, string socketName,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            Socket? sock = null;
            foreach (var s in node.Sockets) { if (s.Type == SocketType.Input && s.Name == socketName) { sock = s; break; } }
            if (sock is null) return null;
            var link = ctx.FindLink(node.Id, sock.Id);
            if (link is null) return null;
            return EvalScalarNode(ctx, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
        }

        private static double? EvalScalarNode(EvalContext ctx, string nodeId,
            string? fromSocketId,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            // Per-call memo mirroring EvalImageMemoized — a pure Math chain feeding
            // several downstream sockets is walked once per evaluation instead of
            // once per consumer (diamonds otherwise degrade toward O(2^depth)).
            // Keyed on the producing output socket too because Vector*.Split
            // resolves a different component per output. Checked before the
            // cycle-guard but populated only after a node completes, so a
            // cycle-bail still returns null without poisoning the memo.
            string memoKey = fromSocketId is null ? nodeId : $"{nodeId}:{fromSocketId}";
            if (ctx.ScalarMemo.TryGetValue(memoKey, out var cached)) return cached;
            if (!visiting.Add(nodeId)) return null; // cycle — bail
            visited.Add(nodeId);
            try
            {
                double? result = Core();
                ctx.ScalarMemo[memoKey] = result;
                return result;
            }
            finally { visiting.Remove(nodeId); }

            double? Core()
            {
                var src = ctx.FindNode(nodeId);
                if (src is null) return null;

                switch (src.Title)
                {
                    case "Scalar.Constant":
                        return ParseDouble(src.Attributes.GetValueOrDefault("Value", "0"));
                    case "Math.Add":   return Binary(ctx, src, "A", "B", layerRes, visiting, visited, (a, b) => a + b);
                    case "Math.Sub":   return Binary(ctx, src, "A", "B", layerRes, visiting, visited, (a, b) => a - b);
                    case "Math.Mul":   return Binary(ctx, src, "A", "B", layerRes, visiting, visited, (a, b) => a * b);
                    // Match compositor.js EXACTLY. The browser runtime
                    // (the source of truth for what the streamer sees) does
                    // `b === 0 ? 0 : a / b` (evalMathBinary). This C# design-time
                    // mirror previously returned NaN and the comment wrongly claimed
                    // JS emits Infinity/NaN — it does not. Returning 0 keeps the
                    // design-time preview identical to the OBS render.
                    case "Math.Div":   return Binary(ctx, src, "A", "B", layerRes, visiting, visited, (a, b) => b == 0 ? 0 : a / b);
                    case "Math.Lerp":
                    {
                        double? a = ResolveScalarSocket(ctx, src, "A", layerRes, visiting, visited);
                        double? b = ResolveScalarSocket(ctx, src, "B", layerRes, visiting, visited);
                        double? t = ResolveScalarSocket(ctx, src, "T", layerRes, visiting, visited);
                        if (a is null || b is null || t is null) return null;
                        return a.Value + (b.Value - a.Value) * t.Value;
                    }
                    case "Math.Clamp":
                    {
                        double? v   = ResolveScalarSocket(ctx, src, "V",   layerRes, visiting, visited);
                        double? mn  = ResolveScalarSocket(ctx, src, "Min", layerRes, visiting, visited);
                        double? mx  = ResolveScalarSocket(ctx, src, "Max", layerRes, visiting, visited);
                        if (v is null || mn is null || mx is null) return null;
                        return System.Math.Clamp(v.Value, mn.Value, mx.Value);
                    }
                    case "Math.Resolution":
                        // The C# eval mirror only walks scalar/image graphs;
                        // Vector outputs aren't consumed here, so collapse the Size vector
                        // to its width component (most common scaling math: resolution.x / 2).
                        return (double)layerRes.Width;

                    // ── Numeric Math nodes ──────────────────────────────────
                    // Each reads its scalar input(s) wired-wins-over-attr via
                    // ResolveScalarOrAttr (the same helper Image.Scale's Factor /
                    // Image.Blur's Radius use), then applies the spec's C# semantics.
                    // Mirrors compositor.js evalMathBinary / evalMathClamp.
                    case "Math.Mod":
                    {
                        double a = ResolveScalarOrAttr(ctx, src, "A", "A", 0, layerRes, visiting, visited);
                        double b = ResolveScalarOrAttr(ctx, src, "B", "B", 1, layerRes, visiting, visited);
                        return b == 0 ? 0 : a - b * System.Math.Floor(a / b);
                    }
                    case "Math.Pow":
                    {
                        double @base = ResolveScalarOrAttr(ctx, src, "Base", "Base", 1, layerRes, visiting, visited);
                        double exp   = ResolveScalarOrAttr(ctx, src, "Exp",  "Exp",  2, layerRes, visiting, visited);
                        return System.Math.Pow(@base, exp);
                    }
                    case "Math.Min":
                    {
                        double a = ResolveScalarOrAttr(ctx, src, "A", "A", 0, layerRes, visiting, visited);
                        double b = ResolveScalarOrAttr(ctx, src, "B", "B", 0, layerRes, visiting, visited);
                        return System.Math.Min(a, b);
                    }
                    case "Math.Max":
                    {
                        double a = ResolveScalarOrAttr(ctx, src, "A", "A", 0, layerRes, visiting, visited);
                        double b = ResolveScalarOrAttr(ctx, src, "B", "B", 0, layerRes, visiting, visited);
                        return System.Math.Max(a, b);
                    }
                    case "Math.Abs":
                        return System.Math.Abs(ResolveScalarOrAttr(ctx, src, "V", "V", 0, layerRes, visiting, visited));
                    case "Math.Sqrt":
                    {
                        double v = ResolveScalarOrAttr(ctx, src, "V", "V", 0, layerRes, visiting, visited);
                        return v < 0 ? 0 : System.Math.Sqrt(v);
                    }
                    case "Math.Floor":
                        return System.Math.Floor(ResolveScalarOrAttr(ctx, src, "V", "V", 0, layerRes, visiting, visited));
                    case "Math.Ceil":
                        return System.Math.Ceiling(ResolveScalarOrAttr(ctx, src, "V", "V", 0, layerRes, visiting, visited));
                    case "Math.Round":
                        // MidpointRounding.AwayFromZero matches JS Math.round (round-half-up).
                        return System.Math.Round(ResolveScalarOrAttr(ctx, src, "V", "V", 0, layerRes, visiting, visited),
                            MidpointRounding.AwayFromZero);
                    case "Math.Sign":
                        return System.Math.Sign(ResolveScalarOrAttr(ctx, src, "V", "V", 0, layerRes, visiting, visited));
                    case "Math.Negate":
                        return -ResolveScalarOrAttr(ctx, src, "V", "V", 0, layerRes, visiting, visited);
                    case "Math.Sin":
                        return System.Math.Sin(ResolveScalarOrAttr(ctx, src, "Degrees", "Degrees", 0, layerRes, visiting, visited) * System.Math.PI / 180);
                    case "Math.Cos":
                        return System.Math.Cos(ResolveScalarOrAttr(ctx, src, "Degrees", "Degrees", 0, layerRes, visiting, visited) * System.Math.PI / 180);
                    case "Math.Tan":
                        return System.Math.Tan(ResolveScalarOrAttr(ctx, src, "Degrees", "Degrees", 0, layerRes, visiting, visited) * System.Math.PI / 180);
                    case "Math.Remap":
                    {
                        double v      = ResolveScalarOrAttr(ctx, src, "V",      "V",      0, layerRes, visiting, visited);
                        double inMin  = ResolveScalarOrAttr(ctx, src, "InMin",  "InMin",  0, layerRes, visiting, visited);
                        double inMax  = ResolveScalarOrAttr(ctx, src, "InMax",  "InMax",  1, layerRes, visiting, visited);
                        double outMin = ResolveScalarOrAttr(ctx, src, "OutMin", "OutMin", 0, layerRes, visiting, visited);
                        double outMax = ResolveScalarOrAttr(ctx, src, "OutMax", "OutMax", 1, layerRes, visiting, visited);
                        double d = inMax - inMin;
                        double t = d == 0 ? 0 : (v - inMin) / d;
                        return outMin + t * (outMax - outMin);
                    }
                    case "Math.Compare":
                    {
                        double a = ResolveScalarOrAttr(ctx, src, "A", "A", 0, layerRes, visiting, visited);
                        double b = ResolveScalarOrAttr(ctx, src, "B", "B", 0, layerRes, visiting, visited);
                        string mode = StripQuotes(src.Attributes.GetValueOrDefault("Mode", "GreaterThan"));
                        const double eps = 1e-6;
                        bool ok = mode switch
                        {
                            "GreaterThan"    => a > b,
                            "LessThan"       => a < b,
                            "GreaterOrEqual" => a >= b,
                            "LessOrEqual"    => a <= b,
                            "Equal"          => System.Math.Abs(a - b) <= eps,
                            "NotEqual"       => System.Math.Abs(a - b) > eps,
                            _                => a > b,
                        };
                        return ok ? 1.0 : 0.0;
                    }

                    // ── Time / animation nodes (design-time t = 0) ──────────
                    // The C# mirror has no clock, so timeMs/1000 collapses to t = 0.
                    // The JS side reads triggerContext.timeMs; here every time-driven
                    // term is evaluated at the origin.
                    case "Time.Elapsed":
                        return 0; // timeMs/1000 with timeMs = 0 at design time.
                    case "Time.Oscillator":
                    {
                        double freq = ResolveScalarOrAttr(ctx, src, "Frequency", "Frequency", 1, layerRes, visiting, visited);
                        double amp  = ResolveScalarOrAttr(ctx, src, "Amplitude", "Amplitude", 1, layerRes, visiting, visited);
                        double ph   = ResolveScalarOrAttr(ctx, src, "Phase",     "Phase",     0, layerRes, visiting, visited);
                        double off  = ResolveScalarOrAttr(ctx, src, "Offset",    "Offset",    0, layerRes, visiting, visited);
                        const double t = 0; // timeMs/1000 at design time.
                        return off + amp * System.Math.Sin(2 * System.Math.PI * (freq * t + ph));
                    }
                    case "Time.Sawtooth":
                    {
                        // P = max(Period, 1e-6); (t % P) / P with t = 0 -> 0.
                        // Period is resolved (wired-wins) for parity even though the
                        // ramp evaluates to 0 at the design-time origin.
                        double p = System.Math.Max(
                            ResolveScalarOrAttr(ctx, src, "Period", "Period", 1, layerRes, visiting, visited), 1e-6);
                        const double t = 0; // timeMs/1000 at design time.
                        return (t % p) / p;
                    }
                    case "Time.Easing":
                    {
                        // REUSE KeyframeInterpolation.ApplyCurve so the easing formulas
                        // stay single-sourced with the keyframe sampler. Mode maps 1:1
                        // onto KeyframeCurve (Linear/EaseIn/EaseOut/EaseInOut/Step).
                        double tIn = ResolveScalarOrAttr(ctx, src, "T", "T", 0, layerRes, visiting, visited);
                        string mode = StripQuotes(src.Attributes.GetValueOrDefault("Mode", "EaseInOut"));
                        KeyframeCurve curve = mode switch
                        {
                            "Linear"    => KeyframeCurve.Linear,
                            "EaseIn"    => KeyframeCurve.EaseIn,
                            "EaseOut"   => KeyframeCurve.EaseOut,
                            "EaseInOut" => KeyframeCurve.EaseInOut,
                            "Step"      => KeyframeCurve.Step,
                            _           => KeyframeCurve.EaseInOut,
                        };
                        // ApplyCurve clamps t to [0,1] internally (clamp01).
                        return KeyframeInterpolation.ApplyCurve(tIn, curve);
                    }

                    // ── Scalar-output Convert / String readers ──────────────
                    // Convert.StringToNumber reads a String input (wired-wins over the
                    // In attribute); parseFloat with NaN/empty -> 0. String.Length reads
                    // a String input and returns its character count.
                    case "Convert.StringToNumber":
                    {
                        string s = ResolveStringOrAttr(ctx, src, "In", "In", "", layerRes, visiting, visited);
                        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                    }
                    case "String.Length":
                    {
                        string s = ResolveStringOrAttr(ctx, src, "In", "In", "", layerRes, visiting, visited);
                        return s.Length;
                    }

                    // ── Overlay Live Channel readers (design time = no channel) ──
                    // Var.Live's Number pin plus every Scalar pin V4 appended to the
                    // tool readers: the timer trio's Progress / Seconds, the counter's
                    // Value, and the loyalty pair's Rank / Balance. All of them are fed
                    // by a channel patch in the browser, and there is no channel behind
                    // the design-time canvas, so they resolve at the origin — 0 — the
                    // same t=0 convention the Time.* nodes above use.
                    //
                    // 0 and not null: null means "this chain did not resolve" and would
                    // surface as a broken/errored preview for a perfectly valid graph.
                    // 0 and not PreviewText either: that attribute is a formatted string
                    // (a clock face, a five-line board) and parsing it back into a number
                    // would be inventing data on a numeric pin — the precise habit this
                    // rework exists to remove. An author authoring a progress bar
                    // therefore sees it empty on the canvas and full only on the live
                    // overlay; that is the honest trade, and no per-pin design-time mock
                    // attribute is invented to paper over it.
                    case "Var.Live":
                    case "Timer.Remaining":
                    case "Countdown.Remaining":
                    case "Stopwatch.Elapsed":
                    case "Counter.Value":
                    case "Loyalty.Leaderboard":
                    case "Loyalty.Balance":
                    // V10 — the two family readers join the same group, for the same reason
                    // and with the same refusal to invent a number:
                    //   Goal.Progress → Progress / Current / Target. An author authoring a
                    //     goal bar therefore sees it EMPTY on the canvas and full only on the
                    //     live overlay. That is the honest trade; parsing the formatted
                    //     PreviewText line back into a fraction would be exactly the
                    //     invented-data habit this rework removed.
                    //   List.Live     → Number / Count. Count 0 is also what the browser
                    //     reports for an unpublished list, so this one is not even a
                    //     concession — it is the same answer.
                    case "Goal.Progress":
                    case "List.Live":
                        return 0;

                    // Vector*.Split nodes expose per-component scalar outputs (X/Y[/Z[/W]]).
                    // When walked from a downstream scalar consumer, look up which named output
                    // socket the link is using, evaluate the upstream Vector input, then return
                    // the requested component. Mirrors the existing Vector.Split (Vector2)
                    // behaviour and extends to Vector3/Vector4.
                    case "Vector.Split":
                    case "Vector3.Split":
                    case "Vector4.Split":
                    {
                        var outSock = fromSocketId is null
                            ? null
                            : src.Sockets.FirstOrDefault(s => s.Id == fromSocketId && s.Type == SocketType.Output);
                        if (outSock is null) return null;
                        // Resolve the upstream Vector input ("V") to its component values.
                        // Pass the Split's natural width so a scalar wired into V broadcasts
                        // to a vector of the right size (mirrors compositor.js evalVectorNSplit
                        // broadcast — Scalar→VectorN parity).
                        int splitLen = src.Title switch
                        {
                            "Vector.Split"  => 2,
                            "Vector3.Split" => 3,
                            "Vector4.Split" => 4,
                            _               => 4,
                        };
                        var vec = ResolveVectorInputSocket(ctx, src, "V", splitLen, layerRes, visiting, visited);
                        if (vec is null) return null;
                        return outSock.Name switch
                        {
                            "X" => vec.Length > 0 ? vec[0] : (double?)null,
                            "Y" => vec.Length > 1 ? vec[1] : (double?)null,
                            "Z" => vec.Length > 2 ? vec[2] : (double?)null,
                            "W" => vec.Length > 3 ? vec[3] : (double?)null,
                            _   => null,
                        };
                    }

                    // Constants exposing per-component scalar attributes. Allow scalar
                    // walks that grab one component (rare, but consistent with Split). Tests
                    // typically read these via the vector evaluator, but this keeps parity
                    // with the JS side which lets you evaluate any output as the typed value.
                    case "Vector2.Constant":
                    case "Vector3.Constant":
                    case "Vector4.Constant":
                    case "Vector.Rect4":
                        return null; // No scalar output; downstream must use the vector evaluator.
                }
                return null;
            }
        }

        // ── Vector evaluator ────────────────────────────────────────────────────
        //
        // The vector evaluator mirrors the scalar walker: it walks upstream from a
        // node's vector output socket, returning a `double[]` of length 2/3/4. It
        // is intentionally narrow — it covers only these kernels:
        //   • Vector2/3/4.Constant (read X/Y[/Z[/W]] attrs)
        //   • Vector.Rect4         (read X/Y/W/H attrs as a 4-vector)
        //   • Vector3/4.Combine    (read N scalar inputs)
        //   • Math.LerpVector2/3/4 (per-component lerp using the existing Math.Lerp model)
        //   • Math.Resolution      (returns layer width/height as a 2-vector)
        // The Vector2.Combine path stays scalar-only at present; the C# walker
        // doesn't need it as no downstream image kernel pulls a Vector2 from it
        // through code paths we currently mirror. The template is still registered
        // so it serializes correctly in graph files.

        /// <summary>
        /// Public entry point for tests that want to evaluate the vector value flowing
        /// out of a specific node. Returns null when the node is unknown, the upstream
        /// chain has a cycle, or an input is missing.
        /// </summary>
        public static double[]? EvaluateVectorOutput(Graph graph, string nodeId,
            LayerResolution layerResolution)
        {
            var ctx      = new EvalContext(graph);
            var visiting = new HashSet<string>();
            var visited  = new List<string>();
            return EvalVectorNode(ctx, nodeId, layerResolution, visiting, visited);
        }

        // Public entry points mirroring EvaluateVectorOutput for the
        // scalar and string walkers. The Math / Time numeric nodes resolve as
        // scalars; the String / Convert(color)/Message.Read nodes resolve as
        // strings (Color is carried as a "#rrggbbaa" hex string, matching
        // Color.Constant's "Value" attribute convention). Returns null when the
        // node is unknown, the upstream chain has a cycle, or an input is missing.

        /// <summary>
        /// Public entry point for tests that want to evaluate the scalar value flowing
        /// out of a specific node. Returns null when the node is unknown, the upstream
        /// chain has a cycle, or a required input is missing.
        /// </summary>
        public static double? EvaluateScalarOutput(Graph graph, string nodeId,
            LayerResolution layerResolution)
        {
            var ctx      = new EvalContext(graph);
            var visiting = new HashSet<string>();
            var visited  = new List<string>();
            return EvalScalarNode(ctx, nodeId, null, layerResolution, visiting, visited);
        }

        /// <summary>
        /// Public entry point for tests that want to evaluate the string value flowing
        /// out of a specific node. Returns null when the node is unknown, the upstream
        /// chain has a cycle, or a required input is missing. Color producers
        /// (Convert.ColorFromRGBA / Convert.HexToColor) return their normalised
        /// "#rrggbbaa" hex string here.
        /// </summary>
        public static string? EvaluateStringOutput(Graph graph, string nodeId,
            LayerResolution layerResolution)
        {
            var ctx      = new EvalContext(graph);
            var visiting = new HashSet<string>();
            var visited  = new List<string>();
            return EvalStringNode(ctx, nodeId, null, layerResolution, visiting, visited);
        }

        private static double[]? ResolveVectorInputSocket(EvalContext ctx, Node node, string socketName,
            int expectedLength,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            Socket? sock = null;
            foreach (var s in node.Sockets) { if (s.Type == SocketType.Input && s.Name == socketName) { sock = s; break; } }
            if (sock is null) return null;
            var link = ctx.FindLink(node.Id, sock.Id);
            if (link is null) return null;

            // Try the vector evaluator first.
            var vec = EvalVectorNode(ctx, link.FromNodeId, layerRes, visiting, visited);
            if (vec is not null)
            {
                // Narrowing-with-zero-pad: a Vector2 wired into a Vector{3,4} socket is
                // legal per the editor's wildcard rules — pad missing components with 0.
                // Mirrors compositor.js _evalVectorSocket's behaviour.
                if (vec.Length < expectedLength)
                {
                    var padded = new double[expectedLength];
                    Array.Copy(vec, padded, vec.Length);
                    return padded;
                }
                return vec;
            }

            // Scalar→VectorN widening parity with compositor.js.
            // When the upstream isn't a recognised vector kernel (e.g. Scalar.Constant,
            // Math.Add, Math.Lerp on scalar inputs), evaluate it as a scalar and broadcast
            // the value across all components of the expected vector width. Without this,
            // wiring `Scalar.Constant → Math.LerpVector3.A` would silently resolve to null
            // and the design-time mirror would diverge from what OBS actually renders.
            var scalar = EvalScalarNode(ctx, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
            if (scalar is not null)
                return BroadcastScalarToVector(scalar.Value, expectedLength);

            return null;
        }

        // Broadcast helper — mirrors compositor.js's _broadcastToVector{2,3,4}.
        // A scalar fed into a VectorN socket widens to (s, s, …, s).
        private static double[] BroadcastScalarToVector(double s, int n)
        {
            var r = new double[n];
            for (int i = 0; i < n; i++) r[i] = s;
            return r;
        }

        private static double[]? EvalVectorNode(EvalContext ctx, string nodeId,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            // Per-call memo mirroring EvalImageMemoized / EvalScalarNode. Keyed by
            // node id only — the vector walker has no per-output-socket variance.
            // Cycle-bails return null without memoizing so cycle detection holds.
            // Memoized arrays are shared, never mutated: consumers copy before
            // padding (ResolveVectorInputSocket) and otherwise only read components.
            if (ctx.VectorMemo.TryGetValue(nodeId, out var cached)) return cached;
            if (!visiting.Add(nodeId)) return null; // cycle — bail
            visited.Add(nodeId);
            try
            {
                double[]? result = Core();
                ctx.VectorMemo[nodeId] = result;
                return result;
            }
            finally { visiting.Remove(nodeId); }

            double[]? Core()
            {
                var src = ctx.FindNode(nodeId);
                if (src is null) return null;

                switch (src.Title)
                {
                    // ── Vector constants ─────────────────────────────────────
                    case "Vector2.Constant":
                        return new[]
                        {
                            ParseDouble(src.Attributes.GetValueOrDefault("X", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("Y", "0")),
                        };
                    case "Vector3.Constant":
                        return new[]
                        {
                            ParseDouble(src.Attributes.GetValueOrDefault("X", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("Y", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("Z", "0")),
                        };
                    case "Vector4.Constant":
                        return new[]
                        {
                            ParseDouble(src.Attributes.GetValueOrDefault("X", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("Y", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("Z", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("W", "0")),
                        };
                    // Vector.Rect4 is a friendly alias for Vector4.Constant whose
                    // attributes name the four components X/Y/W/H (matches Image.Crop's
                    // Rect socket convention). The output is a 4-vector in [X, Y, W, H] order.
                    case "Vector.Rect4":
                        return new[]
                        {
                            ParseDouble(src.Attributes.GetValueOrDefault("X", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("Y", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("W", "0")),
                            ParseDouble(src.Attributes.GetValueOrDefault("H", "0")),
                        };

                    // ── Vector combiners ────────────────────────────────────
                    case "Vector.Combine":
                    {
                        double? x = ResolveScalarSocket(ctx, src, "X", layerRes, visiting, visited);
                        double? y = ResolveScalarSocket(ctx, src, "Y", layerRes, visiting, visited);
                        if (x is null || y is null) return null;
                        return new[] { x.Value, y.Value };
                    }
                    case "Vector3.Combine":
                    {
                        double? x = ResolveScalarSocket(ctx, src, "X", layerRes, visiting, visited);
                        double? y = ResolveScalarSocket(ctx, src, "Y", layerRes, visiting, visited);
                        double? z = ResolveScalarSocket(ctx, src, "Z", layerRes, visiting, visited);
                        if (x is null || y is null || z is null) return null;
                        return new[] { x.Value, y.Value, z.Value };
                    }
                    case "Vector4.Combine":
                    {
                        double? x = ResolveScalarSocket(ctx, src, "X", layerRes, visiting, visited);
                        double? y = ResolveScalarSocket(ctx, src, "Y", layerRes, visiting, visited);
                        double? z = ResolveScalarSocket(ctx, src, "Z", layerRes, visiting, visited);
                        double? w = ResolveScalarSocket(ctx, src, "W", layerRes, visiting, visited);
                        if (x is null || y is null || z is null || w is null) return null;
                        return new[] { x.Value, y.Value, z.Value, w.Value };
                    }

                    // ── Per-component lerps ─────────────────────────────────
                    // Mirrors the scalar Math.Lerp path: a + (b - a) * t, applied per
                    // component. T is a scalar shared across all components.
                    case "Math.LerpVector2":
                        return LerpVectorN(ctx, src, expectedLength: 2, layerRes, visiting, visited);
                    case "Math.LerpVector3":
                        return LerpVectorN(ctx, src, expectedLength: 3, layerRes, visiting, visited);
                    case "Math.LerpVector4":
                        return LerpVectorN(ctx, src, expectedLength: 4, layerRes, visiting, visited);

                    // ── Layer resolution (vector parity) ────────────────────
                    case "Math.Resolution":
                        return new[] { (double)layerRes.Width, (double)layerRes.Height };
                }
                return null;
            }
        }

        private static double[]? LerpVectorN(EvalContext ctx, Node node, int expectedLength,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            var a = ResolveVectorInputSocket(ctx, node, "A", expectedLength, layerRes, visiting, visited);
            var b = ResolveVectorInputSocket(ctx, node, "B", expectedLength, layerRes, visiting, visited);
            double? t = ResolveScalarSocket(ctx, node, "T", layerRes, visiting, visited);
            if (a is null || b is null || t is null) return null;
            if (a.Length < expectedLength || b.Length < expectedLength) return null;
            var r = new double[expectedLength];
            for (int i = 0; i < expectedLength; i++)
                r[i] = a[i] + (b[i] - a[i]) * t.Value;
            return r;
        }

        private static double? Binary(EvalContext ctx, Node node, string aName, string bName,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited, System.Func<double, double, double> op)
        {
            double? a = ResolveScalarSocket(ctx, node, aName, layerRes, visiting, visited);
            double? b = ResolveScalarSocket(ctx, node, bName, layerRes, visiting, visited);
            if (a is null || b is null) return null;
            return op(a.Value, b.Value);
        }

        // ── String / Convert(color) / Message.Read evaluator ────────────────────
        //
        // The widget evaluator previously had no String walker (see the Result.If
        // attribute-read workaround). This walker mirrors EvalScalarNode: it walks
        // upstream from a node's String/Color output socket and returns the resolved
        // string. Color producers (Convert.ColorFromRGBA / Convert.HexToColor) carry
        // their value as a "#rrggbbaa" hex string — the same shape Color.Constant's
        // "Value" attribute uses — so a single string walker covers both worlds.
        //
        // Cross-walker calls: Convert.NumberToString reads a Scalar input via
        // EvalScalarNode; the scalar walker reciprocally handles Convert.StringToNumber
        // and String.Length by calling back into this walker via ResolveStringOrAttr.
        private static string? EvalStringNode(EvalContext ctx, string nodeId,
            string? fromSocketId,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            // Per-call memo mirroring EvalImageMemoized / EvalScalarNode. Keyed on
            // the producing output socket too — Visual.OnTrigger resolves a
            // different value per output. Cycle-bails return null without
            // memoizing so cycle detection holds.
            string memoKey = fromSocketId is null ? nodeId : $"{nodeId}:{fromSocketId}";
            if (ctx.StringMemo.TryGetValue(memoKey, out var cached)) return cached;
            if (!visiting.Add(nodeId)) return null; // cycle — bail
            visited.Add(nodeId);
            try
            {
                string? result = Core();
                ctx.StringMemo[memoKey] = result;
                return result;
            }
            finally { visiting.Remove(nodeId); }

            string? Core()
            {
                var src = ctx.FindNode(nodeId);
                if (src is null) return null;

                switch (src.Title)
                {
                    // ── String constant ────────────────────────────────────
                    // Pure String producer (no inputs). Mirrors Scalar.Constant /
                    // Color.Constant: read the JSON-string-quoted "Value" attribute
                    // and strip the quotes. Matches compositor.js evalStringConstant
                    // => stripQuotes(attr(node,'Value','')). Empty (escaped "") is the
                    // template default, so an untouched node resolves to "".
                    case "String.Constant":
                        return StripQuotes(src.Attributes.GetValueOrDefault("Value", ""));

                    // ── String nodes ───────────────────────────────────────
                    case "String.Concat":
                    {
                        string a = ResolveStringOrAttr(ctx, src, "A", "A", "", layerRes, visiting, visited);
                        string b = ResolveStringOrAttr(ctx, src, "B", "B", "", layerRes, visiting, visited);
                        return a + b;
                    }
                    case "String.Upper":
                        return ResolveStringOrAttr(ctx, src, "In", "In", "", layerRes, visiting, visited).ToUpperInvariant();
                    case "String.Lower":
                        return ResolveStringOrAttr(ctx, src, "In", "In", "", layerRes, visiting, visited).ToLowerInvariant();
                    case "String.Slice":
                    {
                        string s = ResolveStringOrAttr(ctx, src, "In", "In", "", layerRes, visiting, visited);
                        int len = s.Length;
                        double startD = ResolveScalarOrAttr(ctx, src, "Start", "Start", 0,  layerRes, visiting, visited);
                        double countD = ResolveScalarOrAttr(ctx, src, "Count", "Count", -1, layerRes, visiting, visited);
                        int st = System.Math.Clamp((int)startD, 0, len);
                        int n  = countD < 0 ? len - st : System.Math.Max(0, (int)countD);
                        if (st + n > len) n = len - st;
                        return s.Substring(st, n);
                    }
                    case "String.Replace":
                    {
                        string s    = ResolveStringOrAttr(ctx, src, "In",   "In",   "", layerRes, visiting, visited);
                        string find = ResolveStringOrAttr(ctx, src, "Find", "Find", "", layerRes, visiting, visited);
                        string with = ResolveStringOrAttr(ctx, src, "With", "With", "", layerRes, visiting, visited);
                        if (string.IsNullOrEmpty(find)) return s; // Find=="" -> unchanged.
                        return s.Replace(find, with);
                    }

                    // ── Convert nodes ───────────────────────────────────────
                    case "Convert.NumberToString":
                    {
                        double v = ResolveScalarOrAttr(ctx, src, "V", "V", 0, layerRes, visiting, visited);
                        int decimals = (int)ParseDouble(src.Attributes.GetValueOrDefault("Decimals", "0"));
                        decimals = System.Math.Clamp(decimals, 0, 6);
                        return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
                    }
                    case "Convert.ColorFromRGBA":
                    {
                        int r = ClampByte(ResolveScalarOrAttr(ctx, src, "R", "R", 255, layerRes, visiting, visited));
                        int g = ClampByte(ResolveScalarOrAttr(ctx, src, "G", "G", 255, layerRes, visiting, visited));
                        int b = ClampByte(ResolveScalarOrAttr(ctx, src, "B", "B", 255, layerRes, visiting, visited));
                        int a = ClampByte(ResolveScalarOrAttr(ctx, src, "A", "A", 255, layerRes, visiting, visited));
                        return $"#{r:x2}{g:x2}{b:x2}{a:x2}"; // alpha last.
                    }
                    case "Convert.HexToColor":
                    {
                        string hex = ResolveStringOrAttr(ctx, src, "Hex", "Hex", "#ffffff", layerRes, visiting, visited);
                        return NormalizeHexColor(hex);
                    }

                    // ── Message.Read — the read-out / trigger node ──────────
                    // Pure-data String producer. Design-time returns the MockValue
                    // attribute so the canvas/preview is not blind to the transmitted
                    // string (the JS side reads triggerContext.eventData[Key]).
                    case "Message.Read":
                        return StripQuotes(src.Attributes.GetValueOrDefault("MockValue", ""));

                    // ── V7 — Visual.Arg: one named trigger-payload field, as a String ──
                    //
                    // Mirrors the browser reader arm for arm. The evaluator DOES have
                    // event data when a caller supplies it (the Result.If mirror reads
                    // the same thread-static), so a test can drive the real lookup; the
                    // canvas has none, and then the PreviewText attribute is the
                    // design-time placeholder.
                    //
                    // ★ The browser routes that placeholder through its liveMock helper,
                    // which returns '' unless the page is a design-time surface — so in
                    // production an unsupplied field renders NOTHING. There is no such
                    // gate here because this walker IS a design-time surface: it never
                    // runs in OBS. Do not "align" the two by making the browser show the
                    // placeholder; that is the fake-data-on-stream bug this rework
                    // removed. The distinction from Message.Read above (whose MockValue
                    // does reach air) is the whole reason this node exists.
                    case "Visual.Arg":
                    {
                        // ★ The Key default is "Args1", matching compositor.js's
                        // attr(node, 'Key', 'Args1') — and it has to, because the two
                        // defaults are reached by DIFFERENT populations of layer. Hub serves
                        // .phxlayer files raw and never runs LayerGraphMigrator, so a node
                        // saved without a Key attribute reaches the BROWSER un-migrated and
                        // looks up Args1 there; defaulting to empty here made the same node
                        // preview a placeholder in the editor and render a real value on
                        // stream. Divergent defaults in a mirror are the one class of bug
                        // this mirror exists to make impossible.
                        string argKey = StripQuotes(src.Attributes.GetValueOrDefault("Key", "Args1")).Trim();
                        var argEd = _currentEventData;
                        if (argKey.Length > 0 && argEd is not null
                            && argEd.TryGetValue(argKey, out var argVal) && argVal is not null)
                            return argVal;
                        return StripQuotes(src.Attributes.GetValueOrDefault("PreviewText", ""));
                    }

                    // ── V7 — String.Select: N-way string mapping with a default ──
                    //
                    // First Case row that EXACTLY equals the selector wins; nothing
                    // matched ⇒ Default. Byte-identical to compositor.js
                    // evalStringSelect, and every one of the three rules below is load-
                    // bearing rather than defensive:
                    //
                    //   • Ordinal, case-SENSITIVE compare (matching the Result.If gate).
                    //     Case-insensitive was rejected because JS toLowerCase() and
                    //     .NET OrdinalIgnoreCase disagree on some Unicode input, and the
                    //     browser and this mirror picking different rows would be a
                    //     silent, unreproducible authoring bug.
                    //   • An EMPTY Case is an unconfigured row and is skipped. Otherwise
                    //     an empty selector — the normal state on an onStartup render,
                    //     where no event data exists at all — would match the first
                    //     blank row and emit its Value, so a freshly dropped node would
                    //     appear to have chosen row 1.
                    //   • Default is a real row, not a fallback-of-last-resort: the
                    //     Alerts tool labels an unmapped family generically, so a value
                    //     nobody mapped genuinely arrives.
                    //
                    // The selector reads through ResolveStringOrAttr, so wiring a
                    // Visual.Arg into When wins over the inline attribute and an unwired
                    // node still previews from its own box.
                    case "String.Select":
                    {
                        string selector = ResolveStringOrAttr(
                            ctx, src, "When", "When", "", layerRes, visiting, visited);
                        for (int row = 1; row <= NodeTemplates.StringSelectRows; row++)
                        {
                            string caseText = StripQuotes(
                                src.Attributes.GetValueOrDefault("Case" + row.ToString(CultureInfo.InvariantCulture), ""));
                            if (caseText.Length == 0) continue;
                            if (!string.Equals(caseText, selector, StringComparison.Ordinal)) continue;
                            return StripQuotes(
                                src.Attributes.GetValueOrDefault("Value" + row.ToString(CultureInfo.InvariantCulture), ""));
                        }
                        return StripQuotes(src.Attributes.GetValueOrDefault("Default", ""));
                    }

                    // ── Timer.Remaining — live countdown readout (spec §6) ──
                    // At design time there is no live TimerService and no Overlay Live
                    // Channel behind the canvas, so this mirrors Message.Read's mock
                    // behaviour: each status pin previews what a HEALTHY timer would
                    // report and every remaining String socket previews the PreviewText
                    // attribute. The live values arrive browser-side off the channel's
                    // timer.<root>.* key family (compositor.js evalTimerRemaining) — the
                    // TIMER_UPDATE broadcast this comment used to name was retired with
                    // the channel rework and no longer exists. The output socket is
                    // resolved from fromSocketId exactly like the Visual.OnTrigger case
                    // below.
                    case "Timer.Remaining":
                    // Countdown.Remaining / Stopwatch.Elapsed share Timer.Remaining's
                    // shape — the same output pins reading the same live key family —
                    // so they mirror it identically at design time: the State socket
                    // previews "Running", the Text socket previews PreviewText, and the
                    // three String pins appended for the channel are handled below.
                    case "Countdown.Remaining":
                    case "Stopwatch.Elapsed":
                    {
                        var timerSock = fromSocketId is null
                            ? null
                            : src.Sockets.FirstOrDefault(s => s.Id == fromSocketId && s.Type == SocketType.Output);
                        // Every String status pin, and why each previews what it does.
                        // The governing rule: a preview may only be a value the browser
                        // reader can actually emit for this pin — a mirror that invents
                        // a value production never produces is precisely what let the
                        // last contract break pass unnoticed in the editor.
                        //   State  — "Running": the RUN state, read browser-side as the
                        //            VALUE of timer.<root>.state (Running / Paused /
                        //            Stopped / Ended). A healthy timer previews Running.
                        //   Live   — "Active": the CHANNEL's verdict on that same key
                        //            (Active / Stale / Missing). Design time has no
                        //            channel, so it previews the healthy end of the
                        //            vocabulary, exactly as State does. This pin is what
                        //            makes a frozen "Running" detectable on stream, and
                        //            it exists ONLY on the timer trio: the other tool
                        //            readers have no run state, so their State pin
                        //            carries liveness itself.
                        //   Paused — "false", because a Running timer is not paused.
                        //   Mode   — the timer mode THIS palette entry exists to read.
                        //            Each of the three readers is a per-mode entry (see
                        //            their template comments), so echoing that back is a
                        //            faithful mirror of the author's intent rather than
                        //            invented data; the real mode replaces it browser-side.
                        switch (timerSock?.Name)
                        {
                            case "State":  return "Running";
                            case "Live":   return "Active";
                            case "Paused": return "false";
                            case "Mode":   return DesignTimeTimerMode(src.Title);
                        }
                        return StripQuotes(src.Attributes.GetValueOrDefault("PreviewText", ""));
                    }

                    // ── Clock.Now — live digital wall-clock (browser-autonomous) ──
                    // No live clock at design time (t=0 convention), so preview the
                    // PreviewText placeholder — the browser evalClockNow renders the
                    // real machine time each 1 Hz heartbeat.
                    case "Clock.Now":
                        return StripQuotes(src.Attributes.GetValueOrDefault("PreviewText", ""));

                    // ── Loyalty.Leaderboard / Loyalty.Balance — live points readouts ─
                    // Loyalty-tool Layer 5. Structural twins of Timer.Remaining: there is
                    // no live LoyaltyService and no Overlay Live Channel at design time,
                    // so mirror its mock behaviour — each status pin previews a value the
                    // browser reader can really emit, and every other String socket
                    // previews the PreviewText attribute. The live values arrive
                    // browser-side off the channel's loyalty.leaderboard array
                    // (compositor.js evalLoyaltyLeaderboard / evalLoyaltyBalance); the
                    // LOYALTY_UPDATE broadcast this comment used to name was retired with
                    // the channel rework. The output socket is resolved from fromSocketId
                    // exactly like the Timer.Remaining case above.
                    case "Loyalty.Leaderboard":
                    case "Loyalty.Balance":
                    {
                        var loyaltySock = fromSocketId is null
                            ? null
                            : src.Sockets.FirstOrDefault(s => s.Id == fromSocketId && s.Type == SocketType.Output);
                        string loyaltyPreview = StripQuotes(src.Attributes.GetValueOrDefault("PreviewText", ""));
                        if (loyaltySock?.Name == "State")
                        {
                            // The leaderboard's State vocabulary is Active / Stale /
                            // Missing PLUS "Empty" — a board that IS being published but
                            // holds no rows yet. Empty is not a liveness fact and not a
                            // legacy wart: widgets branch on it to render a "no scores
                            // yet" state, so the mirror has to be able to preview it or
                            // that branch is unreachable on the canvas and the author
                            // cannot see the state they are authoring.
                            //
                            // The mirror's ONLY design-time board is the PreviewText
                            // mock, so it reports the state that mock implies: rows
                            // present → Active, PreviewText cleared → Empty. That also
                            // makes clearing PreviewText the deliberate lever for
                            // previewing the empty branch. Loyalty.Balance keeps plain
                            // "Active": a one-row read either resolves or it doesn't, so
                            // its reader never emits Empty and the mirror must not
                            // either.
                            if (src.Title == "Loyalty.Leaderboard")
                                return loyaltyPreview.Length == 0 ? "Empty" : "Active";
                            return "Active";
                        }
                        // Name is the per-row pin the Index attribute selects on the
                        // leaderboard. It must NOT fall through to PreviewText: that
                        // attribute holds a whole formatted five-line BOARD, so a pin
                        // that has to carry one viewer name would hand its consumer the
                        // entire mock ranking. Empty is the honest design-time answer,
                        // and it is also what the browser yields when Index points past
                        // the end of a live board.
                        if (loyaltySock?.Name == "Name") return string.Empty;
                        return loyaltyPreview;
                    }

                    // ── Counter.Value — live named-counter readout (Counters tool) ─
                    // Structural twin of Loyalty.Balance: there is no live
                    // CountersService at design time, so mirror its mock behaviour —
                    // the "State" socket previews "Active" (the healthy end of its
                    // Active / Stale / Missing vocabulary; this node has no run state,
                    // so State IS its liveness verdict and there is no Live pin) and the
                    // Text socket previews the PreviewText attribute. The live value
                    // arrives browser-side off the channel key counter.<name>.count
                    // (compositor.js evalCounterValue) — the COUNTER_UPDATE broadcast
                    // this comment used to name was retired with the channel rework.
                    case "Counter.Value":
                    {
                        var counterSock = fromSocketId is null
                            ? null
                            : src.Sockets.FirstOrDefault(s => s.Id == fromSocketId && s.Type == SocketType.Output);
                        if (counterSock?.Name == "State") return "Active";
                        return StripQuotes(src.Attributes.GetValueOrDefault("PreviewText", ""));
                    }

                    // ── Var.Live — Overlay Live Channel binding node ─────────
                    // The design-time mirror has NO channel to read (there is no Hub
                    // socket behind the canvas), so it resolves from the node's own
                    // attributes only — it must never manufacture a plausible-looking
                    // value, because the browser reader deliberately renders nothing
                    // for an unpublished key and the two sides have to agree about
                    // what "no data" looks like.
                    //   State — from the Key attribute, the one thing knowable at
                    //           design time: a blank Key can never resolve (the
                    //           subscription is derived from this literal text), so it
                    //           previews Missing, which is exactly what the browser
                    //           will report. A filled Key previews Active, matching how
                    //           the sibling readers preview a healthy feed.
                    //   Text  — PreviewText when the author set one; otherwise an echo
                    //           of the bound key in {brace} form, so a canvas full of
                    //           Var.Live nodes shows WHICH binding sits where instead
                    //           of a row of blank boxes. The braces make it unmistakably
                    //           not live data, and they match the {token} convention the
                    //           Format attributes already use. Nothing to echo (no Key,
                    //           no PreviewText) previews empty.
                    case "Var.Live":
                    {
                        var liveSock = fromSocketId is null
                            ? null
                            : src.Sockets.FirstOrDefault(s => s.Id == fromSocketId && s.Type == SocketType.Output);
                        // Trimmed so a whitespace-only Key reads as unbound and the echo
                        // below shows no padding. Deliberately NOT lower-cased: key
                        // normalisation is the publisher/browser half of the contract and
                        // has to stay single-sourced there — this mirror never subscribes
                        // to anything, it only decides what the canvas shows.
                        string liveKey = StripQuotes(src.Attributes.GetValueOrDefault("Key", "")).Trim();
                        if (liveSock?.Name == "State")
                            return liveKey.Length == 0 ? "Missing" : "Active";
                        string livePreview = StripQuotes(src.Attributes.GetValueOrDefault("PreviewText", ""));
                        if (livePreview.Length > 0) return livePreview;
                        return liveKey.Length == 0 ? string.Empty : "{" + liveKey + "}";
                    }

                    // ── V10 — Goal.Progress: the goal.<kind>.* family reader ────
                    // Design time has no channel behind the canvas, so this resolves from the
                    // node's own attributes only and must never manufacture a plausible
                    // value — the browser deliberately renders NOTHING for an unpublished
                    // goal, and the two sides have to agree about what "no data" looks like.
                    //   State — from the Kind attribute, the one thing knowable here: a blank
                    //           Kind can never resolve (the subscription is derived from this
                    //           literal text, and liveGoalRoot refuses to build a partial
                    //           root from it), so it previews Missing — exactly what the
                    //           browser will report. A filled Kind previews Active, matching
                    //           how every sibling reader previews a healthy feed.
                    //   Label — EMPTY, never PreviewText. That attribute holds a whole
                    //           formatted line ("120 / 250"), so a pin whose contract is one
                    //           short display label would hand its consumer the entire mock.
                    //           Same call the Loyalty readers' Name pin makes, and empty is
                    //           also what the browser yields for an unpublished label.
                    //   Text  — the PreviewText placeholder.
                    case "Goal.Progress":
                    {
                        var goalSock = fromSocketId is null
                            ? null
                            : src.Sockets.FirstOrDefault(s => s.Id == fromSocketId && s.Type == SocketType.Output);
                        if (goalSock?.Name == "State")
                        {
                            // Trimmed only. Deliberately NOT lower-cased: key normalisation is
                            // the publisher/browser half of the contract and stays
                            // single-sourced there — this mirror never subscribes to anything,
                            // it only decides what the canvas shows.
                            string kind = StripQuotes(src.Attributes.GetValueOrDefault("Kind", "")).Trim();
                            return kind.Length == 0 ? "Missing" : "Active";
                        }
                        if (goalSock?.Name == "Label") return string.Empty;
                        return StripQuotes(src.Attributes.GetValueOrDefault("PreviewText", ""));
                    }

                    // ── V10 — List.Live: the channel ARRAY reader ───────────────
                    // Structural twin of Loyalty.Leaderboard, whose State vocabulary it shares
                    // (Active / Stale / EMPTY — never Missing, because a never-published list
                    // and a published-empty one are the same thing to a widget, and widgets
                    // branch on 'Empty' to draw a "nothing yet" card).
                    //   State — a blank Key can never resolve, so 'Empty'. Otherwise the
                    //           mirror's ONLY design-time list is the PreviewText mock, so it
                    //           reports the state that mock implies: rows present → Active,
                    //           PreviewText cleared → Empty. That also makes clearing
                    //           PreviewText the deliberate lever for previewing the empty
                    //           branch — the same lever the leaderboard offers.
                    //   Row / Value — EMPTY. PreviewText holds already-FORMATTED rows, so
                    //           slicing one out would hand Row the output of a template it
                    //           never applied while Value (which needs raw field data the mock
                    //           does not carry) stayed blank. Three honest blanks beat one
                    //           inconsistent mock.
                    //   Text  — the PreviewText placeholder (one mock row per line).
                    case "List.Live":
                    {
                        var listSock = fromSocketId is null
                            ? null
                            : src.Sockets.FirstOrDefault(s => s.Id == fromSocketId && s.Type == SocketType.Output);
                        string listPreview = StripQuotes(src.Attributes.GetValueOrDefault("PreviewText", ""));
                        if (listSock?.Name == "State")
                        {
                            string listKey = StripQuotes(src.Attributes.GetValueOrDefault("Key", "")).Trim();
                            if (listKey.Length == 0) return "Empty";
                            return listPreview.Length == 0 ? "Empty" : "Active";
                        }
                        if (listSock?.Name == "Row" || listSock?.Name == "Value") return string.Empty;
                        return listPreview;
                    }

                    // ── Visual.OnTrigger string-output mirror ───────────────
                    // OnTrigger exposes string outputs (EventData / UserName / Message,
                    // etc.). At design time there is no live event, so mirror the
                    // template defaults / empty strings keyed by the output socket the
                    // downstream link pulls from — so wiring OnTrigger.<x> into a String
                    // consumer no longer yields a hard gap in the design-time walk.
                    // Message.Read.MockValue remains the real preview hook.
                    case "Visual.OnTrigger":
                    {
                        var outSock = fromSocketId is null
                            ? null
                            : src.Sockets.FirstOrDefault(s => s.Id == fromSocketId && s.Type == SocketType.Output);
                        string key = outSock?.Name ?? "";
                        // Honour a same-named attribute default if the template carries
                        // one; otherwise an empty string (no live event at design time).
                        return StripQuotes(src.Attributes.GetValueOrDefault(key, ""));
                    }
                }
                return null;
            }
        }

        // Design-time value for the timer readers' appended Mode pin. Each of the
        // three palette entries exists to read one TimerMode (their template comments
        // say so explicitly: the Countdown reader reads a TimerMode.Countdown timer,
        // the Stopwatch reader a TimerMode.Stopwatch one, and the generic reader the
        // subathon clock it was written for), so echoing that mode back is a mirror of
        // the author's intent, not fabricated data. At runtime the channel's own mode
        // field wins — an author who points the generic reader at a Countdown gets
        // "Countdown" on stream and only the canvas preview says "Subathon".
        // Spelled with quoted string literals to match the surrounding dispatch style
        // (and so the coverage scan keeps seeing these titles as covered).
        private static string DesignTimeTimerMode(string? title) => title switch
        {
            "Countdown.Remaining" => "Countdown",
            "Stopwatch.Elapsed"   => "Stopwatch",
            _                     => "Subathon",
        };

        // String socket resolver with attribute fallback. Honours the
        // "wired socket wins" contract: walk the String link if present (and it
        // resolves), then fall back to the (quote-stripped) attribute. Mirrors
        // ResolveScalarOrAttr for the string world.
        private static string ResolveStringOrAttr(
            EvalContext ctx, Node node, string socketName, string attrKey, string defaultValue,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
            => ResolveStringOrAttr(ctx, node, socketName, attrKey, defaultValue,
                                   layerRes, visiting, visited, out _);

        /// <summary>
        /// The resolver above, additionally reporting PROVENANCE:
        /// <paramref name="fromWire"/> is true iff the returned value came from an UPSTREAM
        /// NODE, and false when it came from the node's own attribute.
        ///
        /// It reports where the value came FROM rather than merely whether a link exists — a
        /// dangling wire that resolved to null falls back to the attribute and is reported as
        /// an ATTRIBUTE value. <see cref="ResolveMediaPathInput"/> depends on exactly that
        /// distinction, and so does compositor.js's <c>_evalQuotedStringSocket</c>, whose
        /// optional <c>provenance</c> sink this <c>out</c> parameter mirrors.
        /// </summary>
        private static string ResolveStringOrAttr(
            EvalContext ctx, Node node, string socketName, string attrKey, string defaultValue,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited, out bool fromWire)
        {
            fromWire = false;
            Socket? sock = null;
            foreach (var s in node.Sockets) { if (s.Type == SocketType.Input && s.Name == socketName) { sock = s; break; } }
            if (sock is not null)
            {
                var link = ctx.FindLink(node.Id, sock.Id);
                if (link is not null)
                {
                    var resolved = EvalStringNode(ctx, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
                    if (resolved is not null) { fromWire = true; return resolved; }
                }
            }
            return StripQuotes(node.Attributes.GetValueOrDefault(attrKey, defaultValue));
        }

        /// <summary>
        /// True for a media path that would bypass the Hub <c>/media/</c> route: a leading
        /// '/', an <c>http(s):</c> URL or a <c>data:</c> URI. The exact twin of
        /// compositor.js's <c>isNonRelativeMediaPath</c> — keep the two in lockstep, because
        /// the browser uses it to decide what to PROXY and both sides use it to decide what a
        /// wired path may be (<see cref="ResolveMediaPathInput"/>).
        /// </summary>
        private static bool IsNonRelativeMediaPath(string p)
            => p.StartsWith("/", StringComparison.Ordinal)
            || p.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// THE media-path input resolver for the three local-file loaders (Image.Load /
        /// Video.Load / Audio.Load) — resolution plus the provenance rule, mirroring
        /// compositor.js's <c>Evaluator._evalMediaPathSocket</c> byte for byte in behaviour so
        /// the design-time preview agrees with what OBS will actually fetch.
        ///
        /// <para>THE RULE, and it is deliberately NOT a blanket refusal:</para>
        /// <list type="bullet">
        /// <item>An ATTRIBUTE value keeps today's behaviour exactly — a leading '/', an
        /// <c>http(s):</c> URL and a <c>data:</c> URI all still pass straight through, because
        /// the author who typed them into the Path box IS the streamer.</item>
        /// <item>A WIRED value must be a RELATIVE path. A wired leading '/', <c>http(s):</c> or
        /// <c>data:</c> string is rejected and resolves to empty, so the loader bails.</item>
        /// </list>
        ///
        /// <para>Why provenance and not a flat rule: V7 made Path wirable and its headline
        /// chain wires Visual.Arg — i.e. the trigger payload, i.e. a chat argument — into it.
        /// Browser-side that is a live exposure (a viewer typing
        /// <c>!sound https://attacker/x.mp3</c> makes the streamer's OBS fetch an
        /// attacker-named URL: home IP disclosed, arbitrary media on air, a <c>data:</c> URL
        /// rendering attacker content inline). This walker never fetches anything, so the
        /// mirror is not itself a security boundary — it exists so the canvas does not show an
        /// author a preview of a path the overlay will refuse. Two halves disagreeing about
        /// which file is used is the failure mode the whole C#/JS mirror exists to prevent.</para>
        ///
        /// <paramref name="rejected"/> reports the refusal separately from
        /// <paramref name="isWired"/> so a caller can tell "wired and unresolvable at design
        /// time" (the normal state for a live-data path) from "wired to something this loader
        /// will never accept" — two very different things to tell an author.
        /// </summary>
        private static string ResolveMediaPathInput(
            EvalContext ctx, Node node, LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited,
            out bool isWired, out bool rejected)
        {
            rejected = false;
            string path = ResolveStringOrAttr(
                ctx, node, "Path", "Path", string.Empty, layerRes, visiting, visited, out isWired);
            if (path.Length == 0) return string.Empty;
            if (!isWired) return path;                        // author-typed: unchanged.
            if (!IsNonRelativeMediaPath(path)) return path;
            // Rejected: empty rather than the attribute fallback. Falling back would preview
            // the author's leftover clip for attacker input, which is the same problem quieter.
            rejected = true;
            return string.Empty;
        }

        // Clamp a (possibly fractional / out-of-range) channel value to a
        // 0..255 integer for Convert.ColorFromRGBA hex assembly.
        private static int ClampByte(double v) =>
            (int)System.Math.Clamp(System.Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);

        // Normalise / validate a hex color string for Convert.HexToColor.
        // Accepts #rgb, #rrggbb, #rrggbbaa (with or without a leading '#'); anything
        // else falls back to "#ffffff". #rgb is expanded to #rrggbb. Mirrors the JS
        // side's normalise/validate-or-default behaviour.
        private static string NormalizeHexColor(string hex)
        {
            const string fallback = "#ffffff";
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            string h = hex.Trim();
            if (h.StartsWith("#", StringComparison.Ordinal)) h = h.Substring(1);
            if (h.Length is not (3 or 6 or 8)) return fallback;
            foreach (char c in h)
                if (!Uri.IsHexDigit(c)) return fallback;
            if (h.Length == 3) // #rgb -> #rrggbb
                h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
            return "#" + h.ToLowerInvariant();
        }

        // Used by templates that may or may not be wired to an upstream
        // image (e.g. Text.Translate). When wired: passthrough. When not wired:
        // stamp a widget-sized stub so the graph walk doesn't error. Avoids
        // the previous HasError-on-unwired-input behavior for templates whose
        // primary purpose isn't image transformation.
        private static ImageMetadata? EvalImageOrStub(
            EvalContext ctx, Node node, string imageInputName,
            LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited,
            string kernelName)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == imageInputName && s.Type == SocketType.Input);
            if (sock is null) return new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = kernelName };
            var link = ctx.FindLink(node.Id, sock.Id);
            if (link is null) return new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = kernelName };
            return EvalImage(ctx, link.FromNodeId, link.FromSocketId, layerRes, widgetRect, visiting, visited);
        }

        // Image.Crop dimension parity with compositor.js.
        // The previous C# evaluator used EvalImagePassthrough for Image.Crop, which
        // returned the upstream dims unchanged. JS shrinks to the cropped Rect.
        // Mirror the JS dim calc here so design-time previews match runtime layout.
        //
        // Rect is a Vector4 (x,y,w,h) of 0..1 fractions of the upstream image —
        // NOT pixels. This was the discrepancy: the template's comment
        // historically claimed "source-image pixels" but both the JS kernel
        // (compositor.js evalNodeOutput / case 'Image.Crop') and this evaluator
        // multiply the rect by source w/h. The browser is the source of truth
        // for compositor semantics, so the template comment was the wrong side.
        //
        // The fraction convention is the canonical contract across NodeTemplates.cs,
        // this evaluator, and compositor.js (both the runtime evaluator and the
        // manipulator's parseFractionRect).
        //
        // Rect resolves either via the wired "Rect" socket or — when unwired —
        // the "Rect" attribute as comma-separated "x,y,w,h" (default "0,0,1,1",
        // full-image passthrough).
        private static ImageMetadata? EvalImageCrop(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double[]? rect = ResolveVectorInputSocket(ctx, node, "Rect", 4, layerRes, visiting, visited);
            if (rect is null)
            {
                var raw = node.Attributes.GetValueOrDefault("Rect", "0,0,1,1");
                var parts = raw.Split(',');
                rect = new double[4]
                {
                    parts.Length > 0 ? ParseDouble(parts[0]) : 0,
                    parts.Length > 1 ? ParseDouble(parts[1]) : 0,
                    parts.Length > 2 ? ParseDouble(parts[2]) : 1,
                    parts.Length > 3 ? ParseDouble(parts[3]) : 1,
                };
            }

            // An empty or malformed Rect attribute (e.g. ",,,") parses
            // to [0,0,0,0] and previously collapsed the crop to a 1×1 sliver
            // via the Math.Max(1, …) floors below. That's an opaque failure —
            // authors see no image. Detect the all-zero width/height case and
            // default to source-image full rect [0,0,1,1] so a broken Rect
            // attribute reads as "no crop applied" instead of "invisible
            // widget". Authors that genuinely want a 0×0 crop have to wire a
            // typed Vector4 (which goes through ResolveVectorInputSocket and
            // bypasses this attribute path).
            if (rect[2] <= 0 && rect[3] <= 0)
            {
                rect = new double[] { 0, 0, 1, 1 };
            }

            double sw = upstream.Width;
            double sh = upstream.Height;
            double sx = System.Math.Max(0, System.Math.Min(sw, rect[0] * sw));
            double sy = System.Math.Max(0, System.Math.Min(sh, rect[1] * sh));
            int cw = (int)System.Math.Max(1, System.Math.Min(sw - sx, rect[2] * sw));
            int ch = (int)System.Math.Max(1, System.Math.Min(sh - sy, rect[3] * sh));

            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = cw,
                Height = ch,
                Kernel = "Image.Crop",
            };
        }

        private static ImageMetadata? EvalImagePassthrough(
            EvalContext ctx, Node node, string imageInputName,
            LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == imageInputName && s.Type == SocketType.Input);
            if (sock is null) return new ImageMetadata { HasError = true, ErrorMessage = $"{node.Title} missing '{imageInputName}' socket" };
            var link = ctx.FindLink(node.Id, sock.Id);
            if (link is null) return new ImageMetadata { HasError = true, ErrorMessage = $"{node.Title} '{imageInputName}' socket not connected" };
            return EvalImage(ctx, link.FromNodeId, link.FromSocketId, layerRes, widgetRect, visiting, visited);
        }

        // ── Image kernel evaluators ─────────────────────────────────────────────
        //
        // The C# evaluator does not perform real pixel work — it's the design-time
        // mirror of compositor.js used to validate graph traversal + metadata flow.
        // Each kernel:
        //   1. Walks its required upstream Image input via EvalImagePassthrough.
        //   2. Stamps Kernel + KernelAttributes onto the result so tests can assert
        //      "the graph traversed through Image.<X> with attributes <Y>".
        //   3. Surfaces an error (HasError=true) when a required input is missing,
        //      matching the JS side's `null + console.warn` policy. Silent
        //      passthrough is the failure mode this fix exists to remove.

        // Image.Transform reads its wired Vector2/Scalar
        // sockets (Translate/Scale/Rotate) first, falling back to the static
        // attribute defaults (TranslateX/Y, ScaleX/Y, Rotation) only when the
        // socket is unwired. The previous implementation only read the
        // attribute fallbacks, so any upstream Math/Vector chain feeding one of
        // these sockets was silently dropped at design time even though the
        // socket existed on the template. KernelAttributes carries the
        // resolved value so tests can assert on what the kernel actually saw.
        private static ImageMetadata? EvalImageTransform(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            // Translate / Scale are Vector2 sockets; the attribute fallbacks
            // are split as <name>X / <name>Y so a half-edited node still has a
            // sensible value per component.
            (double x, double y) translate = ResolveVector2OrAttrPair(
                ctx, node, "Translate", "TranslateX", "TranslateY", 0, 0, layerRes, visiting, visited);
            (double x, double y) scale     = ResolveVector2OrAttrPair(
                ctx, node, "Scale",     "ScaleX",     "ScaleY",     1, 1, layerRes, visiting, visited);
            double rotation = ResolveScalarOrAttr(
                ctx, node, "Rotate", "Rotation", 0, layerRes, visiting, visited);

            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = upstream.Width,
                Height = upstream.Height,
                Kernel = "Image.Transform",
                KernelAttributes =
                {
                    ["TranslateX"] = translate.x.ToString(CultureInfo.InvariantCulture),
                    ["TranslateY"] = translate.y.ToString(CultureInfo.InvariantCulture),
                    ["ScaleX"]     = scale.x.ToString(CultureInfo.InvariantCulture),
                    ["ScaleY"]     = scale.y.ToString(CultureInfo.InvariantCulture),
                    ["Rotation"]   = rotation.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        // Image.ColorAdjust resolves each scalar socket (Brightness /
        // Contrast / Saturation / Hue) through the upstream graph before
        // falling back to the attribute default. Mirrors compositor.js's
        // _evalScalarSocket-driven path so design-time previews track the
        // browser-side render once a Math chain drives a colour parameter.
        private static ImageMetadata? EvalImageColorAdjust(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double brightness = ResolveScalarOrAttr(ctx, node, "Brightness", "Brightness", 0, layerRes, visiting, visited);
            double contrast   = ResolveScalarOrAttr(ctx, node, "Contrast",   "Contrast",   0, layerRes, visiting, visited);
            double saturation = ResolveScalarOrAttr(ctx, node, "Saturation", "Saturation", 0, layerRes, visiting, visited);
            double hue        = ResolveScalarOrAttr(ctx, node, "Hue",        "Hue",        0, layerRes, visiting, visited);

            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = upstream.Width,
                Height = upstream.Height,
                Kernel = "Image.ColorAdjust",
                KernelAttributes =
                {
                    ["Brightness"] = brightness.ToString(CultureInfo.InvariantCulture),
                    ["Contrast"]   = contrast.ToString(CultureInfo.InvariantCulture),
                    ["Saturation"] = saturation.ToString(CultureInfo.InvariantCulture),
                    ["Hue"]        = hue.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        // ── Shared helpers ──────────────────────────────────────────────────
        // Both helpers honour the "wired socket wins" contract from
        // compositor.js: walk the link if present, then fall back to the
        // attribute(s). Unwired sockets that can't resolve (cycle, broken
        // upstream) fall through to the attribute too — same lenient
        // behaviour Image.Scale uses for its Factor socket.

        private static double ResolveScalarOrAttr(
            EvalContext ctx, Node node, string socketName, string attrKey, double defaultValue,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            Socket? sock = null;
            foreach (var s in node.Sockets) { if (s.Type == SocketType.Input && s.Name == socketName) { sock = s; break; } }
            if (sock is not null)
            {
                var link = ctx.FindLink(node.Id, sock.Id);
                if (link is not null)
                {
                    var resolved = EvalScalarNode(ctx, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
                    if (resolved is not null) return resolved.Value;
                }
            }
            return ParseDouble(node.Attributes.GetValueOrDefault(attrKey, defaultValue.ToString(CultureInfo.InvariantCulture)));
        }

        // Vector2 socket resolver with per-component attribute fallbacks. Used
        // for sockets like Translate/Scale/Repeat that expose <Name>X / <Name>Y
        // attribute pairs for the unwired case so half-edited templates still
        // round-trip a sensible scalar per axis.
        private static (double x, double y) ResolveVector2OrAttrPair(
            EvalContext ctx, Node node, string socketName,
            string xAttrKey, string yAttrKey,
            double defaultX, double defaultY,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            Socket? sock = null;
            foreach (var s in node.Sockets) { if (s.Type == SocketType.Input && s.Name == socketName) { sock = s; break; } }
            if (sock is not null)
            {
                var link = ctx.FindLink(node.Id, sock.Id);
                if (link is not null)
                {
                    // Try vector evaluator first, then scalar-broadcast as a
                    // compatibility shim (mirrors ResolveVectorInputSocket /
                    // compositor.js _evalVectorSocket broadcast semantics).
                    var vec = EvalVectorNode(ctx, link.FromNodeId, layerRes, visiting, visited);
                    if (vec is not null && vec.Length >= 2)
                        return (vec[0], vec[1]);
                    var scalar = EvalScalarNode(ctx, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
                    if (scalar is not null)
                        return (scalar.Value, scalar.Value);
                }
            }
            double x = ParseDouble(node.Attributes.GetValueOrDefault(xAttrKey, defaultX.ToString(CultureInfo.InvariantCulture)));
            double y = ParseDouble(node.Attributes.GetValueOrDefault(yAttrKey, defaultY.ToString(CultureInfo.InvariantCulture)));
            return (x, y);
        }

        // Image.Blur / Gaussian / Mosaic / Shadow / Glow / Distort
        // all expose Scalar inputs on their templates but historically only read
        // the attribute default. Each kernel now mirrors the JS side
        // (compositor.js _evalScalarSocket) so an upstream Math chain or
        // Scalar.Constant feeding a wired parameter shows up in the design-time
        // metadata that tests assert against.

        private static ImageMetadata? EvalImageBlur(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double radius = ResolveScalarOrAttr(ctx, node, "Radius", "Radius", 0, layerRes, visiting, visited);
            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = upstream.Width,
                Height = upstream.Height,
                Kernel = "Image.Blur",
                KernelAttributes =
                {
                    ["Radius"] = radius.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        private static ImageMetadata? EvalImageGaussian(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double sx = ResolveScalarOrAttr(ctx, node, "SigmaX", "SigmaX", 0, layerRes, visiting, visited);
            double sy = ResolveScalarOrAttr(ctx, node, "SigmaY", "SigmaY", 0, layerRes, visiting, visited);
            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = upstream.Width,
                Height = upstream.Height,
                Kernel = "Image.Gaussian",
                KernelAttributes =
                {
                    ["SigmaX"] = sx.ToString(CultureInfo.InvariantCulture),
                    ["SigmaY"] = sy.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        private static ImageMetadata? EvalImageMosaic(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double tileSize = ResolveScalarOrAttr(ctx, node, "TileSize", "TileSize", 8, layerRes, visiting, visited);
            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = upstream.Width,
                Height = upstream.Height,
                Kernel = "Image.Mosaic",
                KernelAttributes =
                {
                    ["TileSize"] = tileSize.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        private static ImageMetadata? EvalImageShadow(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double ox   = ResolveScalarOrAttr(ctx, node, "OffsetX", "OffsetX", 4, layerRes, visiting, visited);
            double oy   = ResolveScalarOrAttr(ctx, node, "OffsetY", "OffsetY", 4, layerRes, visiting, visited);
            double blur = ResolveScalarOrAttr(ctx, node, "Blur",    "Blur",    6, layerRes, visiting, visited);
            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = upstream.Width,
                Height = upstream.Height,
                Kernel = "Image.Shadow",
                KernelAttributes =
                {
                    ["OffsetX"] = ox.ToString(CultureInfo.InvariantCulture),
                    ["OffsetY"] = oy.ToString(CultureInfo.InvariantCulture),
                    ["Blur"]    = blur.ToString(CultureInfo.InvariantCulture),
                    // Color is a string attribute — no socket exists today.
                    ["Color"]   = node.Attributes.GetValueOrDefault("Color",   "\"rgba(0,0,0,0.5)\""),
                },
            };
        }

        private static ImageMetadata? EvalImageGlow(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double radius    = ResolveScalarOrAttr(ctx, node, "Radius",    "Radius",    12, layerRes, visiting, visited);
            double intensity = ResolveScalarOrAttr(ctx, node, "Intensity", "Intensity", 1,  layerRes, visiting, visited);
            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = upstream.Width,
                Height = upstream.Height,
                Kernel = "Image.Glow",
                KernelAttributes =
                {
                    ["Radius"]    = radius.ToString(CultureInfo.InvariantCulture),
                    ["Intensity"] = intensity.ToString(CultureInfo.InvariantCulture),
                    // Color is a string attribute — no socket exists today.
                    ["Color"]     = node.Attributes.GetValueOrDefault("Color",     "\"rgba(255,255,200,0.85)\""),
                },
            };
        }

        private static ImageMetadata? EvalImageDistort(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double amp  = ResolveScalarOrAttr(ctx, node, "Amplitude", "Amplitude", 8, layerRes, visiting, visited);
            double freq = ResolveScalarOrAttr(ctx, node, "Frequency", "Frequency", 4, layerRes, visiting, visited);
            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = upstream.Width,
                Height = upstream.Height,
                Kernel = "Image.Distort",
                KernelAttributes =
                {
                    // Mode is a string attribute — no socket exists today.
                    ["Mode"]      = node.Attributes.GetValueOrDefault("Mode",      "\"wave\""),
                    ["Amplitude"] = amp.ToString(CultureInfo.InvariantCulture),
                    ["Frequency"] = freq.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        private static ImageMetadata? EvalImageMask(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            // Image input is required.
            var image = EvalImagePassthrough(ctx, node, "Image", layerRes, widgetRect, visiting, visited);
            if (image is null || image.HasError) return image;

            // Mask input is also required — surface an explicit error rather than
            // silently producing the unmasked image (which would mislead authors).
            var maskSock = node.Sockets.FirstOrDefault(s => s.Name == "Mask" && s.Type == SocketType.Input);
            var maskLink = maskSock is null
                ? null
                : ctx.FindLink(node.Id, maskSock.Id);
            if (maskLink is null)
            {
                return new ImageMetadata
                {
                    Source       = image.Source,
                    Width        = image.Width,
                    Height       = image.Height,
                    HasError     = true,
                    ErrorMessage = "Image.Mask 'Mask' socket not connected",
                    Kernel       = "Image.Mask",
                };
            }

            // Walk the mask chain so cycles/errors upstream surface here too.
            var mask = EvalImage(ctx, maskLink.FromNodeId, maskLink.FromSocketId, layerRes, widgetRect, visiting, visited);
            if (mask is { HasError: true })
            {
                return new ImageMetadata
                {
                    Source       = image.Source,
                    Width        = image.Width,
                    Height       = image.Height,
                    HasError     = true,
                    ErrorMessage = $"Image.Mask 'Mask' upstream errored: {mask.ErrorMessage}",
                    Kernel       = "Image.Mask",
                };
            }

            return new ImageMetadata
            {
                Source = image.Source,
                Width  = image.Width,
                Height = image.Height,
                Kernel = "Image.Mask",
                KernelAttributes =
                {
                    // Strip enclosing quotes to mirror compositor.js
                    // (`stripQuotes(attr(node, 'Mode', '"alpha"'))`). Without this,
                    // a Mode attribute persisted as `"alpha"` ships through with
                    // literal escaped quotes and the JS kernel picks the default branch.
                    ["Mode"] = StripQuotes(node.Attributes.GetValueOrDefault("Mode", "alpha")),
                },
            };
        }

        private static ImageMetadata? EvalImageBlend(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            // A blend composites B (top) over A (bottom). A missing / empty /
            // errored side contributes nothing — return the OTHER layer rather than
            // collapsing the whole blend to an error (the bug: an unfilled
            // Text.Render caption over a loaded image showed "load failed" and the
            // node body read "(no input)" even though A was a valid image). Only
            // surface an error when NEITHER side yields an image. Mirrors
            // evalImageBlend in compositor.js.
            var a = EvalImagePassthrough(ctx, node, "A", layerRes, widgetRect, visiting, visited);
            var b = EvalImagePassthrough(ctx, node, "B", layerRes, widgetRect, visiting, visited);
            // Treat a missing / errored side as "no image" (null) so the null-flow
            // below is explicit to the compiler.
            if (a is { HasError: true }) a = null;
            if (b is { HasError: true }) b = null;
            if (a is null && b is null)
                return new ImageMetadata { HasError = true, ErrorMessage = "Image.Blend has no usable input", Kernel = "Image.Blend" };
            if (b is null) return a;   // empty top → just the bottom layer
            if (a is null) return b;   // empty bottom → just the top layer

            string mode = StripQuotes(node.Attributes.GetValueOrDefault("Mode", "normal"));
            // Sanity-check Mode against the standard CSS blend list,
            // matching the JS side's fallback behavior. Invalid modes still
            // succeed with a sane default rather than erroring; the JS side
            // logs a console.warn and falls back to source-over.
            if (!_validBlendModes.Contains(mode))
            {
                mode = "normal";
            }

            // Opacity socket honours wired upstream first.
            double opacity = ResolveScalarOrAttr(ctx, node, "Opacity", "Opacity", 1, layerRes, visiting, visited);

            return new ImageMetadata
            {
                Source = a.Source,
                Width  = a.Width,
                Height = a.Height,
                Kernel = "Image.Blend",
                KernelAttributes =
                {
                    ["Mode"]    = mode,
                    ["Opacity"] = opacity.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        private static ImageMetadata? EvalImageCombine(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            // Same dual-input contract as Image.Blend (A bottom, B top required)
            // but Mode covers blend modes AND alpha/luminance key modes in one
            // node. Shape generators feeding either socket "just work" because
            // they're typed Image.
            // Same empty-tolerance as Image.Blend: a missing/empty side contributes
            // nothing, so return the other rather than failing the whole node.
            var a = EvalImagePassthrough(ctx, node, "A", layerRes, widgetRect, visiting, visited);
            var b = EvalImagePassthrough(ctx, node, "B", layerRes, widgetRect, visiting, visited);
            if (a is { HasError: true }) a = null;
            if (b is { HasError: true }) b = null;
            if (a is null && b is null)
                return new ImageMetadata { HasError = true, ErrorMessage = "Image.Combine has no usable input", Kernel = "Image.Combine" };
            if (b is null) return a;
            if (a is null) return b;

            string mode = StripQuotes(node.Attributes.GetValueOrDefault("Mode", "normal"));
            if (!_validBlendModes.Contains(mode) && !_validKeyModes.Contains(mode))
            {
                mode = "normal";
            }

            // Opacity socket honours wired upstream first.
            double opacity = ResolveScalarOrAttr(ctx, node, "Opacity", "Opacity", 1, layerRes, visiting, visited);

            return new ImageMetadata
            {
                Source = a.Source,
                Width  = a.Width,
                Height = a.Height,
                Kernel = "Image.Combine",
                KernelAttributes =
                {
                    ["Mode"]    = mode,
                    ["Opacity"] = opacity.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        // Image.Combine-only modes (compositor.js handles the actual key math
        // via per-pixel alpha derivation). Standard CSS blend modes live in
        // _validBlendModes and apply to both Image.Blend and Image.Combine.
        private static readonly HashSet<string> _validKeyModes = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "alpha-key", "luminance-key", "inv-luminance",
        };

        // Image.Tile reads its Repeat Vector2 socket first, then
        // falls back to the per-axis attribute (RepeatX / RepeatY). The JS
        // side does the same thing already (see compositor.js evalImageTile),
        // so the C# evaluator was the laggard.
        private static ImageMetadata? EvalImageTile(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            (double rx, double ry) = ResolveVector2OrAttrPair(
                ctx, node, "Repeat", "RepeatX", "RepeatY", 1, 1, layerRes, visiting, visited);

            // Wrap parity with compositor.js. The browser kernel reads
            // `stripQuotes(attr(node, 'Wrap', '"repeat"'))` and branches on
            // repeat / mirror / clamp, warning + falling back to 'repeat' on an
            // unknown value. Mirror that here so the design-time KernelAttributes
            // dict carries the same metadata the OBS runtime acts on.
            string wrap = StripQuotes(node.Attributes.GetValueOrDefault("Wrap", "repeat"));
            if (!_validTileWrapModes.Contains(wrap))
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    $"Image.Tile: unknown Wrap '{wrap}' — using 'repeat'.",
                    "NodeEvaluator",
                    Phoenix.Controls.Shared.Models.LogLevel.System);
                wrap = "repeat";
            }

            // Tiling fills the widget rect with repeats of the source image, so
            // the output dimensions match the widget rect rather than the source.
            return new ImageMetadata
            {
                Source = upstream.Source,
                Width  = widgetRect.Width,
                Height = widgetRect.Height,
                Kernel = "Image.Tile",
                KernelAttributes =
                {
                    ["RepeatX"] = rx.ToString(CultureInfo.InvariantCulture),
                    ["RepeatY"] = ry.ToString(CultureInfo.InvariantCulture),
                    ["Wrap"]    = wrap,
                },
            };
        }

        // Image.Tile per-edge wrap modes. Matches the set compositor.js
        // validates against (repeat / mirror / clamp); anything else falls back
        // to 'repeat' with a design-time warn.
        private static readonly HashSet<string> _validTileWrapModes = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "repeat", "mirror", "clamp",
        };

        // Standard CSS blend modes accepted by canvas
        // globalCompositeOperation. Matches the list compositor.js validates against.
        private static readonly HashSet<string> _validBlendModes = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "normal", "source-over",
            "multiply", "screen", "overlay", "darken", "lighten",
            "color-dodge", "color-burn", "hard-light", "soft-light",
            "difference", "exclusion", "hue", "saturation", "color", "luminosity",
        };

        private static double ParseDouble(string s)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;
            // Surface genuine authoring typos (e.g. Factor="abc") to the design-time
            // log so a numeric attribute that silently collapses to 0 is visible.
            // Skip the benign unset/empty/whitespace case — it's the normal "no value
            // set" fallback and would spam the log on every preview refresh.
            if (!string.IsNullOrWhiteSpace(s))
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    $"NodeEvaluator: could not parse '{s}' as a number — using 0.",
                    "NodeEvaluator",
                    Phoenix.Controls.Shared.Models.LogLevel.System);
            return 0;
        }

        private static string StripQuotes(string s) =>
            s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

        // Result.If — gate the upstream Image when eventData[When] equals the Equals
        // attribute (or wired Equals input).
        //
        // The two "no image flowed through this leg" cases
        // are now distinguishable to the design-time test surface (and the canvas
        // preview):
        //
        //   • Value-mismatch (arg present but != Equals)  → null
        //       Same semantics as before — a legitimately blocked branch is not an
        //       error; downstream evaluators silently skip and the cache still
        //       memoises a null hit.
        //
        //   • Arg-missing (When references a key not in eventData)  → ImageMetadata
        //       with HasError=true and a descriptive ErrorMessage. Matches the
        //       pattern already used for Color.Constant-into-Image upstream
        //       mismatches: tests can assert the misconfiguration explicitly
        //       instead of mistaking it for an intentional gate.
        //
        // Once-per-fire missing-arg log is keyed by (widgetId-or-nodeId, When) so a
        // graph that uses the same When in two Result.If nodes only logs once per
        // fire per node, and never spams when the user re-renders.
        private static ImageMetadata? EvalResultIf(
            EvalContext ctx, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            string when    = StripQuotes(node.Attributes.GetValueOrDefault("When",   "\"\""));
            string expectA = StripQuotes(node.Attributes.GetValueOrDefault("Equals", "\"\""));

            // Equals can also arrive via a wired String input; the wired value wins
            // when present so a Math/Text chain can drive the comparison dynamically.
            var eqSock = node.Sockets.FirstOrDefault(s => s.Name == "Equals" && s.Type == SocketType.Input);
            if (eqSock is not null)
            {
                var eqLink = ctx.FindLink(node.Id, eqSock.Id);
                if (eqLink is not null)
                {
                    var src = ctx.FindNode(eqLink.FromNodeId);
                    if (src is not null)
                    {
                        // The widget evaluator has no String evaluator — most string
                        // sources are constants whose value lives on an attribute. Read
                        // the producing socket's owning attribute when present, fall
                        // back to a Text/Value attribute, then to the static Equals.
                        string? wired = src.Attributes.GetValueOrDefault("Value")
                                     ?? src.Attributes.GetValueOrDefault("Text");
                        if (!string.IsNullOrEmpty(wired)) expectA = StripQuotes(wired);
                    }
                }
            }

            string argValue = "";
            bool   argMissing = true;
            if (!string.IsNullOrEmpty(when))
            {
                var ed = _currentEventData;
                if (ed is not null && ed.TryGetValue(when, out var v))
                {
                    argValue   = v ?? "";
                    argMissing = false;
                }
            }

            if (argMissing)
            {
                LogMissingArg(node, when);
                // Surface arg-missing as a HasError ImageMetadata so the
                // design-time test surface and the canvas preview can distinguish
                // it from a legitimate value-mismatch gate (which still returns
                // null below). Tests assert against ErrorMessage; the canvas
                // preview pipeline renders the Error tint via PreviewKind.Error.
                return new ImageMetadata
                {
                    HasError     = true,
                    ErrorMessage = string.IsNullOrEmpty(when)
                        ? "Result.If: 'When' attribute is empty — wire the comparison key or set an attribute value."
                        : $"Result.If: arg '{when}' not supplied in eventData — branch blocked.",
                    Kernel       = "Result.If",
                };
            }

            if (!string.Equals(argValue, expectA, StringComparison.Ordinal))
                return null; // Value-mismatch: intentional gate, no error surface.

            // Match — pass the upstream Image through unchanged.
            return EvalImagePassthrough(ctx, node, "In", layerRes, widgetRect, visiting, visited);
        }

        private static void LogMissingArg(Node node, string when)
        {
            string key = $"{node.Id}|{when}";
            var dedup = _loggedMissingArgs;
            if (dedup is not null && !dedup.Add(key)) return;
            try
            {
                GlobalLogger.Log(
                    $"Result.If on node '{node.Id}': arg '{when}' not supplied — branch blocked.",
                    "WidgetEval", LogLevel.LogicExecution);
            }
            catch
            {
                // GlobalLogger ring buffer can throw under shutdown; eat the failure
                // because the eval pass must keep walking the rest of the graph.
            }
        }
    }
}
