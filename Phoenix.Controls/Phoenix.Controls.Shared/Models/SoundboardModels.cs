using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Soundboard pre-build tool — a no-code layer over
    // "on !cmd → play a clip on the overlay". CLIP-ONLY: a sound row never sends chat and
    // never touches TTS (audio.play_tts stays the Hub-local script command it always was);
    // it fires ONE shared widget graph with the clip path in Args3 and lets the browser do
    // the playing. Mirrors the Counters/Quotes/CustomCommands config shape: a single JSON
    // blob (SoundboardConfig) in the tool-private SYSTEM table ("SoundboardConfig").
    //
    // There is NO open data table. A soundboard owns no persistent data of its own — the
    // clips are files in data/media and the rows live in the blob.
    //
    // ★ WHY THE OVERLAY HOOKUP IS ONE PAIR FOR THE WHOLE BOARD, not per row: the point of
    // the design is that ONE widget graph (Visual.Arg("Args3") → Audio.Load(Path) →
    // Audio.Play) serves every sound, because the clip travels in the trigger payload
    // rather than being baked into the graph. Per-row layer/trigger fields would be a
    // second way to say the same thing and a second thing to get wrong. The cost is
    // serialization — see SoundboardConfig.TriggerName.

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber /
    /// VIP / Moderator / Broadcaster / Regular). Shared/Models is allowed; only
    /// Shared/UI is forbidden. Kept independent of CommandRoles/CounterRoles/QuoteRoles/
    /// LoyaltyRoles so the serialized Soundboard config never couples to another tool's
    /// naming.
    /// </summary>
    public sealed class SoundboardRoles
    {
        public bool Everyone { get; set; }
        public bool Subscriber { get; set; }
        public bool Vip { get; set; }
        public bool Moderator { get; set; }
        public bool Broadcaster { get; set; }

        /// <summary>The User-Management Regular group — a community-trust tier with no
        /// platform equivalent. Added with the tool rather than after it, but kept as its
        /// own property for the same reason the other tools carry it: the whole config is
        /// one JSON blob, so a blob written by a build that lacked the key deserializes it
        /// to false — the streamer never opted in, the gate is unchanged.</summary>
        public bool Regular { get; set; }

        public SoundboardRoles() { }
        public static SoundboardRoles All() => new() { Everyone = true };
        public static SoundboardRoles Mods() => new() { Moderator = true, Broadcaster = true };

        /// <summary>True when a chatter carrying these role flags passes the gate:
        /// Everyone OR any individually-ticked role they hold.</summary>
        public bool Allows(bool isSub, bool isVip, bool isMod, bool isBroadcaster, bool isRegular)
            => Everyone
            || (Subscriber && isSub)
            || (Vip && isVip)
            || (Moderator && isMod)
            || (Broadcaster && isBroadcaster)
            || (Regular && isRegular);
    }

    /// <summary>
    /// One soundboard row: a trigger word (+ optional aliases), the clip it plays, a role
    /// checkmark set, and a dual-bucket cooldown (per-user + channel-wide global).
    /// </summary>
    public sealed class SoundDef
    {
        /// <summary>The command word typed after '!' (e.g. "airhorn"). Matched
        /// case-insensitively, whole-token. Also the row's display name.</summary>
        public string Command { get; set; } = "";

        /// <summary>The clip, as a path RELATIVE to <c>data/media</c> and forward-slashed
        /// (e.g. <c>audio/airhorn.mp3</c>) — the exact shape <c>GET /api/media</c> hands
        /// back in its <c>rel</c> field.
        ///
        /// <para>★ RELATIVE IS NOT A STYLE PREFERENCE. This value is delivered to the
        /// widget graph as <c>Args3</c> and lands on a WIRED <c>Audio.Load.Path</c>, and a
        /// wired media path is required to be relative — the compositor refuses a leading
        /// '/', an <c>http(s):</c> URL and a <c>data:</c> URI outright. Anything else is
        /// worse than refused: a Windows absolute path (<c>C:\sounds\x.mp3</c>) matches
        /// none of those three, so it is neither rejected nor diagnosed — it URL-encodes
        /// into <c>/media/C%3A%5Csounds%5Cx.mp3</c>, 404s, and the streamer gets silence
        /// with nothing in the log. The tool therefore validates this field at its own
        /// boundary (SoundboardService.TryValidateClipPath) rather than trusting the
        /// browser to complain.</para></summary>
        public string ClipPath { get; set; } = "";

        /// <summary>Alternate trigger words that fire the same sound (matched
        /// case-insensitively, whole-token). Share the row's cooldown buckets.</summary>
        public List<string> Aliases { get; set; } = new();

        /// <summary>Who may play it. Default: everyone.</summary>
        public SoundboardRoles Roles { get; set; } = SoundboardRoles.All();

        /// <summary>Per-user cooldown (seconds); 0 disables the per-user bucket.</summary>
        public int UserCooldownSeconds { get; set; } = 0;

        /// <summary>Channel-wide (global) cooldown (seconds); 0 disables the global
        /// bucket. A soundboard is the tool most likely to want this one — it is the
        /// difference between a sound effect and a chat member holding the airhorn.</summary>
        public int GlobalCooldownSeconds { get; set; } = 0;

        /// <summary>Per-row enable toggle. A disabled row is skipped entirely (the word
        /// falls through to later providers and author scripts) even while the tool is
        /// enabled.</summary>
        public bool Enabled { get; set; } = true;
    }

    public sealed class SoundboardConfig
    {
        /// <summary>Master gate — the whole tool is dormant (no chat commands, no overlay
        /// fires) until the streamer enables it. Default OFF.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>The overlay layer holding the playback widget. Blank means the board
        /// has no output: rows still parse and consume their word, but nothing can play,
        /// so the service logs that instead of failing silently.</summary>
        public string LayerId { get; set; } = "";

        /// <summary>The trigger name on that layer's playback widget ("onTrigger:play",
        /// etc.). Every row fires this same trigger and distinguishes itself by the clip
        /// path in Args3.
        ///
        /// <para>★ CONSEQUENCE WORTH KNOWING: the Hub routes one FIFO per
        /// (layer, widget), gated on the browser's VISUAL_COMPLETE. Two sounds fired a
        /// moment apart at the SAME widget therefore play one after the other, not on top
        /// of each other. That is the correct behaviour for a soundboard (overlapping
        /// airhorns are the bug, not the feature) but it is not obvious, so the panel says
        /// it out loud.</para></summary>
        public string TriggerName { get; set; } = "";

        public List<SoundDef> Sounds { get; set; } = new();
    }
}
