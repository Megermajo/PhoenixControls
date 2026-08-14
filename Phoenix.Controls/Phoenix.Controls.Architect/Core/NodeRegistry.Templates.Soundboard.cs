using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Soundboard band — chat-triggered clip playback ("Pre-Builds ▾ → Soundboard",
    // sibling to Alerts / Custom Commands). Architect-only authoring surface: the Hub's
    // SoundboardService owns the row list and fires the clip at the board's widget;
    // Visualist owns the widget graph that actually plays it.
    //
    // ★ EXACTLY ONE NODE, AND IT IS AN EVENT ROOT — the rest of this tool deliberately has
    // no Architect surface. Firing a widget with an Args payload is what Visual.Trigger
    // already does (visual.trigger_queued), so a play/stop/list command here would be a
    // second way to say something the canvas can already say, and Architect-first parity
    // holds with zero new engine surface. See ScriptManager.Soundboard.cs, which registers
    // no script command for the same reason ScriptManager.Alerts.cs does not.
    //
    // ★ WHY THE ROOT IS NOT OPTIONAL, though. The tool's built-in chat provider returns
    // HandledSuppress, and per the dispatch contract that additionally skips the author
    // on_chat fan-out. So the moment a streamer maps !airhorn on the Soundboard page, an
    // Architect graph that was handling !airhorn on on_chat goes dark — first-handled-wins,
    // logged nowhere. Without a root to move that graph onto, adding the tool is a silent
    // regression for anyone who had already built the thing by hand. This node is the
    // replacement seam, which is why it ships with the tool rather than after it.
    //
    // Five-site sync (see BUILD_CONTRACT §C), all of them keyed on the EXACT title
    // "Soundboard.OnPlay" — the phoenixEvent string SoundboardService.RaisePlayed raises:
    //   1. ScriptExporter.ResolveOutputFromNode — a dedicated arm, MANDATORY here because
    //      the "User" output collides with the generic Events override list and would
    //      collapse to {user.name}, which nothing binds on a soundboard run.
    //   2. AutocompleteScopeBuilder — the inline-attribute token popup.
    //   3. VarChainAnalyzer.ResultEmitterMap — the Trace-Variable picker.
    //   4. AnalyzerNodeKeyIntegrityTests — automatic; it fails the build on a misspelling
    //      in either of the two above.
    //   5. No lang bubble: the Description below is an inline English literal, which is
    //      what eleven of the twelve existing tool roots do.
    public static partial class NodeRegistry
    {
        private static void RegisterSoundboardTemplates()
        {
            // Sienna — unused by any other band (Counters/Users SteelBlue, Timer Coral,
            // Loyalty MediumSeaGreen, Automod/SongRequest IndianRed, Quotes/Polls
            // MediumPurple, CustomCommands Tomato, Ranks DarkCyan) and deliberately NOT the
            // DarkSlateBlue the Audio.* nodes carry: those are Hub-LOCAL audio (audio.play /
            // audio.play_tts speak on the streamer's own machine) and this band is browser
            // audio in OBS. They share the word "sound" and nothing else, so they must not
            // share a header colour either.
            Color soundboard = Color.Sienna;

            // ── Event node (category "Events" — output-only root) ────────────
            // MIRRORS Rank.OnRankUp: null inputs, Flow output first. Category "Events" is
            // what makes it a script entry point; ProcessEventNode's trigger-switch
            // fallback then emits `on_event(Soundboard.OnPlay):`.
            AddTemplate("Soundboard.OnPlay", "Events", soundboard,
                "Fires when the Soundboard actually plays a clip. Use it to rebuild anything your old on_chat handler did for that word: the built-in answers the word first and stops the chat script from running at all. Command is the row's word, User who asked for it, Clip the file that was sent to the overlay.",
                null,
                new[] { ("Flow", ColExec), ("Command", ColString), ("User", ColString),
                        ("Clip", ColString) });

            // ── Socket-level hover help (canvas pin pop-ups + doc form) ──────
            SetSocketDescriptions("Soundboard.OnPlay", new()
            {
                { "Command", "The row's own command word, without the '!'. An alias fires the row it belongs to, so this is always the canonical word rather than the one that was typed." },
                { "User",    "The viewer who asked for the sound, as their display name." },
                { "Clip",    "The clip that was sent to the overlay, relative to your media library (for example audio/airhorn.mp3)." },
            });

            // Fuzzy spawn-search aliases — the gap between what a user types ("sound",
            // "sfx", "airhorn", "clip") and the one Soundboard.* title.
            SetKeywords("Soundboard.OnPlay",
                "soundboard", "sound", "sfx", "clip", "audio", "play", "airhorn", "event", "trigger");
        }
    }
}
