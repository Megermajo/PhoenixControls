using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: the built-in chat-command dispatch — one ordered choke point
    // that every turnkey "pre-build" tool routes its chat commands through, instead
    // of each tool stacking its own if-block into ExecuteOnChatScriptsAsync.
    //
    // History: the Loyalty tool introduced the first built-in interceptor as an
    // inline `if (LoyaltyService.Instance.Active) { ... }` block right before the
    // logic-dir bail. As the counters / quotes / automod / soundboard / custom-command
    // tools land, six such blocks would accrete at the same spot — the anti-pattern
    // this partial replaces. Providers run in a FIXED order (moderation first so a
    // spammer's !cmd is timed out before it is answered; custom commands last so a
    // user trigger can never shadow an economy/counter/quote built-in), first-handled
    // wins, and a single Suppress decision replaces the per-tool inline suppress.
    //
    // DEFAULT-OFF IS A TOTAL NO-OP: a provider whose tool is disabled reports
    // Active==false and is skipped, so this changes nothing about the existing
    // chat/script path when no tool is enabled. The foundation started out carrying
    // only the Loyalty provider and now carries eleven (Automod, UserManagement,
    // Loyalty, UserQueue, Counters, Quotes, SongRequest, Polls, Ranks, Soundboard,
    // CustomCommands); each
    // new tool inserts one line into RegisterBuiltInChatProviders at its ordered slot. Note that
    // one TOOL can own two slots: User-Management holds both the observe-only greeting
    // tap and, four slots later, the handling viewer-queue provider — see the UserQueue
    // block for why splitting them is load-bearing rather than tidy.
    public partial class ScriptManager
    {
        /// <summary>
        /// Outcome of a built-in chat provider examining a message.
        /// <see cref="Handled"/> stops later providers from also handling it;
        /// <see cref="Suppress"/> (only meaningful when Handled) additionally skips
        /// the normal author on_chat script fan-out for this message.
        /// </summary>
        internal readonly struct BuiltInChatResult
        {
            public bool Handled { get; init; }
            public bool Suppress { get; init; }

            public static readonly BuiltInChatResult NotHandled = new() { Handled = false, Suppress = false };
            public static BuiltInChatResult HandledSuppress(bool suppress) => new() { Handled = true, Suppress = suppress };
        }

        /// <summary>
        /// One entry in the ordered built-in chat dispatch. <see cref="IsActive"/> is
        /// re-read per message (so live enable/disable takes effect immediately) and,
        /// when false, the provider is skipped with zero work.
        /// </summary>
        private readonly struct BuiltInChatProvider
        {
            public readonly string Name;
            public readonly Func<bool> IsActive;
            public readonly Func<ChatMessage, Task<BuiltInChatResult>> Handle;

            public BuiltInChatProvider(string name, Func<bool> isActive,
                                       Func<ChatMessage, Task<BuiltInChatResult>> handle)
            {
                Name = name;
                IsActive = isActive;
                Handle = handle;
            }
        }

        // Populated once by RegisterBuiltInChatProviders() during RegisterHubCommands
        // (startup) and only READ afterward on the chat path, so no lock is needed —
        // same lifetime contract as the command dictionary.
        private readonly List<BuiltInChatProvider> _builtInChatProviders = new();

        /// <summary>
        /// Registers the built-in chat-command providers in their FIXED dispatch order.
        /// Called from RegisterHubCommands after the tool services' command registrars.
        /// Order: Automod -> UserManagement -> Loyalty -> UserQueue -> Counters ->
        /// Quotes -> SongRequest -> Polls -> Ranks -> Soundboard -> CustomCommands.
        /// Insert each new tool's provider at its slot.
        /// </summary>
        private void RegisterBuiltInChatProviders()
        {
            _builtInChatProviders.Clear();

            // Automod (spam filter) — the AUTOMOD slot, at INDEX 0 (BEFORE Loyalty) per
            // the fixed order (Automod -> UserManagement -> Loyalty -> Counters -> ...).
            // Moderation MUST run before any command is answered: a spammer's !gamble
            // has to be timed out, not answered by Loyalty. The provider owns the
            // !permit command and moderates violations; it returns
            //   • HandledSuppress(SuppressDispatchWhenModerated) when it MODERATED,
            //   • HandledSuppress(true) when it handled !permit (owns the command),
            //   • NotHandled when the message is clean → falls through to Loyalty/Counters.
            // DEFAULT-OFF is a total no-op (AutomodService.Active gates the predicate).
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "Automod",
                () => AutomodService.Instance.Active,
                async msg =>
                {
                    AutomodOutcome outcome = await TryHandleAutomodAsync(msg).ConfigureAwait(false);
                    return outcome switch
                    {
                        AutomodOutcome.Moderated =>
                            BuiltInChatResult.HandledSuppress(AutomodService.Instance.Config.SuppressDispatchWhenModerated),
                        AutomodOutcome.Permit =>
                            BuiltInChatResult.HandledSuppress(true),
                        _ => BuiltInChatResult.NotHandled,
                    };
                }));

            // User-Management welcoming + first-time greeting — OBSERVE-ONLY, right
            // after Automod so a moderated first message is never greeted (Automod
            // handling stops the chain before this slot). Gated on the MASTER
            // toggle alone: even with both greeting halves off the tap records the
            // known-chatters baseline (the panel promises remembering starts "as
            // soon as the tool is enabled" — that is what makes a later greeting
            // opt-in safe for regulars). It ALWAYS returns NotHandled: the same
            // line may also be a command for a later provider or an authored
            // on_chat script, so greeting must never consume or suppress the
            // message. Its own faults are additionally swallowed inside the
            // service; the dispatcher's per-provider catch is the backstop.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "UserManagement",
                () => UserManagementService.Instance.Active,
                async msg =>
                {
                    await UserManagementService.Instance.OnChatMessageAsync(msg).ConfigureAwait(false);
                    return BuiltInChatResult.NotHandled;
                }));

            // Loyalty (points economy) — the LOYALTY slot, AFTER Automod.
            // Behaviour is byte-identical to the former inline block: handled when the
            // parser consumed the message, suppressing author dispatch when the tool's
            // config asks to.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "Loyalty",
                () => LoyaltyService.Instance.Active,
                async msg =>
                {
                    bool handled = await TryHandleLoyaltyChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(
                              LoyaltyService.Instance.Config.Commands.SuppressAuthorDispatchWhenHandled)
                        : BuiltInChatResult.NotHandled;
                }));

            // Viewer queue (the User-Management tool's fourth part) — the USERQUEUE slot,
            // IMMEDIATELY AFTER Loyalty. Three things about this placement are
            // deliberate, and getting any of them wrong is a live bug:
            //
            //   • It is a SECOND provider, not a behaviour change to the UserManagement
            //     slot above. That one is observe-only by contract — the greeting must
            //     never consume a line — and this one must consume the lines it answers.
            //   • It sits AFTER Loyalty, not at the UserManagement slot's index 1,
            //     because the queue's default join verb is "join" and so is
            //     LoyaltyRaffleConfig.JoinCommand. First-handled-wins means a queue
            //     answering !join from index 1 would silently kill every raffle join the
            //     moment both tools are on.
            //   • That ordering WORKS rather than merely being safer, because Loyalty's
            //     raffle declines !join when no raffle is running ("don't hijack !join").
            //     So the raffle wins while it is live and the queue answers the rest of
            //     the time — which is the behaviour a streamer running both actually
            //     wants. The panel warns when the two commands are configured the same,
            //     so the sharing is visible rather than inferred.
            //
            // Handled => suppress the author on_chat fan-out (mirrors Loyalty/Counters).
            // Gated on the master toggle AND the queue's own section gate, so the rest of
            // the User-Management tool can be on with the queue off and this slot is a
            // zero-work skip. DEFAULT-OFF is a total no-op.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "UserQueue",
                () => UserManagementService.Instance.QueueChatActive,
                async msg =>
                {
                    bool handled = await TryHandleUserQueueChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(true)
                        : BuiltInChatResult.NotHandled;
                }));

            // Counters (named databank-backed counters) — the COUNTERS slot, AFTER
            // Loyalty per the fixed order (Automod -> UserManagement -> Loyalty ->
            // UserQueue -> Counters -> ...). Handled => suppress the author on_chat fan-out
            // (mirrors Loyalty's suppress-when-handled default): a recognized
            // counter command shouldn't also spawn authored on_chat scripts.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "Counters",
                () => CountersService.Instance.Active,
                async msg =>
                {
                    bool handled = await TryHandleCountersChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(true)
                        : BuiltInChatResult.NotHandled;
                }));

            // Quotes (databank-backed quote store) — the QUOTES slot, AFTER Counters
            // per the fixed order (Automod -> UserManagement -> Loyalty -> Counters ->
            // Quotes -> ...). Handled => suppress the author on_chat fan-out (mirrors
            // Loyalty/Counters): a recognized !addquote / !quote / !delquote shouldn't
            // also spawn authored on_chat scripts.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "Quotes",
                () => QuotesService.Instance.Active,
                async msg =>
                {
                    bool handled = await TryHandleQuotesChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(true)
                        : BuiltInChatResult.NotHandled;
                }));

            // Song Request (YouTube request queue) — the SONGREQUEST slot, AFTER Quotes
            // and BEFORE CustomCommands per the fixed order. Its verbs are the most
            // generic words any built-in claims (!skip / !play / !pause / !next), so
            // sitting behind every other built-in is what keeps a shared word landing on
            // the tool that has owned it longer; sitting in FRONT of CustomCommands is the
            // same rule applied one step further out — a streamer-authored !skip must not
            // shadow the player's. Handled => suppress the author on_chat fan-out (mirrors
            // Loyalty/Counters/Quotes).
            //
            // ★ Automod at INDEX 0 would otherwise eat this tool's whole point: with its
            // Links rule ticked and BlockAll on (the default), a viewer's
            // "!sr https://youtu.be/…" is MODERATED before this provider is ever consulted,
            // because first-handled wins. That is resolved on the Automod side rather than
            // by reordering — see SongRequestService.ExemptRequestLinkHost and the
            // host-scoped waiver it feeds in AutomodService.TryDetect — precisely so the
            // moderation-runs-first ordering this file is built around stays intact. The
            // waiver is scoped to the ONE host the request line actually resolved, and only
            // over the BlockAll heuristic, so the streamer's own UrlBlockList still fires.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "SongRequest",
                () => SongRequestService.Instance.Active,
                async msg =>
                {
                    bool handled = await TryHandleSongRequestChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(true)
                        : BuiltInChatResult.NotHandled;
                }));

            // Polls & Betting — the POLLS slot, AFTER SongRequest and BEFORE CustomCommands
            // per the fixed order. Its two verbs (!vote / !bet) are ordinary English words a
            // streamer may well already own as a custom command, which is why two things
            // about this placement are deliberate:
            //
            //   • It sits IN FRONT of CustomCommands, so while a poll is live the built-in
            //     wins — a viewer typing !vote during a poll must reach the poll, not a
            //     static text reply that happens to share the word.
            //   • The parser DECLINES both verbs while no poll is running (it reads liveness
            //     before the role gate, so a role check can never consume the line either),
            //     which hands the word straight back to CustomCommands the rest of the time.
            //     That is the same "don't hijack the word while idle" rule the raffle
            //     applies to !join, and it only works from a slot AHEAD of the tool it is
            //     yielding to.
            //
            // Handled => suppress the author on_chat fan-out (mirrors
            // Loyalty/Counters/Quotes/SongRequest). DEFAULT-OFF is a total no-op.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "Polls",
                () => PollsService.Instance.Active,
                async msg =>
                {
                    bool handled = await TryHandlePollsChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(true)
                        : BuiltInChatResult.NotHandled;
                }));

            // Ranks (the watch-time / points ladder) — the RANKS slot, AFTER Polls and
            // BEFORE CustomCommands per the fixed order. Its two verbs default to !rank and
            // !ranks, and the word it deliberately does NOT take is !top: Loyalty owns that
            // one and its provider sits five slots earlier, so in a first-handled-wins
            // dispatch that logs nothing a Ranks !top would simply never run — a collision
            // whose only symptom is a tool that looks broken. Handled => suppress the author
            // on_chat fan-out (mirrors Loyalty/Counters/Quotes/SongRequest/Polls).
            // DEFAULT-OFF is a total no-op.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "Ranks",
                () => RanksService.Instance.Active,
                async msg =>
                {
                    bool handled = await TryHandleRanksChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(true)
                        : BuiltInChatResult.NotHandled;
                }));

            // Soundboard (chat-triggered clip playback) — the SOUNDBOARD slot, AFTER Ranks
            // and IMMEDIATELY BEFORE CustomCommands. This file reserved this exact slot by
            // name from the day the dispatch was written, and the reasoning still holds:
            //
            //   • A soundboard row is a BUILT-IN, so it sits ahead of CustomCommands like
            //     every other one — a streamer-authored !airhorn must not shadow the sound
            //     they mapped to !airhorn.
            //   • It sits LAST among the built-ins because its words are entirely
            //     streamer-invented and it ships with NO rows at all, so it claims nothing
            //     on a fresh install and can never take a word an older tool already owns.
            //     Every other built-in has shipped defaults; deferring to all of them is
            //     what keeps a shared word landing on the tool that has owned it longer.
            //
            // ★ The panel is what makes that survivable: first-handled-wins is SILENT (see
            // DispatchBuiltInChatAsync), so a row named after an existing verb would simply
            // never fire, with nothing in the log. The Soundboard page therefore checks each
            // row's word against the live configs of every provider ahead of it and says so
            // in the row, rather than leaving it to be discovered on stream.
            //
            // Handled => suppress the author on_chat fan-out (mirrors Counters/Quotes).
            // DEFAULT-OFF is a total no-op.
            //
            // ★ AND BECAUSE IT SUPPRESSES, IT OWES A ROOT. Suppress does not merely stop the
            // later providers — it skips the author on_chat fan-out for that line, so
            // mapping !airhorn on the Soundboard page silently switches off an Architect
            // graph that was handling !airhorn on on_chat. Every other suppressing tool here
            // ships an event root the orphaned graph can move onto (Counter.OnChanged,
            // Quote.OnAdded, Command.OnCustom, Loyalty.On*, Poll.On*, Rank.OnRankUp); this
            // one raises Soundboard.OnPlay for exactly that reason. A new suppressing
            // provider with no root of its own is a silent regression for anyone who had
            // already built the thing by hand — treat the root as part of the slot.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "Soundboard",
                () => SoundboardService.Instance.Active,
                async msg =>
                {
                    bool handled = await TryHandleSoundboardChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(true)
                        : BuiltInChatResult.NotHandled;
                }));

            // Custom Chat Commands (text/variable-only) — the CUSTOMCOMMANDS slot, the
            // FINAL provider (after Soundboard) per the fixed order (... -> Counters ->
            // Quotes -> SongRequest -> Polls -> Ranks -> Soundboard -> CustomCommands).
            // Registering LAST is load-bearing: a streamer-authored custom trigger must
            // NEVER shadow the economy / counter / quote / song / poll / rank / sound /
            // moderation built-ins, which all run first.
            // Handled => suppress the author on_chat fan-out (a custom command replaces a
            // legacy author on_chat for that trigger). DEFAULT-OFF is a total no-op.
            _builtInChatProviders.Add(new BuiltInChatProvider(
                "CustomCommands",
                () => CustomCommandsService.Instance.Active,
                async msg =>
                {
                    bool handled = await TryHandleCustomCommandsChatCommandAsync(msg).ConfigureAwait(false);
                    return handled
                        ? BuiltInChatResult.HandledSuppress(true)
                        : BuiltInChatResult.NotHandled;
                }));
        }

        /// <summary>
        /// Runs each active built-in chat provider in order until one handles the
        /// message. Returns true when the caller should stop (skip the author on_chat
        /// fan-out for this message); false to fall through to normal script dispatch.
        /// A provider fault is logged and treated as "not handled" so one tool can
        /// never wedge the chat path.
        /// </summary>
        private async Task<bool> DispatchBuiltInChatAsync(ChatMessage chatData)
        {
            var providers = _builtInChatProviders;
            for (int i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                if (!provider.IsActive()) continue;

                BuiltInChatResult result;
                try
                {
                    result = await provider.Handle(chatData).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("ScriptManager",
                        $"built-in chat provider '{provider.Name}' failed", ex);
                    continue;
                }

                if (result.Handled)
                    return result.Suppress; // first-handled wins; suppress => caller returns
            }

            return false;
        }
    }
}
