using System;
using System.Collections.Generic;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.Core
{
    // The ONE description of which chat verbs each built-in pre-build tool answers
    // to, derived from a config object the CALLER supplies.
    //
    // ── Why a config parameter and not a service lookup ──────────────────────
    // A tool panel edits a WORKING copy of its config and saves it on a 400ms
    // debounce, so for up to 400ms the live service config and the config the
    // streamer is looking at disagree. A catalog that read
    // LoyaltyService.Instance.Config could therefore only ever show the panel its
    // own edit LATE — the streamer renames !points to !bank, the command list keeps
    // saying "!points" for a beat, and the one surface whose whole job is "what does
    // this tool answer to" is the one surface that lies about it. Passing the config
    // in means a panel shows its in-flight edit immediately, while a cross-tool
    // consumer (a reserved-verb check on another page) passes the live service
    // config and sees committed state. Both callers get the answer they actually
    // asked for from the same code.
    //
    // ── What this replaced ───────────────────────────────────────────────────
    // SoundboardViewModel.ReservedVerbOwner and PollsViewModel.ReservedVerbOwner were
    // two near-duplicate ~100-line hand-maintained tables of exactly this knowledge,
    // each covering a different PREFIX of the built-in dispatch order (Automod →
    // UserManagement → Loyalty → UserQueue → Counters → Quotes → SongRequest → Polls
    // → Ranks → Soundboard → CustomCommands), and each one drifting from the parsers
    // independently. Both are gone: they now call BuiltInCommandOrder.OwnerAhead,
    // which owns the dispatch ORDER (a separate fact from "which verbs does tool X
    // claim") and the LIVE-config reads a cross-tool question needs, and reaches the
    // methods below for the verbs themselves.
    //
    // ── The rules every method here obeys ────────────────────────────────────
    //  * Every verb goes through ChatVerb.Canonical, so what a panel renders is
    //    byte-for-byte what the parser compares — a configured "!points" shows as
    //    "points", exactly like it matches. Rendering the raw field instead is how a
    //    panel ends up printing "!!points".
    //  * A verb that canonicalizes to EMPTY is omitted entirely. ChatVerb.Matches
    //    returns false for an empty configured side, so a blank field is a command
    //    that provably cannot fire; listing it as a live command would be a claim the
    //    code does not support.
    //  * Enabled reports the tool's own PER-COMMAND enable, and nothing above it. The
    //    master toggle is the panel's own headline state — folding it in would make
    //    every row of a dormant tool read as individually switched off, which is not
    //    what the streamer set. The one thing that IS folded in is a gate that
    //    silences the built-in parser as a whole while leaving the tool running:
    //    Loyalty's Commands.AutoHandle. See each method for its exact composition.
    //  * Roles renders the role CHECKMARK set that gates that specific verb — not the
    //    tool's default set — because several tools gate read and write differently on
    //    the same word (Counters, Song Request's !volume).
    //  * ★ Label and Roles are PROSE and go through Localizer.T. Verb and Shape do NOT:
    //    the verb is the streamer's own configured word and the shape is a transcript of
    //    what gets typed into chat, so both are data rather than language. The split
    //    matters in one direction especially — the four synthesized Counters words
    //    ("set"+cmd, "reset"+cmd) and the "!" in a usage line are bytes a parser
    //    compares, and a translated one names a command that can never fire. When adding
    //    a row, ask whether the string is something we SAY about the command or something
    //    the streamer TYPES; only the first is localized.
    //
    // ── ★ THE CONTRACT THIS LIST DEPENDS ON, AND WHERE THE BURDEN SITS ───────
    // Every row is only as true as the parser behind it: a panel that renders the
    // CONFIGURED word while the provider compares a hard-coded literal is a lie the
    // moment the streamer renames the field, and it is a lie that looks fine on a
    // default install because the field's default IS the literal. So the invariant
    // is one-directional — the parser matches the config field through ChatVerb,
    // never a literal — and when the two disagree the parser is the bug, never this
    // file. Do not "fix" a mismatch by hard-coding a word back into a row here.
    //
    // Four verb groups matter because their config field is NEWER than the parser
    // that answers them; as of this file's addition each of these compared a literal
    // and read nothing from the field the panel now shows:
    //     ScriptManager.Automod.cs      IsPermitCommand → "permit"    (PermitCommand)
    //     ScriptManager.Loyalty.cs      Match("accept") / Match("decline")
    //                                                                 (Duel.Accept/DeclineCommand)
    //     ScriptManager.Loyalty.cs      tok[0] == "draw" / "cancel"   (Raffle.Draw/CancelSubCommand)
    //     UserManagementService.cs      Eq(sub, "next"|"pick"|"remove"|"clear")
    //                                                                 (Queue*SubCommand)
    // Those comparisons are re-pointed by the lanes that own those files; this
    // catalogue names the configured word from the start, because the whole point of
    // making the verbs visible is that the panel shows what the streamer set.
    //
    // There is deliberately NO All(...) composition. Every shape it could take needs
    // either a service lookup (which the first section rules out) or all ten configs
    // as parameters — and the one caller that genuinely wants every tool's verbs, the
    // reserved-verb check, needs to know WHICH tool owns the hit, so it wants the
    // per-tool lists anyway. Per-tool methods are the contract.

    /// <summary>
    /// One chat verb a built-in tool answers to, as a panel renders it.
    /// </summary>
    /// <param name="Verb">
    /// The canonical verb WITHOUT a leading '!' — the exact string the tool's parser
    /// compares a chat token against. For a SUB-verb (Loyalty's "raffle draw", the
    /// viewer queue's "queue next") this is the two-word phrase, both halves
    /// canonicalized: no parser compares that phrase as one token, but it is what the
    /// streamer types and what identifies the row.
    /// </param>
    /// <param name="Label">Human name for the row, e.g. "Balance", "Raffle — draw".</param>
    /// <param name="Enabled">
    /// The tool's own per-command enable for this verb; true when the tool has none.
    /// Never folds in the tool's master toggle — see the file header.
    /// </param>
    /// <param name="Roles">
    /// Short rendered role summary for THIS verb, e.g. "Everyone" / "Mod · Broadcaster".
    /// See <see cref="BuiltInCommandCatalog.DescribeRoles"/>.
    /// </param>
    /// <param name="Shape">
    /// Usage, e.g. "!points [user]", "!queue next", "!&lt;counter&gt;+". Always built
    /// from the same canonical verb as <paramref name="Verb"/>, so the two can never
    /// disagree about what to type.
    /// </param>
    public readonly record struct ToolCommandInfo(
        string Verb,
        string Label,
        bool Enabled,
        string Roles,
        string Shape);

    /// <summary>
    /// Which chat verbs each built-in pre-build tool answers to, derived from a config
    /// the caller passes in. See the comment block at the top of
    /// <c>BuiltInCommandCatalog.cs</c> for why the config is a parameter, what the
    /// Enabled / Roles columns mean, and which two existing methods this replaces.
    /// </summary>
    /// <remarks>
    /// <para><b>Callers.</b> The tool panels render their own working config through
    /// these methods (the <c>ChatCommands</c> property behind each page's
    /// <c>ToolCommandList</c>), and <see cref="BuiltInCommandOrder"/> reads OTHER tools'
    /// live configs through them to answer "does something ahead of me in the dispatch
    /// already own this word?". That second question is what the two hand-maintained
    /// copies in <c>SoundboardViewModel</c> and <c>PollsViewModel</c> used to answer, and
    /// it is deliberately NOT a method here: it additionally needs the built-in dispatch
    /// ORDER, which is a fact this catalog does not carry, and it must read live service
    /// config, which the first paragraph of the file header rules out for this type.</para>
    ///
    /// <para><b>Coverage.</b> Every method below covers the DERIVED and SUB-verb shapes
    /// as well as the plainly-configured ones, because those are precisely the ones no
    /// panel shows today: the four synthesized Counters shapes, the viewer queue's four
    /// moderator sub-verbs, Loyalty's raffle draw / cancel and duel accept / decline,
    /// Automod's !permit, and every Soundboard / Custom-Command alias.</para>
    /// </remarks>
    public static class BuiltInCommandCatalog
    {
        // ── Role summary ────────────────────────────────────────────────────
        //
        // Ten tools carry a role class (LoyaltyRoles / AutomodRoles / CounterRoles /
        // QuoteRoles / CommandRoles / SoundboardRoles / SongRoles / PollRoles /
        // RankRoles / QueueRoles). They are structurally identical — the same six
        // bools and the same Allows() — and share NO base type ON PURPOSE: each one is
        // serialized inside its own tool's JSON blob, and a shared base would couple
        // ten independent persisted schemas to one type's evolution. So the shared
        // rendering takes the six booleans rather than trying to unify the classes.

        // ── Why the role words come from the SAME keys the role rows use ─────
        //
        // The six role words below resolve through `panel.common.role.*`, which is
        // exactly what RoleCheckRow's own checkboxes render. That is not tidiness: the
        // role card and this command list sit ON THE SAME PAGE, one above the other,
        // so two independent keys carrying "Mod" would let a translator render the same
        // gate as two different words a few pixels apart. One key per role word makes
        // that state unreachable rather than merely unlikely.

        /// <summary>Rendered when a role set admits nobody at all. A property rather
        /// than a const because the word is looked up per call — the streamer can
        /// switch language while a panel is open.</summary>
        private static string RolesNobody => Localizer.T("hub.commands.role.nobody", "Nobody");

        /// <summary>
        /// The short role summary for one checkmark set — "Everyone", or the ticked
        /// roles joined with " · " in the models' own declaration order
        /// (Sub · VIP · Mod · Broadcaster · Regular).
        /// </summary>
        /// <remarks>
        /// <para><b>Everyone absorbs the rest, because <c>Allows</c> does.</b> Every
        /// tool's gate is <c>Everyone || (Subscriber &amp;&amp; isSub) || …</c>, so with
        /// Everyone ticked the other five cannot change a single verdict. Rendering
        /// "Everyone · Sub · Mod" would invite the streamer to reason about ticks that
        /// have no effect.</para>
        ///
        /// <para><b>Nothing ticked is "Nobody", not "Everyone".</b> An all-false set
        /// makes <c>Allows</c> return false for every chatter including the
        /// broadcaster, so the verb is unusable. That is a real state a streamer can
        /// save from a role row, and it needs to look wrong.</para>
        /// </remarks>
        public static string DescribeRoles(
            bool everyone, bool subscriber, bool vip, bool moderator, bool broadcaster, bool regular)
        {
            if (everyone) return Localizer.T("panel.common.role.everyone", "Everyone");

            var parts = new List<string>(5);
            if (subscriber) parts.Add(Localizer.T("panel.common.role.sub", "Sub"));
            if (vip) parts.Add(Localizer.T("panel.common.role.vip", "VIP"));
            if (moderator) parts.Add(Localizer.T("panel.common.role.mod", "Mod"));
            if (broadcaster) parts.Add(Localizer.T("panel.common.role.broadcaster", "Broadcaster"));
            if (regular) parts.Add(Localizer.T("panel.common.role.regular", "Regular"));
            // The " · " join stays a literal: it is punctuation, not a word, and every
            // locale this suite ships renders a role list the same way.
            return parts.Count == 0 ? RolesNobody : string.Join(" · ", parts);
        }

        // Per-type adapters onto DescribeRoles. A null set renders as "Nobody" — the
        // reading LOYALTY's gate gives it, since that is the one tool that never
        // normalizes its role sets and whose gate spells `roles != null &&
        // roles.Allows(…)`, i.e. a null there refuses everybody including the
        // broadcaster.
        //
        // Every OTHER tool substitutes a permissive default for a missing set instead
        // of refusing — eight of them at config load (`cfg.GetRoles ??= QuoteRoles.All()`
        // and its siblings), the viewer queue at the gate itself. So each call site
        // below passes that tool's OWN substitution as a `?? Default()` fallback rather
        // than letting the null reading through. It matters because a caller may hand
        // this catalogue a config that has NOT been through its service's Normalize —
        // a panel's freshly deserialized working copy is exactly that — and rendering
        // "Nobody" for a verb the runtime will happily let everyone use is the same
        // class of lie as printing the wrong word.
        //
        // The adapters nonetheless stay null-TOLERANT rather than demanding a non-null
        // argument: they sit one forgotten `??` away from every call site, and a
        // visibly wrong "Nobody" in one column beats a NullReferenceException that
        // takes the whole panel refresh down.
        private static string R(LoyaltyRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(AutomodRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(CounterRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(QuoteRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(CommandRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(SoundboardRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(SongRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(PollRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(RankRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        private static string R(QueueRoles? r) => r is null ? RolesNobody
            : DescribeRoles(r.Everyone, r.Subscriber, r.Vip, r.Moderator, r.Broadcaster, r.Regular);

        // ── Row builders ────────────────────────────────────────────────────
        //
        // Both helpers canonicalize the verb ONCE and build the usage shape from that
        // same canonical string, so the Verb column and the Shape column can never
        // disagree about what the streamer has to type. Both drop a row whose verb
        // canonicalizes to empty (see the file header).
        //
        // ── Why `args` is NOT localized, while `label` is ────────────────────
        // Every argument tail here ("<user> <amount>", "[count]", "<0-100>",
        // "<red|black|even|odd|high|low|number>") is part of the USAGE LINE, and the
        // usage line is a transcript of what the streamer types into Twitch chat. The
        // brackets are syntax, the pipe is alternation, and the words inside them name
        // chat tokens the parsers read positionally — a translated "<Benutzer>" would
        // describe a command shape that does not exist. Labels are prose ABOUT the
        // command and are localized; shapes are the command itself and are not. Same
        // reason the Verb column is untouched: it is the streamer's own configured word.

        /// <summary>Adds one top-level verb row. <paramref name="args"/> is the
        /// argument tail of the usage shape ("&lt;user&gt; &lt;amount&gt;"), empty for
        /// a bare command.</summary>
        private static void Add(
            List<ToolCommandInfo> into, string? verb, string label, bool enabled, string roles, string args = "")
        {
            string v = ChatVerb.Canonical(verb);
            if (v.Length == 0) return;
            into.Add(new ToolCommandInfo(v, label, enabled, roles,
                args.Length == 0 ? "!" + v : "!" + v + " " + args));
        }

        /// <summary>
        /// Adds one SUB-verb row — a second word typed after a parent command
        /// ("!raffle draw", "!queue next"). Both halves are canonicalized and joined
        /// with a single space; the row is dropped if either half is empty, because a
        /// sub-verb with no parent (or no word of its own) is unreachable.
        /// </summary>
        private static void AddSub(
            List<ToolCommandInfo> into, string? parentVerb, string? subVerb,
            string label, bool enabled, string roles, string args = "")
        {
            string p = ChatVerb.Canonical(parentVerb);
            string s = ChatVerb.Canonical(subVerb);
            if (p.Length == 0 || s.Length == 0) return;
            string phrase = p + " " + s;
            into.Add(new ToolCommandInfo(phrase, label, enabled, roles,
                args.Length == 0 ? "!" + phrase : "!" + phrase + " " + args));
        }

        /// <summary>
        /// One-line preview of a streamer-authored free-text field, for a row label.
        /// Returns "" for a blank field so the caller can supply its own
        /// "nothing configured" sentence rather than render an empty column.
        /// </summary>
        /// <remarks>
        /// Both halves are load-bearing against real stored values. The whitespace
        /// collapse is because a custom-command response may carry the Shift+Enter line
        /// breaks the editor stores verbatim (the service sends one chat message per
        /// line), and a label is ONE row of a table. The clip is because the field has
        /// no length limit anywhere — config, editor or transport — so an unclipped
        /// label would set the row height from a paragraph.
        /// </remarks>
        private static string Preview(string? raw, int maxChars = 60)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string flat = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= maxChars ? flat : flat.Substring(0, maxChars).TrimEnd() + "…";
        }

        // ── Loyalty ─────────────────────────────────────────────────────────

        /// <summary>
        /// Loyalty's nineteen chat verbs: the nine economy commands, the five
        /// minigames, the raffle's join verb and its two management sub-verbs, and the
        /// duel's two replies.
        /// </summary>
        /// <remarks>
        /// <para><b>Enabled folds in <c>Commands.AutoHandle</c>.</b> That switch is the
        /// gate over the whole built-in parser — with it off, <c>ScriptManager</c>'s
        /// Loyalty handler returns before reaching a single verb, games included, while
        /// the observe-only chat tap (identity map, first-activity bonus) keeps
        /// running. So a row shown as live while AutoHandle is off would name a command
        /// that cannot fire. The tool's MASTER toggle is deliberately not folded in —
        /// see the file header.</para>
        ///
        /// <para><b>No <c>?? Default()</c> fallbacks here, unlike every other method.</b>
        /// Loyalty is the one tool with no role-set normalization anywhere —
        /// <c>LoyaltyService</c> has no Normalize pass for them and the dispatcher's
        /// gate is <c>roles != null &amp;&amp; roles.Allows(…)</c> — so a missing set
        /// really does refuse everybody, and "Nobody" is the honest rendering.</para>
        ///
        /// <para><b>accept / decline carry no role set.</b> The runtime gates them on
        /// nothing at all: only a viewer with a pending challenge can do anything with
        /// them, and they fall through untouched when there is none, precisely so they
        /// never hijack an unrelated !accept in a channel that uses the word. The Roles
        /// column therefore says who can really use them — the challenged viewer —
        /// rather than rendering a role set that does not exist.</para>
        /// </remarks>
        public static IReadOnlyList<ToolCommandInfo> ForLoyalty(LoyaltyConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>(20);
            if (cfg is null) return rows;

            var c = cfg.Commands;
            var g = cfg.Games;
            bool parser = c?.AutoHandle ?? false;

            if (c is not null)
            {
                Add(rows, c.Balance?.Trigger, Localizer.T("hub.commands.loyalty.balance", "Balance"), parser && (c.Balance?.Enabled ?? false), R(c.Balance?.Roles), "[user]");
                Add(rows, c.Give?.Trigger, Localizer.T("hub.commands.loyalty.give", "Give points"), parser && (c.Give?.Enabled ?? false), R(c.Give?.Roles), "<user> <amount>");
                Add(rows, c.Top?.Trigger, Localizer.T("hub.commands.loyalty.top", "Leaderboard"), parser && (c.Top?.Enabled ?? false), R(c.Top?.Roles), "[count]");
                Add(rows, c.Watchtime?.Trigger, Localizer.T("hub.commands.loyalty.watchtime", "Watch time"), parser && (c.Watchtime?.Enabled ?? false), R(c.Watchtime?.Roles));
                Add(rows, c.AddPoints?.Trigger, Localizer.T("hub.commands.loyalty.add_points", "Add points"), parser && (c.AddPoints?.Enabled ?? false), R(c.AddPoints?.Roles), "<user|all> <amount>");
                Add(rows, c.SetPoints?.Trigger, Localizer.T("hub.commands.loyalty.set_points", "Set points"), parser && (c.SetPoints?.Enabled ?? false), R(c.SetPoints?.Roles), "<user> <value>");
                Add(rows, c.RemovePoints?.Trigger, Localizer.T("hub.commands.loyalty.remove_points", "Remove points"), parser && (c.RemovePoints?.Enabled ?? false), R(c.RemovePoints?.Roles), "<user> <amount>");
                Add(rows, c.Wipe?.Trigger, Localizer.T("hub.commands.loyalty.wipe", "Wipe all balances"), parser && (c.Wipe?.Enabled ?? false), R(c.Wipe?.Roles));
                Add(rows, c.Redeem?.Trigger, Localizer.T("hub.commands.loyalty.redeem", "Redeem a reward"), parser && (c.Redeem?.Enabled ?? false), R(c.Redeem?.Roles), "<reward>");
            }

            if (g is null) return rows;

            // Gamble is the one game whose accepted STAKE SHAPES are configurable —
            // AllowAll / AllowPercent are real switches that make "!gamble all" and
            // "!gamble 50%" parse or not — so its usage is built from them. Slots and
            // roulette have no such switches and always take both shapes.
            Add(rows, g.Gamble?.Command, Localizer.T("hub.commands.loyalty.gamble", "Gamble"), parser && (g.Gamble?.Enabled ?? false), R(g.Gamble?.WhoCanPlay),
                StakeShape(g.Gamble?.AllowAll ?? false, g.Gamble?.AllowPercent ?? false));
            Add(rows, g.Slots?.Command, Localizer.T("hub.commands.loyalty.slots", "Slots"), parser && (g.Slots?.Enabled ?? false), R(g.Slots?.WhoCanPlay),
                StakeShape(allowAll: true, allowPercent: true));
            Add(rows, g.Roulette?.Command, Localizer.T("hub.commands.loyalty.roulette", "Roulette"), parser && (g.Roulette?.Enabled ?? false), R(g.Roulette?.WhoCanPlay),
                StakeShape(allowAll: true, allowPercent: true) + " <red|black|even|odd|high|low|number>");

            bool duelOn = parser && (g.Duel?.Enabled ?? false);
            Add(rows, g.Duel?.Command, Localizer.T("hub.commands.loyalty.duel", "Duel"), duelOn, R(g.Duel?.WhoCanPlay), "<user> <wager>");
            // "Challenged viewer" sits in the ROLES column, not the label column — it is
            // the one row whose gate is a sentence rather than a role set (see the
            // remarks above), so it is localized as role prose alongside the six words.
            Add(rows, g.Duel?.AcceptCommand, Localizer.T("hub.commands.loyalty.duel_accept", "Duel — accept"), duelOn,
                Localizer.T("hub.commands.role.challenged_viewer", "Challenged viewer"));
            Add(rows, g.Duel?.DeclineCommand, Localizer.T("hub.commands.loyalty.duel_decline", "Duel — decline"), duelOn,
                Localizer.T("hub.commands.role.challenged_viewer", "Challenged viewer"));

            // The raffle START verb and its two sub-verbs share WhoCanStart; JOIN is
            // the only raffle verb gated by WhoCanPlay.
            bool raffleOn = parser && (g.Raffle?.Enabled ?? false);
            Add(rows, g.Raffle?.Command, Localizer.T("hub.commands.loyalty.raffle_start", "Raffle — start"), raffleOn, R(g.Raffle?.WhoCanStart), "[winners] [duration] [fee]");
            AddSub(rows, g.Raffle?.Command, g.Raffle?.DrawSubCommand, Localizer.T("hub.commands.loyalty.raffle_draw", "Raffle — draw"), raffleOn, R(g.Raffle?.WhoCanStart));
            AddSub(rows, g.Raffle?.Command, g.Raffle?.CancelSubCommand, Localizer.T("hub.commands.loyalty.raffle_cancel", "Raffle — cancel"), raffleOn, R(g.Raffle?.WhoCanStart));
            Add(rows, g.Raffle?.JoinCommand, Localizer.T("hub.commands.loyalty.raffle_join", "Raffle — join"), raffleOn, R(g.Raffle?.WhoCanPlay));

            return rows;
        }

        /// <summary>The stake argument a house game accepts, as its own switches
        /// describe it. Absolute amounts are always legal; percent and all-in are
        /// Gamble's two opt-outs.</summary>
        private static string StakeShape(bool allowAll, bool allowPercent)
        {
            if (allowPercent && allowAll) return "<amount|50%|all>";
            if (allowPercent) return "<amount|50%>";
            if (allowAll) return "<amount|all>";
            return "<amount>";
        }

        // ── Automod ─────────────────────────────────────────────────────────

        /// <summary>
        /// Automod's single chat verb — the link permit.
        /// </summary>
        /// <remarks>
        /// Enabled is <c>true</c> because the command has no per-command switch: while
        /// the tool is active, an authorized chatter's "!permit &lt;name&gt;" is
        /// answered and consumed regardless of which rules are on. It is deliberately
        /// NOT tied to <c>Links.Enabled</c> — the permit grants a grace window that
        /// only the links rule reads, but the command itself still runs and still
        /// replies with the links rule off, and claiming otherwise here would describe
        /// a gate the runtime does not apply.
        /// </remarks>
        public static IReadOnlyList<ToolCommandInfo> ForAutomod(AutomodConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>(1);
            if (cfg is null) return rows;
            Add(rows, cfg.PermitCommand, Localizer.T("hub.commands.automod.permit", "Permit a link"), true,
                R(cfg.PermitRoles ?? AutomodRoles.Mods()), "<user>");
            return rows;
        }

        // ── Counters ────────────────────────────────────────────────────────

        /// <summary>
        /// Every counter's FIVE chat shapes: the bare read plus the four synthesized
        /// forms the parser builds by concatenation — <c>&lt;cmd&gt;+</c>,
        /// <c>&lt;cmd&gt;-</c>, <c>set&lt;cmd&gt;</c>, <c>reset&lt;cmd&gt;</c>.
        /// </summary>
        /// <remarks>
        /// <para>The four synthesized words are built off
        /// <c>ChatVerb.Canonical(EffectiveCommand)</c>, exactly as
        /// <c>ScriptManager.Counters</c> builds them — canonicalizing at the SOURCE
        /// rather than only at the comparison is what stops a configured "!deaths" from
        /// producing "set!deaths", a token that matches nothing.</para>
        ///
        /// <para>Read is gated by <c>ViewRoles</c> and all four modify shapes by
        /// <c>ModifyRoles</c>, which is why one counter yields rows with two different
        /// role summaries.</para>
        /// </remarks>
        public static IReadOnlyList<ToolCommandInfo> ForCounters(CountersConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>();
            if (cfg?.Counters is null) return rows;

            foreach (var def in cfg.Counters)
            {
                if (def is null) continue;
                string cmd = ChatVerb.Canonical(def.EffectiveCommand);
                if (cmd.Length == 0) continue;

                // The row's human name: the counter's own key when it has one (that is
                // the databank row it reads), else the command word it answers to.
                string name = string.IsNullOrWhiteSpace(def.Name) ? cmd : def.Name.Trim();
                string view = R(def.ViewRoles ?? CounterRoles.All());
                string modify = R(def.ModifyRoles ?? CounterRoles.Mods());

                // ★ The "set"/"reset" PREFIXES stay English literals. They are not words
                // on a label — they are the exact bytes ScriptManager.Counters
                // concatenates to build the token it compares, so translating them here
                // would print a command the parser can never match. Only the label
                // suffixes below are prose, and each one is a format so a translator can
                // move the counter's own name (streamer data, never translated).
                Add(rows, cmd, string.Format(Localizer.T("hub.commands.counters.read_format", "{0} — read"), name), def.ChatEnabled, view);
                Add(rows, cmd + "+", string.Format(Localizer.T("hub.commands.counters.step_up_format", "{0} — step up"), name), def.ChatEnabled, modify);
                Add(rows, cmd + "-", string.Format(Localizer.T("hub.commands.counters.step_down_format", "{0} — step down"), name), def.ChatEnabled, modify);
                Add(rows, "set" + cmd, string.Format(Localizer.T("hub.commands.counters.set_format", "{0} — set"), name), def.ChatEnabled, modify, "<number>");
                Add(rows, "reset" + cmd, string.Format(Localizer.T("hub.commands.counters.reset_format", "{0} — reset"), name), def.ChatEnabled, modify);
            }

            return rows;
        }

        // ── Quotes ──────────────────────────────────────────────────────────

        /// <summary>Quotes' three verbs. None has a per-command switch, so every row
        /// reports Enabled — the tool's master toggle is the only gate, and that is the
        /// panel's own headline state.</summary>
        public static IReadOnlyList<ToolCommandInfo> ForQuotes(QuotesConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>(3);
            if (cfg is null) return rows;

            Add(rows, cfg.GetCommand, Localizer.T("hub.commands.quotes.get", "Read a quote"), true, R(cfg.GetRoles ?? QuoteRoles.All()), "[number]");
            Add(rows, cfg.AddCommand, Localizer.T("hub.commands.quotes.add", "Add a quote"), true, R(cfg.AddRoles ?? QuoteRoles.Mods()), "<name> <quote>");
            Add(rows, cfg.DeleteCommand, Localizer.T("hub.commands.quotes.delete", "Delete a quote"), true, R(cfg.DeleteRoles ?? QuoteRoles.Mods()), "<number>");
            return rows;
        }

        // ── Custom chat commands ────────────────────────────────────────────

        /// <summary>
        /// Every custom command's trigger word plus each of its aliases, which fire the
        /// same command and share its cooldown buckets.
        /// </summary>
        /// <remarks>
        /// <para>The usage shape is derived from the RESPONSE template: a response that
        /// substitutes <c>{args}</c> or <c>{target}</c> reads the text after the verb,
        /// so it is shown taking an OPTIONAL argument; one that does not is shown bare.
        /// That is read off the streamer's own template rather than guessed — a command
        /// whose response ignores the tail genuinely does not take one. The bracket is
        /// deliberate: <c>{target}</c> falls back to the chatter when no word follows,
        /// so neither token makes the argument mandatory.</para>
        ///
        /// <para>The token test is case-INSENSITIVE because
        /// <c>CustomCommandsService.RenderAsync</c> is — it lowercases every
        /// <c>{token}</c> before matching — so a template written with <c>{Args}</c>
        /// substitutes at runtime and must not read as argument-free here.</para>
        ///
        /// <para>The label is a one-line preview of that same response (see
        /// <see cref="Preview"/>), or "No response set" when the field is blank —
        /// which is a silent misconfiguration rather than an inert row, because the
        /// service consumes the word and replies nothing.</para>
        /// </remarks>
        public static IReadOnlyList<ToolCommandInfo> ForCustomCommands(CustomCommandsConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>();
            if (cfg?.Commands is null) return rows;

            foreach (var cmd in cfg.Commands)
            {
                if (cmd is null) continue;
                string trigger = ChatVerb.Canonical(cmd.Trigger);
                if (trigger.Length == 0) continue;

                string roles = R(cmd.Roles ?? CommandRoles.All());
                string response = cmd.Response ?? string.Empty;
                bool readsTail =
                    response.IndexOf("{args}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    response.IndexOf("{target}", StringComparison.OrdinalIgnoreCase) >= 0;
                string args = readsTail ? "[text]" : string.Empty;

                // The label previews the RESPONSE, for the same reason the Soundboard
                // rows name the clip: it is the one thing about the row the verb column
                // cannot tell you, and a row with NO response is a real, silent
                // misconfiguration worth seeing — the service consumes the word and
                // returns handled either way (a blank render splits into zero lines and
                // sends nothing), so the command looks alive in chat while the channel
                // hears nothing back. The label deliberately never repeats the trigger;
                // an alias row names its parent instead, which is the one fact the verb
                // column cannot show.
                //
                // The preview itself is the STREAMER'S OWN response text and is never
                // translated — only the "nothing configured" sentence that stands in for
                // it is prose of ours.
                string preview = Preview(cmd.Response);
                Add(rows, trigger,
                    preview.Length == 0
                        ? Localizer.T("hub.commands.customcommands.no_response", "No response set")
                        : preview,
                    cmd.Enabled, roles, args);
                if (cmd.Aliases is null) continue;
                foreach (var alias in cmd.Aliases)
                    Add(rows, alias,
                        string.Format(Localizer.T("hub.commands.alias_of_format", "Alias of !{0}"), trigger),
                        cmd.Enabled, roles, args);
            }

            return rows;
        }

        // ── Song Request ────────────────────────────────────────────────────

        /// <summary>
        /// Song Request's twelve command words — thirteen rows, because
        /// <c>!volume</c> is gated TWICE on the same word: reading the level is a
        /// ViewRoles question and setting it is a ModRoles one, decided by whether a
        /// number followed. Two rows is the only honest rendering of one word with two
        /// gates.
        /// </summary>
        /// <remarks>
        /// No verb here carries a per-command switch, so every row reports Enabled. The
        /// tool's master toggle is the panel's headline state, and the caps / price /
        /// approval settings change what a verb DOES rather than whether it answers.
        /// </remarks>
        public static IReadOnlyList<ToolCommandInfo> ForSongRequest(SongRequestConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>(13);
            if (cfg is null) return rows;

            string view = R(cfg.ViewRoles ?? SongRoles.All());
            string request = R(cfg.RequestRoles ?? SongRoles.All());
            string mod = R(cfg.ModRoles ?? SongRoles.Mods());

            Add(rows, cfg.RequestCommand, Localizer.T("hub.commands.songrequest.request", "Request a song"), true, request, "<link | video id | search words>");
            Add(rows, cfg.CurrentCommand, Localizer.T("hub.commands.songrequest.current", "Now playing"), true, view);
            Add(rows, cfg.NextCommand, Localizer.T("hub.commands.songrequest.next", "Up next"), true, view);
            Add(rows, cfg.WhenCommand, Localizer.T("hub.commands.songrequest.when", "My place in the queue"), true, view);
            // !wrongsong drops the caller's OWN last request, so it rides the request
            // gate rather than the read gate — asking is not the same right as undoing.
            Add(rows, cfg.WrongSongCommand, Localizer.T("hub.commands.songrequest.wrongsong", "Drop my last request"), true, request);
            Add(rows, cfg.VoteSkipCommand, Localizer.T("hub.commands.songrequest.voteskip", "Vote to skip"), true, R(cfg.VoteSkipRoles ?? SongRoles.All()));
            Add(rows, cfg.VolumeCommand, Localizer.T("hub.commands.songrequest.volume_read", "Volume — read"), true, view);
            Add(rows, cfg.VolumeCommand, Localizer.T("hub.commands.songrequest.volume_set", "Volume — set"), true, mod, "<0-100>");
            Add(rows, cfg.SkipCommand, Localizer.T("hub.commands.songrequest.skip", "Skip the current track"), true, mod);
            Add(rows, cfg.PauseCommand, Localizer.T("hub.commands.songrequest.pause", "Pause"), true, mod);
            Add(rows, cfg.PlayCommand, Localizer.T("hub.commands.songrequest.play", "Play / resume"), true, mod);
            Add(rows, cfg.RemoveCommand, Localizer.T("hub.commands.songrequest.remove", "Remove a request"), true, mod, "<queue position>");
            Add(rows, cfg.ClearCommand, Localizer.T("hub.commands.songrequest.clear", "Clear the queue"), true, mod);
            return rows;
        }

        // ── Polls & Betting ─────────────────────────────────────────────────

        /// <summary>
        /// The poll's two verbs.
        /// </summary>
        /// <remarks>
        /// <c>!bet</c> reports <c>BettingEnabled</c> as its per-command enable. The verb
        /// is still parsed with betting off — but that switch decides whether a poll may
        /// be OPENED with betting at all, so with it off no poll can ever accept a
        /// stake and every !bet resolves to a BettingOff refusal. Reporting it as live
        /// would name a command that cannot do the thing it names.
        /// </remarks>
        public static IReadOnlyList<ToolCommandInfo> ForPolls(PollsConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>(2);
            if (cfg is null) return rows;

            Add(rows, cfg.VoteCommand, Localizer.T("hub.commands.polls.vote", "Vote"), true, R(cfg.VoteRoles ?? PollRoles.All()), "<number>");
            Add(rows, cfg.BetCommand, Localizer.T("hub.commands.polls.bet", "Bet on an outcome"), cfg.BettingEnabled,
                R(cfg.BetRoles ?? PollRoles.All()), "<number> <amount>");
            return rows;
        }

        // ── Ranks ───────────────────────────────────────────────────────────

        /// <summary>Ranks' two read commands. Both sit behind the tool's own
        /// <c>ChatEnabled</c> switch — a real per-command gate (it silences the verbs
        /// while the ladder, the grants and the rank-up announcements keep
        /// running).</summary>
        public static IReadOnlyList<ToolCommandInfo> ForRanks(RanksConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>(2);
            if (cfg is null) return rows;

            string view = R(cfg.ViewRoles ?? RankRoles.All());
            Add(rows, cfg.RankCommand, Localizer.T("hub.commands.ranks.rank", "My rank"), cfg.ChatEnabled, view, "[user]");
            Add(rows, cfg.TopCommand, Localizer.T("hub.commands.ranks.top", "Rank ladder"), cfg.ChatEnabled, view);
            return rows;
        }

        // ── Soundboard ──────────────────────────────────────────────────────

        /// <summary>
        /// Every sound row's command word plus each of its aliases, which play the
        /// same clip and share the row's cooldown buckets.
        /// </summary>
        /// <remarks>
        /// The label names the CLIP, because that is the one thing about a soundboard
        /// row the verb cannot tell you, and a row with no clip set is a real, silent
        /// misconfiguration worth seeing here — the word is consumed either way, so
        /// the command looks alive in chat while nothing plays.
        /// </remarks>
        public static IReadOnlyList<ToolCommandInfo> ForSoundboard(SoundboardConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>();
            if (cfg?.Sounds is null) return rows;

            foreach (var sound in cfg.Sounds)
            {
                if (sound is null) continue;
                string cmd = ChatVerb.Canonical(sound.Command);
                if (cmd.Length == 0) continue;

                string roles = R(sound.Roles ?? SoundboardRoles.All());
                string clip = (sound.ClipPath ?? string.Empty).Trim();
                // The clip PATH is the streamer's own file and is never translated; only
                // the sentence wrapped around it is.
                string label = clip.Length == 0
                    ? Localizer.T("hub.commands.soundboard.no_clip", "No clip set")
                    : string.Format(Localizer.T("hub.commands.soundboard.plays_format", "Plays {0}"), clip);

                Add(rows, cmd, label, sound.Enabled, roles);
                if (sound.Aliases is null) continue;
                foreach (var alias in sound.Aliases)
                    Add(rows, alias,
                        string.Format(Localizer.T("hub.commands.alias_of_format", "Alias of !{0}"), cmd),
                        sound.Enabled, roles);
            }

            return rows;
        }

        // ── User Management (the viewer queue) ──────────────────────────────

        /// <summary>
        /// The viewer queue's eight verbs: the four a viewer types, and the four
        /// moderator SUB-verbs that ride the list command ("!queue next").
        /// </summary>
        /// <remarks>
        /// <para>The four viewer verbs share ONE gate (<c>QueueJoinRoles</c>) because
        /// the runtime does — a line whose members may join but not see it is not a
        /// configuration anyone asked for. The four sub-verbs are gated by
        /// <c>QueueModRoles</c>.</para>
        ///
        /// <para>The null fallbacks below (<c>?? QueueRoles.All()</c> /
        /// <c>?? QueueRoles.Mods()</c>) mirror <c>TryHandleQueueChatAsync</c> exactly —
        /// this is the one tool that substitutes its default AT THE GATE rather than in
        /// a Normalize pass, so the same two expressions appear on both sides.</para>
        ///
        /// <para>Enabled reports <c>QueueEnabled</c>, the queue's own section gate —
        /// the closest thing these verbs have to a per-command switch. The tool's
        /// master toggle also silences them (<c>QueueChatActive</c> is
        /// <c>Enabled &amp;&amp; QueueEnabled</c>) and is, as everywhere here,
        /// the panel's headline state rather than a per-row fact.</para>
        ///
        /// <para>Nothing else in User Management appears: welcoming, the once-ever
        /// greeting and the group store answer no chat verb at all.</para>
        /// </remarks>
        public static IReadOnlyList<ToolCommandInfo> ForUserManagement(UserManagementConfig? cfg)
        {
            var rows = new List<ToolCommandInfo>(8);
            if (cfg is null) return rows;

            bool on = cfg.QueueEnabled;
            string viewer = R(cfg.QueueJoinRoles ?? QueueRoles.All());
            string mods = R(cfg.QueueModRoles ?? QueueRoles.Mods());
            string list = cfg.QueueListCommand;

            Add(rows, cfg.QueueJoinCommand, Localizer.T("hub.commands.usermgmt.queue_join", "Queue — join"), on, viewer);
            Add(rows, cfg.QueueLeaveCommand, Localizer.T("hub.commands.usermgmt.queue_leave", "Queue — leave"), on, viewer);
            Add(rows, list, Localizer.T("hub.commands.usermgmt.queue_list", "Queue — show the line"), on, viewer);
            Add(rows, cfg.QueuePositionCommand, Localizer.T("hub.commands.usermgmt.queue_position", "Queue — my position"), on, viewer);

            AddSub(rows, list, cfg.QueueNextSubCommand, Localizer.T("hub.commands.usermgmt.queue_next", "Queue — call the next viewer"), on, mods);
            AddSub(rows, list, cfg.QueuePickSubCommand, Localizer.T("hub.commands.usermgmt.queue_pick", "Queue — call a named viewer"), on, mods, "<user>");
            AddSub(rows, list, cfg.QueueRemoveSubCommand, Localizer.T("hub.commands.usermgmt.queue_remove", "Queue — drop a viewer"), on, mods, "<user>");
            AddSub(rows, list, cfg.QueueClearSubCommand, Localizer.T("hub.commands.usermgmt.queue_clear", "Queue — empty the line"), on, mods);
            return rows;
        }
    }
}
