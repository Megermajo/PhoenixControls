using System.Collections.Generic;

namespace Phoenix.Controls.Shared.Models
{
    // Data model for the Automod / Spam-filter pre-build tool. Mirrors the
    // Loyalty/Counters config doctrine: the whole AutomodConfig is serialized as
    // ONE JSON blob into the SYSTEM "AutomodConfig" table; the per-user offense
    // count lives in the OPEN "AutomodStrikes" table (so db.* scripts can build a
    // !warnings / !pardon command on it); the append-only "AutomodLog" audit is a
    // SYSTEM table (tool-internal, matches GiveawayActivity/TimerActivity). Nothing
    // here is enabled by default — the whole tool AND every rule ship OFF.

    /// <summary>
    /// Per-tool copy of the independent-checkmark role set (Everyone / Subscriber /
    /// VIP / Moderator / Broadcaster / Regular). Copied from CounterRoles/LoyaltyRoles
    /// so the serialized Automod config never couples to another tool's naming.
    /// Shared/Models is allowed; only Shared/UI is forbidden (per-pillar chrome rule).
    /// </summary>
    public sealed class AutomodRoles
    {
        public bool Everyone { get; set; }
        public bool Subscriber { get; set; }
        public bool Vip { get; set; }
        public bool Moderator { get; set; }
        public bool Broadcaster { get; set; }

        /// <summary>The User-Management Regular group — a community-trust tier with no
        /// platform equivalent. Added after the other five; the whole config is one JSON
        /// blob, so a blob written before this box existed simply lacks the key and
        /// deserializes to false — the streamer never opted in, the gate is unchanged.</summary>
        public bool Regular { get; set; }

        public AutomodRoles() { }
        public static AutomodRoles All() => new() { Everyone = true };
        public static AutomodRoles Mods() => new() { Moderator = true, Broadcaster = true };

        /// <summary>True when a chatter carrying these role flags matches the set:
        /// Everyone OR any individually-ticked role they hold.</summary>
        public bool Allows(bool isSub, bool isVip, bool isMod, bool isBroadcaster, bool isRegular)
            => Everyone
            || (Subscriber && isSub)
            || (Vip && isVip)
            || (Moderator && isMod)
            || (Broadcaster && isBroadcaster)
            || (Regular && isRegular);
    }

    /// <summary>The platform moderation verb a rule / ladder step issues. Delete is
    /// CAPABILITY-GATED: the Hub runtime issues it only when the message carries a platform
    /// id AND the connected Streamer.bot exposes that platform's delete action. When it
    /// cannot, a ladder Delete rung falls through to the next non-Delete rung and a fixed
    /// Delete rule takes no platform action (both logged once, and both REPORTED as what
    /// was actually issued — see <see cref="AutomodDecision"/>).
    ///
    /// ★ As this ships, the first half of that gate is never satisfied: no chat mapper in
    /// the Hub populates <c>ChatMessage.MessageId</c>, so Delete is dormant on every
    /// install regardless of which Streamer.bot actions are present. Adding the
    /// "Phoenix: Delete Message" action by hand does NOT light it up on its own.</summary>
    public enum AutomodAction
    {
        None,     // clean — no violation, or a violation whose action could not be issued
        Warn,     // post a chat warning, issue no platform action
        Delete,   // delete the offending message (capability-gated; see the enum doc)
        Timeout,  // time the user out for DurationSeconds
        Ban,      // permanently ban the user
    }

    /// <summary>How a rule chooses its action: follow the global escalation ladder
    /// (strike-count driven) or always issue one fixed action.</summary>
    public enum AutomodActionMode
    {
        Ladder,
        Fixed,
    }

    /// <summary>One rung of the global escalation ladder. Strike N resolves to the
    /// step at index min(N-1, last), verbatim. A Delete step is the one rung that can be
    /// declined at runtime (see <see cref="AutomodAction"/>) — at the capability gate when
    /// the scan runs, or at dispatch if the capability lapsed in between — in which case the
    /// ladder walks forward to the next non-Delete step for that message only.</summary>
    public sealed class AutomodLadderStep
    {
        public AutomodAction Action { get; set; } = AutomodAction.Warn;
        /// <summary>Timeout length in seconds (Timeout only; ignored otherwise).</summary>
        public int DurationSeconds { get; set; } = 600;

        public AutomodLadderStep() { }
        public AutomodLadderStep(AutomodAction action, int durationSeconds = 0)
        { Action = action; DurationSeconds = durationSeconds; }
    }

