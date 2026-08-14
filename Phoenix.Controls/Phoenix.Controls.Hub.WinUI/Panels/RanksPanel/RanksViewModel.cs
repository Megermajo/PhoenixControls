using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using HubCore = Phoenix.Controls.Hub.Core;
using RanksSvc = Phoenix.Controls.Hub.Core.RanksService;
using UserMgmtSvc = Phoenix.Controls.Hub.Core.UserManagementService;

namespace Phoenix.Controls.Hub.WinUI.Panels.RanksPanel;

/// <summary>
/// ViewModel for the Hub Ranks page — a rank ladder over watch time or points, with a live
/// leaderboard, chat commands and an optional User-Management group grant per rung. Like
/// the Alerts / Polls VMs it reaches <see cref="RanksSvc.Instance"/> DIRECTLY for reads AND
/// writes and subscribes to its ConfigChanged / RuntimeChanged events.
///
/// Config editing model (clones AlertsViewModel): the whole <see cref="RanksConfig"/> is
/// deep-cloned into the rung rows + the scalar fields on construction. Every edit mutates a
/// working def and schedules a debounced save (400 ms) — toggles save immediately; the save
/// REBUILDS a fresh detached RanksConfig from the live rows and hands it to
/// <c>UpdateConfigAsync</c> (which deep-owns it, persists, and raises ConfigChanged). A
/// self-triggered ConfigChanged is detected by reference-equality against the last pushed
/// instance and skipped; a foreign one rebuilds the rows wholesale.
///
/// ★ The foreign-reload path is load-bearing here rather than merely tidy: the Ranks
/// runtime writes the User-Management config (the rank grant), and User-Management's own
/// panel does the same to this one's group list — so both pages have to notice a change
/// they did not make. That is also why <see cref="GroupOptions"/> is refreshed on load and
/// on every reload rather than filled once in the ctor.
///
/// The leaderboard preview is NEVER stored in the config — it is live databank /
/// service state, pulled on load and on RuntimeChanged.
/// </summary>
public sealed class RanksViewModel : ObservableObject, IDisposable
{
    private readonly RanksSvc _svc = RanksSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;
    // EVERY non-dormant pill state now depends on a fact NOTHING on this tool raises an
    // event for: the Loyalty master toggle, the Streamer.bot socket, and — since watch-time
    // accrual moved to the always-on ViewerPresenceService — the suite-wide counter switch
    // and the live gate behind FrozenCountingOff / ArmedWaitingForLive. RuntimeChanged only
    // fires when an accrual actually lands, which is precisely what stops happening in all
    // of those states, so a pill driven by events alone would sit on "accruing" forever at
    // exactly the moment it was needed. This repeating timer re-reads the service's pill
    // state (and the watch-time sentence, which reads the same AppConfig) and raises
    // NOTHING unless the answer moved.
    private readonly DispatcherQueueTimer? _pillTimer;

    private bool _masterEnabled;
    private string _metric = RankMetrics.WatchTime;
    private bool _announceEnabled;
    private string _announceMessage = "";
    private bool _chatEnabled;
    private string _rankCommand = "";
    private string _topCommand = "";
    private int _cooldownSeconds;
    private string _rankReplyMessage = "";
    private string _topRankReplyMessage = "";
    private string _unrankedReplyMessage = "";
    private string _noLadderReplyMessage = "";
    private string _topReplyMessage = "";
    private string _topEmptyMessage = "";
    private int _leaderboardSize;
    private bool _overlayEnabled;
    private int _overlaySize;

    private RanksConfig? _lastPushed;   // reference-equality self-trigger guard
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;

    public RanksViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();

