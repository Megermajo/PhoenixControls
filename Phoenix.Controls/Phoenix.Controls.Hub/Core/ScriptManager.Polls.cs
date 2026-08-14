using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Polls & Betting — the Hub-side wiring of the chat-poll + points-betting
    // pre-build tool (sibling of Song Request / Quotes / Counters / Loyalty). Four
    // responsibilities:
    //
    //   1. Seam injection — PollsService is deliberately Hub-state-agnostic (the
    //      SchedulingService / SongRequestService shape), so everything that needs Hub state
    //      is wired at the top of RegisterPollsCommands: the Poll.On* script-event raise,
    //      the Loyalty charge / refund / batch-payout trio, the currency noun, the
    //      unprompted chat announcement, and the native Twitch mirror.
    //
    //   2. The seven poll.* script commands backing the Architect Poll.* nodes — five flow
    //      control commands and two inline value reads — the locked three-way manifest
    //      contract.
    //
    //   3. The BUILT-IN chat-command entry (TryHandlePollsChatCommandAsync), registered as
    //      the Polls provider in RegisterBuiltInChatProviders (AFTER SongRequest, BEFORE
    //      CustomCommands). Thin wrapper: the parse / gate / vote / stake logic lives on
    //      PollsService so it is unit-testable without a ScriptManager, and this wrapper
    //      supplies the per-platform reply send.
    //
    //   4. THE CAPABILITY GATE for the native mirror, which is the only genuinely new
    //      mechanism here — see MirrorNativePoll below.
    //
    // ── Why settlement costs zero chat slots ────────────────────────────────────
    // Nothing in this tool enters the MaxConcurrentChatScripts budget, and it does not have
    // to opt out of one: built-in chat providers run in DispatchBuiltInChatAsync, which sits
    // ABOVE RunChatScriptAsync where `_chatSemaphore.WaitAsync` is the only acquire. The
    // deferred half — the auto-close and its settlement — rides a System.Threading.Timer
    // scheduled outside the session lock whose callback goes through
    // AsyncErrorBoundary.SafeRunAsync, exactly like the Loyalty raffle's auto-draw, so it
    // never touches the chat lane at all. The one rule that follows: the SYNCHRONOUS half a
    // chat verb awaits must stay short, because a blocking provider stalls the chat lane
    // until AppConfig.ChatDispatchDetachSeconds elapses. A vote is in-memory; a bet is one
    // Loyalty debit.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        /// <summary>
        /// The Streamer.bot wrapper action that resolves a native Twitch prediction.
        ///
        /// ★ Deliberately NOT added to <c>PhxSbActions</c> on this branch. The shipped
        /// action pack does not define it and <c>twitch.resolve_prediction</c> is a logged
        /// no-op here, so listing it beside the real names would read as a capability this
        /// build has. The name exists ONLY as the thing the capability gate looks for, and
        /// it is spelled exactly as the extended pack's action — so the gate starts
        /// answering "available" by itself the moment that pack reaches an install, with
        /// nothing here to change.
        /// </summary>
        private const string SbResolvePredictionAction = "Phoenix: Resolve Prediction";

        // Twitch's own limits on the surfaces this mirrors. A poll or prediction outside
        // them is rejected by the platform, so the mirror refuses it here instead of
        // dispatching an action that fails somewhere the streamer will never see.
        private const int NativeMinOptions = 2;
        private const int NativeMaxOptions = 5;
        private const int NativeMinDurationSeconds = 30;
        private const int NativeMaxDurationSeconds = 1800;

        private void RegisterPollsCommands()
        {
            var svc = PollsService.Instance;

            // ── Seam: script events ──────────────────────────────────────────
            // Poll.OnOpened / Poll.OnClosed / Poll.OnSettled flow back through the
            // generic-event dispatcher with pre-built vars, fired-and-forgotten through
            // AsyncErrorBoundary.SafeRunAsync so a faulting handler routes to the log
            // instead of becoming an unobserved Task.
            svc.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "PollsService", $"RaiseScriptEvent({phoenixEvent})");
            };

            // ── Seam: the money ──────────────────────────────────────────────
            // A stake is a PURCHASE: LoyaltyService.ChargeAsync refuses an unaffordable
            // debit where RemoveAsync would floor the viewer at zero and report success.
            // Each half translates LoyaltyOutcome into the tool's own vocabulary so the poll
            // core never has to know the economy's types.
            svc.ChargePoints = async (login, amount) =>
            {
                var res = await LoyaltyService.Instance.ChargeAsync(login, amount, "poll bet").ConfigureAwait(false);
                return res.Outcome switch
                {
                    LoyaltyOutcome.Ok => PollChargeResult.Charged(amount),
                    // Disabled = the Loyalty master toggle is off; TableMissing = its
                    // currency table was never created. Both mean the streamer armed betting
                    // on an economy that cannot collect, which the viewer's reply names
                    // rather than reading as "you're broke".
                    LoyaltyOutcome.Disabled or LoyaltyOutcome.TableMissing => PollChargeResult.Fail(PollChargeOutcome.EconomyOff),
                    LoyaltyOutcome.NoFunds => PollChargeResult.Fail(PollChargeOutcome.NoFunds),
                    _ => PollChargeResult.Fail(PollChargeOutcome.Error),
                };
            };

            // ── Seam: can the economy collect AT ALL right now? ─────────────
            // The open-time betting guard. It exists because the two states
            // PollOpenOutcome.EconomyOff names — the Loyalty master toggle off, its currency
            // table renamed away — were previously only discovered per-bet, by which point
            // the poll was already open and taking votes with a pot that could never fill.
            //
            // Both halves are checked the way the money layer itself checks them:
            // LoyaltyService.Active is the master toggle every debit tests first, and the
            // balance table is the one LoyaltyDebitAsync reports TableMissing for. The table
            // is looked up rather than probed with a write — a readiness check must not
            // move points — and the lookup is one sqlite_master read per poll OPEN, not per
            // bet, so it costs nothing on the chat path.
            svc.EconomyReady = async () =>
            {
                var loyalty = LoyaltyService.Instance;
                if (!loyalty.Active) return false;
                string table = loyalty.Config?.Currency?.BalanceTable ?? "";
                if (table.Length == 0) return false;
                var tables = await DB.Instance.GetAllTableNamesAsync().ConfigureAwait(false);
                foreach (var t in tables)
                    if (string.Equals(t, table, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            };

            // RefundAsync never throws for a refused refund — it RETURNS Disabled (Loyalty
            // master toggle off), TableMissing (the currency table was renamed away) or
            // Invalid. Discarding that result is how a viewer's points vanish in silence, so
            // the seam carries the verdict back and the reason is named HERE, where the
            // economy's own vocabulary is still in scope; the service logs the viewer and
            // the amount on the other side of the boolean.
            svc.RefundPoints = async (login, amount) =>
            {
                var res = await LoyaltyService.Instance.RefundAsync(login, amount, "poll refund").ConfigureAwait(false);
                if (res.Ok) return true;
                GlobalLogger.Log(
                    $"Polls refund refused by the points economy: {res.Outcome} for " +
                    $"{amount.ToString(CultureInfo.InvariantCulture)} to {login}.",
                    "Polls", LogLevel.CriticalError);
                return false;
            };

            // The settlement primitive: ONE transaction, one lock hold, and a COUNT back.
            // The service compares that count against what it asked for — a short batch is
            // the shape a lost payout actually takes, and there is no exception to catch.
            svc.PayoutPoints = (credits, reason) =>
                LoyaltyService.Instance.PayoutManyAsync(credits, reason);

            svc.CurrencyProvider = () => LoyaltyService.Instance.Config.Currency.NamePlural;

            // ── Seam: the unprompted announcement ────────────────────────────
            // A poll opened from the panel and a poll closed by its own timer have no chat
            // message to reply to, so the opening line and the result go out through the
            // same SendTwitchChatCore path chat.send uses — the SchedulingService.ChatSend
            // precedent, no new send path.
            svc.Announce = line => SendTwitchChatCore(line, "polls");

            // ── Seam: the native Twitch mirror (capability-gated) ────────────
            svc.NativeMirror = MirrorNativePoll;

            // ── poll.* flow control commands ─────────────────────────────────
            // Void → return null; flow continues through the node's Done output.

            _engine.RegisterCommand("poll.open", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string title = StripBareQuotes(bound?.GetOrDefault<string>("Title", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string options = StripBareQuotes(bound?.GetOrDefault<string>("Options", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                int duration = (int)ReadPollLong(bound, "DurationSeconds", args, 2, fallback: 0);
                bool betting = ReadPollBool(bound, "Betting", args, 3);
                bool mirror = ReadPollBool(bound, "Mirror", args, 4);

                var parsed = PollsService.SplitOptions(options);
                if (parsed.Count < 2) return null;
                await PollsService.Instance.OpenAsync(title, parsed, duration, betting, mirror, "script").ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("poll.close", async (args) =>
            {
                await PollsService.Instance.CloseAsync("script").ConfigureAwait(false);
                return null;
            });

            _engine.RegisterCommand("poll.cancel", async (args) =>
            {
                await PollsService.Instance.CancelAsync("script").ConfigureAwait(false);
                return null;
            });

            // A graph-driven vote is the SAME capability the !vote verb exposes, not a
            // privileged back door: it is one vote for one named viewer, gated by the same
            // one-vote-each rule. An empty User resolves to the triggering chatter.
            _engine.RegisterCommand("poll.vote", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string option = StripBareQuotes(bound?.GetOrDefault<string>("Option", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                string user = ResolvePollUser(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1));
                int index = ResolveOptionIndex(option);
                if (user.Length == 0 || index < 0) return null;
                PollsService.Instance.Vote(user, index);
                return null;
            });

            _engine.RegisterCommand("poll.bet", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string option = StripBareQuotes(bound?.GetOrDefault<string>("Option", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                long amount = ReadPollLong(bound, "Amount", args, 1, fallback: 0);
                string user = ResolvePollUser(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2));
                int index = ResolveOptionIndex(option);
                if (user.Length == 0 || index < 0 || amount <= 0) return null;
                await PollsService.Instance.BetAsync(user, index, amount).ConfigureAwait(false);
                return null;
            });

            // ── poll.* inline value reads ────────────────────────────────────
            // Pure-data: the exporter emits the call inline (ComputeInlineValue / the
            // Poll.Status arm in ResolveOutputFromNode) and the engine round-trips the
            // return string into the node's primary value output.

            // Poll.Status is multi-output — State / IsOpen / Title / Leader / LeaderVotes /
            // TotalVotes / Pot / SecondsLeft must all describe the SAME instant, so the node
            // hoists ONE poll.status(base) call whose result vars back every socket. Same
            // ResultBase idiom as song.current / quote.get.
            _engine.RegisterCommand("poll.status", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string baseVar = StripBareQuotes(bound?.GetOrDefault<string>("ResultBase", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                var snap = PollsService.Instance.Snapshot();
                string state = StateToken(snap.State);
                if (!string.IsNullOrEmpty(baseVar))
                {
                    _engine.SetLocalResultVar($"{baseVar}_state", state);
                    _engine.SetLocalResultVar($"{baseVar}_is_open", snap.State == PollState.Open ? "true" : "false");
                    _engine.SetLocalResultVar($"{baseVar}_title", snap.Title);
                    _engine.SetLocalResultVar($"{baseVar}_leader", snap.Leader);
                    _engine.SetLocalResultVar($"{baseVar}_leader_votes", snap.LeaderVotes.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar($"{baseVar}_total_votes", snap.TotalVotes.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar($"{baseVar}_pot", snap.Pot.ToString(CultureInfo.InvariantCulture));
                    _engine.SetLocalResultVar($"{baseVar}_seconds_left", snap.SecondsLeft.ToString(CultureInfo.InvariantCulture));
                }
                return state;
            });

            _engine.RegisterCommand("poll.get_votes", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string option = StripBareQuotes(bound?.GetOrDefault<string>("Option", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                var snap = PollsService.Instance.Snapshot();
                int index = ResolveOptionIndex(option);
                // An empty / unmatched Option reads the whole poll's vote count rather than
                // failing the export — the same degrade-to-something-true posture
                // overlay.get and counter.get take on an unknown key.
                if (index < 0 || index >= snap.Options.Count)
                    return snap.TotalVotes.ToString(CultureInfo.InvariantCulture);
                return snap.Options[index].Votes.ToString(CultureInfo.InvariantCulture);
            });
        }

        // Lowercase literals, matching the live-channel key convention. Not
        // State.ToString() — the enum names are C#-cased and a rename would silently move
        // the value a graph branches on.
        internal static string StateToken(PollState state) => state switch
        {
            PollState.Open => "open",
            PollState.Closed => "closed",
            _ => "idle",
        };

        // An option is addressed by its 1-based NUMBER or by its LABEL, because a graph
        // knows one or the other depending on where the value came from: a chat arg is a
        // number, a hard-wired branch is the words the streamer typed into the panel.
        // Returns a ZERO-based index, or -1 when neither matches.
        private static int ResolveOptionIndex(string raw)
        {
            string v = (raw ?? string.Empty).Trim();
            if (v.Length == 0) return -1;
            if (PollsService.TryParseOptionToken(v, out int oneBased)) return oneBased - 1;

            var snap = PollsService.Instance.Snapshot();
            for (int i = 0; i < snap.Options.Count; i++)
                if (string.Equals(snap.Options[i].Label, v, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // Empty User → the triggering chatter, resolved through the IMMUTABLE trigger
        // context: {event.user_login} is the platform LOGIN BuildChatVars parks there, where
        // {user.name} is the operator-styled DISPLAY name. The distinction is load-bearing —
        // one vote and one stake per viewer are keyed on the login, so resolving off the
        // display name would let a viewer whose login is not simply lowercase(display) vote
        // twice. Mirrors ResolveSongUser.
        private string ResolvePollUser(string raw)
        {
            string u = StripBareQuotes(raw ?? string.Empty).Trim();
            if (u.Length == 0) u = StripBareQuotes(_engine.GetExecutionVar("event.user_login")).Trim();
            if (u.Length == 0) u = StripBareQuotes(_engine.GetExecutionVar("user.name")).Trim();
            return PollsService.NormalizeLogin(u);
        }

        // Whole-number read tolerant of the binder's Int coercion AND of a fractional
        // upstream value (math.divide hands back "2.5"), which floors. Blank falls back to
        // the supplied default. Mirrors ReadCounterLong / ReadSongInt.
        private long ReadPollLong(BoundArgs? bound, string key, string[] args, int index, long fallback)
        {
            string raw = StripBareQuotes(bound?.GetOrDefault<string>(key, ArgOrEmpty(args, index)) ?? ArgOrEmpty(args, index)).Trim();
            if (raw.Length == 0) return fallback;
            return ParseLongToken(raw);
        }

        // The binder coerces a Bool arg to "True"/"False", but a direct positional invoke
        // (tests, a hand-written .phx) can carry any of the truthy spellings the engine
        // accepts elsewhere — so parse the same set rather than trusting one.
        private bool ReadPollBool(BoundArgs? bound, string key, string[] args, int index)
        {
            string raw = StripBareQuotes(bound?.GetOrDefault<string>(key, ArgOrEmpty(args, index)) ?? ArgOrEmpty(args, index)).Trim();
            if (raw.Length == 0) return false;
            return raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("1", StringComparison.Ordinal)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        // ── Built-in chat-command entry (thin wrapper over the testable core) ──
        /// <summary>
        /// The built-in Polls chat commands (<c>!vote &lt;number&gt;</c> and
        /// <c>!bet &lt;number&gt; &lt;amount&gt;</c>). Delegates the parse / gate / vote /
        /// stake work to PollsService (unit-testable) and supplies the per-platform reply
        /// send. Returns true when a Polls command was consumed (so the caller suppresses
        /// the author on_chat fan-out). Default-OFF is a total no-op, and so is either verb
        /// while no poll is running.
        /// </summary>
        public Task<bool> TryHandlePollsChatCommandAsync(ChatMessage msg)
            => PollsService.Instance.TryHandleChatCommandAsync(
                   msg,
                   reply => { SendPollsReply(msg, reply); return Task.CompletedTask; });

        // Route the reply on the platform the command arrived on, reusing the exact
        // per-platform chat-send cores chat.send / *.send_chat use — mirrors
        // SendSongRequestReply, no new send path.
        private void SendPollsReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "polls"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "polls"); break;
                default:                    SendTwitchChatCore(reply, "polls"); break;
            }
        }

        // ── The native Twitch mirror, and its capability gate ────────────────
        /// <summary>
        /// Drives Twitch's own poll / prediction surface for a mirrored poll, and reports
        /// what it actually did.
        ///
        /// ★ THE GATE IS THE POINT. Every half of this runs through a Streamer.bot wrapper
        /// action that a given install may simply not have — the shipped action pack on this
        /// branch defines "Phoenix: End Poll" and "Phoenix: Create Prediction" but NOT
        /// "Phoenix: Create Poll" or "Phoenix: Resolve Prediction". Rather than assume, this
        /// asks the connect-probe's own record of what the connected Streamer.bot exposes
        /// (<c>_knownSbActions</c>, populated by ProbeStreamerBotActionsAsync) and refuses
        /// what it cannot do. The tool then runs chat-only, which is its whole product
        /// anyway; the extended action pack lights the mirror up by itself, with no code
        /// change here.
        ///
        /// ★ AND THE OPEN HALF REQUIRES THE CLOSE HALF. Opening a native prediction that
        /// cannot later be resolved leaves the channel's prediction panel occupied until the
        /// streamer clears it by hand — strictly worse than never opening one. So an OPEN is
        /// only reported <see cref="PollMirrorOutcome.Mirrored"/> when BOTH actions are
        /// present, and the service only closes a mirror whose open engaged.
        /// </summary>
        private PollMirrorOutcome MirrorNativePoll(PollMirrorRequest request)
        {
            if (!WS.Instance.IsConnected) return PollMirrorOutcome.NotConnected;

            switch (request.Action)
            {
                case PollMirrorAction.OpenPoll:
                {
                    if (!FitsNativeSurface(request)) return PollMirrorOutcome.Unsupported;
                    if (!SbActionAvailable(PhxSbActions.CreatePoll) || !SbActionAvailable(PhxSbActions.EndPoll))
                        return PollMirrorOutcome.Unavailable;
                    DispatchNamedAction("poll.open (twitch mirror)", PhxSbActions.CreatePoll, new
                    {
                        title = MirrorTitle(request),
                        // The SB wrapper's Create Poll sub-action splits the raw
                        // comma-separated list, which is why the labels go over as one
                        // string rather than five fields.
                        choices = string.Join(",", request.Options),
                        duration = ClampNativeDuration(request.DurationSeconds).ToString(CultureInfo.InvariantCulture),
                    });
                    return PollMirrorOutcome.Mirrored;
                }

                case PollMirrorAction.ClosePoll:
                {
                    if (!SbActionAvailable(PhxSbActions.EndPoll)) return PollMirrorOutcome.Unavailable;
                    // SB's End Poll sub-action acts on the ACTIVE poll; the id is passed
                    // through for forks that accept one and is otherwise advisory — DoAction
                    // cannot return the id the create minted, so there is none to send.
                    DispatchNamedAction("poll.close (twitch mirror)", PhxSbActions.EndPoll, new { pollId = "" });
                    return PollMirrorOutcome.Mirrored;
                }

                case PollMirrorAction.OpenPrediction:
                {
                    if (!FitsNativeSurface(request)) return PollMirrorOutcome.Unsupported;
                    if (!SbActionAvailable(PhxSbActions.CreatePrediction) || !SbActionAvailable(SbResolvePredictionAction))
                        return PollMirrorOutcome.Unavailable;
                    // "Phoenix: Create Prediction" wires %outcomeA%..%outcomeE%. A/B are
                    // required; C/D/E are sent empty when this poll has fewer options, and
                    // the action only adds the non-empty ones.
                    DispatchNamedAction("poll.open (twitch prediction mirror)", PhxSbActions.CreatePrediction, new
                    {
                        title = MirrorTitle(request),
                        outcomeA = OptionAt(request, 0),
                        outcomeB = OptionAt(request, 1),
                        outcomeC = OptionAt(request, 2),
                        outcomeD = OptionAt(request, 3),
                        outcomeE = OptionAt(request, 4),
                        duration = ClampNativeDuration(request.DurationSeconds).ToString(CultureInfo.InvariantCulture),
                    });
                    return PollMirrorOutcome.Mirrored;
                }

                case PollMirrorAction.ResolvePrediction:
                {
                    if (!SbActionAvailable(SbResolvePredictionAction)) return PollMirrorOutcome.Unavailable;
                    // A tie or an unvoted poll has no winning outcome, and Twitch has no
                    // "resolve to nothing" — the streamer must CANCEL the prediction there.
                    // Refusing here (and letting the service log it) beats resolving to an
                    // arbitrary outcome and paying out the wrong side of a real prediction.
                    if (request.WinningIndex < 0) return PollMirrorOutcome.Unsupported;
                    DispatchNamedAction("poll.close (twitch prediction mirror)", SbResolvePredictionAction, new
                    {
                        // The extended pack's custom-C# path resolves the ACTIVE prediction
                        // by ZERO-based outcome index, which is the same numbering the poll
                        // options carry here.
                        outcome = request.WinningIndex.ToString(CultureInfo.InvariantCulture),
                    });
                    return PollMirrorOutcome.Mirrored;
                }

                default:
                    return PollMirrorOutcome.Unsupported;
            }
        }

        // Twitch caps a poll/prediction at five choices and rejects a blank question, so a
        // wider (or unnamed) chat poll simply does not fit the native surface. The chat poll
        // is unaffected — MaxOptions is the tool's own, deliberately larger, limit.
        private static bool FitsNativeSurface(PollMirrorRequest request)
            => request.Options is { Count: >= NativeMinOptions and <= NativeMaxOptions }
            && MirrorTitle(request).Length > 0;

        // Twitch needs a question; a poll opened with a blank title still has options, so
        // borrow them rather than refusing outright.
        private static string MirrorTitle(PollMirrorRequest request)
        {
            string t = (request.Title ?? string.Empty).Trim();
            if (t.Length > 0) return t;
            return request.Options is { Count: > 0 } ? string.Join(" vs ", request.Options) : "";
        }

        private static string OptionAt(PollMirrorRequest request, int index)
            => request.Options is not null && index < request.Options.Count ? request.Options[index] : "";

        private static int ClampNativeDuration(int seconds)
            => Math.Clamp(seconds, NativeMinDurationSeconds, NativeMaxDurationSeconds);
    }
#pragma warning restore CS1998
}