    /// <summary>Outcome of scanning one message. <see cref="Matched"/> false means the
    /// message was clean (or exempt / out of scope) and no action is taken.
    ///
    /// <see cref="Action"/> is the action the runtime INTENDS to issue. It is authoritative
    /// for every action except <see cref="AutomodAction.Delete"/>, which is the one verb the
    /// dispatch path can still decline (the capability can vanish between the scan and the
    /// DoAction — Streamer.bot may drop in that window). The action that was actually issued
    /// is what the audit row, the <c>Automod.OnViolation</c> event and the dry-run preview
    /// report; the dispatcher computes it and it can differ from this field only in the
    /// declined-Delete case.</summary>
    public readonly record struct AutomodDecision(
        bool Matched, string Rule, AutomodAction Action, int DurationSeconds, string Reason)
    {
        /// <summary>What to issue INSTEAD when <see cref="Action"/> is
        /// <see cref="AutomodAction.Delete"/> and the dispatch path declines it.
        ///
        /// A LADDER Delete carries the rung <c>ResolveLadderWithoutDelete</c> chose, so a
        /// capability that disappears mid-flight still lands the moderation the pre-gate
        /// build would have applied — losing it entirely would leave the viewer with NO
        /// moderation, which is a regression rather than a wash.
        ///
        /// A FIXED Delete carries <see cref="AutomodAction.None"/>: the rule names one
        /// action and one only, so there is no second choice, and substituting a punishment
        /// the streamer never configured would be worse than the rule not landing. Nothing
        /// is issued and nothing claims otherwise.
        ///
        /// Never <see cref="AutomodAction.Delete"/>, and the dispatcher normalises one away
        /// if it ever became so, because a declined delete must not re-enter the delete
        /// path.</summary>
        public AutomodAction FallbackAction { get; init; }

        /// <summary>Timeout length for <see cref="FallbackAction"/> when it is
        /// <see cref="AutomodAction.Timeout"/>; ignored otherwise.</summary>
        public int FallbackDurationSeconds { get; init; }

        public static readonly AutomodDecision None =
            new(false, "", AutomodAction.None, 0, "");

        public static AutomodDecision Hit(
            string rule, AutomodAction action, int durationSeconds, string reason,
            AutomodAction fallbackAction = AutomodAction.None, int fallbackDurationSeconds = 0)
            => new(true, rule ?? "", action, durationSeconds, reason ?? "")
            {
                FallbackAction = fallbackAction,
                FallbackDurationSeconds = fallbackDurationSeconds,
            };
    }

    // ── Per-rule config (each rule individually toggled + its own action mode) ──

    /// <summary>Fields shared by every rule: an independent enable sub-toggle
    /// (default OFF) and the per-rule action choice (default: use the global ladder).</summary>
    public abstract class AutomodRuleBase
    {
        /// <summary>Rule enable sub-toggle. Default OFF — nothing moderates until ticked.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Ladder (strike-driven escalation) or a single Fixed action.</summary>
        public AutomodActionMode ActionMode { get; set; } = AutomodActionMode.Ladder;

        /// <summary>Action issued when ActionMode == Fixed.</summary>
        public AutomodAction FixedAction { get; set; } = AutomodAction.Timeout;

        /// <summary>Timeout length (seconds) for a Fixed Timeout action.</summary>
        public int FixedDurationSeconds { get; set; } = 600;
    }

    /// <summary>Excessive-CAPS rule: only checks messages of at least MinLength
    /// letters, fires when the uppercase share of letters is >= Percent.</summary>
    public sealed class AutomodCapsRule : AutomodRuleBase
    {
        public int MinLength { get; set; } = 10;
        public int Percent { get; set; } = 70;
    }

    /// <summary>Maximum-message-length rule.</summary>
    public sealed class AutomodLengthRule : AutomodRuleBase
    {
        public int MaxLength { get; set; } = 350;
    }

    /// <summary>Repeated-character-run rule (N of the same character in a row).</summary>
    public sealed class AutomodRepeatRule : AutomodRuleBase
    {
        public int MaxRun { get; set; } = 8;
    }

    /// <summary>Symbol / emote spam rule: gated on MinLength, fires when the
    /// non-alphanumeric (symbol) share of the non-space characters is >= Percent.</summary>
    public sealed class AutomodSymbolRule : AutomodRuleBase
    {
        public int MinLength { get; set; } = 8;
        public int Percent { get; set; } = 60;
    }

    /// <summary>Link rule. When enabled and BlockAll is set, any URL not on the
    /// allow-list fires; otherwise only URLs whose domain hits the block-list fire.
    /// The allow/block domain lists and the permit grace are honored by the detector.</summary>
    public sealed class AutomodLinksRule : AutomodRuleBase
    {
        public bool BlockAll { get; set; } = true;
    }

    /// <summary>Blocklist words / phrases rule (case-insensitive; whole-word for a
    /// single-word term, substring for a multi-word phrase). Terms live on the
    /// config's BlocklistWords list.</summary>
    public sealed class AutomodWordsRule : AutomodRuleBase { }

    /// <summary>Blocklist regex rule (P0: every pattern compiles + matches with a
    /// 100 ms matchTimeout — a timeout is treated as no-match). Patterns live on the
    /// config's BlocklistRegex list.</summary>
    public sealed class AutomodRegexRule : AutomodRuleBase { }

    /// <summary>Rate / flood rule: fires when a user posts more than MaxMessages in
    /// the last WindowSeconds (per-user monotonic sliding window).</summary>
    public sealed class AutomodRateRule : AutomodRuleBase
    {
        public int MaxMessages { get; set; } = 5;
        public int WindowSeconds { get; set; } = 10;
    }