            _pillTimer = dispatcher.CreateTimer();
            _pillTimer.Interval = TimeSpan.FromSeconds(2);
            _pillTimer.IsRepeating = true;
            _pillTimer.Tick += (_, _) => RefreshStatusPill();
            _pillTimer.Start();
        }

        var cfg = Clone(_svc.Config);
        ViewRoles = new RankRolesVm(cfg.ViewRoles ?? RankRoles.All(), ScheduleSave);
        ApplyScalars(cfg);
        BuildRows(cfg);
        RefreshStatusPill();

        _svc.ConfigChanged += OnConfigChanged;
        _svc.RuntimeChanged += OnRuntimeChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.RuntimeChanged -= OnRuntimeChanged;
        _pillTimer?.Stop();
        _saveTimer?.Stop();
        // Flush any pending edit so the last keystroke isn't lost on close. Parked, not
        // dropped: at shutdown MainWindow's coordinator ends in Environment.Exit(0), which
        // would kill this write mid-flight. The tracker lets PreBuildsHostView.DisposeAllTools
        // hand it to the coordinator as a tracked step.
        if (_dirty)
        {
            _dirty = false;
            var cfg = BuildConfig();
            _lastPushed = cfg;
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.UpdateConfigAsync(cfg), "RanksViewModel", "final config flush"));
        }
    }

    // ── Bound collections ────────────────────────────────────────────────
    /// <summary>The ladder, in the streamer's own order.</summary>
    public ObservableCollection<RankRowVm> Ranks { get; } = new();

    /// <summary>The SHARED item source behind every rung's grant picker — the live
    /// User-Management group list, prefixed with the "(no group)" sentinel.</summary>
    public ObservableCollection<string> GroupOptions { get; } = new();

    /// <summary>Read-only leaderboard preview: what the overlay and !ranks would print
    /// right now. Databank state, never config.</summary>
    public ObservableCollection<string> Leaderboard { get; } = new();

    /// <summary>The two role checkmark rows share one instance across the page.</summary>
    public RankRolesVm ViewRoles { get; }

    // ── Master switch (immediate save) ───────────────────────────────────
    public bool MasterEnabled
    {
        get => _masterEnabled;
        set
        {
            if (_masterEnabled == value) return;
            _masterEnabled = value;
            Raise(nameof(MasterEnabled));
            // WatchTimeStateText deliberately NOT raised here: recording no longer depends
            // on this switch at all, so the sentence cannot move when it flips.
            RefreshStatusPill();
            SaveNow();
        }
    }

    // ── Metric ───────────────────────────────────────────────────────────
    /// <summary>Index into the metric picker: 0 = watch time, 1 = points. An index rather
    /// than the raw string because a ComboBox two-way-binds SelectedIndex cleanly and the
    /// stored value is a normalized constant either way.</summary>
    public int MetricIndex
    {
        get => RankMetrics.Normalize(_metric) == RankMetrics.Points ? 1 : 0;
        set
        {
            string next = value == 1 ? RankMetrics.Points : RankMetrics.WatchTime;
            if (string.Equals(_metric, next, StringComparison.Ordinal)) return;
            _metric = next;
            Raise(nameof(MetricIndex));
            Raise(nameof(MetricHint));
            // Every rung's unit word and its hours hint follow the metric, so a switch that
            // left them saying "minutes" beside a points threshold would be a page telling
            // the streamer two different things about the same number.
            foreach (var row in Ranks) row.RaiseUnitChanged();
            // The pill's "frozen · Loyalty is off" state applies to the POINTS metric only,
            // so the metric switch can move it in either direction.
            RefreshStatusPill();
            SaveNow();
            _ = RefreshLeaderboardAsync();
        }
    }

    public string MetricHint => RankMetrics.Normalize(_metric) == RankMetrics.Points
        ? Localizer.T("panel.ranks.metric.hint.points",
            "Thresholds are points, read live from Loyalty's balance table (Loyalty must be on).")
        : Localizer.T("panel.ranks.metric.hint.watch_time",
            "Thresholds are watched MINUTES, counted into the open \"WatchTime\" databank table by the Hub's background presence sweep.");

    /// <summary>
    /// What watch-time recording is doing right now, and where its switches live. READ
    /// ONLY — this page no longer owns them.
    /// </summary>
    /// <remarks>
    /// <para>★ Recording is not a Ranks setting any more. The two toggles that used to sit
    /// on this card (<c>RanksConfig.TrackWatchTime</c> / <c>WatchTimeOnlineOnly</c>) were a
    /// suite-wide decision wearing one tool's clothes: minutes feed the User-Management
    /// watch-hour rules and the <c>!watchtime</c> command as much as this ladder, and a
    /// default install — where Ranks ships OFF — recorded nothing at all. They are now
    /// <c>AppConfig.WatchTimeTrackingEnabled</c> / <c>WatchTimeOnlyWhenLive</c>, read by the
    /// always-on <c>ViewerPresenceService</c>, and this sentence reports them rather than
    /// setting them.</para>
    ///
    /// <para>Deliberately independent of this tool's master toggle: accrual runs with every
    /// pre-build tool switched off, so a line that said "nothing is counted while the tool
    /// is off" would now be exactly wrong. The metric is not consulted either — minutes are
    /// recorded whether or not this ladder happens to measure them.</para>
    ///
    /// <para>The <c>?? true</c> fallbacks are the same ones <c>ViewerPresenceService</c> and
    /// <c>RanksService.PillState</c> use, so a config that has not finished loading reads as
    /// the shipped default rather than flashing "not being recorded".</para>
    /// </remarks>
    public string WatchTimeStateText
    {
        get
        {
            var cfg = ConfigManager.Current;
            if (!(cfg?.WatchTimeTrackingEnabled ?? true))
                return Localizer.T("panel.ranks.watchtime.state.not_recording",
                    "Watch time is NOT being recorded — a watch-time ladder will stay at zero. " +
                    "Switch it back on under User Management ▸ Watch Time.");
            return (cfg?.WatchTimeOnlyWhenLive ?? true)
                ? Localizer.T("panel.ranks.watchtime.state.live_only",
                    "Recorded in the background for every viewer while the stream is live, whatever this tool is set to. " +
                    "Settings live under User Management ▸ Watch Time.")
                : Localizer.T("panel.ranks.watchtime.state.always",
                    "Recorded in the background for every viewer, live or not, whatever this tool is set to. " +
                    "Settings live under User Management ▸ Watch Time.");
        }
    }

    // Last published sentence, so the 2 s pill tick raises nothing while nothing moved.
    // AppConfig is another page's surface and raises no event this VM subscribes to, so the
    // only way this card notices a change made over there is to look.
    private string _watchTimeStateText = "";

    // ── Announcement ─────────────────────────────────────────────────────
    public bool AnnounceEnabled
    {
        get => _announceEnabled;
        set { if (_announceEnabled == value) return; _announceEnabled = value; Raise(nameof(AnnounceEnabled)); SaveNow(); }
    }

    public string AnnounceMessage
    {
        get => _announceMessage;
        set { var v = value ?? ""; if (_announceMessage == v) return; _announceMessage = v; Raise(nameof(AnnounceMessage)); ScheduleSave(); }
    }

    // ── Chat commands ────────────────────────────────────────────────────
    public bool ChatEnabled
    {
        get => _chatEnabled;
        set { if (_chatEnabled == value) return; _chatEnabled = value; Raise(nameof(ChatEnabled)); SaveNow(); }
    }

    public string RankCommand
    {
        get => _rankCommand;
        set { var v = value ?? ""; if (_rankCommand == v) return; _rankCommand = v; Raise(nameof(RankCommand)); ScheduleSave(); }
    }

    public string TopCommand
    {
        get => _topCommand;
        set { var v = value ?? ""; if (_topCommand == v) return; _topCommand = v; Raise(nameof(TopCommand)); ScheduleSave(); }
    }

    /// <summary>
    /// Both chat verbs this tool answers to, as the CHAT COMMANDS block renders it.
    /// </summary>
    /// <remarks>
    /// <para>Built from <see cref="BuildConfig"/> — the very config the next save will push
    /// — rather than from a hand-picked few fields. This page keeps its editable state in
    /// scalars instead of a working config object, so a shortcut that constructed a
    /// <c>RanksConfig</c> carrying only the four fields the catalogue reads today would
    /// silently start rendering DEFAULTS the day it reads a fifth. BuildConfig costs one
    /// snapshot of the ladder rows, on a list of at most a handful, and only when an edit
    /// lands.</para>
    ///
    /// <para>A fresh list on every read, deliberately: <c>ToolCommandList.Commands</c> is a
    /// dependency property and repaints on reference inequality, so a cached instance
    /// edited in place would update nothing. Raised from <see cref="RaiseStats"/>, which
    /// every edit path and the foreign reload already funnel through.</para>
    /// </remarks>
    public IReadOnlyList<HubCore.ToolCommandInfo> ChatCommands
        => HubCore.BuiltInCommandCatalog.ForRanks(BuildConfig());

    public string CooldownText
    {
        get => _cooldownSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(0, ParseInt(value, _cooldownSeconds));
            if (_cooldownSeconds == v) { Raise(nameof(CooldownText)); return; }
            _cooldownSeconds = v;
            Raise(nameof(CooldownText));
            ScheduleSave();
        }
    }

    public string RankReplyMessage
    {
        get => _rankReplyMessage;
        set { var v = value ?? ""; if (_rankReplyMessage == v) return; _rankReplyMessage = v; Raise(nameof(RankReplyMessage)); ScheduleSave(); }
    }

    public string TopRankReplyMessage
    {
        get => _topRankReplyMessage;
        set { var v = value ?? ""; if (_topRankReplyMessage == v) return; _topRankReplyMessage = v; Raise(nameof(TopRankReplyMessage)); ScheduleSave(); }
    }

    public string UnrankedReplyMessage
    {
        get => _unrankedReplyMessage;
        set { var v = value ?? ""; if (_unrankedReplyMessage == v) return; _unrankedReplyMessage = v; Raise(nameof(UnrankedReplyMessage)); ScheduleSave(); }
    }

    /// <summary>The reply for a ladder with no reachable rung at all — a freshly enabled
    /// tool. Its own template because the unranked one names a {next} rank that does not
    /// exist yet, and renders as a sentence trailing off into nothing.</summary>
    public string NoLadderReplyMessage
    {
        get => _noLadderReplyMessage;
        set { var v = value ?? ""; if (_noLadderReplyMessage == v) return; _noLadderReplyMessage = v; Raise(nameof(NoLadderReplyMessage)); ScheduleSave(); }
    }

    public string TopReplyMessage
    {
        get => _topReplyMessage;
        set { var v = value ?? ""; if (_topReplyMessage == v) return; _topReplyMessage = v; Raise(nameof(TopReplyMessage)); ScheduleSave(); }
    }

    public string TopEmptyMessage
    {
        get => _topEmptyMessage;
        set { var v = value ?? ""; if (_topEmptyMessage == v) return; _topEmptyMessage = v; Raise(nameof(TopEmptyMessage)); ScheduleSave(); }
    }

    public string LeaderboardSizeText
    {
        get => _leaderboardSize.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Clamp(ParseInt(value, _leaderboardSize), 1, 100);
            if (_leaderboardSize == v) { Raise(nameof(LeaderboardSizeText)); return; }
            _leaderboardSize = v;
            Raise(nameof(LeaderboardSizeText));
            ScheduleSave();
        }
    }

    // ── Overlay ──────────────────────────────────────────────────────────
    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set { if (_overlayEnabled == value) return; _overlayEnabled = value; Raise(nameof(OverlayEnabled)); SaveNow(); }
    }

    public string OverlaySizeText
    {
        get => _overlaySize.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Clamp(ParseInt(value, _overlaySize), 1, 100);
            if (_overlaySize == v) { Raise(nameof(OverlaySizeText)); return; }
            _overlaySize = v;
            Raise(nameof(OverlaySizeText));
            ScheduleSave();
        }
    }

    // UI-thread only — callers are row edits (ScheduleSave / SaveNow), BuildRows, and the
    // _ui.Post continuations of the service event handlers.
    private void RaiseStats()
    {
        Raise(nameof(LadderEmptyVisibility));
        // The command block rides this raise rather than owning a second bookkeeping site:
        // every path that can change a verb, the chat switch or the role ticks already
        // funnels through here (ScheduleSave / SaveNow from any field or role edit,
        // BuildRows on a foreign reload, LoadAsync on first paint).
        Raise(nameof(ChatCommands));
    }

    /// <summary>Whether the ladder card should show its empty caption. Every path that adds
    /// or removes a rung already funnels through RaiseStats (AddRank / RemoveRank via
    /// ScheduleSave, BuildRows directly), so this rides that one raise rather than adding a
    /// second bookkeeping site.</summary>
    public Visibility LadderEmptyVisibility
        => Ranks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── Status pill (the header band's state phrase rides its raise) ─────
    // The state machine is RanksService.PillState — a pure service-side property, which is
    // the only form the test suite can reach (it cannot construct a Hub.WinUI view-model).
    // This VM names the state; it never re-derives one from config fields.
    //
    // ★ "frozen · Loyalty is off" is a POINTS-METRIC state and nothing wider. On the
    // watch-time metric the accrual is deliberately independent of the Loyalty master
    // toggle — an hour watched is an hour watched whether or not the points economy is on —
    // so the service's state machine only returns FrozenLoyaltyOff under the points metric,
    // and this wording must not be broadened to imply watch time stops too.

    private RanksSvc.RanksPillState _pillState = RanksSvc.RanksPillState.Dormant;

    public string StatusPillText => _pillState switch
    {
        // Points metric with the economy off: the balance the ladder reads is not moving.
        RanksSvc.RanksPillState.FrozenLoyaltyOff => Localizer.T("panel.ranks.pill.frozen_loyalty_off", "frozen · Loyalty is off"),
        // Watch-time tracking is on but its own online-only gate is holding it.
        RanksSvc.RanksPillState.ArmedWaitingForLive => Localizer.T("panel.ranks.pill.armed_waiting_live", "armed · waiting for live"),
        // Two-hop, and invisible on this page until now: accrual has no clock of its own —
        // it rides Loyalty's tick, whose viewer list comes from Streamer.bot. With the
        // socket down, nobody accrues.
        RanksSvc.RanksPillState.FrozenStreamerBotDown => Localizer.T("panel.ranks.pill.frozen_bot_down", "frozen · Streamer.bot down"),
        // Watch-time metric with the minute counter switched off. The strip used to say
        // "accruing" here — a green pulsing chip directly above the card that reads
        // "Counting is OFF — a watch-time ladder will stay at zero."
        RanksSvc.RanksPillState.FrozenCountingOff => Localizer.T("panel.ranks.pill.frozen_counting_off", "frozen · counting is off"),
        RanksSvc.RanksPillState.Accruing => Localizer.T("panel.ranks.pill.accruing", "accruing"),
        _ => Localizer.T("panel.ranks.pill.dormant", "dormant"),
    };

    /// <summary>UI thread only. Raises nothing when neither the pill state nor the
    /// watch-time sentence has moved, which is what makes it safe on the 2 s timer.</summary>
    private void RefreshStatusPill()
    {
        RanksSvc.RanksPillState next;
        try { next = _svc.PillState; }
        catch (Exception ex)
        {
            GlobalLogger.Error("RanksViewModel", "pill state read failed", ex);
            return;
        }

        if (next != _pillState)
        {
            _pillState = next;
            Raise(nameof(StatusPillText));
        }

        // The watch-time card describes AppConfig — a surface this page does not own and
        // that raises no event this VM subscribes to — so the only way the card notices a
        // change made on the User-Management page is to look. It rides this tick rather
        // than a second timer, and re-raises only when the sentence actually moved.
        string watch = WatchTimeStateText;
        if (!string.Equals(watch, _watchTimeStateText, StringComparison.Ordinal))
        {
            _watchTimeStateText = watch;
            Raise(nameof(WatchTimeStateText));
        }
    }

    // ── Load ─────────────────────────────────────────────────────────────
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        RefreshGroups();
        RaiseStats();
        await RefreshLeaderboardAsync().ConfigureAwait(false);
    }

    /// <summary>Re-reads the User-Management GRANTABLE group list into
    /// <see cref="GroupOptions"/> — Regular plus the custom groups, via
    /// <c>GrantableGroupNames()</c>, deliberately NOT <c>GroupNames()</c>: that one
    /// answers "which groups exist" and still lists Moderator / VIP / Subscriber,
    /// which are platform-owned and which <c>GrantGroupAsync</c> refuses — offering
    /// them here would let a rank save a grant that renders as valid and silently
    /// never applies. UI thread only. Kept lazy (load + reload + the ↻ button)
    /// rather than filled in the ctor, because the group list is another tool's
    /// config and can change while this page is open — including as a result of
    /// THIS tool's own grants.</summary>
    public void RefreshGroups()
    {
        var names = new List<string>();
        try { names.AddRange(UserMgmtSvc.Instance.GrantableGroupNames()); }
        catch (Exception ex)
        {
            // Keep whatever list we last resolved rather than blanking every open picker.
            GlobalLogger.Error("RanksViewModel", "group enumeration failed", ex);
            return;
        }

        bool same = names.Count == GroupOptions.Count;
        if (same)
        {
            for (int i = 0; i < names.Count; i++)
                if (!string.Equals(names[i], GroupOptions[i], StringComparison.Ordinal)) { same = false; break; }
        }
        if (same) return;
        GroupOptions.Clear();
        foreach (var n in names) GroupOptions.Add(n);
    }

    /// <summary>Pulls the live standings for the preview list. Databank state — never
    /// config — so it is re-read rather than remembered.</summary>
    public async Task RefreshLeaderboardAsync()
    {
        List<RankStanding> board;
        try { board = await _svc.TopAsync(Math.Max(1, _leaderboardSize)).ConfigureAwait(false); }
        catch (Exception ex)
        {
            GlobalLogger.Error("RanksViewModel", "leaderboard read failed", ex);
            return;
        }
        var lines = new List<string>(board.Count);
        foreach (var s in board)
        {
            // The rank NAME is the streamer's own word and stays verbatim; only the
            // "has no rank" stand-in is ours to translate.
            string rank = string.IsNullOrEmpty(s.Rank) ? Localizer.T("panel.ranks.board.unranked", "unranked") : s.Rank;
            lines.Add($"{s.Position}. {s.Name} — {s.Value.ToString(CultureInfo.InvariantCulture)} ({rank})");
        }
        _ui.Post(() =>
        {
            Leaderboard.Clear();
            foreach (var l in lines) Leaderboard.Add(l);
            Raise(nameof(LeaderboardEmptyVisibility));
        });
    }

    /// <summary>Visibility-typed rather than bool: x:Bind has no implicit bool→Visibility
    /// conversion, so a bool here would be a markup-compile error rather than a converter
    /// the page silently needs. Matches AlertEventVm.EmptyVisibility.</summary>
    public Visibility LeaderboardEmptyVisibility
        => Leaderboard.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── Ladder editing ───────────────────────────────────────────────────
    public void AddRank()
    {
        // A new rung starts one step above the highest existing threshold so it is
        // reachable on creation — a row that lands unreachable would flash the collision
        // caption the instant it appears, which reads as an error the streamer caused.
        //
        // The seed is 1, NOT 0: a threshold of 0 is unreachable by rule, because a value of
        // 0 means "nothing recorded" and is unranked whatever the ladder says
        // (RanksService.ResolveStanding). Seeding the very first rung at 0 would hand every
        // new streamer a bottom rung that silently never awards.
        long next = 1;
        foreach (var r in Ranks) if (r.Threshold >= next) next = r.Threshold + 1;
        var def = new RankDef { Name = "", Threshold = next, Enabled = true, GrantGroup = "" };
        Ranks.Add(NewRow(def));
        RefreshReachability();
        ScheduleSave();
    }

    private RankRowVm NewRow(RankDef def)
        => new(def, OnRowChanged, RemoveRank, () => RankMetrics.UnitFor(RankMetrics.Normalize(_metric), CurrencyNoun()), GroupOptions);

    private void RemoveRank(RankRowVm row)
    {
        if (row is null) return;
        Ranks.Remove(row);
        RefreshReachability();
        ScheduleSave();
    }

    private void OnRowChanged()
    {
        RefreshReachability();
        ScheduleSave();
    }

    // A rung is unreachable for one of two reasons, and the caption has to say which:
    //
    //   * an EARLIER enabled rung already claims its threshold — RanksService.Resolve keeps
    //     the first on a tie, so the later one can never win;
    //   * the threshold is 0 — a value of 0 means "nothing recorded" and is unranked by
    //     rule (RanksService.ResolveStanding), so no viewer can ever clear a 0 rung.
    //
    // Passive caption only — never rejects the value, never blocks the save; mid-edit
    // collisions are normal while typing.
    private void RefreshReachability()
    {
        var claimed = new HashSet<long>();
        foreach (var row in Ranks)
        {
            if (!row.Enabled) { row.SetUnreachable(null); continue; }
            if (row.Threshold <= 0)
            {
                row.SetUnreachable(Localizer.T("panel.ranks.row.unreachable.zero",
                    "0 can never be reached — use 1 or more."));
                continue;
            }
            row.SetUnreachable(claimed.Add(row.Threshold)
                ? null
                : Localizer.T("panel.ranks.row.unreachable.claimed",
                    "An earlier rank already claims this threshold — this one can never be reached."));
        }
    }

    // NOT localized, deliberately: this stands in for the streamer's OWN currency noun
    // (LoyaltyService's configured NamePlural), which is user data in whatever language
    // they typed it. A translated fallback would put a German word where every other
    // reading of the same slot shows their word, so the shipped default stays as-is.
    private static string CurrencyNoun()
    {
        try { return Phoenix.Controls.Hub.Core.LoyaltyService.Instance.Config.Currency.NamePlural; }
        catch { return "points"; }
    }

    // ── Persistence ──────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        RaiseStats();
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        RaiseStats();
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        // Build the fresh config on the current (UI) thread BEFORE awaiting so the snapshot
        // is taken from a stable, non-mutating view of the rows.
        var cfg = BuildConfig();
        _lastPushed = cfg;
        try { await _svc.UpdateConfigAsync(cfg).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("RanksViewModel", "UpdateConfigAsync failed", ex); }
        await RefreshLeaderboardAsync().ConfigureAwait(false);
    }

    // The single place the UI's editable state is projected back into a config blob — a
    // FRESH detached RanksConfig, never sharing row objects.
    private RanksConfig BuildConfig()
    {
        var cfg = new RanksConfig
        {
            Enabled = _masterEnabled,
            Metric = RankMetrics.Normalize(_metric),
            Ranks = new List<RankDef>(Ranks.Count),
            AnnounceEnabled = _announceEnabled,
            AnnounceMessage = _announceMessage ?? "",
            ChatEnabled = _chatEnabled,
            RankCommand = _rankCommand ?? "",
            TopCommand = _topCommand ?? "",
            ViewRoles = ViewRoles.ToSnapshot(),
            CooldownSeconds = _cooldownSeconds,
            RankReplyMessage = _rankReplyMessage ?? "",
            TopRankReplyMessage = _topRankReplyMessage ?? "",
            UnrankedReplyMessage = _unrankedReplyMessage ?? "",
            NoLadderReplyMessage = _noLadderReplyMessage ?? "",
            TopReplyMessage = _topReplyMessage ?? "",
            TopEmptyMessage = _topEmptyMessage ?? "",
            LeaderboardSize = _leaderboardSize,
            OverlayEnabled = _overlayEnabled,
            OverlaySize = _overlaySize,
        };
        foreach (var row in Ranks) cfg.Ranks.Add(row.ToSnapshot());
        return cfg;
    }

    // The non-collection fields, in ONE place. The ctor and the foreign-reload handler both
    // need the identical assignment list, and a field only one of them sets is a silent
    // desync — the User-Management queue's dozen-plus settings made that a when, not an if.
    private void ApplyScalars(RanksConfig cfg)
    {
        _masterEnabled = cfg.Enabled;
        _metric = RankMetrics.Normalize(cfg.Metric);
        _announceEnabled = cfg.AnnounceEnabled;
        _announceMessage = cfg.AnnounceMessage ?? "";
        _chatEnabled = cfg.ChatEnabled;
        _rankCommand = cfg.RankCommand ?? "";
        _topCommand = cfg.TopCommand ?? "";
        _cooldownSeconds = cfg.CooldownSeconds;
        _rankReplyMessage = cfg.RankReplyMessage ?? "";
        _topRankReplyMessage = cfg.TopRankReplyMessage ?? "";
        _unrankedReplyMessage = cfg.UnrankedReplyMessage ?? "";
        _noLadderReplyMessage = cfg.NoLadderReplyMessage ?? "";
        _topReplyMessage = cfg.TopReplyMessage ?? "";
        _topEmptyMessage = cfg.TopEmptyMessage ?? "";
        _leaderboardSize = cfg.LeaderboardSize;
        _overlayEnabled = cfg.OverlayEnabled;
        _overlaySize = cfg.OverlaySize;
    }

    private void BuildRows(RanksConfig cfg)
    {
        Ranks.Clear();
        foreach (var def in cfg.Ranks) Ranks.Add(NewRow(def));
        RefreshReachability();
        RaiseStats();
    }

    // ── Service events (SafeEvent — raised on a background thread; MUST marshal) ─
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Self-triggered save assigns our pushed instance as the live config → skip.
        if (ReferenceEquals(_svc.Config, _lastPushed)) return;
        _ui.Post(() =>
        {
            var cfg = Clone(_svc.Config);
            ApplyScalars(cfg);
            // ViewRoles wraps a roles object captured in the ctor, so a foreign reload has
            // to reseat it or the checkmarks would keep describing the config we replaced.
            ViewRoles.Reseat(cfg.ViewRoles ?? RankRoles.All());
            BuildRows(cfg);
            RefreshGroups();
            RefreshStatusPill();
            Raise(nameof(MasterEnabled));
            Raise(nameof(MetricIndex));
            Raise(nameof(MetricHint));
            // WatchTimeStateText is NOT raised here: it reads AppConfig, which a RanksConfig
            // reload cannot have changed. RefreshStatusPill above is what notices that one.
            Raise(nameof(AnnounceEnabled));
            Raise(nameof(AnnounceMessage));
            Raise(nameof(ChatEnabled));
            Raise(nameof(RankCommand));
            Raise(nameof(TopCommand));
            Raise(nameof(CooldownText));
            Raise(nameof(RankReplyMessage));
            Raise(nameof(TopRankReplyMessage));
            Raise(nameof(UnrankedReplyMessage));
            Raise(nameof(NoLadderReplyMessage));
            Raise(nameof(TopReplyMessage));
            Raise(nameof(TopEmptyMessage));
            Raise(nameof(LeaderboardSizeText));
            Raise(nameof(OverlayEnabled));
            Raise(nameof(OverlaySizeText));
        });
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            // WatchTimeStateText rides RefreshStatusPill's own moved-only check rather than
            // an unconditional raise — an accrual landing cannot change what AppConfig says.
            RefreshStatusPill();
        });

    // Deep clone through JSON round-trip (mirrors the Alerts / Scheduling VMs) so the
    // working copy is fully detached from the live service config.
    private static RanksConfig Clone(RanksConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new RanksConfig());
            return JsonSerializer.Deserialize<RanksConfig>(json) ?? new RanksConfig();
        }
        catch { return new RanksConfig(); }
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
