using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: THE POLL — one live session at a time, its votes, its staked pot and
    // the settlement that pays or refunds it.
    //
    // This file is a deliberate clone of the Loyalty raffle's session machine
    // (LoyaltyService.Games.cs), because a betting poll is the same problem: money is taken
    // outside the session lock, and the session can close underneath a charge that is still
    // in flight. The four rules that make it correct, all inherited from there:
    //
    //   1. RESERVE, then confirm. A bettor's login is claimed under the lock BEFORE the
    //      debit runs, so two concurrent !bet lines from the same viewer cannot both charge.
    //   2. Only CONFIRMED stakes size the pot or share it. A debit still in flight neither
    //      grows the pot nor can win a slice of one it has not paid into.
    //   3. The close CLAIMS the session under the lock (`Closed = true`) and disposes the
    //      auto-timer there, which is what makes the timer close and a manual close
    //      idempotent — whichever gets there first wins and the other is a no-op.
    //   4. A charge that lands after the claim is REFUNDED, not kept. The confirm and the
    //      claim share one lock, so a stake is either confirmed before the close snapshots
    //      the pot or refunded here — never half of each.
    //
    // The one thing this file does NOT clone is the raffle's draw: a raffle picks winners at
    // random, a poll pays whoever backed the option that won the VOTE. The payout is
    // parimutuel — each winner takes their share of the whole pot in proportion to their own
    // stake, which returns their stake and adds a slice of the losing side. A largest-
    // remainder pass makes the paid total equal the pot EXACTLY, so integer division cannot
    // quietly evaporate points.
    public sealed partial class PollsService
    {
        // ── Session state (NEVER persisted — see PollsModels.cs) ────────────
        private sealed class PollSession
        {
            public string Title = "";
            public string[] Labels = Array.Empty<string>();
            public readonly int[] VoteCounts;

            /// <summary>login → chosen option index. One vote each; moved rather than added
            /// when <see cref="PollsConfig.AllowVoteChange"/> is on.</summary>
            public readonly Dictionary<string, int> Voters = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>RESERVATIONS — claimed the instant a bet starts, so two concurrent
            /// bets by the same viewer can't both charge. A login in here has NOT
            /// necessarily paid (the debit runs outside the lock).</summary>
            public readonly HashSet<string> BetReserved = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Stakes whose charge actually LANDED. The settlement sizes the pot
            /// and picks payees from this map ONLY.</summary>
            public readonly Dictionary<string, (int Index, long Amount)> Stakes =
                new(StringComparer.OrdinalIgnoreCase);

            public bool BettingEnabled;
            public int DurationSeconds;
            public long OpenedAtMs;
            public long EndsAtMs;
            public long ClosedAtMs;
            public System.Threading.Timer? AutoTimer;
            public bool Closed;
            public bool Settled;
            public int WinnerIndex = -1;
            public PollMirrorMode MirrorMode = PollMirrorMode.None;
            public bool MirrorEngaged;
            public string StartedBy = "";

            public PollSession(int optionCount) => VoteCounts = new int[optionCount];
        }

        private readonly object _pollGate = new();
        private PollSession? _session;

        /// <summary>
        /// Whether a poll is accepting votes right now — a side-effect-free probe, unlike
        /// <see cref="Vote"/> which answers the same question only by attempting one.
        ///
        /// <para>★ This exists for the chat parser. The <c>!vote</c> / <c>!bet</c> arms have
        /// to decide whether to DECLINE the message so a later provider (a streamer's own
        /// custom command) can answer it, and they must reach that decision WITHOUT
        /// consuming the line on a role check — refusing a non-subscriber a vote in a poll
        /// that does not exist would consume <c>!vote</c> and silently starve everything
        /// behind this tool.</para>
        /// </summary>
        public bool IsOpen
        {
            get { lock (_pollGate) { ExpireLingerLocked(); return _session is not null && !_session.Closed; } }
        }

        // A closed poll keeps its result readable for ResultLingerSeconds and is then
        // dropped, which is what lets the overlay RETRACT its keys instead of painting a
        // finished poll forever. Expiry is lazy — checked at the top of every locked section
        // — rather than a second timer: the overlay heartbeat already runs once a second,
        // so the result is dropped within one tick of its linger elapsing without another
        // timer to dispose, race and leak.
        private void ExpireLingerLocked()
        {
            var s = _session;
            if (s is null || !s.Closed) return;
            long linger = Math.Max(0, _config.ResultLingerSeconds) * 1000L;
            if (Clock.ElapsedMilliseconds - s.ClosedAtMs >= linger) _session = null;
        }

        // ── Snapshots ───────────────────────────────────────────────────────
        /// <summary>
        /// A read-only projection of the poll, taken under the lock so the panel, the read
        /// nodes and the live-channel publish all describe ONE instant rather than three
        /// interleaved ones. An idle tool reports <see cref="PollState.Idle"/> with no
        /// options rather than null, so every caller has one shape to render.
        /// </summary>
        public PollSnapshot Snapshot()
        {
            lock (_pollGate)
            {
                ExpireLingerLocked();
                return SnapshotLocked();
            }
        }

        // The snapshot the overlay paints, or null when there is nothing to paint (idle, or
        // a closed poll whose linger has elapsed). Distinct from Snapshot() because "idle"
        // is a value the panel renders and a RETRACTION on the live channel.
        private PollSnapshot? PaintableSnapshot()
        {
            lock (_pollGate)
            {
                ExpireLingerLocked();
                return _session is null ? null : SnapshotLocked();
            }
        }

        private PollSnapshot SnapshotLocked()
        {
            var s = _session;
            if (s is null) return new PollSnapshot();

            int total = 0;
            for (int i = 0; i < s.VoteCounts.Length; i++) total += s.VoteCounts[i];

            var stakeByOption = new long[s.Labels.Length];
            long pot = 0;
            foreach (var kv in s.Stakes)
            {
                if (kv.Value.Index >= 0 && kv.Value.Index < stakeByOption.Length)
                    stakeByOption[kv.Value.Index] += kv.Value.Amount;
                pot += kv.Value.Amount;
            }

            var options = new List<PollOptionView>(s.Labels.Length);
            for (int i = 0; i < s.Labels.Length; i++)
            {
                options.Add(new PollOptionView
                {
                    Index = i,
                    Label = s.Labels[i],
                    Votes = s.VoteCounts[i],
                    Stake = stakeByOption[i],
                    // Guarded division: a poll with zero votes reports 0, never a NaN the
                    // live channel would have to normalise away.
                    Percent = total > 0 ? Math.Round(s.VoteCounts[i] * 100.0 / total, 1) : 0,
                });
            }

            int leaderIndex = s.Closed ? s.WinnerIndex : ResolveWinnerLocked(s);
            int secondsLeft = 0;
            if (!s.Closed)
            {
                long remainMs = s.EndsAtMs - Clock.ElapsedMilliseconds;
                if (remainMs > 0) secondsLeft = (int)Math.Ceiling(remainMs / 1000.0);
            }

            return new PollSnapshot
            {
                State = s.Closed ? PollState.Closed : PollState.Open,
                Title = s.Title,
                Options = options,
                TotalVotes = total,
                Leader = leaderIndex >= 0 ? s.Labels[leaderIndex] : "",
                LeaderVotes = leaderIndex >= 0 ? s.VoteCounts[leaderIndex] : 0,
                BettingEnabled = s.BettingEnabled,
                Pot = pot,
                BettorCount = s.Stakes.Count,
                SecondsLeft = secondsLeft,
                DurationSeconds = s.DurationSeconds,
                Mirror = s.MirrorMode,
                MirrorEngaged = s.MirrorEngaged,
            };
        }

        // The option with a STRICTLY highest vote count, or -1 on a tie / no votes at all.
        // Never an arbitrary pick: points ride on this number, and "first option wins ties"
        // would hand a pot to whoever the streamer happened to type first.
        private static int ResolveWinnerLocked(PollSession s)
        {
            int best = -1, bestVotes = 0, tied = 0;
            for (int i = 0; i < s.VoteCounts.Length; i++)
            {
                int v = s.VoteCounts[i];
                if (v == 0) continue;
                if (v > bestVotes) { best = i; bestVotes = v; tied = 1; }
                else if (v == bestVotes) tied++;
            }
            return tied == 1 ? best : -1;
        }

        // ── Open ────────────────────────────────────────────────────────────
        /// <summary>
        /// Opens a poll. Fails when the tool is off, a poll is already running, or the
        /// options don't make a poll. <paramref name="betting"/> is honoured only when the
        /// tool's own betting gate is on — a per-poll flag can never switch the money on
        /// behind the streamer's back.
        ///
        /// The auto-close timer is scheduled OUTSIDE the lock, carries THIS session as its
        /// state, and closes only that session — so a manual close before it fires no-ops
        /// the callback, and a callback already queued when the timer was disposed cannot
        /// close the poll opened after it. The role gate for who may open is a command-layer
        /// concern.
        /// </summary>
        public async Task<PollOpenResult> OpenAsync(string title, IReadOnlyList<string> options,
            int durationSeconds, bool betting, bool mirrorNative, string byWhom)
        {
            if (!Active) return PollOpenResult.Fail(PollOpenOutcome.Disabled);
            var cfg = _config;

            // Order matters: CleanOptions reports an over-long list by returning NOTHING, so
            // testing the count first would answer "give me at least two options" to someone
            // who just gave twenty — the opposite of the actual problem.
            var labels = CleanOptions(options, cfg.MaxOptions, out bool tooMany);
            if (tooMany) return PollOpenResult.Fail(PollOpenOutcome.TooManyOptions);
            if (labels.Length < 2) return PollOpenResult.Fail(PollOpenOutcome.NotEnoughOptions);

            bool wantBetting = betting && cfg.BettingEnabled;
            // A poll that says it takes stakes but has no way to collect them would take
            // votes and quietly never charge anyone. Refuse the open instead — the same
            // rule Song Request applies to a price it cannot collect.
            //
            // ★ This tests the ECONOMY, not the wiring. It used to read
            // `ChargePoints is null`, which asks whether the seam was ever assigned —
            // ScriptManager.Polls does that unconditionally at command registration, long
            // before a panel exists, so in a shipped Hub the guard could never fire while
            // PollOpenOutcome.EconomyOff and its panel string advertised that it did. The
            // conditions it was written for (the Loyalty master toggle off, its currency
            // table missing) were only discovered per-bet, one refused viewer at a time,
            // with the poll already open and taking votes.
            if (wantBetting && !await EconomyCanCollectAsync().ConfigureAwait(false))
                return PollOpenResult.Fail(PollOpenOutcome.EconomyOff);

            int duration = durationSeconds > 0 ? durationSeconds : cfg.DefaultDurationSeconds;
            duration = Math.Clamp(duration, 5, 3600);

            PollSession session;
            lock (_pollGate)
            {
                ExpireLingerLocked();
                if (_session is not null && !_session.Closed) return PollOpenResult.Fail(PollOpenOutcome.AlreadyOpen);

                long now = Clock.ElapsedMilliseconds;
                session = new PollSession(labels.Length)
                {
                    Title = (title ?? string.Empty).Trim(),
                    Labels = labels,
                    BettingEnabled = wantBetting,
                    DurationSeconds = duration,
                    OpenedAtMs = now,
                    EndsAtMs = now + duration * 1000L,
                    StartedBy = NormalizeLogin(byWhom),
                };
                _session = session;
            }

            // Mirror OUTSIDE the lock — it dispatches over the Streamer.bot socket. A
            // refused mirror is not a refused poll: the chat poll is the product, the native
            // surface is a bonus, and saying so once beats failing the whole open.
            //
            // The CALLER decides whether to ask, always: the panel hands over its
            // MirrorToNative checkbox and a Poll.Open node hands over its Mirror pin. There
            // is no "unset" third state falling back on a config default, which is what
            // keeps the node's pin meaning exactly what it says on the canvas.
            PollMirrorOutcome mirror = PollMirrorOutcome.NotRequested;
            if (mirrorNative)
            {
                var mode = wantBetting ? PollMirrorMode.Prediction : PollMirrorMode.Poll;
                mirror = InvokeMirror(new PollMirrorRequest(
                    mode == PollMirrorMode.Prediction ? PollMirrorAction.OpenPrediction : PollMirrorAction.OpenPoll,
                    session.Title, labels, duration, -1));
                if (mirror == PollMirrorOutcome.Mirrored)
                    lock (_pollGate) { session.MirrorMode = mode; session.MirrorEngaged = true; }
            }

            // Scheduled after the mirror so the countdown a viewer sees and the one Twitch
            // shows start from the same moment rather than a socket round-trip apart. The
            // re-check is not paranoia: the mirror dispatch runs outside the lock, so a
            // panel close landing in that window would dispose an AutoTimer field that is
            // still null and this line would then arm a timer on an already-closed session.
            // The callback would no-op (the close claim is idempotent), but the timer itself
            // would sit undisposed for the poll's whole duration.
            var autoTimer = new System.Threading.Timer(AutoCloseCallback, session, duration * 1000, Timeout.Infinite);
            bool armed;
            lock (_pollGate)
            {
                armed = !session.Closed && ReferenceEquals(_session, session);
                if (armed) session.AutoTimer = autoTimer;
            }
            if (!armed) { try { autoTimer.Dispose(); } catch { /* best-effort */ } }

            var snap = Snapshot();
            if (cfg.AnnounceInChat) AnnounceLine(DescribeOpen(cfg, snap));
            RaiseOpened(snap);
            RaisePollChanged();
            PublishOverlay();
            return new PollOpenResult { Outcome = PollOpenOutcome.Opened, Snapshot = snap, Mirror = mirror };
        }

        /// <summary>
        /// Whether the points economy could take a stake if one were placed right now — the
        /// open-time half of the betting guard.
        ///
        /// <para>Two seams, two different questions. <see cref="ChargePoints"/> being null is
        /// "there is no money layer at all", which only happens in a test or a partially
        /// booted Hub. <see cref="EconomyReady"/> is the live probe, and it is the one that
        /// answers the case the strings describe: the Loyalty tool switched off, or its
        /// currency table renamed away.</para>
        ///
        /// <para>An UNWIRED probe reads as collectable rather than as "the economy is off":
        /// refusing every betting poll because nobody wired a probe would be a worse lie
        /// than the one this replaces, and the per-bet path still reports EconomyOff
        /// honestly. A probe that THREW is logged and gets the same benefit of the doubt —
        /// a failed question is not a negative answer.</para>
        /// </summary>
        private async Task<bool> EconomyCanCollectAsync()
        {
            if (ChargePoints is null) return false;
            var probe = EconomyReady;
            if (probe is null) return true;
            try { return await probe().ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("PollsService", "economy readiness probe failed", ex);
                return true;
            }
        }

        // Trim, drop blanks, and drop case-insensitive duplicates — two options spelled the
        // same are one option, and letting both through would split the vote against itself
        // and make a tie out of a clear win.
        //
        // An over-long list returns NOTHING rather than the first <max> entries: silently
        // dropping the options a streamer typed would open a poll missing the answer half of
        // chat wanted, and there is no honest way to choose which ones to lose. The caller
        // reads <paramref name="tooMany"/> BEFORE the count.
        private static string[] CleanOptions(IReadOnlyList<string>? raw, int max, out bool tooMany)
        {
            tooMany = false;
            if (raw is null) return Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>(raw.Count);
            foreach (var r in raw)
            {
                string v = (r ?? string.Empty).Trim();
                if (v.Length == 0 || !seen.Add(v)) continue;
                if (list.Count >= max) { tooMany = true; return Array.Empty<string>(); }
                list.Add(v);
            }
            return list.ToArray();
        }

        /// <summary>Splits the comma-separated Options pin / panel box into labels. Exposed
        /// so the node handler, the chat layer and the panel all split identically.</summary>
        public static IReadOnlyList<string> SplitOptions(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
            var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var list = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                string v = p.Trim();
                if (v.Length > 0) list.Add(v);
            }
            return list;
        }

        // The auto-close closes THE POLL THIS TIMER WAS ARMED FOR, never "whatever poll is
        // current". System.Threading.Timer.Dispose cannot recall a callback the thread pool
        // has already picked up, so a manual close that disposes the timer a tick too late
        // leaves a live callback in flight — and if the streamer opened the next poll in
        // that window, an identity-free callback would close a poll that has run none of its
        // duration. The session travels as the timer's state and is verified under the
        // close's own claim lock.
        private void AutoCloseCallback(object? state)
        {
            if (state is not PollSession fired) return;
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => EndAsync("timer", cancel: false, only: fired), "PollsService", "poll auto-close");
        }

        // ── Vote ────────────────────────────────────────────────────────────
        /// <summary>
        /// Records one viewer's vote for a ZERO-based option index. First vote stands unless
        /// <see cref="PollsConfig.AllowVoteChange"/> is on, in which case a later vote is
        /// MOVED (the old option loses its count) rather than added.
        /// </summary>
        public PollVoteResult Vote(string login, int index)
        {
            if (!Active) return PollVoteResult.Fail(PollVoteOutcome.Disabled);
            string u = NormalizeLogin(login);
            if (u.Length == 0) return PollVoteResult.Fail(PollVoteOutcome.NoUser);

            PollVoteResult result;
            lock (_pollGate)
            {
                ExpireLingerLocked();
                var s = _session;
                if (s is null || s.Closed) return PollVoteResult.Fail(PollVoteOutcome.NoPoll);
                if (index < 0 || index >= s.Labels.Length) return PollVoteResult.Fail(PollVoteOutcome.BadOption);

                if (s.Voters.TryGetValue(u, out int previous))
                {
                    if (!_config.AllowVoteChange) return PollVoteResult.Fail(PollVoteOutcome.AlreadyVoted);
                    if (previous == index)
                        return new PollVoteResult(PollVoteOutcome.Changed, index, s.Labels[index], s.VoteCounts[index]);
                    if (previous >= 0 && previous < s.VoteCounts.Length && s.VoteCounts[previous] > 0)
                        s.VoteCounts[previous]--;
                    s.Voters[u] = index;
                    s.VoteCounts[index]++;
                    result = new PollVoteResult(PollVoteOutcome.Changed, index, s.Labels[index], s.VoteCounts[index]);
                }
                else
                {
                    s.Voters[u] = index;
                    s.VoteCounts[index]++;
                    result = new PollVoteResult(PollVoteOutcome.Counted, index, s.Labels[index], s.VoteCounts[index]);
                }
            }

            RaisePollChanged();
            PublishOverlay();
            return result;
        }

        // ── Bet ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Stakes points on a ZERO-based option index, charging the viewer atomically. On
        /// any refusal NO stake is recorded and nothing is charged. One stake per viewer per
        /// poll: a stake is a position, and letting a viewer add to it later would make the
        /// parimutuel share depend on when they typed rather than what they risked.
        /// </summary>
        public async Task<PollBetResult> BetAsync(string login, int index, long amount)
        {
            if (!Active) return PollBetResult.Fail(PollBetOutcome.Disabled);
            var cfg = _config;
            string u = NormalizeLogin(login);
            if (u.Length == 0) return PollBetResult.Fail(PollBetOutcome.NoUser);
            if (amount <= 0) return PollBetResult.Fail(PollBetOutcome.BadAmount);
            if (amount < cfg.MinBet) return PollBetResult.Fail(PollBetOutcome.BelowMin);
            if (cfg.MaxBet > 0 && amount > cfg.MaxBet) return PollBetResult.Fail(PollBetOutcome.AboveMax);

            PollSession session;
            string label;
            lock (_pollGate)
            {
                ExpireLingerLocked();
                var s = _session;
                if (s is null || s.Closed) return PollBetResult.Fail(PollBetOutcome.NoPoll);
                if (!s.BettingEnabled) return PollBetResult.Fail(PollBetOutcome.BettingOff);
                if (index < 0 || index >= s.Labels.Length) return PollBetResult.Fail(PollBetOutcome.BadOption);
                // Reserve up front so a second concurrent bet from the same viewer can't
                // double-charge; rolled back below on every failure path.
                if (!s.BetReserved.Add(u)) return PollBetResult.Fail(PollBetOutcome.AlreadyBet);
                session = s;
                label = s.Labels[index];
            }

            var charge = ChargePoints;
            if (charge is null)
            {
                lock (_pollGate) session.BetReserved.Remove(u);
                return PollBetResult.Fail(PollBetOutcome.EconomyOff);
            }

            PollChargeResult res;
            try
            {
                res = await charge(u, amount).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_pollGate) session.BetReserved.Remove(u);
                GlobalLogger.Error("PollsService", $"stake charge threw for {u}", ex);
                return PollBetResult.Fail(PollBetOutcome.Error);
            }

            if (!res.Ok)
            {
                lock (_pollGate) session.BetReserved.Remove(u);   // record NO stake on a refusal
                return PollBetResult.Fail(res.Outcome switch
                {
                    PollChargeOutcome.NoFunds => PollBetOutcome.NoFunds,
                    PollChargeOutcome.EconomyOff => PollBetOutcome.EconomyOff,
                    _ => PollBetOutcome.Error,
                });
            }

            // Charged OK — but if the poll closed meanwhile, refund and refuse. The confirm
            // and the close's Closed claim share _pollGate, so a stake is either confirmed
            // before the close snapshots the pot or refunded here, never half of each.
            bool stillOpen;
            long pot = 0;
            lock (_pollGate)
            {
                stillOpen = ReferenceEquals(_session, session) && !session.Closed;
                if (stillOpen)
                {
                    session.Stakes[u] = (index, res.Amount);
                    foreach (var kv in session.Stakes) pot += kv.Value.Amount;
                }
                else session.BetReserved.Remove(u);
            }

            if (!stillOpen)
            {
                await RefundOneAsync(u, res.Amount, "poll closed before the bet landed").ConfigureAwait(false);
                return PollBetResult.Fail(PollBetOutcome.NoPoll);
            }

            RaisePollChanged();
            PublishOverlay();
            return new PollBetResult(PollBetOutcome.Placed, index, label, res.Amount, pot);
        }

        // A single-viewer refund whose RESULT is checked. A refused refund is the failure
        // mode that loses a viewer's points in silence, so it is reported at CriticalError
        // with the viewer and the amount named — the operator can then give it back by hand,
        // which is impossible if nothing ever said it happened.
        private async Task RefundOneAsync(string login, long amount, string why)
        {
            if (amount <= 0) return;
            var refund = RefundPoints;
            bool ok = false;
            try
            {
                if (refund is not null) ok = await refund(login, amount).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("PollsService", $"refund threw for {login}", ex);
                ok = false;
            }
            if (!ok)
                GlobalLogger.Log(
                    $"Polls: {amount.ToString(CultureInfo.InvariantCulture)} point(s) could NOT be returned to {login} ({why}). " +
                    "The points economy refused the refund — give them back by hand once it is available again.",
                    "Polls", LogLevel.CriticalError);
        }

        // ── Close / cancel ──────────────────────────────────────────────────
        /// <summary>
        /// Closes the poll, resolves the winner from the votes and settles the pot.
        /// Idempotent — the auto-timer and a manual close share the claim, so whichever gets
        /// there first wins and the other is a no-op.
        /// </summary>
        public Task<PollCloseResult> CloseAsync(string byWhom) => EndAsync(byWhom, cancel: false);

        /// <summary>
        /// Ends the poll with NO winner and hands every stake back. This is what a
        /// mid-stream abort and the shutdown path use: a poll cut short has no honest
        /// outcome, and keeping the pot would be taking points for a question that never got
        /// an answer.
        /// </summary>
        public Task<PollCloseResult> CancelAsync(string byWhom) => EndAsync(byWhom, cancel: true);

        // `only`, when non-null, scopes the close to that exact session — see
        // AutoCloseCallback. It is tested under the same lock that makes the claim, so there
        // is no window between "it is still my poll" and "it is claimed".
        private async Task<PollCloseResult> EndAsync(string byWhom, bool cancel, PollSession? only = null)
        {
            PollSession session;
            PollSnapshot snap;
            lock (_pollGate)
            {
                ExpireLingerLocked();
                if (_session is null || _session.Closed)
                    return PollCloseResult.Fail(Active ? PollCloseOutcome.NoPoll : PollCloseOutcome.Disabled);
                if (only is not null && !ReferenceEquals(_session, only))
                    return PollCloseResult.Fail(PollCloseOutcome.NoPoll);

                session = _session;
                session.Closed = true;                       // claim it — auto + manual are now idempotent
                session.ClosedAtMs = Clock.ElapsedMilliseconds;
                session.WinnerIndex = cancel ? -1 : ResolveWinnerLocked(session);
                try { session.AutoTimer?.Dispose(); } catch { /* best-effort */ }
                session.AutoTimer = null;

                // ★ THE SNAPSHOT IS TAKEN UNDER THE CLAIM, and it has to be. Snapshot()
                // runs ExpireLingerLocked first, whose test is "elapsed >= linger" — with
                // ResultLingerSeconds = 0 that is true the instant ClosedAtMs is stamped, so
                // a snapshot taken AFTER this block would find _session already dropped and
                // return a default PollSnapshot. Chat then announced 'Poll closed: "" —
                // nobody voted.' while the pot was being paid out, and Poll.OnClosed /
                // Poll.OnSettled both fired with a blank title, no winner and zero counts.
                // 0 is a documented, panel-reachable value (an emptied linger box yields it),
                // so this was reachable in a shipped app, not a theoretical edge.
                //
                // Nothing between here and the settlement can change what this describes:
                // BetAsync confirms a stake only while `!session.Closed` under this same
                // lock, so the pot is already final, and the settlement moves points rather
                // than votes.
                snap = SnapshotLocked();
            }

            var settlement = await SettleAsync(session, cancel).ConfigureAwait(false);

            // Close the native mirror only when the OPEN half actually engaged. Closing a
            // mirror that was never opened would end whatever unrelated poll the streamer
            // happens to be running on Twitch by hand.
            if (session.MirrorEngaged)
            {
                var action = session.MirrorMode == PollMirrorMode.Prediction
                    ? PollMirrorAction.ResolvePrediction
                    : PollMirrorAction.ClosePoll;
                var outcome = InvokeMirror(new PollMirrorRequest(
                    action, session.Title, session.Labels, session.DurationSeconds,
                    session.WinnerIndex));
                if (outcome != PollMirrorOutcome.Mirrored)
                    GlobalLogger.Log(
                        $"Polls: the native {(session.MirrorMode == PollMirrorMode.Prediction ? "prediction" : "poll")} " +
                        $"mirror could not be closed ({outcome}). Close it on Twitch by hand — an unresolved prediction " +
                        "keeps the channel's prediction panel occupied.",
                        "Polls", LogLevel.CriticalError);
            }

            if (_config.AnnounceInChat)
                AnnounceLine(cancel
                    ? DescribeCancel(snap, settlement, CurrencyName())
                    : DescribeResult(snap, settlement, CurrencyName()));

            RaiseClosed(snap);
            if (settlement.Outcome != PollSettlementOutcome.NoStakes)
                RaiseSettled(snap, settlement, CurrencyName());
            RaisePollChanged();
            PublishOverlay();

            return new PollCloseResult
            {
                Outcome = PollCloseOutcome.Closed,
                Snapshot = snap,
                Settlement = settlement,
                Cancelled = cancel,
            };
        }

        // ── Settlement ──────────────────────────────────────────────────────
        // Parimutuel: the winners split the WHOLE pot in proportion to their own stakes, so
        // each gets their stake back plus a slice of the losing side. A tie, a poll nobody
        // voted in, a cancel, or a winning option nobody backed all mean "there is no
        // defensible winner" and every stake goes back untouched.
        private async Task<PollSettlement> SettleAsync(PollSession session, bool cancel)
        {
            List<(string User, int Index, long Amount)> stakes;
            int winner;
            lock (_pollGate)
            {
                if (session.Settled) return new PollSettlement();
                session.Settled = true;
                winner = session.WinnerIndex;
                stakes = session.Stakes.Select(kv => (kv.Key, kv.Value.Index, kv.Value.Amount)).ToList();
            }
            if (stakes.Count == 0) return new PollSettlement();

            long pot = 0;
            foreach (var s in stakes) pot += s.Amount;

            long winningStake = 0;
            if (!cancel && winner >= 0)
                foreach (var s in stakes)
                    if (s.Index == winner) winningStake += s.Amount;

            List<(string Name, long Amount)> credits;
            PollSettlementOutcome outcome;
            if (cancel || winner < 0 || winningStake <= 0)
            {
                outcome = PollSettlementOutcome.Refunded;
                credits = stakes.Select(s => (s.User, s.Amount)).ToList();
            }
            else
            {
                outcome = PollSettlementOutcome.Paid;
                credits = SplitPot(stakes, winner, winningStake, pot);
            }

            string reason = outcome == PollSettlementOutcome.Paid ? "poll payout" : "poll refund";
            int applied = 0;
            var payout = PayoutPoints;
            try
            {
                if (payout is not null) applied = await payout(credits, reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("PollsService", $"{reason} failed", ex);
                applied = 0;
            }

            // ★ The seam returns a COUNT and it is compared, not discarded. The batch credit
            // reports 0 when the currency table is missing (the whole transaction rolls
            // back) and skips anything it cannot apply, so "fewer applied than asked for" is
            // the shape a lost payout actually takes — there is no exception to catch.
            if (applied < credits.Count)
                GlobalLogger.Log(
                    $"Polls: {reason} paid {applied.ToString(CultureInfo.InvariantCulture)} of " +
                    $"{credits.Count.ToString(CultureInfo.InvariantCulture)} viewer(s) " +
                    $"({pot.ToString(CultureInfo.InvariantCulture)} point pot). The points economy refused the rest — " +
                    "settle them by hand.",
                    "Polls", LogLevel.CriticalError);

            return new PollSettlement
            {
                Outcome = outcome,
                Pot = pot,
                Payouts = credits.Select(c => new PollPayout(c.Name, c.Amount)).ToList(),
                Applied = applied,
            };
        }

        // Largest-remainder split so the paid total equals the pot EXACTLY. Plain integer
        // division would leave up to (winners - 1) points unpaid on every single settlement,
        // which over a stream is points quietly deleted from the economy.
        //
        // ★ THE PRODUCT IS WIDENED, and it is not academic. MaxBet = 0 means "no cap", so
        // `stake * pot` overflows a long once two viewers hold a few billion points each —
        // an inflating currency reaches that on its own. A wrapped product used to produce a
        // NEGATIVE share, which the trailing filter dropped (that winner's stake vanished),
        // drove `assigned` negative, made `remainder` enormous, and then threw out of the
        // largest-remainder loop — AFTER session.Settled was already claimed, so the pot was
        // neither paid nor refunded and every stake was simply gone. Int128 holds the full
        // long × long range exactly, and `share` is provably a long again (stake <=
        // winningStake ⇒ share <= pot).
        private static List<(string Name, long Amount)> SplitPot(
            List<(string User, int Index, long Amount)> stakes, int winner, long winningStake, long pot)
        {
            var winners = stakes.Where(s => s.Index == winner).ToList();
            var shares = new List<(string Name, long Amount, long Remainder)>(winners.Count);
            long assigned = 0;
            foreach (var w in winners)
            {
                Int128 numerator = (Int128)w.Amount * pot;
                long share = (long)(numerator / winningStake);
                shares.Add((w.User, share, (long)(numerator % winningStake)));
                assigned += share;
            }

            // At most one extra point each: the largest-remainder method never owes more
            // than (winners - 1). Bounding by the winner count rather than trusting the
            // arithmetic keeps this loop finite even if the figures above ever go wrong
            // again — the previous `i % order.Count` wrap threw on a negative modulus
            // instead, which is the failure that lost the pot.
            long remainder = pot - assigned;
            if (remainder > 0 && shares.Count > 0)
            {
                // Biggest fractional part first; a tie there falls back to the bigger stake,
                // so the order is fully determined and two identical polls settle identically.
                var order = shares
                    .Select((s, i) => (Index: i, s.Remainder, s.Amount))
                    .OrderByDescending(x => x.Remainder)
                    .ThenByDescending(x => x.Amount)
                    .ToList();
                int extra = (int)Math.Min(remainder, order.Count);
                for (int i = 0; i < extra; i++)
                {
                    int slot = order[i].Index;
                    shares[slot] = (shares[slot].Name, shares[slot].Amount + 1, shares[slot].Remainder);
                }
            }

            return shares.Where(s => s.Amount > 0).Select(s => (s.Name, s.Amount)).ToList();
        }

        // The economy's plural currency noun, for the chat lines and the settled event.
        // A SEAM rather than a direct LoyaltyService.Instance read, for the same reason the
        // money calls are: touching that singleton here would drag the whole points economy
        // (and the process-wide databank behind it) into every unit test of this file.
        internal string CurrencyName()
        {
            try { return CurrencyProvider?.Invoke() is { Length: > 0 } n ? n : "points"; }
            catch (Exception ex)
            {
                GlobalLogger.Error("PollsService", "CurrencyProvider failed", ex);
                return "points";
            }
        }

        // ── Native mirror ───────────────────────────────────────────────────
        // One missing-capability line per mirror action per process. The gate itself lives
        // in ScriptManager.Polls.cs (it is the half that knows about Streamer.bot); this is
        // only the "say it once, then stop" wrapper, so a streamer running polls all stream
        // with an un-mirrorable pack gets one explanation rather than one per poll.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _mirrorWarned =
            new(StringComparer.Ordinal);

        private PollMirrorOutcome InvokeMirror(PollMirrorRequest request)
        {
            var seam = NativeMirror;
            PollMirrorOutcome outcome;
            if (seam is null) outcome = PollMirrorOutcome.Unavailable;
            else
            {
                try { outcome = seam(request); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("PollsService", $"native mirror ({request.Action}) failed", ex);
                    outcome = PollMirrorOutcome.Unavailable;
                }
            }

            if (outcome is PollMirrorOutcome.Unavailable or PollMirrorOutcome.NotConnected or PollMirrorOutcome.Unsupported
                && _mirrorWarned.TryAdd(request.Action.ToString() + ":" + outcome, 0))
            {
                GlobalLogger.Log(
                    $"Polls: the native Twitch mirror is unavailable ({request.Action} → {outcome}). " +
                    "The poll runs in chat only. Further notices for this case are suppressed.",
                    "Polls", LogLevel.Communication);
            }
            return outcome;
        }
    }
}
