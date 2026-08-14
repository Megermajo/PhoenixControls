using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: the earn engine — the watch-time payout tick and the passive
    // event→points map. Both credit through the atomic DB.Instance.Loyalty* layer;
    // this file only decides WHO earns HOW MUCH.
    public sealed partial class LoyaltyService
    {
        // ── Watch-time tick lifecycle ───────────────────────────────────────
        private readonly object _earnLoopGate = new();
        private bool _earnStarted;
        private CancellationTokenSource? _earnCts;
        private Task? _earnTask;

        // Per-stream dedupe set (cleared by SetStreamLive on the going-live edge) and
        // per-user daily earn accrual.
        //
        // ★ The set carries TWO scopes, not one: a bare "<user>" entry is the FOLLOW
        // dedupe, and a "firstactivity\0<user>" entry is the first-message-of-stream
        // dedupe. They share the set because they share a lifetime exactly — both are
        // "once per stream", and both must be forgotten on the same edge — so one
        // Clear() is the whole reset. A username can never contain \0, so the two
        // namespaces cannot collide.
        private readonly object _followGate = new();
        private readonly HashSet<string> _followedThisStream = new(StringComparer.OrdinalIgnoreCase);
        private const string FirstActivityKeyPrefix = "firstactivity\0";

        // Viewers who typed in chat since the last watch-time tick. ONLY populated when
        // the streamer unticked LoyaltyEarnConfig.ActiveViewersOnly — with the default
        // ticked box there is nothing to widen, so the chat tap costs one boolean read
        // per message and this set stays empty forever.
        private readonly object _chatSeenGate = new();
        private HashSet<string> _chattedSinceTick = new(StringComparer.OrdinalIgnoreCase);
        // A chat-driven set must not be unbounded (same reasoning as the gift-bomb
        // window): a raid-sized burst inside one interval stops adding rather than
        // growing without limit. Everyone already recorded still gets paid.
        private const int ChattedSinceTickMaxKeys = 5000;

        private readonly object _dailyGate = new();
        private Dictionary<string, long> _dailyEarned = new(StringComparer.OrdinalIgnoreCase);
        private DateOnly _dailyDate = DateOnly.FromDateTime(DateTime.Now);

        /// <summary>Starts the watch-time earn tick. Idempotent. The loop runs while
        /// the service lives but only pays when the tool + watch-time earn are on
        /// (and, when OnlineOnly, the stream is live).</summary>
        public void StartEarning()
        {
            lock (_earnLoopGate)
            {
                if (_earnStarted) return;
                _earnStarted = true;
                _earnCts = new CancellationTokenSource();
                var ct = _earnCts.Token;
                _earnTask = Task.Run(() => EarnLoopAsync(ct));
            }
            GlobalLogger.Log("LoyaltyService watch-time earning started.", "LoyaltyService", LogLevel.System);
        }

        // Cancels + drains the earn loop (called from ShutdownAsync).
        private async Task StopEarningAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_earnLoopGate)
            {
                cts = _earnCts; loop = _earnTask;
                _earnStarted = false; _earnCts = null; _earnTask = null;
            }
            try { cts?.Cancel(); } catch { /* best-effort */ }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { GlobalLogger.Error("LoyaltyService", "earn loop drain failed", ex); }
            }
            cts?.Dispose();
        }

        private async Task EarnLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int minutes = Math.Clamp(_config.Earn.WatchTimeIntervalMinutes, 1, 60);
                try { await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                // A bg-thread throw must never kill the process — the whole tick body
                // is guarded (SafeEvent + these try/catches are the crash-isolation).
                // The tick takes no interval argument: `minutes` is only the delay, and
                // the elapsed span used to be passed because this tick also credited watch
                // MINUTES. That half now lives in ViewerPresenceService, which measures its
                // own interval; a payout is a flat per-tick award and never scaled by it.
                try { await WatchTimeTickAsync().ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("LoyaltyService", "watch-time tick failed", ex); }
            }
        }

        // One watch-time tick, and now ONE job: sweep the active viewers, credit each of
        // them their (base × multiplier) capped amount in a single batch, then push one
        // overlay refresh and one aggregate Loyalty.OnPayout event. Widened by this
        // interval's chatters when the streamer unticked "Active viewers only" (see the
        // block below — the ticked default changes nothing).
        //
        // ★ THIS TICK NO LONGER RECORDS WATCH MINUTES. It used to carry a second,
        // independently-gated half that handed the swept logins to the Ranks tool's
        // watch-hour store, because this was the only active-viewer sweep in the suite and
        // sharing it was the only way to keep "who was paid" and "who accrued" from
        // disagreeing. Minutes are now recorded by ViewerPresenceService — always on, on
        // its own cadence, with every pre-build tool switched off — which is what makes
        // watch time a background data source rather than a passenger on the points
        // economy. Nothing in this file needs to know who is counting hours any more.
        private async Task WatchTimeTickAsync()
        {
            var cfg = _config;
            bool payPoints = cfg.Enabled
                          && cfg.Earn.WatchTimeEnabled
                          && (!cfg.Earn.OnlineOnly || _streamLive)
                          && cfg.Earn.WatchTimeAmount > 0;
            // ★ The early-out is the POINTS gate alone now. It used to read
            // `if (!payPoints && !accrue) return;` — two terms because the accrual half had
            // to run even with the economy off. With that half gone the second term would
            // be permanently false, so keeping it would have cost a Streamer.bot round-trip
            // every interval to build a credit list nobody reads; dropping the WRONG one
            // would have stopped paying. The surviving rule: no payout wanted, no sweep.
            if (!payPoints) return;

            IReadOnlyList<ActiveViewer> viewers = await FetchActiveViewersAsync().ConfigureAwait(false);
            if (viewers.Count == 0) return;

            // Always drained, even when the widening is off, so a mid-interval flip of
            // the switch can never pay out chatters recorded under a stale setting.
            var chatted = TakeChattedSinceTick();

            var excluded = BuildExclusionSet(cfg);
            // Regular status comes from the User-Management Regular GROUP — the same
            // overlay ScriptManager.Loyalty's RoleOk consults. Cheap hash lookups, and
            // false while that tool is dormant, so RegularMultiplier simply won't apply.
            var users = UserManagementService.Instance;
            int baseAmount = cfg.Earn.WatchTimeAmount;
            var credits = new List<(string, long)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var v in viewers)
            {
                string name = Normalize(v.Login);
                if (name.Length == 0 || excluded.Contains(name) || !seen.Add(name)) continue;
                double mult = MultiplierFor(cfg.Earn, v.Subscribed, v.IsMod, v.IsVip, users.IsRegular(name));
                long amount = (long)Math.Round(baseAmount * mult, MidpointRounding.AwayFromZero);
                amount = AllowEarn(cfg, name, amount);
                if (amount <= 0) continue;
                credits.Add((name, amount));
                total += amount;
            }

            // ── "Active viewers only" unticked: also pay this interval's chatters ──
            // The sweep is ONE snapshot per interval and can miss somebody who is
            // demonstrably present — they typed. Unticking the box says "don't limit the
            // payout to the sweep", so those logins are credited too. It only ever
            // BROADENS: with the box ticked (the default) this loop does not run and the
            // payout is byte-for-byte what it always was.
            //
            // The widening is a rule about MONEY and reaches nothing else. Watch MINUTES
            // are recorded by ViewerPresenceService off its own sweep, so a switch on the
            // Loyalty page cannot quietly write hours for viewers no presence sample ever
            // reported. That separation used to be maintained by hand — these logins were
            // deliberately withheld from the list the accrual half consumed — and is now
            // structural.
            //
            // Role multipliers can't apply here — a chatter outside the sweep carries no
            // subscribed/mod/VIP flag — so only the Regular group (a local lookup) does.
            if (!cfg.Earn.ActiveViewersOnly && chatted.Count > 0)
            {
                foreach (var login in chatted)
                {
                    string name = Normalize(login);
                    if (name.Length == 0 || excluded.Contains(name) || !seen.Add(name)) continue;
                    double mult = MultiplierFor(cfg.Earn, false, false, false, users.IsRegular(name));
                    long amount = (long)Math.Round(baseAmount * mult, MidpointRounding.AwayFromZero);
                    amount = AllowEarn(cfg, name, amount);
                    if (amount <= 0) continue;
                    credits.Add((name, amount));
                    total += amount;
                }
            }

            if (credits.Count == 0) return;

            string? ledger = (cfg.Currency.LedgerEnabled && cfg.Currency.LogWatchTimePayouts) ? cfg.Currency.LedgerTable : null;
            int applied = await Db.LoyaltyCreditManyAsync(cfg.Currency.BalanceTable, credits, ledger, "watchtime", "watch-time")
                .ConfigureAwait(false);
            if (applied <= 0) return;

            RaiseScript("Loyalty.OnPayout", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.count"] = applied.ToString(CultureInfo.InvariantCulture),
                ["event.total"] = total.ToString(CultureInfo.InvariantCulture),
                ["event.amount"] = baseAmount.ToString(CultureInfo.InvariantCulture),
                ["event.currency"] = cfg.Currency.NamePlural,
            });
            await AfterBalanceChangeAsync($"watch-time payout — {applied} viewer(s), {total} {cfg.Currency.NamePlural}").ConfigureAwait(false);
        }

        // ── Chat tap: activity recording + the first-message-of-stream bonus ────

        /// <summary>
        /// Records that <paramref name="login"/> typed in chat during the current
        /// watch-time interval. A no-op — one volatile read and one boolean test —
        /// unless the streamer unticked <c>Earn.ActiveViewersOnly</c>, because with the
        /// box ticked the payout is the sweep and there is nothing to widen.
        /// Called from the Hub's built-in chat tap, which sees every message whenever
        /// the tool is Active — the tap is independent of <c>Commands.AutoHandle</c>,
        /// which gates only the built-in command parsing.
        /// </summary>
        public void NoteChatActivity(string? login)
        {
            var cfg = _config;
            if (!cfg.Enabled || cfg.Earn.ActiveViewersOnly) return;
            string n = Normalize(login);
            if (n.Length == 0) return;
            lock (_chatSeenGate)
            {
                if (_chattedSinceTick.Count >= ChattedSinceTickMaxKeys && !_chattedSinceTick.Contains(n)) return;
                _chattedSinceTick.Add(n);
            }
        }

        // Hands the interval's chatters to the tick and starts a fresh set in one step,
        // so a message arriving mid-payout lands in the NEXT interval rather than being
        // paid twice or dropped.
        private IReadOnlyCollection<string> TakeChattedSinceTick()
        {
            lock (_chatSeenGate)
            {
                if (_chattedSinceTick.Count == 0) return Array.Empty<string>();
                var taken = _chattedSinceTick;
                _chattedSinceTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return taken;
            }
        }

        /// <summary>
        /// Pays the once-per-stream "first activity" bonus for a viewer's FIRST chat
        /// message of the current stream. Returns true only when points actually landed.
        /// Called from the Hub's built-in chat tap, which sees every message whenever
        /// the tool is Active — the tap runs regardless of <c>Commands.AutoHandle</c>
        /// (that switch gates only the built-in command parsing), so switching
        /// auto-handled commands off does not silence this bonus.
        ///
        /// <para>Silent no-op while the tool is off, while the bonus is unticked or set
        /// to 0 (the shipped default), for an excluded/bot account, and for every message
        /// after the first — the dedupe slot lives in the per-stream set beside the
        /// follow dedupe and is cleared on the same going-live edge. The award is a
        /// normal earn: exclusion list, per-event max and daily cap all apply, and it
        /// raises Loyalty.OnEarn like any other.</para>
        ///
        /// <para><paramref name="userKey"/> is the wallet key — the LOGIN where the
        /// platform supplied one. The caller resolves it; this method only normalizes.</para>
        /// </summary>
        public async Task<bool> TryAwardFirstActivityAsync(string? userKey)
        {
            if (!Active) return false;
            var cfg = _config;
            if (!cfg.Earn.FirstActivityEnabled || cfg.Earn.FirstActivityAmount <= 0) return false;

            string user = Normalize(userKey);
            if (user.Length == 0) return false;
            string slot = FirstActivityKeyPrefix + user;

            // Peek before anything that allocates: this runs on EVERY chat message, and
            // after a viewer's first one the set lookup is all the work that is done.
            lock (_followGate)
                if (_followedThisStream.Contains(slot)) return false;
            if (BuildExclusionSet(cfg).Contains(user)) return false;

            // Consume the per-stream slot only after every cheap gate above, so a
            // message that could never have paid does not burn the viewer's one chance.
            // Add is the authority — the peek above is an optimisation, not the guard.
            lock (_followGate)
                if (!_followedThisStream.Add(slot)) return false;

            long allowed = AllowEarn(cfg, user, cfg.Earn.FirstActivityAmount);
            if (allowed <= 0) return false;

            var res = await Db.LoyaltyCreditAsync(cfg.Currency.BalanceTable, user, allowed,
                LedgerTableIfEnabled(cfg), "event:firstactivity", "firstactivity").ConfigureAwait(false);
            if (!res.Ok) return false;

            RaiseScript("Loyalty.OnEarn", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.user"] = user,
                ["event.amount"] = allowed.ToString(CultureInfo.InvariantCulture),
                ["event.reason"] = "firstactivity",
                ["event.balance"] = res.NewBalance.ToString(CultureInfo.InvariantCulture),
                ["event.currency"] = cfg.Currency.NamePlural,
                ["user.name"] = user,
            });
            await AfterBalanceChangeAsync(
                $"{user} earned {allowed} {cfg.Currency.NamePlural} for firstactivity").ConfigureAwait(false);
            return true;
        }

        // ── Passive event earn ──────────────────────────────────────────────
        // A gift bomb arrives as the AGGREGATE event (user.count = N) and, on its
        // heels, N per-recipient GiftSub events (platform behaviour). Crediting both
        // pays the GIFTER roughly twice, so after a bomb that gifter's per-recipient
        // half is suppressed for a short window.
        //
        // Keyed PER GIFTER — never one global stamp. A global window also swallows a
        // DIFFERENT viewer's legitimate one-off gift sub that happens to land in the
        // same ten seconds, and this is the money path. AlertsService uses the same
        // window LENGTH but may stay global: it throttles alert volume, not payouts.
        //
        // Absence of a key is the "no bomb from this gifter yet" state, so unlike
        // AlertsService there is no zero-tick sentinel to floor. Clock is the shared
        // monotonic NowMs (never wall clock — see the _mono comment in LoyaltyService).
        private readonly object _giftBombGate = new();
        private readonly Dictionary<string, long> _giftBombMs = new(StringComparer.Ordinal);  // gifter -> bomb tick ms
        internal const long GiftSubSuppressAfterBombMs = 10_000;
        // Entries live 10 s and every arm sweeps the aged-out ones, so the map only
        // ever holds gifters seen inside the window. The ceiling is the backstop for a
        // burst wide enough to out-run one sweep — an unbounded dictionary on a
        // chat-driven path is a leak (same reasoning as AutomodService.MaybeSweepRate).
        private const int GiftBombWindowMaxKeys = 256;

        // Arms this gifter's window, sweeping everything that has aged out first. Only
        // runs on a bomb (rare), so the O(live gifters) sweep is free in practice.
        private void ArmGiftBombWindow(string gifter, long now)
        {
            lock (_giftBombGate)
            {
                List<string>? stale = null;
                foreach (var kv in _giftBombMs)
                    if (now - kv.Value >= GiftSubSuppressAfterBombMs)
                        (stale ??= new List<string>()).Add(kv.Key);
                if (stale is not null)
                    foreach (var k in stale) _giftBombMs.Remove(k);

                // A wide-enough burst of distinct gifters can still outgrow one sweep;
                // drop the whole map rather than let it grow without bound. Worst case
                // a handful of tails get credited twice — never a leak.
                if (_giftBombMs.Count >= GiftBombWindowMaxKeys) _giftBombMs.Clear();
                _giftBombMs[gifter] = now;
            }
        }

        // True while this gifter's OWN bomb is still inside the suppression window.
        private bool IsInGiftBombWindow(string gifter, long now)
        {
            lock (_giftBombGate)
            {
                if (!_giftBombMs.TryGetValue(gifter, out long stamp)) return false;
                if (now - stamp < GiftSubSuppressAfterBombMs) return true;
                _giftBombMs.Remove(gifter);   // aged out — reclaim on read as well as on arm
                return false;
            }
        }

        // The identity a gift event's window keys on: user.gifter where the payload
        // carries it (ScriptManager fills it from the explicit "gifter" key, falling
        // back to the actor), else user.name — on both the bomb and its tail the
        // acting user IS the gifter, which is also who gets credited below.
        private static string ResolveGifterKey(IReadOnlyDictionary<string, string> vars)
        {
            string gifter = Normalize(GetVar(vars, "user.gifter"));
            return gifter.Length != 0 ? gifter : Normalize(GetVar(vars, "user.name"));
        }

        /// <summary>
        /// Passive event award. Maps an SB/Phoenix event (Follow / Sub / Resub /
        /// gift subs / Cheer / Raid / Tip, on Twitch/YouTube/Kick) to points from
        /// <see cref="LoyaltyEarnConfig"/>, credits the triggering user (subscriber
        /// multiplier applied where the event implies a sub), and raises a per-user
        /// Loyalty.OnEarn. No-op while the tool is disabled.
        /// </summary>
        public async Task ApplyStreamEventAsync(string eventType, IReadOnlyDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(eventType) || vars is null) return;
            var kind = ClassifyEvent(eventType, out bool tailIsCredited);

            // Arm/consult the bomb window on DETECTION — before the master gate and
            // before any per-event enabled check — so a config toggle flipped between
            // the bomb and its per-recipient tail can never let the double credit
            // through. Per gifter, so an unrelated viewer's gift sub is untouched.
            if (kind is EarnKind.GiftBomb or EarnKind.Gift)
            {
                string gifter = ResolveGifterKey(vars);
                if (gifter.Length != 0)
                {
                    if (kind == EarnKind.GiftBomb)
                    {
                        // Arm only for an aggregate this file can actually double-pay
                        // (tailIsCredited) AND that carried a real count. A bomb with no
                        // parseable user.count pays 1× via ComputePointsForEvent's
                        // fallback — suppressing its N tails would pay LESS than simply
                        // crediting them, so a countless bomb arms nothing.
                        if (tailIsCredited && ParseLong(GetVar(vars, "user.count"), 0) >= 1)
                            ArmGiftBombWindow(gifter, NowMs);
                    }
                    else if (IsInGiftBombWindow(gifter, NowMs)) return;
                }
            }

            if (!Active) return;
            var cfg = _config;
            if (kind == EarnKind.None) return;

            long points = ComputePointsForEvent(kind, cfg.Earn, vars, out bool impliesSub, out string reason);
            if (points <= 0) return;
            if (impliesSub && cfg.Earn.SubMultiplier > 0)
                points = (long)Math.Round(points * cfg.Earn.SubMultiplier, MidpointRounding.AwayFromZero);
            if (points <= 0) return;

            // ★ ONE identity for the wallet, and it is the LOGIN. user.name carries the
            // platform DISPLAY name — possibly localized/non-ASCII and entirely different
            // from the login — while the watch-time tick above credits Normalize(v.Login).
            // Keying this path on the display name gave such a viewer TWO wallets: one
            // their watch time filled, one their sub/cheer/raid points filled, and no
            // command could read both. event.user_login is written only when the payload
            // carried a real platform login, so the fallback to user.name is what keeps a
            // broker/tip event (which carries no login) working exactly as before.
            string user = Normalize(GetVar(vars, "event.user_login"));
            if (user.Length == 0) user = Normalize(GetVar(vars, "user.name"));
            if (user.Length == 0) return;
            if (BuildExclusionSet(cfg).Contains(user)) return;

            // ── Donor identity gate ──────────────────────────────────────────
            // Every other earn kind arrives from a platform event where the actor
            // is authenticated: Streamer.bot vouches that the follower/subscriber/
            // cheerer really is that account. A TIP does not work that way. The
            // donor types their own display name into the broker's tip form, and
            // that free text lands in user.name — so "MajoTheStreamer" tipping €1
            // would credit the broadcaster's own wallet, and naming any regular
            // viewer credits theirs. It is an impersonation vector, not a
            // theoretical one, and it is why this is gated rather than warned.
            //
            // The discriminator is event.user_login: it is written only when the
            // payload carried a real platform login, which a third-party broker
            // payload never does. Present ⇒ verified actor ⇒ credit as before.
            // Absent on a tip ⇒ credit ONLY if the streamer explicitly opted in.
            // Scoped to BROKER tips. The gate exists because a broker's donor field is
            // free text; a platform money event (Twitch SuperChat / charity, Kick
            // KicksGifted, YouTube JewelsGifted) carries a platform-authenticated
            // actor and must not be caught by it. Kick is the concrete case:
            // ResolveActorLogin needs a nested data.user object, but Kick payloads
            // carry the actor as a flat string, so event.user_login is empty and every
            // Kicks gift was silently refused with a message blaming "broker-supplied
            // free text" for a platform event — wrong outcome and misleading diagnosis.
            if (kind == EarnKind.Tip && DonationIngest.IsDonationEvent(eventType))
            {
                string verifiedLogin = GetVar(vars, "event.user_login");
                if (string.IsNullOrWhiteSpace(verifiedLogin) && !cfg.Earn.TipCreditUnverifiedDonor)
                {
                    GlobalLogger.Log(
                        $"Tip from '{user}' did not pay points — the donor name is broker-supplied free text, not a verified account. " +
                        "Enable \"credit tips to the typed donor name\" in the Loyalty page if you accept that risk.",
                        "LoyaltyService", LogLevel.Debug);
                    return;
                }
            }

            // Follow-dedupe (once per stream) — only consume a slot for an actual award.
            if (kind == EarnKind.Follow && cfg.AntiAbuse.DedupeFollows)
            {
                lock (_followGate)
                    if (!_followedThisStream.Add(user)) return;
            }

            long allowed = AllowEarn(cfg, user, points);
            if (allowed <= 0) return;

            var res = await Db.LoyaltyCreditAsync(cfg.Currency.BalanceTable, user, allowed,
                LedgerTableIfEnabled(cfg), "event:" + reason, reason).ConfigureAwait(false);
            if (!res.Ok) return;

            RaiseScript("Loyalty.OnEarn", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["event.user"] = user,
                ["event.amount"] = allowed.ToString(CultureInfo.InvariantCulture),
                ["event.reason"] = reason,
                ["event.balance"] = res.NewBalance.ToString(CultureInfo.InvariantCulture),
                ["event.currency"] = cfg.Currency.NamePlural,
                ["user.name"] = user,
            });
            await AfterBalanceChangeAsync($"{user} earned {allowed} {cfg.Currency.NamePlural} for {reason}").ConfigureAwait(false);
        }

        // ── Multiplier + anti-abuse + classification helpers ────────────────
        // Sub is reliable; mod/VIP best-effort (only when the platform reports the
        // flag). Regular is the User-Management Regular group (see the caller).
        //
        // ★ LoyaltyEarnConfig.RegularThresholdHours is STILL a streamer note, and that is
        // now a decision rather than a limitation. A watch-hour store exists — the open
        // "WatchTime" table, filled by the always-on ViewerPresenceService — so the wire
        // could be made, but connecting it would turn a setting that has been inert on
        // every shipped build into a live rule, and the first update after that would start
        // handing out Regular to viewers on existing installs with nobody having asked.
        // Promoting on hours already has TWO surfaces that were designed for it and are
        // opt-in by default: UserManagementConfig.RegularWatchHours (and UserGroup
        // .AutoWatchHours for a custom group), which evaluate the rule live rather than
        // writing members into a list; and the Ranks ladder, where a rung can carry a
        // watch-minute threshold and grant the Regular group.
        private static double MultiplierFor(LoyaltyEarnConfig e, bool sub, bool mod, bool vip, bool isRegular)
        {
            var applicable = new List<double>(4);
            if (sub) applicable.Add(e.SubMultiplier);
            if (mod) applicable.Add(e.ModMultiplier);
            if (vip) applicable.Add(e.VipMultiplier);
            if (isRegular) applicable.Add(e.RegularMultiplier);
            if (applicable.Count == 0) return 1.0;

            if (string.Equals(e.MultiplierStacking, "multiply", StringComparison.OrdinalIgnoreCase))
            {
                double m = 1.0;
                foreach (var x in applicable) m *= (x <= 0 ? 1.0 : x);
                return m <= 0 ? 1.0 : m;
            }
            // "highest" (default): the single largest applicable multiplier, floored at 1.
            double max = 1.0;
            foreach (var x in applicable) if (x > max) max = x;
            return max;
        }

        // Applies the per-event max and per-user daily cap, recording the accrual.
        // Returns the amount actually creditable (0 when the daily cap is exhausted).
        private long AllowEarn(LoyaltyConfig cfg, string user, long amount)
        {
            if (amount <= 0) return 0;
            int perEvent = cfg.AntiAbuse.PerEventMax;
            if (perEvent > 0 && amount > perEvent) amount = perEvent;

            int dailyCap = cfg.AntiAbuse.DailyCapPerUser;
            lock (_dailyGate)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (today != _dailyDate) { _dailyEarned.Clear(); _dailyDate = today; }

                if (dailyCap > 0)
                {
                    _dailyEarned.TryGetValue(user, out long soFar);
                    long room = dailyCap - soFar;
                    if (room <= 0) return 0;
                    if (amount > room) amount = room;
                }
                if (amount > 0)
                {
                    _dailyEarned.TryGetValue(user, out long cur);
                    _dailyEarned[user] = cur + amount;
                }
            }
            return amount;
        }

        private enum EarnKind { None, Follow, Sub, Resub, Gift, GiftBomb, Cheer, Raid, Tip }

        // Mirrors TimerService.ClassifyEvent's leaf-name set (Twitch/YouTube/Kick),
        // but yields POINTS instead of seconds. Gift is split bomb-vs-single so the
        // suppression window above can tell an aggregate from a per-recipient tail —
        // the points math is identical for both.
        //
        // tailIsCredited marks the ONE aggregate whose per-recipient tail this
        // classifier ALSO credits: Twitch's GiftBomb, whose tail arrives as "GiftSub".
        // Kick's MassGiftSubscription tails as "GiftSubscription" and YouTube's
        // MembershipGift as "GiftMembershipReceived" — neither leaf reaches
        // EarnKind.Gift, so neither has ever been double-credited and neither may arm
        // the window (arming would suppress an unrelated Twitch gift sub for nothing).
        // If a leaf is ever added to the Gift branch below, widen this flag with it.
        private static EarnKind ClassifyEvent(string eventType, out bool tailIsCredited)
        {
            var kind = ClassifyByLeaf(eventType, out tailIsCredited);

            // A third-party money broker may reuse a PLATFORM word: Ko-Fi's paid
            // membership arrives as Kofi.Subscription / Kofi.Resubscription, whose leaves
            // are exactly the Twitch sub leaves, so it was classified as a Twitch Tier-1
            // sub and minted sub points at the sub multiplier — money arriving through a
            // door the tip config, the donor-identity gate and the tip opt-out migration
            // all sit beside rather than in front of.
            //
            // ★ Scoped to the ENGAGEMENT kinds, mirroring TimerService.ClassifyEvent —
            // see its comment for why a blanket "broker ⇒ Tip" is wrong (it matches a
            // SOURCE PREFIX, so it would pay out on Patreon.PledgeDeleted, a
            // cancellation, and double-count Shopify's two-event order pair).
            if (kind is EarnKind.Sub or EarnKind.Resub or EarnKind.Gift or EarnKind.GiftBomb
                && DonationIngest.IsDonationEvent(eventType))
            {
                // A broker "gift" is a purchase, not a Twitch gift sub, so it must not arm
                // the gift-bomb suppression window either.
                tailIsCredited = false;
                return EarnKind.Tip;
            }

            return kind;
        }

        private static EarnKind ClassifyByLeaf(string eventType, out bool tailIsCredited)
        {
            tailIsCredited = false;
            int dot = eventType.LastIndexOf('.');
            string leaf = dot >= 0 ? eventType[(dot + 1)..] : eventType;

            if (Eq(leaf, "GiftBomb"))
            {
                tailIsCredited = true;
                return EarnKind.GiftBomb;
            }
            if (Eq(leaf, "MassGiftSubscription") || Eq(leaf, "MembershipGift"))
                return EarnKind.GiftBomb;
            if (Eq(leaf, "GiftSub"))
                return EarnKind.Gift;
            if (Eq(leaf, "Resub") || Eq(leaf, "Resubscription"))
                return EarnKind.Resub;
            if (Eq(leaf, "Sub") || Eq(leaf, "Subscription") || Eq(leaf, "NewSubscriber") || Eq(leaf, "NewSponsor"))
                return EarnKind.Sub;
            if (Eq(leaf, "Cheer"))
                return EarnKind.Cheer;
            if (Eq(leaf, "Follow"))
                return EarnKind.Follow;
            if (Eq(leaf, "Raid"))
                return EarnKind.Raid;
            // TimerService counted "CampaignTip" (Pally.gg) and "CharityDonation" (Twitch's
            // own money leaf) as tips while this classifier did not, so those donations
            // extended the subathon and fired an alert but paid NO points. Listing them
            // here ends that disagreement — and it is done by LEAF, not by the broker
            // source rule above, so it also covers Twitch charity, which is not a broker.
            if (Eq(leaf, "SuperChat") || Eq(leaf, "SuperSticker") || Eq(leaf, "KicksGifted") || Eq(leaf, "JewelsGifted")
                || Eq(leaf, "Tip") || Eq(leaf, "Donation") || Eq(leaf, "CampaignTip") || Eq(leaf, "CharityDonation"))
                return EarnKind.Tip;
            return EarnKind.None;
        }

        // Points for one event given the earn config + event vars. impliesSub marks
        // events whose triggering user is (by definition) a subscriber, so the caller
        // applies the subscriber multiplier.
        private static long ComputePointsForEvent(EarnKind kind, LoyaltyEarnConfig e,
            IReadOnlyDictionary<string, string> vars, out bool impliesSub, out string reason)
        {
            impliesSub = false;
            reason = kind.ToString().ToLowerInvariant();
            switch (kind)
            {
                case EarnKind.Follow:
                    if (!e.FollowEnabled) return 0;
                    return e.FollowAmount; // 0 = off

                case EarnKind.Sub:
                case EarnKind.Resub:
                    if (!e.SubEnabled) return 0;
                    impliesSub = true;
                    reason = kind == EarnKind.Resub ? "resub" : "sub";
                    return TierPoints(e, GetVar(vars, "user.tier"));

                case EarnKind.Gift:
                case EarnKind.GiftBomb:
                {
                    if (!e.SubEnabled) return 0;
                    reason = "giftsub";
                    // The gifter isn't necessarily a subscriber, so no sub multiplier.
                    // The bomb carries the whole batch in user.count; a single gifted
                    // sub carries no count and falls back to 1 — which is also why an
                    // aggregate WITHOUT a count arms no suppression window: it pays the
                    // same 1× its tails would, so eating them would under-pay.
                    long count = ParseLong(GetVar(vars, "user.count"), 1);
                    if (count < 1) count = 1;
                    return count * e.GiftSubAmount;
                }

                case EarnKind.Cheer:
                {
                    if (!e.CheerEnabled || e.CheerPer100Bits <= 0) return 0;
                    reason = "cheer";
                    long bits = ParseLong(GetVar(vars, "user.bits"), 0);
                    if (bits <= 0) bits = ParseLong(GetVar(vars, "event.bits"), 0);
                    if (bits <= 0) return 0;
                    return bits * e.CheerPer100Bits / 100;
                }

                case EarnKind.Raid:
                {
                    if (!e.RaidEnabled) return 0;
                    reason = "raid";
                    long viewers = ParseLong(GetVar(vars, "user.count"), -1);
                    if (viewers < 0) viewers = ParseLong(GetVar(vars, "raid.viewers"), -1);
                    if (viewers < 0) viewers = ParseLong(GetVar(vars, "user.viewers"), 0);
                    long perViewer = (viewers > 0 && e.RaidPerViewer > 0) ? viewers * e.RaidPerViewer : 0;
                    return e.RaidFlat + perViewer;
                }

                case EarnKind.Tip:
                {
                    if (!e.TipEnabled || e.TipPerUnit <= 0) return 0;
                    reason = "tip";
                    double amount = ParseDouble(GetVar(vars, "event.amount"));
                    if (amount <= 0) amount = ParseDouble(GetVar(vars, "user.amount"));
                    if (amount <= 0) return 0;
                    return (long)Math.Round(amount * e.TipPerUnit, MidpointRounding.AwayFromZero);
                }

                default:
                    return 0;
            }
        }

        private static long TierPoints(LoyaltyEarnConfig e, string? tier)
        {
            string t = (tier ?? string.Empty).Trim();
            if (t.Equals("prime", StringComparison.OrdinalIgnoreCase)) return e.SubPrime;
            return t switch
            {
                "2" => e.SubTier2,
                "3" => e.SubTier3,
                _ => e.SubTier1, // "1" and unknowns default to tier 1
            };
        }

        private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        private static string GetVar(IReadOnlyDictionary<string, string> vars, string key)
            => vars.TryGetValue(key, out var v) ? v : string.Empty;

        private static long ParseLong(string s, long fallback)
            => long.TryParse((s ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        private static double ParseDouble(string s)
            => double.TryParse((s ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
