using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Polls band — the chat-poll + points-betting nodes ("Pre-Builds ▾ → Polls",
    // sibling to Song Requests / Quotes / Counters / Loyalty). Architect-only authoring
    // surface: the Hub's PollsService owns the live poll and every points move goes
    // through the Loyalty economy; Visualist never touches either.
    //
    // Three shapes, mirroring the Counters / Song Requests bands exactly:
    //   * Control nodes (category "Polls") — void, Flow-in → Done-out. Emit poll.*
    //     commands via SimpleEmitDescriptors (see ExporterRegistry.Handlers1.RegisterPolls).
    //   * Value nodes (category "Polls") — no flow, resolved inline. "Polls" is
    //     deliberately NOT a _pureDataCategory — the control nodes must emit flow — so both
    //     are routed by TITLE instead, exactly like the Song.* / Timer.Get*
    //     value nodes. Poll.Status is multi-output and hoists ONE poll.status(base) read so
    //     all eight sockets describe the same instant; Poll.GetVotes is a single inline
    //     poll.get_votes(...) call expression.
    //   * Event nodes (category "Events") — output-only roots that MIRROR Counter.OnChanged:
    //     category "Events" makes them entry points, ProcessEventNode's trigger-switch
    //     fallback emits `on_event(Poll.OnOpened):` and friends (matching the phoenixEvent
    //     strings PollsService raises), and a dedicated Poll.On* arm in
    //     ScriptExporter.ResolveOutputFromNode maps every output socket to the exact
    //     {event.*} token the runtime binds. That arm is MANDATORY here rather than
    //     decorative: TotalVotes / WinnerVotes / WinnerCount / DurationSeconds would each
    //     hit the generic Events tail and emit the LOWER-CASED socket name
    //     ({event.totalvotes}), while the runtime binds the snake_case spelling.
    //
    // ARCHITECT-FIRST PARITY, spelled out because this band is the proof: every verb the
    // Polls panel and the two chat commands can perform has a node here — open, close,
    // cancel, vote, stake — and every point at which the tool acts has an event root. There
    // is no capability the tool exposes that a hand-built graph cannot reach. The one
    // deliberate exception is the tool's CONFIGURATION (command words, role gates, limits),
    // which no node in this product writes — a graph
    // that runs every stream must not be able to silently re-configure a tool the
    // streamer tuned by hand (the boundary the retired Counter.Delete node documented).
    public static partial class NodeRegistry
    {
        private static void RegisterPollsTemplates()
        {
            // MediumPurple header — distinct from Song Requests' IndianRed, Counters'
            // SteelBlue, Timer's Coral, Loyalty's MediumSeaGreen and Giveaway's Goldenrod
            // while keeping the "Pre-Builds" siblings visually adjacent. Unused by any
            // other band.
            Color polls = Color.MediumPurple;

            // ── Value nodes (no flow — resolved inline) ──────────────────────
            AddTemplate("Poll.Status", "Polls", polls,
                "Reads the live poll in one go: whether it is open, its question, the leading option and its votes, the staked pot and the seconds left. Every output describes the same instant.",
                null,
                new[]
                {
                    ("State", ColString), ("IsOpen", ColBool), ("Title", ColString),
                    ("Leader", ColString), ("LeaderVotes", ColNumber), ("TotalVotes", ColNumber),
                    ("Pot", ColNumber), ("SecondsLeft", ColNumber),
                });

            AddTemplate("Poll.GetVotes", "Polls", polls,
                "Outputs how many votes one option has. Address it by its number (1 is the first option) or by its exact label; leave it empty for the poll's total vote count.",
                new[] { ("Option", ColString) },
                new[] { ("Votes", ColNumber) });

            // ── Control nodes (void, Flow-in → Done-out) ─────────────────────
            AddTemplate("Poll.Open", "Polls", polls,
                "Opens a chat poll. Options is one comma-separated list, so the same node runs a two-way or a five-way poll. Betting takes points stakes, and only works while the Polls panel's betting switch is on.",
                new[]
                {
                    ("Flow", ColExec), ("Title", ColString), ("Options", ColString),
                    ("DurationSeconds", ColNumber), ("Betting", ColBool), ("Mirror", ColBool),
                },
                new[] { ("Done", ColExec) });

            AddTemplate("Poll.Close", "Polls", polls,
                "Closes the poll early, resolves the winning option from the votes and settles any pot. Doing nothing has the same effect a moment later — a poll closes itself when its time runs out.",
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            // MONEY, NOT A TIDY-UP — the line this node holds. Cancel is the only way to end
            // a poll without picking a winner, and it exists because a poll cut short has no
            // honest outcome: keeping the pot would be taking points for a question that
            // never got an answer.
            AddTemplate("Poll.Cancel", "Polls", polls,
                "Ends the poll with no winner and hands every stake back. Use this to abandon a poll; use Poll.Close to end one early and still pay out.",
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("Poll.Vote", "Polls", polls,
                "Casts one viewer's vote — the same thing the chat vote command does, gated by the same one-vote-each rule. Leave User empty for the viewer whose message triggered this graph.",
                new[] { ("Flow", ColExec), ("Option", ColString), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Poll.Bet", "Polls", polls,
                "Stakes a viewer's points on an option, charging them straight away. Refused when they cannot afford it, when they have already staked, or when the poll takes no bets — no points move on a refusal.",
                new[] { ("Flow", ColExec), ("Option", ColString), ("Amount", ColNumber), ("User", ColString) },
                new[] { ("Done", ColExec) });

            // ── Event nodes (category "Events" — output-only roots) ──────────
            AddTemplate("Poll.OnOpened", "Events", polls,
                "Fires when a poll opens, from the panel, a Poll.Open node or a chat command. Options lists the choices in order, and Betting says whether this one takes stakes.",
                null,
                new[]
                {
                    ("Flow", ColExec), ("Title", ColString), ("Options", ColString),
                    ("DurationSeconds", ColNumber), ("Betting", ColBool),
                });

            AddTemplate("Poll.OnClosed", "Events", polls,
                "Fires when a poll ends, however it ended. Winner is the option with the most votes, and is empty on a tie or when nobody voted — it is never an arbitrary pick, because points can ride on it.",
                null,
                new[]
                {
                    ("Flow", ColExec), ("Title", ColString), ("Winner", ColString),
                    ("WinnerVotes", ColNumber), ("TotalVotes", ColNumber), ("Options", ColString),
                });

            AddTemplate("Poll.OnSettled", "Events", polls,
                "Fires after a betting poll's pot is resolved, and only when something was staked. Outcome is paid or refunded — a tie, an unvoted poll, a cancel, or a winning option nobody backed all return every stake.",
                null,
                new[]
                {
                    ("Flow", ColExec), ("Title", ColString), ("Winner", ColString),
                    ("Outcome", ColString), ("Pot", ColNumber), ("Winners", ColString),
                    ("WinnerCount", ColNumber), ("Currency", ColString),
                });

            // ── Socket-level hover help (canvas pin pop-ups + doc form) ──────
            SetSocketDescriptions("Poll.Status", new()
            {
                { "State",       "open while votes are being taken, closed while the result is still showing, idle when no poll has run." },
                { "IsOpen",      "True only while the poll is taking votes. Wire it straight into a branch." },
                { "Title",       "The poll's question, or empty when it was opened without one." },
                { "Leader",      "The option with the most votes — empty on a tie or with no votes at all." },
                { "LeaderVotes", "How many votes the leading option holds." },
                { "TotalVotes",  "Votes cast across every option." },
                { "Pot",         "Everything staked on this poll, in points. 0 when it takes no bets." },
                { "SecondsLeft", "Whole seconds still to run. 0 once the poll has closed." },
            });
            SetSocketDescriptions("Poll.GetVotes", new()
            {
                { "Option", "Which option to read — its number (1 is the first) or its exact label. Empty reads the poll's total." },
                { "Votes",  "How many votes that option holds." },
            });
            SetSocketDescriptions("Poll.Open", new()
            {
                { "Title",           "The question chat sees. Optional, but a mirrored Twitch poll needs one and will borrow the options if it is blank." },
                { "Options",         "The choices, comma-separated: Yes, No, Maybe. Blank and duplicate entries are dropped; two are the minimum." },
                { "DurationSeconds", "How long voting stays open. Leave it at 0 for the length set in the Polls panel." },
                { "Betting",         "Take points stakes on this poll. Only works while the Polls panel's betting switch is on." },
                { "Mirror",          "Also run this as Twitch's own poll (or prediction, when betting is on). Skipped with a note when the Streamer.bot action pack cannot do it." },
            });
            SetSocketDescriptions("Poll.Vote", new()
            {
                { "Option", "Which option to vote for — its number (1 is the first) or its exact label." },
                { "User",   "Whose vote this is. Empty means the viewer whose message triggered this graph." },
            });
            SetSocketDescriptions("Poll.Bet", new()
            {
                { "Option", "Which option to back — its number (1 is the first) or its exact label." },
                { "Amount", "How many points to stake. Must clear the minimum and stay under the maximum set in the Polls panel." },
                { "User",   "Who is staking. Empty means the viewer whose message triggered this graph." },
            });
            SetSocketDescriptions("Poll.OnOpened", new()
            {
                { "Title",           "The poll's question." },
                { "Options",         "The choices, comma-separated, in the order they were given." },
                { "DurationSeconds", "How long the poll will take votes." },
                { "Betting",         "True when this poll takes points stakes." },
            });
            SetSocketDescriptions("Poll.OnClosed", new()
            {
                { "Title",       "The poll's question." },
                { "Winner",      "The winning option — empty on a tie or when nobody voted." },
                { "WinnerVotes", "How many votes the winner took." },
                { "TotalVotes",  "Votes cast across every option." },
                { "Options",     "The choices, comma-separated, in the order they were given." },
            });
            SetSocketDescriptions("Poll.OnSettled", new()
            {
                { "Title",       "The poll's question." },
                { "Winner",      "The backed option that won — empty when every stake was returned instead." },
                { "Outcome",     "paid when the pot went to the winners, refunded when every stake was handed back." },
                { "Pot",         "Everything that was staked on the poll." },
                { "Winners",     "Who was paid, comma-separated. Empty on a refund." },
                { "WinnerCount", "How many viewers were paid." },
                { "Currency",    "The plural name of your points, for a chat line." },
            });

            // Fuzzy spawn-search aliases — the gap between what a user types
            // ("poll", "vote", "bet", "predict") and the Poll.* titles.
            SetKeywords("Poll.Status",    "poll", "vote", "status", "state", "leader", "result", "read");
            SetKeywords("Poll.GetVotes",  "poll", "vote", "votes", "count", "tally", "option");
            SetKeywords("Poll.Open",      "poll", "vote", "open", "start", "ask", "question", "predict", "bet");
            SetKeywords("Poll.Close",     "poll", "vote", "close", "end", "finish", "result");
            SetKeywords("Poll.Cancel",    "poll", "vote", "cancel", "abort", "refund", "stop");
            SetKeywords("Poll.Vote",      "poll", "vote", "cast", "choose", "pick");
            SetKeywords("Poll.Bet",       "poll", "bet", "stake", "wager", "gamble", "predict", "points");
            SetKeywords("Poll.OnOpened",  "poll", "vote", "opened", "onopen", "event", "trigger");
            SetKeywords("Poll.OnClosed",  "poll", "vote", "closed", "onclose", "result", "event", "trigger");
            SetKeywords("Poll.OnSettled", "poll", "bet", "settled", "payout", "refund", "event", "trigger");
        }
    }
}
