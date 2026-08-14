using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.UI;
using AutomodSvc = Phoenix.Controls.Hub.Core.AutomodService;
using Catalog = Phoenix.Controls.Hub.Core.BuiltInCommandCatalog;
using CommandInfo = Phoenix.Controls.Hub.Core.ToolCommandInfo;
using SbLink = Phoenix.Controls.Hub.Core.WS;

namespace Phoenix.Controls.Hub.WinUI.Panels.AutomodPanel;

/// <summary>
/// ViewModel for the Hub Automod page. Like the Counters / Loyalty VMs it reaches
/// <see cref="AutomodSvc.Instance"/> DIRECTLY for reads AND writes and subscribes to
/// its ConfigChanged / Activity events (Hub.WinUI already references the Hub runtime
/// and the service is always-on). The whole <see cref="AutomodConfig"/> is deep-cloned
/// into a private working copy on construction; every field edits that clone in place
/// and schedules a debounced SaveConfigAsync (which replaces + persists the config and
/// recompiles the blocklist regex cache). A self-triggered ConfigChanged is skipped by
/// reference-equality; a foreign one rebuilds the working copy.
/// </summary>
public sealed class AutomodViewModel : ObservableObject, IDisposable
{
    private readonly AutomodSvc _svc = AutomodSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;
    private readonly DispatcherQueueTimer? _activityTimer;

    private AutomodConfig _working;
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;
    // Set on the UI thread immediately before our own SaveConfigAsync and consumed on
    // the UI thread when the resulting ConfigChanged echoes back. AutomodService clones
    // on save, so _svc.Config is never our _working instance — reference-equality alone
    // can never detect a self-save, which left every ~400 ms own-save rebuilding the
    // whole surface (BuildRules/BuildLadder Clear+recreate) mid-edit and jumping the caret.
    private bool _selfSaving;

    public AutomodViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        _working = Clone(_svc.Config);

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();

