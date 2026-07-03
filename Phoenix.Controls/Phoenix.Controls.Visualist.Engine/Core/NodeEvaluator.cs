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

        public sealed class EvalResult
        {
            public ImageMetadata? Display { get; set; }
            public bool           DisplayConnected { get; set; }
            public bool           CompleteConnected { get; set; }
            public List<string>   VisitedNodeIds { get; } = new();
        }

        /// <summary>
        /// Body-preview snapshot kind. Drives how WidgetGraphCanvas.PaintBodyPreview
        /// renders the strip — bitmap, swatch, empty hint, or error tint.
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
        /// </summary>
        public static IReadOnlyDictionary<string, PreviewSnapshot> EvaluatePreviews(Graph graph)
        {
            var snapshots = new Dictionary<string, PreviewSnapshot>(StringComparer.Ordinal);
            if (graph is null) return snapshots;

            foreach (var node in graph.Nodes)
            {
                var src = NodeTemplates.GetPreviewSource(node.Title);
                if (src == PreviewSource.None) continue;
                snapshots[node.Id] = ResolvePreviewSnapshot(graph, node, src);
            }
            return snapshots;
        }

        private static PreviewSnapshot ResolvePreviewSnapshot(Graph graph, Node node, PreviewSource src)
        {
            switch (src)
            {
                case PreviewSource.OwnPath:
                {
                    string path = StripQuotes(node.Attributes.GetValueOrDefault("Path", string.Empty));
                    if (string.IsNullOrEmpty(path))
                        return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = src, Hint = "(no path set)" };
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
                {
                    string hex = StripQuotes(
                        node.Attributes.GetValueOrDefault("Value",
                            node.Attributes.GetValueOrDefault("Color", string.Empty)));
                    if (string.IsNullOrEmpty(hex))
                        return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = src, Hint = "(no color set)" };
                    return new PreviewSnapshot { Kind = PreviewKind.Color, ColorHex = hex, Declared = src };
                }

                case PreviewSource.UpstreamImage:
                    return ResolveUpstreamImageSnapshot(graph, node);
            }

            return new PreviewSnapshot { Declared = src };
        }

        // BFS-style walk from `node`'s canonical "In" socket to the nearest
        // resolvable Image source. Lives next to the rest of the evaluator so
        // the canvas can paint from a pre-computed snapshot rather than
        // re-walking the graph on every Invalidate.
        private static PreviewSnapshot ResolveUpstreamImageSnapshot(Graph graph, Node start)
        {
            var inSock = start.Sockets.FirstOrDefault(s =>
                s.Type == SocketType.Input &&
                (s.DataType == SocketDataType.Image || s.DataType == SocketDataType.Any) &&
                string.Equals(s.Name, "In", StringComparison.OrdinalIgnoreCase));
            if (inSock is null)
                return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(no input)" };

            var firstLink = graph.Links.FirstOrDefault(l => l.ToNodeId == start.Id && l.ToSocketId == inSock.Id);
            if (firstLink is null)
                return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(no image upstream)" };

            // Pre-index nodes by id and links by (ToNodeId, ToSocketId) so the upstream
            // walk below uses O(1) lookups instead of O(n)/O(m) linear scans per hop —
            // EvaluatePreviews calls this for every preview node on every graph mutation,
            // so the previous per-hop FirstOrDefault scans were O(NodeCount * 100 * (N+M))
            // on the UI thread. Both dictionaries keep the first occurrence per key to
            // preserve the prior FirstOrDefault first-match semantics.
            var nodeById = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var gn in graph.Nodes)
                if (gn.Id is not null && !nodeById.ContainsKey(gn.Id)) nodeById[gn.Id] = gn;
            var linkByTarget = new Dictionary<(string, string), Link>();
            foreach (var gl in graph.Links)
            {
                if (gl.ToNodeId is null || gl.ToSocketId is null) continue;
                var k = (gl.ToNodeId, gl.ToSocketId);
                if (!linkByTarget.ContainsKey(k)) linkByTarget[k] = gl;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { start.Id };
            string nextNodeId = firstLink.FromNodeId;
            for (int hop = 0; hop < 100; hop++)
            {
                if (!visited.Add(nextNodeId))
                    return new PreviewSnapshot { Kind = PreviewKind.Error, Declared = PreviewSource.UpstreamImage, Hint = "(cycle in upstream chain)" };

                var n = nodeById.GetValueOrDefault(nextNodeId);
                if (n is null)
                    return new PreviewSnapshot { Kind = PreviewKind.Error, Declared = PreviewSource.UpstreamImage, Hint = "(broken link)" };

                if (string.Equals(n.Title, "Image.Load", StringComparison.OrdinalIgnoreCase))
                {
                    string p = StripQuotes(n.Attributes.GetValueOrDefault("Path", string.Empty));
                    if (string.IsNullOrEmpty(p))
                        return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(no path set)" };
                    return new PreviewSnapshot { Kind = PreviewKind.Image, Source = p, Declared = PreviewSource.UpstreamImage };
                }

                // Walk through the canonical Image input — same priority order
                // the canvas uses for its passthrough preview. Bail when the
                // chain terminates in a non-Image source we can't decode at
                // design time (Image.LoadUrl, Video.Load, mask shape generator,
                // Color.Constant, etc.).
                string[] preferredNames = { "In", "Image", "A" };
                Socket? upSock = null;
                foreach (var name in preferredNames)
                {
                    upSock = n.Sockets.FirstOrDefault(s =>
                        s.Type == SocketType.Input &&
                        s.DataType == SocketDataType.Image &&
                        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (upSock is not null) break;
                }
                if (upSock is null)
                    return new PreviewSnapshot { Kind = PreviewKind.Unloaded, Declared = PreviewSource.UpstreamImage, Hint = "(no decodable image upstream)" };

                var upLink = (n.Id is not null && upSock.Id is not null)
                    ? linkByTarget.GetValueOrDefault((n.Id, upSock.Id))
                    : null;
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
            var sink = graph.Nodes.FirstOrDefault(DisplaySinkNode.Is);
            if (sink is null) return result;

            var imageSocket = sink.Sockets.FirstOrDefault(s => s.Type == SocketType.Input && s.DataType == SocketDataType.Image);
            var visiting = new HashSet<string>();
            if (imageSocket is not null)
            {
                var upstreamLink = graph.Links.FirstOrDefault(l => l.ToNodeId == sink.Id && l.ToSocketId == imageSocket.Id);
                if (upstreamLink is not null)
                {
                    result.DisplayConnected = true;
                    result.Display = EvalImageMemoized(graph, upstreamLink.FromNodeId, upstreamLink.FromSocketId, layerResolution, widgetRect, visiting, result.VisitedNodeIds, memo);
                }
            }

            // Visual.Complete sink: evaluate (and walk into VisitedNodeIds) any
            // upstream graph that ends at the Complete sink, mirroring compositor.js's
            // evalAnyInputOf(Complete) probe. Result isn't surfaced as ImageMetadata
            // (Complete is just a flow signal), but the visited-node bookkeeping must
            // include it so parity tests don't drift between sides.
            var completeSink = graph.Nodes.FirstOrDefault(n =>
                string.Equals(n.Title, "Visual.Complete", System.StringComparison.OrdinalIgnoreCase));
            if (completeSink is not null)
            {
                foreach (var inSock in completeSink.Sockets.Where(s => s.Type == SocketType.Input))
                {
                    var link = graph.Links.FirstOrDefault(l => l.ToNodeId == completeSink.Id && l.ToSocketId == inSock.Id);
                    if (link is null) continue;
                    result.CompleteConnected = true;
                    if (inSock.DataType == SocketDataType.Image)
                    {
                        EvalImageMemoized(graph, link.FromNodeId, link.FromSocketId, layerResolution, widgetRect, visiting, result.VisitedNodeIds, memo);
                    }
                    else
                    {
                        EvalScalarNode(graph, link.FromNodeId, link.FromSocketId, layerResolution, visiting, result.VisitedNodeIds);
                    }
                }
            }
            return result;
        }

        // Memoization wrapper around EvalImage.
        private static ImageMetadata? EvalImageMemoized(
            Graph graph, string nodeId, string socketId,
            LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited,
            Dictionary<string, ImageMetadata?> memo)
        {
            string key = $"{nodeId}:{socketId}";
            if (memo.TryGetValue(key, out var cached)) return cached;
            var result = EvalImage(graph, nodeId, socketId, layerRes, widgetRect, visiting, visited);
            memo[key] = result;
            return result;
        }

        private static ImageMetadata? EvalImage(
            Graph graph, string nodeId, string socketId,
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
            var node = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is null)
                return new ImageMetadata { HasError = true, ErrorMessage = $"unknown node id {nodeId}" };

            ImageMetadata? r = node.Title switch
            {
                "Image.Load"        => EvalImageLoad(node, widgetRect),
                "Image.LoadUrl"     => EvalImageLoadUrl(node, widgetRect),
                "Image.Scale"       => EvalImageScale(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Transform"   => EvalImageTransform(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.ColorAdjust" => EvalImageColorAdjust(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Blur"        => EvalImageBlur(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Gaussian"    => EvalImageGaussian(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Mosaic"      => EvalImageMosaic(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Shadow"      => EvalImageShadow(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Glow"        => EvalImageGlow(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Distort"     => EvalImageDistort(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Mask"        => EvalImageMask(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Crop"        => EvalImageCrop(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Tile"        => EvalImageTile(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Blend"       => EvalImageBlend(graph, node, layerRes, widgetRect, visiting, visited),
                "Image.Combine"     => EvalImageCombine(graph, node, layerRes, widgetRect, visiting, visited),
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
                // Viewer passes the upstream image through unchanged.
                "Viewer"            => EvalImagePassthrough(graph, node, "In",    layerRes, widgetRect, visiting, visited),
                // Result.If — barrier between an Image source and Display. Reads
                // eventData[When] and compares to Equals; passes In through on match,
                // emits null (branch blocked) on mismatch or when the named arg is
                // missing. Logs once per fire when the arg is missing so authoring
                // mistakes (mis-typed When attr) surface in GlobalLogger.
                "Result.If"         => EvalResultIf(graph, node, layerRes, widgetRect, visiting, visited),

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
                "Text.Translate"      => EvalImageOrStub(graph, node, "In", layerRes, widgetRect, visiting, visited, "Text.Translate"),

                // Video.Load — outputs a video stream. Design-time stub returns
                // widget-rect dims with the Source attribute so authoring sees
                // the path even though decode happens browser-side.
                "Video.Load"          => new ImageMetadata
                {
                    Source = StripQuotes(node.Attributes.GetValueOrDefault("Path", "")),
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
        private static ImageMetadata EvalImageLoad(Node node, WidgetRect widgetRect)
        {
            string path = StripQuotes(node.Attributes.GetValueOrDefault("Path", ""));
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

        private static ImageMetadata? EvalImageScale(
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            // Find the image input.
            var imgSocket = node.Sockets.FirstOrDefault(s => s.Name == "In" && s.Type == SocketType.Input);
            if (imgSocket is null) return new ImageMetadata { HasError = true, ErrorMessage = "Image.Scale missing 'In' socket" };

            var imgLink = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == imgSocket.Id);
            if (imgLink is null) return new ImageMetadata { HasError = true, ErrorMessage = "Image.Scale 'In' socket not connected" };

            var upstream = EvalImage(graph, imgLink.FromNodeId, imgLink.FromSocketId, layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            // Factor: scalar input or attribute fallback.
            // JS / C# parity. compositor.js falls back silently to the Factor
            // attribute when the Factor socket's Math chain returns null (cycle,
            // missing upstream), surfacing the error via node._errorGlyph rather
            // than blocking the render. C# previously hard-errored here, so a graph
            // that rendered fine in OBS would show a HasError stub at design time.
            // Mirror the JS behavior: warn via GlobalLogger and fall through to the
            // attribute value so the design-time preview stays in sync with runtime.
            bool factorWired = node.Sockets.Any(s => s.Name == "Factor" && s.Type == SocketType.Input)
                && graph.Links.Any(l => l.ToNodeId == node.Id &&
                    node.Sockets.Any(s => s.Id == l.ToSocketId && s.Name == "Factor"));
            double? wired = factorWired
                ? ResolveScalarSocket(graph, node, "Factor", layerRes, visiting, visited)
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
        private static double? ResolveScalarSocket(Graph graph, Node node, string socketName,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == socketName && s.Type == SocketType.Input);
            if (sock is null) return null;
            var link = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == sock.Id);
            if (link is null) return null;
            return EvalScalarNode(graph, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
        }

        private static double? EvalScalarNode(Graph graph, string nodeId,
            string? fromSocketId,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            if (!visiting.Add(nodeId)) return null; // cycle — bail
            visited.Add(nodeId);
            try
            {
                var src = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
                if (src is null) return null;

                switch (src.Title)
                {
                    case "Scalar.Constant":
                        return ParseDouble(src.Attributes.GetValueOrDefault("Value", "0"));
                    case "Math.Add":   return Binary(graph, src, "A", "B", layerRes, visiting, visited, (a, b) => a + b);
                    case "Math.Sub":   return Binary(graph, src, "A", "B", layerRes, visiting, visited, (a, b) => a - b);
                    case "Math.Mul":   return Binary(graph, src, "A", "B", layerRes, visiting, visited, (a, b) => a * b);
                    // Match compositor.js EXACTLY. The browser runtime
                    // (the source of truth for what the streamer sees) does
                    // `b === 0 ? 0 : a / b` (evalMathBinary). This C# design-time
                    // mirror previously returned NaN and the comment wrongly claimed
                    // JS emits Infinity/NaN — it does not. Returning 0 keeps the
                    // design-time preview identical to the OBS render.
                    case "Math.Div":   return Binary(graph, src, "A", "B", layerRes, visiting, visited, (a, b) => b == 0 ? 0 : a / b);
                    case "Math.Lerp":
                    {
                        double? a = ResolveScalarSocket(graph, src, "A", layerRes, visiting, visited);
                        double? b = ResolveScalarSocket(graph, src, "B", layerRes, visiting, visited);
                        double? t = ResolveScalarSocket(graph, src, "T", layerRes, visiting, visited);
                        if (a is null || b is null || t is null) return null;
                        return a.Value + (b.Value - a.Value) * t.Value;
                    }
                    case "Math.Clamp":
                    {
                        double? v   = ResolveScalarSocket(graph, src, "V",   layerRes, visiting, visited);
                        double? mn  = ResolveScalarSocket(graph, src, "Min", layerRes, visiting, visited);
                        double? mx  = ResolveScalarSocket(graph, src, "Max", layerRes, visiting, visited);
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
                        double a = ResolveScalarOrAttr(graph, src, "A", "A", 0, layerRes, visiting, visited);
                        double b = ResolveScalarOrAttr(graph, src, "B", "B", 1, layerRes, visiting, visited);
                        return b == 0 ? 0 : a - b * System.Math.Floor(a / b);
                    }
                    case "Math.Pow":
                    {
                        double @base = ResolveScalarOrAttr(graph, src, "Base", "Base", 1, layerRes, visiting, visited);
                        double exp   = ResolveScalarOrAttr(graph, src, "Exp",  "Exp",  2, layerRes, visiting, visited);
                        return System.Math.Pow(@base, exp);
                    }
                    case "Math.Min":
                    {
                        double a = ResolveScalarOrAttr(graph, src, "A", "A", 0, layerRes, visiting, visited);
                        double b = ResolveScalarOrAttr(graph, src, "B", "B", 0, layerRes, visiting, visited);
                        return System.Math.Min(a, b);
                    }
                    case "Math.Max":
                    {
                        double a = ResolveScalarOrAttr(graph, src, "A", "A", 0, layerRes, visiting, visited);
                        double b = ResolveScalarOrAttr(graph, src, "B", "B", 0, layerRes, visiting, visited);
                        return System.Math.Max(a, b);
                    }
                    case "Math.Abs":
                        return System.Math.Abs(ResolveScalarOrAttr(graph, src, "V", "V", 0, layerRes, visiting, visited));
                    case "Math.Sqrt":
                    {
                        double v = ResolveScalarOrAttr(graph, src, "V", "V", 0, layerRes, visiting, visited);
                        return v < 0 ? 0 : System.Math.Sqrt(v);
                    }
                    case "Math.Floor":
                        return System.Math.Floor(ResolveScalarOrAttr(graph, src, "V", "V", 0, layerRes, visiting, visited));
                    case "Math.Ceil":
                        return System.Math.Ceiling(ResolveScalarOrAttr(graph, src, "V", "V", 0, layerRes, visiting, visited));
                    case "Math.Round":
                        // MidpointRounding.AwayFromZero matches JS Math.round (round-half-up).
                        return System.Math.Round(ResolveScalarOrAttr(graph, src, "V", "V", 0, layerRes, visiting, visited),
                            MidpointRounding.AwayFromZero);
                    case "Math.Sign":
                        return System.Math.Sign(ResolveScalarOrAttr(graph, src, "V", "V", 0, layerRes, visiting, visited));
                    case "Math.Negate":
                        return -ResolveScalarOrAttr(graph, src, "V", "V", 0, layerRes, visiting, visited);
                    case "Math.Sin":
                        return System.Math.Sin(ResolveScalarOrAttr(graph, src, "Degrees", "Degrees", 0, layerRes, visiting, visited) * System.Math.PI / 180);
                    case "Math.Cos":
                        return System.Math.Cos(ResolveScalarOrAttr(graph, src, "Degrees", "Degrees", 0, layerRes, visiting, visited) * System.Math.PI / 180);
                    case "Math.Tan":
                        return System.Math.Tan(ResolveScalarOrAttr(graph, src, "Degrees", "Degrees", 0, layerRes, visiting, visited) * System.Math.PI / 180);
                    case "Math.Remap":
                    {
                        double v      = ResolveScalarOrAttr(graph, src, "V",      "V",      0, layerRes, visiting, visited);
                        double inMin  = ResolveScalarOrAttr(graph, src, "InMin",  "InMin",  0, layerRes, visiting, visited);
                        double inMax  = ResolveScalarOrAttr(graph, src, "InMax",  "InMax",  1, layerRes, visiting, visited);
                        double outMin = ResolveScalarOrAttr(graph, src, "OutMin", "OutMin", 0, layerRes, visiting, visited);
                        double outMax = ResolveScalarOrAttr(graph, src, "OutMax", "OutMax", 1, layerRes, visiting, visited);
                        double d = inMax - inMin;
                        double t = d == 0 ? 0 : (v - inMin) / d;
                        return outMin + t * (outMax - outMin);
                    }
                    case "Math.Compare":
                    {
                        double a = ResolveScalarOrAttr(graph, src, "A", "A", 0, layerRes, visiting, visited);
                        double b = ResolveScalarOrAttr(graph, src, "B", "B", 0, layerRes, visiting, visited);
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
                        double freq = ResolveScalarOrAttr(graph, src, "Frequency", "Frequency", 1, layerRes, visiting, visited);
                        double amp  = ResolveScalarOrAttr(graph, src, "Amplitude", "Amplitude", 1, layerRes, visiting, visited);
                        double ph   = ResolveScalarOrAttr(graph, src, "Phase",     "Phase",     0, layerRes, visiting, visited);
                        double off  = ResolveScalarOrAttr(graph, src, "Offset",    "Offset",    0, layerRes, visiting, visited);
                        const double t = 0; // timeMs/1000 at design time.
                        return off + amp * System.Math.Sin(2 * System.Math.PI * (freq * t + ph));
                    }
                    case "Time.Sawtooth":
                    {
                        // P = max(Period, 1e-6); (t % P) / P with t = 0 -> 0.
                        // Period is resolved (wired-wins) for parity even though the
                        // ramp evaluates to 0 at the design-time origin.
                        double p = System.Math.Max(
                            ResolveScalarOrAttr(graph, src, "Period", "Period", 1, layerRes, visiting, visited), 1e-6);
                        const double t = 0; // timeMs/1000 at design time.
                        return (t % p) / p;
                    }
                    case "Time.Easing":
                    {
                        // REUSE KeyframeInterpolation.ApplyCurve so the easing formulas
                        // stay single-sourced with the keyframe sampler. Mode maps 1:1
                        // onto KeyframeCurve (Linear/EaseIn/EaseOut/EaseInOut/Step).
                        double tIn = ResolveScalarOrAttr(graph, src, "T", "T", 0, layerRes, visiting, visited);
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
                        string s = ResolveStringOrAttr(graph, src, "In", "In", "", layerRes, visiting, visited);
                        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                    }
                    case "String.Length":
                    {
                        string s = ResolveStringOrAttr(graph, src, "In", "In", "", layerRes, visiting, visited);
                        return s.Length;
                    }

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
                        var vec = ResolveVectorInputSocket(graph, src, "V", splitLen, layerRes, visiting, visited);
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
            finally { visiting.Remove(nodeId); }
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
            var visiting = new HashSet<string>();
            var visited  = new List<string>();
            return EvalVectorNode(graph, nodeId, layerResolution, visiting, visited);
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
            var visiting = new HashSet<string>();
            var visited  = new List<string>();
            return EvalScalarNode(graph, nodeId, null, layerResolution, visiting, visited);
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
            var visiting = new HashSet<string>();
            var visited  = new List<string>();
            return EvalStringNode(graph, nodeId, null, layerResolution, visiting, visited);
        }

        private static double[]? ResolveVectorInputSocket(Graph graph, Node node, string socketName,
            int expectedLength,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == socketName && s.Type == SocketType.Input);
            if (sock is null) return null;
            var link = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == sock.Id);
            if (link is null) return null;

            // Try the vector evaluator first.
            var vec = EvalVectorNode(graph, link.FromNodeId, layerRes, visiting, visited);
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
            var scalar = EvalScalarNode(graph, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
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

        private static double[]? EvalVectorNode(Graph graph, string nodeId,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            if (!visiting.Add(nodeId)) return null; // cycle — bail
            visited.Add(nodeId);
            try
            {
                var src = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
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
                        double? x = ResolveScalarSocket(graph, src, "X", layerRes, visiting, visited);
                        double? y = ResolveScalarSocket(graph, src, "Y", layerRes, visiting, visited);
                        if (x is null || y is null) return null;
                        return new[] { x.Value, y.Value };
                    }
                    case "Vector3.Combine":
                    {
                        double? x = ResolveScalarSocket(graph, src, "X", layerRes, visiting, visited);
                        double? y = ResolveScalarSocket(graph, src, "Y", layerRes, visiting, visited);
                        double? z = ResolveScalarSocket(graph, src, "Z", layerRes, visiting, visited);
                        if (x is null || y is null || z is null) return null;
                        return new[] { x.Value, y.Value, z.Value };
                    }
                    case "Vector4.Combine":
                    {
                        double? x = ResolveScalarSocket(graph, src, "X", layerRes, visiting, visited);
                        double? y = ResolveScalarSocket(graph, src, "Y", layerRes, visiting, visited);
                        double? z = ResolveScalarSocket(graph, src, "Z", layerRes, visiting, visited);
                        double? w = ResolveScalarSocket(graph, src, "W", layerRes, visiting, visited);
                        if (x is null || y is null || z is null || w is null) return null;
                        return new[] { x.Value, y.Value, z.Value, w.Value };
                    }

                    // ── Per-component lerps ─────────────────────────────────
                    // Mirrors the scalar Math.Lerp path: a + (b - a) * t, applied per
                    // component. T is a scalar shared across all components.
                    case "Math.LerpVector2":
                        return LerpVectorN(graph, src, expectedLength: 2, layerRes, visiting, visited);
                    case "Math.LerpVector3":
                        return LerpVectorN(graph, src, expectedLength: 3, layerRes, visiting, visited);
                    case "Math.LerpVector4":
                        return LerpVectorN(graph, src, expectedLength: 4, layerRes, visiting, visited);

                    // ── Layer resolution (vector parity) ────────────────────
                    case "Math.Resolution":
                        return new[] { (double)layerRes.Width, (double)layerRes.Height };
                }
                return null;
            }
            finally { visiting.Remove(nodeId); }
        }

        private static double[]? LerpVectorN(Graph graph, Node node, int expectedLength,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            var a = ResolveVectorInputSocket(graph, node, "A", expectedLength, layerRes, visiting, visited);
            var b = ResolveVectorInputSocket(graph, node, "B", expectedLength, layerRes, visiting, visited);
            double? t = ResolveScalarSocket(graph, node, "T", layerRes, visiting, visited);
            if (a is null || b is null || t is null) return null;
            if (a.Length < expectedLength || b.Length < expectedLength) return null;
            var r = new double[expectedLength];
            for (int i = 0; i < expectedLength; i++)
                r[i] = a[i] + (b[i] - a[i]) * t.Value;
            return r;
        }

        private static double? Binary(Graph graph, Node node, string aName, string bName,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited, System.Func<double, double, double> op)
        {
            double? a = ResolveScalarSocket(graph, node, aName, layerRes, visiting, visited);
            double? b = ResolveScalarSocket(graph, node, bName, layerRes, visiting, visited);
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
        private static string? EvalStringNode(Graph graph, string nodeId,
            string? fromSocketId,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            if (!visiting.Add(nodeId)) return null; // cycle — bail
            visited.Add(nodeId);
            try
            {
                var src = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
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
                        string a = ResolveStringOrAttr(graph, src, "A", "A", "", layerRes, visiting, visited);
                        string b = ResolveStringOrAttr(graph, src, "B", "B", "", layerRes, visiting, visited);
                        return a + b;
                    }
                    case "String.Upper":
                        return ResolveStringOrAttr(graph, src, "In", "In", "", layerRes, visiting, visited).ToUpperInvariant();
                    case "String.Lower":
                        return ResolveStringOrAttr(graph, src, "In", "In", "", layerRes, visiting, visited).ToLowerInvariant();
                    case "String.Slice":
                    {
                        string s = ResolveStringOrAttr(graph, src, "In", "In", "", layerRes, visiting, visited);
                        int len = s.Length;
                        double startD = ResolveScalarOrAttr(graph, src, "Start", "Start", 0,  layerRes, visiting, visited);
                        double countD = ResolveScalarOrAttr(graph, src, "Count", "Count", -1, layerRes, visiting, visited);
                        int st = System.Math.Clamp((int)startD, 0, len);
                        int n  = countD < 0 ? len - st : System.Math.Max(0, (int)countD);
                        if (st + n > len) n = len - st;
                        return s.Substring(st, n);
                    }
                    case "String.Replace":
                    {
                        string s    = ResolveStringOrAttr(graph, src, "In",   "In",   "", layerRes, visiting, visited);
                        string find = ResolveStringOrAttr(graph, src, "Find", "Find", "", layerRes, visiting, visited);
                        string with = ResolveStringOrAttr(graph, src, "With", "With", "", layerRes, visiting, visited);
                        if (string.IsNullOrEmpty(find)) return s; // Find=="" -> unchanged.
                        return s.Replace(find, with);
                    }

                    // ── Convert nodes ───────────────────────────────────────
                    case "Convert.NumberToString":
                    {
                        double v = ResolveScalarOrAttr(graph, src, "V", "V", 0, layerRes, visiting, visited);
                        int decimals = (int)ParseDouble(src.Attributes.GetValueOrDefault("Decimals", "0"));
                        decimals = System.Math.Clamp(decimals, 0, 6);
                        return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
                    }
                    case "Convert.ColorFromRGBA":
                    {
                        int r = ClampByte(ResolveScalarOrAttr(graph, src, "R", "R", 255, layerRes, visiting, visited));
                        int g = ClampByte(ResolveScalarOrAttr(graph, src, "G", "G", 255, layerRes, visiting, visited));
                        int b = ClampByte(ResolveScalarOrAttr(graph, src, "B", "B", 255, layerRes, visiting, visited));
                        int a = ClampByte(ResolveScalarOrAttr(graph, src, "A", "A", 255, layerRes, visiting, visited));
                        return $"#{r:x2}{g:x2}{b:x2}{a:x2}"; // alpha last.
                    }
                    case "Convert.HexToColor":
                    {
                        string hex = ResolveStringOrAttr(graph, src, "Hex", "Hex", "#ffffff", layerRes, visiting, visited);
                        return NormalizeHexColor(hex);
                    }

                    // ── Message.Read — the read-out / trigger node ──────────
                    // Pure-data String producer. Design-time returns the MockValue
                    // attribute so the canvas/preview is not blind to the transmitted
                    // string (the JS side reads triggerContext.eventData[Key]).
                    case "Message.Read":
                        return StripQuotes(src.Attributes.GetValueOrDefault("MockValue", ""));

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
            finally { visiting.Remove(nodeId); }
        }

        // String socket resolver with attribute fallback. Honours the
        // "wired socket wins" contract: walk the String link if present (and it
        // resolves), then fall back to the (quote-stripped) attribute. Mirrors
        // ResolveScalarOrAttr for the string world.
        private static string ResolveStringOrAttr(
            Graph graph, Node node, string socketName, string attrKey, string defaultValue,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == socketName && s.Type == SocketType.Input);
            if (sock is not null)
            {
                var link = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == sock.Id);
                if (link is not null)
                {
                    var resolved = EvalStringNode(graph, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
                    if (resolved is not null) return resolved;
                }
            }
            return StripQuotes(node.Attributes.GetValueOrDefault(attrKey, defaultValue));
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
            Graph graph, Node node, string imageInputName,
            LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited,
            string kernelName)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == imageInputName && s.Type == SocketType.Input);
            if (sock is null) return new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = kernelName };
            var link = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == sock.Id);
            if (link is null) return new ImageMetadata { Width = widgetRect.Width, Height = widgetRect.Height, Kernel = kernelName };
            return EvalImage(graph, link.FromNodeId, link.FromSocketId, layerRes, widgetRect, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double[]? rect = ResolveVectorInputSocket(graph, node, "Rect", 4, layerRes, visiting, visited);
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
            Graph graph, Node node, string imageInputName,
            LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == imageInputName && s.Type == SocketType.Input);
            if (sock is null) return new ImageMetadata { HasError = true, ErrorMessage = $"{node.Title} missing '{imageInputName}' socket" };
            var link = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == sock.Id);
            if (link is null) return new ImageMetadata { HasError = true, ErrorMessage = $"{node.Title} '{imageInputName}' socket not connected" };
            return EvalImage(graph, link.FromNodeId, link.FromSocketId, layerRes, widgetRect, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            // Translate / Scale are Vector2 sockets; the attribute fallbacks
            // are split as <name>X / <name>Y so a half-edited node still has a
            // sensible value per component.
            (double x, double y) translate = ResolveVector2OrAttrPair(
                graph, node, "Translate", "TranslateX", "TranslateY", 0, 0, layerRes, visiting, visited);
            (double x, double y) scale     = ResolveVector2OrAttrPair(
                graph, node, "Scale",     "ScaleX",     "ScaleY",     1, 1, layerRes, visiting, visited);
            double rotation = ResolveScalarOrAttr(
                graph, node, "Rotate", "Rotation", 0, layerRes, visiting, visited);

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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double brightness = ResolveScalarOrAttr(graph, node, "Brightness", "Brightness", 0, layerRes, visiting, visited);
            double contrast   = ResolveScalarOrAttr(graph, node, "Contrast",   "Contrast",   0, layerRes, visiting, visited);
            double saturation = ResolveScalarOrAttr(graph, node, "Saturation", "Saturation", 0, layerRes, visiting, visited);
            double hue        = ResolveScalarOrAttr(graph, node, "Hue",        "Hue",        0, layerRes, visiting, visited);

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
            Graph graph, Node node, string socketName, string attrKey, double defaultValue,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == socketName && s.Type == SocketType.Input);
            if (sock is not null)
            {
                var link = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == sock.Id);
                if (link is not null)
                {
                    var resolved = EvalScalarNode(graph, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
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
            Graph graph, Node node, string socketName,
            string xAttrKey, string yAttrKey,
            double defaultX, double defaultY,
            LayerResolution layerRes,
            HashSet<string> visiting, List<string> visited)
        {
            var sock = node.Sockets.FirstOrDefault(s => s.Name == socketName && s.Type == SocketType.Input);
            if (sock is not null)
            {
                var link = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == sock.Id);
                if (link is not null)
                {
                    // Try vector evaluator first, then scalar-broadcast as a
                    // compatibility shim (mirrors ResolveVectorInputSocket /
                    // compositor.js _evalVectorSocket broadcast semantics).
                    var vec = EvalVectorNode(graph, link.FromNodeId, layerRes, visiting, visited);
                    if (vec is not null && vec.Length >= 2)
                        return (vec[0], vec[1]);
                    var scalar = EvalScalarNode(graph, link.FromNodeId, link.FromSocketId, layerRes, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double radius = ResolveScalarOrAttr(graph, node, "Radius", "Radius", 0, layerRes, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double sx = ResolveScalarOrAttr(graph, node, "SigmaX", "SigmaX", 0, layerRes, visiting, visited);
            double sy = ResolveScalarOrAttr(graph, node, "SigmaY", "SigmaY", 0, layerRes, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double tileSize = ResolveScalarOrAttr(graph, node, "TileSize", "TileSize", 8, layerRes, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double ox   = ResolveScalarOrAttr(graph, node, "OffsetX", "OffsetX", 4, layerRes, visiting, visited);
            double oy   = ResolveScalarOrAttr(graph, node, "OffsetY", "OffsetY", 4, layerRes, visiting, visited);
            double blur = ResolveScalarOrAttr(graph, node, "Blur",    "Blur",    6, layerRes, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double radius    = ResolveScalarOrAttr(graph, node, "Radius",    "Radius",    12, layerRes, visiting, visited);
            double intensity = ResolveScalarOrAttr(graph, node, "Intensity", "Intensity", 1,  layerRes, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            double amp  = ResolveScalarOrAttr(graph, node, "Amplitude", "Amplitude", 8, layerRes, visiting, visited);
            double freq = ResolveScalarOrAttr(graph, node, "Frequency", "Frequency", 4, layerRes, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            // Image input is required.
            var image = EvalImagePassthrough(graph, node, "Image", layerRes, widgetRect, visiting, visited);
            if (image is null || image.HasError) return image;

            // Mask input is also required — surface an explicit error rather than
            // silently producing the unmasked image (which would mislead authors).
            var maskSock = node.Sockets.FirstOrDefault(s => s.Name == "Mask" && s.Type == SocketType.Input);
            var maskLink = maskSock is null
                ? null
                : graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == maskSock.Id);
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
            var mask = EvalImage(graph, maskLink.FromNodeId, maskLink.FromSocketId, layerRes, widgetRect, visiting, visited);
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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            // A blend composites B (top) over A (bottom). A missing / empty /
            // errored side contributes nothing — return the OTHER layer rather than
            // collapsing the whole blend to an error (the bug: an unfilled
            // Text.Render caption over a loaded image showed "load failed" and the
            // node body read "(no input)" even though A was a valid image). Only
            // surface an error when NEITHER side yields an image. Mirrors
            // evalImageBlend in compositor.js.
            var a = EvalImagePassthrough(graph, node, "A", layerRes, widgetRect, visiting, visited);
            var b = EvalImagePassthrough(graph, node, "B", layerRes, widgetRect, visiting, visited);
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
            double opacity = ResolveScalarOrAttr(graph, node, "Opacity", "Opacity", 1, layerRes, visiting, visited);

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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            // Same dual-input contract as Image.Blend (A bottom, B top required)
            // but Mode covers blend modes AND alpha/luminance key modes in one
            // node. Shape generators feeding either socket "just work" because
            // they're typed Image.
            // Same empty-tolerance as Image.Blend: a missing/empty side contributes
            // nothing, so return the other rather than failing the whole node.
            var a = EvalImagePassthrough(graph, node, "A", layerRes, widgetRect, visiting, visited);
            var b = EvalImagePassthrough(graph, node, "B", layerRes, widgetRect, visiting, visited);
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
            double opacity = ResolveScalarOrAttr(graph, node, "Opacity", "Opacity", 1, layerRes, visiting, visited);

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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            var upstream = EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
            if (upstream is null || upstream.HasError) return upstream;

            (double rx, double ry) = ResolveVector2OrAttrPair(
                graph, node, "Repeat", "RepeatX", "RepeatY", 1, 1, layerRes, visiting, visited);

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
            Graph graph, Node node, LayerResolution layerRes, WidgetRect widgetRect,
            HashSet<string> visiting, List<string> visited)
        {
            string when    = StripQuotes(node.Attributes.GetValueOrDefault("When",   "\"\""));
            string expectA = StripQuotes(node.Attributes.GetValueOrDefault("Equals", "\"\""));

            // Equals can also arrive via a wired String input; the wired value wins
            // when present so a Math/Text chain can drive the comparison dynamically.
            var eqSock = node.Sockets.FirstOrDefault(s => s.Name == "Equals" && s.Type == SocketType.Input);
            if (eqSock is not null)
            {
                var eqLink = graph.Links.FirstOrDefault(l => l.ToNodeId == node.Id && l.ToSocketId == eqSock.Id);
                if (eqLink is not null)
                {
                    var src = graph.Nodes.FirstOrDefault(n => n.Id == eqLink.FromNodeId);
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
            return EvalImagePassthrough(graph, node, "In", layerRes, widgetRect, visiting, visited);
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
