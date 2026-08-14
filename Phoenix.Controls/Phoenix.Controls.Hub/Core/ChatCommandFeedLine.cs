using System;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// The one place that knows the shape of the chat-command line WS writes into
    /// the curated feed: <c>"!&lt;verb&gt; by &lt;user&gt;"</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why writer and reader live together.</b> WS logs this line at
    /// <c>LogLevel.StreamEvent</c>, and <c>LiveFeedSource</c> in Hub.WinUI is the
    /// consumer that has to tell it apart from the three OTHER shapes that arrive
    /// on the same level — <c>"Scene → &lt;scene&gt;"</c>,
    /// <c>"&lt;sender&gt; whispered: &lt;text&gt;"</c> and the generic
    /// <c>"&lt;EventType&gt; by &lt;actor&gt;"</c>. While the format string and the
    /// parser sat in different projects there was nothing stopping one from being
    /// edited without the other, and the failure mode is silent: the row simply
    /// starts being classified as whatever event its verb happens to spell.</para>
    ///
    /// <para>That is not hypothetical — it is the defect this type was extracted
    /// to fix. The consumer had no shape check at all and ran unanchored
    /// substring probes over the WHOLE line, so <c>"!subs by Bob"</c> rendered as
    /// a SUB and <c>"!raid by Bob"</c> as a RAID, and any command typed by a
    /// viewer named <i>SubZero</i> became a SUB.</para>
    ///
    /// <para>Kept in <c>Phoenix.Controls.Hub</c> rather than Hub.WinUI on purpose:
    /// the parse is pure, and the test project can exercise it without loading a
    /// WinUI-coupled type.</para>
    /// </remarks>
    public static class ChatCommandFeedLine
    {
        /// <summary>The separator between the command word and the sender.</summary>
        private const string Infix = " by ";

        /// <summary>
        /// Builds the feed line for a chat command. <paramref name="verb"/> is the
        /// command word WITHOUT its leading <c>!</c>.
        /// </summary>
        public static string Format(string verb, string user) => $"!{verb}{Infix}{user}";

        /// <summary>
        /// Reads a feed line produced by <see cref="Format"/> and returns the
        /// sender. False for any line that is not exactly that shape.
        /// </summary>
        /// <remarks>
        /// Anchored on the literal <c>'!'</c> in the FIRST character position
        /// (ordinal — see <c>ChatVerb.LooksLikeCommand</c>) plus the infix
        /// immediately after the verb. No platform permits a username to begin
        /// with <c>!</c>, so a genuine stream event cannot be read as a command.
        /// <para>The verb is a single token by construction — WS cuts the message
        /// at its first space before prefixing the bang — which is what makes
        /// "the infix starts at the first space" a safe anchor.</para>
        /// <para>The sender is deliberately NOT rejected for containing spaces:
        /// YouTube display names legitimately do, and rejecting them would drop
        /// those rows back into the mis-classification path this closes.</para>
        /// </remarks>
        public static bool TryRead(string? line, out string user)
        {
            user = string.Empty;
            if (string.IsNullOrEmpty(line) || line!.Length < 2 || line[0] != '!') return false;
            int sp = line.IndexOf(' ');
            if (sp <= 1) return false;                                   // needs a non-empty verb
            if (!line.AsSpan(sp).StartsWith(Infix.AsSpan(), StringComparison.Ordinal)) return false;
            string sender = line[(sp + Infix.Length)..].Trim();
            if (sender.Length == 0) return false;
            user = sender;
            return true;
        }
    }
}
