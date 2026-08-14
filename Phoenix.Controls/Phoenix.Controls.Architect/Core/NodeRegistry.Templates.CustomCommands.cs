using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Custom Chat Commands band — the Architect parity surface for the text/variable-only
    // custom-command tool ("Pre-Builds ▾ → Custom Commands").
    //
    // ARCHITECT-FIRST NOTE: a custom command is just "on !cmd → send templated text",
    // which is ALREADY hand-buildable today (Chat.Message → Chat.Send). So this tool adds
    // exactly ONE node — the event root Command.OnCustom — and NOTHING else: no imperative
    // command, no exporter descriptor, and (deliberately) NO Counter.* nodes. The
    // {count}/{count.next} tokens CONSUME the already-built Counters tool, so the Counter.*
    // band already exists and is not duplicated here.
    //
    // Command.OnCustom MIRRORS Counter.OnChanged / Quote.OnAdded: category "Events" makes
    // it a script entry point, the ProcessEventNode trigger-switch fallback emits
    // `on_event(Command.OnCustom):` (matching the phoenixEvent string CustomCommandsService
    // raises), and a dedicated arm in ScriptExporter.ResolveOutputFromNode maps the
    // Command/User/Args outputs to {event.command}/{event.user}/{event.args}.
    public static partial class NodeRegistry
    {
        private static void RegisterCustomCommandsTemplates()
        {
            // Tomato header — distinct from Quotes' MediumPurple, Counters' SteelBlue,
            // Automod's IndianRed-family, Timer's Coral, Loyalty's MediumSeaGreen and
            // Giveaway's Goldenrod, while keeping the "Pre-Builds" siblings adjacent.
            Color custom = Color.Tomato;

            // ── Event node (category "Events" — output-only root) ────────────
            // null inputs, Flow output first (mirrors Counter.OnChanged / Quote.OnAdded).
            AddTemplate("Command.OnCustom", "Events", custom,
                "Fires when a viewer runs one of your custom chat commands. Command is the trigger word, User is who ran it, Args is everything they typed after it.",
                null,
                new[] { ("Flow", ColExec), ("Command", ColString), ("User", ColString), ("Args", ColString) });

            // ── Socket-level hover help (canvas pin pop-ups + doc form) ──────
            SetSocketDescriptions("Command.OnCustom", new()
            {
                { "Command", "The command word that fired (the trigger, without the leading '!')." },
                { "User",    "The chatter who ran the command." },
                { "Args",    "Everything the chatter typed after the command word (empty if nothing)." },
            });

            // Fuzzy spawn-search aliases — the gap between what a user types
            // ("command", "custom", "!cmd") and the Command.OnCustom title.
            SetKeywords("Command.OnCustom", "command", "commands", "custom", "chat", "oncustom", "event", "trigger");
        }
    }
}
