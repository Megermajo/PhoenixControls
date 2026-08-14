using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>Monotonic + wall clock seam so the escalation decay, the !permit
    /// grace window and the rate/flood sliding windows are all testable without
    /// wall-clock waits. The default reads a process-wide Stopwatch (monotonic —
    /// immune to NTP steps / DST) and UTC now.</summary>
    public interface IAutomodClock
    {
        /// <summary>Monotonic milliseconds — permit + rate windows.</summary>
        long MonotonicMs { get; }
        /// <summary>Unix seconds (wall clock) — strike decay + last_ts.</summary>
        long UnixSeconds { get; }
    }

    internal sealed class SystemAutomodClock : IAutomodClock
    {
        private static readonly Stopwatch _mono = Stopwatch.StartNew();
        public long MonotonicMs => _mono.ElapsedMilliseconds;
        public long UnixSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    // AutomodService — the Hub-runtime brain of the spam-filter / automod pre-build
    // tool (sibling of Loyalty / Counters). Stateless-over-DB for the escalation
    // strikes (the OPEN "AutomodStrikes" table is the single source of truth — never
    // cached, because db.* scripts write the same table); only the tool CONFIG and
    // the EPHEMERAL rate/flood + permit windows are held in memory. Every strike
    // mutation is ONE atomic DB transaction (decay computed inside it).
    //
    // The service is Hub-side but can't touch the script dispatcher / chat sender
    // directly, so RaiseScriptEvent (Automod.OnViolation) and BotAccountsProvider are
    // injected from the ScriptManager side (RegisterAutomodSeams), exactly like Loyalty.
    // DEFAULT-OFF (Config.Enabled == false) makes Active false → the provider predicate
    // is false → a total no-op.
    public sealed class AutomodService
    {
        private readonly DB _db;
        private readonly IAutomodClock _clock;

        public AutomodService(DB db, IAutomodClock? clock = null)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _clock = clock ?? new SystemAutomodClock();
        }

        private static AutomodService? _instance;
        private static readonly object _instanceGate = new();
        public static AutomodService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new AutomodService(DB.Instance);
            }
        }

        // ── Config (cached; strikes are NEVER cached) ───────────────────────
        private volatile AutomodConfig _config = new();
        public AutomodConfig Config => _config;

        /// <summary>Master gate — false makes the provider predicate false, so the
        /// interceptor is a total no-op. The Architect moderation nodes are unaffected
        /// (this tool adds none) — only the tool's automatic scanning goes dormant.</summary>
        public bool Active => _config.Enabled;

        // ── Injected Hub-side seams ─────────────────────────────────────────
        /// <summary>Raises the Automod.OnViolation script event (wired in RegisterAutomodSeams).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>Live Hub bot-account list (AppConfig.BotUsername split/trim/lower).
        /// Defense-in-depth — bots are already dropped at ingest. Wired in RegisterAutomodSeams.</summary>
        public Func<IReadOnlyCollection<string>>? BotAccountsProvider { get; set; }

        /// <summary>Answers "can Hub actually delete THIS message right now?" — the runtime
        /// capability behind a Delete ladder rung. Both halves of the answer are Hub-side
        /// (the message's platform id, and whether the connected Streamer.bot exposes that
        /// platform's delete action), so it is injected in RegisterAutomodSeams like the two
        /// seams above. The wired implementation ALSO emits the one-shot operator diagnostic
        /// when it answers false — this side short-circuits the rung and would otherwise
        /// never say why, and only the Hub side knows the reason.
        ///
        /// NULL — no Hub wired it, e.g. the service standing alone in a test — reads as NO.
        /// A capability gate cannot be optimistic: an unconfirmed capability that resolved
        /// "yes" would issue a delete that silently does nothing INSTEAD of the timeout the
        /// rung degrades to. Same doctrine as ScriptManager.SbActionAvailable.</summary>
        public Func<ChatMessage, bool>? DeleteCapabilityProvider { get; set; }

        // ── Change notifications (UI) ───────────────────────────────────────
        public event EventHandler? ConfigChanged;
        public event EventHandler? Activity;

        private void RaiseConfigChanged() => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "AutomodService", "ConfigChanged");
        private void RaiseActivity() => SafeEvent.Raise(Activity, this, EventArgs.Empty, "AutomodService", "Activity");

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ── Ephemeral in-memory windows (monotonic clock) ───────────────────
        private readonly object _permitGate = new();
        private readonly Dictionary<string, long> _permits = new(StringComparer.Ordinal);   // user → expiry ms

        private readonly object _rateGate = new();
        private readonly Dictionary<string, Queue<long>> _rateWindows = new(StringComparer.Ordinal); // user → msg ms
        private int _rateSweepCounter;

        // Dry-run strike PROJECTION — user → (simulated count, unix seconds of the last
        // simulated hit). Dry-run is observe-only, so the persisted AutomodStrikes row
        // must NOT move while it is on: a streamer previewing the tool would otherwise
        // bank real strikes and ban a chatter at rung 5 the moment they switch dry-run
        // off. The simulated tally lets the dry-run log still report the rung the
        // message WOULD have hit, and it is discarded when dry-run is switched off.
        private readonly object _dryRunGate = new();
        private readonly Dictionary<string, (long Count, long LastTs)> _dryRunStrikes = new(StringComparer.Ordinal);

        // Compiled blocklist regexes (P0: each carries a 100 ms matchTimeout). Rebuilt
        // on every config change; volatile publish so the chat path reads a whole list.
        private volatile IReadOnlyList<Regex> _compiledRegexes = Array.Empty<Regex>();
        private volatile IReadOnlyList<string> _invalidRegexPatterns = Array.Empty<string>();
        private readonly object _regexWarnGate = new();
        private readonly HashSet<string> _regexWarned = new(StringComparer.Ordinal);

        /// <summary>Blocklist patterns that would not compile on the last config change
        /// (empty when the whole blocklist is valid). Published so the Lists tab can show
        /// a PASSIVE cue beside the field instead of only the once-per-pattern System Log
        /// line — the panel reports this list, it never compiles a pattern itself.</summary>
        public IReadOnlyList<string> InvalidRegexPatterns => _invalidRegexPatterns;

        // ── Lifecycle ───────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? raw = await _db.LoadAutomodConfigAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cfg = JsonSerializer.Deserialize<AutomodConfig>(raw!, JsonOpts);
                    if (cfg != null) _config = Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("AutomodService", "InitializeAsync: config load failed", ex);
            }
            try
            {
                await _db.EnsureAutomodStrikesTableAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("AutomodService", "InitializeAsync: strikes-table ensure failed", ex);
            }
            RebuildRegexes(_config);
            GlobalLogger.Log(
                $"AutomodService online — spam filter {(Active ? "ENABLED" : "disabled")} (dormant until enabled).",
                "AutomodService", LogLevel.System);
        }

        private static AutomodConfig Normalize(AutomodConfig cfg)
        {
            cfg.ExemptRoles ??= AutomodRoles.Mods();
            cfg.PermitRoles ??= AutomodRoles.Mods();
            cfg.Caps ??= new AutomodCapsRule();
            cfg.Length ??= new AutomodLengthRule();
            cfg.Repeat ??= new AutomodRepeatRule();
            cfg.Symbols ??= new AutomodSymbolRule();
            cfg.Links ??= new AutomodLinksRule();
            cfg.Words ??= new AutomodWordsRule();
            cfg.Regex ??= new AutomodRegexRule();
            cfg.Rate ??= new AutomodRateRule();
            cfg.Ladder ??= AutomodConfig.DefaultLadder();
            if (cfg.Ladder.Count == 0) cfg.Ladder = AutomodConfig.DefaultLadder();
            cfg.UrlAllowList ??= new List<string>();
            cfg.UrlBlockList ??= new List<string>();
            cfg.BlocklistWords ??= new List<string>();
            cfg.BlocklistRegex ??= new List<string>();
            if (cfg.PermitSeconds <= 0) cfg.PermitSeconds = 60;
            return cfg;
        }

        // ── Config edits (panel) ────────────────────────────────────────────
        /// <summary>Replaces the whole config, persists it, recompiles the regex cache
        /// and notifies the UI.</summary>
        public async Task SaveConfigAsync(AutomodConfig cfg)
        {
            bool wasDryRun = _config.DryRun;
            // Deep-clone so the service never adopts the panel VM's live-mutating instance
            // — an in-place Ladder/list edit must not race the chat hot path's _config read.
            _config = Normalize(CloneConfig(cfg));
            RebuildRegexes(_config);
            // Leaving dry-run throws the simulation away — the real strike rows were never
            // touched, so enforcement resumes from where the chatter actually stands.
            if (wasDryRun && !_config.DryRun)
            {
                lock (_dryRunGate) _dryRunStrikes.Clear();
            }
            try
            {
                string json = JsonSerializer.Serialize(_config, JsonOpts);
                await _db.SaveAutomodConfigAsync(json, NowUnixMs()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("AutomodService", "SaveConfigAsync failed", ex);
            }
            RaiseConfigChanged();
        }

        // Deep-clone via a JSON round-trip so _config is always a private copy the UI
        // cannot mutate (a live Ladder edit must not alias the chat hot path).
        private static AutomodConfig CloneConfig(AutomodConfig? cfg)
        {
            if (cfg == null) return new AutomodConfig();
            try
            {
                return JsonSerializer.Deserialize<AutomodConfig>(
                    JsonSerializer.Serialize(cfg, JsonOpts), JsonOpts) ?? new AutomodConfig();
            }
            catch { return new AutomodConfig(); }
        }

        // ── Permit (!<verb> <name> → link grace) ────────────────────────────
        /// <summary>The permit word in the ONE canonical form both the parser and the
        /// usage reply use — <see cref="ChatVerb.Canonical"/> of
        /// <see cref="AutomodConfig.PermitCommand"/>, "permit" out of the box.</summary>
        /// <remarks>
        /// <para>Read canonical rather than raw because the same string is BOTH compared
        /// against the parsed chat token AND printed back ("Usage: !permit &lt;name&gt;"):
        /// a streamer who types the bang into the field would otherwise be told to type
        /// "!!permit". One derived form means the word a viewer is told to use is
        /// byte-for-byte the word that matches.</para>
        /// <para>The stored field is deliberately left VERBATIM: <see cref="Normalize"/>
        /// does not rewrite it, so whatever the streamer typed round-trips to whatever
        /// surface edits it. Canonicalizing on the READ instead is the suite-wide rule
        /// (see <see cref="ChatVerb"/>) — it is the one place that covers every path a
        /// verb can arrive by, including the live panel edits that are pushed straight
        /// into a service.</para>
        /// <para>EMPTY — a blank or bang-only field — means the command is OFF, not
        /// "match anything": <see cref="ChatVerb.Matches"/> refuses an empty configured
        /// side, so the interceptor's permit branch cannot fire and a "!permit …" line is
        /// scanned like any other chat message. That direction is the safe one; the
        /// opposite would turn a blank field into a filter bypass every chatter could
        /// reach.</para>
        /// </remarks>
        public string PermitVerb => ChatVerb.Canonical(_config.PermitCommand);

        /// <summary>True when the chatter's roles allow issuing the permit command.
        /// Consults the User-Management group overlay (a group-granted Mod passes like
        /// the platform rank; passthrough while that tool is dormant).</summary>
        public bool IsPermitAuthorized(ChatMessage msg)
        {
            if (msg == null || _config.PermitRoles == null) return false;
            var eff = UserManagementService.Instance.Effective(msg);
            return _config.PermitRoles.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);
        }

        /// <summary>Grants the named user a PermitSeconds grace window (links rule skip).</summary>
        public void GrantPermit(string userNorm)
        {
            if (string.IsNullOrEmpty(userNorm)) return;
            long now = _clock.MonotonicMs;
            long exp = now + Math.Max(1, _config.PermitSeconds) * 1000L;
            lock (_permitGate)
            {
                _permits[userNorm] = exp;
                // The permit command is role-gated (PermitRoles, mods by default) + rare;
                // opportunistically drop expired grants so the map stays bounded
                // (evict-on-read in IsPermitted handles the common case).
                if (_permits.Count > 64)
                {
                    var stale = new List<string>();
                    foreach (var kv in _permits) if (kv.Value <= now) stale.Add(kv.Key);
                    foreach (var k in stale) _permits.Remove(k);
                }
            }
        }

        /// <summary>True while the named user's link-permit grace is live.</summary>
        public bool IsPermitted(string userNorm)
        {
            if (string.IsNullOrEmpty(userNorm)) return false;
            long now = _clock.MonotonicMs;
            lock (_permitGate)
            {
                if (!_permits.TryGetValue(userNorm, out long exp)) return false;
                if (now < exp) return true;
                _permits.Remove(userNorm);   // evict-on-read so the map can't grow unbounded
                return false;
            }
        }

        // ── Core scan ───────────────────────────────────────────────────────
        /// <summary>Scans one inbound chat message. Returns a matched decision (rule +
        /// action + duration + reason) or <see cref="AutomodDecision.None"/> when the
        /// message is clean, exempt, out of scope, from a bot, or the tool is off.
        /// Respects exempt roles, the bot list and the link permit; on a firing rule
        /// bumps the escalation strikes (atomic) unless the rule uses a fixed action —
        /// or dry-run is on, in which case the count is PROJECTED in memory and nothing
        /// is persisted (see <see cref="ProjectStrikeAsync"/>).
        ///
        /// A resolved LADDER Delete rung is capability-gated here (see
        /// <see cref="DeleteCapabilityProvider"/>) and degrades through
        /// <see cref="ResolveLadderWithoutDelete"/> when the answer is no. A FIXED Delete is
        /// deliberately NOT gated here — see the comment on that branch — so a returned
        /// <see cref="AutomodAction.Delete"/> means "the runtime intends to delete", not "a
        /// delete is guaranteed". The action that was actually ISSUED is computed by the
        /// dispatcher, which is what the audit row, the Automod.OnViolation event and the
        /// dry-run preview report; a Delete this method returns can still be declined there
        /// (Streamer.bot may drop between the two), in which case
        /// <see cref="AutomodDecision.FallbackAction"/> is issued and reported instead.</summary>
        public async Task<AutomodDecision> EvaluateAsync(ChatMessage msg)
        {
            if (!Active || msg is null) return AutomodDecision.None;
            var cfg = _config;
            string text = msg.Message ?? "";
            if (text.Length == 0) return AutomodDecision.None;

            if (!InScope(cfg, msg.Platform)) return AutomodDecision.None;

            string userNorm = (msg.Username ?? "").Trim().ToLowerInvariant();
            if (IsBotAccount(userNorm)) return AutomodDecision.None;

            // Exemption consults the User-Management group overlay — a group-granted
            // Mod/VIP/Sub is exempt exactly like the platform rank, and a Regular-group
            // member is exempt once the streamer ticks that box.
            var effRoles = UserManagementService.Instance.Effective(msg);
            if (cfg.ExemptRoles != null && cfg.ExemptRoles.Allows(effRoles.IsSub, effRoles.IsVip, effRoles.IsMod, msg.IsBroadcaster, effRoles.IsRegular))
                return AutomodDecision.None;

            if (!TryDetect(cfg, msg, userNorm, out string rule, out string reason, out AutomodRuleBase? firingRule))
                return AutomodDecision.None;

            // Fixed action → issue it directly (no strike bump). Deliberately NOT
            // capability-gated: a fixed rule names one action and one only, so there is no
            // second choice to degrade to, and substituting a punishment the streamer never
            // configured would be worse than the rule not landing. A fixed Delete the
            // platform cannot carry out is therefore refused by the dispatcher
            // (ScriptManager.DispatchDelete) with a one-shot log line, and — because its
            // FallbackAction stays None — the audit row, the Automod.OnViolation event and
            // the dry-run preview all report that NOTHING was issued. They must not claim a
            // delete that did not happen: a streamer whose Links rule reads Fixed→Delete has
            // to be able to see, from the Activity log alone, that no message is being
            // removed.
            if (firingRule != null && firingRule.ActionMode == AutomodActionMode.Fixed)
                return AutomodDecision.Hit(rule, firingRule.FixedAction, firingRule.FixedDurationSeconds, reason);

            // Ladder → bump strikes ATOMICALLY, resolve the step for the new count. In
            // DRY-RUN the persisted row is left alone and the count is projected instead,
            // so the preview still escalates through the ladder without banking a strike
            // the streamer never actually issued.
            long strikes;
            if (cfg.DryRun)
            {
                strikes = await ProjectStrikeAsync(userNorm, cfg).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    strikes = await _db.AutomodStrikeBumpAsync(userNorm, _clock.UnixSeconds, cfg.StrikeDecayHours).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("AutomodService", "strike bump failed", ex);
                    strikes = 1;   // degrade to the first rung rather than skipping enforcement
                }
            }
            var (action, duration) = ResolveLadder(cfg, strikes);
            if (action != AutomodAction.Delete)
                return AutomodDecision.Hit(rule, action, duration, reason);

            // A Delete rung only stands when the platform can actually carry it out. The
            // degradation is resolved ONCE, here, and used twice:
            //
            //   • as the ANSWER when the capability is already absent — the gate runs at
            //     scan time (not at the DispatchModeration switch) because the resolved
            //     action is what the audit row, the Automod.OnViolation event and the
            //     DRY-RUN preview report, and dry-run never reaches a dispatcher at all;
            //   • as the FALLBACK carried on the decision for the window between this check
            //     and the DoAction, in which Streamer.bot can drop. Without it that race
            //     would leave the viewer with NO moderation where the pre-gate build gave
            //     them the next rung — a regression, not a wash.
            //
            // Either way the rung falls through to exactly the rung the pre-gate ladder used
            // to skip to, so an install without the capability is behaviourally unchanged.
            var fallback = ResolveLadderWithoutDelete(cfg, strikes);
            if (!CanDelete(msg))
                return AutomodDecision.Hit(rule, fallback.Action, fallback.DurationSeconds, reason);

            return AutomodDecision.Hit(rule, AutomodAction.Delete, duration, reason,
                                       fallback.Action, fallback.DurationSeconds);
        }

        // The Hub-side capability answer, hardened: an unwired seam and a throwing seam
        // both read "cannot delete", which degrades the rung rather than issuing a delete
        // that would silently do nothing.
        private bool CanDelete(ChatMessage msg)
        {
            var probe = DeleteCapabilityProvider;
            if (probe == null) return false;
            try { return probe(msg); }
            catch (Exception ex)
            {
                GlobalLogger.Error("AutomodService", "delete-capability probe failed", ex);
                return false;
            }
        }

        // ── Dry-run strike projection (nothing is persisted) ────────────────
        // Returns the strike count this hit WOULD have produced. The persisted row seeds
        // the projection on a user's first dry-run hit (so an existing offender previews
        // from where they really stand), DECAYED with the same rule the bump transaction
        // applies before IT adds — a raw seed would preview a long-idle offender several
        // rungs above the action enforcement would actually take (4 banked strikes gone
        // stale reads as rung 5 = Ban when the live path yields rung 1 = Warn), which is
        // exactly the miscalibration a preview exists to prevent. After the seed the tally
        // lives in memory and decays on the same StrikeDecayHours rule.
        private async Task<long> ProjectStrikeAsync(string userNorm, AutomodConfig cfg)
        {
            long now = _clock.UnixSeconds;
            lock (_dryRunGate)
            {
                if (_dryRunStrikes.TryGetValue(userNorm, out var prior))
                {
                    long next = DecayStrikes(prior.Count, prior.LastTs, now, cfg.StrikeDecayHours) + 1;
                    _dryRunStrikes[userNorm] = (next, now);
                    return next;
                }
            }

            (long Count, long LastTs) row;
            try { row = await _db.AutomodStrikeReadAsync(userNorm).ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("AutomodService", "dry-run strike read failed", ex);
                row = (0, 0);   // preview from a clean slate rather than skipping the report
            }
            long stored = DecayStrikes(row.Count, row.LastTs, now, cfg.StrikeDecayHours);

            lock (_dryRunGate)
            {
                // A concurrent message from the same user may have seeded the projection
                // while the read was in flight — take the higher of it and the decayed
                // stored count so the preview never walks backwards. The raced entry was
                // written at ~now, so it needs no decay of its own.
                long baseline = _dryRunStrikes.TryGetValue(userNorm, out var raced)
                    ? Math.Max(raced.Count, stored)
                    : stored;
                long projected = baseline + 1;
                _dryRunStrikes[userNorm] = (projected, now);
                PruneDryRunProjection(cfg, now);
                return projected;
            }
        }

        // Bounded like the permit map: past 64 entries drop everyone whose simulated
        // strikes have fully decayed. Decay can be switched off entirely (and a raid of
        // one-off names outgrows any prune), so a hard ceiling resets the whole tally —
        // it is disposable preview state, never enforcement truth. Caller holds _dryRunGate.
        private void PruneDryRunProjection(AutomodConfig cfg, long now)
        {
            if (_dryRunStrikes.Count <= 64) return;
            var stale = new List<string>();
            foreach (var kv in _dryRunStrikes)
                if (DecayStrikes(kv.Value.Count, kv.Value.LastTs, now, cfg.StrikeDecayHours) <= 0) stale.Add(kv.Key);
            foreach (var k in stale) _dryRunStrikes.Remove(k);
            if (_dryRunStrikes.Count > 512) _dryRunStrikes.Clear();
        }

        // Local mirror of the decay rule the strike-bump transaction applies (DB's helper
        // is internal to Shared): one strike forgiven per decayHours idle, floored at 0.
        private static long DecayStrikes(long count, long lastTs, long nowUnixSec, double decayHours)
        {
            if (count <= 0 || decayHours <= 0 || lastTs <= 0 || nowUnixSec <= lastTs) return Math.Max(0, count);
            long steps = (long)Math.Floor((nowUnixSec - lastTs) / 3600.0 / decayHours);
            return steps <= 0 ? count : Math.Max(0, count - steps);
        }

        private bool InScope(AutomodConfig cfg, string? platform)
        {
            string p = platform ?? ChatPlatforms.Twitch;
            if (string.Equals(p, ChatPlatforms.YouTube, StringComparison.OrdinalIgnoreCase)) return cfg.ScopeYouTube;
            if (string.Equals(p, ChatPlatforms.Kick, StringComparison.OrdinalIgnoreCase)) return cfg.ScopeKick;
            return cfg.ScopeTwitch;   // Twitch is the default platform
        }

        private bool IsBotAccount(string userNorm)
        {
            if (string.IsNullOrEmpty(userNorm)) return false;
            var provider = BotAccountsProvider;
            if (provider == null) return false;
            IReadOnlyCollection<string> bots;
            try { bots = provider() ?? Array.Empty<string>(); }
            catch { return false; }
            foreach (var b in bots)
                if (string.Equals(b, userNorm, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Run the enabled detectors, first-fire wins. Rate/flood records the message
        // in the sliding window FIRST (so every scanned message counts) but is checked
        // LAST so a more specific rule takes precedence for the reported rule.
        private bool TryDetect(AutomodConfig cfg, ChatMessage msg, string userNorm,
            out string rule, out string reason, out AutomodRuleBase? firingRule)
        {
            rule = ""; reason = ""; firingRule = null;
            string text = msg.Message ?? "";

            bool rateFired = cfg.Rate != null && cfg.Rate.Enabled
                && RecordAndCheckRate(userNorm, cfg.Rate);

            if (cfg.Regex != null && cfg.Regex.Enabled && RegexFired(text, out reason))
            { rule = "Blocklist regex"; firingRule = cfg.Regex; return true; }

            if (cfg.Words != null && cfg.Words.Enabled && AutomodRules.WordsFired(text, cfg.BlocklistWords, out reason))
            { rule = "Blocklist word"; firingRule = cfg.Words; return true; }

            // ★ Cross-tool exemption, and it is load-bearing rather than a courtesy: this
            // service sits at INDEX 0 of the built-in chat dispatch, so with the Links rule
            // ticked and BlockAll on (its default, and the known-TLD list carries "be"
            // explicitly "because youtu.be is everywhere") a viewer's
            // "!sr https://youtu.be/…" was MODERATED before the Song Request provider six
            // slots later could ever see it — first-handled wins.
            //
            // The waiver is a HOST, not a rule skip, and the difference is a real bypass:
            // skipping LinksFired outright also skipped the streamer's explicit UrlBlockList,
            // so "!sr <blocked-domain-url>" walked through a block they had set on purpose.
            // Handing LinksFired one exempt host instead waives only its generic
            // "links are not allowed" heuristic, only for the request's own URL — the
            // allow-list, the block-list and every other host in the same line are judged
            // exactly as before. ExemptRequestLinkHost additionally demands the tool be
            // ENABLED, the first token be its configured request word, AND the remainder
            // parse as a real YouTube video reference, so "!sr https://spam.tld" is
            // moderated as before and with the tool off this is a field read and an "".
            if (cfg.Links != null && cfg.Links.Enabled
                && AutomodRules.LinksFired(text, cfg.Links, cfg.UrlAllowList, cfg.UrlBlockList,
                       IsPermitted(userNorm), out reason,
                       SongRequestService.Instance.ExemptRequestLinkHost(text)))
            { rule = "Links"; firingRule = cfg.Links; return true; }

            if (cfg.Caps != null && cfg.Caps.Enabled && AutomodRules.CapsFired(text, cfg.Caps, out reason))
            { rule = "Excessive caps"; firingRule = cfg.Caps; return true; }

            if (cfg.Symbols != null && cfg.Symbols.Enabled && AutomodRules.SymbolsFired(text, cfg.Symbols, out reason))
            { rule = "Symbol spam"; firingRule = cfg.Symbols; return true; }

            if (cfg.Repeat != null && cfg.Repeat.Enabled && AutomodRules.RepeatFired(text, cfg.Repeat, out reason))
            { rule = "Repeated characters"; firingRule = cfg.Repeat; return true; }

            if (cfg.Length != null && cfg.Length.Enabled && AutomodRules.LengthFired(text, cfg.Length, out reason))
            { rule = "Message too long"; firingRule = cfg.Length; return true; }

            if (rateFired)
            {
                rule = "Rate / flood";
                reason = $"more than {cfg.Rate!.MaxMessages} messages in {cfg.Rate.WindowSeconds}s";
                firingRule = cfg.Rate;
                return true;
            }

            return false;
        }

        // Per-user monotonic sliding window. Records this message, prunes anything
        // older than the window, returns whether the count now exceeds the max.
        private bool RecordAndCheckRate(string userNorm, AutomodRateRule rule)
        {
            if (string.IsNullOrEmpty(userNorm)) return false;
            int maxMsgs = Math.Max(1, rule.MaxMessages);
            long windowMs = Math.Max(1, rule.WindowSeconds) * 1000L;
            long now = _clock.MonotonicMs;
            long cutoff = now - windowMs;

            lock (_rateGate)
            {
                if (!_rateWindows.TryGetValue(userNorm, out var q))
                {
                    q = new Queue<long>();
                    _rateWindows[userNorm] = q;
                }
                q.Enqueue(now);
                while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();

                MaybeSweepRate(cutoff);
                return q.Count > maxMsgs;
            }
        }

        // Lightweight bounded prune: every ~256 records, drop users whose whole window
        // has aged out, so a raid/bot-flood of one-off names can't grow the map forever.
        private void MaybeSweepRate(long cutoff)
        {
            if (++_rateSweepCounter < 256) return;
            _rateSweepCounter = 0;
            if (_rateWindows.Count < 64) return;   // sweep earlier so a mid-raid map is reclaimed
            var stale = new List<string>();
            foreach (var kv in _rateWindows)
            {
                var q = kv.Value;
                while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
                if (q.Count == 0) stale.Add(kv.Key);
            }
            foreach (var k in stale) _rateWindows.Remove(k);
        }

        // ── Blocklist regex (P0 matchTimeout) ───────────────────────────────
        // Recompile the whole blocklist into Regex objects, each with a 100 ms match
        // timeout + IgnoreCase|CultureInvariant. An invalid pattern is logged once,
        // recorded for the panel's passive cue and skipped (never crashes config load,
        // never rejects the save). Called on load + on every config change.
        private void RebuildRegexes(AutomodConfig cfg)
        {
            var compiled = new List<Regex>();
            var invalid = new List<string>();
            var patterns = cfg.BlocklistRegex ?? new List<string>();
            foreach (var raw in patterns)
            {
                string pat = (raw ?? "").Trim();
                if (pat.Length == 0) continue;
                try
                {
                    compiled.Add(new Regex(pat,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)));
                }
                catch (ArgumentException ex)
                {
                    invalid.Add(pat);
                    WarnRegexOnce(pat, $"invalid pattern: {ex.Message}");
                }
            }
            _compiledRegexes = compiled;
            _invalidRegexPatterns = invalid;
        }

        // Timeout-safe matching. A RegexMatchTimeoutException (catastrophic backtracking)
        // is caught → treated as NO-MATCH + logged once → NEVER rethrown (a missing guard
        // here is a P0 chat-thread freeze).
        private bool RegexFired(string text, out string reason)
        {
            reason = "";
            var list = _compiledRegexes;
            for (int i = 0; i < list.Count; i++)
            {
                var rx = list[i];
                try
                {
                    if (rx.IsMatch(text))
                    {
                        reason = "matched a blocked pattern";
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    WarnRegexOnce(rx.ToString(), "match timed out (100 ms) — treated as no-match");
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("AutomodService", "regex match failed", ex);
                }
            }
            return false;
        }

        private void WarnRegexOnce(string pattern, string detail)
        {
            bool first;
            lock (_regexWarnGate) first = _regexWarned.Add(pattern ?? "");
            if (first)
                GlobalLogger.Log($"Automod blocklist regex /{pattern}/ — {detail}.",
                    "AutomodService", LogLevel.CriticalError);
        }

        // ── Escalation ladder ───────────────────────────────────────────────
        /// <summary>Resolves the configured action for a given strike count: strike N takes
        /// rung min(N-1, last), verbatim — a Delete rung resolves to Delete like every other
        /// action. Whether a delete can actually be carried out is a separate, RUNTIME
        /// question (does the message carry an id, does the connected Streamer.bot expose the
        /// platform's delete action) that this pure function cannot answer;
        /// <see cref="EvaluateAsync"/> asks <see cref="DeleteCapabilityProvider"/> and, on a
        /// no, degrades through <see cref="ResolveLadderWithoutDelete"/>.</summary>
        public static (AutomodAction Action, int DurationSeconds) ResolveLadder(AutomodConfig cfg, long strikes)
        {
            var ladder = cfg?.Ladder;
            if (ladder == null || ladder.Count == 0) return (AutomodAction.Warn, 0);
            var step = ladder[LadderIndex(ladder, strikes)];
            return (step.Action, step.DurationSeconds);
        }

        /// <summary>The rung a Delete step degrades to when the platform cannot delete right
        /// now: walk forward to the next non-Delete rung; if the ladder is Delete all the way
        /// down from there, clamp to the last rung and issue a timeout instead (its own
        /// duration when it carries one, else 600 s).
        ///
        /// Used at both refusal points: as the resolved action when the capability gate says
        /// no during the scan, and — carried on <see cref="AutomodDecision.FallbackAction"/>
        /// — as what the dispatcher issues if the capability lapses before the DoAction.
        ///
        /// This is the pre-capability-gate resolution preserved EXACTLY — up to 2026-08-03
        /// <see cref="ResolveLadder"/> did this unconditionally, because delete was believed
        /// non-functional on every platform. Keeping it byte-for-byte is what makes an
        /// install whose delete capability is absent behave identically to before.</summary>
        public static (AutomodAction Action, int DurationSeconds) ResolveLadderWithoutDelete(AutomodConfig cfg, long strikes)
        {
            var ladder = cfg?.Ladder;
            if (ladder == null || ladder.Count == 0) return (AutomodAction.Warn, 0);
            int idx = LadderIndex(ladder, strikes);
            while (idx < ladder.Count && ladder[idx].Action == AutomodAction.Delete) idx++;
            if (idx >= ladder.Count) idx = ladder.Count - 1;
            var step = ladder[idx];
            if (step.Action == AutomodAction.Delete)
                return (AutomodAction.Timeout, step.DurationSeconds > 0 ? step.DurationSeconds : 600);
            return (step.Action, step.DurationSeconds);
        }

        // Strike N → rung index min(N-1, last). Strikes below 1 are treated as 1 so a
        // caller that never bumped still lands on the first rung rather than going negative.
        private static int LadderIndex(IReadOnlyList<AutomodLadderStep> ladder, long strikes)
            => (int)Math.Min(Math.Max(strikes, 1) - 1, ladder.Count - 1);

        // ── Panel helpers ───────────────────────────────────────────────────
        /// <summary>Appends one audit row and notifies the UI (Activity).</summary>
        public async Task AppendLogAsync(AutomodLogEntry entry)
        {
            if (entry == null) return;
            try
            {
                await _db.LogAutomodAsync(entry.Time, entry.Name, entry.Platform, entry.Rule, entry.Action, entry.Detail)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("AutomodService", "AppendLogAsync failed", ex);
            }
            RaiseActivity();
        }

        /// <summary>Recent audit rows (newest first).</summary>
        public Task<List<AutomodLogEntry>> GetLogAsync(int limit = 100) => _db.GetAutomodLogAsync(limit);

        /// <summary>All strike rows (name, raw count, last_ts), highest first.</summary>
        public Task<List<(string Name, long Count, long LastTs)>> ListStrikesAsync() => _db.AutomodStrikesListAsync();

        /// <summary>Resets a user's strikes (manual Pardon). Also notifies the UI.</summary>
        public async Task PardonAsync(string name)
        {
            try { await _db.AutomodStrikePardonAsync(name ?? "").ConfigureAwait(false); }
            catch (Exception ex) { GlobalLogger.Error("AutomodService", "PardonAsync failed", ex); }
            RaiseActivity();
        }
    }
}
