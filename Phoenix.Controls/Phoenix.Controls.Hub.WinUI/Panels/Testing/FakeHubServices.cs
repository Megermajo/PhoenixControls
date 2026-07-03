// Was compiled into the production binary by accident — now gated
// behind DEBUG so Release builds don't ship the fixture
// data. Design-time previews and smoke tests run on Debug builds and
// continue to instantiate this normally.
#if DEBUG

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.WinUI.Contracts;

// Fake services exist only to feed design-time previews and the
// PanelFactory(new FakeHubServices()) smoke path the prompt requires;
// they intentionally never raise their interface events. Silence CS0067
// so the build stays warning-clean.
#pragma warning disable CS0067

namespace Phoenix.Controls.Hub.WinUI.Panels.Testing;

// Static fixture used in design-time / smoke tests so PanelFactory can be
// instantiated without the real bridge. Mirrors the data shape from
// redesign-plan/design/project/hub.jsx so the panels render the same scene
// the design HTML shows.
public sealed class FakeHubServices : IHubServices
{
    public FakeHubServices()
    {
        LiveFeed  = new FakeLiveFeed();
        Chat      = new FakeChat();
        Scripts   = new FakeScripts();
        SystemLog = new FakeSystemLog();
        Status    = new FakeStatus();
        Layers    = new FakeLayerRegistry();
        Giveaway  = new FakeGiveaway();
    }

    public ILiveFeedSource       LiveFeed  { get; }
    public IChatSource           Chat      { get; }
    public IScriptHostMonitor    Scripts   { get; }
    public ISystemLogSource      SystemLog { get; }
    public IConnectionStatus     Status    { get; }
    public ILayerRegistrySource  Layers    { get; }
    public IGiveawaySource       Giveaway  { get; }

    // IHubServices now requires IAsyncDisposable. The fake holds
    // only in-memory fixture lists — there's nothing to release — but the
    // contract has to be satisfied so design-time previews and smoke tests
    // can hand the same disposal protocol production uses.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeLayerRegistry : ILayerRegistrySource
{
    public bool IsLayerActive(string layerId) => false;
    public IReadOnlyList<string> GetActiveLayerIds() => Array.Empty<string>();
    public event Action<string, bool>? LiveLayerChanged;
}

internal sealed class FakeLiveFeed : ILiveFeedSource
{
    private readonly List<LiveFeedEntry> _entries = new()
    {
        New("21:14:08", LiveFeedKind.Sub,    "ember_ash",      "subscribed · 12 months · Tier 1"),
        New("21:14:02", LiveFeedKind.Chat,   "wraith_owl",     "!hello"),
        New("21:13:55", LiveFeedKind.Redeem, "marigold_77",    "Channel Points · Hydrate Reminder"),
        New("21:13:41", LiveFeedKind.Raid,   "captain_torch",  "raided with 47 viewers"),
        New("21:13:22", LiveFeedKind.Visual, "main",           "VISUAL_TRIGGER · greet · widget#3"),
        New("21:13:11", LiveFeedKind.Follow, "kindling_kid",   "started following"),
        New("21:12:58", LiveFeedKind.Chat,   "broadcaster",    "we are LIVE — testing alerts"),
        New("21:12:40", LiveFeedKind.Sub,    "loomweaver",     "gifted 5 subs to the channel"),
        New("21:12:22", LiveFeedKind.Visual, "main",           "VISUAL_COMPLETE · jackpot · 1.84s"),
        New("21:12:09", LiveFeedKind.Chat,   "scribe_of_iron", "!points"),
    };

    /// <summary>
    /// Returns a defensive copy of the backing list. Callers may
    /// retain the result for the panel lifetime; mutating the live backing
    /// list later (during a future hot-reload of fixture data, for example)
    /// must not retroactively edit prior snapshots the VM is rendering.
    /// </summary>
    public IReadOnlyList<LiveFeedEntry> Snapshot() => _entries.ToArray();
    public event EventHandler<LiveFeedEntry>? EntryAdded;
    [System.Obsolete("Filtering is per-VM; see ILiveFeedSource.SetFilter.")]
    public void SetFilter(LiveFeedFilter filter) { /* no-op in fake */ }

