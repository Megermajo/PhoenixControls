using System;
using System.Collections.Generic;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // The built-in chat dispatch ORDER, and the one lookup that answers "does something
    // ahead of me already own this word?".
    //
    // ── Why this is a second type and not more methods on BuiltInCommandCatalog ──
    // The catalogue is deliberately pure: config in, rows out, no service lookup, because a
    // tool panel must be able to render its own UNSAVED edit (see the rationale at the top
    // of BuiltInCommandCatalog.cs). This question is the opposite shape — it is asked ABOUT
    // OTHER tools, which the asking panel holds no working copy of, so it must read those
    // tools' LIVE configs — and it additionally needs a fact the catalogue does not carry:
    // where each tool sits in the dispatch. Folding live-service reads into the catalogue
    // would contradict the one property that makes it correct for its own callers, so the
    // ordering and the reads live here and reach the catalogue for the verbs themselves.
    //
    // ── What this replaces ──────────────────────────────────────────────────
    // SoundboardViewModel.ReservedVerbOwner and PollsViewModel.ReservedVerbOwner were two
    // hand-maintained ~100-line copies of this, over two different PREFIXES of the same
    // order. Being copies, they drifted: both spelled Automod's permit verb, Loyalty's duel
    // replies and the Counters shapes as hard-coded literals, all of which are now
    // config-driven, so both under-reported the moment a streamer renamed one. And both
    // compared with a local `Eq` that only trimmed — no bang stripping — so a word saved as
    // "!points" in one tool matched at runtime (every provider goes through ChatVerb) while
    // being invisible to the warning. Going through the catalogue fixes both classes at
    // once: the rows are already ChatVerb.Canonical, and the incoming word is canonicalized
    // here before the compare.
    //
    // ── Two rules carried over from those two methods, deliberately ─────────
    //  * ENABLED STATE IS NOT CONSULTED. A tool that is off today shadows nothing today,
    //    but the streamer is choosing a word they will keep, and a warning that appears the
    //    moment an unrelated tool is switched on is worse than one that is there from the
    //    start. The rows carry an Enabled flag; this lookup ignores it, and the callers'
    //    wording ("while it is on") is what keeps that honest.
    //  * ONLY THE SLOTS AHEAD ARE CONSULTED. The dispatch is first-handled-wins, so a tool
    //    BEHIND the asker loses to it — naming one would send the streamer to rename the
    //    command that is working.

    /// <summary>
    /// A slot in <c>ScriptManager.RegisterBuiltInChatProviders</c>'s fixed dispatch order.
    /// The numeric values ARE the dispatch indices and are compared as such — keep them in
    /// step with that method, which is the authority.
    /// </summary>
    /// <remarks>
    /// One TOOL can own two slots: User-Management holds the observe-only greeting tap at
    /// index 1 and the handling viewer queue at index 3. That gap is load-bearing rather
    /// than incidental — the queue's default join verb is "join" and so is the Loyalty
    /// raffle's, and the queue sits behind Loyalty precisely so a live raffle wins the word
    /// (the raffle declines !join while none is running, handing it back). Collapsing the
    /// two slots here would report the queue as owning "join" against Loyalty, i.e. exactly
    /// backwards.
    /// </remarks>
    public enum BuiltInChatSlot
    {
        Automod = 0,
        /// <summary>The observe-only welcoming / greeting tap. Answers no chat verb.</summary>
        UserManagement = 1,
        Loyalty = 2,
        /// <summary>The viewer queue — User-Management's second, HANDLING slot.</summary>
        UserQueue = 3,
        Counters = 4,
        Quotes = 5,
        SongRequest = 6,
        Polls = 7,
        Ranks = 8,
        Soundboard = 9,
        CustomCommands = 10,
    }

    /// <summary>
    /// Which built-in tool already answers a chat verb, resolved against the dispatch order
    /// so the answer is limited to the tools that can actually shadow the asker. See the
    /// comment block at the top of <c>BuiltInCommandOrder.cs</c> for why this is separate
    /// from <see cref="BuiltInCommandCatalog"/> and which two hand-written tables it
    /// replaced.
    /// </summary>
    public static class BuiltInCommandOrder
    {
        /// <summary>
        /// The display name of the built-in tool at <paramref name="slot"/>, as a warning
        /// sentence names it. Written to read naturally after "belongs to" and before
        /// "already answers", which is why the queue carries its article.
        /// </summary>
        public static string DisplayName(BuiltInChatSlot slot) => slot switch
        {
            BuiltInChatSlot.Automod => "Automod",
            BuiltInChatSlot.UserManagement => "User Management",
            BuiltInChatSlot.Loyalty => "Loyalty",
            BuiltInChatSlot.UserQueue => "the viewer queue",
            BuiltInChatSlot.Counters => "Counters",
            BuiltInChatSlot.Quotes => "Quotes",
            BuiltInChatSlot.SongRequest => "Song Requests",
            BuiltInChatSlot.Polls => "Polls",
            BuiltInChatSlot.Ranks => "Ranks",
            BuiltInChatSlot.Soundboard => "Soundboard",
            _ => "Custom Commands",
        };

        /// <summary>
        /// The display name of the built-in tool that runs BEFORE <paramref name="slot"/>
        /// and already answers <paramref name="word"/>, or <c>null</c> when the word is
        /// free. Never throws: one tool's config being unreachable degrades to "that tool
        /// claims nothing" and is logged, exactly as the two hand-written predecessors did.
        /// </summary>
        /// <param name="slot">The asking tool's own slot. Only slots with a LOWER index are
        /// consulted — a tool behind the asker loses to it.</param>
        /// <param name="word">A CONFIGURED verb, with or without its leading '!'.</param>
        public static string? OwnerAhead(BuiltInChatSlot slot, string? word)
        {
            string w = ChatVerb.Canonical(word);
            if (w.Length == 0) return null;

            for (int i = 0; i < (int)slot; i++)
            {
                var ahead = (BuiltInChatSlot)i;
                foreach (var row in VerbsAt(ahead))
                {
                    // Sub-verb rows ("queue next", "raffle draw") are two-word PHRASES, and
                    // no parser compares a phrase against a token — the dispatch cuts the
                    // chat line at the first space. So a phrase can never shadow a
                    // top-level verb, and its parent word is already a row of its own.
                    // (The empty test is defensive: the catalogue never emits a blank verb,
                    // but ToolCommandInfo is a struct and a default one would carry null.)
                    string verb = row.Verb ?? string.Empty;
                    if (verb.Length == 0 || verb.IndexOf(' ') >= 0) continue;
                    // Both sides are canonical here: the catalogue built the row through
                    // ChatVerb.Canonical and w came through it above, so this is the same
                    // verdict ChatVerb.Matches would give without re-canonicalizing twice
                    // per row.
                    if (string.Equals(verb, w, StringComparison.OrdinalIgnoreCase))
                        return DisplayName(ahead);
                }
            }

            return null;
        }

        /// <summary>
        /// Every verb the tool at <paramref name="slot"/> currently claims, from its LIVE
        /// service config. Empty when the tool claims none, and empty (plus one logged
        /// error) when its config cannot be read.
        /// </summary>
        private static IReadOnlyList<ToolCommandInfo> VerbsAt(BuiltInChatSlot slot)
        {
            try
            {
                return slot switch
                {
                    BuiltInChatSlot.Automod =>
                        BuiltInCommandCatalog.ForAutomod(AutomodService.Instance.Config),
                    // Index 1 is the observe-only greeting tap: it always returns
                    // NotHandled and consumes no word, so it can shadow nothing. The
                    // User-Management VERBS all belong to the queue at index 3.
                    BuiltInChatSlot.UserManagement =>
                        Array.Empty<ToolCommandInfo>(),
                    BuiltInChatSlot.Loyalty =>
                        BuiltInCommandCatalog.ForLoyalty(LoyaltyService.Instance.Config),
                    BuiltInChatSlot.UserQueue =>
                        BuiltInCommandCatalog.ForUserManagement(UserManagementService.Instance.Config),
                    BuiltInChatSlot.Counters =>
                        BuiltInCommandCatalog.ForCounters(CountersService.Instance.Config),
                    BuiltInChatSlot.Quotes =>
                        BuiltInCommandCatalog.ForQuotes(QuotesService.Instance.Config),
                    BuiltInChatSlot.SongRequest =>
                        BuiltInCommandCatalog.ForSongRequest(SongRequestService.Instance.Config),
                    BuiltInChatSlot.Polls =>
                        BuiltInCommandCatalog.ForPolls(PollsService.Instance.Config),
                    BuiltInChatSlot.Ranks =>
                        BuiltInCommandCatalog.ForRanks(RanksService.Instance.Config),
                    BuiltInChatSlot.Soundboard =>
                        BuiltInCommandCatalog.ForSoundboard(SoundboardService.Instance.Config),
                    _ =>
                        BuiltInCommandCatalog.ForCustomCommands(CustomCommandsService.Instance.Config),
                };
            }
            catch (Exception ex)
            {
                // Per-slot rather than one catch around the whole scan: a service that
                // cannot be reached (early boot, a design-time host) must cost that one
                // tool's verbs, not every tool behind it in the order.
                GlobalLogger.Error("BuiltInCommandOrder", $"{DisplayName(slot)} verb scan failed", ex);
                return Array.Empty<ToolCommandInfo>();
            }
        }
    }
}