    /// <summary>Top-level Automod configuration — one serialized JSON blob in the
    /// SYSTEM AutomodConfig table. Everything ships OFF / empty.</summary>
    public sealed class AutomodConfig
    {
        /// <summary>Master gate. OFF by default — the provider predicate is false, so
        /// the whole tool is a total no-op until the streamer enables it.</summary>
        public bool Enabled { get; set; } = false;

        // ── Scope (which platforms Automod acts on) ─────────────────────────
        public bool ScopeTwitch { get; set; } = true;
        public bool ScopeYouTube { get; set; } = false;
        public bool ScopeKick { get; set; } = false;

        // ── Exemptions / behaviour ──────────────────────────────────────────
        /// <summary>Chatters holding a ticked role skip ALL rules. Default {Mod,Broadcaster}.</summary>
        public AutomodRoles ExemptRoles { get; set; } = AutomodRoles.Mods();

        /// <summary>Log the intended action instead of dispatching it. Default OFF.</summary>
        public bool DryRun { get; set; } = false;

        /// <summary>When a message is moderated, also skip the author on_chat script
        /// fan-out for it. Default ON.</summary>
        public bool SuppressDispatchWhenModerated { get; set; } = true;

        // ── !permit ─────────────────────────────────────────────────────────
        /// <summary>The word that grants a viewer a link pass ("!permit &lt;name&gt;").
        /// It was a hard-coded literal, so it was the one built-in command in the
        /// suite a streamer could neither see in a panel nor move — awkward on a
        /// channel whose bot already answers to it. Stored without the leading '!'
        /// like every other configured verb; a typed '!' is stripped at match
        /// time.</summary>
        public string PermitCommand { get; set; } = "permit";

        /// <summary>Grace window (seconds) a !permit grants for the LINKS rule. Default 60.</summary>
        public int PermitSeconds { get; set; } = 60;

        /// <summary>Who may issue !permit. Default {Mod,Broadcaster}.</summary>
        public AutomodRoles PermitRoles { get; set; } = AutomodRoles.Mods();

        // ── Escalation ──────────────────────────────────────────────────────
        /// <summary>Hours of no offenses that decays one strike (0 = never decays). Default 24.</summary>
        public double StrikeDecayHours { get; set; } = 24;

        /// <summary>Ordered escalation ladder. Strike N → step min(N-1, last).</summary>
        public List<AutomodLadderStep> Ladder { get; set; } = DefaultLadder();

        // ── Rules ───────────────────────────────────────────────────────────
        public AutomodCapsRule Caps { get; set; } = new();
        public AutomodLengthRule Length { get; set; } = new();
        public AutomodRepeatRule Repeat { get; set; } = new();
        public AutomodSymbolRule Symbols { get; set; } = new();
        public AutomodLinksRule Links { get; set; } = new();
        public AutomodWordsRule Words { get; set; } = new();
        public AutomodRegexRule Regex { get; set; } = new();
        public AutomodRateRule Rate { get; set; } = new();

        // ── Shared lists (edited on the Lists tab; verbatim, numeric-range-free) ──
        public List<string> UrlAllowList { get; set; } = new();
        public List<string> UrlBlockList { get; set; } = new();
        public List<string> BlocklistWords { get; set; } = new();
        public List<string> BlocklistRegex { get; set; } = new();

        /// <summary>Ships the recommended warn → delete → timeout(escalating) → ban ladder.
        ///
        /// ★ The Delete rung STAYS, deliberately, even though NO install can currently act on
        /// it: the runtime stopped skipping it unconditionally on 2026-08-03 and started
        /// asking the capability gate instead, and that gate's first condition — the chat
        /// message carrying a platform id — is not met anywhere in this build, so strike 2
        /// still resolves to Timeout(600) exactly as it always did. Three reasons to keep it:
        /// the fallthrough makes the rung a no-op rather than a hole, so nothing changes for
        /// anyone; removing it here would not protect a single existing install anyway,
        /// because a streamer who has ever saved the tool's config carries their own
        /// persisted ladder and a new default never rewrites it; and if the capability ever
        /// arrives, the rung it lights up is MILDER than what it replaces — one message
        /// removed instead of a ten-minute mute — so that day cannot make an existing install
        /// punish harder. Silently rewriting a streamer's saved ladder would break the same
        /// rule worse.</summary>
        public static List<AutomodLadderStep> DefaultLadder() => new()
        {
            new AutomodLadderStep(AutomodAction.Warn),
            new AutomodLadderStep(AutomodAction.Delete),
            new AutomodLadderStep(AutomodAction.Timeout, 600),
            new AutomodLadderStep(AutomodAction.Timeout, 3600),
            new AutomodLadderStep(AutomodAction.Ban),
        };
    }

    /// <summary>One append-only AutomodLog audit row (SYSTEM table).</summary>
    public sealed class AutomodLogEntry
    {
        public long Id { get; set; }
        public string Time { get; set; } = "";
        public string Name { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Rule { get; set; } = "";
        public string Action { get; set; } = "";
        public string Detail { get; set; } = "";
    }
}
