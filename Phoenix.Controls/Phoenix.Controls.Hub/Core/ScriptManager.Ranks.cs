using System;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Ranks (watch-time / points rank ladder) — the Hub-side wiring of the
    // eleventh pre-build tool. Three responsibilities, mirroring ScriptManager.Counters.cs:
    //
    //   1. Seam injection — RanksService is Hub-side but cannot reach the script
    //      dispatcher, the chat send path, the Loyalty config or the User-Management
    //      group store, so all four seams are wired at the top of RegisterRanksCommands.
    //      The ladder's watch-minute feed is subscribed here too, and it is now a
    //      SUBSCRIPTION rather than an injected accrual pair: minutes are recorded by the
    //      always-on ViewerPresenceService, so the tool no longer supplies the accrual —
    //      it listens for the credit and evaluates the viewers it names.
    //
    //   2. The two surviving rank.* script commands backing the Architect Rank.* nodes
    //      — one inline value read (get) and one flow command (evaluate) — the locked
    //      three-way manifest contract. (rank.value / rank.top were RETIRED in the
    //      2026-08 tool-node cut: both read OPEN tables, so db.get_cell / db.top cover
    //      them; the old names answer through ScriptManager.RetiredCommands shims.)
    //
    //   3. The BUILT-IN chat-command bridge. The parse, the role gate, the cooldown and
    //      the reply TEXT all live in RanksService (which keeps them testable in-memory,
    //      the UserManagementService.TryHandleQueueChatAsync precedent); this file only
    //      routes the answer onto the platform the command arrived on.
    //
    // ★ ARCHITECT-FIRST PARITY. Every capability this tool exposes stays reachable from
    // a graph: the chat verbs map to Rank.Get + the generic db.* reads (db.top for the
    // ladder), the rank-up trigger point maps to Rank.OnRankUp, and Rank.Evaluate
    // exposes the one verb the tool performs for itself — re-checking a viewer.
    // Rank.Evaluate is also the answer to the one gap the service cannot close on its
    // own: the Loyalty balance table is OPEN, so a graph that moves points with
    // db.set_cell bypasses every service-level hook, and calling Rank.Evaluate on the
    // next line is how that graph gets its rank-up.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterRanksCommands()
        {
            var svc = RanksService.Instance;

            // ── Seam: Rank.OnRankUp script events ────────────────────────────
            // Fired through the generic-event dispatcher with pre-built vars. The seam is
            // an Action (the service can't await it), so the async dispatch is
            // fire-and-forgotten HERE through AsyncErrorBoundary.SafeRunAsync — faults
            // route to the log, expected shutdown cancellation is swallowed. Verbatim
            // ScriptManager.Counters.cs.
            svc.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "RanksService", $"RaiseScriptEvent({phoenixEvent})");
            };

            // ── Seam: the unprompted rank-up announcement ────────────────────
            // A promotion detected off a watch-minute credit has no chat message to reply
            // to, so the line goes out through the same SendTwitchChatCore path chat.send uses
            // — the SchedulingService.ChatSend / PollsService.Announce precedent, no new
            // send path.
            svc.Announce = line => SendTwitchChatCore(line, "ranks");

            // ── Seams: the Loyalty facts the points metric needs ─────────────
            // Read through seams rather than reaching into LoyaltyService directly so the
            // ladder's replies and its metric source stay testable in-memory. The service
            // validates whatever the balance-table seam returns (it is streamer-typed) and
            // falls back to reading 0 rather than guessing a table name.
            svc.CurrencyProvider = () => LoyaltyService.Instance.Config.Currency.NamePlural;
            svc.BalanceTableProvider = () => LoyaltyService.Instance.Config.Currency.BalanceTable;

            // ── Subscription: watch-minute credits → ladder evaluation ───────
            // The ladder used to OWN the accrual and hang it off Loyalty's payout tick,
            // behind its own master toggle — so a default install (this tool ships OFF)
            // recorded no hours at all, and the minutes stopped whenever the points economy
            // did. ViewerPresenceService records them now, always, and raises the logins it
            // credited; all the tool does is re-resolve those viewers' rungs. Nothing is
            // injected in this direction any more: the recorder does not need to know a
            // ladder exists.
            //
            // Remove-then-add makes the wiring idempotent — see OnWatchTimeCredited for why
            // that matters and why the handler is static.
            ViewerPresenceService.Instance.WatchTimeCredited -= OnWatchTimeCredited;
            ViewerPresenceService.Instance.WatchTimeCredited += OnWatchTimeCredited;

            // ── rank.evaluate (Architect Rank.Evaluate) ──────────────────────
            // Void → return null; flow continues through the node's Done output. The only
            // rank.* command with side effects, and deliberately the only one: the read
            // below is a data pin the exporter may inline into a condition it evaluates
            // more than once, so firing an event or writing a row from it would make the
            // same graph behave differently depending on how many consumers a wire
            // happened to have. (It said "the three reads" while rank.value and rank.top
            // still existed; both were retired in the 2026-08 tool-node cut.)
            _engine.RegisterCommand("rank.evaluate", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string user = ResolveRankUser(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                await RanksService.Instance.EvaluateAsync(user).ConfigureAwait(false);
                return null;
            });

            // ── rank.get inline value read (Rank.Get) ────────────────────────
            // Pure-data: the exporter emits the call inline (ComputeInlineValue) and the
            // engine round-trips the return string into the node's value output.
            // CurrentBoundArgs can be null on an inline read, which is why it falls back
            // to the positional arg. Rank.Get survives the 2026-08 tool-node cut because
            // the value→rank-name ladder lives in the Ranks CONFIG, which db.* cannot
            // reach; rank.value / rank.top were retired — both read OPEN tables, so
            // db.get_cell / db.top cover them — and answer through
            // ScriptManager.RetiredCommands shims.
            _engine.RegisterCommand("rank.get", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string user = ResolveRankUser(bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0));
                return await RanksService.Instance.RankNameAsync(user).ConfigureAwait(false);
            });
        }

        // The ladder's link to the always-on watch-minute recorder. STATIC, and wired with
        // a remove-then-add, for one reason each:
        //
        //   * STATIC — the delegate is stored on ViewerPresenceService.Instance, which
        //     lives for the process. An instance handler would pin the ScriptManager that
        //     registered it for just as long, and in the test host that means every
        //     ScriptManager ever constructed stays reachable. It needs nothing from the
        //     instance: RanksService.Instance is a singleton too, so this is one singleton
        //     bridged to another and there is nothing to capture.
        //
        //   * REMOVE-THEN-ADD — RegisterRanksCommands runs once per ScriptManager, and a
        //     second ScriptManager (a reload, a test class) would otherwise stack a second
        //     handler on the same singleton event and evaluate every credited viewer twice
        //     — announcing nothing extra, but doubling the databank batch and racing
        //     itself. `-=` against a static method group removes the previously-added
        //     identical delegate, so the pair is exactly idempotent and needs no
        //     bookkeeping flag. There is no unsubscribe on the way out because there is no
        //     hook to hang one on: ScriptManager.BeginStop cancels scripts and disposes
        //     timers but unsubscribes nothing (ScriptRegistry.ScriptContentChanged is wired
        //     the same way), and a static bridge between two process-lifetime singletons
        //     holds nothing that could leak.
        private static void OnWatchTimeCredited(object? sender, WatchTimeCreditedEventArgs e)
        {
            if (e?.Logins is null || e.Logins.Count == 0) return;
            // Fire-and-forget through the shared boundary, exactly like the RaiseScriptEvent
            // hop above: this is raised from inside the presence sampling loop, and letting
            // a ladder evaluation hold that loop would delay the next sample for every
            // consumer of it — the payout sweep, the role cache and the accrual itself.
            _ = AsyncErrorBoundary.SafeRunAsync(
                () => RanksService.Instance.OnWatchTimeCreditedAsync(e.Logins),
                "RanksService", "OnWatchTimeCredited");
        }

        // Empty User → the triggering chatter, resolved through the IMMUTABLE trigger
        // context: {event.user_login} is the platform LOGIN BuildChatVars parks there, where
        // {user.name} is the operator-styled DISPLAY name. Mirrors ResolvePollUser /
        // ResolveSongUser, and the distinction is load-bearing for exactly the same reason:
        // every store this tool reads and writes — the open "WatchTime" table, the open
        // "Ranks" table, the Loyalty balance table and the User-Management group members —
        // is keyed on the LOGIN. A viewer whose display name is not simply their login
        // re-cased ("neko_chan" shown as "NekoChan!") would otherwise be read under a key
        // nothing was ever written to, so the built-in !rank would answer correctly while an
        // authored graph's Rank.Get read empty for the same person on the same line.
        //
        // {user.name} stays as the LAST fallback rather than being dropped: a graph reached
        // from a non-chat trigger parks no event.user_login at all, and the display name is
        // then the only identity there is.
        private string ResolveRankUser(string? raw)
        {
            string u = StripBareQuotes(raw ?? string.Empty).Trim();
            if (u.Length == 0) u = StripBareQuotes(_engine.GetExecutionVar("event.user_login")).Trim();
            if (u.Length == 0) u = StripBareQuotes(_engine.GetExecutionVar("user.name")).Trim();
            return RanksService.Normalize(u);
        }

        // ── Built-in chat-command bridge ─────────────────────────────────────
        /// <summary>
        /// Offers one chat line to the Ranks tool and posts whatever it answers with.
        /// Returns true only when a Ranks command was recognized AND consumed (so the
        /// caller can suppress the author on_chat fan-out). A role-denied command is still
        /// consumed but silent — the same convention the Counters / Quotes / Polls parsers
        /// use, so a denied viewer's line never falls through to an authored on_chat script
        /// that would answer it anyway. DEFAULT-OFF IS A TOTAL NO-OP.
        /// </summary>
        public async Task<bool> TryHandleRanksChatCommandAsync(ChatMessage msg)
        {
            var result = await RanksService.Instance.TryHandleChatAsync(msg).ConfigureAwait(false);
            if (!result.Handled) return false;
            SendRanksReply(msg, result.Reply);
            return true;
        }

        // Route the reply on the platform the command arrived on, reusing the exact
        // per-platform chat-send cores chat.send / *.send_chat use — mirrors
        // SendCountersReply, no new send path.
        private void SendRanksReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "ranks"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "ranks"); break;
                default:                    SendTwitchChatCore(reply, "ranks"); break;
            }
        }
    }
#pragma warning restore CS1998
}
