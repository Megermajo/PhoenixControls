using System;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// PlayerEmbedSinkNode — identity + contract constants for the <c>Player.Embed</c>
    /// node: a side-effecting DOM-overlay sink that mounts a THIRD-PARTY <c>&lt;iframe&gt;</c>
    /// over the widget rect. Sibling of <see cref="WebOverlaySinkNode"/>, and it rides
    /// the exact same <c>#dom-overlay</c> track for the same reason: the browser
    /// composites the element natively over the canvas and nothing is rasterised into
    /// the Image pipeline.
    ///
    /// <para><b>The documented limit — an iframe owns its whole widget rect.</b> A
    /// cross-origin iframe cannot be painted to a canvas, faded, masked, transformed
    /// or z-ordered against canvas widgets: the DOM track sits at one fixed z-index
    /// above every canvas widget and below the editor manipulator, and the browser
    /// draws it, not us. That is a property of cross-origin embedding, not a gap to
    /// close later. Consequence for an author: a Player widget is a full-rect player,
    /// and layering it under a canvas overlay is not expressible. (The same reasoning
    /// is why <c>WebSource</c> — which registers an Image output — was deliberately
    /// NOT built as an iframe; see its template comment in NodeTemplates.)</para>
    ///
    /// <para><b>Two feeds, one substrate.</b> <see cref="SourceAttr"/> picks which:</para>
    /// <list type="bullet">
    /// <item><see cref="SourceSongRequest"/> — QUEUE-FED. The node subscribes the
    /// <c>songrequest.*</c> Overlay Live Channel keys that Hub's SongRequestService
    /// publishes and plays whatever is selected there. It is an ORDINARY SUBSCRIBER:
    /// it owns no queue state and never decides what plays next. When the media ends
    /// it sends ONE <c>MEDIA_ENDED</c> frame up the existing <c>/hud</c> socket, which
    /// only that service consumes, to advance the queue.</item>
    /// <item><see cref="SourceClip"/> — TRIGGER-FED one-shot. The <see cref="ClipSocket"/>
    /// String input carries a Twitch clip (URL or bare slug), normally wired from a
    /// <c>Visual.Arg</c> so a shoutout script can push one over the EXISTING
    /// <c>Visual.Trigger</c> channel. A shoutout plays its clip once; the widget's
    /// trigger hold governs how long it stays up, and the ordinary revert-to-onStartup
    /// tears the iframe down. No upward signal — nothing owns a clip queue.</item>
    /// </list>
    ///
    /// <para><b>Where the runtime lives.</b> Entirely in compositor.js
    /// (<c>evalPlayerEmbed</c>). C# holds only this identity, the template and the
    /// design-time <see cref="NodeEvaluator"/> mirror stub, exactly like
    /// <c>WebOverlay.Custom</c> — there is no browser at design time, so there is
    /// nothing here to evaluate.</para>
    ///
    /// <para><b>★ UNVERIFIED SUBSTRATE.</b> Nobody has yet confirmed that a
    /// youtube-nocookie iframe loads AND autoplays inside a real OBS Browser Source
    /// (the CEF pre-flight). The runtime is therefore built to FAIL LOUDLY rather than
    /// to sit black: every failure paints a legible card on the widget host AND pushes one
    /// <c>TRIGGER_DIAGNOSTIC</c>, so the reason lands in Hub's System Log.</para>
    ///
    /// <para><b>And the verdicts are kept apart, because the pre-flight is read off
    /// them.</b> "No player ever came up in that frame" (blocked outright) is settled first,
    /// on the postMessage HANDSHAKE rather than on the iframe's <c>load</c> event — <c>load</c>
    /// fires for a CSP refusal, an X-Frame-Options block and a browser error page alike, so
    /// gating on it would report a hard block as a healthy mount and let it be mislabelled
    /// "autoplay was refused" seconds later. Only after a real player has answered does
    /// <see cref="ReadyTimeoutMs"/> get to give the autoplay verdict. The clip feed, which
    /// speaks no cross-origin protocol at all, refuses what IS detectable (a value that is not
    /// a clip; no usable <c>parent=</c> host) before mounting anything.</para>
    ///
    /// <para>If the pre-flight comes back negative, this node and the <c>Player</c> preset are
    /// what get struck — the iframe substrate is deliberately confined to this file, one
    /// compositor.js block, and the CSP <c>frame-src</c> line.</para>
    /// </summary>
    public static class PlayerEmbedSinkNode
    {
        public const string Title    = "Player.Embed";
        public const string Category = "Sink";

        /// <summary>Attribute key selecting the feed. Raw string, not JSON-quoted.</summary>
        public const string SourceAttr = "Source";

        /// <summary>Queue-fed: follow the <c>songrequest.*</c> live keys.</summary>
        public const string SourceSongRequest = "songrequest";

        /// <summary>Trigger-fed one-shot: play the clip named on <see cref="ClipSocket"/>.</summary>
        public const string SourceClip = "clip";

        /// <summary>
        /// The <see cref="SourceAttr"/> values the compositor understands, published as the
        /// editor's dropdown choices via the <c>__KnownValues</c> convention. Anything else
        /// is treated as <see cref="SourceSongRequest"/> browser-side.
        /// </summary>
        public const string SourceKnownValues = SourceSongRequest + "," + SourceClip;

        /// <summary>
        /// String input carrying the Twitch clip for <see cref="SourceClip"/> — a
        /// <c>clips.twitch.tv/&lt;slug&gt;</c> URL, a <c>clips.twitch.tv/embed?clip=&lt;slug&gt;</c>
        /// URL (what Twitch's own share dialog hands out), a
        /// <c>twitch.tv/&lt;channel&gt;/clip/&lt;slug&gt;</c> URL, or a bare slug. Ignored
        /// entirely by <see cref="SourceSongRequest"/>. A value that is none of those is
        /// refused with a card and a diagnostic rather than silently leaving the widget black.
        /// </summary>
        public const string ClipSocket = "Clip";

        /// <summary>
        /// The <c>songrequest.*</c> live-channel prefix the queue-fed feed subscribes.
        /// ★ Must stay byte-identical to the root SongRequestService publishes under
        /// (see its PublishOverlay key list) and to the prefix compositor.js's
        /// <c>liveKeysForNode</c> announces — three spellings of one root is a blank
        /// widget with a running producer and no error on either side.
        ///
        /// <para><b>Nothing reads this at runtime, and that is stated rather than implied.</b>
        /// Hub publishes with its own literals and the browser subscribes with its own; this
        /// side has no producer and no reader. It is declared for the same reason
        /// <see cref="ReadyTimeoutMs"/> is — it is the ANCHOR the cross-file test pins the
        /// other two spellings against. <c>PlayerEmbedV15Tests</c> parses every key
        /// <c>SongRequestService.PublishOverlay</c> writes out of that file's source and
        /// requires each one to start with this prefix, then requires the four keys the player
        /// reads to appear in compositor.js. Renaming the root in any ONE of the three places
        /// therefore fails a test instead of producing a blank player over a running
        /// queue.</para>
        /// </summary>
        public const string SongRequestKeyPrefix = "songrequest.";

        /// <summary>
        /// How long the compositor waits, AFTER a real player has answered and been told to
        /// play, for it to report itself PLAYING before painting the failure card. Long enough
        /// for a slow fetch of the media itself; short enough that a streamer notices during
        /// the same track rather than at the end of the stream.
        ///
        /// <para>It does NOT cover the cold CEF start or the fetch of YouTube's player bundle —
        /// that is the first stage's job (<c>_PLAYER_LOAD_TIMEOUT_MS</c>, settled by the
        /// handshake). Keeping the two apart is what lets the pre-flight distinguish "the embed
        /// was blocked" from "autoplay was refused"; a paused track is never asked to play and
        /// so is never measured by this at all.</para>
        ///
        /// <para>The value that runs is <c>_PLAYER_READY_TIMEOUT_MS</c> in compositor.js —
        /// this side has no browser and no timer. It is declared here anyway because it is
        /// part of the node's author-facing contract (how long a silent player stays silent
        /// before it says so), and it is PINNED equal to the browser constant by
        /// <c>PlayerEmbedV15Tests</c>, so the two cannot drift into a documented value the
        /// product does not have.</para>
        /// </summary>
        public const int ReadyTimeoutMs = 12000;

        public static bool Is(Node? node) =>
            node is not null
            && string.Equals(node.Title, Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.Category, Category, StringComparison.OrdinalIgnoreCase);
    }
}
