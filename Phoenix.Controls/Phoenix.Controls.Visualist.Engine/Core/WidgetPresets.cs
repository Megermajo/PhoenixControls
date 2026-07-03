using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// WidgetPresets — maps a widget preset to a starting <see cref="Graph"/>
    /// for its <c>onStartup</c> trigger. Phase 3: every preset returns a graph containing
    /// at minimum a <c>Display</c> sink (auto-injected). Phase 4 will replace these with
    /// preset-specific graph templates (e.g., Image preset → <c>Image.Load → Display</c>).
    /// </summary>
    public static class WidgetPresets
    {
        public static Graph GetStartingGraph(WidgetPreset? preset)
        {
            var graph = new Graph { Name = "onStartup" };
            var sink  = DisplaySinkNode.Build();
            graph.Nodes.Add(sink);

            // CC preset has its own multi-node chain: Caption.LiveCaption.Translated → Text.Render → Display
            if (preset == WidgetPreset.CC)
            {
                BuildCcChain(graph, sink);
                return graph;
            }

            // Text preset wires a Text.Render directly into Display so the user
            // can author copy without first dragging Text.Render onto the canvas.
            if (preset == WidgetPreset.Text)
            {
                BuildTextChain(graph, sink);
                return graph;
            }

            // Audio preset: Audio.Load → Audio.Play with Loop=true. Display sink is
            // auto-injected but stays unwired (Audio is a sibling sink, not an Image source).
            if (preset == WidgetPreset.Audio)
            {
                BuildAudioChain(graph);
                return graph;
            }

            // Chat preset: Visual.OnTrigger.Message → Text.Render.Text → Display.
            // The Visualist registry has no chat-fetching node by design (Twitch is Hub-only,
            // banned from WidgetNodeRegistry). The Chat widget is therefore a *renderer*: Hub
            // pushes a VISUAL_TRIGGER carrying the chat line and Visual.OnTrigger surfaces it.
            // Authors can chain Text.Translate or swap Message for UserName to reshape the
            // rendered string.
            if (preset == WidgetPreset.Chat)
            {
                BuildChatChain(graph, sink);
                return graph;
            }

            // Seed preset-specific source nodes wired into the Display sink.
            // WebSource gains its starting graph (the runtime kernel
            // landed in compositor.js). Particles
            // gains its starting graph likewise (per-widget rAF emitter loop).
            Node? source = preset switch
            {
                WidgetPreset.Image     => WidgetNodeRegistry.Get("Image.Load")     != null ? WidgetNodeRegistry.Instantiate("Image.Load",     new System.Drawing.Point(120, 120)) : null,
                WidgetPreset.Video     => WidgetNodeRegistry.Get("Image.LoadUrl")  != null ? WidgetNodeRegistry.Instantiate("Image.LoadUrl",  new System.Drawing.Point(120, 120)) : null,
                WidgetPreset.WebSource => WidgetNodeRegistry.Get("WebSource")      != null ? WidgetNodeRegistry.Instantiate("WebSource",      new System.Drawing.Point(120, 120)) : null,
                WidgetPreset.Particles => WidgetNodeRegistry.Get("Particles.Emit") != null ? WidgetNodeRegistry.Instantiate("Particles.Emit", new System.Drawing.Point(120, 120)) : null,
                _                      => null,
            };

            if (source is not null)
            {
                graph.Nodes.Add(source);
                var srcOut  = source.Sockets.Find(s => s.Type == SocketType.Output);
                var sinkIn  = sink.Sockets.Find(s => s.Type == SocketType.Input);
                if (srcOut is not null && sinkIn is not null)
                {
                    graph.Links.Add(new Link
                    {
                        FromNodeId   = source.Id,
                        FromSocketId = srcOut.Id,
                        ToNodeId     = sink.Id,
                        ToSocketId   = sinkIn.Id,
                    });
                }
            }

            return graph;
        }

        /// <summary>
        /// Text preset chain: Text.Render → Display.
        /// </summary>
        private static void BuildTextChain(Graph graph, Node sink)
        {
            if (WidgetNodeRegistry.Get("Text.Render") is null) return;

            var render = WidgetNodeRegistry.Instantiate("Text.Render", new System.Drawing.Point(120, 120));
            graph.Nodes.Add(render);

            var renderImage = render.Sockets.Find(s => s.Type == SocketType.Output && s.Name == "Image");
            var sinkIn      = sink.Sockets.Find(s => s.Type == SocketType.Input);
            if (renderImage is not null && sinkIn is not null)
                graph.Links.Add(new Link
                {
                    FromNodeId = render.Id, FromSocketId = renderImage.Id,
                    ToNodeId   = sink.Id,   ToSocketId   = sinkIn.Id,
                });
        }

        /// <summary>
        /// CC preset chain: Caption.LiveCaption (Translated output) → Text.Render → Display.
        /// Author can swap the wired output to "Text" instead of "Translated" to disable translation
        /// per widget, or chain a Text.Translate node to override the language at the graph level.
        /// </summary>
        private static void BuildCcChain(Graph graph, Node sink)
        {
            if (WidgetNodeRegistry.Get("Caption.LiveCaption") is null) return;
            if (WidgetNodeRegistry.Get("Text.Render")         is null) return;

            var caption = WidgetNodeRegistry.Instantiate("Caption.LiveCaption", new System.Drawing.Point(80, 120));
            var render  = WidgetNodeRegistry.Instantiate("Text.Render",         new System.Drawing.Point(360, 120));
            graph.Nodes.Add(caption);
            graph.Nodes.Add(render);

            // Caption.LiveCaption.Translated → Text.Render.Text
            var translated = caption.Sockets.Find(s => s.Type == SocketType.Output && s.Name == "Translated");
            var renderText = render.Sockets.Find(s => s.Type == SocketType.Input  && s.Name == "Text");
            if (translated is not null && renderText is not null)
                graph.Links.Add(new Link
                {
                    FromNodeId = caption.Id, FromSocketId = translated.Id,
                    ToNodeId   = render.Id,  ToSocketId   = renderText.Id,
                });

            // Text.Render.Image → Display.<first input>
            var renderImage = render.Sockets.Find(s => s.Type == SocketType.Output && s.Name == "Image");
            var sinkIn      = sink.Sockets.Find(s => s.Type == SocketType.Input);
            if (renderImage is not null && sinkIn is not null)
                graph.Links.Add(new Link
                {
                    FromNodeId = render.Id, FromSocketId = renderImage.Id,
                    ToNodeId   = sink.Id,   ToSocketId   = sinkIn.Id,
                });
        }

        /// <summary>
        /// Audio preset chain: <c>Audio.Load → Audio.Play</c> with looping enabled.
        /// The graph keeps the auto-injected <c>Display</c> sink so visual chains can be
        /// added later without restructuring (e.g. an authored Image.Load wired into
        /// Display alongside the audio loop). Audio.Play's <c>Loop</c> attribute is
        /// flipped to <c>"true"</c> to match the "looping ambient SFX" use case; one-shot
        /// authors flip it back.
        /// </summary>
        private static void BuildAudioChain(Graph graph)
        {
            if (WidgetNodeRegistry.Get("Audio.Load") is null) return;

            var load = WidgetNodeRegistry.Instantiate("Audio.Load", new System.Drawing.Point(120, 240));
            var play = AudioSinkNode.Build();
            play.Location = new System.Drawing.Point(420, 240);
            // Default the preset to looping ambient playback. Authors flip back for one-shots.
            play.Attributes["Loop"] = "true";
            graph.Nodes.Add(load);
            graph.Nodes.Add(play);

            var loadAudio = load.Sockets.Find(s => s.Type == SocketType.Output && s.Name == "Audio");
            var playAudio = play.Sockets.Find(s => s.Type == SocketType.Input  && s.Name == "Audio");
            if (loadAudio is not null && playAudio is not null)
                graph.Links.Add(new Link
                {
                    FromNodeId = load.Id, FromSocketId = loadAudio.Id,
                    ToNodeId   = play.Id, ToSocketId   = playAudio.Id,
                });
        }

        /// <summary>
        /// Chat preset chain: <c>Visual.OnTrigger.Message → Text.Render.Text → Display</c>.
        /// The Hub pushes a chat line via <c>VISUAL_TRIGGER</c>; <c>Visual.OnTrigger</c> exposes
        /// the payload to the visual graph. Visualist cannot fetch chat directly (Twitch /
        /// Streamer.bot integration is Architect/Hub-only — see WidgetNodeRegistry.ForbiddenCategories),
        /// so this preset is strictly a renderer. Authors can swap the wired output (e.g.
        /// <c>UserName</c> instead of <c>Message</c>) or chain a <c>Text.Translate</c> ahead of
        /// Text.Render to localise the rendered line.
        /// </summary>
        private static void BuildChatChain(Graph graph, Node sink)
        {
            if (WidgetNodeRegistry.Get("Visual.OnTrigger") is null) return;
            if (WidgetNodeRegistry.Get("Text.Render")      is null) return;

            var trigger = WidgetNodeRegistry.Instantiate("Visual.OnTrigger", new System.Drawing.Point(80, 120));
            var render  = WidgetNodeRegistry.Instantiate("Text.Render",       new System.Drawing.Point(360, 120));
            graph.Nodes.Add(trigger);
            graph.Nodes.Add(render);

            // Visual.OnTrigger.Message → Text.Render.Text
            var message    = trigger.Sockets.Find(s => s.Type == SocketType.Output && s.Name == "Message");
            var renderText = render.Sockets.Find(s => s.Type == SocketType.Input  && s.Name == "Text");
            if (message is not null && renderText is not null)
                graph.Links.Add(new Link
                {
                    FromNodeId = trigger.Id, FromSocketId = message.Id,
                    ToNodeId   = render.Id,  ToSocketId   = renderText.Id,
                });

            // Text.Render.Image → Display.<first input>
            var renderImage = render.Sockets.Find(s => s.Type == SocketType.Output && s.Name == "Image");
            var sinkIn      = sink.Sockets.Find(s => s.Type == SocketType.Input);
            if (renderImage is not null && sinkIn is not null)
                graph.Links.Add(new Link
                {
                    FromNodeId = render.Id, FromSocketId = renderImage.Id,
                    ToNodeId   = sink.Id,   ToSocketId   = sinkIn.Id,
                });
        }

        /// <summary>Build a non-removable Display sink node — every trigger's graph terminates here.</summary>
        public static Node BuildDisplaySinkNode() => DisplaySinkNode.Build();
    }
}
