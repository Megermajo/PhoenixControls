using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.UI;
using HubCore = Phoenix.Controls.Hub.Core;
using PollsSvc = Phoenix.Controls.Hub.Core.PollsService;

namespace Phoenix.Controls.Hub.WinUI.Panels.PollsPanel;

/// <summary>
/// ViewModel for the Hub Polls page — the button-side front-end onto the same poll the
/// poll.* script nodes and the !vote / !bet chat commands drive. Reaches
/// <see cref="PollsSvc.Instance"/> DIRECTLY for reads AND writes and subscribes to its
/// ConfigChanged / PollChanged events.
///
/// Config editing model: the whole <see cref="PollsConfig"/> is deep-cloned into a private
/// working copy. Every settings field edits that clone and schedules a debounced
/// <c>SaveConfigAsync</c>. Because SaveConfigAsync deep-clones AND Normalize()s the incoming
/// object, the persisted <c>_svc.Config</c> is a different reference AND can differ
/// byte-for-byte from <c>_working</c> (trimmed command words, clamped numbers) — so a
/// serialized-content compare would mis-read our own save as a foreign change and rebuild
/// the settings mid-edit. A one-shot <c>_selfSaving</c> flag detects the self-save
/// deterministically instead; this is the QuotesViewModel flavour of the guard, and it is
/// the correct one for a service that clones.
///
/// The live poll is NEVER cached in the config — it comes from <c>Snapshot()</c> and
/// refreshes on load, on PollChanged, and on the once-a-second countdown tick that keeps
/// "seconds left" honest between changes.
/// </summary>
public sealed class PollsViewModel : ObservableObject, IDisposable
{
    private readonly PollsSvc _svc = PollsSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;
    // The live poll paints a COUNTDOWN, and nothing raises PollChanged while it simply
    // ticks — so without this the panel would show a frozen "45s left" until the next vote
    // landed. Repeating, unlike the save timer.
    //
    // ★ It runs for the WHOLE session — a tool tab is hidden when you switch away, never
    // destroyed — so every tick must be free when nothing moved. RefreshPoll therefore
    // fingerprints the snapshot and raises nothing unless the fingerprint changed, the same
    // posture as SchedulingViewModel.RefreshPill and SongRequestViewModel's pill heartbeat.
    private readonly DispatcherQueueTimer? _tickTimer;

    private PollsConfig _working;
    private bool _disposed;
    private bool _dirty;
    private bool _selfSaving;

    public PollsViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        _working = Clone(_svc.Config);

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();

