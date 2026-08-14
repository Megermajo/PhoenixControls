using System.Collections.Concurrent;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Services;

// Curated event stream powering the "Live Feed" panel.
// Two upstream feeds + one suppression rule per the design doc:
//
//   • Bus.OnMessageReceived → kind=Visual for VISUAL_TRIGGER /
//     VISUAL_COMPLETE envelopes (everything else is bus chatter and stays
//     in the System Log panel).
//   • GlobalLogger.OnLogEntry filtered to Level==StreamEvent → kind heuristic
//     from the log Source / Message text. The existing WinForms Hub already
//     promotes "Sub", "Raid", "Follow", "Cheer", "Resub", etc. into
//     StreamEvent log entries from WS's per-event branches, so the
//     curated feed is a superset of what's visible in the existing chat
//     window's status strip.
//   • Twitch.ChatMessage events deliberately do NOT surface as kind=Chat
//     here — every chat line would flood the curated feed. They'll appear
//     in the chat panel via IChatSource. (The design doc's "scripts named
//     them" rule maps to ScriptLifecycle.Trigger pings, but those are
//     decorative and excluded by IsEventTriggerNode's Twitch.ChatMessage
//     guard inside ScriptManager.) Panels can opt into the chat
//     event surface separately if needed.
//
// ── Ownership ──────────────────────────────────────────────────────────
// Single instance per Hub lifetime. Constructed inside
// HubBootstrapper.BootAsync and handed to the HubServices composite
// container (which is owned by App._services for the process lifetime).
// MainWindow's Closed handler awaits HubServices.DisposeAsync, which
// walks the composite and calls Dispose() here — that's the only place
// Bus.OnMessageReceived / GlobalLogger.OnLogEntry get unhooked, so
// subscriptions stay live for as long as Hub is running.
//
// There is no hot-reload / reboot path that re-creates HubServices in
// the current code. If a future feature ever needs to swap services at
// runtime (e.g. reconnecting to a different Streamer.bot), the new
// LiveFeedSource instance MUST come up only after Dispose() on the
// previous one — otherwise both instances will append every bus/log
// event to their own buffer and the panel will double-render. The
// singleton bus/logger singletons don't dedup subscriptions, so the
// safety net is the disposal protocol: dispose old → wait for the
// finalizer / event handler -= to complete → construct new.
//
// No fix needed today; flagged here so any future re-entrant boot path
// has the contract documented before it's added.
public sealed class LiveFeedSource : ILiveFeedSource, IDisposable
{
    private const int BufferCapacity = 500;

    private readonly IUiDispatcher _ui;
    private readonly ConcurrentQueue<LiveFeedEntry> _entries = new();
    private int _disposed;

    // Fan-out replaces the previous single-primary gate. EntryAdded's
    // multicast delegate IS the subscriber list; every VM that subscribes
    // receives every row.

    private readonly Action<BusMessage> _onBus;
    private readonly Action<Log> _onLog;

    public LiveFeedSource(IUiDispatcher uiDispatcher)
    {
        _ui = uiDispatcher;

        _onBus = msg =>
        {
            if (msg.Type != "VISUAL_TRIGGER" && msg.Type != "VISUAL_COMPLETE") return;
            string detail = string.IsNullOrEmpty(msg.Payload)
                ? msg.Type
                : $"{msg.Type} · {Trim(msg.Payload, 80)}";
            Append(new LiveFeedEntry(
                Timestamp: ToUtcOffset(msg.SentAt),
                Kind:      LiveFeedKind.Visual,
                Who:       msg.Source ?? "",
                Detail:    detail));
        };

        _onLog = entry =>
        {
            if (entry.Level != LogLevel.StreamEvent) return;
            var (kind, who) = ClassifyStreamEvent(entry);
            Append(new LiveFeedEntry(
                Timestamp: ToUtcOffset(entry.Timestamp),
                Kind:      kind,
                Who:       who,
                Detail:    entry.Message ?? ""));
        };

        Bus.Instance.OnMessageReceived += _onBus;
        GlobalLogger.OnLogEntry += _onLog;
    }

    public IReadOnlyList<LiveFeedEntry> Snapshot()
    {
        // Always return the full unfiltered set — every VM does
        // its own filtering against its local _buffer (mirrors
        // SystemLogViewModel). The pre-fix source-side _filter slot was a
        // shared trap: a SetFilter call from one pop-out narrowed the
        // initial Snapshot every later pop-out received, even though
        // each VM thought it owned its filter independently.
        return _entries.ToArray();
    }

    public event EventHandler<LiveFeedEntry>? EntryAdded;

    // No-op for contract stability; see ILiveFeedSource.SetFilter
    // doc-comment. Filtering happens per-VM, never on the shared source.
    [System.Obsolete("Filtering is per-VM. Use LiveFeedViewModel.SelectedFilter / Snapshot + local predicate.")]
    public void SetFilter(LiveFeedFilter filter) { /* see XML doc */ }

    private void Append(LiveFeedEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > BufferCapacity && _entries.TryDequeue(out _)) { /* drop oldest */ }

        // Always fan out — per-VM filtering is downstream. The
        // pre-fix source-side predicate would suppress rows for every
        // subscriber based on one pop-out's filter choice.

