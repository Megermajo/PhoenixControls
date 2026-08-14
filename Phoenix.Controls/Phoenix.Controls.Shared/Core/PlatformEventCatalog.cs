using System;
using System.Collections.Generic;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Core
{
    // Single source of truth for the DECLARATIVE event surface — YouTube, Kick,
    // the second-wave Twitch events, and the third-party donation brokers. One
    // PlatformEventDef per Streamer.bot event; consumed by:
    //   * NodeRegistry.Templates.Events.cs — one Architect template per entry
    //     (Flow output prepended by the registry; it is NOT listed here).
    //   * ScriptExporter — socket→token emission (VarToken) and the
    //     event-trigger roster check.
    //   * AutocompleteScopeBuilder / VarChainAnalyzer — var tokens contributed
    //     per event title.
    //   * ScriptManager.BuildGenericEventVars — payload extraction (Vars).
    // Chat events (YouTube.Message / Kick.ChatMessage) are handled by the
    // unified Chat.Message pipeline and deliberately do NOT appear here.
    //
    // Twitch appears here TWICE over, and the split is deliberate. The nine
    // first-wave Twitch event nodes (Subscription / Resub / GiftSub / GiftBomb /
    // Raid / Cheer / Follow / PointRedeem, plus Chat.Message) are hand-written
    // in NodeRegistry with bespoke probe code in ScriptManager, and stay that
    // way — they are behavior-frozen and carry per-event quirks (anonymous-gift
    // handling, resub→sub fan-out) that no declarative shape expresses. Every
    // Twitch event added AFTER that wave lives here instead, because a Def()
    // entry mints the node, the exporter tokens, the autocomplete/var-chain
    // entries and the payload extraction in one line. The two sets are visually
    // distinguishable on the canvas: hand-written = ForestGreen headers,
    // catalog-driven = MediumPurple.

    /// <summary>
    /// How a <see cref="PlatformEventVar"/> probe list is evaluated against the
    /// event's <c>data</c> JSON object.
    ///
    /// Probes may be dotted paths (e.g. <c>"reward.title"</c>, <c>"kicks.amount"</c>,
    /// <c>"broadcast.title"</c>): the extractor walks nested objects segment by
    /// segment, then falls back to trying the full dotted string as a literal
    /// property key on <c>data</c>.
    /// </summary>
    public enum PlatformProbeKind
    {
        /// <summary>First non-empty string among the probes.</summary>
        Str,

        /// <summary>First parseable integer among the probes (accepts a JSON number or a numeric string).</summary>
        Int,

        /// <summary>First parseable bool among the probes.</summary>
        Bool,

        /// <summary>
        /// Reads <c>data.user.&lt;probe&gt;</c> as a string — YouTube nests the
        /// acting user in a <c>user</c> object — falling back to the flat probe
        /// name directly on <c>data</c>.
        /// </summary>
        NestedUserStr,

        /// <summary>Bool variant of <see cref="NestedUserStr"/>.</summary>
        NestedUserBool,

        /// <summary>
        /// JSON array at the probe → comma-joined string. Elements that are
        /// strings join as-is; elements that are objects contribute their
        /// <c>name</c> / <c>userName</c> / <c>login</c> property (first present),
        /// otherwise the element is skipped.
        /// </summary>
        JoinArray,
    }

    /// <summary>
    /// One data socket on the event node. <paramref name="VarToken"/> is the
    /// exporter token WITHOUT braces (e.g. <c>user.name</c>, <c>event.amount</c>).
    /// </summary>
    public sealed record PlatformEventSocket(string Name, SocketDataType Type, string VarToken);

    /// <summary>
    /// One payload-extraction rule. <paramref name="VarName"/> equals the
    /// owning socket's <see cref="PlatformEventSocket.VarToken"/>.
    /// </summary>
    public sealed record PlatformEventVar(string VarName, string[] Probes, PlatformProbeKind Kind);

    /// <summary>One platform event: node template, exporter tokens, and payload extraction.</summary>
    public sealed record PlatformEventDef(
        string Title,
        string Platform,
        IReadOnlyList<PlatformEventSocket> Sockets,
        IReadOnlyList<PlatformEventVar> Vars,
        string BubbleLocKey,
        string? VersionNote);

    /// <summary>
    /// The catalog: 30 YouTube + 20 Kick + 34 Twitch + 24 donation-broker
    /// definitions. Titles equal the normalized Streamer.bot EventType (the
    /// wire name <c>Kick.sGifted</c> is normalized to <c>Kick.KicksGifted</c>
    /// in WS before lookup).
    /// </summary>
    public static class PlatformEventCatalog
    {
        private const string YouTubePlatform = "youtube";
        private const string KickPlatform    = "kick";
        private const string TwitchPlatform  = "twitch";

        // The ten third-party money brokers. The token is always the LOWERCASED
        // title prefix, because that is what ScriptManager derives at runtime
        // (eventType.Substring(0, IndexOf('.'))) — a token that disagreed with
        // the prefix would mislabel {user.platform} on every var of the event.
        // The prefixes themselves are the LIVE Streamer.bot 1.0.4 source names,
        // not the marketing spellings: "Kofi" (not "Ko-Fi") and "Pallygg" (not
        // "Pally.gg"); DonationIngest.DonationSources holds the same ten.
        private const string StreamlabsPlatform     = "streamlabs";
        private const string StreamElementsPlatform = "streamelements";
        private const string KofiPlatform           = "kofi";
        private const string PatreonPlatform        = "patreon";
        private const string TipeeeStreamPlatform   = "tipeeestream";
        private const string TreatStreamPlatform    = "treatstream";
        private const string DonorDrivePlatform     = "donordrive";
        private const string FourthwallPlatform     = "fourthwall";
        private const string PallyggPlatform        = "pallygg";
        private const string ShopifyPlatform        = "shopify";

        // Must be initialized BEFORE Events (static initializers run in textual
        // order): BuildCatalog() reaches this array through KickUser()/KickGifter(),
        // and declared-after-Events it would still be null at build time — the vars
        // would silently mint with null probes and the resolver would NRE (caught
        // + swallowed downstream), stripping every Kick actor-bearing event's vars.
        // Kick actor fields are flat on data.
        private static readonly string[] KickActorProbes = { "user", "userName", "displayName" };

        // Same ordering constraint as KickActorProbes above — these three feed
        // BuildCatalog() through factory methods and MUST stay declared before
        // the Events field.
        //
        // Twitch's WebSocket payloads carry the acting user flat on data under
        // one of three spellings depending on which EventSub topic SB relayed.
        private static readonly string[] TwitchActorProbes = { "user", "userName", "displayName" };

        // Ban/timeout is the one Twitch family whose WebSocket schema IS
        // documented, and it nests both parties in objects. Nested first, flat
        // second: the trigger-variable surface flattens the same data into bare
        // keys, and the resolver walks a dotted probe before trying it literally.
        private static readonly string[] TwitchBanTargetProbes = { "targetUser.login", "targetUser.name", "userName", "user" };
        private static readonly string[] TwitchModeratorProbes = { "moderator.login", "moderator.name", "createdByUsername" };

        /// <summary>All catalog entries, YouTube first, in spec order.</summary>
        public static readonly IReadOnlyList<PlatformEventDef> Events = BuildCatalog();

        /// <summary>
        /// WIRE name → catalog title, for the entries whose node title had to differ
        /// from the event name Streamer.bot actually sends.
        ///
        /// <para>The index is keyed on <c>def.Title</c> because that is what a NODE is
        /// called, but <c>BuildGenericEventVars</c> looks the definition up by the RAW
        /// EVENT TYPE off the wire. For every other entry those are the same string and
        /// it works by coincidence of naming. Where they were forced apart, the lookup
        /// returned null and the node's pins stayed permanently empty with nothing
        /// logged anywhere — the node existed and the script even ran, the vars just
        /// never resolved.</para>
        ///
        /// <para>Aliasing rather than renaming keeps the node title stable:
        /// <c>Twitch.Announcement</c> is already the hand-written ACTION node that SENDS
        /// an announcement, so the trigger cannot have that name. Same shape as the
        /// <c>matchType</c> alias ScriptManager applies one layer up for script
        /// SELECTION — that half already worked, which is exactly why the var half
        /// looked fine from the outside.</para>
        ///
        /// <para>★ Declared HERE, above <c>_byTitle</c>, and it has to stay above it:
        /// static field initializers run in TEXTUAL order, so an alias table declared
        /// below would still be null when <see cref="BuildIndex"/> reads it — a
        /// <c>TypeInitializationException</c> that takes out every consumer of the
        /// catalog at once. Same rule the <c>_byTitle</c>-after-<c>Events</c> note
        /// below records.</para>
        /// </summary>
        private static readonly (string Wire, string Title)[] WireNameAliases =
        {
            ("Twitch.Announcement", "Twitch.AnnouncementReceived"),
        };

        // Must be initialized AFTER Events AND after WireNameAliases (static
        // initializers run in textual order). Add() throws on a duplicate title, so a
        // copy-paste collision fails fast at type-init instead of shadowing an entry.
        private static readonly Dictionary<string, PlatformEventDef> _byTitle = BuildIndex();

        /// <summary>
        /// Case-insensitive lookup by NODE TITLE; null when unknown.
        ///
        /// <para>★ Title-only, deliberately. Consumers that key on a node's title —
        /// <c>AutocompleteScopeBuilder.Contribute</c> walks every upstream node, not just
        /// event roots — must not be handed a definition for a node that merely SHARES a
        /// name with an event. <c>Twitch.Announcement</c> is the concrete case: it is the
        /// hand-written Platforms ACTION node that SENDS an announcement, and resolving it
        /// here would offer its author <c>{user.message}</c> / <c>{event.color}</c>
        /// tokens the send never binds. Wire names live in
        /// <see cref="FindForEvent"/>.</para>
        /// </summary>
        public static PlatformEventDef? Find(string title)
            => !string.IsNullOrEmpty(title) && _byTitle.TryGetValue(title, out var def) ? def : null;

        /// <summary>
        /// Lookup by the RAW EVENT TYPE off the wire — <see cref="Find"/> plus the
        /// wire-name aliases. This is what runtime var resolution must call.
        ///
        /// <para>Separate from <see cref="Find"/> because the two questions differ for
        /// exactly the entries the aliases exist for: "what does a node called X produce"
        /// is not "what does an event called X carry", and conflating them let an alias
        /// reach a same-named action node in Architect's autocomplete.</para>
        /// </summary>
        public static PlatformEventDef? FindForEvent(string eventType)
        {
            if (string.IsNullOrEmpty(eventType)) return null;
            if (_byTitle.TryGetValue(eventType, out var def)) return def;

            foreach (var (wire, title) in WireNameAliases)
                if (string.Equals(wire, eventType, StringComparison.OrdinalIgnoreCase))
                    return _byTitle.TryGetValue(title, out var aliased) ? aliased : null;

            return null;
        }

        // ---- construction helpers -------------------------------------------

        // Socket-name → exporter-token rule. The user.* names below are the
        // EXISTING generic-exporter mappings (ScriptExporter socket switch) and
        // must stay aligned with them; every other socket falls through to
        // "event.<name lowercased>", which is also what BuildGenericEventVars
        // injects. "Payload" resolves to "event.payload" via the default arm.
        private static string TokenFor(string socketName) => socketName switch
        {
            "User"      => "user.name",
            "Message"   => "user.message",
            "Gifter"    => "user.gifter",
            "Recipient" => "user.recipient",
            "Months"    => "user.sub_months",
            "Count"     => "user.count",
            "Tier"      => "user.tier",
            "Reward"    => "user.reward",
            "Input"     => "user.input",
            "Viewers"   => "user.viewers",

            // The canonical-money tokens DonationIngest.NormalizeMoneyVars
            // writes. They need explicit arms because the default lower-cases
            // without inserting the separator ("AmountCents" would become
            // event.amountcents, which no consumer reads). Source/IsTest have
            // no socket today — DonationIngest still writes both tokens on
            // every money event, so the arms are here to keep socket→token
            // agreement automatic if a broker entry ever exposes the pin.
            "AmountCents" => "event.amount_cents",
            "IsTest"      => "event.is_test",
            "TxId"        => "event.tx_id",
            "Source"      => "event.source",

            _           => "event." + socketName.ToLowerInvariant(),
        };

        // Socket factories, one per spec type letter. Numeric sockets carry
        // SocketDataType.Int — Architect's DataTypeFromColor(ColNumber) maps
        // the number color to Int, and the registry derives the socket color
        // back from this type.
        private static PlatformEventSocket S(string name) => new(name, SocketDataType.String,     TokenFor(name));
        private static PlatformEventSocket N(string name) => new(name, SocketDataType.Int,        TokenFor(name));
        private static PlatformEventSocket B(string name) => new(name, SocketDataType.Bool,       TokenFor(name));
        private static PlatformEventSocket L(string name) => new(name, SocketDataType.Collection, TokenFor(name));

        // Var factories keyed by socket name so VarName == the socket's VarToken
        // by construction.
        private static PlatformEventVar Str(string socketName, params string[] probes)  => new(TokenFor(socketName), probes, PlatformProbeKind.Str);
        private static PlatformEventVar Int(string socketName, params string[] probes)  => new(TokenFor(socketName), probes, PlatformProbeKind.Int);
        private static PlatformEventVar Bool(string socketName, params string[] probes) => new(TokenFor(socketName), probes, PlatformProbeKind.Bool);
        private static PlatformEventVar Join(string socketName, params string[] probes) => new(TokenFor(socketName), probes, PlatformProbeKind.JoinArray);

        /// <summary>YouTube actor: nested <c>data.user.name</c> first, flat fallbacks after.</summary>
        private static PlatformEventVar YtUser()
            => new("user.name", new[] { "name", "displayName", "user" }, PlatformProbeKind.NestedUserStr);

        // KickActorProbes lives ABOVE the Events field — see the ordering note there.
        private static PlatformEventVar KickUser()   => new("user.name",   KickActorProbes, PlatformProbeKind.Str);
        private static PlatformEventVar KickGifter() => new("user.gifter", KickActorProbes, PlatformProbeKind.Str);

        // No var entries are authored for "user.platform" or "event.payload":
        // the runtime injects both universally for every catalog event
        // (platform from eventSource; payload = raw JSON of data). Payload
        // SOCKETS still appear below so thin nodes expose the pin.
        private static PlatformEventDef Def(
            string title,
            string platform,
            PlatformEventSocket[] sockets,
            PlatformEventVar[] vars,
            string? versionNote = null)
            => new(
                title,
                platform,
                sockets,
                vars,
                "architect.node.bubble." + title.ToLowerInvariant().Replace('.', '_'),
                versionNote);

        // The five Broadcast* lifecycle events share one shape; probes are
        // nested (data.broadcast.*) with flat fallbacks.
        private static PlatformEventDef YtBroadcast(string title) => Def(
            title, YouTubePlatform,
            new[] { S("Title"), S("BroadcastId"), S("Privacy"), S("Status") },
            new[]
            {
                Str("Title",       "broadcast.title",   "title"),
                Str("BroadcastId", "broadcast.id",      "id"),
                Str("Privacy",     "broadcast.privacy", "privacy"),
                Str("Status",      "broadcast.status",  "status"),
            });

        private static PlatformEventDef YtBroadcastMonitoring(string title) => Def(
            title, YouTubePlatform,
            new[] { S("Title"), S("BroadcastId"), S("Payload") },
            new[]
            {
                Str("Title",       "broadcast.title", "title"),
                Str("BroadcastId", "broadcast.id",    "id"),
            });

        // Poll schema is unpublished; probe both plausible title fields.
        private static PlatformEventDef YtPoll(string title) => Def(
            title, YouTubePlatform,
            new[] { S("Title"), S("Payload") },
            new[] { Str("Title", "title", "question") });

        private static PlatformEventDef EmoteEvent(string title, string platform) => Def(
            title, platform,
            new[] { S("EmoteName"), S("Payload") },
            new[] { Str("EmoteName", "name", "emoteName") });

        // ---- Twitch second-wave factories -----------------------------------

        // Charity Started/Progress/Completed are one shape: SB reports the
        // running total in charity.currentAmount and the campaign target in
        // charity.targetAmount. (CharityDonation is a different shape — a single
        // gift under charity.amount — and is written out below.)
        private static PlatformEventDef TwitchCharityProgress(string title) => Def(
            title, TwitchPlatform,
            new[] { S("CharityName"), S("Amount"), S("TargetAmount"), S("Currency"), S("Payload") },
            new[]
            {
                Str("CharityName",  "charity.name",                    "charityName"),
                Str("Amount",       "charity.currentAmount.value",     "amount"),
                Str("TargetAmount", "charity.targetAmount.value",      "targetAmount"),
                Str("Currency",     "charity.currentAmount.currency",  "currency"),
            });

        private static PlatformEventDef TwitchGoal(string title) => Def(
            title, TwitchPlatform,
            new[] { S("GoalId"), S("GoalType"), S("Description"), N("CurrentAmount"), N("TargetAmount"), S("Payload") },
            new[]
            {
                Str("GoalId",        "goal.id",            "id"),
                Str("GoalType",      "goal.type",          "type"),
                Str("Description",   "goal.description",   "description"),
                Int("CurrentAmount", "goal.currentAmount", "currentAmount"),
                Int("TargetAmount",  "goal.targetAmount",  "targetAmount"),
            });

        // Un-ban / un-timeout carry no reason and no duration — Twitch models
        // them as the reversal, not as an event of their own.
        private static PlatformEventDef TwitchModUndo(string title) => Def(
            title, TwitchPlatform,
            new[] { S("User"), S("Moderator"), S("Payload") },
            new[]
            {
                Str("User",      TwitchBanTargetProbes),
                Str("Moderator", TwitchModeratorProbes),
            });

        /// <summary>Role-grant / role-revoke events: the affected user and nothing else.</summary>
        private static PlatformEventDef TwitchUserOnly(string title) => Def(
            title, TwitchPlatform,
            new[] { S("User"), S("Payload") },
            new[] { Str("User", TwitchActorProbes) });

        private static PlatformEventDef TwitchPoll(string title) => Def(
            title, TwitchPlatform,
            new[] { S("Title"), S("PollId"), S("Payload") },
            new[]
            {
                Str("Title",  "title"),
                Str("PollId", "id"),
            });

        // WinningOutcome only carries a value on Completed; probing it on the
        // other four costs nothing (a miss leaves the var unset) and keeps one
        // node shape across the whole prediction lifecycle.
        private static PlatformEventDef TwitchPrediction(string title) => Def(
            title, TwitchPlatform,
            new[] { S("Title"), S("PredictionId"), S("WinningOutcome"), S("Payload") },
            new[]
            {
                Str("Title",          "title"),
                Str("PredictionId",   "id"),
                Str("WinningOutcome", "winningOutcome.title", "winningOutcomeId"),
            });

        // ---- donation-broker factories --------------------------------------

        // Every broker entry declares AmountCents with NO probe of its own:
        // DonationIngest.NormalizeMoneyVars runs after the catalog resolver and
        // rewrites event.amount / event.amount_cents / event.currency into one
        // invariant form, so the probes below are only the first pass. The
        // corollary is that Amount / Currency / User / TxId all get a second
        // chance from DonationIngest's broad candidate lists (plus its one-shot
        // "payload fields were […]" diagnostic on a total miss) — Message does
        // NOT, which is why it alone carries a generic fallback here.
        private static PlatformEventDef KofiMoney(string title) => Def(
            title, KofiPlatform,
            new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("Message"), S("TxId"), S("Payload") },
            new[]
            {
                Str("User",     "from"),
                Str("Amount",   "amount"),
                Str("Currency", "currency"),
                Str("Message",  "message"),
                Str("TxId",     "messageId"),
            });

        // Patreon reports US CENTS as an integer and publishes no currency at
        // all; DonationIngest reads the same field through its MinorUnitProbes
        // so the decimal never round-trips. Donor is nested and capital-A —
        // "user.Attributes.full_name" is verbatim, not a transcription slip.
        private static PlatformEventDef PatreonPledge(string title) => Def(
            title, PatreonPlatform,
            new[] { S("User"), S("Amount"), S("AmountCents"), S("TxId"), S("Payload") },
            new[]
            {
                Str("User",   "user.Attributes.full_name"),
                Str("Amount", "attributes.currently_entitled_amount_cents"),
                Str("TxId",   "id"),
            });

        // DonorDrive publishes no currency field and no transaction id — the
        // charity platform assumes one campaign currency.
        private static PlatformEventDef DonorDriveMoney(string title) => Def(
            title, DonorDrivePlatform,
            new[] { S("User"), S("Amount"), S("AmountCents"), S("Message"), S("Payload") },
            new[]
            {
                Str("User",    "donorName"),
                Str("Amount",  "donorAmount"),
                Str("Message", "donorMessage", "message"),
            });

        // The three non-donation Fourthwall purchases share a shape and differ
        // only in which id field the payload happens to use.
        private static PlatformEventDef FourthwallPurchase(
            string title, string donorProbe, string amountProbe, params string[] txIdProbes) => Def(
            title, FourthwallPlatform,
            new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("TxId"), S("Payload") },
            new[]
            {
                Str("User",     donorProbe),
                Str("Amount",   amountProbe),
                Str("Currency", "fw.currency"),
                Str("TxId",     txIdProbes),
            });

        private static PlatformEventDef ShopifyOrder(string title) => Def(
            title, ShopifyPlatform,
            new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("Message"), S("TxId"), S("Payload") },
            new[]
            {
                Str("User",     "shopify.name"),
                Str("Amount",   "shopify.total_price"),
                Str("Currency", "shopify.currency"),
                Str("Message",  "shopify.note"),
                Str("TxId",     "shopify.id"),
            });

        private static Dictionary<string, PlatformEventDef> BuildIndex()
        {
            var map = new Dictionary<string, PlatformEventDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in Events)
                map.Add(def.Title, def);

            // ★ Aliases are NOT folded into this index. It backs Find(), which answers
            // "what does a node with this TITLE produce", and a wire name that collides
            // with an unrelated node's title would hijack it there (see Find's remarks).
            // FindForEvent applies the alias table instead. What IS validated here is that
            // every alias points at a real entry — a typo would otherwise be a lookup that
            // silently returns null forever, which is the exact failure the alias exists
            // to fix.
            foreach (var (wire, title) in WireNameAliases)
            {
                if (!map.ContainsKey(title))
                    throw new InvalidOperationException(
                        $"PlatformEventCatalog: wire alias '{wire}' names catalog title '{title}', which does not exist.");
            }

            return map;
        }

        // ---- the catalog ----------------------------------------------------

        private static IReadOnlyList<PlatformEventDef> BuildCatalog() => new[]
        {
            // ---------------- YouTube (30; Message handled by Chat.Message) --

            Def("YouTube.SuperChat", YouTubePlatform,
                new[] { S("User"), S("Message"), S("Amount"), S("Currency"), S("Tier") },
                new[]
                {
                    YtUser(),
                    Str("Message",  "message"),
                    Str("Amount",   "amount"),
                    Str("Currency", "currencyCode"),
                    Str("Tier",     "tier"),
                }),

            Def("YouTube.SuperSticker", YouTubePlatform,
                new[] { S("User"), S("Amount"), S("Currency"), S("Tier"), S("StickerAlt"), S("StickerUrl") },
                new[]
                {
                    YtUser(),
                    Str("Amount",     "amount"),
                    Str("Currency",   "currencyCode"),
                    Str("Tier",       "tier"),
                    Str("StickerAlt", "stickerAltText"),
                    Str("StickerUrl", "stickerImageUrl"),
                }),

            Def("YouTube.NewSponsor", YouTubePlatform,
                new[] { S("User"), S("Level"), B("IsUpgrade") },
                new[]
                {
                    YtUser(),
                    Str("Level",      "levelName"),
                    Bool("IsUpgrade", "isUpgrade"),
                }),

            Def("YouTube.MemberMileStone", YouTubePlatform,
                new[] { S("User"), S("Level"), N("Months"), S("Message") },
                new[]
                {
                    YtUser(),
                    Str("Level",    "levelName"),
                    Int("Months",   "months"),
                    Str("Message",  "message"),
                }),

            // Actor (user.name) is the GIFTER on this event.
            Def("YouTube.MembershipGift", YouTubePlatform,
                new[] { S("User"), N("Count"), S("Tier") },
                new[]
                {
                    YtUser(),
                    Int("Count", "count"),
                    Str("Tier",  "tier"),
                }),

            // Actor (user.name) is the RECIPIENT on this event.
            Def("YouTube.GiftMembershipReceived", YouTubePlatform,
                new[] { S("User"), S("Gifter"), S("Tier") },
                new[]
                {
                    YtUser(),
                    Str("Gifter", "gifterUser", "gifterUserName"),
                    Str("Tier",   "tier"),
                }),

            Def("YouTube.FirstWords", YouTubePlatform,
                new[] { S("User"), S("Message") },
                new[]
                {
                    YtUser(),
                    Str("Message", "message"),
                }),

            Def("YouTube.NewSubscriber", YouTubePlatform,
                new[] { S("User"), S("Payload") },
                new[] { YtUser() }),

            Def("YouTube.UserBanned", YouTubePlatform,
                new[] { S("User"), S("BanType"), N("Duration") },
                new[]
                {
                    YtUser(),
                    Str("BanType",  "banType"),
                    Int("Duration", "banDuration"),
                }),

            Def("YouTube.UserTimedout", YouTubePlatform,
                new[] { S("User"), N("Duration") },
                new[]
                {
                    YtUser(),
                    Int("Duration", "banDuration"),
                },
                versionNote: "SB 1.0.5+"),

            Def("YouTube.MessageDeleted", YouTubePlatform,
                new[] { S("User"), S("MessageId"), S("Payload") },
                new[]
                {
                    YtUser(),
                    Str("MessageId", "messageId"),
                }),

            // Schema unpublished; amount kept as string.
            Def("YouTube.JewelsGifted", YouTubePlatform,
                new[] { S("User"), S("Amount"), S("Payload") },
                new[]
                {
                    YtUser(),
                    Str("Amount", "amount"),
                },
                versionNote: "SB 1.0.5+"),

            Def("YouTube.StatisticsUpdated", YouTubePlatform,
                new[] { N("Viewers"), N("Views"), N("Likes") },
                new[]
                {
                    Int("Viewers", "concurrentViewers"),
                    Int("Views",   "viewCount"),
                    Int("Likes",   "likeCount"),
                }),

            Def("YouTube.PresentViewers", YouTubePlatform,
                new[] { L("Users"), B("IsLive"), S("Payload") },
                new[]
                {
                    Join("Users",   "users"),
                    Bool("IsLive",  "isLive"),
                }),

            YtBroadcast("YouTube.BroadcastStarted"),
            YtBroadcast("YouTube.BroadcastEnded"),
            YtBroadcast("YouTube.BroadcastAdded"),
            YtBroadcast("YouTube.BroadcastRemoved"),
            YtBroadcast("YouTube.BroadcastUpdated"),

            YtBroadcastMonitoring("YouTube.BroadcastMonitoringStarted"),
            YtBroadcastMonitoring("YouTube.BroadcastMonitoringEnded"),

            Def("YouTube.NewSponsorOnlyStarted", YouTubePlatform,
                new[] { S("Payload") },
                Array.Empty<PlatformEventVar>()),

            Def("YouTube.NewSponsorOnlyEnded", YouTubePlatform,
                new[] { S("Payload") },
                Array.Empty<PlatformEventVar>()),

            YtPoll("YouTube.PollStarted"),
            YtPoll("YouTube.PollUpdated"),
            YtPoll("YouTube.PollClosed"),

            EmoteEvent("YouTube.SevenTVEmoteAdded",    YouTubePlatform),
            EmoteEvent("YouTube.SevenTVEmoteRemoved",  YouTubePlatform),
            EmoteEvent("YouTube.BetterTTVEmoteAdded",  YouTubePlatform),
            EmoteEvent("YouTube.BetterTTVEmoteRemoved", YouTubePlatform),

            // ---------------- Kick (20; ChatMessage handled by Chat.Message) -

            Def("Kick.FirstWords", KickPlatform,
                new[] { S("User"), S("Message") },
                new[]
                {
                    KickUser(),
                    Str("Message", "message"),
                }),

            Def("Kick.Follow", KickPlatform,
                new[] { S("User") },
                new[] { KickUser() }),

            Def("Kick.Subscription", KickPlatform,
                new[] { S("User"), N("Months"), N("Duration") },
                new[]
                {
                    KickUser(),
                    Int("Months",   "monthsSubscribed"),
                    Int("Duration", "duration"),
                }),

            Def("Kick.Resubscription", KickPlatform,
                new[] { S("User"), N("Months"), N("Duration") },
                new[]
                {
                    KickUser(),
                    Int("Months",   "monthsSubscribed"),
                    Int("Duration", "duration"),
                }),

            // Actor is the gifter; the recipient rides in a nested object.
            Def("Kick.GiftSubscription", KickPlatform,
                new[] { S("Gifter"), S("Recipient") },
                new[]
                {
                    KickGifter(),
                    Str("Recipient", "recipient.userName", "recipient.userLogin", "recipient"),
                }),

            // A missing "count" is fine — the runtime extractor falls back to
            // the recipients array length.
            Def("Kick.MassGiftSubscription", KickPlatform,
                new[] { S("Gifter"), N("Count"), L("Recipients") },
                new[]
                {
                    KickGifter(),
                    Int("Count",       "count"),
                    Join("Recipients", "recipients", "recipient"),
                }),

            Def("Kick.RewardRedemption", KickPlatform,
                new[] { S("User"), S("Reward"), S("Input"), N("Cost"), S("RewardId") },
                new[]
                {
                    KickUser(),
                    Str("Reward",   "reward.title"),
                    Str("Input",    "rawInput"),
                    Int("Cost",     "reward.cost"),
                    Str("RewardId", "reward.id"),
                },
                versionNote: "SB 1.0.2+"),

            // Wire name is "Kick.sGifted"; WS normalizes to this title.
            Def("Kick.KicksGifted", KickPlatform,
                new[] { S("User"), N("Amount"), S("KickName"), S("Tier") },
                new[]
                {
                    KickUser(),
                    Int("Amount",   "kicks.amount"),
                    Str("KickName", "kicks.name"),
                    Str("Tier",     "kicks.tier"),
                },
                versionNote: "SB 1.0.2+"),

            Def("Kick.StreamOnline", KickPlatform,
                new[] { S("Title"), S("Category") },
                new[]
                {
                    Str("Title",    "title"),
                    Str("Category", "category.name"),
                }),

            Def("Kick.StreamOffline", KickPlatform,
                new[] { S("Payload") },
                Array.Empty<PlatformEventVar>()),

            Def("Kick.ViewerCountUpdate", KickPlatform,
                new[] { N("Viewers") },
                new[] { Int("Viewers", "viewerCount") }),

            // Kick calls the stream title "status" on this event.
            Def("Kick.ChannelUpdate", KickPlatform,
                new[] { S("Title"), S("OldTitle"), S("Category"), S("OldCategory") },
                new[]
                {
                    Str("Title",       "status"),
                    Str("OldTitle",    "oldStatus"),
                    Str("Category",    "categoryName"),
                    Str("OldCategory", "oldCategoryName"),
                }),

            Def("Kick.UserBanned", KickPlatform,
                new[] { S("User"), S("By"), S("Reason") },
                new[]
                {
                    KickUser(),
                    Str("By",     "createdByUsername"),
                    Str("Reason", "reason"),
                }),

            Def("Kick.UserTimedOut", KickPlatform,
                new[] { S("User"), S("By"), S("Reason"), N("Duration") },
                new[]
                {
                    KickUser(),
                    Str("By",       "createdByUsername"),
                    Str("Reason",   "reason"),
                    Int("Duration", "duration"),
                }),

            Def("Kick.PresentViewers", KickPlatform,
                new[] { L("Users"), S("Payload") },
                new[] { Join("Users", "users") }),

            Def("Kick.BroadcasterAuthenticated", KickPlatform,
                new[] { S("Payload") },
                Array.Empty<PlatformEventVar>()),

            Def("Kick.BroadcasterChatConnected", KickPlatform,
                Array.Empty<PlatformEventSocket>(),
                Array.Empty<PlatformEventVar>()),

            Def("Kick.BroadcasterChatDisconnected", KickPlatform,
                Array.Empty<PlatformEventSocket>(),
                Array.Empty<PlatformEventVar>()),

            EmoteEvent("Kick.SevenTVEmoteAdded",   KickPlatform),
            EmoteEvent("Kick.SevenTVEmoteRemoved", KickPlatform),

            // ---------------- Twitch — Hype Train (4) -----------------------
            //
            // There is deliberately NO Total / Goal socket on any of the four.
            // Streamer.bot documents no such field: the only progress signals it
            // exposes are the level and the percentage into that level, and a
            // socket that could never resolve would render its token literally
            // in chat. Percent probes the decimal spelling second because the
            // two SB surfaces disagree on which one they publish.

            Def("Twitch.HypeTrainStart", TwitchPlatform,
                new[] { N("Level"), N("Percent"), S("Id"), B("IsGolden"), S("Payload") },
                new[]
                {
                    Int("Level",     "level"),
                    Int("Percent",   "percent", "percentDecimal"),
                    Str("Id",        "id"),
                    Bool("IsGolden", "isGoldenKappaTrain"),
                }),

            Def("Twitch.HypeTrainUpdate", TwitchPlatform,
                new[] { N("Level"), N("Percent"), S("Id"), S("Payload") },
                new[]
                {
                    Int("Level",   "level"),
                    Int("Percent", "percent", "percentDecimal"),
                    Str("Id",      "id"),
                }),

            Def("Twitch.HypeTrainLevelUp", TwitchPlatform,
                new[] { N("Level"), N("PrevLevel"), N("Percent"), S("Id"), S("Payload") },
                new[]
                {
                    Int("Level",     "level"),
                    Int("PrevLevel", "prevLevel"),
                    Int("Percent",   "percent", "percentDecimal"),
                    Str("Id",        "id"),
                }),

            Def("Twitch.HypeTrainEnd", TwitchPlatform,
                new[] { N("Level"), S("Id"), S("Payload") },
                new[]
                {
                    Int("Level", "level"),
                    Str("Id",    "id"),
                }),

            // ---------------- Twitch — Charity (4) --------------------------
            //
            // Amount/Currency here are the FIRST pass only: charity rides the
            // same DonationIngest.NormalizeMoneyVars path as broker tips (it is
            // real money), so whatever shape SB sends is rewritten into the
            // canonical decimal + minor units + ISO currency afterwards. The
            // probes stay broad — nested documented path first, bare key second.

            Def("Twitch.CharityDonation", TwitchPlatform,
                new[] { S("User"), S("Amount"), S("Currency"), S("CharityName"), S("Payload") },
                new[]
                {
                    Str("User",        TwitchActorProbes),
                    Str("Amount",      "charity.amount.value",    "amount"),
                    Str("Currency",    "charity.amount.currency", "currency"),
                    Str("CharityName", "charity.name"),
                }),

            TwitchCharityProgress("Twitch.CharityStarted"),
            TwitchCharityProgress("Twitch.CharityProgress"),
            TwitchCharityProgress("Twitch.CharityCompleted"),

            // ---------------- Twitch — Channel Goals (3) --------------------
            //
            // CommunityGoalContribution / CommunityGoalEnded are absent ON
            // PURPOSE: Streamer.bot REMOVED both in v1.0.0 after Twitch retired
            // the PubSub API behind them. They can never fire again, so a node
            // for either would be permanently dead with no error to explain it.

            TwitchGoal("Twitch.GoalBegin"),
            TwitchGoal("Twitch.GoalProgress"),

            // Only the terminal event knows whether the goal was actually met.
            Def("Twitch.GoalEnd", TwitchPlatform,
                new[] { S("GoalId"), S("GoalType"), S("Description"), N("CurrentAmount"), N("TargetAmount"), B("IsAchieved"), S("Payload") },
                new[]
                {
                    Str("GoalId",        "goal.id",            "id"),
                    Str("GoalType",      "goal.type",          "type"),
                    Str("Description",   "goal.description",   "description"),
                    Int("CurrentAmount", "goal.currentAmount", "currentAmount"),
                    Int("TargetAmount",  "goal.targetAmount",  "targetAmount"),
                    Bool("IsAchieved",   "goal.isAchieved",    "isAchieved", "is_achieved"),
                }),

            // ---------------- Twitch — Shoutouts (2) ------------------------
            //
            // The two directions are separate events with separate actors: on
            // Created WE shouted someone out (the other channel is the target),
            // on Received someone shouted US out (they are the actor). Naming
            // the socket after the role rather than "User" on both keeps the
            // direction readable on the canvas.

            Def("Twitch.ShoutoutCreated", TwitchPlatform,
                new[] { S("TargetUser"), N("ViewerCount"), S("Payload") },
                new[]
                {
                    Str("TargetUser",  "targetUserDisplayName", "targetUserLogin", "targetUser"),
                    Int("ViewerCount", "viewerCount"),
                }),

            Def("Twitch.ShoutoutReceived", TwitchPlatform,
                new[] { S("User"), N("ViewerCount"), S("Payload") },
                new[]
                {
                    Str("User",        TwitchActorProbes),
                    Int("ViewerCount", "viewerCount"),
                }),

            // ---------------- Twitch — Bans / timeouts (4) ------------------

            Def("Twitch.UserBanned", TwitchPlatform,
                new[] { S("User"), S("Moderator"), S("Reason"), S("Payload") },
                new[]
                {
                    Str("User",      TwitchBanTargetProbes),
                    Str("Moderator", TwitchModeratorProbes),
                    Str("Reason",    "reason"),
                }),

            // Duration is in SECONDS, matching Kick.UserTimedOut above.
            Def("Twitch.UserTimedOut", TwitchPlatform,
                new[] { S("User"), S("Moderator"), S("Reason"), N("Duration"), S("Payload") },
                new[]
                {
                    Str("User",      TwitchBanTargetProbes),
                    Str("Moderator", TwitchModeratorProbes),
                    Str("Reason",    "reason"),
                    Int("Duration",  "duration"),
                }),

            TwitchModUndo("Twitch.UserUnbanned"),
            TwitchModUndo("Twitch.UserUntimedOut"),

            // ---------------- Twitch — misc (3) -----------------------------

            // ★ Streak is snake_case FIRST. The WebSocket payload spells it
            // streak_count while the trigger-variable surface spells it
            // watchStreak, and the same split hits the points field — so both
            // spellings are probed rather than betting on one surface.
            Def("Twitch.WatchStreak", TwitchPlatform,
                new[] { S("User"), N("Streak"), N("PointsAwarded"), S("Message"), S("Payload") },
                new[]
                {
                    Str("User",           "user.name", "user.login", "userName"),
                    Int("Streak",         "streak_count", "watchStreak", "streakCount"),
                    Int("PointsAwarded",  "channel_points_awarded", "copoReward"),
                    Str("Message",        "text", "message"),
                }),

            // ★ Renamed from the wire name "Twitch.Announcement", which is
            // ALREADY taken by the hand-written Twitch.Announcement ACTION node
            // (Platforms category — it SENDS an announcement). NodeRegistry's
            // template table is a plain dictionary assignment, so a same-titled
            // catalog entry would silently overwrite that action node instead of
            // failing loudly.
            //
            // ★ The rename needs an alias on BOTH lookup paths, and for a while it
            // only had one. Script SELECTION is aliased in ScriptManager (matchType,
            // the same trick used for Twitch.RewardRedemption → Twitch.PointRedeem)
            // and worked; VAR RESOLUTION goes through Find() keyed on this Title
            // while BuildGenericEventVars passes the raw wire type, so it returned
            // null and Message/Color were permanently empty. WireNameAliases above
            // closes that half — do not remove it without renaming the entry.
            // ★ Color is announcementColor on one SB surface and announceColor
            // on the other; probe both.
            Def("Twitch.AnnouncementReceived", TwitchPlatform,
                new[] { S("User"), S("Message"), S("Color"), S("Payload") },
                new[]
                {
                    Str("User",    TwitchActorProbes),
                    Str("Message", "text", "message"),
                    Str("Color",   "announcementColor", "announceColor"),
                }),

            // The built-in automatic rewards (Highlight My Message, Gigantify
            // an Emote, …). Distinct from Twitch.PointRedeem, which covers
            // CUSTOM rewards only — these have no reward id, just a type.
            Def("Twitch.AutomaticRewardRedemption", TwitchPlatform,
                new[] { S("User"), S("RewardType"), N("ChannelPoints"), S("Message"), S("Payload") },
                new[]
                {
                    Str("User",          TwitchActorProbes),
                    Str("RewardType",    "rewardType"),
                    Int("ChannelPoints", "channelPoints"),
                    Str("Message",       "text", "userInput"),
                }),

            // ---------------- Twitch — Polls & Predictions (8) --------------
            //
            // WS.cs already SUBSCRIBES to all eight; without a node the inbound
            // events had nowhere to land, so the ingestion was dead on arrival.

            TwitchPoll("Twitch.PollCreated"),
            TwitchPoll("Twitch.PollUpdated"),
            TwitchPoll("Twitch.PollCompleted"),

            TwitchPrediction("Twitch.PredictionCreated"),
            TwitchPrediction("Twitch.PredictionUpdated"),
            TwitchPrediction("Twitch.PredictionLocked"),
            TwitchPrediction("Twitch.PredictionCompleted"),
            TwitchPrediction("Twitch.PredictionCanceled"),

            // ---------------- Twitch — moderation surface (6) ---------------

            // A chat wipe names no user and no moderator on the wire; Payload
            // is the only honest socket.
            Def("Twitch.ChatCleared", TwitchPlatform,
                new[] { S("Payload") },
                Array.Empty<PlatformEventVar>()),

            Def("Twitch.ChatMessageDeleted", TwitchPlatform,
                new[] { S("User"), S("MessageId"), S("Payload") },
                new[]
                {
                    Str("User",      TwitchActorProbes),
                    Str("MessageId", "messageId", "targetMessageId"),
                }),

            TwitchUserOnly("Twitch.ModeratorAdded"),
            TwitchUserOnly("Twitch.ModeratorRemoved"),
            TwitchUserOnly("Twitch.VipAdded"),
            TwitchUserOnly("Twitch.VipRemoved"),

            // ---------------- Donation brokers (24) -------------------------
            //
            // Ten third-party money sources, verified against a LIVE Streamer.bot
            // 1.0.4 GetEvents roster. Streamer.bot publishes NO WebSocket schema
            // for any of them ("No Schema Available" on every broker page), so
            // the field names below come from its documented TRIGGER-VARIABLE
            // surface — a different surface that flattens nesting into dotted
            // key names. The resolver walks a dotted probe as a nested path
            // first and then retries it as one literal key, which is exactly why
            // "fw.amount" and "shopify.total_price" can be written as-is.
            //
            // See the donation-broker factory block above for why AmountCents
            // carries no probe and why Message is the one field with a generic
            // fallback.

            // -- Streamlabs (3) --
            Def("Streamlabs.Donation", StreamlabsPlatform,
                new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("Message"), S("Payload") },
                new[]
                {
                    Str("User",     "donationFrom"),
                    Str("Amount",   "donationAmount"),
                    Str("Currency", "donationCurrency"),
                    Str("Message",  "donationMessage", "message"),
                }),

            // Merchandise carries no amount or currency at all — Streamlabs
            // reports the item, not the price — so neither socket is declared.
            Def("Streamlabs.Merchandise", StreamlabsPlatform,
                new[] { S("User"), S("Message"), S("Payload") },
                new[]
                {
                    Str("User",    "merchandiseFrom"),
                    Str("Message", "merchandiseMessage", "message"),
                }),

            Def("Streamlabs.CharityDonation", StreamlabsPlatform,
                new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("Message"), S("Payload") },
                new[]
                {
                    Str("User",     "charityDonationFrom"),
                    Str("Amount",   "charityDonationAmount"),
                    Str("Currency", "charityDonationCurrency"),
                    Str("Message",  "charityDonationMessage", "message"),
                }),

            // -- StreamElements (2) --
            Def("StreamElements.Tip", StreamElementsPlatform,
                new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("Message"), S("Payload") },
                new[]
                {
                    Str("User",     "tipUsername"),
                    Str("Amount",   "tipAmount"),
                    Str("Currency", "tipCurrency"),
                    Str("Message",  "tipMessage", "message"),
                }),

            Def("StreamElements.Merch", StreamElementsPlatform,
                new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("Message"), S("Payload") },
                new[]
                {
                    Str("User",     "merchUsername"),
                    Str("Amount",   "merchAmount"),
                    Str("Currency", "merchCurrency"),
                    Str("Message",  "merchMessage", "message"),
                }),

            // -- Ko-fi (5) --
            // All five money events share one payload shape; messageId is the
            // per-transaction id that makes Ko-fi exactly de-dupable.
            KofiMoney("Kofi.Donation"),
            KofiMoney("Kofi.Subscription"),
            KofiMoney("Kofi.Resubscription"),
            KofiMoney("Kofi.ShopOrder"),
            KofiMoney("Kofi.Commission"),

            // -- Patreon (3) --
            PatreonPledge("Patreon.PledgeCreated"),
            PatreonPledge("Patreon.PledgeUpdated"),
            PatreonPledge("Patreon.PledgeDeleted"),

            // -- TipeeeStream (1) --
            Def("TipeeeStream.Donation", TipeeeStreamPlatform,
                new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("Message"), S("TxId"), S("Payload") },
                new[]
                {
                    Str("User",     "username"),
                    Str("Amount",   "amount"),
                    Str("Currency", "currency"),
                    Str("Message",  "message"),
                    Str("TxId",     "tipeeStreamId"),
                }),

            // -- TreatStream (1) --
            // A "treat" is a redeemed action, not a payment: no amount, no
            // currency, no transaction id on the wire.
            Def("TreatStream.Treat", TreatStreamPlatform,
                new[] { S("User"), S("Message"), S("Payload") },
                new[]
                {
                    Str("User",    "username"),
                    Str("Message", "message"),
                }),

            // -- DonorDrive (2) --
            DonorDriveMoney("DonorDrive.Donation"),
            DonorDriveMoney("DonorDrive.Incentive"),

            // -- Fourthwall (4) --
            // Only Donation carries a supporter message; the shop events don't.
            Def("Fourthwall.Donation", FourthwallPlatform,
                new[] { S("User"), S("Amount"), S("AmountCents"), S("Currency"), S("Message"), S("TxId"), S("Payload") },
                new[]
                {
                    Str("User",     "fw.username"),
                    Str("Amount",   "fw.amount"),
                    Str("Currency", "fw.currency"),
                    Str("Message",  "fw.message", "message"),
                    Str("TxId",     "fw.donationId"),
                }),

            FourthwallPurchase("Fourthwall.OrderPlaced", "fw.username", "fw.total", "fw.orderId"),

            // ★ "fw.Id" — capital I — is a VERBATIM Streamer.bot typo on this
            // one event; every other Fourthwall event uses lowercase. Probed
            // first with the corrected spelling behind it, so the node keeps
            // working if SB ever fixes it.
            FourthwallPurchase("Fourthwall.GiftPurchase", "fw.username", "fw.total", "fw.Id", "fw.id"),

            // Subscriptions report the buyer under "nickname", not "username".
            FourthwallPurchase("Fourthwall.SubscriptionPurchased", "fw.nickname", "fw.amount", "fw.id"),

            // -- Pally.gg (1) --
            // Amount is the NET figure — Pally reports the gross and the fee
            // separately, and the fee field is the misspelled
            // "tipProccessingFee" (double-c, verbatim). Net is what actually
            // reaches the streamer, so that is what the socket carries. Pally
            // publishes no currency on the tip event.
            Def("Pallygg.CampaignTip", PallyggPlatform,
                new[] { S("User"), S("Amount"), S("AmountCents"), S("Message"), S("TxId"), S("Payload") },
                new[]
                {
                    Str("User",    "tipDisplayName"),
                    Str("Amount",  "tipNetAmount"),
                    Str("Message", "tipMessage", "message"),
                    Str("TxId",    "tipId"),
                }),

            // -- Shopify (2) --
            // Created fires on checkout, Paid on settlement — the same order
            // raises both, and shopify.id is identical across the pair, so
            // DonationIngest's per-event-type de-dupe key keeps them distinct
            // while still killing a replay of either.
            ShopifyOrder("Shopify.OrderCreated"),
            ShopifyOrder("Shopify.OrderPaid"),
        };
    }
}