            _tickTimer = dispatcher.CreateTimer();
            _tickTimer.Interval = TimeSpan.FromSeconds(1);
            _tickTimer.IsRepeating = true;
            _tickTimer.Tick += (_, _) => RefreshPoll();
            _tickTimer.Start();
        }

        BuildSettings();
        RefreshPoll();

        _svc.ConfigChanged += OnConfigChanged;
        _svc.PollChanged += OnPollChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.PollChanged -= OnPollChanged;
        _tickTimer?.Stop();
        _saveTimer?.Stop();
        if (_dirty)
        {
            _dirty = false;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which would kill this write mid-flight. The tracker lets
            // PreBuildsHostView.DisposeAllTools hand it to the coordinator as a tracked step;
            // mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.SaveConfigAsync(_working), "PollsViewModel", "final config flush"));
        }
    }

    /// <summary>Nothing to load asynchronously — the poll is in-memory and the config is
    /// already resident. Kept so the View's Loaded handler matches every sibling tool.</summary>
    public Task LoadAsync()
    {
        RefreshPoll();
        return Task.CompletedTask;
    }

    // ── Master switch ───────────────────────────────────────────────────
    public bool MasterEnabled
    {
        get => _working.Enabled;
        set
        {
            if (_working.Enabled == value) return;
            _working.Enabled = value;
            Raise(nameof(MasterEnabled));
            RefreshStatusPill();
            SaveNow();
        }
    }

    // ── Command words ───────────────────────────────────────────────────
    public string VoteCommand
    {
        get => _working.VoteCommand ?? "";
        set { var v = value ?? ""; if ((_working.VoteCommand ?? "") == v) return; _working.VoteCommand = v; Raise(nameof(VoteCommand)); UpdateVerbWarnings(); ScheduleSave(); }
    }

    public string BetCommand
    {
        get => _working.BetCommand ?? "";
        set { var v = value ?? ""; if ((_working.BetCommand ?? "") == v) return; _working.BetCommand = v; Raise(nameof(BetCommand)); UpdateVerbWarnings(); ScheduleSave(); }
    }

    // ── The page's own chat verbs ───────────────────────────────────────
    /// <summary>
    /// Every chat verb this tool answers to, as the CHAT COMMANDS block renders it —
    /// derived from the WORKING config, so a verb the streamer just typed shows
    /// immediately rather than 400 ms later when the debounced save lands.
    /// </summary>
    /// <remarks>
    /// <para>A fresh list on every read, deliberately. <c>ToolCommandList.Commands</c> is a
    /// dependency property, so it repaints on reference inequality — handing back a cached
    /// instance after editing it in place would update nothing. Every
    /// <c>BuiltInCommandCatalog.For*</c> call allocates, so the natural shape is correct
    /// here; the cost is two rows.</para>
    ///
    /// <para>Raised from <c>ScheduleSave</c> / <c>SaveNow</c> / <c>BuildSettings</c>, which
    /// between them are every path that can move a verb, a role tick or the betting switch
    /// — including a foreign reload. Deliberately NOT raised from the once-a-second poll
    /// tick: nothing that tick reads appears in this list, and rebuilding it 60 times a
    /// minute for the life of the tab is exactly the churn the tick's signature guards
    /// avoid elsewhere in this file.</para>
    /// </remarks>
    public IReadOnlyList<HubCore.ToolCommandInfo> ChatCommands
        => HubCore.BuiltInCommandCatalog.ForPolls(_working);

    // ── Poll shape ──────────────────────────────────────────────────────
    public string DefaultDurationSeconds
    {
        get => _working.DefaultDurationSeconds.ToString(CultureInfo.InvariantCulture);
        set { _working.DefaultDurationSeconds = ParseInt(value, _working.DefaultDurationSeconds); Raise(nameof(DefaultDurationSeconds)); ScheduleSave(); }
    }

    public string MaxOptions
    {
        get => _working.MaxOptions.ToString(CultureInfo.InvariantCulture);
        set { _working.MaxOptions = ParseInt(value, _working.MaxOptions); Raise(nameof(MaxOptions)); Raise(nameof(MirrorHint)); ScheduleSave(); }
    }

    public bool AllowVoteChange
    {
        get => _working.AllowVoteChange;
        set { if (_working.AllowVoteChange == value) return; _working.AllowVoteChange = value; Raise(nameof(AllowVoteChange)); ScheduleSave(); }
    }

    public bool AnnounceInChat
    {
        get => _working.AnnounceInChat;
        set { if (_working.AnnounceInChat == value) return; _working.AnnounceInChat = value; Raise(nameof(AnnounceInChat)); ScheduleSave(); }
    }

    // ── Betting ─────────────────────────────────────────────────────────
    public bool BettingEnabled
    {
        get => _working.BettingEnabled;
        set
        {
            if (_working.BettingEnabled == value) return;
            _working.BettingEnabled = value;
            Raise(nameof(BettingEnabled));
            Raise(nameof(BettingToggleLabel));
            Raise(nameof(BettingHint));
            // A start form offering "take bets" that the tool would silently ignore is the
            // confusing state; keep the two in step.
            if (!value && StartBetting) StartBetting = false;
            RefreshStatusPill();
            ScheduleSave();
        }
    }

    /// <summary>The betting pill's label. This switch has SEMANTIC on/off content — the
    /// stock control it replaced said what each state means rather than "On" / "Off" — so
    /// the current-state sentence travels with the pill.</summary>
    public string BettingToggleLabel => _working.BettingEnabled
        ? Localizer.T("panel.polls.betting.toggle.on", "Polls may take points stakes")
        : Localizer.T("panel.polls.betting.toggle.off", "Votes only");

    public string MinBet
    {
        get => _working.MinBet.ToString(CultureInfo.InvariantCulture);
        set { _working.MinBet = ParseLong(value, _working.MinBet); Raise(nameof(MinBet)); ScheduleSave(); }
    }

    public string MaxBet
    {
        get => _working.MaxBet.ToString(CultureInfo.InvariantCulture);
        set { _working.MaxBet = ParseLong(value, _working.MaxBet); Raise(nameof(MaxBet)); ScheduleSave(); }
    }

    public string BettingHint => _working.BettingEnabled
        ? Localizer.T("panel.polls.betting.hint.on",
                      "stakes charged on the bet · winners split the pot pro rata · tie / no votes / cancel refunds")
        : Localizer.T("panel.polls.betting.hint.off", "Off — no points are staked.");

    // ── Native mirror ───────────────────────────────────────────────────
    public bool MirrorToNative
    {
        get => _working.MirrorToNative;
        set { if (_working.MirrorToNative == value) return; _working.MirrorToNative = value; Raise(nameof(MirrorToNative)); ScheduleSave(); }
    }

    public string MirrorHint => Localizer.T("panel.polls.mirror.hint",
        "Runs a native Twitch poll (a prediction when the poll takes bets). 2-5 options only — " +
        "a wider poll stays chat-only, as does a Streamer.bot pack missing the open or close action.");

    // ── Overlay ─────────────────────────────────────────────────────────
    public bool PublishOverlay
    {
        get => _working.PublishOverlay;
        set { if (_working.PublishOverlay == value) return; _working.PublishOverlay = value; Raise(nameof(PublishOverlay)); ScheduleSave(); }
    }

    public string ResultLingerSeconds
    {
        get => _working.ResultLingerSeconds.ToString(CultureInfo.InvariantCulture);
        set { _working.ResultLingerSeconds = ParseInt(value, _working.ResultLingerSeconds); Raise(nameof(ResultLingerSeconds)); ScheduleSave(); }
    }

    // ── Role checkmark sets (rebuilt on foreign reload) ─────────────────
    public PollRolesVm VoteRoles { get; private set; } = null!;
    public PollRolesVm BetRoles { get; private set; } = null!;

    private void BuildSettings()
    {
        _working.VoteRoles ??= PollRoles.All();
        _working.BetRoles ??= PollRoles.All();
        VoteRoles = new PollRolesVm(_working.VoteRoles, ScheduleSave);
        BetRoles = new PollRolesVm(_working.BetRoles, ScheduleSave);
        Raise(nameof(VoteRoles));
        Raise(nameof(BetRoles));
        Raise(nameof(VoteCommand));
        Raise(nameof(BetCommand));
        Raise(nameof(DefaultDurationSeconds));
        Raise(nameof(MaxOptions));
        Raise(nameof(AllowVoteChange));
        Raise(nameof(AnnounceInChat));
        Raise(nameof(BettingEnabled));
        Raise(nameof(BettingToggleLabel));
        Raise(nameof(BettingHint));
        Raise(nameof(MinBet));
        Raise(nameof(MaxBet));
        Raise(nameof(MirrorToNative));
        Raise(nameof(PublishOverlay));
        Raise(nameof(ResultLingerSeconds));
        Raise(nameof(MasterEnabled));
        Raise(nameof(ChatCommands));
        UpdateVerbWarnings();
        RefreshStatusPill();
    }

    // ── Chat-verb shadowing ─────────────────────────────────────────────
    // VoteCommand and BetCommand are free text, and the built-in chat dispatch is
    // FIRST-HANDLED-WINS with nothing logged when a provider consumes a line. So a streamer
    // who types "top" into the vote box gets a poll verb that never fires once — Loyalty
    // answered it three slots earlier — with no error anywhere to explain it. This is the
    // only place that can say so.

    private string _voteWarning = "";
    private string _betWarning = "";

    public string VoteCommandWarning => _voteWarning;
    public string BetCommandWarning => _betWarning;
    public Visibility VoteCommandWarningVisibility => _voteWarning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BetCommandWarningVisibility => _betWarning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    // The tick's throttled entry point. The cross-tool half of this check is the one part
    // of the once-a-second tick that is not free — it rebuilds each earlier tool's verb
    // list from that tool's live config — and the question it answers is "has someone
    // renamed a command in ANOTHER tab", which is not a per-second question. So the tick
    // asks every fourth beat; every EDIT path still calls UpdateVerbWarnings directly and
    // answers on the keystroke.
    private int _verbScanTick;

    private void UpdateVerbWarningsOnTick()
    {
        if (++_verbScanTick < 4) return;
        _verbScanTick = 0;
        UpdateVerbWarnings();
    }

    // Recomputed on every edit and (throttled) on the poll tick, because another tool's
    // command can be renamed in a different tab while this page is open. Only raises when
    // the text actually moves — a binding update a second, forever, for an unchanged string
    // is exactly the churn the option-row signature avoids on the same timer.
    private void UpdateVerbWarnings()
    {
        string vote = OwnerWarning(VoteCommand);

        string bet = OwnerWarning(BetCommand);
        // The tool can also shadow ITSELF: the parser tests the vote verb first, so two
        // identical words leave !bet permanently unreachable. Compared through ChatVerb
        // because the PARSER does: "!vote" and "vote" are one word to it, and a plain trim
        // would call that pair different and stay silent about a dead command.
        if (bet.Length == 0 && ChatVerb.Matches(BetCommand, VoteCommand))
            bet = Localizer.T("panel.polls.commands.bet.warning.same_word",
                              "Same word as the vote command — votes match first, so betting never fires.");

        if (vote != _voteWarning)
        {
            _voteWarning = vote;
            Raise(nameof(VoteCommandWarning));
            Raise(nameof(VoteCommandWarningVisibility));
        }
        if (bet != _betWarning)
        {
            _betWarning = bet;
            Raise(nameof(BetCommandWarning));
            Raise(nameof(BetCommandWarningVisibility));
        }
    }

    private string OwnerWarning(string word)
    {
        string owner = ReservedVerbOwner(word) ?? "";
        if (owner.Length == 0) return "";
        // The BANG is added here, so the word must arrive without one — a streamer who
        // typed "!vote" into the box would otherwise be told about "!!vote", a token no
        // parser will ever see. Canonical is the same trim-and-strip the comparison itself
        // ran, so the sentence names exactly the word that collided.
        return string.Format(
            Localizer.T("panel.polls.commands.warning.shadowed",
                        "{0} already answers !{1} and runs first — this word never reaches the poll."),
            owner, ChatVerb.Canonical(word));
    }

    /// <summary>
    /// The display name of the built-in tool that already answers <paramref name="word"/>,
    /// or null when the word is free.
    ///
    /// <para>Only the providers that run BEFORE Polls are consulted, because only those can
    /// shadow it. The dispatch is first-handled-wins in one fixed order —
    /// Automod → UserManagement → Loyalty → UserQueue → Counters → Quotes → SongRequest →
    /// <b>Polls</b> → Ranks → Soundboard → CustomCommands — so Ranks, Soundboard and Custom
    /// Commands all LOSE to a Polls verb, and warning about them here would point the
    /// streamer at the wrong tool. Passing this page's own slot is the whole of that rule:
    /// <see cref="Phoenix.Controls.Hub.Core.BuiltInCommandOrder.OwnerAhead"/> consults the
    /// slots ahead of it and nothing else.</para>
    ///
    /// <para>Still read from the LIVE service configs rather than a table of defaults, so
    /// renaming another tool's command changes this answer instead of quietly making it
    /// wrong; and enabled state is still NOT consulted, which is why the warning text says
    /// "runs first" rather than "is running". Both rules now live in
    /// <c>BuiltInCommandOrder</c> — this method is the page's name for them.</para>
    ///
    /// <para>★ What re-pointing FIXED, beyond the duplication. The table this replaced
    /// spelled Automod's permit verb and Loyalty's two duel replies as hard-coded literals
    /// (they are configurable now, so a renamed one went unreported), and it compared with
    /// a local helper that only trimmed — so a Loyalty trigger saved as "!points" matched
    /// in chat, where every provider goes through <c>ChatVerb</c>, while being invisible
    /// here. The catalogue's rows are canonical and so is the word on the way in.</para>
    /// </summary>
    public string? ReservedVerbOwner(string word)
        => Phoenix.Controls.Hub.Core.BuiltInCommandOrder.OwnerAhead(
               Phoenix.Controls.Hub.Core.BuiltInChatSlot.Polls, word);

    // ── Start form ──────────────────────────────────────────────────────
    private string _startTitle = "";
    public string StartTitle
    {
        get => _startTitle;
        set { _startTitle = value ?? ""; Raise(nameof(StartTitle)); }
    }

    private string _startOptions = "";
    public string StartOptions
    {
        get => _startOptions;
        set { _startOptions = value ?? ""; Raise(nameof(StartOptions)); Raise(nameof(CanStart)); }
    }

    private string _startDuration = "";
    public string StartDuration
    {
        get => _startDuration;
        set { _startDuration = value ?? ""; Raise(nameof(StartDuration)); }
    }

    private bool _startBetting;
    public bool StartBetting
    {
        get => _startBetting;
        set { if (_startBetting == value) return; _startBetting = value; Raise(nameof(StartBetting)); }
    }

    /// <summary>Whether the typed options could open a poll — two distinct choices is the
    /// floor. Drives the START button so the streamer sees the rule before the click
    /// rather than as a refusal after it.</summary>
    public bool CanStart => PollsSvc.SplitOptions(_startOptions).Count >= 2;

    private string _startFeedback = "";
    /// <summary>The last outcome of a panel-driven start/close/cancel, in plain words.
    /// Never a modal — a repeatable refusal belongs on the surface that caused it.</summary>
    public string StartFeedback
    {
        get => _startFeedback;
        private set { _startFeedback = value ?? ""; Raise(nameof(StartFeedback)); }
    }

    public async Task StartPollAsync()
    {
        var options = PollsSvc.SplitOptions(_startOptions);
        if (options.Count < 2)
        {
            StartFeedback = Localizer.T("panel.polls.feedback.need_two_options",
                                        "Give at least two options, separated by commas.");
            return;
        }
        int duration = ParseInt(_startDuration, 0);

        try
        {
            var res = await _svc.OpenAsync(_startTitle, options, duration, _startBetting,
                                           _working.MirrorToNative, "panel").ConfigureAwait(false);
            _ui.Post(() =>
            {
                StartFeedback = DescribeOpen(res);
                if (res.Ok) { StartTitle = ""; StartOptions = ""; }
            });
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PollsViewModel", "StartPollAsync failed", ex);
            _ui.Post(() => StartFeedback = Localizer.T("panel.polls.feedback.open_failed",
                                                       "That poll could not be opened — see the System Log."));
        }
    }

    // Names the mirror's verdict rather than swallowing it: a streamer who ticked
    // "mirror to Twitch" and got a chat-only poll must be told here, on the surface that
    // asked, not only in the System Log.
    private static string DescribeOpen(PollOpenResult res) => res.Outcome switch
    {
        PollOpenOutcome.Opened => res.Mirror switch
        {
            PollMirrorOutcome.Mirrored => Localizer.T("panel.polls.feedback.opened_mirrored",
                "Poll opened, and mirrored onto Twitch."),
            PollMirrorOutcome.NotRequested => Localizer.T("panel.polls.feedback.opened", "Poll opened."),
            PollMirrorOutcome.NotConnected => Localizer.T("panel.polls.feedback.opened_not_connected",
                "Poll opened in chat. Streamer.bot is not connected, so nothing was mirrored to Twitch."),
            PollMirrorOutcome.Unsupported => Localizer.T("panel.polls.feedback.opened_unsupported",
                "Poll opened in chat. Twitch takes 2-5 options and needs a question, so this one was not mirrored."),
            _ => Localizer.T("panel.polls.feedback.opened_no_action",
                "Poll opened in chat. Your Streamer.bot action pack is missing an open or close action, so nothing was mirrored."),
        },
        PollOpenOutcome.AlreadyOpen => Localizer.T("panel.polls.feedback.already_open",
            "A poll is already running — close it first."),
        PollOpenOutcome.NotEnoughOptions => Localizer.T("panel.polls.feedback.not_enough_options",
            "Give at least two different options."),
        PollOpenOutcome.TooManyOptions => Localizer.T("panel.polls.feedback.too_many_options",
            "That is more options than the maximum set below."),
        // Checked at OPEN time against the live economy, so this is a refusal the streamer
        // can act on before chat sees a poll — not something discovered one refused bettor
        // at a time with the poll already running.
        PollOpenOutcome.EconomyOff => Localizer.T("panel.polls.feedback.economy_off",
            "Betting needs the Loyalty tool on with a currency table."),
        _ => Localizer.T("panel.polls.feedback.tool_off",
            "The tool is switched off — turn it on first."),
    };

    public async Task ClosePollAsync()
    {
        try
        {
            var res = await _svc.CloseAsync("panel").ConfigureAwait(false);
            _ui.Post(() => StartFeedback = res.Ok
                ? DescribeSettlement(res)
                : Localizer.T("panel.polls.feedback.close_no_poll", "No poll is running."));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PollsViewModel", "ClosePollAsync failed", ex);
            _ui.Post(() => StartFeedback = Localizer.T("panel.polls.feedback.close_failed",
                                                       "That poll could not be closed — see the System Log."));
        }
    }

    public async Task CancelPollAsync()
    {
        try
        {
            var res = await _svc.CancelAsync("panel").ConfigureAwait(false);
            _ui.Post(() => StartFeedback = res.Ok
                ? DescribeSettlement(res)
                : Localizer.T("panel.polls.feedback.cancel_no_poll", "No poll is running."));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PollsViewModel", "CancelPollAsync failed", ex);
            _ui.Post(() => StartFeedback = Localizer.T("panel.polls.feedback.cancel_failed",
                                                       "That poll could not be cancelled — see the System Log."));
        }
    }

    // Surfaces a SHORT payout rather than reporting a clean close over the top of it: the
    // settlement returns how many credits actually landed, and fewer than were owed is the
    // shape a lost payout takes.
    private static string DescribeSettlement(PollCloseResult res)
    {
        string head = res.Cancelled
            ? Localizer.T("panel.polls.settlement.cancelled", "Poll cancelled.")
            : Localizer.T("panel.polls.settlement.closed", "Poll closed.");
        var s = res.Settlement;
        if (s.Outcome == PollSettlementOutcome.NoStakes) return head;
        string what = s.Outcome == PollSettlementOutcome.Paid
            ? Localizer.T("panel.polls.settlement.paid", "paid")
            : Localizer.T("panel.polls.settlement.refunded", "refunded");
        // The separating space stays in code rather than riding inside the key's value —
        // a leading space in a bundle entry is the first thing a translator's editor eats.
        if (s.Applied < s.Payouts.Count)
            return head + " " + string.Format(
                Localizer.T("panel.polls.settlement.short_payout",
                            "Only {0} of {1} could be {2} — see the System Log."),
                s.Applied.ToString(CultureInfo.InvariantCulture), Viewers(s.Payouts.Count), what);
        return head + " " + string.Format(
            Localizer.T("panel.polls.settlement.line", "{0} {1} to {2}."),
            Points(s.Pot), what, Viewers(s.Payouts.Count));
    }

    // Spelled-out plurals rather than "point(s)" / "viewer(s)": a settlement line is read
    // once, mid-stream, and the parenthesised form is the thing this page was told to drop.
    private static string Points(long n) =>
        n == 1
            ? Localizer.T("panel.polls.settlement.point_one", "1 point")
            : string.Format(Localizer.T("panel.polls.settlement.points_other", "{0} points"),
                            n.ToString(CultureInfo.InvariantCulture));

    private static string Viewers(int n) =>
        n == 1
            ? Localizer.T("panel.polls.settlement.viewer_one", "1 viewer")
            : string.Format(Localizer.T("panel.polls.settlement.viewers_other", "{0} viewers"),
                            n.ToString(CultureInfo.InvariantCulture));

    // ── Live poll ───────────────────────────────────────────────────────
    public ObservableCollection<PollOptionRowVm> Options { get; } = new();

    private PollSnapshot _snapshot = new();

    public bool IsPollOpen => _snapshot.State == PollState.Open;
    public bool HasPoll => _snapshot.State != PollState.Idle;
    public Visibility PollVisibility => HasPoll ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => HasPoll ? Visibility.Collapsed : Visibility.Visible;

    public string PollTitleText => _snapshot.Title.Length > 0
        ? _snapshot.Title
        : Localizer.T("panel.polls.live.no_question", "(no question)");
    public string TotalVotesValue => _snapshot.TotalVotes.ToString(CultureInfo.InvariantCulture);
    public string LeaderText => _snapshot.State == PollState.Idle
        ? ""
        : _snapshot.Leader.Length > 0
            ? (_snapshot.State == PollState.Closed
                ? string.Format(Localizer.T("panel.polls.live.winner", "Winner: {0}"), _snapshot.Leader)
                : string.Format(Localizer.T("panel.polls.live.leading", "Leading: {0}"), _snapshot.Leader))
            : (_snapshot.TotalVotes == 0
                ? Localizer.T("panel.polls.live.no_votes", "No votes yet")
                : Localizer.T("panel.polls.live.tied", "Tied — no single leader"));

    /// <summary>Names the mirror on the live card, and only when it actually engaged — a
    /// requested-but-refused mirror must never read as a running Twitch poll.</summary>
    public string MirrorStatusText => _snapshot.MirrorEngaged
        ? (_snapshot.Mirror == PollMirrorMode.Prediction
            ? Localizer.T("panel.polls.live.mirrored_prediction", "Mirrored as a Twitch prediction")
            : Localizer.T("panel.polls.live.mirrored_poll", "Mirrored as a Twitch poll"))
        : "";

    // Cheap fingerprint of everything the option rows render. The countdown ticks once a
    // second whether or not a vote landed, and rebuilding the row list on every one of those
    // ticks would tear the ItemsControl down and back up sixty times a minute for no visible
    // change — so the rows are rebuilt only when this moves.
    private string _optionsSignature = "";

    // The same idea for the scalar properties below the rows. Without it the tick
    // raised them all once a second for the life of the tab whether or not a poll existed;
    // an idle tool has no moving field at all, so an idle tick must cost one snapshot read
    // and nothing else. Covers every field the getters project, so a change to any of
    // them still repaints on the very next tick.
    private string _snapshotSignature = "";

    private void RefreshPoll()
    {
        var snap = _svc.Snapshot();
        _snapshot = snap;

        var sig = new System.Text.StringBuilder(snap.BettingEnabled ? "b" : "-");
        foreach (var o in snap.Options)
            sig.Append('|').Append(o.Label).Append(':').Append(o.Votes).Append(':').Append(o.Stake);
        string signature = sig.ToString();

        if (signature != _optionsSignature)
        {
            _optionsSignature = signature;
            // Rebuild rather than reconcile: nothing here is editable, the option list is
            // fixed for the poll's whole life, and there is no in-progress edit to preserve
            // or scroll position worth defending on a list of at most a handful of rows.
            Options.Clear();
            foreach (var o in snap.Options) Options.Add(new PollOptionRowVm(o, snap.BettingEnabled));
        }

        var head = new System.Text.StringBuilder();
        head.Append((int)snap.State).Append('|')
            .Append(snap.Title).Append('|')
            .Append(snap.TotalVotes.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(snap.Pot.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(snap.SecondsLeft.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(snap.Leader).Append('|')
            .Append(snap.BettingEnabled ? '1' : '0').Append('|')
            .Append(snap.MirrorEngaged ? '1' : '0').Append('|')
            .Append((int)snap.Mirror);
        string headSignature = head.ToString();

        if (headSignature != _snapshotSignature)
        {
            _snapshotSignature = headSignature;
            Raise(nameof(IsPollOpen));
            Raise(nameof(HasPoll));
            Raise(nameof(PollVisibility));
            Raise(nameof(EmptyVisibility));
            Raise(nameof(PollTitleText));
            Raise(nameof(TotalVotesValue));
            Raise(nameof(LeaderText));
            Raise(nameof(MirrorStatusText));
        }

        // Rides the existing once-a-second tick rather than a second timer: another tool's
        // command word can be renamed in a different tab while this page sits open. Its own
        // throttle (see UpdateVerbWarningsOnTick) keeps the cross-tool scan off three ticks
        // in four, and it raises nothing unless the answer moved.
        UpdateVerbWarningsOnTick();
        RefreshStatusPill();
    }

    // ── Status pill (the action strip's live state chip) ─────────────────
    // The state machine is PollsService.PillState — a pure service-side property, which is
    // the only form the test suite can reach (it cannot construct a Hub.WinUI view-model).
    // This VM names and colours the state; it never re-derives one from config fields,
    // because two surfaces computing the same state independently is how they end up
    // disagreeing.
    //
    // ⚠ One state from the design notes is deliberately NOT built: "open · Twitch mirror
    // refused". PollsService documents that the mirror verdict is handed back to the caller
    // and never stored on the session, so nothing the service holds afterwards can tell a
    // never-requested mirror from a requested-and-refused one — a pill claiming it would be
    // guessing. The verdict is still reported in full: DescribeOpen puts all five mirror
    // outcomes into StartFeedback, and MirrorStatusText names the mirror on the live card
    // only when it actually engaged.

    private static readonly Color DormantAccent = Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72); // CoalSecondaryText
    private static readonly Color OkAccent      = Color.FromArgb(0xFF, 0x6F, 0xA4, 0x6B); // Ok
    private static readonly Color EmberAccent   = Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E); // EmberPrimary
    private static readonly Color WarnAccent    = Color.FromArgb(0xFF, 0xE0, 0xA2, 0x3A); // Warn

    private PollsSvc.PollsPillState _pillState = PollsSvc.PollsPillState.Dormant;

    // The last label actually published. The open state's label carries the countdown, so
    // the text can move while the state does not — and, on every OTHER state, it can stand
    // still for hours while the 1 Hz tick keeps calling in.
    private string _pillText = "";

    public string StatusPillText => _pillState switch
    {
        PollsSvc.PollsPillState.Open =>
            string.Format(Localizer.T("panel.polls.pill.open", "open · {0}s left"),
                          _snapshot.SecondsLeft.ToString(CultureInfo.InvariantCulture)),
        PollsSvc.PollsPillState.Closed => Localizer.T("panel.polls.pill.closed", "closed · settled"),
        // The one fact the master switch cannot carry: betting is armed while the points
        // economy is off, so the next poll opened with stakes is refused before it takes a
        // single vote.
        PollsSvc.PollsPillState.IdleEconomyOff => Localizer.T("panel.polls.pill.economy_off", "idle · economy off"),
        PollsSvc.PollsPillState.Idle => Localizer.T("panel.polls.pill.idle", "idle"),
        _ => Localizer.T("panel.polls.pill.dormant", "dormant"),
    };

    public Color StatusPillColor => _pillState switch
    {
        PollsSvc.PollsPillState.Open => OkAccent,
        PollsSvc.PollsPillState.Closed => EmberAccent,
        PollsSvc.PollsPillState.IdleEconomyOff => WarnAccent,
        _ => DormantAccent,
    };

    /// <summary>Only an open poll has a liveness beat — votes are landing and the clock is
    /// running. Every other state is static, so the dot is hidden rather than parked.</summary>
    public bool StatusPulsing => _pillState == PollsSvc.PollsPillState.Open;

    // ── The header band's one state phrase ───────────────────────────────
    //
    // Same predicate, house wording. These two getters switch on the SAME private
    // _pillState the three properties above read — nothing is re-derived from config
    // fields, no service-side logic moved, and the state PollsService refuses to model
    // ("open · Twitch mirror refused", unknowable after the fact) is still not modelled
    // here. What changes is only the register: the band replaced three rows that said the
    // same boolean as OFF, DISABLED and dormant, so it says it once, lowercase, in three
    // words or fewer, with the CONSEQUENCE attached rather than the bare state.
    //
    // Moves in lockstep with StatusPillText — every state's phrase is constant except
    // Open's, whose countdown both share — so RefreshStatusPill raises the two together.

    /// <summary>The header band's state phrase.</summary>
    public string HeaderStateText => _pillState switch
    {
        PollsSvc.PollsPillState.Open =>
            string.Format(Localizer.T("panel.polls.state.open", "open · {0}s left"),
                          _snapshot.SecondsLeft.ToString(CultureInfo.InvariantCulture)),
        PollsSvc.PollsPillState.Closed => Localizer.T("panel.polls.state.closed", "closed · settled"),
        // Names the consequence, not the dependency: the streamer does not need to be
        // told the economy is off (Loyalty's own page says that), they need to know the
        // next poll opened with stakes is refused before it takes a vote.
        PollsSvc.PollsPillState.IdleEconomyOff => Localizer.T("panel.polls.state.bets_refused", "idle · bets refused"),
        PollsSvc.PollsPillState.Idle => Localizer.T("panel.polls.state.idle", "idle · ready"),
        _ => Localizer.T("panel.polls.state.off", "off"),
    };

    /// <summary>
    /// The foreground tier for <see cref="HeaderStateText"/>.
    ///
    /// <para>Only an OPEN poll is Live — a settled one is holding a result on the overlay,
    /// not taking votes, and painting that green would be the same over-claim the 1.1.7
    /// pill audit removed elsewhere.</para>
    ///
    /// <para>IdleEconomyOff is Error rather than Dormant. The tier reads "enabled but
    /// unable to work", which is exactly true of betting here, and the old pill already
    /// carried a warning accent for it — dropping to Dormant would erase the signal
    /// entirely. The phrase beside it names the scope ("bets refused"), so the tier never
    /// claims the whole tool is dead: a votes-only poll still opens.</para>
    /// </summary>
    public ToolStateKind HeaderStateKind => _pillState switch
    {
        PollsSvc.PollsPillState.Open => ToolStateKind.Live,
        PollsSvc.PollsPillState.IdleEconomyOff => ToolStateKind.Error,
        _ => ToolStateKind.Dormant,
    };

    private void RefreshStatusPill()
    {
        PollsSvc.PollsPillState next;
        try { next = _svc.PillState; }
        catch (Exception ex)
        {
            GlobalLogger.Error("PollsViewModel", "pill state read failed", ex);
            return;
        }

        if (next != _pillState)
        {
            _pillState = next;
            Raise(nameof(StatusPillColor));
            Raise(nameof(StatusPulsing));
            Raise(nameof(HeaderStateKind));
        }
        // The label is re-READ every call — the open state's countdown moves whether or not
        // the state does — but only re-RAISED when it actually changed, so an idle tab's 1 Hz
        // tick raises nothing at all. HeaderStateText rides the same gate: it varies with
        // exactly the same two inputs (the state, and the countdown inside Open).
        string text = StatusPillText;
        if (!string.Equals(text, _pillText, StringComparison.Ordinal))
        {
            _pillText = text;
            Raise(nameof(StatusPillText));
            Raise(nameof(HeaderStateText));
        }
    }

    // ── Persistence ─────────────────────────────────────────────────────
    // Both entry points re-raise ChatCommands: they are the choke point every settings edit
    // funnels through (the two verb boxes, the betting switch, both role rows), so the
    // command block repaints from the WORKING copy the moment the edit lands rather than
    // when the debounced save does.
    private void ScheduleSave()
    {
        _dirty = true;
        Raise(nameof(ChatCommands));
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        Raise(nameof(ChatCommands));
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        // SaveConfigAsync raises ConfigChanged synchronously (before this await returns),
        // so hold the self-save flag across the whole call — OnConfigChanged fires while it
        // is set and bails out.
        _selfSaving = true;
        try { await _svc.SaveConfigAsync(_working).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("PollsViewModel", "SaveConfigAsync failed", ex); }
        finally { _selfSaving = false; }
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Ignore the ConfigChanged our own SaveConfigAsync just raised — otherwise the
        // Normalize()/deep-clone the service applies makes it look foreign and we'd rebuild
        // the settings out from under the streamer's cursor.
        if (_selfSaving) return;
        _ui.Post(() =>
        {
            _working = Clone(_svc.Config);
            BuildSettings();
        });
    }

    private void OnPollChanged(object? sender, EventArgs e) => _ui.Post(RefreshPoll);

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            // A cleared box is 0; anything unparseable keeps the previous value rather than
            // silently zeroing a limit mid-keystroke. The service clamps on save, so a 0
            // that is not meaningful for a field lands back on its floor.
            : ((raw ?? "").Trim().Length == 0 ? 0 : fallback);

    private static long ParseLong(string? raw, long fallback)
        => long.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
            ? v
            : ((raw ?? "").Trim().Length == 0 ? 0 : fallback);

    private static PollsConfig Clone(PollsConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new PollsConfig());
            return JsonSerializer.Deserialize<PollsConfig>(json) ?? new PollsConfig();
        }
        catch { return new PollsConfig(); }
    }
}
