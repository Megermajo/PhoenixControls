using System;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    /// The single canonical form of a CONFIGURED chat-command verb, and the
    /// single comparison used to match one against a parsed chat token.
    /// </summary>
    /// <remarks>
    /// <para><b>The bug this exists to kill.</b> Every built-in chat provider
    /// parses an inbound line the same way — reject unless it starts with
    /// <c>'!'</c>, take the body after the bang, cut at the first space — so the
    /// token a provider compares against has <b>already had its <c>!</c>
    /// removed</b>. The streamer's configured verb has not. Only two of the
    /// eleven providers (Polls, SongRequest) stripped a leading <c>!</c> from the
    /// configured side, so on the other nine a perfectly reasonable
    /// <c>Trigger = "!hello"</c> was a provably dead command that every panel
    /// still rendered as active. There is no error, no log line and no
    /// diagnostic: the verb simply never matches.</para>
    ///
    /// <para><b>Why the fix lives at the comparison and not at config load.</b>
    /// A load-time strip is what Polls and SongRequest already do, and it is
    /// fine as far as it goes — but it cannot be the load-bearing fix, because
    /// <c>LoyaltyService</c> and <c>UserManagementService</c> have no
    /// config-normalization pass at all (their own <c>Normalize</c> is a
    /// USERNAME normalizer), and because alias lists and row-level words on
    /// Counters / Soundboard / CustomCommands are edited live by their panels
    /// and pushed straight into the service. Canonicalizing at the point of
    /// comparison covers every one of those paths with one rule.</para>
    ///
    /// <para><b>Empty is never a match.</b> An unset or whitespace-only verb
    /// returns false rather than matching an empty token — that is the shape
    /// <c>RanksService</c>'s local <c>Eq</c> already required, and it is what
    /// stops a blank config field from swallowing every bang in chat.</para>
    ///
    /// <para>Pairs with <see cref="LooksLikeCommand"/>, which is the ORDINAL
    /// bang test the providers use. Both halves of the system — the tools and
    /// the graph / Live-Feed path — go through it, so a leading
    /// culture-ignorable code point can no longer raise a command signal on one
    /// half that is invisible to the other.</para>
    /// </remarks>
    public static class ChatVerb
    {
        /// <summary>
        /// Canonical form of a configured verb: trimmed, with every leading
        /// <c>!</c> removed, trimmed again. <c>"  !!hello "</c> → <c>"hello"</c>.
        /// </summary>
        /// <remarks>
        /// Stripping ALL leading bangs (rather than one) keeps parity with the
        /// <c>TrimStart('!')</c> the Polls and SongRequest configs have always
        /// used, so a config already normalized by those two round-trips
        /// unchanged. The second trim catches <c>"! hello"</c>, which a streamer
        /// types more often than <c>"!hello"</c> would suggest.
        /// </remarks>
        public static string Canonical(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            return raw.Trim().TrimStart('!').Trim();
        }

        /// <summary>
        /// True when a configured verb matches a parsed chat token. Both sides
        /// are canonicalized; an empty side never matches.
        /// </summary>
        /// <param name="configured">
        /// The streamer-supplied word, as stored in tool config — with or
        /// without a leading <c>!</c>.
        /// </param>
        /// <param name="token">
        /// The first word of a chat line with its leading <c>!</c> already
        /// removed by the caller's parser. Canonicalized anyway, so a caller
        /// that passes the raw word still behaves.
        /// </param>
        public static bool Matches(string? configured, string? token)
        {
            string a = Canonical(configured);
            if (a.Length == 0) return false;
            string b = Canonical(token);
            if (b.Length == 0) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The ORDINAL "is this a command line?" test — a non-empty body after a
        /// literal <c>'!'</c> in the first character position.
        /// </summary>
        /// <remarks>
        /// <para>Ordinal is deliberate and is the whole point of the helper.
        /// <c>string.StartsWith("!")</c> uses the <b>CurrentCulture</b>
        /// comparer, which skips culture-ignorable code points — so a line
        /// beginning with a soft hyphen (U+00AD) or a zero-width space followed
        /// by <c>!</c> reads as a command to that overload and NOT to
        /// <c>text[0] != '!'</c>. Hub runs full ICU, so the divergence is live,
        /// not theoretical.</para>
        /// <para>Before this helper the eleven tool services all used the
        /// ordinal char test while the graph dispatch and the Live Feed used the
        /// culture-sensitive string overload, which meant such a line raised a
        /// command signal that no tool could ever answer. The two halves now
        /// agree, deliberately, on ordinal — the tools' semantic — so a command
        /// signal implies some provider can actually see the word.</para>
        /// </remarks>
        public static bool LooksLikeCommand(string? text)
            => !string.IsNullOrEmpty(text) && text!.Length >= 2 && text[0] == '!';
    }
}
