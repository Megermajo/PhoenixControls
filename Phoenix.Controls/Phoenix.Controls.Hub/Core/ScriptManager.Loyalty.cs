using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Loyalty (viewer points economy) — the Hub-side wiring of the
    // third pre-build tool (sibling of Timer and Giveaway). Two responsibilities:
    //
    //   1. Seam injection — LoyaltyService can't touch the Bus, the script
    //      dispatcher, Streamer.bot's active-viewer sweep, the live Bot-Accounts
    //      list, or the VISUAL_TRIGGER pipeline itself (all Hub-side). Wired at the
    //      top of RegisterLoyaltyCommands, mirroring ScriptManager.Timer.cs's
    //      RaiseScriptEvent/BusEmit and ScriptManager.Giveaway.cs's
    //      SubscriberStatusResolver. The active-viewer seam is the one exception to
    //      "wired here": it is set by RegisterViewerPresenceSeams (called from this
    //      registrar) because the sweep is now shared with ViewerPresenceService —
    //      see ScriptManager.ViewerPresence.cs. (The overlay readout needs no seam: the service
    //      publishes loyalty.leaderboard / loyalty.currency into the Overlay Live
    //      Channel through its own LiveStore property, defaulted to the process-wide
    //      store — nothing to wire, here or in HubBootstrapper. Same for Timer's.)
    //
    //   2. The BUILT-IN chat-command parser (TryHandleLoyaltyChatCommandAsync). It
    //      runs at the chat entry BEFORE the logic-dir bail so !points / !gamble /
    //      !raffle etc. work with zero authored scripts. Default-OFF is a total
    //      no-op: the parser early-returns false the instant the tool is disabled.
    //      While the tool IS Active, the observe-only chat tap (identity map,
    //      chat-activity note, first-activity bonus) runs for every message;
    //      Commands.AutoHandle gates only the command handling below it.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        // Per-user per-command cooldown map for the BUILT-IN chat commands only.
        // The five minigames enforce their own cooldowns inside LoyaltyService; this
        // map gates the config commands (LoyaltyCommand.CooldownSeconds). Monotonic
        // Stopwatch clock — never wall clock — so an NTP step / DST change can't
        // unblock a cooldown early. Keyed "<trigger>\0<normUser>" -> end-tick ms.
        private readonly object _loyaltyCdGate = new();
        private readonly Dictionary<string, long> _loyaltyUserCdMs = new(StringComparer.Ordinal);
        private static readonly System.Diagnostics.Stopwatch _loyaltyMono = System.Diagnostics.Stopwatch.StartNew();

        private void RegisterLoyaltyCommands()
        {
            // ── Seams ────────────────────────────────────────────────────────
            // Loyalty.On{Earn,Payout,Redeem,Raffle} script events flow back through
            // the generic-event dispatcher with pre-built vars (presetVars != null
            // skips the JsonElement var-builder AND the earn re-feed guard in
            // ExecuteGenericEventAsync). Bus fan-out reuses the same Target="*"
            // broadcast shape as bus.broadcast / the Timer feed.
            // Both seams are Action (the service can't await them), so the async
            // event-dispatch / bus-broadcast is fired-and-forgotten HERE through
            // AsyncErrorBoundary.SafeRunAsync — faults route to the log, expected
            // shutdown cancellation is swallowed. (Timer's are Func<…,Task> and the
            // service awaits them itself; the Action shape moves that job to us.)
            LoyaltyService.Instance.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "LoyaltyService", $"RaiseScriptEvent({phoenixEvent})");
            };
            LoyaltyService.Instance.BusEmit = (busType, payloadJson) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => Bus.Instance.BroadcastAsync(new BusMessage
                    {
                        Type = busType,
                        Source = "Hub",
                        Target = "*",
                        Payload = string.IsNullOrEmpty(payloadJson) ? "{}" : payloadJson,
                    }),
                    "LoyaltyService", $"BusEmit({busType})");
            };
            // Active-viewer sweep + the always-on presence sampler behind it. Loyalty
            // used to own a PRIVATE GetActiveViewers round-trip here; it now reads the
            // shared sample, and the wiring for both sides lives together in
            // ScriptManager.ViewerPresence.cs (which also sets ActiveViewersProvider —
            // splitting the two halves across two call sites is how they drift apart).
            // Called from this registrar because it is already on the ScriptManager
            // construction path and it is the seam block these providers belong to.
            RegisterViewerPresenceSeams();
            // Live Hub Bot Accounts list (read each call so a Settings edit is picked up).
            LoyaltyService.Instance.BotAccountsProvider = GetLoyaltyBotAccounts;
            // Reward effects ride the SAME VISUAL_TRIGGER pipeline Architect uses.
            LoyaltyService.Instance.FireVisualTrigger = FireLoyaltyRewardVisualAsync;

            // The six points.* script commands (add / remove / set / give /
            // get_balance / top) were RETIRED in the 2026-08 tool-node cut with the
            // Architect Loyalty.* wrapper nodes: the balance and ledger are OPEN
            // tables, so graphs use the generic db.* family (db.top replaces
            // points.top). The old names answer through ScriptManager.RetiredCommands
            // shims; the tool's chat commands, games and store are untouched.
        }

        private static long ParseLongToken(string s)
        {
            string t = (s ?? string.Empty).Trim().Trim('"').Trim();
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            {
                double r = Math.Round(d, MidpointRounding.AwayFromZero);
                // Finite is NOT enough: an out-of-range magnitude ("1e20") casts to
                // long.MinValue with no signal, which would hand the service a huge
                // NEGATIVE amount. Out of range is garbage input — fall through to the
                // 0 the rest of this parser returns for anything it can't read.
                if (FitsInLong(r)) return (long)r;
            }
            return 0;
        }

        // 2^63. (double)long.MaxValue rounds UP to exactly this, so the upper bound is
        // EXCLUSIVE; (double)long.MinValue is representable exactly, so the lower bound
        // is inclusive. Guards every double→long cast in the tool parsers.
        private const double LongCastUpperExclusive = 9223372036854775808.0;

        private static bool FitsInLong(double rounded)
            => rounded >= long.MinValue && rounded < LongCastUpperExclusive;

        // ── Seam: live Hub Bot Accounts ──────────────────────────────────────
        // AppConfig.BotUsername is a comma-separated list (same split as
        // WS.RebuildBlockedAccountsCache); read each call so a Settings edit is
        // reflected without a restart. Trimmed + lowercased.
        internal IReadOnlyCollection<string> GetLoyaltyBotAccounts()
        {
            string raw = ConfigManager.Current?.BotUsername ?? string.Empty;
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(raw))
                foreach (var part in raw.Split(','))
                    if (!string.IsNullOrWhiteSpace(part)) list.Add(part.Trim().ToLowerInvariant());
            return list;
        }

        // ── Seam: reward VISUAL_TRIGGER ──────────────────────────────────────
        // A reward carries only (LayerId, TriggerName) — no widget id. LayerRuntime
        // routes per (LayerId, WidgetId) and the browser matches by widget, so we
        // resolve every widget on the layer that owns the trigger and fire each
        // through the SAME fire-and-forget Bus method (Bus.TriggerVisualQueuedAsync)
        // that visual.trigger_queued uses. No wait — reward effects are one-shot.
        internal Task FireLoyaltyRewardVisualAsync(string layerId, string triggerName)
            => FireVisualTriggerFanOutAsync(layerId, triggerName, eventData: null, source: "LoyaltyService");

        // Mirror compositor.js findTrigger — an explicit 'onTrigger:<name>' matches
        // both the full 'onTrigger:greet' and the bare 'greet' spelling.
        private static bool WidgetOwnsTrigger(LayerWidget w, string triggerName)
        {
            foreach (var t in w.Triggers)
            {
                if (t.Name.Equals(triggerName, StringComparison.OrdinalIgnoreCase)) return true;
                if (t.Name.Equals("onTrigger:" + triggerName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ── Chat identity map (display name → login) ─────────────────────────
        // The wallet is keyed on the LOGIN, and the ACTOR side of every built-in
        // command resolves it from msg.Login. A COUNTERPARTY does not: "!give
        // NinjaKid 100", "!duel NinjaKid 100", a mod's "!setpoints NinjaKid 0" all
        // carry raw chat text, which in practice is the target's DISPLAY name. Passing
        // that straight to LoyaltyService keys the operation on a row nothing else
        // ever touches — !give debits the sender's real wallet and credits an orphan
        // (the points leave the economy), !setpoints zeroes a phantom while the real
        // balance stands, and a duel parked under the display name can never be found
        // by an !accept that looks up the accepter's login, so the challenge expires
        // in silence. Both halves of a two-sided operation must live in ONE identity
        // space; ResolveLoyaltyTarget puts them there.
        //
        // The map is filled by the observe-only chat tap, which sees msg.Login and
        // msg.Username together on EVERY message. Two entry kinds:
        //
        //   authoritative — the key IS a login (that account chatted)
        //   weak          — the key is a display name that differs from its own login
        //
        // A weak entry never overwrites an authoritative one, so a stylized display
        // name that happens to spell some other viewer's login cannot hijack it; a
        // login always resolves to itself. Unmapped names fall through to the
        // lowercased token, which IS the login whenever the chatter typed one — i.e.
        // the pre-map behaviour, never worse than it.
        //
        // Bounded FIFO: a raid must not grow this without limit. Eviction is by
        // insertion order, not by use — the map only has to cover the viewers who are
        // talking in the current session, which is exactly the recent window.
        private const int LoyaltyIdentityCap = 4096;
        private readonly object _loyaltyIdGate = new();
        private readonly Dictionary<string, (string Login, bool Authoritative)> _loyaltyIdentity =
            new(StringComparer.Ordinal);
        private readonly Queue<string> _loyaltyIdentityOrder = new();

        // Lowercase + trim (what LoyaltyService.Normalize does) PLUS a leading '@'
        // strip, which it does not — "!give @NinjaKid 100" is the normal way viewers
        // type a name and used to credit a literal "@ninjakid" wallet.
        private static string NormalizeLoyaltyName(string? raw)
        {
            string s = (raw ?? string.Empty).Trim();
            if (s.Length == 0) return string.Empty;
            s = s.TrimStart('@').Trim();
            return s.ToLowerInvariant();
        }

        // ★ Whether this platform binds the display name 1:1 to the account — the
        // same security line UserManagementService.DisplayNameIsAccountBound draws
        // (mirrored here because that guard is private to its tool). On Twitch the
        // display name is the login's own casing / localized form, so honouring it
        // grants nothing a stranger can claim. YouTube and Kick let any viewer pick
        // any display name at any time, and the duel accept/decline fallback below
        // SETTLES someone's wager: a viewer renamed to a victim's login could accept
        // (and deliberately lose) the victim's duel, or decline it out from under
        // them. A null/empty platform means no ChatMessage was in scope;
        // ChatMessage.Platform itself defaults to Twitch, so message-borne callers
        // keep the exact fallback behaviour they always had.
        private static bool LoyaltyDisplayNameIsAccountBound(string? platform)
            => string.IsNullOrEmpty(platform)
            || string.Equals(platform, ChatPlatforms.Twitch, StringComparison.OrdinalIgnoreCase);

        // Record what this message proves about one chatter's identity.
        private void RememberLoyaltyIdentity(string? login, string? display)
        {
            string l = NormalizeLoyaltyName(login);
            if (l.Length == 0) return;
            RememberLoyaltyIdentityEntry(l, l, authoritative: true);
            string d = NormalizeLoyaltyName(display);
            if (d.Length == 0 || string.Equals(d, l, StringComparison.Ordinal)) return;
            RememberLoyaltyIdentityEntry(d, l, authoritative: false);
        }

        private void RememberLoyaltyIdentityEntry(string key, string login, bool authoritative)
        {
            lock (_loyaltyIdGate)
            {
                if (_loyaltyIdentity.TryGetValue(key, out var existing))
                {
                    // A display name must never displace a real login. Re-seeing a key
                    // does not re-queue it, so the FIFO stays insertion-ordered.
                    if (!authoritative && existing.Authoritative) return;
                    _loyaltyIdentity[key] = (login, existing.Authoritative || authoritative);
                    return;
                }
                _loyaltyIdentity[key] = (login, authoritative);
                _loyaltyIdentityOrder.Enqueue(key);
                while (_loyaltyIdentityOrder.Count > LoyaltyIdentityCap)
                    _loyaltyIdentity.Remove(_loyaltyIdentityOrder.Dequeue());
            }
        }

        // A counterparty token → the same identity space the actor key lives in.
        private string ResolveLoyaltyTarget(string? typed)
        {
            string k = NormalizeLoyaltyName(typed);
            if (k.Length == 0) return string.Empty;
            lock (_loyaltyIdGate)
                if (_loyaltyIdentity.TryGetValue(k, out var e)) return e.Login;
            return k;
        }

        // ── Built-in chat-command parser ─────────────────────────────────────
        /// <summary>
        /// The built-in Loyalty chat commands (points / games / rewards). Called at
        /// the chat entry BEFORE the logic-dir bail so commands work with zero
        /// authored scripts. Returns true only when the tool actually recognized AND
        /// handled the message (so ScriptManager can suppress author dispatch when
        /// Config.Commands.SuppressAuthorDispatchWhenHandled). DEFAULT-OFF IS A TOTAL
        /// NO-OP — this returns false the instant the tool is disabled, changing
        /// nothing about existing chat/script behaviour.
        ///
        /// <para>TWO gates, and they cover DIFFERENT halves. The tool's master switch
        /// (<c>Config.Enabled</c>) gates everything. <c>Commands.AutoHandle</c> gates
        /// ONLY the command handling below it — the observe-only chat tap (identity-map
        /// learning, NoteChatActivity, the first-activity bonus) runs for every message
        /// whenever the tool is Active. Those three are Earn/identity features the Earn
        /// page arms independently of "Auto-handle commands"; gating them behind
        /// AutoHandle silently killed all three while the page showed them armed.</para>
        /// </summary>
        public async Task<bool> TryHandleLoyaltyChatCommandAsync(ChatMessage msg)
        {
            var svc = LoyaltyService.Instance;
            if (!svc.Active) return false;                       // Config.Enabled == false → no-op
            var cfg = svc.Config;
            if (cfg is null || msg is null) return false;

            string userDisplay = string.IsNullOrWhiteSpace(msg.Username) ? "someone" : msg.Username.Trim();
            // ★ ONE identity for the wallet, and it is the LOGIN. msg.Username carries the
            // platform DISPLAY name (WS.cs sets it from displayName on Twitch) — possibly
            // localized/non-ASCII and entirely different from the login — while the
            // watch-time sweep credits the login. Keying the commands on the display name
            // gave such a viewer TWO wallets: hours of watch-time points in one row, every
            // !points / !gamble / !give / !redeem read and write against another, and the
            // COLLATE NOCASE matching in DB.Loyalty unifies only CASE, never two genuinely
            // different strings. msg.Login is empty only when the payload carried no login,
            // which is the one case the display name is still the best identity available.
            // (The role gate one block down already resolved by login — see Effective(msg).)
            // userDisplay stays for chat TEXT only.
            string userKey = (!string.IsNullOrWhiteSpace(msg.Login) ? msg.Login : msg.Username ?? string.Empty)
                .Trim().ToLowerInvariant();

            // ── Observe-only chat tap (runs for EVERY message, command or not) ──
            // Learning who this chatter is comes FIRST and unconditionally: a command
            // in this very message may name them as a counterparty (a mod correcting
            // their own balance, a viewer duelling someone who just spoke), and the
            // map is the only thing that can put that name in the same identity space
            // as the wallet. Pass userKey rather than msg.Login so a payload with no
            // login still anchors the display name to the key the commands use.
            RememberLoyaltyIdentity(userKey, msg.Username);
            // Neither call below can consume the line or change what this method returns: the
            // activity note is a set insert that only happens when the streamer widened
            // the watch-time sweep, and the first-activity bonus is a once-per-stream
            // award that returns false without touching the DB when it is off (the
            // shipped default is 0 = off) or already paid. Placed BEFORE the '!' test on
            // purpose — "first activity" is the first MESSAGE of the stream, not the
            // first command.
            svc.NoteChatActivity(userKey);
            await svc.TryAwardFirstActivityAsync(userKey).ConfigureAwait(false);

            // ── AutoHandle gate — COMMANDS only, and it must sit BELOW the tap ──
            // "Auto-handle commands" is the Commands page's switch over the built-in
            // parser; it says nothing about the Earn features above. It used to sit at
            // the top of this method, which starved the tap: switching it OFF silently
            // killed first-activity awards, the ActiveViewersOnly chat-activity
            // widening and identity-map learning while the Earn page showed them
            // armed. The tap is observe-only (never returns handled), so running it
            // first cannot change what an AutoHandle-off Hub replies to chat.
            if (!cfg.Commands.AutoHandle) return false;

            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return false;

            string bodyText = text.Substring(1);
            int sp = bodyText.IndexOf(' ');
            string trigger = (sp < 0 ? bodyText : bodyText.Substring(0, sp)).Trim();
            if (trigger.Length == 0) return false;
            string rest = sp < 0 ? string.Empty : bodyText.Substring(sp + 1).Trim();
            string[] tok = rest.Length == 0
                ? Array.Empty<string>()
                : rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            string currency = cfg.Currency.NamePlural;
            var C = cfg.Commands;
            var G = cfg.Games;

            // Verb match goes through the one shared canonicalizer. Loyalty has no
            // config-normalization pass (its own `Normalize` is a USERNAME
            // normalizer), so a configured "!points" reached this comparison
            // verbatim against a `trigger` whose '!' the parse above had already
            // removed — every Loyalty command and minigame was dead for anyone who
            // typed the bang into the field. Empty-never-matches is preserved.
            bool Match(string configured) => ChatVerb.Matches(configured, trigger);
            // Usage lines print "!<verb>", so the verb is canonicalized there too —
            // otherwise a configured "!give" renders "Usage: !!give <user> <amount>".
            static string Verb(string? configured) => ChatVerb.Canonical(configured);
            // A verb rendered INTO a chat line that tells a viewer what to type
            // ("Type !accept within 60s", "Type !join to enter"). Same
            // canonicalization as Verb, plus one extra rule: a blank verb renders as
            // nothing rather than a bare "!". Blank is how this suite spells "this
            // word is off" — ChatVerb.Matches never matches an empty configured verb —
            // so advertising "!" would be telling viewers to type a command that
            // cannot possibly be recognized.
            static string Say(string? configured)
            {
                string v = ChatVerb.Canonical(configured);
                return v.Length == 0 ? string.Empty : "!" + v;
            }
            // Role checks consult the User-Management group overlay (a group-granted
            // Mod/VIP/Sub passes like the platform rank, and the Regular tick resolves
            // off the same overlay; passthrough while dormant).
            var eff = UserManagementService.Instance.Effective(msg);
            bool RoleOk(LoyaltyRoles roles) =>
                roles != null && roles.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);
            // Gate a config command: role + per-user cooldown. Returns false → blocked
            // (caller returns true/handled, silently). Stamps the cooldown on pass.
            bool GateCommand(LoyaltyCommand cmd)
                => RoleOk(cmd.Roles) && LoyaltyCooldownOk(cmd.Trigger, userKey, cmd.CooldownSeconds);

            // ── Config commands ──────────────────────────────────────────────
            if (C.Balance.Enabled && Match(C.Balance.Trigger))
            {
                if (!GateCommand(C.Balance)) return true;
                // Bare "!points" reads YOUR OWN wallet, so it must look up the LOGIN —
                // reading the display name is how a viewer with a localized name was told
                // they had 0 after hours of watching. The printed name stays the display
                // name; only the lookup key changes. A NAMED target is still whatever the
                // chatter typed, resolved through the identity map so a viewer whose
                // display name is not their login re-cased is looked up on the same row
                // their own "!points" reads.
                string targetKey  = tok.Length > 0 ? ResolveLoyaltyTarget(tok[0]) : userKey;
                string targetName = tok.Length > 0 ? tok[0].Trim() : userDisplay;
                long bal = await svc.GetBalanceAsync(targetKey).ConfigureAwait(false);
                SendLoyaltyReply(msg, $"{targetName} has {bal} {currency}.");
                return true;
            }
            if (C.Give.Enabled && Match(C.Give.Trigger))
            {
                if (!GateCommand(C.Give)) return true;
                if (tok.Length < 2) { SendLoyaltyReply(msg, $"Usage: !{Verb(C.Give.Trigger)} <user> <amount>"); return true; }
                string to = tok[0].Trim();
                long amount = ParseLongToken(tok[1]);
                // BOTH sides must be the login. The sender is debited, so handing
                // GiveAsync the display name debited a second, unreachable wallet (and
                // reported NoFunds for a viewer who had the points); the recipient is
                // credited, so an unresolved display name there parks the transferred
                // points in an orphan row no command ever reads — the sender really
                // loses them. `to` (as typed) is kept for the chat line only.
                var res = await svc.GiveAsync(userKey, ResolveLoyaltyTarget(to), amount).ConfigureAwait(false);
                SendLoyaltyReply(msg, res.Outcome switch
                {
                    LoyaltyOutcome.Ok      => $"{userDisplay} gave {amount} {currency} to {to}. Balance: {res.NewBalance}.",
                    LoyaltyOutcome.NoFunds => $"{userDisplay}, you don't have enough {currency}.",
                    LoyaltyOutcome.Invalid => $"{userDisplay}, invalid transfer.",
                    _ => string.Empty,
                });
                return true;
            }
            if (C.Top.Enabled && Match(C.Top.Trigger))
            {
                if (!GateCommand(C.Top)) return true;
                int n = tok.Length > 0 && int.TryParse(tok[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nn) && nn > 0 ? nn : 5;
                if (n > 20) n = 20;
                var top = await svc.TopAsync(n).ConfigureAwait(false);
                SendLoyaltyReply(msg, top.Count == 0
                    ? $"No {currency} leaderboard yet."
                    : "Top " + top.Count + ": " + string.Join(", ", top.Select(s => $"{s.Rank}. {s.Name} ({s.Balance})")));
                return true;
            }
            if (C.Watchtime.Enabled && Match(C.Watchtime.Trigger))
            {
                if (!GateCommand(C.Watchtime)) return true;
                // A watch-hour store now EXISTS — it belongs to the Ranks tool, and the
                // honest answer is whatever is IN it.
                //
                // ★ The read is UNGATED, deliberately. It used to sit behind
                // RanksService.WatchTimeAccrualWanted, which is false whenever
                // WatchTimeOnlineOnly is set and no StreamOnline EDGE has been seen — so
                // restarting the Hub mid-stream told a viewer with 340 recorded minutes that
                // hours "aren't tracked" and to go enable a tool that was already enabled.
                // "Are we accruing right now?" is not the same question as "what is on
                // record?", and only the second one was asked. The recorded value answers
                // for itself.
                //
                // ★ The key is the LOGIN, not the display name. WatchTime rows are written
                // from the login the active-viewer sweep hands over, so a viewer whose
                // display name is not simply their login re-cased used to miss their own row
                // and get the not-tracked line. This was the FIRST command fixed that way;
                // userKey above now resolves the same identity for every other one. It is
                // still resolved separately here because the Ranks store is read with
                // RanksService.Normalize, which additionally strips a leading '@'.
                string watchLogin = RanksService.Normalize(
                    !string.IsNullOrWhiteSpace(msg.Login) ? msg.Login : msg.Username);
                long watchedMinutes = watchLogin.Length == 0
                    ? 0
                    : await DB.Instance.RankValueAsync(DB.WatchTimeSource, watchLogin).ConfigureAwait(false);

                // Three outcomes, three different truths — a single "no data" line would be
                // wrong for two of them.
                //
                // ★ The gate is no longer the Ranks tool. Watch time is recorded by the
                // always-on background viewer sampler, with every pre-build switched off, so
                // "you have no hours" can only mean tracking itself is disabled or this
                // viewer has not been seen yet. The old copy told people to enable Ranks,
                // which would now be advice that changes nothing.
                bool countingConfigured = ConfigManager.Current?.WatchTimeTrackingEnabled ?? true;
                SendLoyaltyReply(msg,
                    watchedMinutes > 0
                        ? $"{userDisplay}, you've watched {watchedMinutes / 60} h {watchedMinutes % 60} min."
                        : countingConfigured
                            ? $"{userDisplay}, no watch time is on record for you yet."
                            : $"{userDisplay}, watch-time tracking is switched off, so no hours are being recorded. (Pre-Builds ▸ User Management ▸ Watch Time turns it back on.)");
                return true;
            }
            if (C.AddPoints.Enabled && Match(C.AddPoints.Trigger))
            {
                if (!GateCommand(C.AddPoints)) return true;
                if (tok.Length < 2) { SendLoyaltyReply(msg, $"Usage: !{Verb(C.AddPoints.Trigger)} <user|all> <amount>"); return true; }
                long amount = ParseLongToken(tok[1]);
                if (string.Equals(tok[0], "all", StringComparison.OrdinalIgnoreCase))
                {
                    int applied = await svc.AwardActiveViewersAsync(amount, userKey).ConfigureAwait(false);
                    SendLoyaltyReply(msg, $"Gave {amount} {currency} to {applied} active viewer(s).");
                }
                else
                {
                    // The named viewer is the WRITE target — resolve it to the login or
                    // the grant lands on a row the viewer's own !points never reads.
                    var res = await svc.AddAsync(ResolveLoyaltyTarget(tok[0]), amount, userKey).ConfigureAwait(false);
                    if (res.Ok) SendLoyaltyReply(msg, $"Gave {amount} {currency} to {tok[0].Trim()}. Balance: {res.NewBalance}.");
                }
                return true;
            }
            if (C.SetPoints.Enabled && Match(C.SetPoints.Trigger))
            {
                if (!GateCommand(C.SetPoints)) return true;
                if (tok.Length < 2) { SendLoyaltyReply(msg, $"Usage: !{Verb(C.SetPoints.Trigger)} <user> <value>"); return true; }
                long value = ParseLongToken(tok[1]);
                // Resolved to the login: a correction aimed at a phantom row leaves the
                // real balance standing, which is the opposite of what a mod just asked for.
                var res = await svc.SetAsync(ResolveLoyaltyTarget(tok[0]), value, userKey).ConfigureAwait(false);
                if (res.Ok) SendLoyaltyReply(msg, $"Set {tok[0].Trim()} to {value} {currency}.");
                return true;
            }
            if (C.RemovePoints.Enabled && Match(C.RemovePoints.Trigger))
            {
                if (!GateCommand(C.RemovePoints)) return true;
                if (tok.Length < 2) { SendLoyaltyReply(msg, $"Usage: !{Verb(C.RemovePoints.Trigger)} <user> <amount>"); return true; }
                long amount = ParseLongToken(tok[1]);
                // Same as !setpoints — an unresolved name makes the deduction a silent no-op.
                var res = await svc.RemoveAsync(ResolveLoyaltyTarget(tok[0]), amount, userKey).ConfigureAwait(false);
                if (res.Ok) SendLoyaltyReply(msg, $"Removed {amount} {currency} from {tok[0].Trim()}. Balance: {res.NewBalance}.");
                return true;
            }
            if (C.Wipe.Enabled && Match(C.Wipe.Trigger))
            {
                if (!GateCommand(C.Wipe)) return true;
                int rows = await svc.WipeAsync(userKey).ConfigureAwait(false);
                SendLoyaltyReply(msg, $"Wiped all {currency} balances ({rows} row(s)).");
                return true;
            }
            if (C.Redeem.Enabled && Match(C.Redeem.Trigger))
            {
                if (!GateCommand(C.Redeem)) return true;
                if (rest.Length == 0) { SendLoyaltyReply(msg, $"Usage: !{Verb(C.Redeem.Trigger)} <reward>"); return true; }
                var res = await svc.RedeemAsync(userKey, rest).ConfigureAwait(false);
                SendLoyaltyReply(msg, res.Outcome switch
                {
                    LoyaltyRedeemOutcome.Ok         => res.Message,
                    LoyaltyRedeemOutcome.NotFound   => $"{userDisplay}, there's no reward called \"{rest}\".",
                    LoyaltyRedeemOutcome.Disabled   => $"{userDisplay}, that reward is disabled.",
                    LoyaltyRedeemOutcome.SoldOut    => $"{userDisplay}, that reward is sold out.",
                    LoyaltyRedeemOutcome.OnCooldown => $"{userDisplay}, that reward is on cooldown.",
                    LoyaltyRedeemOutcome.NoFunds    => $"{userDisplay}, you can't afford that reward.",
                    _ => string.Empty,
                });
                return true;
            }

            // ── Games — house games (self-gating cooldowns in the service) ────
            if (G.Gamble.Enabled && Match(G.Gamble.Command))
            {
                if (!RoleOk(G.Gamble.WhoCanPlay)) return true;
                var stake = ParseLoyaltyStake(tok.Length > 0 ? tok[0] : string.Empty,
                    G.Gamble.AllowAll, G.Gamble.AllowPercent);
                var res = await svc.GambleAsync(userKey, stake).ConfigureAwait(false);
                SendLoyaltyReply(msg, res.Ok
                    ? T(res.Won ? G.Gamble.WinMessage : G.Gamble.LoseMessage,
                        ("user", userDisplay), ("currency", currency),
                        ("won", res.Net.ToString(CultureInfo.InvariantCulture)),
                        ("bet", res.Stake.ToString(CultureInfo.InvariantCulture)),
                        ("balance", res.NewBalance.ToString(CultureInfo.InvariantCulture)))
                    : BetFailReply(res.Outcome, userDisplay, currency, G.Gamble));
                return true;
            }
            if (G.Slots.Enabled && Match(G.Slots.Command))
            {
                if (!RoleOk(G.Slots.WhoCanPlay)) return true;
                var stake = ParseLoyaltyStake(tok.Length > 0 ? tok[0] : string.Empty);
                var (res, reels) = await svc.SlotsAsync(userKey, stake).ConfigureAwait(false);
                SendLoyaltyReply(msg, res.Ok
                    ? T(res.Won ? G.Slots.WinMessage : G.Slots.LoseMessage,
                        ("user", userDisplay), ("currency", currency), ("reels", reels),
                        ("won", res.Net.ToString(CultureInfo.InvariantCulture)),
                        ("bet", res.Stake.ToString(CultureInfo.InvariantCulture)),
                        ("balance", res.NewBalance.ToString(CultureInfo.InvariantCulture)))
                    : BetFailReply(res.Outcome, userDisplay, currency, G.Slots));
                return true;
            }
            if (G.Roulette.Enabled && Match(G.Roulette.Command))
            {
                if (!RoleOk(G.Roulette.WhoCanPlay)) return true;
                // Accept "<stake> <bet>" (documented) and "<bet> <stake>" when the
                // bet is a color/parity word (numeric bets stay stake-first).
                string a0 = tok.Length > 0 ? tok[0] : string.Empty;
                string a1 = tok.Length > 1 ? tok[1] : string.Empty;
                LoyaltyStake stake; string betSpec;
                if (IsRouletteColorWord(a0)) { betSpec = a0; stake = ParseLoyaltyStake(a1); }
                else { stake = ParseLoyaltyStake(a0); betSpec = a1; }
                var (res, number, betType) = await svc.RouletteAsync(userKey, stake, betSpec).ConfigureAwait(false);
                SendLoyaltyReply(msg, res.Ok
                    ? T(res.Won ? G.Roulette.WinMessage : G.Roulette.LoseMessage,
                        ("user", userDisplay), ("currency", currency),
                        ("betType", betType), ("result", number.ToString(CultureInfo.InvariantCulture)),
                        ("won", res.Net.ToString(CultureInfo.InvariantCulture)),
                        ("bet", res.Stake.ToString(CultureInfo.InvariantCulture)),
                        ("balance", res.NewBalance.ToString(CultureInfo.InvariantCulture)))
                    : BetFailReply(res.Outcome, userDisplay, currency, G.Roulette));
                return true;
            }

            // ── Duel (challenge / accept / decline) ──────────────────────────
            if (G.Duel.Enabled && Match(G.Duel.Command))
            {
                if (!RoleOk(G.Duel.WhoCanPlay)) return true;
                if (tok.Length < 2) { SendLoyaltyReply(msg, $"Usage: !{Verb(G.Duel.Command)} <user> <wager>"); return true; }
                // The target is RAW chat text and the only free-text value that
                // reaches a message template — strip braces so it can never read as
                // a token spelling downstream (T() is single-pass, this keeps the
                // printed name clean as well). An emptied target falls through to
                // the service's InvalidTarget reply.
                string target = tok[0].Trim().Replace("{", string.Empty).Replace("}", string.Empty);
                long wager = ParseLongToken(tok[1]);
                // The duel is parked under the TARGET key and answered by !accept, which
                // looks the accepter up by LOGIN — so the challenge has to be filed under
                // the login too or the two ends never meet and the duel expires with no
                // reply. Resolving here also restores the self-challenge guard: typing
                // your own display name (or "@yourlogin") now collapses onto your own
                // login and is refused instead of opening an unacceptable duel.
                var ds = await svc.ChallengeAsync(userKey, ResolveLoyaltyTarget(target), wager).ConfigureAwait(false);
                SendLoyaltyReply(msg, ds.Ok
                    ? T(G.Duel.ChallengeMessage,
                        ("challenger", userDisplay), ("target", target), ("currency", currency),
                        ("bet", wager.ToString(CultureInfo.InvariantCulture)),
                        // The {accept} token has to render the CONFIGURED word: the
                        // literal was correct only for as long as "accept" was
                        // hard-coded in the parser below, and a streamer who moves the
                        // verb out of the way of some other channel command would
                        // otherwise have the challenge line tell every viewer to type
                        // a word that no longer answers.
                        ("accept", Say(G.Duel.AcceptCommand)),
                        ("timeout", ds.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)))
                    : DuelStartFailReply(ds.Outcome, userDisplay, currency, G.Duel));
                return true;
            }
            // accept / decline are NOT role-gated (the challenged target is answering a
            // challenge they already received) and fall through when there's no pending
            // duel — so they never hijack an unrelated !accept / !decline.
            //
            // Both words come from config now (LoyaltyDuelConfig.Accept/DeclineCommand);
            // they were literals here, which made them the two duel words a streamer
            // could neither see in the panel nor move — and "accept" / "decline" are
            // words a channel very plausibly already uses for something else. Match()
            // canonicalizes both sides, so a configured "!accept" works too, and a
            // blank field disables the word rather than matching every bang.
            if (G.Duel.Enabled && Match(G.Duel.AcceptCommand))
            {
                var dr = await svc.AcceptAsync(userKey).ConfigureAwait(false);
                if (dr.Outcome == DuelResultOutcome.NoPending)
                {
                    // Fallback to the DISPLAY key. A challenge opened before this viewer
                    // had said anything at all could not be resolved to a login at
                    // challenge time (the map is filled from chat), so it is parked under
                    // the typed display name. Retrying under that key is what keeps the
                    // duel answerable instead of expiring in silence; NoPending is
                    // side-effect-free, so the first attempt cost nothing.
                    //
                    // ★ TWITCH-ONLY, and that is a security line, not tidiness (see
                    // LoyaltyDisplayNameIsAccountBound): on YouTube/Kick the display
                    // name is free-form, so a viewer renamed to a victim's login could
                    // settle — accept and deliberately lose — the victim's duel.
                    if (!LoyaltyDisplayNameIsAccountBound(msg.Platform)) return false;
                    string displayKey = NormalizeLoyaltyName(msg.Username);
                    if (displayKey.Length == 0 || string.Equals(displayKey, userKey, StringComparison.Ordinal))
                        return false;
                    dr = await svc.AcceptAsync(displayKey).ConfigureAwait(false);
                    if (dr.Outcome == DuelResultOutcome.NoPending) return false;
                }
                SendLoyaltyReply(msg, dr.Outcome switch
                {
                    DuelResultOutcome.Ok      => T(G.Duel.WinMessage,
                        ("winner", dr.Winner), ("loser", dr.Loser), ("currency", currency),
                        ("won", dr.Wager.ToString(CultureInfo.InvariantCulture))),
                    DuelResultOutcome.NoFunds => $"Duel off — {dr.BrokeUser} can't cover the wager.",
                    _ => string.Empty,
                });
                return true;
            }
            if (G.Duel.Enabled && Match(G.Duel.DeclineCommand))
            {
                bool had = await svc.DeclineAsync(userKey).ConfigureAwait(false);
                if (!had)
                {
                    // Same display-key fallback as !accept — a duel you can't accept must
                    // not be a duel you can't decline either. DeclineAsync is a no-op when
                    // there is nothing pending.
                    //
                    // ★ And the same Twitch-only security gate: a free-form YouTube/Kick
                    // display name must not let a stranger decline someone else's duel
                    // (see LoyaltyDisplayNameIsAccountBound).
                    if (!LoyaltyDisplayNameIsAccountBound(msg.Platform)) return false;
                    string displayKey = NormalizeLoyaltyName(msg.Username);
                    if (displayKey.Length == 0 || string.Equals(displayKey, userKey, StringComparison.Ordinal))
                        return false;
                    had = await svc.DeclineAsync(displayKey).ConfigureAwait(false);
                }
                if (!had) return false;
                SendLoyaltyReply(msg, $"{userDisplay} declined the duel.");
                return true;
            }

            // ── Raffle (start / draw / cancel / join) ────────────────────────
            if (G.Raffle.Enabled && Match(G.Raffle.Command))
            {
                // Sub-verb: draw (WhoCanStart). Configured word, matched through the
                // one shared canonicalizer like every other verb in this tool — a
                // sub-verb carries no '!' of its own, but a streamer who types one
                // into the field ("!draw") must not end up with a dead sub-command.
                if (tok.Length > 0 && ChatVerb.Matches(G.Raffle.DrawSubCommand, tok[0]))
                {
                    if (!RoleOk(G.Raffle.WhoCanStart)) return true;
                    var outcome = await svc.RaffleDrawAsync().ConfigureAwait(false);
                    SendLoyaltyReply(msg, outcome.Outcome switch
                    {
                        RaffleDrawOutcome.Ok => T(G.Raffle.WinMessage,
                            ("winners", string.Join(", ", outcome.Winners.Select(w => w.Name))),
                            ("each", outcome.Winners.Count > 0 ? outcome.Winners[0].Amount.ToString(CultureInfo.InvariantCulture) : "0"),
                            ("currency", currency)),
                        RaffleDrawOutcome.NoEntries => "Raffle closed — no entries.",
                        RaffleDrawOutcome.Cancelled => "Raffle cancelled — entries refunded.",
                        _ => string.Empty,
                    });
                    return true;
                }

                // Sub-verb: cancel (WhoCanStart) — close a running raffle WITHOUT
                // drawing. Same role gate as start/draw: whoever may open one may
                // call it off.
                //
                // Every entrant whose fee LANDED is refunded in full by
                // CancelRaffleAsync. A cancel that kept viewers' stakes would be a
                // points sink, and the money layer exists precisely to make that
                // impossible — so the reply reports the refund rather than just
                // announcing the close.
                if (tok.Length > 0 && ChatVerb.Matches(G.Raffle.CancelSubCommand, tok[0]))
                {
                    if (!RoleOk(G.Raffle.WhoCanStart)) return true;
                    var cancelled = await svc.CancelRaffleAsync(userKey).ConfigureAwait(false);
                    SendLoyaltyReply(msg, cancelled.Outcome switch
                    {
                        RaffleDrawOutcome.Cancelled => cancelled.TotalPot > 0
                            ? $"Raffle cancelled — {cancelled.EntrantCount} entrant(s) refunded {cancelled.TotalPot} {currency}."
                            : "Raffle cancelled.",
                        RaffleDrawOutcome.NoRaffle => "No raffle is running.",
                        _ => string.Empty,
                    });
                    return true;
                }
                // Start (WhoCanStart). Optional [winners] [duration] [fee]; 0/blank = config default.
                if (!RoleOk(G.Raffle.WhoCanStart)) return true;
                int winners  = tok.Length > 0 && int.TryParse(tok[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wv) && wv > 0 ? wv : 0;
                int duration = tok.Length > 1 && int.TryParse(tok[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv) && dv > 0 ? dv : 0;
                long fee     = tok.Length > 2 && long.TryParse(tok[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fv) && fv >= 0 ? fv : G.Raffle.EntryFee;
                bool ok = await svc.RaffleStartAsync(winners, duration, fee, userKey).ConfigureAwait(false);
                if (ok)
                {
                    int shownWinners = winners > 0 ? winners : Math.Max(1, G.Raffle.DefaultWinners);
                    int shownDur     = duration > 0 ? duration : Math.Max(1, G.Raffle.DefaultDurationSeconds);
                    // {join} advertises the word viewers must type, so it goes through
                    // the same canonicalizer the JoinCommand match does — otherwise a
                    // configured "!join" opened the raffle with "Type !!join to enter".
                    SendLoyaltyReply(msg, T(G.Raffle.OpenMessage,
                        ("join", Say(G.Raffle.JoinCommand)), ("currency", currency),
                        ("fee", fee.ToString(CultureInfo.InvariantCulture)),
                        ("winners", shownWinners.ToString(CultureInfo.InvariantCulture)),
                        ("duration", shownDur.ToString(CultureInfo.InvariantCulture))));
                }
                return true;
            }
            if (G.Raffle.Enabled && Match(G.Raffle.JoinCommand))
            {
                // ★ LIVENESS BEFORE ROLE, and the order is load-bearing. The `return false` here is
                // what lets a later provider — the viewer queue shares this verb by default — answer
                // !join when no raffle is running. Applying the role gate first defeats it: a
                // subscriber-only raffle would consume every non-subscriber's !join even with no
                // raffle open, so those viewers could never join the queue, with no reply and no log
                // line to explain it. Refusing entry to a raffle that does not exist is also simply
                // wrong on Loyalty's own terms. Once a raffle IS live the gate applies as before.
                if (!svc.RaffleLive) return false;                            // don't hijack !join
                if (!RoleOk(G.Raffle.WhoCanPlay)) return true;
                var rj = await svc.RaffleJoinAsync(userKey).ConfigureAwait(false);
                if (rj.Outcome == RaffleJoinOutcome.NoRaffle) return false;   // lost the race to the draw
                SendLoyaltyReply(msg, rj.Outcome switch
                {
                    RaffleJoinOutcome.Ok            => $"{userDisplay} joined the raffle! ({rj.EntrantCount} entrant(s))",
                    RaffleJoinOutcome.AlreadyJoined => $"{userDisplay}, you're already in the raffle.",
                    RaffleJoinOutcome.NoFunds       => $"{userDisplay}, you can't afford the {rj.Fee} {currency} entry.",
                    _ => string.Empty,
                });
                return true;
            }

            return false;   // not a Loyalty command — author scripts handle it normally
        }

        // Per-user command cooldown check + stamp. True = clear to run (and stamped).
        private bool LoyaltyCooldownOk(string trigger, string user, int cooldownSeconds)
        {
            if (cooldownSeconds <= 0) return true;
            string key = trigger + "\0" + user;
            long now = _loyaltyMono.ElapsedMilliseconds;
            lock (_loyaltyCdGate)
            {
                if (_loyaltyUserCdMs.TryGetValue(key, out long end) && now < end) return false;
                _loyaltyUserCdMs[key] = now + cooldownSeconds * 1000L;
                return true;
            }
        }

        // Route the reply on the platform the command arrived on, reusing the exact
        // per-platform chat-send cores chat.send / *.send_chat use — no new send path.
        private void SendLoyaltyReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "loyalty"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "loyalty"); break;
                default:                    SendTwitchChatCore(reply, "loyalty"); break;
            }
        }

        // "all" → AllIn; "NN%" → Pct; integer → Abs. The service/DB resolves the
        // percent/all against the live balance atomically — we only pass the shape.
        //
        // allowAll / allowPercent are the Gamble page's two money-shaping switches. They
        // are enforced HERE, at the parse, rather than in the settlement: a refused shape
        // must not silently become some other bet, it must not be a bet at all. Abs(0)
        // is what the service answers Invalid to ("that's not a valid bet"), which is the
        // same reply an unparseable stake already gets. Both default to true because only
        // Gamble carries the switches — slots and roulette have no such setting and keep
        // accepting both shapes.
        private static LoyaltyStake ParseLoyaltyStake(string raw, bool allowAll = true, bool allowPercent = true)
        {
            string s = (raw ?? string.Empty).Trim().Trim('"').Trim().ToLowerInvariant();
            if (s.Length == 0) return LoyaltyStake.Abs(0);
            if (s is "all" or "allin" or "all-in" or "max") return allowAll ? LoyaltyStake.AllIn : LoyaltyStake.Abs(0);
            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                if (!allowPercent) return LoyaltyStake.Abs(0);
                string num = s.Substring(0, s.Length - 1).Trim();
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct > 0)
                    return LoyaltyStake.Pct(Math.Clamp(pct / 100.0, 0.0, 1.0));
                return LoyaltyStake.Abs(0);
            }
            return LoyaltyStake.Abs(ParseLongToken(s));
        }

        private static bool IsRouletteColorWord(string s)
        {
            string t = (s ?? string.Empty).Trim().ToLowerInvariant();
            return t is "red" or "black" or "even" or "odd" or "low" or "high" or "1-18" or "19-36";
        }

        // Terse reply for a rejected house-game bet. Disabled / OnCooldown → silent.
        private static string BetFailReply(LoyaltyOutcome o, string user, string currency, LoyaltyGameBase g)
            => o switch
            {
                LoyaltyOutcome.NoFunds  => $"{user}, you don't have enough {currency}.",
                LoyaltyOutcome.BelowMin => $"{user}, the minimum bet is {g.MinBet} {currency}.",
                LoyaltyOutcome.AboveMax => $"{user}, the maximum bet is {g.MaxBet} {currency}.",
                LoyaltyOutcome.Offline  => $"{user}, the stream must be live to play.",
                LoyaltyOutcome.Invalid  => $"{user}, that's not a valid bet.",
                _ => string.Empty,
            };

        private static string DuelStartFailReply(DuelStartOutcome o, string user, string currency, LoyaltyDuelConfig d)
            => o switch
            {
                DuelStartOutcome.SelfChallenge  => $"{user}, you can't duel yourself.",
                DuelStartOutcome.InvalidTarget  => $"{user}, name someone to duel.",
                DuelStartOutcome.ChallengerBusy => $"{user}, you already have a pending duel.",
                DuelStartOutcome.TargetBusy     => $"{user}, that person already has a pending duel.",
                DuelStartOutcome.BelowMin       => $"{user}, the minimum wager is {d.MinBet} {currency}.",
                DuelStartOutcome.AboveMax       => $"{user}, the maximum wager is {d.MaxBet} {currency}.",
                DuelStartOutcome.Offline        => $"{user}, the stream must be live to duel.",
                _ => string.Empty,
            };

        // Message-template substitution — {token} → value (missing tokens untouched).
        // ONE pass, by construction: a sequential Replace chain re-scans text an
        // earlier token already substituted, so a value carrying a later token's
        // spelling gets expanded too — chatter-supplied values reach this (the duel
        // target), which made "!duel {bet} 50" print the wager where the name goes.
        // A single Regex pass with an evaluator can never re-scan its own output.
        // Same shape as CustomCommandsService.RenderAsync.
        private static readonly System.Text.RegularExpressions.Regex TemplateTokenRx =
            new(@"\{([^{}]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string T(string template, params (string Key, string Val)[] tokens)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;
            var map = new Dictionary<string, string>(tokens.Length, StringComparer.Ordinal);
            foreach (var (k, v) in tokens) map[k] = v ?? string.Empty;
            // Unknown token → the match verbatim, exactly what the old chain left behind.
            return TemplateTokenRx.Replace(template, m =>
                map.TryGetValue(m.Groups[1].Value, out var val) ? val : m.Value);
        }
    }
#pragma warning restore CS1998
}