    // Primary-subscriber gate removed; the
    // multicast event itself is the subscriber list.

    private static LiveFeedEntry New(string hms, LiveFeedKind kind, string who, string detail)
        => new(ParseHms(hms), kind, who, detail);

    private static DateTimeOffset ParseHms(string hms)
    {
        var today = DateTimeOffset.Now.Date;
        var parts = hms.Split(':');
        return new DateTimeOffset(
            today.AddHours(int.Parse(parts[0]))
                 .AddMinutes(int.Parse(parts[1]))
                 .AddSeconds(int.Parse(parts[2])),
            DateTimeOffset.Now.Offset);
    }
}

internal sealed class FakeChat : IChatSource
{
    private readonly List<ChatMessage> _messages = new()
    {
        new(DateTimeOffset.Now, ChatRole.Broadcaster, "Megermajo",     "alright, going live in 30",            IsBroadcaster: true),
        new(DateTimeOffset.Now, ChatRole.Mod,         "ironscribe",    "stream looking sharp tonight",         IsMod: true),
        new(DateTimeOffset.Now, ChatRole.Vip,         "marigold_77",   "this overlay is forged",               IsVip: true, IsSubscriber: true),
        new(DateTimeOffset.Now, ChatRole.Sub,         "ember_ash",     "!points",                              IsSubscriber: true),
        new(DateTimeOffset.Now, ChatRole.Viewer,      "wraith_owl",    "!hello"),
        new(DateTimeOffset.Now, ChatRole.Bot,         "Phoenix",       "Hello, wraith_owl!"),
        new(DateTimeOffset.Now, ChatRole.Sub,         "loomweaver",    "the brass title bar is gorgeous",      IsSubscriber: true),
        new(DateTimeOffset.Now, ChatRole.Mod,         "captain_torch", "raid incoming in 5",                   IsMod: true, IsSubscriber: true),
        new(DateTimeOffset.Now, ChatRole.Viewer,      "kindling_kid",  "first time here, looks slick"),
    };

