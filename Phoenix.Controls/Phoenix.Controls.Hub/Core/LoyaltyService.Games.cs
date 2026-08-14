using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: the five minigames. Each one gates (master / enabled /
    // online-only / cooldown) BEFORE any DB call, rolls the RNG seam, then hands the
    // outcome to the atomic settle method (LoyaltyPlayHouseAsync / LoyaltyTransferAsync
    // / LoyaltyDebit+CreditMany). The DB layer guarantees no negative bet / no
    // overspend, so nothing here can be exploited by a crafted stake.
    //
    // WHO-CAN-PLAY: the role gate (LoyaltyGameBase.WhoCanPlay above Everyone) is
    // enforced by the command layer, which holds the chatter's badge — the fixed
    // service signatures carry only a username. Everyone (the default) always passes
    // here; the service still enforces every gate it CAN see.
    public sealed partial class LoyaltyService
    {
        // Common gate for the house games. Returns a non-null failing result (with the
        // reason in its Outcome) when blocked; null = clear to play. Emits the
        // normalized username for the settle call.
        private LoyaltyBetResult? GateBet(LoyaltyGameBase game, string scope, string user, out string normUser)
        {
            normUser = Normalize(user);
            if (!Active) return new LoyaltyBetResult(LoyaltyOutcome.Disabled, 0, 0, 0);
            if (!game.Enabled) return new LoyaltyBetResult(LoyaltyOutcome.Disabled, 0, 0, 0);
            if (game.OnlineOnly && !_streamLive) return new LoyaltyBetResult(LoyaltyOutcome.Offline, 0, 0, 0);
            if (normUser.Length == 0) return new LoyaltyBetResult(LoyaltyOutcome.Invalid, 0, 0, 0);
            if (IsOnCooldown(scope, normUser, game.UserCooldownSeconds, game.GlobalCooldownSeconds))
                return new LoyaltyBetResult(LoyaltyOutcome.OnCooldown, 0, 0, 0);
            return null;
        }

        // ── Gamble ──────────────────────────────────────────────────────────
        /// <summary>Coin-flip against the house. Wins pay the configured multiplier.</summary>
        public async Task<LoyaltyBetResult> GambleAsync(string user, LoyaltyStake stake)
        {
            var cfg = _config;
            var g = cfg.Games.Gamble;
            var gate = GateBet(g, "gamble", user, out string u);
            if (gate.HasValue) return gate.Value;

            bool won = _rng.NextDouble() < g.WinChance;
            var res = await Db.LoyaltyPlayHouseAsync(cfg.Currency.BalanceTable, u, stake,
                g.MinBet, g.MaxBet, won, g.PayoutMultiplier, LedgerTableIfEnabled(cfg), "gamble").ConfigureAwait(false);
            if (res.Ok)
            {
                StampCooldown("gamble", u, g.UserCooldownSeconds, g.GlobalCooldownSeconds);
                await AfterBalanceChangeAsync($"{u} gambled {res.Stake} — {(res.Won ? "won" : "lost")}").ConfigureAwait(false);
            }
            return res;
        }

        // ── Slots ───────────────────────────────────────────────────────────
        /// <summary>Three-reel slots. Triple pays the per-symbol multiplier, any two
        /// matching pays the any-two multiplier. Returns the spun reel string too.</summary>
        public async Task<(LoyaltyBetResult Result, string Reels)> SlotsAsync(string user, LoyaltyStake stake)
        {
            var cfg = _config;
            var s = cfg.Games.Slots;
            var gate = GateBet(s, "slots", user, out string u);
            if (gate.HasValue) return (gate.Value, string.Empty);

            int symbolCount = s.Symbols.Count > 0 ? s.Symbols.Count : 1;
            int r0 = _rng.Next(symbolCount), r1 = _rng.Next(symbolCount), r2 = _rng.Next(symbolCount);
            string reels = $"{Sym(s, r0)} {Sym(s, r1)} {Sym(s, r2)}";

            double gross;
            if (r0 == r1 && r1 == r2) gross = TripleMult(s, r0);
            else if (r0 == r1 || r1 == r2 || r0 == r2) gross = s.AnyTwoMultiplier;
            else gross = 0;
            bool won = gross > 0;

            var res = await Db.LoyaltyPlayHouseAsync(cfg.Currency.BalanceTable, u, stake,
                s.MinBet, s.MaxBet, won, gross, LedgerTableIfEnabled(cfg), "slots").ConfigureAwait(false);
            if (res.Ok)
            {
                StampCooldown("slots", u, s.UserCooldownSeconds, s.GlobalCooldownSeconds);
                await AfterBalanceChangeAsync($"{u} spun {reels} — {(res.Won ? "won" : "lost")}").ConfigureAwait(false);
            }
            return (res, reels);
        }

        private static string Sym(LoyaltySlotsConfig s, int idx)
            => idx >= 0 && idx < s.Symbols.Count ? s.Symbols[idx] : "?";

        private static double TripleMult(LoyaltySlotsConfig s, int idx)
            => idx >= 0 && idx < s.TripleMultipliers.Count ? s.TripleMultipliers[idx] : 0;

        // ── Roulette ────────────────────────────────────────────────────────
        // European single-zero wheel red pockets.
        private static readonly HashSet<int> RouletteRed = new()
        { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 };

        /// <summary>European (single-zero) roulette. betSpec is red/black/even/odd/
        /// low/high or a number 0-36. Returns the settle result, the spun number, and
        /// the normalized bet label.</summary>
        public async Task<(LoyaltyBetResult Result, int Number, string BetType)> RouletteAsync(
            string user, LoyaltyStake stake, string betSpec)
        {
            var cfg = _config;
            var r = cfg.Games.Roulette;
            var gate = GateBet(r, "roulette", user, out string u);
            if (gate.HasValue) return (gate.Value, -1, string.Empty);

            int spin = _rng.Next(37); // 0..36
            var (valid, won, grossMult, label) = EvaluateRoulette(r, betSpec, spin);
            if (!valid) return (new LoyaltyBetResult(LoyaltyOutcome.Invalid, 0, 0, 0), spin, string.Empty);

            var res = await Db.LoyaltyPlayHouseAsync(cfg.Currency.BalanceTable, u, stake,
                r.MinBet, r.MaxBet, won, grossMult, LedgerTableIfEnabled(cfg), "roulette").ConfigureAwait(false);
            if (res.Ok)
            {
                StampCooldown("roulette", u, r.UserCooldownSeconds, r.GlobalCooldownSeconds);
                await AfterBalanceChangeAsync($"{u} bet {label} — landed {spin}, {(res.Won ? "won" : "lost")}").ConfigureAwait(false);
            }
            return (res, spin, label);
        }

        // grossMult is the TOTAL return multiple the DB layer applies (net = stake ×
        // grossMult − stake), so a straight number pays StraightPayout+1 and an
        // even-money bet pays ColorPayout+1.
        private static (bool Valid, bool Won, double GrossMult, string Label) EvaluateRoulette(
            LoyaltyRouletteConfig cfg, string spec, int spin)
        {
            string s = (spec ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0) return (false, false, 0, string.Empty);

            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num) && num >= 0 && num <= 36)
                return (true, spin == num, cfg.StraightPayout + 1.0, num.ToString(CultureInfo.InvariantCulture));

            bool won;
            string label;
            switch (s)
            {
                case "red": won = RouletteRed.Contains(spin); label = "red"; break;
                case "black": won = spin != 0 && !RouletteRed.Contains(spin); label = "black"; break;
                case "even": won = spin != 0 && spin % 2 == 0; label = "even"; break;
                case "odd": won = spin % 2 == 1; label = "odd"; break;
                case "low": case "1-18": won = spin >= 1 && spin <= 18; label = "low"; break;
                case "high": case "19-36": won = spin >= 19 && spin <= 36; label = "high"; break;
                default: return (false, false, 0, string.Empty);
            }
            return (true, won, cfg.ColorPayout + 1.0, label);
        }

        // ── Duel (challenge / accept state machine) ─────────────────────────
        private sealed class PendingDuel
        {
            public string Challenger = string.Empty;
            public string Target = string.Empty;
            public long Wager;
            public long ExpiresAtMs;
        }

        private readonly object _duelGate = new();
        private readonly Dictionary<string, PendingDuel> _duelsByChallenger = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingDuel> _duelsByTarget = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Opens a pending duel challenge. No funds move until the target
        /// accepts (the loser's stake is checked atomically then). One pending
        /// challenge per challenger and per target.</summary>
        public Task<DuelStart> ChallengeAsync(string challenger, string target, long wager)
        {
            var cfg = _config;
            var d = cfg.Games.Duel;
            string c = Normalize(challenger), t = Normalize(target);

            if (!Active) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.Inactive));
            if (!d.Enabled) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.Disabled));
            if (c.Length == 0 || t.Length == 0) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.InvalidTarget));
            if (c == t) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.SelfChallenge));
            if (d.OnlineOnly && !_streamLive) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.Offline));
            if (wager <= 0 || wager < d.MinBet) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.BelowMin));
            if (d.MaxBet > 0 && wager > d.MaxBet) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.AboveMax));
            if (IsOnCooldown("duel", c, d.UserCooldownSeconds, d.GlobalCooldownSeconds))
                return Task.FromResult(DuelStart.Fail(DuelStartOutcome.OnCooldown));

            int timeout = Math.Max(1, d.AcceptTimeoutSeconds);
            long now = NowMs;
            lock (_duelGate)
            {
                PurgeExpiredDuelsLocked(now);
                if (_duelsByChallenger.ContainsKey(c)) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.ChallengerBusy));
                if (_duelsByTarget.ContainsKey(t)) return Task.FromResult(DuelStart.Fail(DuelStartOutcome.TargetBusy));
                var pd = new PendingDuel { Challenger = c, Target = t, Wager = wager, ExpiresAtMs = now + timeout * 1000L };
                _duelsByChallenger[c] = pd;
                _duelsByTarget[t] = pd;
            }
            return Task.FromResult(new DuelStart(DuelStartOutcome.Ok, c, t, wager, timeout));
        }

        /// <summary>Accepts a pending duel: rolls the winner, then settles by an
        /// atomic transfer loser→winner (which re-checks the loser's funds). On
        /// NoFunds nobody is charged and the broke side is named.</summary>
        public async Task<DuelResult> AcceptAsync(string target)
        {
            var cfg = _config;
            var d = cfg.Games.Duel;
            string t = Normalize(target);

            if (!Active) return new DuelResult(DuelResultOutcome.NoPending, string.Empty, string.Empty, 0, 0, string.Empty);

            PendingDuel? pd;
            long now = NowMs;
            lock (_duelGate)
            {
                PurgeExpiredDuelsLocked(now);
                if (!_duelsByTarget.TryGetValue(t, out pd) || pd is null)
                    return new DuelResult(DuelResultOutcome.NoPending, string.Empty, string.Empty, 0, 0, string.Empty);
                _duelsByTarget.Remove(t);
                _duelsByChallenger.Remove(pd.Challenger);
            }

            bool challengerWins = _rng.NextDouble() < d.WinChance;
            string winner = challengerWins ? pd.Challenger : pd.Target;
            string loser = challengerWins ? pd.Target : pd.Challenger;

            var res = await Db.LoyaltyTransferAsync(cfg.Currency.BalanceTable, loser, winner, pd.Wager,
                LedgerTableIfEnabled(cfg), "duel").ConfigureAwait(false);
            if (!res.Ok)
            {
                string broke = res.Outcome == LoyaltyOutcome.NoFunds ? loser : string.Empty;
                return new DuelResult(DuelResultOutcome.NoFunds, winner, loser, pd.Wager, 0, broke);
            }

            // The challenger "spent" their turn — start their cooldown on a settled duel.
            StampCooldown("duel", pd.Challenger, d.UserCooldownSeconds, d.GlobalCooldownSeconds);
            long winnerBalance = await GetBalanceAsync(winner).ConfigureAwait(false);
            await AfterBalanceChangeAsync($"{winner} beat {loser} for {pd.Wager} {cfg.Currency.NamePlural}").ConfigureAwait(false);
            return new DuelResult(DuelResultOutcome.Ok, winner, loser, pd.Wager, winnerBalance, string.Empty);
        }

        /// <summary>Declines (cancels) the pending duel aimed at this target.
        /// Returns false when there was none.</summary>
        public Task<bool> DeclineAsync(string target)
        {
            string t = Normalize(target);
            long now = NowMs;
            lock (_duelGate)
            {
                PurgeExpiredDuelsLocked(now);
                if (_duelsByTarget.TryGetValue(t, out var pd) && pd is not null)
                {
                    _duelsByTarget.Remove(t);
                    _duelsByChallenger.Remove(pd.Challenger);
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }

        private void PurgeExpiredDuelsLocked(long now)
        {
            if (_duelsByChallenger.Count == 0) return;
            var expired = _duelsByChallenger.Values.Where(p => now >= p.ExpiresAtMs).ToList();
            foreach (var p in expired)
            {
                _duelsByChallenger.Remove(p.Challenger);
                _duelsByTarget.Remove(p.Target);
            }
        }

        // ── Raffle (one live session per channel) ───────────────────────────
        private sealed class RaffleSession
        {
            public int Winners = 1;
            public long Fee;
            public string StartedBy = string.Empty;
            /// <summary>RESERVATIONS — claimed the instant a join starts, so two
            /// concurrent joins by the same user can't both charge the fee. A name in
            /// here has NOT necessarily paid (the debit runs outside the lock).</summary>
            public readonly HashSet<string> Entrants = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Entries whose fee actually LANDED (free raffles confirm on
            /// reservation). The draw sizes the pot and picks winners from this set
            /// ONLY — a debit still in flight must never win a share of a pot it
            /// hasn't paid into.</summary>
            public readonly HashSet<string> ConfirmedEntrants = new(StringComparer.OrdinalIgnoreCase);
            public System.Threading.Timer? AutoTimer;
            public bool Drawn;
        }

        private readonly object _raffleGate = new();
        private RaffleSession? _raffle;

        /// <summary>
        /// Whether a raffle is open right now — a side-effect-free probe, unlike
        /// <see cref="RaffleJoinAsync"/> which answers the same question only by attempting a join.
        ///
        /// <para>★ This exists for the chat dispatcher, not for the raffle. The <c>!join</c> arm has
        /// to decide whether to DECLINE the message so a later provider (the viewer queue) can
        /// answer it, and it must reach that decision WITHOUT consuming the line on a role check —
        /// refusing a non-subscriber entry to a raffle that does not exist would consume <c>!join</c>
        /// and silently starve every provider behind Loyalty. Reading liveness first keeps the
        /// role gate meaning what it says: it guards a raffle that is actually running.</para>
        /// </summary>
        public bool RaffleLive
        {
            get { lock (_raffleGate) return _raffle is not null && !_raffle.Drawn; }
        }

        /// <summary>Opens a raffle. Fails when the tool/game is off or a raffle is
        /// already live. An auto-draw timer fires <see cref="RaffleDrawAsync"/> at the
        /// duration (never a blocking wait). The role gate for who-can-start is a
        /// command-layer concern.</summary>
        public Task<bool> RaffleStartAsync(int winners, int durationSec, long fee, string byWhom)
        {
            if (!Active) return Task.FromResult(false);
            var cfg = _config;
            var r = cfg.Games.Raffle;
            if (!r.Enabled) return Task.FromResult(false);

            if (winners <= 0) winners = Math.Max(1, r.DefaultWinners);
            if (durationSec <= 0) durationSec = Math.Max(1, r.DefaultDurationSeconds);
            if (fee < 0) fee = 0;

            RaffleSession session;
            lock (_raffleGate)
            {
                if (_raffle is not null && !_raffle.Drawn) return Task.FromResult(false);
                session = new RaffleSession { Winners = winners, Fee = fee, StartedBy = Normalize(byWhom) };
                _raffle = session;
            }
            // Schedule the auto-draw outside the lock; RaffleDrawAsync is idempotent
            // (a manual draw before the timer fires just no-ops the timer callback).
            session.AutoTimer = new System.Threading.Timer(RaffleAutoDrawCallback, session, durationSec * 1000, Timeout.Infinite);

            RaiseActivity($"raffle opened by {session.StartedBy} — {winners} winner(s), {durationSec}s, fee {fee}");
            return Task.FromResult(true);
        }

        private void RaffleAutoDrawCallback(object? state)
            => _ = AsyncErrorBoundary.SafeRunAsync(() => RaffleDrawAsync(), "LoyaltyService", "raffle auto-draw");

        /// <summary>Enters the caller into the live raffle, charging the entry fee
        /// atomically. On NoFunds the join is rejected and NO entry is added. The
        /// entry only counts for the draw once the fee has landed (see
        /// <c>RaffleSession.ConfirmedEntrants</c>). Entrants are deduped by
        /// lowercased username.</summary>
        public async Task<RaffleJoin> RaffleJoinAsync(string user)
        {
            if (!Active) return new RaffleJoin(RaffleJoinOutcome.Inactive, 0, 0, 0);
            var cfg = _config;
            string u = Normalize(user);
            if (u.Length == 0) return new RaffleJoin(RaffleJoinOutcome.NoRaffle, 0, 0, 0);

            RaffleSession session;
            long fee;
            lock (_raffleGate)
            {
                if (_raffle is null || _raffle.Drawn) return new RaffleJoin(RaffleJoinOutcome.NoRaffle, 0, 0, 0);
                session = _raffle;
                if (session.Entrants.Contains(u))
                    return new RaffleJoin(RaffleJoinOutcome.AlreadyJoined, 0, 0, session.ConfirmedEntrants.Count);
                fee = session.Fee;
                // Reserve the slot up front so a second concurrent join for the same
                // user can't double-charge; rolled back below if the fee debit fails.
                session.Entrants.Add(u);
            }

            if (fee <= 0)
            {
                // Free raffle — there is nothing to settle, so the reservation IS the
                // confirmation. Re-check Drawn: the draw can have claimed the session
                // between the two lock holds.
                int c;
                lock (_raffleGate)
                {
                    if (session.Drawn || !ReferenceEquals(_raffle, session))
                    {
                        session.Entrants.Remove(u);
                        return new RaffleJoin(RaffleJoinOutcome.NoRaffle, 0, 0, 0);
                    }
                    session.ConfirmedEntrants.Add(u);
                    c = session.ConfirmedEntrants.Count;
                }
                return new RaffleJoin(RaffleJoinOutcome.Ok, 0, 0, c);
            }

            LoyaltyResult res;
            try
            {
                res = await Db.LoyaltyDebitAsync(cfg.Currency.BalanceTable, u, fee,
                    LedgerTableIfEnabled(cfg), "raffle", "raffle entry").ConfigureAwait(false);
            }
            catch
            {
                lock (_raffleGate) session.Entrants.Remove(u);
                throw;
            }

            if (!res.Ok)
            {
                lock (_raffleGate) session.Entrants.Remove(u);   // add NO entry on NoFunds
                return new RaffleJoin(RaffleJoinOutcome.NoFunds, fee, res.NewBalance, 0);
            }

            // Charged OK — but if the raffle was drawn/replaced meanwhile, refund and
            // reject. The confirm and the draw's Drawn flag share _raffleGate, so an
            // entry is either confirmed before the draw claims the session (paid, and
            // in the draw's snapshot) or refunded here — never half of each.
            bool stillLive;
            int count;
            lock (_raffleGate)
            {
                stillLive = ReferenceEquals(_raffle, session) && !session.Drawn;
                if (stillLive) { session.ConfirmedEntrants.Add(u); count = session.ConfirmedEntrants.Count; }
                else { session.Entrants.Remove(u); count = 0; }
            }
            if (!stillLive)
            {
                await Db.LoyaltyCreditAsync(cfg.Currency.BalanceTable, u, fee,
                    LedgerTableIfEnabled(cfg), "raffle", "raffle refund").ConfigureAwait(false);
                return new RaffleJoin(RaffleJoinOutcome.NoRaffle, fee, res.NewBalance + fee, 0);
            }
            return new RaffleJoin(RaffleJoinOutcome.Ok, fee, res.NewBalance, count);
        }

        /// <summary>Draws the live raffle: picks exactly min(winners, entrants)
        /// DISTINCT winners from the CONFIRMED entrants (a join whose fee is still in
        /// flight neither sizes the pot nor can win it), pays them per the PrizeMode,
        /// and closes the raffle. Idempotent (auto-timer + manual draw share this).
        /// Empty entrants → NoEntries, nothing paid.</summary>
        public async Task<RaffleOutcome> RaffleDrawAsync()
        {
            RaffleSession session;
            lock (_raffleGate)
            {
                if (_raffle is null || _raffle.Drawn) return new RaffleOutcome { Outcome = RaffleDrawOutcome.NoRaffle };
                session = _raffle;
                session.Drawn = true;               // claim it — makes auto + manual idempotent
                try { session.AutoTimer?.Dispose(); } catch { /* best-effort */ }
                session.AutoTimer = null;
            }

            var cfg = _config;
            var r = cfg.Games.Raffle;
            // Confirmed only — Drawn is now set, so every later join refunds itself
            // instead of joining this snapshot.
            List<string> entrants;
            lock (_raffleGate) entrants = session.ConfirmedEntrants.ToList();

            // ── Master gate ──────────────────────────────────────────────────
            // This was the ONE balance-increasing path in the whole service with no
            // Active check — GateBet, DuelStartAsync, AcceptAsync, RaffleStartAsync
            // and RaffleJoinAsync all have one. And two of the three call sites here
            // (the auto-draw timer and the shutdown resolver) fire with no user
            // action at all, so a raffle opened before the tool was switched off
            // still paid out prizes afterwards. That is the reported defect: points
            // credited with the tool fully disabled.
            //
            // Refusing outright would be worse than the bug — entrants have already
            // been DEBITED. So a disabled draw becomes a cancel: everyone who paid
            // gets their stake back and no prize is minted.
            if (!Active)
            {
                long refunded = await RefundRaffleEntrantsAsync(session, entrants, cfg, "raffle cancelled — tool disabled")
                    .ConfigureAwait(false);
                lock (_raffleGate) if (ReferenceEquals(_raffle, session)) _raffle = null;
                GlobalLogger.Log(
                    $"Raffle closed without a draw — Loyalty is disabled. Refunded {entrants.Count} entrant(s) {refunded} total.",
                    "LoyaltyService", LogLevel.System);
                // Same fan-out CancelRaffleAsync performs, in the same order (refund →
                // close → fan-out): balances changed hands, so BalancesChanged refreshes
                // the panel and the activity feed records the refund like every other
                // balance move. The live-channel publish inside is gated on
                // Config.Enabled and stays silent here by design — the retract-on-disable
                // sweep already withdrew loyalty.*, and re-publishing standings the tool
                // can no longer update is exactly what that gate exists to prevent.
                await AfterBalanceChangeAsync(
                    $"raffle cancelled — tool disabled — {entrants.Count} entrant(s) refunded {refunded} total").ConfigureAwait(false);
                return new RaffleOutcome
                {
                    Outcome = RaffleDrawOutcome.Cancelled,
                    EntrantCount = entrants.Count,
                };
            }

            if (entrants.Count == 0)
            {
                lock (_raffleGate) if (ReferenceEquals(_raffle, session)) _raffle = null;
                RaiseActivity("raffle closed — no entries");
                return new RaffleOutcome { Outcome = RaffleDrawOutcome.NoEntries };
            }

            string mode = (r.PrizeMode ?? "SplitPot").Trim();
            bool potToOne = mode.Equals("PotToOne", StringComparison.OrdinalIgnoreCase);
            int winnersWanted = potToOne ? 1 : Math.Max(1, session.Winners);
            winnersWanted = Math.Min(winnersWanted, entrants.Count);

            var winners = PickDistinct(entrants, winnersWanted);
            long[] amounts = ComputePrizes(mode, winners.Count, session.Fee, entrants.Count, r.FixedPrize, out long pot);

            var credits = new List<(string, long)>(winners.Count);
            var winnerList = new List<RaffleWinner>(winners.Count);
            for (int i = 0; i < winners.Count; i++)
            {
                long amt = amounts[i];
                if (amt > 0) credits.Add((winners[i], amt));
                winnerList.Add(new RaffleWinner(winners[i], amt));
            }
            if (credits.Count > 0)
                await Db.LoyaltyCreditManyAsync(cfg.Currency.BalanceTable, credits,
                    LedgerTableIfEnabled(cfg), "raffle", "raffle prize").ConfigureAwait(false);

            lock (_raffleGate) if (ReferenceEquals(_raffle, session)) _raffle = null;

            RaiseScript("Loyalty.OnRaffle", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.winners"] = string.Join(",", winnerList.Select(w => w.Name)),
                ["event.count"] = winnerList.Count.ToString(CultureInfo.InvariantCulture),
                ["event.pot"] = pot.ToString(CultureInfo.InvariantCulture),
                ["event.entrants"] = entrants.Count.ToString(CultureInfo.InvariantCulture),
                ["event.currency"] = cfg.Currency.NamePlural,
            });
            await AfterBalanceChangeAsync($"raffle drawn — {winnerList.Count} winner(s) of {entrants.Count} entrant(s)").ConfigureAwait(false);

            return new RaffleOutcome
            {
                Outcome = RaffleDrawOutcome.Ok,
                Winners = winnerList,
                TotalPot = pot,
                EntrantCount = entrants.Count,
            };
        }

        /// <summary>
        /// Cancels the live raffle WITHOUT drawing: every entrant whose fee landed is
        /// refunded in full and the raffle closes. Idempotent with the auto-draw timer
        /// and a manual draw — all three claim the session under <c>_raffleGate</c>,
        /// so a cancel racing a draw resolves to exactly one of them.
        /// </summary>
        /// <remarks>
        /// A cancel deliberately does NOT raise <c>Loyalty.OnRaffle</c>: that event
        /// carries winners and a pot, and firing it with neither would make any script
        /// bound to it announce a draw that never happened.
        /// </remarks>
        public async Task<RaffleOutcome> CancelRaffleAsync(string byWhom = "")
        {
            RaffleSession session;
            lock (_raffleGate)
            {
                if (_raffle is null || _raffle.Drawn)
                    return new RaffleOutcome { Outcome = RaffleDrawOutcome.NoRaffle };
                session = _raffle;
                session.Drawn = true;               // claim it, exactly as the draw does
                try { session.AutoTimer?.Dispose(); } catch { /* best-effort */ }
                session.AutoTimer = null;
            }

            var cfg = _config;
            List<string> entrants;
            lock (_raffleGate) entrants = session.ConfirmedEntrants.ToList();

            string who = string.IsNullOrWhiteSpace(byWhom) ? "" : $" by {Normalize(byWhom)}";
            long refunded = await RefundRaffleEntrantsAsync(
                session, entrants, cfg, $"raffle cancelled{who}").ConfigureAwait(false);

            lock (_raffleGate) if (ReferenceEquals(_raffle, session)) _raffle = null;

            await AfterBalanceChangeAsync(
                $"raffle cancelled{who} — {entrants.Count} entrant(s) refunded {refunded} total").ConfigureAwait(false);

            return new RaffleOutcome
            {
                Outcome = RaffleDrawOutcome.Cancelled,
                EntrantCount = entrants.Count,
                TotalPot = refunded,
            };
        }

        /// <summary>
        /// Gives every CONFIRMED entrant their fee back. Confirmed-only is the same
        /// rule the draw uses to size the pot: a reservation whose debit is still in
        /// flight has not paid, and refunding it would mint points from nothing. A
        /// free raffle (Fee == 0) has nothing to return, so this is a no-op.
        /// Returns the total credited.
        /// </summary>
        private async Task<long> RefundRaffleEntrantsAsync(
            RaffleSession session, List<string> entrants, LoyaltyConfig cfg, string reason)
        {
            if (session.Fee <= 0 || entrants.Count == 0) return 0;

            var credits = new List<(string, long)>(entrants.Count);
            foreach (var name in entrants) credits.Add((name, session.Fee));

            await Db.LoyaltyCreditManyAsync(cfg.Currency.BalanceTable, credits,
                LedgerTableIfEnabled(cfg), "raffle", reason).ConfigureAwait(false);

            return session.Fee * entrants.Count;
        }

        // Draw any open raffle on shutdown so entrants who paid a fee are paid out
        // rather than losing their entry silently.
        private async Task ResolveOpenRaffleOnShutdownAsync()
        {
            bool has;
            lock (_raffleGate) has = _raffle is not null && !_raffle.Drawn;
            if (has) await RaffleDrawAsync().ConfigureAwait(false);
        }

        // Fisher-Yates partial shuffle over a copy — k distinct winners.
        private List<string> PickDistinct(List<string> pool, int k)
        {
            var copy = new List<string>(pool);
            int n = copy.Count;
            k = Math.Min(k, n);
            for (int i = 0; i < k; i++)
            {
                int j = i + _rng.Next(n - i);
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }
            return copy.GetRange(0, k);
        }

        // Per-winner payouts. Pot = collected entry fees, or FixedPrize as the pool
        // for a free raffle so Split/PotToOne still pay out. FixedEach ignores the pot
        // and pays FixedPrize each; SplitPot uses a largest-remainder split.
        private static long[] ComputePrizes(string mode, int winnerCount, long fee, int entrantCount, long fixedPrize, out long pot)
        {
            pot = fee > 0 ? fee * entrantCount : Math.Max(0, fixedPrize);
            var amounts = new long[winnerCount];
            if (winnerCount <= 0) return amounts;

            if (mode.Equals("FixedEach", StringComparison.OrdinalIgnoreCase))
            {
                long each = Math.Max(0, fixedPrize);
                for (int i = 0; i < winnerCount; i++) amounts[i] = each;
            }
            else if (mode.Equals("PotToOne", StringComparison.OrdinalIgnoreCase))
            {
                amounts[0] = pot; // winnerCount is forced to 1 upstream
            }
            else // SplitPot (default) — largest-remainder split
            {
                long each = pot / winnerCount;
                long rem = pot - each * winnerCount;
                for (int i = 0; i < winnerCount; i++) amounts[i] = each + (i < rem ? 1 : 0);
            }
            return amounts;
        }
    }
}