            // Coalesce a violation burst: the service raises Activity per moderated
            // message, so a spam flood would otherwise fire one full activity+strikes DB
            // refresh (GetLog + ListStrikes + Clear/re-add) each. Debounce to one.
            _activityTimer = dispatcher.CreateTimer();
            _activityTimer.Interval = TimeSpan.FromMilliseconds(400);
            _activityTimer.IsRepeating = false;
            _activityTimer.Tick += (_, _) => _ = RefreshActivityAsync();
        }

        ExemptRoles = new AutomodRolesVm(_working.ExemptRoles ??= AutomodRoles.Mods(), ScheduleSave);
        PermitRoles = new AutomodRolesVm(_working.PermitRoles ??= AutomodRoles.Mods(), ScheduleSave);

        BuildRules();
        BuildLadder();
        RefreshRegexIssue();
        RefreshChatCommands();

        _svc.ConfigChanged += OnConfigChanged;
        _svc.Activity += OnActivity;

        // The status pill's "degraded — nothing enforced" state is the ONLY place the
        // page says a connected-Streamer.bot outage silently swallows every dispatch
        // (ScriptManager.Automod's DispatchModeration logs loudly and then the action
        // fails). Reading IsConnected once at construction would freeze that state, so
        // the pill follows the socket. Fires on the socket's thread — marshal.
        SbLink.Instance.OnConnectionStatusChanged += OnSbConnectionChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.Activity -= OnActivity;
        SbLink.Instance.OnConnectionStatusChanged -= OnSbConnectionChanged;
        _saveTimer?.Stop();
        _activityTimer?.Stop();
        if (_dirty)
        {
            _dirty = false;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker
            // lets PreBuildsHostView.DisposeAllTools hand it to the coordinator as a
            // tracked step; mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.SaveConfigAsync(_working), "AutomodViewModel", "final config flush"));
        }
    }

    // ── Bound collections ───────────────────────────────────────────────
    public ObservableCollection<AutomodRuleRowVm> Rules { get; } = new();
    public ObservableCollection<AutomodLadderStepVm> Ladder { get; } = new();
    public ObservableCollection<AutomodListRowVm> Activity { get; } = new();

    public AutomodRolesVm ExemptRoles { get; private set; }
    public AutomodRolesVm PermitRoles { get; private set; }

    // Empty-state gates for the two lists that can genuinely be empty (the
    // ladder after removing every rung; activity before any moderation).
    public Visibility LadderEmptyVisibility => Ladder.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivityEmptyVisibility => Activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── Chat-command reference block ────────────────────────────────────
    //
    // Built from the WORKING config, not from _svc.Config: the streamer renaming the
    // permit word must see the new word immediately, and reading the live config would
    // show the old one until the 400 ms debounce elapsed — on the one surface whose
    // entire job is "what does this tool answer to".
    //
    // ★ A FRESH LIST EVERY TIME OR NOTHING UPDATES. ToolCommandList.Commands is a
    // dependency property, so it raises its change callback on reference inequality;
    // mutating a list in place and re-assigning it would render nothing. Catalog.For*
    // allocates, which is what makes the natural "rebuild and assign" work.
    private IReadOnlyList<CommandInfo> _chatCommands = Array.Empty<CommandInfo>();

    /// <summary>
    /// The verbs Automod answers to, as <c>BuiltInCommandCatalog</c> derives them from
    /// the working config — today the link permit alone. Re-raised whenever an edit is
    /// scheduled, i.e. as soon as the command-word box commits, not when the debounced
    /// save eventually lands.
    /// </summary>
    public IReadOnlyList<CommandInfo> ChatCommands => _chatCommands;

    // Rebuilds the block and raises ONLY when the rendered rows actually changed.
    //
    // The gate is not premature caution: this runs from ScheduleSave, i.e. on every
    // committed edit anywhere on the page — decay hours, a block-list line, a rule
    // threshold, a ladder rung — and almost none of those touch a verb, a role tick or
    // an enable. Raising unconditionally would rebuild the repeater's rows on each of
    // them for an identical result. ToolCommandInfo is a record struct, so Equals
    // compares all five rendered columns and the comparison is exactly as fine-grained
    // as what the user can see.
    private void RefreshChatCommands()
    {
        var next = Catalog.ForAutomod(_working);
        if (SameRows(_chatCommands, next)) return;
        _chatCommands = next;
        Raise(nameof(ChatCommands));
    }

    private static bool SameRows(IReadOnlyList<CommandInfo> a, IReadOnlyList<CommandInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    // ── General section ─────────────────────────────────────────────────
    public bool MasterEnabled
    {
        get => _working.Enabled;
        set
        {
            if (_working.Enabled == value) return;
            _working.Enabled = value;
            Raise(nameof(MasterEnabled));
            Raise(nameof(MasterToggleLabel));
            Raise(nameof(MasterStateText));
            RaiseStatusPill();
            SaveNow();
        }
    }

    /// <summary>Chip caption beside the master TogglePill in the action strip.</summary>
    public string MasterToggleLabel => _working.Enabled
        ? Localizer.T("panel.automod.master.on", "ON")
        : Localizer.T("panel.automod.master.off", "OFF");

    // "Rules tab" was the Pivot's wording. The Pivot is gone; the sections are now
    // chips on the detail column, so the sentence names the section, not a tab.
    public string MasterStateText => _working.Enabled
        ? Localizer.T("panel.automod.master.state.enabled", "ENABLED — chat is moderated per the Rules section.")
        : Localizer.T("panel.automod.master.state.disabled", "DISABLED — nothing is scanned or moderated.");

    // ── Section selector (the Pivot replacement) ────────────────────────
    // Four chips, in the Pivot's own order, driving four Visibility gates over
    // section bodies that all stay in the tree. Binding Visibility rather than
    // swapping content is what keeps a focused TextBox's LostFocus commit from
    // being lost when the streamer switches section mid-edit — every box on this
    // page commits on LostFocus.
    //
    // ACTIVITY is deliberately NOT a section here: the Pivot's fifth tab (Pardon +
    // the moderation log) moved to the right-hand live column, where it is visible
    // from every section and stays ungated while the tool is off.
    public IList<string> SectionNames { get; } = new List<string>
    {
        Localizer.T("panel.automod.section.general", "General"),
        Localizer.T("panel.automod.section.rules", "Rules"),
        Localizer.T("panel.automod.section.lists", "Lists"),
        Localizer.T("panel.automod.section.ladder", "Ladder"),
    };

    private int _selectedSection;

    /// <summary>Selected detail section, 0-3, indexing <see cref="SectionNames"/>.</summary>
    public int SelectedSection
    {
        get => _selectedSection;
        set
        {
            // An out-of-range index would blank the whole detail column, so it is
            // ignored rather than clamped — the selector emits only valid indices,
            // and a TwoWay binding mid-initialization can transiently offer -1.
            if (value < 0 || value >= SectionNames.Count) return;
            if (!Set(ref _selectedSection, value)) return;
            Raise(nameof(GeneralVisibility));
            Raise(nameof(RulesVisibility));
            Raise(nameof(ListsVisibility));
            Raise(nameof(LadderVisibility));
        }
    }

    public Visibility GeneralVisibility => _selectedSection == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RulesVisibility => _selectedSection == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListsVisibility => _selectedSection == 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LadderVisibility => _selectedSection == 3 ? Visibility.Visible : Visibility.Collapsed;

    // ── Status pill ─────────────────────────────────────────────────────
    // Four states, and three of them are things the master switch cannot say:
    // dry-run is enabled-but-inert, a downed Streamer.bot is enabled-but-unenforced,
    // and only the fourth is actually filtering. The colours are the PhoenixDark
    // token hues (CoalSecondaryText / EmberPrimary / Warn / Ok) as literal Colors —
    // ToolStatusPill takes a Color, not a Brush, and does its own alpha ramps.
    //
    // Computed from the WORKING config, not from _svc.Config: the working copy is
    // what the surface shows, and the 400 ms debounce would otherwise leave the pill
    // contradicting the dry-run toggle the streamer just flipped.
    private static readonly Color DormantAccent = Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72);
    private static readonly Color ObservingAccent = Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E);
    private static readonly Color DegradedAccent = Color.FromArgb(0xFF, 0xE0, 0xA2, 0x3A);
    private static readonly Color FilteringAccent = Color.FromArgb(0xFF, 0x6F, 0xA4, 0x6B);

    // Read live rather than latched. The socket can go down between two reads and the
    // pill must not claim "filtering" through an outage; the catch is for a host that
    // never initialised the Streamer.bot link at all, where "not connected" is right.
    private bool SbConnected
    {
        get { try { return SbLink.Instance.IsConnected; } catch { return false; } }
    }

    // Order matters: dry-run outranks the connection state, because dry-run dispatches
    // nothing at all — a downed Streamer.bot changes nothing about what the tool does.
    public string StatusPillText
    {
        get
        {
            if (!_working.Enabled) return Localizer.T("panel.automod.pill.dormant", "dormant");
            if (_working.DryRun) return Localizer.T("panel.automod.pill.observing", "observing · taking no action");
            if (!SbConnected) return Localizer.T("panel.automod.pill.degraded", "degraded · nothing enforced");
            return Localizer.T("panel.automod.pill.filtering", "filtering");
        }
    }

    public Color StatusPillColor
    {
        get
        {
            if (!_working.Enabled) return DormantAccent;
            if (_working.DryRun) return ObservingAccent;
            if (!SbConnected) return DegradedAccent;
            return FilteringAccent;
        }
    }

    /// <summary>Only the actually-filtering state carries a liveness beat.</summary>
    public bool StatusPulsing => _working.Enabled && !_working.DryRun && SbConnected;

    private void RaiseStatusPill()
    {
        Raise(nameof(StatusPillText));
        Raise(nameof(StatusPillColor));
        Raise(nameof(StatusPulsing));
    }

    private void OnSbConnectionChanged(bool connected)
        => _ui.Post(() => { if (!_disposed) RaiseStatusPill(); });

    public bool ScopeTwitch
    {
        get => _working.ScopeTwitch;
        set { if (_working.ScopeTwitch == value) return; _working.ScopeTwitch = value; Raise(nameof(ScopeTwitch)); ScheduleSave(); }
    }
    public bool ScopeYouTube
    {
        get => _working.ScopeYouTube;
        set { if (_working.ScopeYouTube == value) return; _working.ScopeYouTube = value; Raise(nameof(ScopeYouTube)); ScheduleSave(); }
    }
    public bool ScopeKick
    {
        get => _working.ScopeKick;
        set { if (_working.ScopeKick == value) return; _working.ScopeKick = value; Raise(nameof(ScopeKick)); ScheduleSave(); }
    }

    public bool DryRun
    {
        get => _working.DryRun;
        // Dry-run is one of the four status-pill states, so the pill re-reads here —
        // it must not wait out the 400 ms debounce to stop claiming "filtering".
        set { if (_working.DryRun == value) return; _working.DryRun = value; Raise(nameof(DryRun)); RaiseStatusPill(); ScheduleSave(); }
    }

    public bool SuppressDispatchWhenModerated
    {
        get => _working.SuppressDispatchWhenModerated;
        set { if (_working.SuppressDispatchWhenModerated == value) return; _working.SuppressDispatchWhenModerated = value; Raise(nameof(SuppressDispatchWhenModerated)); ScheduleSave(); }
    }

    /// <summary>
    /// The word the link permit answers to (<c>AutomodConfig.PermitCommand</c>).
    /// </summary>
    /// <remarks>
    /// <para>Saved VERBATIM, bang and all. <c>AutomodService.PermitVerb</c> canonicalizes
    /// the field once at the source and the parser compares through
    /// <c>ChatVerb</c>, so "permit" and "!permit" behave identically at runtime —
    /// rewriting the streamer's text in the box would be unrequested validation that
    /// changes nothing about what matches.</para>
    ///
    /// <para>An empty field is a legal, meaningful state and is stored as typed: it
    /// switches the command off (an empty configured side never matches). The field now
    /// lives in the Links rule's expansion (Rules section) with that stated on the line
    /// under it — the page-wide CHAT COMMANDS block that used to say it was one row tall
    /// and cost ~96 px above the first editable control.</para>
    /// </remarks>
    public string PermitCommand
    {
        get => _working.PermitCommand ?? "";
        set
        {
            string v = value ?? "";
            if ((_working.PermitCommand ?? "") == v) return;
            _working.PermitCommand = v;
            Raise(nameof(PermitCommand));
            ScheduleSave();
        }
    }

    public string PermitSecondsText
    {
        get => _working.PermitSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            int v = Math.Max(1, ParseInt(value, _working.PermitSeconds));
            if (_working.PermitSeconds == v) { Raise(nameof(PermitSecondsText)); return; }
            _working.PermitSeconds = v;
            Raise(nameof(PermitSecondsText));
            ScheduleSave();
        }
    }

    // ── Ladder section ──────────────────────────────────────────────────
    public string StrikeDecayHoursText
    {
        get => _working.StrikeDecayHours.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            double v = ParseDouble(value, _working.StrikeDecayHours);
            if (v < 0) v = 0;
            if (Math.Abs(_working.StrikeDecayHours - v) < 0.0001) { Raise(nameof(StrikeDecayHoursText)); return; }
            _working.StrikeDecayHours = v;
            Raise(nameof(StrikeDecayHoursText));
            ScheduleSave();
        }
    }

    // ── Lists section (verbatim, one entry per line) ────────────────────
    public string UrlAllowText
    {
        get => Join(_working.UrlAllowList);
        set { _working.UrlAllowList = Split(value); Raise(nameof(UrlAllowText)); ScheduleSave(); }
    }
    public string UrlBlockText
    {
        get => Join(_working.UrlBlockList);
        set { _working.UrlBlockList = Split(value); Raise(nameof(UrlBlockText)); ScheduleSave(); }
    }
    public string WordsText
    {
        get => Join(_working.BlocklistWords);
        set { _working.BlocklistWords = Split(value); Raise(nameof(WordsText)); ScheduleSave(); }
    }
    public string RegexText
    {
        get => Join(_working.BlocklistRegex);
        set { _working.BlocklistRegex = Split(value); Raise(nameof(RegexText)); ScheduleSave(); }
    }

    // ── Passive cues (never block a save, never reject input) ───────────
    // Both of these only DESCRIBE what the config already is. A de-escalating ladder
    // and an uncompilable regex are both saved verbatim — the panel says so inline
    // instead of rejecting the edit or opening a dialog.

    private string _ladderOrderHint = "";
    /// <summary>Muted cue when the ladder de-escalates (a softer rung after a harder
    /// one). Empty when the order climbs — the cue is informational only.</summary>
    public string LadderOrderHint
    {
        get => _ladderOrderHint;
        private set
        {
            if (_ladderOrderHint == value) return;
            _ladderOrderHint = value;
            Raise(nameof(LadderOrderHint));
            Raise(nameof(LadderOrderHintVisibility));
        }
    }
    public Visibility LadderOrderHintVisibility => _ladderOrderHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Severity follows the AutomodAction ordinal (Warn < Delete < Timeout < Ban); two
    // Timeout rungs compare on duration, so 1h-then-10m also reads as de-escalating.
    // Reports the FIRST offending pair only — the point is to flag the shape, not to
    // enumerate it.
    private void RefreshLadderOrderHint()
    {
        var ladder = _working.Ladder;
        if (ladder is null || ladder.Count < 2) { LadderOrderHint = ""; return; }
        for (int i = 1; i < ladder.Count; i++)
        {
            var prev = ladder[i - 1];
            var cur = ladder[i];
            bool softer = (int)cur.Action < (int)prev.Action
                || (cur.Action == AutomodAction.Timeout && prev.Action == AutomodAction.Timeout
                    && cur.DurationSeconds < prev.DurationSeconds);
            if (!softer) continue;
            LadderOrderHint = string.Format(
                Localizer.T("panel.automod.ladder.order_hint",
                    "Rung {0} is softer than rung {1}. Saved as ordered."),
                i + 1, i);
            return;
        }
        LadderOrderHint = "";
    }

    private string _regexIssueText = "";
    /// <summary>Muted cue naming the blocklist patterns the service could not compile.
    /// Empty when every pattern is valid.</summary>
    public string RegexIssueText
    {
        get => _regexIssueText;
        private set
        {
            if (_regexIssueText == value) return;
            _regexIssueText = value;
            Raise(nameof(RegexIssueText));
            Raise(nameof(RegexIssueVisibility));
        }
    }
    public Visibility RegexIssueVisibility => _regexIssueText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Reports what the SERVICE already found when it recompiled the blocklist on save
    // (AutomodService.InvalidRegexPatterns) — the VM never compiles a pattern itself.
    private void RefreshRegexIssue()
    {
        var bad = _svc.InvalidRegexPatterns;
        if (bad is null || bad.Count == 0) { RegexIssueText = ""; return; }
        string list = string.Join(", ", bad.Select(p => $"/{p}/"));
        RegexIssueText = bad.Count == 1
            ? string.Format(
                Localizer.T("panel.automod.lists.regex.issue_one",
                    "{0} does not compile — it is still saved, but it never matches."),
                list)
            : string.Format(
                Localizer.T("panel.automod.lists.regex.issue_many",
                    "{0} patterns do not compile — they are still saved, but they never match: {1}."),
                bad.Count, list);
    }

    // ── Stat tiles (pinned above the detail column, ungated) ────────────
    private string _statActionsToday = "0";
    private string _statActiveStrikes = "0";
    private string _statTopOffender = "—";
    public string StatActionsToday { get => _statActionsToday; private set { _statActionsToday = value; Raise(nameof(StatActionsToday)); } }
    public string StatActiveStrikes { get => _statActiveStrikes; private set { _statActiveStrikes = value; Raise(nameof(StatActiveStrikes)); } }
    public string StatTopOffender { get => _statTopOffender; private set { _statTopOffender = value; Raise(nameof(StatTopOffender)); } }

    // ── Moderation column — Pardon ──────────────────────────────────────
    private string _pardonName = "";
    public string PardonName { get => _pardonName; set { _pardonName = value ?? ""; Raise(nameof(PardonName)); } }

    // ── Load ────────────────────────────────────────────────────────────
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshActivityAsync().ConfigureAwait(false);
    }

    private void BuildRules()
    {
        Rules.Clear();
        var c = _working;
        // A description is carried ONLY where the two threshold labels do not already
        // say how the rule fires. AutomodRules.CapsFired / SymbolsFired are conjunctive
        // (the min-length gate must pass AND the percentage must be reached), and that
        // AND is the one thing the labels cannot show. Caps counts LETTERS, symbols
        // counts non-space characters; the wording follows the detectors, not the label.
        Rules.Add(new AutomodRuleRowVm(
            Localizer.T("panel.automod.rule.caps.title", "Excessive caps"),
            Localizer.T("panel.automod.rule.caps.desc", "Fires once the message has Min length letters and reaches Caps %."), c.Caps, ScheduleSave,
            Localizer.T("panel.automod.rule.caps.t1", "Min length"), () => c.Caps.MinLength, v => c.Caps.MinLength = v,
            Localizer.T("panel.automod.rule.caps.t2", "Caps %"), () => c.Caps.Percent, v => c.Caps.Percent = v,
            glyph: "\uE8E9"));   // FontSize

        Rules.Add(new AutomodRuleRowVm(
            Localizer.T("panel.automod.rule.length.title", "Message too long"),
            "", c.Length, ScheduleSave,
            Localizer.T("panel.automod.rule.length.t1", "Max chars"), () => c.Length.MaxLength, v => c.Length.MaxLength = v,
            glyph: "\uE8BD"));   // Message

        Rules.Add(new AutomodRuleRowVm(
            Localizer.T("panel.automod.rule.repeat.title", "Repeated characters"),
            Localizer.T("panel.automod.rule.repeat.desc", "Flags a run of the same character (aaaaaaaa)."), c.Repeat, ScheduleSave,
            Localizer.T("panel.automod.rule.repeat.t1", "Max run"), () => c.Repeat.MaxRun, v => c.Repeat.MaxRun = v,
            glyph: "\uE8EE"));   // RepeatAll

        Rules.Add(new AutomodRuleRowVm(
            Localizer.T("panel.automod.rule.symbols.title", "Symbol / emote spam"),
            Localizer.T("panel.automod.rule.symbols.desc", "Fires once the message has Min length non-space characters and reaches Symbol %."), c.Symbols, ScheduleSave,
            Localizer.T("panel.automod.rule.symbols.t1", "Min length"), () => c.Symbols.MinLength, v => c.Symbols.MinLength = v,
            Localizer.T("panel.automod.rule.symbols.t2", "Symbol %"), () => c.Symbols.Percent, v => c.Symbols.Percent = v,
            glyph: "\uE76E"));   // Emoji2

        // \u2605 2026-08-14 \u2014 the link permit MOVED INTO this row's expansion.
        //
        // It used to be its own LINK PERMIT card in the General section, two sections
        // away from the single rule it bypasses, with a one-row CHAT COMMANDS block
        // pinned above the whole page as the only place the verb was named. Three
        // surfaces, one setting. It is now the first field of the Links expansion,
        // which is the rule it grants a pass to.
        //
        // The accessors point at THIS VM's own properties, so the verbatim-save
        // contract, the 1-second clamp and the debounce all still live in one place \u2014
        // the row only renders them. PermitRoles is passed by value: a foreign
        // ConfigChanged replaces that VM and *then* calls BuildRules, so the row
        // always receives the fresh instance.
        Rules.Add(new AutomodRuleRowVm(
            Localizer.T("panel.automod.rule.links.title", "Links"),
            "", c.Links, ScheduleSave,
            toggleLabel: Localizer.T("panel.automod.rule.links.toggle", "Block all links (allow-list bypasses)"),
            getToggle: () => c.Links.BlockAll, setToggle: v => c.Links.BlockAll = v,
            listsHint: Localizer.T("panel.automod.rule.links.lists_hint", "Allow / block domains live in the Lists section."),
            glyph: "\uE71B",   // Link
            permitRoles: PermitRoles,
            getPermitCommand: () => PermitCommand, setPermitCommand: v => PermitCommand = v,
            getPermitSeconds: () => PermitSecondsText, setPermitSeconds: v => PermitSecondsText = v));

        Rules.Add(new AutomodRuleRowVm(
            Localizer.T("panel.automod.rule.words.title", "Blocklist words"),
            "", c.Words, ScheduleSave,
            listsHint: Localizer.T("panel.automod.rule.words.lists_hint", "Blocked words / phrases live in the Lists section (one per line)."),
            glyph: "\uE71C"));   // Filter

        Rules.Add(new AutomodRuleRowVm(
            Localizer.T("panel.automod.rule.regex.title", "Blocklist regex"),
            Localizer.T("panel.automod.rule.regex.desc", "Flags a message matching a blocked pattern (100 ms match timeout)."), c.Regex, ScheduleSave,
            listsHint: Localizer.T("panel.automod.rule.regex.lists_hint", "Blocked regex patterns live in the Lists section (one per line)."),
            glyph: "\uE943"));   // Code

        Rules.Add(new AutomodRuleRowVm(
            Localizer.T("panel.automod.rule.rate.title", "Rate / flood"),
            Localizer.T("panel.automod.rule.rate.desc", "Flags a user sending too many messages in a window."), c.Rate, ScheduleSave,
            Localizer.T("panel.automod.rule.rate.t1", "Max messages"), () => c.Rate.MaxMessages, v => c.Rate.MaxMessages = v,
            Localizer.T("panel.automod.rule.rate.t2", "Window (s)"), () => c.Rate.WindowSeconds, v => c.Rate.WindowSeconds = v,
            glyph: "\uE823"));   // Recent
    }

    private void BuildLadder()
    {
        Ladder.Clear();
        _working.Ladder ??= AutomodConfig.DefaultLadder();
        for (int i = 0; i < _working.Ladder.Count; i++)
            Ladder.Add(new AutomodLadderStepVm(i + 1, _working.Ladder[i], ScheduleSave));
        Raise(nameof(LadderEmptyVisibility));
        RefreshLadderOrderHint();
    }

    public void AddLadderStep()
    {
        _working.Ladder ??= new List<AutomodLadderStep>();
        _working.Ladder.Add(new AutomodLadderStep(AutomodAction.Timeout, 600));
        BuildLadder();
        ScheduleSave();
    }

    public void RemoveLadderStep(AutomodLadderStepVm row)
    {
        if (row is null || _working.Ladder is null) return;
        _working.Ladder.Remove(row.Step);
        BuildLadder();
        ScheduleSave();
    }

    // ── Activity + strikes refresh ──────────────────────────────────────
    private async Task RefreshActivityAsync()
    {
        List<AutomodLogEntry> log;
        List<(string Name, long Count, long LastTs)> strikes;
        try
        {
            log = await _svc.GetLogAsync(200).ConfigureAwait(false);
            strikes = await _svc.ListStrikesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("AutomodViewModel", "RefreshActivityAsync failed", ex);
            return;
        }

        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int actionsToday = log.Count(e => (e.Time ?? "").StartsWith(today, StringComparison.Ordinal));
        int active = strikes.Count(s => s.Count > 0);
        string top = strikes.Count > 0 && strikes[0].Count > 0
            ? $"{strikes[0].Name} ({strikes[0].Count})" : "—";

        _ui.Post(() =>
        {
            Activity.Clear();
            foreach (var e in log) Activity.Add(new AutomodListRowVm(e));
            StatActionsToday = actionsToday.ToString(CultureInfo.InvariantCulture);
            StatActiveStrikes = active.ToString(CultureInfo.InvariantCulture);
            StatTopOffender = top;
            Raise(nameof(ActivityEmptyVisibility));
        });
    }

    public async Task PardonAsync()
    {
        string name = (_pardonName ?? "").Trim();
        if (name.Length == 0) return;
        try { await _svc.PardonAsync(name).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("AutomodViewModel", "PardonAsync failed", ex); }
        _ui.Post(() => PardonName = "");
        await RefreshActivityAsync().ConfigureAwait(false);
    }

    public Task RefreshAsync() => RefreshActivityAsync();

    // ── Persistence ─────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        // Every rung edit funnels through here (the rung VMs' change delegate), so this
        // is the one place the order cue can react to an in-place Action/duration change
        // without a rebuild. Cheap — the ladder is a handful of rungs.
        RefreshLadderOrderHint();
        // Same funnel argument for the command block: the permit word and the permit
        // role ticks both write through here (the role VM's change delegate), so this is
        // the one place that catches every edit that can change a rendered row. The
        // rebuild is gated on the rows actually differing — see RefreshChatCommands.
        RefreshChatCommands();
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        // Nothing the master switch touches is rendered in the command block today (the
        // catalogue never folds a master toggle into a per-command Enabled), so this call
        // is normally a no-op that the equality gate swallows. It is here so the two save
        // entry points stay interchangeable: a future per-command gate wired to SaveNow
        // would otherwise leave the block silently stale.
        RefreshChatCommands();
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        _selfSaving = true;   // suppress the ConfigChanged this save will echo back (see OnConfigChanged)
        try { await _svc.SaveConfigAsync(_working).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _selfSaving = false;   // no echo will arrive if the save threw before RaiseConfigChanged
            GlobalLogger.Error("AutomodViewModel", "SaveConfigAsync failed", ex);
        }
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _ui.Post(() =>
        {
            if (_disposed) return;
            // Consume our own save's echo without a rebuild — the flag is both set (in
            // SaveWorkingAsync) and cleared here on the UI thread, so it's race-free even
            // though the service clones on save (ReferenceEquals can't catch a self-save).
            // Covers both the debounced save and the SaveNow (MasterEnabled) path, since
            // both funnel through SaveWorkingAsync.
            // Our own save still has to refresh the regex cue — the service recompiled
            // the blocklist as part of it, and that compile result is the only thing the
            // Lists section can report. Everything else on the surface is already current.
            if (_selfSaving) { _selfSaving = false; RefreshRegexIssue(); return; }
            if (ReferenceEquals(_svc.Config, _working)) return;
            _working = Clone(_svc.Config);
            ExemptRoles = new AutomodRolesVm(_working.ExemptRoles ??= AutomodRoles.Mods(), ScheduleSave);
            PermitRoles = new AutomodRolesVm(_working.PermitRoles ??= AutomodRoles.Mods(), ScheduleSave);
            Raise(nameof(ExemptRoles));
            Raise(nameof(PermitRoles));
            BuildRules();
            BuildLadder();
            Raise(nameof(MasterEnabled));
            Raise(nameof(MasterToggleLabel));
            Raise(nameof(MasterStateText));
            Raise(nameof(ScopeTwitch)); Raise(nameof(ScopeYouTube)); Raise(nameof(ScopeKick));
            Raise(nameof(DryRun)); Raise(nameof(SuppressDispatchWhenModerated));
            Raise(nameof(PermitCommand));
            Raise(nameof(PermitSecondsText)); Raise(nameof(StrikeDecayHoursText));
            Raise(nameof(UrlAllowText)); Raise(nameof(UrlBlockText));
            Raise(nameof(WordsText)); Raise(nameof(RegexText));
            // Enabled and DryRun both moved, so the strip's pill has to move with them.
            // Every property bound by the new shell must join this list — that is what
            // makes a foreign save (Architect node, another window) reach the surface.
            RaiseStatusPill();
            RefreshRegexIssue();
            // A foreign save can rename the permit word or retick its roles (another Hub
            // window, a future script node), so the block is rebuilt from the fresh clone
            // rather than left showing the config this VM was holding.
            RefreshChatCommands();
        });
    }

    private void OnActivity(object? sender, EventArgs e)
    {
        // Debounce through the UI-thread timer so a burst collapses to one refresh
        // (restart-on-tick, like the save timer). Falls back to a direct refresh when
        // no dispatcher was injected (tests).
        if (_activityTimer is not null)
            _ui.Post(() => { if (!_disposed) { _activityTimer.Stop(); _activityTimer.Start(); } });
        else
            _ = RefreshActivityAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseDouble(string? s, double fallback)
        => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Join(List<string>? list)
        => list == null ? "" : string.Join(Environment.NewLine, list);

    // Verbatim (rule 7) — split on newlines, trim only whitespace-empty lines.
    private static List<string> Split(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    private static AutomodConfig Clone(AutomodConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new AutomodConfig());
            return JsonSerializer.Deserialize<AutomodConfig>(json) ?? new AutomodConfig();
        }
        catch { return new AutomodConfig(); }
    }
}
