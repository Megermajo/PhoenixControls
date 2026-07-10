using System;
using System.Collections.Generic;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Core
{
    // Single source of truth for the YouTube/Kick event surface. One
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
    /// The catalog: 30 YouTube + 20 Kick event definitions. Titles equal the
    /// normalized Streamer.bot EventType (the wire name <c>Kick.sGifted</c> is
    /// normalized to <c>Kick.KicksGifted</c> in WS before lookup).
    /// </summary>
    public static class PlatformEventCatalog
    {
        private const string YouTubePlatform = "youtube";
        private const string KickPlatform    = "kick";

        // Must be initialized BEFORE Events (static initializers run in textual
        // order): BuildCatalog() reaches this array through KickUser()/KickGifter(),
        // and declared-after-Events it would still be null at build time — the vars
        // would silently mint with null probes and the resolver would NRE (caught
        // + swallowed downstream), stripping every Kick actor-bearing event's vars.
        // Kick actor fields are flat on data.
        private static readonly string[] KickActorProbes = { "user", "userName", "displayName" };

        /// <summary>All catalog entries, YouTube first, in spec order.</summary>
        public static readonly IReadOnlyList<PlatformEventDef> Events = BuildCatalog();

        // Must be initialized AFTER Events (static initializers run in textual
        // order). Add() throws on a duplicate title, so a copy-paste collision
        // fails fast at type-init instead of shadowing an entry.
        private static readonly Dictionary<string, PlatformEventDef> _byTitle = BuildIndex();

        /// <summary>Case-insensitive lookup by event title; null when unknown.</summary>
        public static PlatformEventDef? Find(string title)
            => !string.IsNullOrEmpty(title) && _byTitle.TryGetValue(title, out var def) ? def : null;

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

        private static Dictionary<string, PlatformEventDef> BuildIndex()
        {
            var map = new Dictionary<string, PlatformEventDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in Events)
                map.Add(def.Title, def);
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
        };
    }
}
