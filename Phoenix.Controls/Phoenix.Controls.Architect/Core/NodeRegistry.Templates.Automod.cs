using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Automod band — a SINGLE event node ("Automod.OnViolation") exposed so a
    // streamer can hand-build the exact reaction the turnkey spam-filter tool fires
    // automatically (Rule-1 parity: the tool's trigger point is a first-class node).
    // There is NO new imperative command and NO manifest/exporter descriptor — the
    // moderation verbs (twitch.timeout / twitch.ban / kick.* …) already exist, and an
    // Events-category node emits via ProcessEventNode's generic on_event({Title})
    // fallback, matching the phoenixEvent string AutomodService raises.
    //
    // The template + prose + autocomplete carry ZERO Streamer.bot / Twitch references
    // (pillar rule) — this is a pure "when moderation acted" trigger with plain outputs.
    public static partial class NodeRegistry
    {
        private static void RegisterAutomodTemplates()
        {
            // IndianRed header — a distinct moderation tint, kept clear of the other
            // Pre-Builds siblings (Counters SteelBlue, Timer Coral, Loyalty
            // MediumSeaGreen, Giveaway Goldenrod). Used only by this node.
            Color automod = Color.IndianRed;

            // ── Event node (category "Events" — output-only root) ────────────
            // MIRROR Counter.OnChanged / Timer.OnZero: null inputs, Flow output first.
            // Category "Events" makes it a script entry point; the exporter emits
            // on_event(Automod.OnViolation) via the trigger-switch fallback, matching
            // the phoenixEvent string AutomodService raises. A dedicated arm in
            // ScriptExporter.ResolveOutputFromNode maps the string outputs to the
            // event.* tokens (User/Message would otherwise collapse to {user.*}).
            AddTemplate("Automod.OnViolation", "Events", automod,
                "Fires when the spam filter acts on a message. User is who was moderated, Rule is which check fired, Action is what was done (Warn/Delete/Timeout/Ban), Reason explains why, and Message is the offending text.",
                null,
                new[]
                {
                    ("Flow", ColExec),
                    ("User", ColString),
                    ("Rule", ColString),
                    ("Action", ColString),
                    ("Reason", ColString),
                    ("Message", ColString),
                });

            // ── Socket-level hover help (canvas pin pop-ups + doc form) ──────
            SetSocketDescriptions("Automod.OnViolation", new()
            {
                { "User",    "The chatter who was moderated." },
                { "Rule",    "Which check fired (e.g. Links, Excessive caps, Blocklist word)." },
                { "Action",  "What the filter did: Warn, Delete, Timeout or Ban." },
                { "Reason",  "A short explanation of why the rule fired." },
                { "Message", "The offending message text." },
            });

            // Fuzzy spawn-search aliases.
            SetKeywords("Automod.OnViolation",
                "automod", "spam", "filter", "moderation", "violation", "onviolation",
                "timeout", "ban", "event", "trigger", "caps", "links");
        }
    }
}