        // Fan out on the UI thread so panels can update ItemsSource without
        // an Invoke wrapper at every binding site.
        _ui.Post(() => EntryAdded?.Invoke(this, entry));
    }

    private static (LiveFeedKind Kind, string Who) ClassifyStreamEvent(Log entry)
    {
        // ── The four shapes WS actually emits at LogLevel.StreamEvent ────────
        //   1. "Scene → <scene>"                         source "OBS"
        //   2. "<sender> whispered: <text>"              source "Twitch"  (actor FIRST)
        //   3. "<EventType> by <actor>"  /  "<EventType>"  source = platform (actor LAST)
        //   4. "!<verb> by <user>"                       source "Chat"
        //
        // ★ Classification is keyed off the EVENT-TYPE token, never off the whole
        // line. The previous pass ran unanchored `Contains` probes over a string
        // that EMBEDS the actor, which produced two live defects:
        //   • a chat command rendered as the event its own verb spelled — "!subs
        //     by Bob" painted a SUB row with the ember brush, "!raid by Bob" a
        //     RAID — and any command at all typed by a viewer named *SubZero*
        //     became a SUB;
        //   • `Who` was the FIRST word, which for shape 3 is the event type, so
        //     the column read "Twitch.Sub" instead of the viewer, and the
        //     right-click "filter to this user" it feeds was poisoned for every
        //     real stream event, not just for commands.
        // Splitting on the " by " infix separates the two halves, so the probes
        // see only the type and `Who` sees only the actor.
        string m = entry.Message ?? "";
        string source = entry.Source ?? "";

        // Shape 4 — a chat command. Anchored on the literal '!' in the first
        // character position (ordinal, per ChatVerb) plus WS's " by " infix; no
        // platform allows a username to begin with '!', so a real stream event
        // cannot take this branch. Routed to the EXISTING Chat kind, which until
        // now had a live filter chip and no producer at all. Whether a command is
        // REGISTERED — and therefore whether it deserves its own kind and chip —
        // needs the command registry (CR0/CR5); this fix is only about the row
        // no longer lying about what it is and who sent it.
        if (ChatCommandFeedLine.TryRead(m, out string commandUser))
            return (LiveFeedKind.Chat, commandUser);

        // Shape 2 — private whispers to the bot account, emitted through the
        // ephemeral LogTransient path. Matched on the exact " whispered:" verb
        // form — NOT a bare "whisper" substring — so a viewer or scene name that
        // merely contains "whisper" ("WhisperKing raided", "Scene → WhisperCam")
        // can't hijack the kind. Checked before the split because this is the one
        // shape whose actor comes FIRST, and because the whisper BODY is free text
        // that could otherwise contain " by " (or the word "raid") and steal the
        // row from itself. Detail keeps the full line.
        int whisperAt = m.IndexOf(" whispered:", StringComparison.OrdinalIgnoreCase);
        if (whisperAt > 0)
            return (LiveFeedKind.Whisper, m[..whisperAt].Trim());

        // Shapes 1 and 3 — head = the classifiable token, tail = the actor. The
        // FIRST " by " is the separator: an event type never contains one, while
        // a YouTube display name legitimately can.
        string head = m;
        string who = source;
        int byAt = m.IndexOf(" by ", StringComparison.Ordinal);
        if (byAt > 0)
        {
            head = m[..byAt];
            string actor = m[(byAt + 4)..].Trim();
            if (actor.Length > 0) who = actor;
        }

        // YouTube memberships are subs in Twitch vocabulary — NewSponsor /
        // MembershipGift / MemberMileStone bucket with Sub alongside Kick's
        // Subscription/GiftSubscription (already caught by the "sub" probe).
        if (head.Contains("sub", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("sponsor", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("membership", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("milestone", StringComparison.OrdinalIgnoreCase))
            return (LiveFeedKind.Sub, who);
        if (head.Contains("raid", StringComparison.OrdinalIgnoreCase))   return (LiveFeedKind.Raid,   who);
        if (head.Contains("follow", StringComparison.OrdinalIgnoreCase)) return (LiveFeedKind.Follow, who);
        // Monetary one-offs bucket with Redeem: Twitch cheers/redeems,
        // YouTube SuperChat/SuperSticker/JewelsGifted, Kick KicksGifted.
        if (head.Contains("redeem", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("reward", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("cheer",  StringComparison.OrdinalIgnoreCase) ||
            head.Contains("superchat", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("supersticker", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("kicksgifted", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("jewels", StringComparison.OrdinalIgnoreCase))
            return (LiveFeedKind.Redeem, who);
        // Default to Visual so the row still surfaces — better than dropping.
        return (LiveFeedKind.Visual, who);
    }


    private static DateTimeOffset ToUtcOffset(DateTime dt) =>
        dt.Kind switch
        {
            DateTimeKind.Utc        => new DateTimeOffset(dt, TimeSpan.Zero),
            DateTimeKind.Local      => new DateTimeOffset(dt),
            _                       => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)),
        };

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Bus.Instance.OnMessageReceived -= _onBus;
        GlobalLogger.OnLogEntry -= _onLog;
    }
}