    /// <summary>
    /// Defensive copy — see <see cref="FakeLiveFeed.Snapshot"/>.
    /// </summary>
    public IReadOnlyList<ChatMessage> Snapshot() => _messages.ToArray();
    public event EventHandler<ChatMessage>? MessageReceived;
    public Task SendAsBotAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeScripts : IScriptHostMonitor
{
    private readonly List<ScriptStatus> _scripts = new()
    {
        new("scripts/hello.phx",          "hello",          ScriptState.Idle,    true,  TimeSpan.FromMilliseconds(0.4),  1_200_000,  47, null),
        new("scripts/sub_alerts.phx",     "sub_alerts",     ScriptState.Running, true,  TimeSpan.FromMilliseconds(12.1), 4_800_000,  12, null),
        new("scripts/rotating_tips.phx",  "rotating_tips",  ScriptState.Idle,    true,  TimeSpan.FromMilliseconds(0.9),    800_000, 188, null),
        new("scripts/points_redeems.phx", "points_redeems", ScriptState.Errored, false, TimeSpan.Zero,                            0,   1, "Twitch.SendChat: rate-limited"),
        new("scripts/ai_moderate.phx",    "ai_moderate",    ScriptState.Idle,    true,  TimeSpan.FromMilliseconds(143),  12_400_000, 22, null),
    };

    /// <summary>
    /// Defensive copy — see <see cref="FakeLiveFeed.Snapshot"/>.
    /// </summary>
    public IReadOnlyList<ScriptStatus> Snapshot() => _scripts.ToArray();
    public event EventHandler<ScriptStatus>? StatusChanged;
    public Task SetEnabledAsync(string scriptName, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReloadAsync(string scriptName, CancellationToken ct = default) => Task.CompletedTask;
    public Task OpenInArchitectAsync(string scriptName, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeSystemLog : ISystemLogSource
{
    private readonly List<SystemLogEntry> _entries = new()
    {
        Log("21:14:08.412", SystemLogLevel.Info,  "twitch.eventsub", "subscription.create id=ember_ash tier=1000 months=12"),
        Log("21:14:08.398", SystemLogLevel.Debug, "ipc.bus",         "→ visualist  TRIGGER greet payload=88B"),
        Log("21:14:08.391", SystemLogLevel.Info,  "script.engine",   "sub_alerts.phx invoked (run #12, 12.1ms)"),
        Log("21:14:02.220", SystemLogLevel.Debug, "twitch.irc",      "PRIVMSG #megermajo :!hello"),
        Log("21:14:02.219", SystemLogLevel.Debug, "script.engine",   "hello.phx matched filter ^!hello$"),
        Log("21:14:02.224", SystemLogLevel.Info,  "twitch.api",      "POST /chat 200 OK 47ms"),
        Log("21:13:55.002", SystemLogLevel.Info,  "channel.points",  "redemption Hydrate Reminder (cost 250)"),
        Log("21:13:42.115", SystemLogLevel.Warn,  "obs.browsersrc",  "client 127.0.0.1:51844 disconnected (will reconnect)"),
        Log("21:13:42.808", SystemLogLevel.Info,  "obs.browsersrc",  "client 127.0.0.1:51845 connected · layer=main"),
        Log("21:13:11.991", SystemLogLevel.Error, "script.engine",   "points_redeems.phx → Twitch.SendChat: rate-limited; continuing"),
        Log("21:13:11.992", SystemLogLevel.Info,  "script.engine",   "execution continued past error (resilience=on)"),
        Log("21:12:58.001", SystemLogLevel.Debug, "fs.watcher",      "scripts/hello.phx changed → hot-reloaded (47 → 48)"),
        Log("21:12:40.620", SystemLogLevel.Info,  "twitch.eventsub", "subscription.gift count=5 from loomweaver"),
        Log("21:12:22.017", SystemLogLevel.Debug, "ipc.bus",         "← visualist  COMPLETE jackpot 1.84s"),
    };

    /// <summary>
    /// Defensive copy — see <see cref="FakeLiveFeed.Snapshot"/>.
    /// </summary>
    public IReadOnlyList<SystemLogEntry> Snapshot() => _entries.ToArray();
    public event EventHandler<SystemLogEntry>? EntryAdded;
    public void SetLevelFilter(SystemLogLevel mask) { /* no-op in fake */ }

    private static SystemLogEntry Log(string hmsf, SystemLogLevel level, string source, string message)
    {
        var today = DateTime.Today;
        var parts = hmsf.Split(':');
        var sec = parts[2].Split('.');
        var ts = today.AddHours(int.Parse(parts[0]))
                      .AddMinutes(int.Parse(parts[1]))
                      .AddSeconds(int.Parse(sec[0]))
                      .AddMilliseconds(int.Parse(sec[1]));
        return new SystemLogEntry(new DateTimeOffset(ts, DateTimeOffset.Now.Offset), level, source, message, null);
    }
}

internal sealed class FakeStatus : IConnectionStatus
{
    public ConnectionState StreamerBot => ConnectionState.Connected;
    public ConnectionState HudOverlay  => ConnectionState.Connected;
    public ConnectionState IpcBus      => ConnectionState.Connected;
    // Typed-payload form to match IConnectionStatus.StateChanged.
    public event EventHandler<ConnectionStateChange>? StateChanged;
}

// Design-time giveaway fixture — mirrors the SAMPLE_GIVEAWAYS / ENTRANTS /
// GIVEAWAY_LOG data shape in the design's giveaway.jsx so the Hub Giveaway page
// renders the same scene at design time. DEBUG-only fixture, never shipped.
internal sealed class FakeGiveaway : IGiveawaySource
{
    private readonly List<GiveawayInfo> _giveaways = new()
    {
        new(1, "g-2024-11-22-a", "Forged Coffee Mug · Nov stream", GiveawayStatus.Open,   "21:02:11", null,     "broadcaster", true,  24, 137, "5.7", "8s", "!enter", 1, 1, 0, "Weighted by tickets", ""),
        new(2, "g-2024-11-15-b", "Sticker Pack v2",                GiveawayStatus.Drawn,  "Nov 15",   "Nov 15", "broadcaster", false, 41, 218, "5.3", "—",  "!enter", 1, 1, 0, "Weighted by tickets", "marigold_77"),
        new(3, "g-2024-11-09-c", "Custom Phoenix .phx pack",       GiveawayStatus.Closed, "Nov 9",    "Nov 9",  "ironscribe",  false, 18, 64,  "3.6", "—",  "!enter", 1, 0, 0, "Weighted by tickets", ""),
        new(4, "g-2024-10-30-d", "Halloween brass keycap",         GiveawayStatus.Drawn,  "Oct 30",   "Oct 30", "broadcaster", false, 67, 312, "4.7", "—",  "!enter", 1, 2, 0, "Weighted by tickets", "captain_torch"),
    };

    private readonly Dictionary<long, List<GiveawayEntrantInfo>> _entrants = new()
    {
        [1] = new()
        {
            new("ember_ash",      ChatRole.Sub,    18, "8s"),
            new("marigold_77",    ChatRole.Vip,    14, "12s"),
            new("captain_torch",  ChatRole.Mod,    12, "20s"),
            new("ironscribe",     ChatRole.Mod,    11, "38s"),
            new("loomweaver",     ChatRole.Sub,     9, "1m"),
            new("wraith_owl",     ChatRole.Viewer,  8, "1m"),
            new("scribe_of_iron", ChatRole.Sub,     7, "2m"),
            new("kindling_kid",   ChatRole.Viewer,  7, "2m"),
            new("brass_smith",    ChatRole.Viewer,  6, "3m"),
            new("coal_walker",    ChatRole.Viewer,  6, "3m"),
            new("midnight_forge", ChatRole.Sub,     5, "4m"),
            new("lantern_keeper", ChatRole.Viewer,  5, "4m"),
        },
    };

    private readonly List<GiveawayActivityEntry> _log = new()
    {
        new("21:14:08", "INF", "ember_ash entered  +1 ticket   (18 total)"),
        new("21:14:02", "INF", "marigold_77 entered  +1 ticket  (14 total)"),
        new("21:13:48", "INF", "loomweaver entered  +1 ticket   (9 total)"),
        new("21:13:22", "INF", "ironscribe entered  +2 tickets  (11 total · subscriber bonus)"),
        new("21:13:02", "INF", "leaderboard refreshed on stream overlay"),
        new("21:12:40", "INF", "marked as default giveaway"),
        new("21:02:11", "INF", "giveaway created — \"Forged Coffee Mug · Nov stream\""),
    };

    public long? DefaultGiveawayId => 1;
    public event EventHandler? GiveawaysChanged;
    public event EventHandler<long>? EntrantsChanged;

    public Task<IReadOnlyList<GiveawayInfo>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GiveawayInfo>>(_giveaways.ToArray());

    public Task<GiveawayInfo?> GetAsync(long id, CancellationToken ct = default)
        => Task.FromResult(_giveaways.FirstOrDefault(g => g.Id == id));

    public Task<IReadOnlyList<GiveawayEntrantInfo>> GetEntrantsAsync(long id, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GiveawayEntrantInfo>>(
            (_entrants.TryGetValue(id, out var list) ? list : new()).ToArray());

    public Task<IReadOnlyList<GiveawayActivityEntry>> GetActivityAsync(long id, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GiveawayActivityEntry>>(_log.ToArray());

    public Task<GiveawayInfo> CreateAsync(string title, CancellationToken ct = default)
        => Task.FromResult(new GiveawayInfo(99, "g-new", title, GiveawayStatus.Open, "now", null, "broadcaster",
            false, 0, 0, "—", "—", "!enter", 1, 1, 0, "Weighted by tickets", ""));

    public Task CloseAsync(long id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<(string WinnerName, int WinnerTickets)?> DrawWinnerAsync(long id, CancellationToken ct = default)
        => Task.FromResult<(string, int)?>(("ember_ash", 18));

    public Task SetDefaultAsync(long id, bool isDefault, CancellationToken ct = default) => Task.CompletedTask;
}

#endif
