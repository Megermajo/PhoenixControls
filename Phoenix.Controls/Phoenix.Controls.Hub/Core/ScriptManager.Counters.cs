using System;
using System.Globalization;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Counters (named databank-backed counters) — the Hub-side
    // wiring of the fourth pre-build tool (sibling of Timer / Loyalty / Giveaway).
    // Two responsibilities, mirroring ScriptManager.Quotes.cs:
    //
    //   1. Seam injection — CountersService can't touch the script dispatcher
    //      itself (Hub-side), so RaiseScriptEvent (Counter.OnChanged) is wired at
    //      the top of RegisterCountersCommands, mirroring Loyalty's Action-shaped
    //      seam (fire-and-forget through AsyncErrorBoundary.SafeRunAsync so a
    //      faulting event handler routes to the log instead of becoming an
    //      unobserved Task). The overlay readout needs NO seam: CountersService
    //      publishes counter.<name>.count into the Overlay Live Channel through
    //      its own LiveStore property, which defaults to the process-wide store —
    //      so there is nothing to wire here or in HubBootstrapper.
    //
    //   2. The BUILT-IN chat-command parser (TryHandleCountersChatCommandAsync),
    //      registered as the Counters provider in RegisterBuiltInChatProviders.
    //      Parses !<cmd> / !<cmd>+ / !<cmd>- / !set<cmd> N / !reset<cmd> against
    //      each enabled counter. Default-OFF is a total no-op: the parser
    //      early-returns false the instant the tool is disabled.
    //
    // The five counter.* script commands (set / add / reset / delete / get) were
    // RETIRED in the 2026-08 tool-node cut with the Architect Counter.* wrapper
    // nodes: the counts live in the OPEN "Counters" table, so graphs use the
    // generic db.* family instead, and CountersService's 15-second databank sweep
    // keeps the overlay keys + Counter.OnChanged honest for db-written changes.
    // The old names answer through ScriptManager.RetiredCommands shims; the chat
    // commands and the panel are untouched.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        // Per-user per-counter cooldown map for the BUILT-IN chat commands only
        // (CounterDef.CooldownSeconds). Monotonic Stopwatch clock — never wall
        // clock — so an NTP step / DST change can't unblock a cooldown early.
        // Keyed "<cmd>\0<normUser>" -> end-tick ms. Mirrors the Loyalty pattern.
        private readonly object _countersCdGate = new();
        private readonly System.Collections.Generic.Dictionary<string, long> _countersUserCdMs =
            new(StringComparer.Ordinal);
        private static readonly System.Diagnostics.Stopwatch _countersMono =
            System.Diagnostics.Stopwatch.StartNew();

        private void RegisterCountersCommands()
        {
            // ── Seam ─────────────────────────────────────────────────────────
            // Counter.OnChanged script events flow back through the generic-event
            // dispatcher with pre-built vars (event.counter / event.count). The
            // seam is an Action (the service can't await it), so the async
            // event-dispatch is fired-and-forgotten HERE through
            // AsyncErrorBoundary.SafeRunAsync — faults route to the log, expected
            // shutdown cancellation is swallowed (mirrors ScriptManager.Loyalty.cs).
            CountersService.Instance.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "CountersService", $"RaiseScriptEvent({phoenixEvent})");
            };
        }

        // ── Built-in chat-command parser ─────────────────────────────────────
        /// <summary>
        /// The built-in Counters chat commands: !&lt;cmd&gt; shows the count,
        /// !&lt;cmd&gt;+ / !&lt;cmd&gt;- step it by the counter's Step, !set&lt;cmd&gt; N
        /// sets it, !reset&lt;cmd&gt; zeroes it. READ (!&lt;cmd&gt;) is gated by
        /// ViewRoles; every MODIFY form is gated by ModifyRoles; both honor the
        /// per-user CooldownSeconds. Returns true only when a counter command was
        /// recognized AND handled (so the caller can suppress the author on_chat
        /// fan-out). DEFAULT-OFF IS A TOTAL NO-OP — returns false the instant the
        /// tool is disabled. Bot self-messages are already dropped upstream.
        /// </summary>
        public async Task<bool> TryHandleCountersChatCommandAsync(ChatMessage msg)
        {
            var svc = CountersService.Instance;
            if (!svc.Active) return false;                       // Config.Enabled == false → no-op
            if (msg is null) return false;
            var cfg = svc.Config;
            if (cfg?.Counters == null || cfg.Counters.Count == 0) return false;

            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return false;

            string body = text.Substring(1);
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body.Substring(0, sp)).Trim();
            if (token.Length == 0) return false;
            string rest = sp < 0 ? string.Empty : body.Substring(sp + 1).Trim();

            string userNorm = (msg.Username ?? string.Empty).Trim().ToLowerInvariant();

            // Role checks consult User-Management's Effective(): Mod/VIP/Sub are the
            // platform's own flags (the member lists that once granted them are
            // retired), and the Regular tick resolves off the Regular list plus the
            // watch-hour rule; passthrough while the tool is dormant.
            var eff = UserManagementService.Instance.Effective(msg);
            bool ViewOk(CounterDef d) =>
                d.ViewRoles != null && d.ViewRoles.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);
            bool ModifyOk(CounterDef d) =>
                d.ModifyRoles != null && d.ModifyRoles.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);
            static bool Eq(string a, string b) => ChatVerb.Matches(b, a);

            // Two passes so an EXACT-token form always beats another counter's
            // synthesized prefix form: a counter literally named "setdeaths" stays
            // readable via !setdeaths even when a "deaths" counter also exists (whose
            // "set"+cmd expansion would otherwise shadow it AND zero "deaths").
            //
            // PASS 1 — exact-token forms: !<cmd> (read), !<cmd>+ / !<cmd>- (step).
            foreach (var def in cfg.Counters)
            {
                if (def == null || !def.ChatEnabled) continue;
                // Canonicalized at the SOURCE, not just at the comparison, because
                // `cmd` is also concatenated into the synthesized shapes ("set"+cmd,
                // cmd+"+") and echoed back in every reply. A configured "!deaths"
                // would otherwise build "set!deaths" — which matches nothing — and
                // print "Usage: !set!deaths <number>".
                string cmd = ChatVerb.Canonical(def.EffectiveCommand);
                if (cmd.Length == 0) continue;
                long step = def.Step == 0 ? 1 : def.Step;

                if (Eq(token, cmd + "+"))
                {
                    if (!ModifyOk(def) || !CountersCooldownOk(cmd, userNorm, def.CooldownSeconds, isModify: true)) return true;
                    long r = await svc.AddAsync(def.Name, step).ConfigureAwait(false);
                    SendCountersReply(msg, CounterReply(def, cmd, r));
                    return true;
                }
                if (Eq(token, cmd + "-"))
                {
                    if (!ModifyOk(def) || !CountersCooldownOk(cmd, userNorm, def.CooldownSeconds, isModify: true)) return true;
                    long r = await svc.AddAsync(def.Name, -step).ConfigureAwait(false);
                    SendCountersReply(msg, CounterReply(def, cmd, r));
                    return true;
                }
                if (Eq(token, cmd))   // READ
                {
                    if (!ViewOk(def) || !CountersCooldownOk(cmd, userNorm, def.CooldownSeconds, isModify: false)) return true;
                    long c = await svc.GetAsync(def.Name).ConfigureAwait(false);
                    SendCountersReply(msg, CounterReply(def, cmd, c));
                    return true;
                }
            }

            // PASS 2 — prefix forms: !set<cmd> N / !reset<cmd> (lower priority than an
            // exact bare/step match, so a prefix shape can't shadow another counter's read).
            foreach (var def in cfg.Counters)
            {
                if (def == null || !def.ChatEnabled) continue;
                // Canonicalized at the SOURCE, not just at the comparison, because
                // `cmd` is also concatenated into the synthesized shapes ("set"+cmd,
                // cmd+"+") and echoed back in every reply. A configured "!deaths"
                // would otherwise build "set!deaths" — which matches nothing — and
                // print "Usage: !set!deaths <number>".
                string cmd = ChatVerb.Canonical(def.EffectiveCommand);
                if (cmd.Length == 0) continue;

                if (Eq(token, "set" + cmd))
                {
                    if (!ModifyOk(def) || !CountersCooldownOk(cmd, userNorm, def.CooldownSeconds, isModify: true)) return true;
                    // A blank / non-numeric argument is an operator typo — never silently
                    // zero the counter. Reply with usage and leave the value untouched.
                    if (!TryParseCounterValue(rest, out long value))
                    {
                        SendCountersReply(msg, $"Usage: !set{cmd} <number>");
                        return true;
                    }
                    long r = await svc.SetAsync(def.Name, value).ConfigureAwait(false);
                    SendCountersReply(msg, CounterReply(def, cmd, r));
                    return true;
                }
                if (Eq(token, "reset" + cmd))
                {
                    if (!ModifyOk(def) || !CountersCooldownOk(cmd, userNorm, def.CooldownSeconds, isModify: true)) return true;
                    long r = await svc.ResetAsync(def.Name).ConfigureAwait(false);
                    SendCountersReply(msg, CounterReply(def, cmd, r));
                    return true;
                }
            }

            return false;   // not a Counters command — author scripts handle it normally
        }

        // Strict parse for !set<cmd>: accepts a plain integer or a floored finite
        // decimal, rejects blank/garbage so a typo can't zero the counter.
        private static bool TryParseCounterValue(string raw, out long value)
        {
            value = 0;
            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0) return false;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
            {
                // Finite is NOT enough: "!setdeaths 1e20" floors to a value outside
                // long's range and the cast silently yields long.MinValue — the counter
                // would land on a nonsense number the operator never typed. Reject it
                // like any other garbage so the caller replies with the usage line.
                double floored = Math.Floor(d);
                if (!FitsInLong(floored)) return false;   // value is already 0 (failed long.TryParse)
                value = (long)floored;
                return true;
            }
            return false;
        }

        // Reply text: ReplyTemplate with {name}/{count}, else "<cmd>: <count>".
        // {name} resolves to the counter's Name (falls back to the command word).
        private static string CounterReply(CounterDef def, string cmd, long count)
        {
            string tmpl = def.ReplyTemplate;
            string countStr = count.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(tmpl))
                return $"{cmd}: {countStr}";
            string name = string.IsNullOrWhiteSpace(def.Name) ? cmd : def.Name;
            return tmpl.Replace("{name}", name).Replace("{count}", countStr);
        }

        // Per-user command cooldown check + stamp. True = clear to run (and stamped).
        // Per-user cooldown, bucketed by operation class (READ vs MODIFY) so a benign
        // read never consumes the modify budget (a read then a !cmd+ must both run).
        private bool CountersCooldownOk(string cmd, string user, int cooldownSeconds, bool isModify)
        {
            if (cooldownSeconds <= 0) return true;
            string key = cmd + "\0" + (isModify ? "m" : "r") + "\0" + user;
            long now = _countersMono.ElapsedMilliseconds;
            lock (_countersCdGate)
            {
                if (_countersUserCdMs.TryGetValue(key, out long end) && now < end) return false;
                _countersUserCdMs[key] = now + cooldownSeconds * 1000L;
                return true;
            }
        }

        // Route the reply on the platform the command arrived on, reusing the exact
        // per-platform chat-send cores chat.send / *.send_chat use — mirrors
        // SendLoyaltyReply, no new send path.
        private void SendCountersReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "counters"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "counters"); break;
                default:                    SendTwitchChatCore(reply, "counters"); break;
            }
        }
    }
#pragma warning restore CS1998
}
