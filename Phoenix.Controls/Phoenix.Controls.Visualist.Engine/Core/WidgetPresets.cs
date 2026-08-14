using System;
using System.Collections.Generic;
using System.Globalization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

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

            // AlertBox (V8): onStartup is DELIBERATELY the bare Display sink.
            //
            // An alert box is invisible until an alert fires, and compositor.js reverts a
            // widget to its onStartup graph when the hold expires — so onStartup IS the
            // idle state, and the idle state of an alert box is "nothing". Compiling the
            // alert chain into onStartup instead would paint the FALLBACK image (the
            // String.Select Default row) permanently, because onStartup renders with no
            // eventData at all: Visual.Arg yields "", nothing matches, and the default is
            // emitted. A streamer would see their generic alert graphic burned onto the
            // overlay forever.
            //
            // The compiled chain therefore lives on a SEPARATE onTrigger:<id> trigger —
            // see CompileAlertBoxGraph / RegenerateAlertBox. This early return exists so
            // that intent is stated rather than inherited from the `_ => null` arm below.
            if (preset == WidgetPreset.AlertBox)
            {
                return graph;
            }

            // Player (V15): one Player.Embed sink, and the Display sink stays UNWIRED.
            //
            // Unwired is not an omission — it is the only correct wiring. Player.Embed
            // mounts a cross-origin iframe on the DOM-overlay track, which the browser
            // draws over the canvas; it produces no Image, so there is nothing to link
            // into Display. Exactly the Audio preset's shape (BuildAudioChain likewise
            // leaves the auto-injected sink alone), and compositor.js suppresses the
            // "no Image input" hint card for a widget whose only content is a DOM sink,
            // so the unwired Display costs the author nothing on screen.
            //
            // onStartup rather than a compiled onTrigger — the OPPOSITE of AlertBox, for
            // the opposite reason. An alert box is idle-blank until something fires; the
            // queue-fed player's job is to be mounted and following the songrequest.*
            // channel from the moment the source opens, and its own idle state (state
            // "idle" / an empty video_id) is handled inside the runtime. An author
            // building the clip-shoutout variant flips Source to "clip" and moves the
            // node onto an onTrigger:<id> graph, which the ordinary revert then tears
            // down when the hold expires.
            if (preset == WidgetPreset.Player)
            {
                BuildPlayerChain(graph);
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
        /// V15 Player preset: ONE <c>Player.Embed</c> sink, seeded at the queue-fed
        /// <c>songrequest</c> source, and nothing wired into <c>Display</c>.
        ///
        /// <para>There is no chain to build because there is no data to route: the node
        /// pulls the <c>songrequest.*</c> live keys itself (queue-fed) or reads its own
        /// <c>Clip</c> input (one-shot), and it emits no Image. This helper exists so the
        /// preset SEEDS the sink rather than dropping a blank widget the author has to
        /// discover a node for — the same service <see cref="BuildAudioChain"/> performs
        /// for Audio.Play.</para>
        ///
        /// <para>Silent no-op when the template is absent, matching every sibling builder:
        /// a registry that failed to register is a startup fault, not something a preset
        /// spawn should throw over.</para>
        /// </summary>
        private static void BuildPlayerChain(Graph graph)
        {
            if (WidgetNodeRegistry.Get(PlayerEmbedSinkNode.Title) is null) return;

            var player = WidgetNodeRegistry.Instantiate(
                PlayerEmbedSinkNode.Title, new System.Drawing.Point(120, 240));
            graph.Nodes.Add(player);
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

        // ══ V8 — the Alert Box compiler ═══════════════════════════════════════
        //
        // The ONLY compiled preset: its onTrigger graph is generated from
        // AlertBoxSettings instead of being hand-authored. Everything below is the
        // one direction settings → graph; nothing ever reads a value back out of a
        // compiled graph (see AlertBoxSettings' remarks for why).

        /// <summary>What <see cref="RegenerateAlertBox"/> actually did. Every non-success
        /// arm is a REFUSAL rather than a throw: regeneration is reached from a settings
        /// commit, a preset apply and a widget spawn, and none of those may take the editor
        /// down. The caller logs and surfaces the reason.</summary>
        public enum AlertBoxRegenResult
        {
            /// <summary>The compiled trigger was installed / replaced.</summary>
            Regenerated,
            /// <summary>Not a <see cref="WidgetPreset.AlertBox"/> widget — nothing to do.</summary>
            NotAnAlertBox,
            /// <summary>The author detached this widget. One-way; the graph is theirs now.</summary>
            Detached,
            /// <summary><see cref="AlertBoxSettings.TriggerId"/> cannot form a valid trigger name.</summary>
            InvalidTriggerId,
            /// <summary>A node template the chain needs is not registered (registry not populated).</summary>
            RegistryUnavailable,
            /// <summary>
            /// The trigger name these settings resolve to is already taken by a trigger this
            /// compiler did NOT install, and that trigger carries a graph. Nothing was touched —
            /// the author renames the id or points the settings elsewhere.
            /// </summary>
            TriggerNameTaken,
            /// <summary>
            /// An unexpected exception was caught on the way through a commit. Distinct from
            /// <see cref="NotAnAlertBox"/> on purpose: a caught throw used to be reported as
            /// that, so the ONE outcome where the author most needs pointing at the System Log
            /// read as a statement about the wrong widget. Every surface that renders an
            /// outcome must say "something went wrong, check the log" for this.
            /// </summary>
            Failed,
            /// <summary>
            /// There was no open Visualist document to commit into, so the edit was dropped.
            /// <para><b>Why this is not <see cref="NotAnAlertBox"/> and not
            /// <see cref="Failed"/>.</b> It used to be the former, which every surface renders as
            /// "Not an Alert Box widget." — a false statement about the author's selection, on a
            /// path where the selection was fine. Reporting it as <see cref="Failed"/> would be
            /// the other half of the same problem: that wording sends the author to the System
            /// Log, and this arm wrote nothing to the log, so the one instruction the message
            /// gave led to an empty page. It is its own outcome, it logs, and its wording names
            /// the real cause — the layer was closed while an edit was still in flight (the
            /// media-row <c>Browse…</c> dialog is awaited, so its commit closure can outlive the
            /// document that owned it).</para>
            /// </summary>
            NoDocument,
        }

        /// <summary>
        /// What a preset apply actually DID — the one value every apply surface reports from,
        /// instead of each surface re-deriving it from something correlated.
        ///
        /// <para><b>★ Why this exists as an outcome rather than as booleans at the call sites.</b>
        /// Two separate defects came from re-derivation. (1) The preset gallery keyed
        /// "Applied … to …" versus "Spawned …" off whether it had been handed a target widget id
        /// at OPEN time — but the gallery is non-modal, so an author can delete that widget and
        /// then click Apply, at which point <c>ApplyPreset</c> finds no target, takes the spawn
        /// branch, and the strip claims a widget was converted. (2) A throw caught after the
        /// destructive half of an apply (undo pushed, preset flipped, <c>onStartup.Graph</c>
        /// replaced) was reported with the same "nothing changed" shape as a refusal, so the
        /// author read "not applied" over a wiped idle graph and never pressed Ctrl+Z.</para>
        ///
        /// <para>The apply method knows every answer first-hand; every surface reads this. Note
        /// that the spawn/apply split runs through BOTH halves — success and failure — because the
        /// recovery gesture differs on the failure side too (Ctrl+Z removes a half-built spawn, and
        /// restores a half-applied existing widget).</para>
        /// </summary>
        public enum PresetApplyOutcome
        {
            /// <summary>A NEW widget was created and seeded with the preset.</summary>
            Spawned,
            /// <summary>An EXISTING widget's preset + <c>onStartup</c> graph were replaced.</summary>
            Applied,
            /// <summary>
            /// Refused before anything was touched — the model is exactly as it was, no undo
            /// entry was consumed, and the paired refusal reason says why. This is the only
            /// outcome a surface may describe as "not applied".
            /// </summary>
            RefusedNoChange,
            /// <summary>
            /// Something threw AFTER an EXISTING widget had already been changed. The widget is in
            /// a half-applied state and the undo entry taken at the start of the apply is what
            /// takes it back, so a surface must say "partially applied — Ctrl+Z restores it"
            /// rather than "not applied".
            /// <para>Pairs with <see cref="FailedAfterSpawn"/>: both mean "a throw landed inside
            /// the post-change window", and they are separate because the RECOVERY differs.</para>
            /// </summary>
            FailedAfterChange,
            /// <summary>
            /// Something threw AFTER a NEW widget had already been spawned — i.e. the failure
            /// landed in the same post-change window as <see cref="FailedAfterChange"/>, but on the
            /// spawn branch.
            ///
            /// <para><b>★ Why this is not the same outcome as <see cref="FailedAfterChange"/>.</b>
            /// The advice is the opposite. On the apply branch Ctrl+Z RESTORES the widget's previous
            /// state, which is what the author wants to hear because their authored idle graph was
            /// replaced. On the spawn branch there IS no previous state: the widget was created by
            /// this apply, so Ctrl+Z REMOVES it. Reporting a failed spawn as
            /// <see cref="FailedAfterChange"/> tells the author to press Ctrl+Z "to restore" a
            /// widget that had just been created and that Ctrl+Z deletes — it points at a real
            /// gesture with an inverted description, which is worse than no advice. Same reason the
            /// success path keeps <see cref="Spawned"/> and <see cref="Applied"/> apart.</para>
            /// </summary>
            FailedAfterSpawn,
        }

        // Node titles the compiled chain needs. Named once so the guard, the builder and
        // the test all agree — a typo'd title here would make Get() return null and the
        // whole compile bail as "registry unavailable", which reads as an environment
        // problem rather than the code bug it would be.
        private const string ArgTitle     = "Visual.Arg";
        private const string SelectTitle  = "String.Select";
        private const string ImageTitle   = "Image.Load";
        private const string AudioTitle   = "Audio.Load";
        private const string TextTitle    = "Text.Render";
        private const string CombineTitle = "Image.Combine";

        /// <summary>
        /// Deterministic node id for one ROLE in one widget's compiled Alert Box graph.
        /// Same (seed, role) ⇒ same id, forever and across processes, so a recompile
        /// preserves the identity every authored keyframe path is keyed on.
        ///
        /// <para>Derived rather than stored because there is nowhere honest to store it:
        /// the settings blob is the author's document, and a saved id table would be one
        /// more thing that can go stale against the compiled graph. A pure function of
        /// (widget, role) cannot drift.</para>
        ///
        /// <para>Shape is a normal RFC-4122 v5-style GUID string, so it is
        /// indistinguishable from a minted one to every consumer (serializer, canvas
        /// index, keyframe path). Uniqueness across widgets comes from the seed being the
        /// widget id; ids only ever have to be unique WITHIN one graph anyway.</para>
        /// </summary>
        internal static string StableNodeId(string? identitySeed, string role)
        {
            // The namespace prefix keeps this id space disjoint from any other derived-id
            // scheme that might later hash the same (widget, role) pair.
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    "phoenix.alertbox" + (identitySeed ?? "") + "" + (role ?? "")));

            var bytes = new byte[16];
            Array.Copy(hash, bytes, 16);

            // RFC 4122: version 5 (name-based) in the high nibble of the 7th BIG-ENDIAN
            // byte, variant 10x in the top bits of the 9th.
            //
            // ★ The version byte is bytes[7], NOT bytes[6]. new Guid(byte[16]) reads the
            // first three fields LITTLE-ENDIAN on this platform — Data1 from bytes[0..3]
            // reversed, Data2 from bytes[4..5] reversed, Data3 from bytes[6..7] reversed —
            // so the nibble that prints as the version digit (first character of the third
            // group in the canonical string) comes from the HIGH byte of Data3, i.e.
            // bytes[7]. Writing bytes[6] set a nibble in the middle of the third group and
            // left the version reading as whatever the hash happened to produce, so the
            // value was not the v5 UUID the doc above claims. The variant byte is Data4[0],
            // which IS bytes[8] — that half was already right.
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

            return new Guid(bytes).ToString();
        }

        /// <summary>
        /// The compiler's roles, paired with the node title each one is built from.
        /// The two <c>String.Select</c> roles are deliberately absent — they share a title
        /// and are told apart by what they feed, in <see cref="MapRolesToExistingIds"/>.
        /// </summary>
        private static readonly (string Role, string Title)[] UniqueRoleTitles =
        {
            ("arg",          ArgTitle),
            ("image.load",   ImageTitle),
            ("audio.load",   AudioTitle),
            ("caption",      TextTitle),
            ("combine",      CombineTitle),
            ("sink.audio",   AudioSinkNode.Title),
            ("sink.display", DisplaySinkNode.Title),
        };

        /// <summary>
        /// Recovers role → node-id from an EXISTING compiled Alert Box graph, so authored
        /// keyframes can be carried onto the ids the next compile will mint.
        ///
        /// <para>Needed exactly once per widget, for graphs compiled BEFORE node identity
        /// became derived. Those carry <c>Guid.NewGuid()</c> ids that no amount of
        /// recomputation can reproduce, so the only way their keyframes survive the first
        /// recompile is to read the old ids back out of the graph and rewrite the paths.
        /// After that the ids already match and the whole pass is a no-op.</para>
        ///
        /// <para>Returns null unless the graph is EXACTLY the shape this compiler emits —
        /// one node per unique role and two <c>String.Select</c>s wired to the two loaders.
        /// A partial or unexpected match returns nothing rather than guessing: rewriting a
        /// keyframe path onto the wrong node would silently move an author's animation to
        /// a different parameter, which is worse than leaving it orphaned where they can
        /// see it.</para>
        /// </summary>
        private static Dictionary<string, string>? MapRolesToExistingIds(Graph? graph)
        {
            if (graph?.Nodes is not { Count: > 0 } nodes) return null;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach ((string role, string title) in UniqueRoleTitles)
            {
                Node? only = null;
                foreach (Node n in nodes)
                {
                    if (n is null || !string.Equals(n.Title, title, StringComparison.Ordinal)) continue;
                    if (only is not null) return null;   // duplicate ⇒ not our shape
                    only = n;
                }
                if (only is null) return null;           // missing ⇒ not our shape
                map[role] = only.Id;
            }

            // The two selects are identified by their CONSUMER: one feeds Image.Load's
            // Path, the other Audio.Load's. That is the only thing that distinguishes
            // them, and it is exactly how the compiler wires them.
            string imageLoadId = map["image.load"];
            string audioLoadId = map["audio.load"];

            string? selImg = FindUpstreamSelectId(graph, imageLoadId);
            string? selSnd = FindUpstreamSelectId(graph, audioLoadId);
            if (selImg is null || selSnd is null || string.Equals(selImg, selSnd, StringComparison.Ordinal))
                return null;

            map["select.image"] = selImg;
            map["select.sound"] = selSnd;
            return map;
        }

        /// <summary>
        /// The id of the <c>String.Select</c> feeding <paramref name="consumerNodeId"/>,
        /// or null when the wire is missing or the upstream node is something else.
        /// </summary>
        private static string? FindUpstreamSelectId(Graph graph, string consumerNodeId)
        {
            string? found = null;
            foreach (Link l in graph.Links)
            {
                if (l is null || !string.Equals(l.ToNodeId, consumerNodeId, StringComparison.Ordinal)) continue;

                Node? from = graph.Nodes.Find(n => n is not null && string.Equals(n.Id, l.FromNodeId, StringComparison.Ordinal));
                if (from is null || !string.Equals(from.Title, SelectTitle, StringComparison.Ordinal)) continue;

                if (found is not null && !string.Equals(found, from.Id, StringComparison.Ordinal))
                    return null;   // two different selects into one loader ⇒ not our shape
                found = from.Id;
            }
            return found;
        }

        /// <summary>
        /// Rewrites <paramref name="timeline"/>'s keyframe paths from the old node ids in
        /// <paramref name="oldRoleIds"/> to the ids <paramref name="identitySeed"/> derives,
        /// and returns how many keyframes moved.
        ///
        /// <para>Paths are <c>"&lt;nodeId&gt;.&lt;component&gt;"</c>
        /// (<c>AnimatedPinRegistry.MakeParameterPath</c>), so only the prefix up to the
        /// FIRST dot is touched — a component name containing dots (colour channels are
        /// <c>Color.R</c>) has to survive intact.</para>
        ///
        /// <para>A path whose prefix belongs to no known role is left ALONE. It is either
        /// already migrated or genuinely foreign, and in both cases inventing a new home
        /// for it would be worse than leaving it where the author can see it.</para>
        /// </summary>
        private static int MigrateKeyframePaths(WidgetTimeline? timeline,
                                                Dictionary<string, string> oldRoleIds,
                                                string identitySeed)
        {
            if (timeline?.Keyframes is not { Count: > 0 } frames) return 0;

            // old id → new id, skipping roles whose id already matches (the steady state
            // after the first migration, and the whole reason this is idempotent).
            var rewrite = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (role, oldId) in oldRoleIds)
            {
                string newId = StableNodeId(identitySeed, role);
                if (!string.Equals(oldId, newId, StringComparison.Ordinal))
                    rewrite[oldId] = newId;
            }
            if (rewrite.Count == 0) return 0;

            int moved = 0;
            foreach (Keyframe k in frames)
            {
                if (k is null) continue;
                string path = k.ParameterPath ?? "";
                int dot = path.IndexOf('.');
                if (dot <= 0) continue;

                string prefix = path.Substring(0, dot);
                if (!rewrite.TryGetValue(prefix, out string? newId)) continue;

                k.ParameterPath = newId + path.Substring(dot);
                moved++;
            }
            return moved;
        }

        /// <summary>
        /// True when every node template the compiled chain needs is registered. Exists so
        /// a caller can pre-flight the <see cref="AlertBoxRegenResult.RegistryUnavailable"/>
        /// refusal *before* it snapshots undo — the alternative (snapshot, try, roll back)
        /// is not available here, because <c>LayerDocument.Undo()</c> swaps in a freshly
        /// deserialized Layer and would orphan every live widget reference.
        /// </summary>
        public static bool AlertBoxTemplatesAvailable()
            => WidgetNodeRegistry.Get(ArgTitle)     is not null
            && WidgetNodeRegistry.Get(SelectTitle)  is not null
            && WidgetNodeRegistry.Get(ImageTitle)   is not null
            && WidgetNodeRegistry.Get(AudioTitle)   is not null
            && WidgetNodeRegistry.Get(TextTitle)    is not null
            && WidgetNodeRegistry.Get(CombineTitle) is not null;

        /// <summary>
        /// True when regenerating <paramref name="widget"/> AS IT STANDS would have to write into
        /// a trigger this compiler does NOT own — i.e. the name
        /// <see cref="AlertBoxSettings.ResolvedTriggerName"/> resolves to is already taken by a
        /// trigger that no previous compile installed and that carries a graph worth protecting.
        ///
        /// <para><b>Why this exists as a public pre-flight.</b> The refusal has to be reachable
        /// BEFORE a caller mutates or snapshots undo: <c>ApplyPreset</c> replaces the widget's
        /// <c>onStartup</c> graph on its way to the compile, so a compiler-only guard would still
        /// leave a half-applied preset behind (idle graph wiped, no alert chain). Both the VM's
        /// settings commit and <c>ApplyPreset</c> call this first; the compiler re-checks anyway,
        /// which is what makes the guard un-bypassable.</para>
        ///
        /// <para>This overload is for the SETTINGS-COMMIT caller, where the widget is already an
        /// Alert Box carrying settings. A caller that is about to TURN a widget into an Alert Box
        /// must use <see cref="AlertBoxTriggerNameTaken(LayerWidget?, WidgetPreset)"/> — see its
        /// remarks for why this one cannot answer that question.</para>
        /// </summary>
        public static bool AlertBoxTriggerNameTaken(LayerWidget? widget)
            => AlertBoxTriggerNameTakenCore(widget, widget?.Preset == WidgetPreset.AlertBox);

        /// <summary>
        /// True when compiling <paramref name="widget"/> as an Alert Box — because
        /// <paramref name="presetBeingApplied"/> is <see cref="WidgetPreset.AlertBox"/> — would
        /// collide with a hand-authored trigger of the resolved name.
        ///
        /// <para><b>★ Why the preset has to be a PARAMETER, and why the one-argument overload is
        /// the wrong question on the conversion path.</b> <c>ApplyPreset</c> pre-flights this
        /// BEFORE it writes <c>target.Preset = preset</c> and before any settings exist, because
        /// pre-flighting after those writes is worthless (the graph is already gone). On the only
        /// path the pre-flight was added for — a Text / Image widget being turned INTO an Alert
        /// Box from the gallery — the widget's Preset is therefore still Text and its
        /// <c>AlertBox</c> is still null, so both of the one-argument overload's early-outs fire
        /// and it answers "no collision" for a widget that is about to collide. Execution then
        /// continues into the destructive half: undo snapshot, preset flip, <c>onStartup.Graph</c>
        /// replaced with the preset's bare Display sink, and only THEN does the compiler
        /// materialise default settings, resolve <c>onTrigger:&lt;id&gt;</c>, find the author's
        /// trigger and refuse. End state: idle graph destroyed, no alert chain, no ownership
        /// record — verbatim the half-applied state the guard exists to prevent.</para>
        ///
        /// <para>So this overload asks what the caller actually needs answered: <i>would compiling
        /// this widget as an Alert Box collide?</i> It skips the <c>widget.Preset</c> test (the
        /// preset is the caller's intent, not the widget's current tag) and treats a null
        /// <c>AlertBox</c> as <see cref="AlertBoxSettings.CreateDefault"/>, which is exactly what
        /// <see cref="RegenerateAlertBox"/> will attach a moment later — so the pre-flight and the
        /// compiler judge the same settings. The stand-in is a LOCAL: a pre-flight must not be
        /// able to leave settings on a widget it then refuses to touch.</para>
        /// </summary>
        public static bool AlertBoxTriggerNameTaken(LayerWidget? widget, WidgetPreset presetBeingApplied)
            => AlertBoxTriggerNameTakenCore(widget, presetBeingApplied == WidgetPreset.AlertBox);

        private static bool AlertBoxTriggerNameTakenCore(LayerWidget? widget, bool compilesAsAlertBox)
        {
            if (widget is null) return false;
            if (!compilesAsAlertBox) return false;
            // A widget with no settings yet gets the same defaults RegenerateAlertBox would
            // attach, so "onTrigger:alert" is pre-flighted rather than silently skipped.
            AlertBoxSettings settings = widget.AlertBox ?? AlertBoxSettings.CreateDefault();
            if (settings.Detached) return false;
            if (settings.ResolvedTriggerName is not { } triggerName) return false;
            if (widget.Triggers is not { } triggers) return false;

            WidgetTrigger? existing = triggers.Find(t =>
                t is not null && string.Equals(t.Name, triggerName, StringComparison.OrdinalIgnoreCase));
            return existing is not null && !CompilerOwnsTrigger(settings, existing);
        }

        /// <summary>
        /// True when <paramref name="trigger"/> is one the compiler may overwrite: either a
        /// PREVIOUS COMPILE installed it (its name is the one recorded in
        /// <see cref="AlertBoxSettings.CompiledTriggerName"/>) or there is nothing on it to
        /// protect (no graph, no nodes, or nothing but the auto-injected <c>Display</c> sink —
        /// the two shapes <c>VisualistViewModel.AddTrigger</c> and <c>WidgetGraphCanvas</c>'s
        /// sink injection produce between them).
        ///
        /// <para><b>★ Why an ownership record and not "does it look compiled".</b> Without this
        /// check the compiler adopts ANY same-named trigger: a streamer with a hand-built
        /// <c>onTrigger:alert</c> who picks the AlertBox preset once has that graph overwritten,
        /// and — worse — <c>CompiledTriggerName</c> then claims it, so the next trigger-id rename
        /// DELETES it outright via the rename cleanup below. A heuristic over graph shape would
        /// have the same failure mode in the other direction (an author whose hand graph happens
        /// to look compiled), which is exactly why ownership is recorded rather than inferred.</para>
        /// </summary>
        private static bool CompilerOwnsTrigger(AlertBoxSettings settings, WidgetTrigger trigger)
        {
            string owned = (settings.CompiledTriggerName ?? "").Trim();
            string name  = (trigger.Name ?? "").Trim();
            if (owned.Length > 0 && string.Equals(owned, name, StringComparison.OrdinalIgnoreCase))
                return true;

            // An empty trigger is not authored work, and "empty" has TWO shapes:
            //
            //  1. zero nodes — VisualistViewModel.AddTrigger seeds a bare `new Graph()`;
            //  2. nothing but the auto-injected Display sink — WidgetGraphCanvas injects one
            //     into any graph that lacks it the first time the trigger is OPENED (and marks
            //     the document dirty doing so).
            //
            // Shape 2 is why counting nodes is not enough: a trigger the author created and
            // merely LOOKED AT holds one node they did not author, so a Count == 0 test protects
            // it forever. The Inspector then tells them to rename or delete a trigger containing
            // nothing of theirs, and no field edit will ever compile. DisplaySinkNode.Is is the
            // same predicate the injector itself tests with, so the two cannot drift.
            return CountAuthoredNodes(trigger.Graph) == 0;
        }

        /// <summary>Nodes on <paramref name="graph"/> that the AUTHOR is responsible for — i.e.
        /// everything except the auto-injected <c>Display</c> sink. Used both for the ownership
        /// test above and for the refusal log line, so the number the author reads and the
        /// decision the compiler made can never disagree.</summary>
        private static int CountAuthoredNodes(Graph? graph)
        {
            if (graph?.Nodes is not { } nodes) return 0;
            int n = 0;
            foreach (Node? node in nodes)
            {
                if (node is null) continue;
                if (DisplaySinkNode.Is(node)) continue;
                n++;
            }
            return n;
        }

        /// <summary>
        /// Compile an Alert Box's settings into the graph its <c>onTrigger:&lt;id&gt;</c>
        /// trigger runs. Returns <c>null</c> when a required template is missing — the
        /// same null-guard-first contract every other preset builder here follows.
        ///
        /// <para><b>Shape</b> (9 nodes, 8 links):</para>
        /// <code>
        ///   Visual.Arg("Args1") ─┬─▶ String.Select #img ──▶ Image.Load.Path ──▶ Image.Combine.A ─┐
        ///                        │                                                              ├─▶ Display
        ///   Text.Render(Message) ─────────────────────────────────────────▶ Image.Combine.B ─────┘
        ///                        └─▶ String.Select #snd ──▶ Audio.Load.Path ──▶ Audio.Play
        /// </code>
        ///
        /// <para><b>ONE Image.Load, ONE Audio.Load, ONE Audio.Play — selection by VALUE,
        /// never by branch.</b> The tempting alternative is one <c>Result.If</c>-gated
        /// loader pair per kind, and it does not work: the browser's audio sink pass
        /// evaluates EVERY <c>Audio.Play</c> in the graph unconditionally, outside every
        /// <c>Result.If</c> arm, and <c>Result.If</c> is an Image-typed barrier while
        /// <c>Audio.Play</c>'s only input is Audio-typed — the two type systems cannot
        /// meet, so a branch-gated alert would play all ten sounds at once. Choosing the
        /// clip upstream, in the string world, is the entire reason V7 built
        /// <c>String.Select</c>.</para>
        ///
        /// <para><b>Text over image via Image.Combine, not two links into Display.</b>
        /// Display has ONE Image input. Combine is null-tolerant on both sides in both
        /// halves of the mirror (C# <c>EvalImageCombine</c>: <c>if (b is null) return a;
        /// if (a is null) return b;</c> — browser: identical), which is what makes an
        /// image-only alert and a caption-only alert both render instead of erroring, and
        /// it composes in union extent centred on the widget centre, so the render
        /// contract ("never frame-size an intermediate node, crop only at Display") holds
        /// without any geometry work here.</para>
        ///
        /// <para><b>Every link is built from the INSTANCE.</b>
        /// <c>WidgetNodeRegistry.Instantiate</c> mints fresh socket GUIDs per call, so a
        /// link assembled from a template's socket ids points at nothing — and the failure
        /// is silent: the graph loads, no error is raised anywhere, the overlay is simply
        /// blank.</para>
        /// </summary>
        /// <param name="identitySeed">
        /// Stable per-widget seed for <see cref="StableNodeId"/>. Pass the owning
        /// widget's <c>Id</c>; <see cref="RegenerateAlertBox"/> always does. Empty is
        /// accepted (tests, and any caller that only wants the graph SHAPE) and still
        /// produces deterministic ids — they are simply shared by every seedless
        /// compile, which is harmless because node ids only have to be unique within
        /// one graph.
        /// </param>
        public static Graph? CompileAlertBoxGraph(AlertBoxSettings settings, string identitySeed = "")
        {
            if (settings is null) return null;

            // Null-guard EVERY template first — bail before mutating anything, so a
            // half-built graph can never be installed onto a widget. (Instantiate THROWS
            // on an unknown title, so this is the difference between a refusal and an
            // exception out of a settings commit.)
            if (!AlertBoxTemplatesAvailable()) return null;

            var graph = new Graph { Name = settings.ResolvedTriggerName ?? "onTrigger:alert" };

            var arg     = WidgetNodeRegistry.Instantiate(ArgTitle,     new System.Drawing.Point(60,  180));
            var selImg  = WidgetNodeRegistry.Instantiate(SelectTitle,  new System.Drawing.Point(300,  40));
            var selSnd  = WidgetNodeRegistry.Instantiate(SelectTitle,  new System.Drawing.Point(300, 380));
            var imgLoad = WidgetNodeRegistry.Instantiate(ImageTitle,   new System.Drawing.Point(560,  40));
            var sndLoad = WidgetNodeRegistry.Instantiate(AudioTitle,   new System.Drawing.Point(560, 380));
            var caption = WidgetNodeRegistry.Instantiate(TextTitle,    new System.Drawing.Point(560, 200));
            var combine = WidgetNodeRegistry.Instantiate(CombineTitle, new System.Drawing.Point(820, 100));
            var audio   = AudioSinkNode.Build();
            var display = DisplaySinkNode.Build();
            audio.Location   = new System.Drawing.Point(820, 380);
            display.Location = new System.Drawing.Point(1080, 100);

            // ── stable identity, BEFORE any link is built ──────────────────────
            //
            // Instantiate/Build mint a fresh Guid per call, and keyframes are keyed
            // "<nodeId>.<component>" (AnimatedPinRegistry.MakeParameterPath). A
            // recompile that re-randomised these ids therefore orphaned every
            // authored track on this widget — the timeline kept the rows, they
            // simply stopped matching any node, so they went dead-but-visible and a
            // fresh dead set accumulated per settings commit.
            //
            // Re-deriving the id from (widget, role) instead makes a recompile
            // IDENTITY-PRESERVING: the caption node is the same node it was last
            // time, so its tracks keep animating. This runs before LinkSocket
            // because links are built from ids read off these instances.
            //
            // The role strings below are a PERSISTED CONTRACT — an author's saved
            // keyframes resolve through them. Renaming one silently orphans exactly
            // the tracks this fix exists to preserve; add roles, never rename them.
            arg.Id     = StableNodeId(identitySeed, "arg");
            selImg.Id  = StableNodeId(identitySeed, "select.image");
            selSnd.Id  = StableNodeId(identitySeed, "select.sound");
            imgLoad.Id = StableNodeId(identitySeed, "image.load");
            sndLoad.Id = StableNodeId(identitySeed, "audio.load");
            caption.Id = StableNodeId(identitySeed, "caption");
            combine.Id = StableNodeId(identitySeed, "combine");
            audio.Id   = StableNodeId(identitySeed, "sink.audio");
            display.Id = StableNodeId(identitySeed, "sink.display");

            graph.Nodes.Add(arg);
            graph.Nodes.Add(selImg);
            graph.Nodes.Add(selSnd);
            graph.Nodes.Add(imgLoad);
            graph.Nodes.Add(sndLoad);
            graph.Nodes.Add(caption);
            graph.Nodes.Add(combine);
            graph.Nodes.Add(audio);
            graph.Nodes.Add(display);

            // ── attribute overrides (after instantiation, per the BuildAudioChain
            //    precedent — Instantiate has already copied the template defaults) ──

            // Args1 is where AlertsService puts KindLabel(family). PreviewText stays
            // empty: it is DESIGN-TIME only, and a compiled graph's rows are rewritten
            // behind the author's back, so a mock they never typed must never be able to
            // reach air.
            arg.Attributes["Key"]         = Quote(AlertBoxSettings.KindArgKey);
            arg.Attributes["PreviewText"] = Quote("");

            FillSelect(selImg, settings, image: true);
            FillSelect(selSnd, settings, image: false);

            // Path attributes are cleared so the WIRE is unambiguously the source. Leaving
            // a template default in place would be harmless today (a wired socket wins)
            // but would give a detached author a stale second value to wonder about.
            imgLoad.Attributes["Path"] = Quote("");
            sndLoad.Attributes["Path"] = Quote("");

            // Text.Render has no "Text" default attribute; both readers fall back to
            // attr(node,'Text','') when the socket is unwired, so writing it here IS the
            // supported way to carry a literal caption. {Args2}/{Args3} are expanded by
            // Text.Render's own substituteArgs pass, so the caption needs no wire.
            caption.Attributes["Text"] = Quote(settings.MessageFormat ?? "");
            // A 2px black outline by default, for the same reason the Audio preset defaults
            // Loop=true: the preset should be usable as generated. An alert caption is
            // drawn over arbitrary artwork, and white-on-white is the single most likely
            // way a first-run alert looks broken. Authors clear StrokeWidth for flat text.
            caption.Attributes["StrokeColor"] = Quote("#000000");
            caption.Attributes["StrokeWidth"] = "2";

            // ── links: every id read off the RETURNED instances ──
            LinkSocket(graph, arg,     "Value", selImg,  "When");
            LinkSocket(graph, arg,     "Value", selSnd,  "When");
            LinkSocket(graph, selImg,  "Value", imgLoad, "Path");
            LinkSocket(graph, selSnd,  "Value", sndLoad, "Path");
            LinkSocket(graph, imgLoad, "Image", combine, "A");
            LinkSocket(graph, caption, "Image", combine, "B");
            LinkSocket(graph, combine, "Out",   display, "Image");
            LinkSocket(graph, sndLoad, "Audio", audio,   "Audio");

            return graph;
        }

        /// <summary>
        /// Regenerate (or first-generate) an Alert Box widget's compiled trigger from its
        /// settings. Idempotent: calling it twice with unchanged settings leaves the widget
        /// in the same shape, with the previous compiled graph REPLACED rather than
        /// appended to.
        ///
        /// <para><b>Detach is enforced HERE, at the single choke point.</b> Every caller —
        /// a settings commit, <c>ApplyPreset</c>, a widget spawn — routes through this
        /// method, so one guard covers all of them and there is no path that can quietly
        /// re-attach. There is deliberately no <c>force</c> parameter: a flag that
        /// overrides the protection is a flag someone eventually passes.</para>
        ///
        /// <para><b>Only a trigger the compiler OWNS is ever written into.</b> If the resolved
        /// name is already taken by a trigger no previous compile installed, and it carries
        /// nodes, this refuses with <see cref="AlertBoxRegenResult.TriggerNameTaken"/> and
        /// touches nothing — see <see cref="AlertBoxTriggerNameTaken"/> for why adopting a
        /// same-named trigger is a data-loss bug and not a convenience.</para>
        ///
        /// <para>Attaches default settings when the widget has none (a widget whose Preset
        /// was flipped to AlertBox by the pill picker, which sets the tag and nothing
        /// else), and tops up any canonical kind the settings predate.</para>
        /// </summary>
        public static AlertBoxRegenResult RegenerateAlertBox(LayerWidget widget)
        {
            if (widget is null) return AlertBoxRegenResult.NotAnAlertBox;
            if (widget.Preset != WidgetPreset.AlertBox) return AlertBoxRegenResult.NotAnAlertBox;

            // A widget that has never been configured gets settings now; one that HAS been
            // detached keeps whatever it has and is left alone.
            AlertBoxSettings settings = widget.AlertBox ??= AlertBoxSettings.CreateDefault();
            if (settings.Detached) return AlertBoxRegenResult.Detached;

            if (settings.ResolvedTriggerName is not { } triggerName)
                return AlertBoxRegenResult.InvalidTriggerId;

            // ── ownership pre-flight, BEFORE the first mutation of anything ──
            //
            // Only a trigger the compiler installed (or an empty one) may be written into. A
            // hand-authored graph sitting on that name is refused and the widget is left
            // COMPLETELY alone — no settings top-up, no compile, and above all no
            // CompiledTriggerName write, because recording ownership of someone else's trigger
            // is what turns the next id rename into a deletion.
            //
            // This runs ahead of EnsureCanonicalKinds / CompileAlertBoxGraph on purpose: a
            // refusal must not leave a partially updated settings object behind either.
            WidgetTrigger? existing = null;
            if (widget.Triggers is { } preTriggers)
            {
                existing = preTriggers.Find(t =>
                    t is not null && string.Equals(t.Name, triggerName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && !CompilerOwnsTrigger(settings, existing))
                {
                    GlobalLogger.Log(
                        $"Alert Box '{widget.Name}' ({widget.Id}): trigger '{triggerName}' already "
                        + "exists on this widget and was not generated from these settings — it has "
                        + $"{CountAuthoredNodes(existing.Graph)} hand-authored node(s). Nothing was "
                        + "changed. Give the Alert Box a different trigger id, or rename/delete that "
                        + "trigger first.",
                        source: "WidgetPresets",
                        level: LogLevel.System);
                    return AlertBoxRegenResult.TriggerNameTaken;
                }
            }

            int added = settings.EnsureCanonicalKinds();
            if (added > 0)
            {
                GlobalLogger.Log(
                    $"Alert Box '{widget.Name}' ({widget.Id}): added {added} new alert-kind row(s) — "
                    + "the alert families Hub can fire grew since these settings were saved.",
                    source: "WidgetPresets",
                    level: LogLevel.System);
            }

            // Seeded with the WIDGET id, not the trigger name, so that BOTH a settings
            // commit and a trigger rename are identity-preserving — the rename path below
            // carries the timeline across, and seeding on the trigger name would have made
            // that impossible by re-minting every id at the moment the name changed.
            string identitySeed = widget.Id ?? "";
            Graph? compiled = CompileAlertBoxGraph(settings, identitySeed);
            if (compiled is null) return AlertBoxRegenResult.RegistryUnavailable;

            widget.Triggers ??= new List<WidgetTrigger>();

            // Drop the previously compiled trigger when the author renamed the id. Guarded
            // three ways: only when we actually recorded a name, only when it differs from
            // the one we are about to install, and never onStartup (a malformed settings
            // file must not be able to delete the idle graph).
            //
            // ★ The removed trigger's TIMELINE is carried onto the replacement. A rename is
            // the author retitling their own work, not discarding it — and because node
            // identity is derived from the WIDGET, every keyframe path still resolves
            // against the new graph. Before this the rename silently threw the author's
            // keyframes away: same data loss as the id churn, through a different door,
            // and equally invisible.
            WidgetTimeline? carriedTimeline = null;
            Graph? carriedGraph = null;
            string previous = (settings.CompiledTriggerName ?? "").Trim();
            if (previous.Length > 0
                && !string.Equals(previous, triggerName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(previous, "onStartup", StringComparison.OrdinalIgnoreCase))
            {
                WidgetTrigger? old = widget.Triggers.Find(t =>
                    t is not null && string.Equals(t.Name, previous, StringComparison.OrdinalIgnoreCase));

                // ★ The donor must be a graph THIS COMPILER SHAPED, and the check for that
                // is the role map — not CompilerOwnsTrigger.
                //
                // CompilerOwnsTrigger compares the trigger's name against
                // settings.CompiledTriggerName, which is the very predicate that found
                // `old` one line up: it is tautologically true here and protects nothing.
                // The hazard it was meant to catch is real and reachable — an author who
                // deletes the compiled trigger and hand-authors a new one under the same
                // name still leaves CompiledTriggerName pointing at it — and grafting
                // THAT timeline onto a generated graph would install rows whose paths name
                // hand-authored nodes that exist in no compiled graph.
                //
                // Requiring the role map to resolve is a guard that can actually fail: it
                // succeeds only for a graph with exactly this compiler's node shape.
                if (old is not null)
                {
                    var donorRoles = MapRolesToExistingIds(old.Graph);
                    if (donorRoles is not null)
                    {
                        carriedTimeline = old.Timeline;
                        carriedGraph    = old.Graph;   // ★ needed by the migration below
                    }
                    else if (old.Timeline is { Keyframes.Count: > 0 })
                    {
                        GlobalLogger.Log(
                            $"Alert Box '{widget.Name}' ({widget.Id}): the trigger being renamed away from "
                            + $"('{previous}') is not shaped like a compiled Alert Box, so its "
                            + $"{old.Timeline.Keyframes.Count} keyframe(s) were NOT carried onto "
                            + $"'{triggerName}' — their paths name nodes the generated graph does not have.",
                            source: "WidgetPresets",
                            level: LogLevel.System);
                    }
                }

                int removed = widget.Triggers.RemoveAll(t =>
                    t is not null && string.Equals(t.Name, previous, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                    GlobalLogger.Log(
                        $"Alert Box '{widget.Name}' ({widget.Id}): trigger renamed "
                        + $"'{previous}' → '{triggerName}'; removed the stale compiled trigger. "
                        + "Re-point the Alerts tool's Trigger Name at the new value.",
                        source: "WidgetPresets",
                        level: LogLevel.System);
            }

            // Resolved by the ownership pre-flight above (the rename cleanup cannot have removed
            // it — the removal is guarded on `previous != triggerName`), so no second lookup.
            WidgetTrigger? target = existing;

            // ── one-time keyframe migration ──────────────────────────────────
            //
            // Graphs compiled BEFORE node identity became derived carry Guid.NewGuid ids
            // that cannot be recomputed, so their authored keyframes would be orphaned by
            // the very first recompile after the upgrade — the exact harm this fix exists
            // to end, landing once on the way out. Read the old ids back out of the graph
            // being replaced and rewrite the paths onto the ids the new graph uses.
            //
            // Idempotent by construction: once the ids already match, the rewrite map is
            // empty and this is a no-op on every subsequent commit.
            //
            // ★ Migrated as (timeline, ITS OWN graph) PAIRS, not as one timeline against
            // one graph. There can be two live candidates at once — the destination
            // trigger's, and a donor carried in by a rename — each keyed on the ids of a
            // DIFFERENT graph. Pairing them individually is what keeps a rename-into-an-
            // existing-trigger correct: whichever timeline the install below adopts has
            // already been migrated against the graph its paths actually came from.
            // An earlier shape used `target?.X ?? carried` for both halves, which made the
            // block unreachable on a plain rename (target is looked up by the NEW name, so
            // it is null exactly then) and migrated the wrong pair when it was reachable.
            foreach (var (timeline, sourceGraph) in new[]
                     {
                         (target?.Timeline, target?.Graph),
                         (carriedTimeline,  carriedGraph),
                     })
            {
                if (timeline is not { Keyframes.Count: > 0 } || sourceGraph is null) continue;

                var oldRoleIds = MapRolesToExistingIds(sourceGraph);
                if (oldRoleIds is not null)
                {
                    int moved = MigrateKeyframePaths(timeline, oldRoleIds, identitySeed);
                    if (moved > 0)
                        GlobalLogger.Log(
                            $"Alert Box '{widget.Name}' ({widget.Id}): carried {moved} authored keyframe(s) "
                            + "onto the recompiled graph. Earlier builds re-minted node ids on every "
                            + "settings commit, which silently detached these tracks; this is the one-time "
                            + "migration onto stable ids.",
                            source: "WidgetPresets",
                            level: LogLevel.System);
                }
                else
                {
                    // Unrecognised shape — a hand-edited compiled graph, or one from a
                    // future/older compiler. Say so rather than guessing a mapping: a
                    // keyframe rewritten onto the wrong node is worse than one left where
                    // the author can still see it.
                    GlobalLogger.Log(
                        $"Alert Box '{widget.Name}' ({widget.Id}): a graph being replaced does not match "
                        + "the compiler's shape, so its keyframe paths were left untouched. Tracks that "
                        + "referenced its nodes will not animate on the new graph.",
                        source: "WidgetPresets",
                        level: LogLevel.System);
                }
            }

            double durationMs = NormalizeDurationMs(settings.DurationMs);

            if (target is null)
            {
                // Name is assigned through the validating setter, which is safe because
                // ResolvedTriggerName already proved the value passes IsValidName — an
                // invalid one would have silently no-op'd and left a SECOND "onStartup".
                target = new WidgetTrigger { Name = triggerName };
                target.Graph = compiled;
                // A rename donates its timeline (keyframes included, already migrated
                // above); a genuinely new trigger starts empty.
                target.Timeline = carriedTimeline ?? new WidgetTimeline();
                target.Timeline.DurationMs = durationMs;
                widget.Triggers.Add(target);
            }
            else
            {
                // REPLACE, never merge. The settings are the whole truth; a merge would
                // leave orphan nodes from a previous shape wired to nothing.
                target.Graph = compiled;
                // Keep the trigger's own Timeline INSTANCE so any keyframes an author
                // recorded against it are not silently discarded by a settings commit —
                // only the authored duration is settings-owned. (Volume is likewise left
                // alone: it is the TRIGGERS section's per-trigger master.)
                target.Timeline ??= new WidgetTimeline();

                // ★ A rename INTO a name that already has a compiler-owned trigger.
                //
                // Two timelines exist and only one can survive: the donor's (the author's
                // actual work, on the trigger they renamed away from) and the target's
                // (whatever already sat on the destination name — usually an empty trigger
                // the author created by hand). Taking `target`'s unconditionally, as this
                // branch used to, threw the author's keyframes away while the rename log
                // still announced that they had been carried across.
                //
                // Adopt the donor only when the destination has nothing to lose. If BOTH
                // carry keyframes there is no safe automatic answer — two authored tracks
                // cannot be merged without inventing intent — so the destination wins and
                // the loss is REPORTED rather than silent.
                if (carriedTimeline is { Keyframes.Count: > 0 })
                {
                    if (target.Timeline.Keyframes.Count == 0)
                    {
                        target.Timeline = carriedTimeline;
                        GlobalLogger.Log(
                            $"Alert Box '{widget.Name}' ({widget.Id}): carried "
                            + $"{carriedTimeline.Keyframes.Count} keyframe(s) onto '{triggerName}', which "
                            + "already existed but had none.",
                            source: "WidgetPresets",
                            level: LogLevel.System);
                    }
                    else
                    {
                        GlobalLogger.Log(
                            $"Alert Box '{widget.Name}' ({widget.Id}): '{triggerName}' already has "
                            + $"{target.Timeline.Keyframes.Count} keyframe(s), so the "
                            + $"{carriedTimeline.Keyframes.Count} keyframe(s) from '{previous}' were "
                            + "DISCARDED. Rename onto an empty trigger to keep them.",
                            source: "WidgetPresets",
                            level: LogLevel.System);
                    }
                }

                target.Timeline.DurationMs = durationMs;
            }

            settings.CompiledTriggerName = triggerName;
            return AlertBoxRegenResult.Regenerated;
        }

        /// <summary>
        /// Clamp the authored hold to the window both halves of the runtime can honour.
        /// compositor.js clamps the hold to <c>[2000, 60000]</c> and Hub derives the
        /// widget queue's completion timeout from the same number, so a NaN / negative /
        /// absurd value here would desync the two. Non-finite ⇒ the 4 s default; the
        /// authored value is otherwise passed through and clamped to the same
        /// <c>[0, 600000]</c> band the widget editor's own duration box uses.
        /// </summary>
        private static double NormalizeDurationMs(double ms)
        {
            if (!double.IsFinite(ms)) return 4000;
            if (ms < 0) return 0;
            return ms > 600000 ? 600000 : ms;
        }

        /// <summary>
        /// Write one <c>String.Select</c>'s rows from the settings: <c>Case&lt;i&gt;</c> =
        /// the alert kind label, <c>Value&lt;i&gt;</c> = its media path, <c>Default</c> =
        /// the fallback. All values are JSON-quoted string literals, which is what every
        /// reader quote-strips.
        ///
        /// <para><b>A row with no media is OMITTED, not written blank.</b> Omitting it
        /// makes nothing match for that kind, so <c>String.Select</c> emits its Default —
        /// which is exactly "fall through to the fallback". Writing the row with an empty
        /// Value would instead make that kind resolve to an empty path, i.e. silence /
        /// no image, and would burn one of the twelve slots doing it.</para>
        ///
        /// <para>Bounded by <see cref="NodeTemplates.StringSelectRows"/> (12) because that
        /// is how many Case/Value pairs the template ships. Ten canonical kinds leave two
        /// spare, but <c>EnsureCanonicalKinds</c> is additive and preserves
        /// author-invented kinds, so the list CAN exceed twelve. Overflow is dropped and
        /// logged by name — a silently ignored row would be an alert that never fires with
        /// a fully configured-looking Inspector.</para>
        /// </summary>
        private static void FillSelect(Node select, AlertBoxSettings settings, bool image)
        {
            // Clear every row first: the node was instantiated from the template so the
            // rows are already empty, but this method is the one place that decides what a
            // row means and it must not depend on that.
            for (int i = 1; i <= NodeTemplates.StringSelectRows; i++)
            {
                select.Attributes["Case"  + i.ToString(CultureInfo.InvariantCulture)] = Quote("");
                select.Attributes["Value" + i.ToString(CultureInfo.InvariantCulture)] = Quote("");
            }
            select.Attributes["When"]    = Quote("");
            select.Attributes["Default"] = Quote(
                ((image ? settings.FallbackImage : settings.FallbackSound) ?? "").Trim());

            int row = 0;
            List<string>? dropped = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (AlertBoxKindRow r in settings.Rows)
            {
                if (r is null) continue;
                string kind  = (r.Kind ?? "").Trim();
                // Normalise separators exactly like the node-level MediaPath row does
                // (VisualistViewModel.NodeParamVm.CommitText). Without it a pasted
                // Windows path ("alerts\follow.png") is quoted into the String.Select
                // row verbatim, URL-encoded to /media/alerts%5Cfollow.png and 404s with
                // no diagnostic — and AlertBoxKindRow's own doc already claims "the
                // compiler normalises separators".
                string media = ((image ? r.Image : r.Sound) ?? "").Trim().Replace("\\", "/");
                if (kind.Length == 0 || media.Length == 0) continue;
                // First row wins on a duplicate kind, matching String.Select's own
                // first-match-wins scan — so a duplicate cannot consume a second slot.
                if (!seen.Add(kind)) continue;
                if (row >= NodeTemplates.StringSelectRows)
                {
                    (dropped ??= new List<string>()).Add(kind);
                    continue;
                }
                row++;
                string n = row.ToString(CultureInfo.InvariantCulture);
                select.Attributes["Case"  + n] = Quote(kind);
                select.Attributes["Value" + n] = Quote(media);
            }

            if (dropped is not null)
            {
                GlobalLogger.Log(
                    $"Alert Box: more than {NodeTemplates.StringSelectRows} configured "
                    + (image ? "image" : "sound") + " rows — dropped "
                    + string.Join(", ", dropped) + ". Those kinds will show the fallback.",
                    source: "WidgetPresets",
                    level: LogLevel.System);
            }
        }

        /// <summary>Add a link between two INSTANTIATED nodes, resolved by socket
        /// Type + Name. A missing socket is skipped rather than throwing: the templates
        /// were all null-guarded above, so this can only mean a template's socket set
        /// changed, and a partially wired graph an author can see and repair beats an
        /// exception out of a settings commit.</summary>
        private static void LinkSocket(Graph graph, Node from, string fromSocket, Node to, string toSocket)
        {
            var f = from.Sockets.Find(s => s.Type == SocketType.Output && s.Name == fromSocket);
            var t = to.Sockets.Find(s => s.Type == SocketType.Input  && s.Name == toSocket);
            if (f is null || t is null)
            {
                GlobalLogger.Log(
                    $"Alert Box compile: could not wire {from.Title}.{fromSocket} → {to.Title}.{toSocket} "
                    + "(socket missing from the current template).",
                    source: "WidgetPresets",
                    level: LogLevel.System);
                return;
            }
            graph.Links.Add(new Link
            {
                FromNodeId = from.Id, FromSocketId = f.Id,
                ToNodeId   = to.Id,   ToSocketId   = t.Id,
            });
        }

        /// <summary>JSON-quote an attribute value — the storage convention every widget
        /// attribute reader quote-strips (<c>StripQuotes</c> / <c>stripQuotes</c>).</summary>
        private static string Quote(string value) => "\"" + value + "\"";
    }
}
