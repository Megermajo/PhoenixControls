using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Song Requests band — the YouTube request-queue nodes ("Pre-Builds ▾ → Song
    // Requests", sibling to Counters / Timer / Loyalty / Quotes / Giveaway).
    // Architect-only authoring surface: the Hub's SongRequestService owns the queue and
    // the transport state; the overlay player that actually plays the audio is a Visualist
    // widget and is not addressed from here.
    //
    // ★ ARCHITECT-FIRST PARITY. Every verb the tool exposes to chat has a node in this
    // band, because no tool may reach a capability a graph cannot:
    //   !sr → Song.Request      !song → Song.Current       !next → Song.UpNext
    //   !when → Song.QueuePosition                          !wrongsong → Song.RemoveLast
    //   !voteskip → Song.VoteSkip                           !volume → Song.SetVolume /
    //                                                        Song.Current's Volume output
    //   !skip → Song.Skip       !pause → Song.Pause         !play → Song.Resume
    //   !removesong → Song.Remove                           !srclear → Song.Clear
    // The panel's approve/deny buttons are Song.Approve / Song.Deny for the same reason.
    //
    // ★ NAMESPACE. This band is Song.*, never Audio.* or Queue.*: Audio.Play /
    // Audio.PlayTts / Audio.SetVolume already own the "volume" concept for local sound
    // playback, and Queue.Push / Pop / Length / Clear are the generic data-structure band.
    // A song-player volume node is Song.SetVolume; the song queue is not a Queue.* node.
    //
    // Three shapes, mirroring the Quotes band exactly:
    //   * Control nodes (category "Song Requests") — void, Flow-in → Done-out. Emit
    //     song.* commands via SimpleEmitDescriptors (ExporterRegistry.Handlers1.
    //     RegisterSongRequest).
    //   * Value nodes (category "Song Requests") — no flow, resolved inline. Song.Current
    //     and Song.UpNext are multi-output and hoist ONE read each (their arms in
    //     ScriptExporter.ResolveOutputFromNode); Song.QueueLength / Song.QueuePosition
    //     resolve straight through ComputeInlineValue. "Song Requests" is deliberately NOT
    //     a _pureDataCategory — the control nodes above must still emit flow — so all four
    //     are routed by TITLE instead.
    //   * Event nodes (category "Events") — output-only roots. Category "Events" makes
    //     them script entry points and ProcessEventNode's trigger-switch fallback emits
    //     `on_event(Song.OnQueued)` etc., matching the phoenixEvent strings
    //     SongRequestService raises. Each has a dedicated arm in
    //     ScriptExporter.ResolveOutputFromNode: the VideoId / DurationSeconds / SkippedBy
    //     sockets would otherwise hit the generic tail and emit {event.videoid} /
    //     {event.durationseconds} / {event.skippedby} while the runtime binds
    //     event.video_id / event.duration_seconds / event.skipped_by — a pin wired to
    //     nothing, with no error anywhere.
    public static partial class NodeRegistry
    {
        private static void RegisterSongRequestTemplates()
        {
            // IndianRed header — distinct from Counters' SteelBlue, Timer's Coral,
            // Loyalty's MediumSeaGreen, Quotes' MediumPurple and Giveaway's Goldenrod,
            // while keeping the "Pre-Builds" siblings visually adjacent. Unused elsewhere.
            Color song = Color.IndianRed;

            // ── Value nodes (no flow — resolved inline) ──────────────────────
            // Song.Current's five outputs all describe the SAME instant: one
            // song.current(base) read backs them, so a graph can't print a title from
            // before a skip beside a state from after it. Volume lives here rather than on
            // its own node because it is player state, readable whether or not a track is
            // selected, and folding it saves a whole command whose only job was one number.
            AddTemplate("Song.Current", "Song Requests", song,
                "Reads what the song player is doing right now — the playing track, who asked for it, and the volume. All outputs describe the same moment.",
                null,
                new[]
                {
                    ("Title", ColString), ("Requester", ColString), ("VideoId", ColString),
                    ("State", ColString), ("Volume", ColNumber),
                });

            AddTemplate("Song.UpNext", "Song Requests", song,
                "Reads the next song that will actually play. A request still waiting for a moderator is skipped over — it is not up next yet.",
                null,
                new[] { ("Title", ColString), ("Requester", ColString), ("VideoId", ColString) });

            AddTemplate("Song.QueueLength", "Song Requests", song,
                "Outputs how many songs are waiting. The one playing is not counted.",
                null,
                new[] { ("Count", ColNumber) });

            AddTemplate("Song.QueuePosition", "Song Requests", song,
                "Outputs where a viewer's next song sits in the queue, counting from 1. Outputs 0 when they have nothing waiting. Leave User empty for the viewer who triggered the script.",
                new[] { ("User", ColString) },
                new[] { ("Position", ColNumber) });

            // ── Control nodes (void, Flow-in → Done-out) ─────────────────────
            AddTemplate("Song.Request", "Song Requests", song,
                "Adds a song to the queue on a viewer's behalf. Query takes a YouTube link, a bare video id, or words to search for. Every cap, price and approval rule applies exactly as it would for a chat request.",
                new[] { ("Flow", ColExec), ("Query", ColString), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.Skip", "Song Requests", song,
                "Skips the song that is playing and starts the next one. Does nothing when nothing is playing.",
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.Pause", "Song Requests", song,
                "Holds the song that is playing. The queue keeps its order — nothing is dropped.",
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.Resume", "Song Requests", song,
                "Starts the music: resumes a held song, or picks up the first queued one when nothing is selected. This is the node behind the !play command.",
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.Remove", "Song Requests", song,
                "Removes the waiting song at Position (counting from 1) and refunds anything it was charged. The playing song has no position and cannot be removed this way — skip it instead.",
                new[] { ("Flow", ColExec), ("Position", ColNumber) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.RemoveLast", "Song Requests", song,
                "Removes a viewer's most recent waiting request and refunds it — the !wrongsong command, for a mis-pasted link. Leave User empty for the viewer who triggered the script.",
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.Clear", "Song Requests", song,
                "Empties the waiting queue and refunds every charged request in it. The song that is playing keeps playing.",
                new[] { ("Flow", ColExec) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.SetVolume", "Song Requests", song,
                "Sets the song player's volume from 0 to 100. This is the music player only — Audio.SetVolume is the separate node for locally played sounds.",
                new[] { ("Flow", ColExec), ("Volume", ColNumber) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.VoteSkip", "Song Requests", song,
                "Counts one viewer's vote to skip the playing song, and skips it once enough people have voted. One vote per viewer per song. Leave User empty for the viewer who triggered the script.",
                new[] { ("Flow", ColExec), ("User", ColString) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.Approve", "Song Requests", song,
                "Approves the waiting request at Position so it can play. Only does something while approval is switched on and that request is still awaiting a verdict.",
                new[] { ("Flow", ColExec), ("Position", ColNumber) },
                new[] { ("Done", ColExec) });

            AddTemplate("Song.Deny", "Song Requests", song,
                "Rejects the waiting request at Position, removing it and refunding whatever it cost. Only affects a request that is still awaiting a verdict.",
                new[] { ("Flow", ColExec), ("Position", ColNumber) },
                new[] { ("Done", ColExec) });

            // ── Event nodes (category "Events" — output-only roots) ──────────
            AddTemplate("Song.OnQueued", "Events", song,
                "Fires when a song joins the queue for real. With approval switched on that is the moment a moderator approves it, not the moment it was asked for — so an announcement never celebrates a request that is about to be rejected.",
                null,
                new[]
                {
                    ("Flow", ColExec), ("Title", ColString), ("Requester", ColString),
                    ("VideoId", ColString), ("Position", ColNumber),
                });

            AddTemplate("Song.OnPlay", "Events", song,
                "Fires each time a song is handed to the player — from a skip, a !play, or a graph advancing the queue. DurationSeconds is 0 when the length is unknown.",
                null,
                new[]
                {
                    ("Flow", ColExec), ("Title", ColString), ("Requester", ColString),
                    ("VideoId", ColString), ("DurationSeconds", ColNumber),
                });

            AddTemplate("Song.OnSkip", "Events", song,
                "Fires when a playing song is cut short rather than finishing. SkippedBy names who did it — a moderator's display name, \"voteskip\" when the vote passed, or \"script\" from a Song.Skip node.",
                null,
                new[]
                {
                    ("Flow", ColExec), ("Title", ColString), ("Requester", ColString),
                    ("VideoId", ColString), ("SkippedBy", ColString),
                });

            // ── Socket-level hover help (canvas pin pop-ups + doc form) ──────
            SetSocketDescriptions("Song.Current", new()
            {
                { "Title", "The playing song's title — or its YouTube id when no API key is set and the title could not be read." },
                { "Requester", "Who asked for the playing song." },
                { "VideoId", "The playing song's YouTube video id (empty when nothing is playing)." },
                { "State", "idle, playing, or paused." },
                { "Volume", "The player volume, 0 to 100." },
            });
            SetSocketDescriptions("Song.UpNext", new()
            {
                { "Title", "The next song's title, or its YouTube id when the title is unknown. Empty when nothing is queued." },
                { "Requester", "Who asked for the next song." },
                { "VideoId", "The next song's YouTube video id." },
            });
            SetSocketDescriptions("Song.QueueLength", new()
            {
                { "Count", "How many songs are waiting. The one playing is not counted." },
            });
            SetSocketDescriptions("Song.QueuePosition", new()
            {
                { "User", "Whose place to look up, as a LOGIN — pass {event.user_login}, not {user.name}. Leave empty for the viewer who triggered the script." },
                { "Position", "Their place in the queue, counting from 1. 0 means they have nothing waiting." },
            });
            SetSocketDescriptions("Song.Request", new()
            {
                { "Query", "A YouTube link, a bare 11-character video id, or words to search for. Searching by words needs the streamer's YouTube API key; links and ids never do." },
                { "User", "Who the request is for, as a LOGIN — pass {event.user_login}, not {user.name}. The queue keys every cap, lookup and refund on the login, so a display name here mis-keys any viewer whose two spellings differ. Leave empty for the viewer who triggered the script." },
            });
            SetSocketDescriptions("Song.Remove", new()
            {
                { "Position", "Which waiting song to drop, counting from 1. An unknown position does nothing." },
            });
            SetSocketDescriptions("Song.RemoveLast", new()
            {
                { "User", "Whose most recent waiting request to drop, as a LOGIN — pass {event.user_login}, not {user.name}. Leave empty for the viewer who triggered the script." },
            });
            SetSocketDescriptions("Song.SetVolume", new()
            {
                { "Volume", "0 to 100. Values outside that range are pulled back into it." },
            });
            SetSocketDescriptions("Song.VoteSkip", new()
            {
                { "User", "Who is voting, as a LOGIN — pass {event.user_login}, not {user.name}. Leave empty for the viewer who triggered the script. One vote per viewer per song." },
            });
            SetSocketDescriptions("Song.Approve", new()
            {
                { "Position", "Which waiting request to approve, counting from 1." },
            });
            SetSocketDescriptions("Song.Deny", new()
            {
                { "Position", "Which waiting request to reject, counting from 1. Anything it cost is refunded." },
            });
            SetSocketDescriptions("Song.OnQueued", new()
            {
                { "Title", "The queued song's title, or its video id when unknown." },
                { "Requester", "Who asked for it." },
                { "VideoId", "Its YouTube video id." },
                { "Position", "Where it landed in the queue, counting from 1." },
            });
            SetSocketDescriptions("Song.OnPlay", new()
            {
                { "Title", "The song now playing." },
                { "Requester", "Who asked for it." },
                { "VideoId", "Its YouTube video id." },
                { "DurationSeconds", "Its length in seconds, or 0 when unknown." },
            });
            SetSocketDescriptions("Song.OnSkip", new()
            {
                { "Title", "The song that was cut short." },
                { "Requester", "Who had asked for it." },
                { "VideoId", "Its YouTube video id." },
                { "SkippedBy", "Who skipped it — a name, \"voteskip\", or \"script\"." },
            });

            // Fuzzy spawn-search aliases — the gap between what a user types ("music",
            // "sr", "youtube") and the Song.* titles.
            SetKeywords("Song.Current",       "song", "music", "current", "nowplaying", "playing", "sr");
            SetKeywords("Song.UpNext",        "song", "music", "next", "upnext", "queue", "sr");
            SetKeywords("Song.QueueLength",   "song", "music", "queue", "length", "count", "how many", "sr");
            SetKeywords("Song.QueuePosition", "song", "music", "queue", "position", "place", "when", "sr");
            SetKeywords("Song.Request",       "song", "music", "request", "sr", "add", "youtube", "queue");
            SetKeywords("Song.Skip",          "song", "music", "skip", "next", "sr");
            SetKeywords("Song.Pause",         "song", "music", "pause", "hold", "sr");
            SetKeywords("Song.Resume",        "song", "music", "play", "resume", "start", "sr");
            SetKeywords("Song.Remove",        "song", "music", "remove", "delete", "removesong", "sr");
            SetKeywords("Song.RemoveLast",    "song", "music", "wrongsong", "undo", "remove", "sr");
            SetKeywords("Song.Clear",         "song", "music", "clear", "empty", "srclear", "sr");
            SetKeywords("Song.SetVolume",     "song", "music", "volume", "loud", "quiet", "sr");
            SetKeywords("Song.VoteSkip",      "song", "music", "voteskip", "vote", "skip", "sr");
            SetKeywords("Song.Approve",       "song", "music", "approve", "accept", "moderate", "sr");
            SetKeywords("Song.Deny",          "song", "music", "deny", "reject", "moderate", "sr");
            SetKeywords("Song.OnQueued",      "song", "music", "queued", "onqueued", "event", "trigger", "sr");
            SetKeywords("Song.OnPlay",        "song", "music", "play", "onplay", "event", "trigger", "sr");
            SetKeywords("Song.OnSkip",        "song", "music", "skip", "onskip", "event", "trigger", "sr");
        }
    }
}
