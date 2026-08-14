using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Automod / spam-filter (the FIRST built-in chat provider, at
    // dispatch index 0 — moderation runs BEFORE any command is answered, so a
    // spammer's !gamble is timed out, not answered). Three responsibilities,
    // mirroring ScriptManager.Counters.cs / ScriptManager.Loyalty.cs:
    //
    //   1. Seam injection (RegisterAutomodSeams, called from RegisterHubCommands next
    //      to RegisterCountersCommands) — AutomodService can't touch the script
    //      dispatcher / bot list / Streamer.bot action inventory itself, so
    //      RaiseScriptEvent (Automod.OnViolation), BotAccountsProvider
    //      (AppConfig.BotUsername) and DeleteCapabilityProvider are wired here.
    //
    //   2. The interceptor entry (TryHandleAutomodAsync) — owns the permit command
    //      (AutomodService.PermitVerb, i.e. AutomodConfig.PermitCommand canonicalized;
    //      "!permit" out of the box) for the chatters PermitRoles authorizes and, for
    //      EVERY other message (an unauthorized "!permit …" included), scans + applies
    //      moderation through the EXISTING fire-and-forget DispatchNamedAction path
    //      (twitch.* / kick.* / youtube.*).
    //
    //   3. Effect application (ApplyModerationAsync) — Warn / Delete / Timeout / Ban per
    //      platform, Dry-run log-only support, the Automod.OnViolation event raise + the
    //      AutomodLog audit append. All three of those report the action that was ACTUALLY
    //      ISSUED, which a declined Delete makes differ from the one the scan resolved.
    //
    // DEFAULT-OFF IS A TOTAL NO-OP — the provider predicate (AutomodService.Active) is
    // false, so this never runs when the tool is disabled.
    public partial class ScriptManager
    {
        /// <summary>The interceptor's verdict, mapped to a BuiltInChatResult by the
        /// provider lambda: Clean → NotHandled; Moderated → HandledSuppress(config
        /// SuppressDispatchWhenModerated); Permit → HandledSuppress(true) (Automod owns
        /// the permit command — but only for a chatter PermitRoles authorizes; an
        /// unauthorized "!permit …" is scanned like any other line and never yields
        /// Permit).</summary>
        internal enum AutomodOutcome { Clean, Moderated, Permit }

        private void RegisterAutomodSeams()
        {
            // Automod.OnViolation flows back through the generic-event dispatcher with
            // pre-built vars (event.user / event.rule / event.action / event.reason /
            // event.message). The seam is an Action (the service can't await it), so the
            // async event-dispatch is fired-and-forgotten HERE through
            // AsyncErrorBoundary.SafeRunAsync — faults route to the log, expected
            // shutdown cancellation is swallowed (mirrors ScriptManager.Counters.cs).
            AutomodService.Instance.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "AutomodService", $"RaiseScriptEvent({phoenixEvent})");
            };

            // Live Hub bot-account list (AppConfig.BotUsername, comma-split/trim/lower)
            // — reuses the shared helper the Loyalty tool already exposes. Bots are
            // already dropped at ingest; this is defense-in-depth.
            AutomodService.Instance.BotAccountsProvider = GetLoyaltyBotAccounts;

            // Delete capability — the service resolves the ladder, Hub answers whether a
            // Delete rung can actually land for THIS message (its platform id + the
            // connected Streamer.bot's action inventory). Same seam pattern as the two
            // above; the shared resolver below is also what the dispatch path and the
            // dry-run preview consult, so the gate, the preview and the dispatch can never
            // disagree about what "can delete" means.
            AutomodService.Instance.DeleteCapabilityProvider = CanDeleteChatMessage;
        }

        // ── Interceptor entry (Automod provider) ─────────────────────────────
        /// <summary>Runs the automod filter for one message. Owns the configured permit
        /// command (<see cref="AutomodService.PermitVerb"/>) for the chatters PermitRoles
        /// authorizes (mod-only by default) and, for any other message — including a
        /// "!permit …" from someone who is not authorized, and every message at all once the
        /// verb is blanked — scans it and applies the resolved moderation. Returns the
        /// outcome the provider lambda maps to a BuiltInChatResult. DEFAULT-OFF is a total
        /// no-op.</summary>
        internal async Task<AutomodOutcome> TryHandleAutomodAsync(ChatMessage msg)
        {
            var svc = AutomodService.Instance;
            if (!svc.Active || msg is null) return AutomodOutcome.Clean;

            // 1) !<verb> <name> — Automod owns this command ONLY for a chatter PermitRoles
            //    authorizes. For anyone else "!permit …" is not a command at all, it is an
            //    ordinary chat line, so it falls through to the scan below.
            //
            //    The word is the streamer's (AutomodConfig.PermitCommand — this was the last
            //    hard-coded built-in verb in the suite, awkward on a channel whose other bot
            //    already answers to "!permit"). It is read through svc.PermitVerb, i.e.
            //    canonicalized ONCE at the source, because the same string is both compared
            //    against the parsed token and printed back in the usage reply — a configured
            //    "!permit" must not tell the mod to type "!!permit". Inside this branch the
            //    verb is therefore guaranteed non-empty, so the usage line always names a
            //    word that actually matches.
            //
            //    A verb that canonicalizes to EMPTY (blank or bang-only field) means the
            //    command is OFF, and "off" resolves in the one safe direction: ChatVerb.Matches
            //    refuses an empty configured side, so IsPermitCommand answers false for EVERY
            //    line and "!permit …" is scanned exactly like any other chat message. The
            //    cost is that nobody can grant a link pass until the field is refilled. The
            //    opposite reading — empty as a wildcard — would hand every chatter the
            //    Permit outcome and with it HandledSuppress(true), i.e. the filter bypass the
            //    ★ note below exists to prevent, for every message in chat.
            //
            //    ★ The authorization check MUST be part of this branch's condition, not a
            //    body-level if. Returning here for an unauthorized chatter made the whole
            //    filter opt-out: seven leading characters skipped Links / Blocklist / Caps /
            //    Symbols / Repeat / Length, banked no strike, and — because the rate/flood
            //    window is recorded inside EvaluateAsync's TryDetect — left an unlimited
            //    "!permit …" flood invisible to the rate rule too, while Permit's
            //    HandledSuppress(true) hid the line from every downstream provider.
            string permitVerb = svc.PermitVerb;
            if (IsPermitCommand(msg, permitVerb, out string target) && svc.IsPermitAuthorized(msg))
            {
                if (target.Length == 0)
                {
                    SendAutomodReply(msg, $"Usage: !{permitVerb} <name>");
                }
                else
                {
                    svc.GrantPermit(target.ToLowerInvariant());
                    SendAutomodReply(msg, $"{target} may post a link for {svc.Config.PermitSeconds}s.");
                }
                return AutomodOutcome.Permit;
            }

            // 2) Scan + moderate.
            AutomodDecision decision;
            try
            {
                decision = await svc.EvaluateAsync(msg).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ScriptManager", "Automod EvaluateAsync failed", ex);
                return AutomodOutcome.Clean;
            }

            if (!decision.Matched || decision.Action == AutomodAction.None)
                return AutomodOutcome.Clean;

            await ApplyModerationAsync(msg, decision).ConfigureAwait(false);
            // Dry-run is observe-only: ApplyModeration logged the would-be action but
            // dispatched nothing and raised no event, so let the message flow normally
            // (chat identical to automod-off) instead of suppressing downstream providers.
            return svc.Config.DryRun ? AutomodOutcome.Clean : AutomodOutcome.Moderated;
        }

        // Parse "!<permitVerb> <name>". The '!' stays hard-coded (like BuildChatVars /
        // Counters — the bang is the suite-wide command marker, only the VERB is the
        // streamer's), and the token comparison goes through ChatVerb.Matches like every
        // other built-in provider: it canonicalizes both sides, so a configured "!permit"
        // is live, and it never matches an empty configured side, so a blanked verb makes
        // this answer false for every line rather than true for all of them.
        // permitVerb arrives already canonical from the caller, which needs that same form
        // for the usage reply; Matches canonicalizing it again is idempotent.
        // Returns true when the message is the permit command (target may be empty).
        private static bool IsPermitCommand(ChatMessage msg, string permitVerb, out string target)
        {
            target = "";
            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return false;
            string body = text.Substring(1);
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body.Substring(0, sp)).Trim();
            if (!ChatVerb.Matches(permitVerb, token)) return false;
            string rest = sp < 0 ? string.Empty : body.Substring(sp + 1).Trim();
            // <name> is one word — take the first token, strip a leading '@'.
            if (rest.Length > 0)
            {
                int rsp = rest.IndexOf(' ');
                string first = rsp < 0 ? rest : rest.Substring(0, rsp);
                target = first.TrimStart('@').Trim();
            }
            return true;
        }

        // ── Moderation application ───────────────────────────────────────────
        private async Task ApplyModerationAsync(ChatMessage msg, AutomodDecision decision)
        {
            var svc = AutomodService.Instance;
            string user = msg.Username ?? "";
            string platform = msg.Platform ?? ChatPlatforms.Twitch;
            bool dryRun = svc.Config.DryRun;

            // What was — or, in dry-run, would be — ACTUALLY issued. It can differ from
            // decision.Action only for a Delete, the one verb the runtime is allowed to
            // decline: the scan-time capability gate cannot see a Streamer.bot that drops a
            // moment later, and it deliberately leaves a FIXED Delete alone. All three
            // downstream consumers below (the dry-run line, event.action, the audit row)
            // report THIS, never the intent, so none of them can claim a delete that did not
            // happen.
            (AutomodAction Action, int DurationSeconds) issued;

            if (dryRun)
            {
                issued = PreviewModeration(msg, platform, decision);
                GlobalLogger.Log(
                    $"Automod  would {DescribeAction(issued.Action, issued.DurationSeconds)} {user} — rule '{decision.Rule}' ({decision.Reason}).",
                    "AutomodService", LogLevel.LogicExecution);
            }
            else
            {
                try { issued = DispatchModeration(msg, user, platform, decision); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("AutomodService", "moderation dispatch failed", ex);
                    // A throw leaves the outcome unknowable; naming the intended action here
                    // would be exactly the claim this path exists to stop making.
                    issued = (AutomodAction.None, 0);
                }
            }

            // Automod.OnViolation script event (event.user / event.rule / event.action /
            // event.reason / event.message) via the seam — fire-and-forget. ONLY on REAL
            // enforcement: in dry-run (observe-only) firing it would let an author graph
            // take a real action off a would-be flag. event.action carries the ISSUED
            // action, so a graph branching on == "Delete" fires only when a delete was
            // actually sent, and reads "None" when the violation produced no platform action.
            if (!dryRun)
            {
                try
                {
                    var raise = svc.RaiseScriptEvent;
                    if (raise != null)
                    {
                        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["event.user"] = user,
                            ["event.rule"] = decision.Rule,
                            ["event.action"] = issued.Action.ToString(),
                            ["event.reason"] = decision.Reason,
                            ["event.message"] = msg.Message ?? "",
                        };
                        raise("Automod.OnViolation", vars);
                    }
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("AutomodService", "RaiseScriptEvent(Automod.OnViolation) failed", ex);
                }
            }

            // Append the audit row (raises Activity for the panel). Action is the ISSUED one:
            // a row reading "Delete" is a promise the panel makes to the streamer that a
            // message came down, so a refused delete has to show as what it was instead.
            await svc.AppendLogAsync(new AutomodLogEntry
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Name = user,
                Platform = platform,
                Rule = decision.Rule,
                Action = dryRun ? issued.Action + " (dry-run)" : issued.Action.ToString(),
                Detail = decision.Reason,
            }).ConfigureAwait(false);
        }

        // Route the resolved action to the EXISTING per-platform DispatchNamedAction
        // wrappers and report what was ISSUED. All fire-and-forget — a disconnected SB logs
        // LOUDLY (CriticalError) inside DispatchNamedAction and the action silently fails;
        // it never throws.
        private (AutomodAction Action, int DurationSeconds) DispatchModeration(
            ChatMessage msg, string user, string platform, AutomodDecision decision)
        {
            switch (decision.Action)
            {
                case AutomodAction.Warn:
                    SendAutomodReply(msg, $"@{user} — {WarnText(decision)}");
                    return AutomodIssued(AutomodAction.Warn, 0);

                case AutomodAction.Delete:
                    // Dispatched via the per-platform SB delete wrapper. A LADDER Delete
                    // already passed the capability gate in AutomodService.EvaluateAsync,
                    // so this normally sends; DispatchDelete re-checks anyway because a
                    // FIXED Delete rule reaches here ungated, and because Streamer.bot can
                    // drop between the two. A refusal issues the decision's fallback — for a
                    // ladder rung that is the rung ResolveLadderWithoutDelete chose, so the
                    // race costs the viewer nothing; a fixed rule carries None and nothing
                    // is issued.
                    if (DispatchDelete(msg, platform)) return AutomodIssued(AutomodAction.Delete, 0);
                    return DispatchModerationFallback(msg, user, platform, decision);

                case AutomodAction.Timeout:
                    return DispatchTimeout(user, platform, decision.DurationSeconds, decision.Reason)
                        ? AutomodIssued(AutomodAction.Timeout, decision.DurationSeconds)
                        : AutomodIssued(AutomodAction.None, 0);

                case AutomodAction.Ban:
                    return DispatchBan(user, platform, decision.Reason)
                        ? AutomodIssued(AutomodAction.Ban, 0)
                        : AutomodIssued(AutomodAction.None, 0);
            }
            return AutomodIssued(AutomodAction.None, 0);
        }

        // Issue the substitute the decision named for a declined Delete, and report it.
        // FallbackAction is None for a FIXED rule — that rule names one action and there is
        // no second choice, so nothing goes out and "None" is what the audit row, the event
        // and the preview all say.
        private (AutomodAction Action, int DurationSeconds) DispatchModerationFallback(
            ChatMessage msg, string user, string platform, AutomodDecision decision)
        {
            var (action, seconds) = AutomodFallbackOf(decision);
            switch (action)
            {
                case AutomodAction.Warn:
                    SendAutomodReply(msg, $"@{user} — {WarnText(decision)}");
                    return AutomodIssued(AutomodAction.Warn, 0);

                case AutomodAction.Timeout:
                    return DispatchTimeout(user, platform, seconds, decision.Reason)
                        ? AutomodIssued(AutomodAction.Timeout, seconds)
                        : AutomodIssued(AutomodAction.None, 0);

                case AutomodAction.Ban:
                    return DispatchBan(user, platform, decision.Reason)
                        ? AutomodIssued(AutomodAction.Ban, 0)
                        : AutomodIssued(AutomodAction.None, 0);

                default:
                    return AutomodIssued(AutomodAction.None, 0);
            }
        }

        /// <summary>The action a DRY-RUN would issue. Everything resolves as configured except
        /// a Delete, which is asked the same capability question the live dispatcher asks — a
        /// preview promising a delete the runtime would decline is precisely the
        /// miscalibration a preview exists to prevent.</summary>
        private (AutomodAction Action, int DurationSeconds) PreviewModeration(
            ChatMessage msg, string platform, AutomodDecision decision)
        {
            if (decision.Action != AutomodAction.Delete)
                return AutomodIssued(decision.Action, decision.DurationSeconds);
            if (ResolveAutomodDeleteAction(msg, platform, out string blocker) != null)
                return AutomodIssued(AutomodAction.Delete, 0);
            WarnAutomodDeleteBlockedOnce(platform, blocker);
            var (action, seconds) = AutomodFallbackOf(decision);
            return AutomodIssued(action, seconds);
        }

        // The decision's substitute for a declined Delete, normalised. It can never itself be
        // Delete — ResolveLadderWithoutDelete cannot return one and a fixed rule carries None
        // — but normalising here means a declined delete can never re-enter the delete path
        // from either the preview or the dispatcher.
        private static (AutomodAction Action, int DurationSeconds) AutomodFallbackOf(AutomodDecision d)
            => d.FallbackAction == AutomodAction.Delete
                ? (AutomodAction.None, 0)
                : (d.FallbackAction, d.FallbackDurationSeconds);

        // One shape for "what got issued", so a previewed duration is the one that would go
        // out: only a Timeout carries seconds, and they are clamped exactly as DispatchTimeout
        // clamps them.
        private static (AutomodAction Action, int DurationSeconds) AutomodIssued(AutomodAction action, int durationSeconds)
            => (action, action == AutomodAction.Timeout ? ClampAutomodTimeout(durationSeconds) : 0);

        // Twitch bounds: 1..1209600s (14 days). Kick/YouTube: at least 1s. An unset or
        // non-positive rung duration means "use the house default" rather than "no timeout".
        private static int ClampAutomodTimeout(int durationSeconds)
        {
            if (durationSeconds <= 0) return 600;
            return durationSeconds > 1209600 ? 1209600 : durationSeconds;
        }

        // Returns whether the request went out; a nameless author leaves nothing to act on,
        // and the caller must not report a timeout it never sent.
        private bool DispatchTimeout(string user, string platform, int durationSeconds, string reason)
        {
            if (string.IsNullOrWhiteSpace(user)) return false;
            int sec = ClampAutomodTimeout(durationSeconds);
            string dur = sec.ToString(CultureInfo.InvariantCulture);

            switch (platform)
            {
                case ChatPlatforms.YouTube:
                    DispatchNamedAction("automod.timeout", PhxSbActions.YtTimeout, new { user, duration = dur });
                    break;
                case ChatPlatforms.Kick:
                    DispatchNamedAction("automod.timeout", PhxSbActions.KickTimeout, new { user, duration = dur });
                    break;
                default:
                    DispatchNamedAction("automod.timeout", PhxSbActions.Timeout, new { user, duration = dur });
                    break;
            }
            GlobalLogger.Log($"Automod: timed out {user} for {sec}s ({reason}).", "AutomodService", LogLevel.LogicExecution);
            return true;
        }

        // Returns whether the request went out — same reason as DispatchTimeout.
        private bool DispatchBan(string user, string platform, string reason)
        {
            if (string.IsNullOrWhiteSpace(user)) return false;
            string r = string.IsNullOrEmpty(reason) ? "spam" : reason;
            if (r.Length > 500) r = r.Substring(0, 500);   // Twitch/Kick ban-reason cap.

            switch (platform)
            {
                case ChatPlatforms.YouTube:
                    // YT ban has no reason field.
                    DispatchNamedAction("automod.ban", PhxSbActions.YtBan, new { user });
                    break;
                case ChatPlatforms.Kick:
                    DispatchNamedAction("automod.ban", PhxSbActions.KickBan, new { user, reason = r });
                    break;
                default:
                    DispatchNamedAction("automod.ban", PhxSbActions.Ban, new { user, reason = r });
                    break;
            }
            GlobalLogger.Log($"Automod: banned {user} ({r}).", "AutomodService", LogLevel.LogicExecution);
            return true;
        }

        /// <summary>Sends the platform delete for <paramref name="msg"/>. Returns whether the
        /// request was DISPATCHED — deliberately not whether the message came down, which
        /// this code cannot know: <see cref="DispatchNamedAction"/> is fire-and-forget and
        /// Streamer.bot returns no result, and on Kick the wrapper the pack ships is an empty
        /// placeholder that accepts the call and does nothing. False means Hub declined to
        /// send at all, with the reason logged once.</summary>
        private bool DispatchDelete(ChatMessage msg, string platform)
        {
            string? action = ResolveAutomodDeleteAction(msg, platform, out string blocker);
            if (action == null)
            {
                WarnAutomodDeleteBlockedOnce(platform, blocker);
                return false;
            }
            DispatchNamedAction("automod.delete_message", action, new { messageId = msg.MessageId });
            GlobalLogger.Log(
                $"Automod: sent \"{action}\" to delete a message from {msg.Username} on {platform} " +
                "(fire-and-forget — Streamer.bot reports no result, so this is not a confirmation).",
                "AutomodService", LogLevel.LogicExecution);
            return true;
        }

        /// <summary>The Streamer.bot wrapper action that can delete <paramref name="msg"/> on
        /// <paramref name="platform"/> RIGHT NOW, or null plus the operator-readable reason it
        /// cannot. One resolver, three callers: the capability gate wired into
        /// AutomodService.DeleteCapabilityProvider, the dry-run preview, and DispatchDelete
        /// itself — so the rung's promise, the preview and the dispatch can never drift apart.
        ///
        /// TWO independent conditions must hold, and the FIRST is the one that decides the
        /// answer on this build: the chat payload has to carry a message id, and no chat
        /// mapper in this tree populates <c>ChatMessage.MessageId</c> — WS.cs builds every
        /// ChatMessage without one on all three platforms. Hand-building the
        /// "Phoenix: Delete Message" action in Streamer.bot therefore does NOT enable the
        /// Automod Delete rung; it satisfies the second condition only, and the id check still
        /// refuses first. That is deliberate rather than a placeholder: the moment a mapper
        /// supplies the id, this same code starts deleting with no change here.
        ///
        /// What the action check PROVES is that the connected Streamer.bot has an action by
        /// that name (the connect-probe's inventory, via SbActionAvailable). It does NOT prove
        /// the wrapper's sub-action actually deletes: the shipped pack's
        /// "Phoenix: Kick Delete Message" imports as an empty placeholder, and a user is free
        /// to gut any wrapper. Streamer.bot's GetActions reports names, not bodies, so that is
        /// the strongest capability signal this codebase has.</summary>
        private string? ResolveAutomodDeleteAction(ChatMessage msg, string platform, out string blocker)
        {
            // No null guard: both entry points reject a null message before Automod does any
            // work — AutomodService.EvaluateAsync for the capability gate, and
            // TryHandleAutomodAsync for ApplyModerationAsync's dispatch and preview.
            if (string.IsNullOrWhiteSpace(msg.MessageId))
            {
                blocker = "the chat payload carried no message id";
                return null;
            }

            string? action = platform switch
            {
                ChatPlatforms.Kick    => PhxSbActions.KickDeleteMessage,
                // Streamer.bot 1.0.x exposes no YouTube delete-message sub-action, so the
                // pack defines no wrapper and there is no const to name here.
                ChatPlatforms.YouTube => null,
                _                     => PhxSbActions.DeleteMessage,
            };
            if (action == null)
            {
                blocker = "Streamer.bot exposes no delete-message action for this platform";
                return null;
            }

            // SbActionAvailable folds three different states into one false — offline,
            // un-probed, and genuinely absent. Split them here, because "your Streamer.bot
            // has no Phoenix: Delete Message action" is a wrong and actively misleading
            // instruction to give an operator whose Streamer.bot merely is not running.
            if (!WS.Instance.IsConnected)
            {
                blocker = "Streamer.bot is not connected";
                return null;
            }
            if (_knownSbActions is null)
            {
                blocker = "Streamer.bot's action list has not been read yet (the connect probe has not answered)";
                return null;
            }
            // Still routed through the shared gate rather than the local set, so this stays
            // the same authority every other capability check in the Hub uses.
            if (!SbActionAvailable(action))
            {
                blocker = $"the connected Streamer.bot has no \"{action}\" action";
                return null;
            }

            blocker = "";
            return action;
        }

        // The capability gate AutomodService consults before letting a Delete rung stand.
        // It logs the refusal itself — the gate short-circuits the rung to a different
        // action, so DispatchDelete never runs for it and would never get the chance to
        // say why. Same one-shot key as the dispatch path, so a platform whose delete is
        // blocked for one reason produces exactly one System Log line either way.
        private bool CanDeleteChatMessage(ChatMessage msg)
        {
            // AutomodService.EvaluateAsync rejects a null message before reaching the gate.
            string platform = msg.Platform ?? ChatPlatforms.Twitch;
            if (ResolveAutomodDeleteAction(msg, platform, out string blocker) != null) return true;
            WarnAutomodDeleteBlockedOnce(platform, blocker);
            return false;
        }

        // ONE line per (platform, blocker) per Streamer.bot connect-probe — a spam wave is
        // hundreds of messages and the operator learns nothing from the 2nd through 400th.
        // Same lock-and-set shape as AutomodService.WarnRegexOnce.
        private readonly object _automodDeleteWarnGate = new();
        private readonly HashSet<string> _automodDeleteBlockedWarned = new(StringComparer.Ordinal);

        // The _knownSbActions instance the entries above were judged against.
        // ProbeStreamerBotActionsAsync publishes a FRESH set on every (re)connect and clears
        // its own _missingSbActionWarned right there; this partial keeps the same reset-per-
        // probe contract by noticing the new set by reference, which is also the exact moment
        // the answers can change. Without the reset, a single "has no Phoenix: Delete Message
        // action" line would be the operator's only warning for the life of the process — even
        // after they import the pack, and even when the real state at the time was
        // "Streamer.bot was not connected".
        private HashSet<string>? _automodDeleteWarnGeneration;

        private void WarnAutomodDeleteBlockedOnce(string platform, string blocker)
        {
            bool first;
            lock (_automodDeleteWarnGate)
            {
                var inventory = _knownSbActions;
                if (!ReferenceEquals(_automodDeleteWarnGeneration, inventory))
                {
                    _automodDeleteWarnGeneration = inventory;
                    _automodDeleteBlockedWarned.Clear();
                }
                first = _automodDeleteBlockedWarned.Add(platform + "|" + blocker);
            }
            if (!first) return;
            GlobalLogger.Log(
                $"Automod cannot delete messages on {platform} — {blocker}. " +
                "A Delete ladder rung falls through to the next rung; a rule with a fixed " +
                "Delete action takes no platform action. Either way the Activity log records " +
                "what was actually issued, not the Delete.",
                "AutomodService", LogLevel.Communication);
        }

        private static string DescribeAction(AutomodAction action, int durationSeconds) => action switch
        {
            AutomodAction.Warn => "warn",
            AutomodAction.Delete => "delete the message of",
            AutomodAction.Timeout => $"time out ({durationSeconds}s)",
            AutomodAction.Ban => "ban",
            // A violation whose action cannot be issued — say so rather than name the
            // configured verb, which is the whole point of previewing.
            _ => "take no action on",
        };

        private static string WarnText(AutomodDecision d)
        {
            string why = string.IsNullOrWhiteSpace(d.Reason) ? d.Rule : d.Reason;
            return string.IsNullOrWhiteSpace(why) ? "please watch the chat rules." : $"please watch the chat rules ({why}).";
        }

        // Route a reply on the platform the message arrived on, reusing the exact
        // per-platform chat-send cores (mirrors SendCountersReply / SendLoyaltyReply).
        private void SendAutomodReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "automod"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "automod"); break;
                default:                    SendTwitchChatCore(reply, "automod"); break;
            }
        }
    }
}
