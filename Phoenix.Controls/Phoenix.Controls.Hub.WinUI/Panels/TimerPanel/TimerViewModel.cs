using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using LayerRt = Phoenix.Controls.Hub.Core.LayerRuntime;
using TimerSvc = Phoenix.Controls.Hub.Core.TimerService;

namespace Phoenix.Controls.Hub.WinUI.Panels.TimerPanel;

/// <summary>
/// ViewModel for the Hub Timer page — the button-side front-end onto the same
/// subathon countdown the timer.* script nodes drive ("one implementation, two
/// front-ends"). Unlike the Giveaway page (which goes through an IHubServices
/// bridge), the Timer VM reaches <see cref="TimerSvc.Instance"/> DIRECTLY for
/// reads AND writes and subscribes to its <c>TimersChanged</c> /
/// <c>TimerTicked</c> events — Hub.WinUI already references the Hub runtime and
/// the service is always-on, so no cross-lane seam is needed.
///
/// Subscribes in the ctor and unsubscribes in <see cref="Dispose"/>, mirroring
/// the other panel VMs. All service events are marshalled to the UI thread
/// through the injected dispatcher pump before touching the collections.
/// </summary>
public sealed class TimerViewModel : ObservableObject, IDisposable
{
    private readonly TimerSvc _svc = TimerSvc.Instance;
    private readonly UiDispatcherPump _ui;

    private bool _disposed;
    private bool _loaded;

    // Selected timer, snapshot clone from the service list.
    private StreamTimer? _selected;
    private List<StreamTimer> _all = new();
    private string _pickerFilter = string.Empty;

    // Live readings, authoritative from the in-memory service — kept apart
    // from the snapshot so the big countdown updates every tick without a
    // full list refresh.
    private long _liveRemainingMs;
    private double _liveProgress;
    private TimerRunState _liveState = TimerRunState.Stopped;

    // Settings-card fields (draft/commit seam). Ordered for bulk seeding.
    private readonly TimerDraftField[] _allFields;

    public TimerViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);

        // ── Per-event "seconds added" fields (SetActionConfigAsync writes the
        //    whole config; each field mutates one member of a fresh clone). ──
        SubT1Field = new TimerDraftField(
            t => t.Actions.SubT1Seconds.ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, t.Actions.SubT1Seconds,
                v => _svc.SetActionConfigAsync(t.Slug, CloneActionsWith(t.Actions, c => c.SubT1Seconds = v))));
        SubT2Field = new TimerDraftField(
            t => t.Actions.SubT2Seconds.ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, t.Actions.SubT2Seconds,
                v => _svc.SetActionConfigAsync(t.Slug, CloneActionsWith(t.Actions, c => c.SubT2Seconds = v))));
        SubT3Field = new TimerDraftField(
            t => t.Actions.SubT3Seconds.ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, t.Actions.SubT3Seconds,
                v => _svc.SetActionConfigAsync(t.Slug, CloneActionsWith(t.Actions, c => c.SubT3Seconds = v))));
        SubPrimeField = new TimerDraftField(
            t => t.Actions.SubPrimeSeconds.ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, t.Actions.SubPrimeSeconds,
                v => _svc.SetActionConfigAsync(t.Slug, CloneActionsWith(t.Actions, c => c.SubPrimeSeconds = v))));
        BitsPer100Field = new TimerDraftField(
            t => t.Actions.BitsPer100Seconds.ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, t.Actions.BitsPer100Seconds,
                v => _svc.SetActionConfigAsync(t.Slug, CloneActionsWith(t.Actions, c => c.BitsPer100Seconds = v))));
        TipPerUnitField = new TimerDraftField(
            t => t.Actions.TipPerUnitSeconds.ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, t.Actions.TipPerUnitSeconds,
                v => _svc.SetActionConfigAsync(t.Slug, CloneActionsWith(t.Actions, c => c.TipPerUnitSeconds = v))));
        FollowField = new TimerDraftField(
            t => t.Actions.FollowSeconds.ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, t.Actions.FollowSeconds,
                v => _svc.SetActionConfigAsync(t.Slug, CloneActionsWith(t.Actions, c => c.FollowSeconds = v))));
        RaidPerViewerField = new TimerDraftField(
            t => t.Actions.RaidPerViewerSeconds.ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, t.Actions.RaidPerViewerSeconds,
                v => _svc.SetActionConfigAsync(t.Slug, CloneActionsWith(t.Actions, c => c.RaidPerViewerSeconds = v))));

        // ── Duration fields (user types "4h" / "72h" / "30m"; stored as ms). ──
        StartDurationField = new TimerDraftField(
            t => FormatDurationCompact(t.StartDurationMs),
            (d, t) => PlanDuration(d, t.StartDurationMs, ms => _svc.SetStartDurationAsync(t.Slug, ms)));
        MaxCapField = new TimerDraftField(
            t => FormatDurationCompact(t.MaxCapMs),
            (d, t) => PlanDuration(d, t.MaxCapMs, ms => _svc.SetMaxCapAsync(t.Slug, ms)));
        PerAddCapField = new TimerDraftField(
            t => FormatDurationCompact(t.PerAddCapMs),
            (d, t) => PlanDuration(d, t.PerAddCapMs, ms => _svc.SetPerAddCapAsync(t.Slug, ms)));

        // ── Feedback cards (chat + visual per timer event) ──────────────────
        // One per event that HAS an Architect event node — Timer.OnZero /
        // Timer.OnMilestone / Timer.OnAdd. The token hints list exactly what the
        // matching raise site binds.
        ZeroFeedback = new TimerFeedbackCardVm(
            Localizer.T("panel.timer.feedback.zero.eyebrow", "ON ZERO"),
            "{timer} · {slug}",
            Localizer.T("panel.timer.feedback.zero.message.placeholder", "The subathon is over — thank you all!"),
            LayerIds, GetTriggersFor, CommitFeedbackAsync);
        MilestoneFeedback = new TimerFeedbackCardVm(
            Localizer.T("panel.timer.feedback.milestone.eyebrow", "ON MILESTONE"),
            "{timer} · {slug} · {label}",
            Localizer.T("panel.timer.feedback.milestone.message.placeholder", "GOAL REACHED — {label}!"),
            LayerIds, GetTriggersFor, CommitFeedbackAsync);
        // "clock now", not "left": {clock} renders DisplayMs, which is ELAPSED on a
        // Stopwatch and remaining on the other two modes, so "left" is backwards for one
        // of the three timer kinds. {count} is offered in the hint above but kept out of
        // the example — a template cannot inflect a noun, and every natural phrasing of it
        // needs either "event(s)" (the parenthesised plural this page dropped) or a bare
        // "1 events". The example is copy a streamer may broadcast verbatim.
        AddFeedback = new TimerFeedbackCardVm(
            Localizer.T("panel.timer.feedback.add.eyebrow", "ON TIME ADDED"),
            "{timer} · {slug} · {seconds} · {count} · {source} · {remaining} · {clock}",
            Localizer.T("panel.timer.feedback.add.message.placeholder", "+{seconds}s added — clock now {clock}"),
            LayerIds, GetTriggersFor, CommitFeedbackAsync);

        // Feedback is defaulted by the model, but a hand-edited blob can deserialize it
        // as null — read it defensively rather than trusting the JSON.
        AddMinSecondsField = new TimerDraftField(
            t => MinSecondsOf(t).ToString(CultureInfo.InvariantCulture),
            (d, t) => PlanSeconds(d, MinSecondsOf(t),
                v => _svc.SetFeedbackAsync(t.Slug, BuildFeedbackSettings(v))));

        _allFields = new[]
        {
            SubT1Field, SubT2Field, SubT3Field, SubPrimeField,
            BitsPer100Field, TipPerUnitField, FollowField, RaidPerViewerField,
            StartDurationField, MaxCapField, PerAddCapField,
            AddMinSecondsField,
        };

        _svc.TimersChanged += OnTimersChanged;
        _svc.TimerTicked += OnTimerTicked;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.TimersChanged -= OnTimersChanged;
        _svc.TimerTicked -= OnTimerTicked;
    }

    // ── Bound collections ───────────────────────────────────────────────
    public ObservableCollection<TimerPickerItemVm> PickerItems { get; } = new();
    public ObservableCollection<TimerMilestoneRowVm> Milestones { get; } = new();
    public ObservableCollection<TimerActivityRowVm> Activity { get; } = new();

    /// <summary>The selected timer snapshot — the view passes it to field commits.</summary>
    public StreamTimer? SelectedTimer => _selected;

    // ── Settings-card fields ────────────────────────────────────────────
    public TimerDraftField SubT1Field { get; }
    public TimerDraftField SubT2Field { get; }
    public TimerDraftField SubT3Field { get; }
    public TimerDraftField SubPrimeField { get; }
    public TimerDraftField BitsPer100Field { get; }
    public TimerDraftField TipPerUnitField { get; }
    public TimerDraftField FollowField { get; }
    public TimerDraftField RaidPerViewerField { get; }
    public TimerDraftField StartDurationField { get; }
    public TimerDraftField MaxCapField { get; }
    public TimerDraftField PerAddCapField { get; }
    public TimerDraftField AddMinSecondsField { get; }

    // ── Feedback card VMs (chat + visual responses) ──────────────────────
    public TimerFeedbackCardVm ZeroFeedback { get; }
    public TimerFeedbackCardVm MilestoneFeedback { get; }
    public TimerFeedbackCardVm AddFeedback { get; }

    /// <summary>Registered overlay layer ids, shared by the three cards AND every
    /// milestone row — one registry read per refresh instead of one per picker.</summary>
    public ObservableCollection<string> LayerIds { get; } = new();

    // ── Initial load ────────────────────────────────────────────────────
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        // Called from TimerView.OnLoaded, i.e. already on the UI thread — LayerIds is a
        // bound ObservableCollection. The feedback pickers re-read on drop-down open too,
        // so a layer registered later in the session still shows up.
        RefreshLayers();
        await RefreshTimersAsync().ConfigureAwait(false);
    }

    // preferredSlug: when set (e.g. a just-created timer), it wins the selection so a
    // create can't be lost to a concurrent refresh. The whole preferred computation is
    // resolved INSIDE the UI-thread post — reading _selected off the list-continuation
    // thread was a race (it saw the pre-create selection before the create's _selected
    // assignment posted), so a non-default new timer sometimes failed to select.
    private async Task RefreshTimersAsync(string? preferredSlug = null)
    {
        List<StreamTimer> list;
        try { list = await _svc.ListAsync().ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "ListAsync failed", ex); return; }

        _ui.Post(() =>
        {
            if (_disposed) return;
            _all = list;
            RebuildPicker();
            string? preferred = preferredSlug
                ?? _selected?.Slug
                ?? _svc.GetDefaultSlug()
                ?? (_all.Count > 0 ? _all[0].Slug : null);
            var pick = (preferred is not null
                    ? _all.FirstOrDefault(t => string.Equals(t.Slug, preferred, StringComparison.OrdinalIgnoreCase))
                    : null)
                ?? _all.FirstOrDefault();
            ApplySelection(pick);
        });
    }

    private void RebuildPicker()
    {
        PickerItems.Clear();
        IEnumerable<StreamTimer> filtered = _all;
        if (!string.IsNullOrWhiteSpace(_pickerFilter))
        {
            string q = _pickerFilter.Trim();
            filtered = _all.Where(t =>
                t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Slug.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var t in filtered) PickerItems.Add(new TimerPickerItemVm(t));
    }

    // ── Selection ───────────────────────────────────────────────────────
    public void SelectTimer(string slug)
    {
        var t = _all.FirstOrDefault(x => string.Equals(x.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (t is null) return;
        ApplySelection(t);
    }

    // Fingerprint of the last-applied selection — see SelectionStamp. Empty until
    // the first ApplySelection, and reset by every selection change.
    private string _appliedStamp = string.Empty;

    // Everything about the SELECTED timer that the two ItemsRepeater collections
    // and the activity query depend on. TimersChanged is a GLOBAL event raised
    // from 20+ call sites across ALL timers, so without this gate a sub landing on
    // a different timer tore down and rebuilt this timer's Milestones + Activity
    // lists (and fired an extra GetActivityAsync), resetting the user's scroll in
    // both repeaters mid-subathon. UpdatedAtUnixMs covers every mutating write;
    // IsDefault and the milestone counts cover the two service paths that change
    // what's on screen WITHOUT bumping that stamp (SetDefaultAsync, and the tick's
    // milestone-crossing sweep).
    private static string SelectionStamp(StreamTimer? t)
        => t is null
            ? string.Empty
            : string.Join('|',
                t.Slug,
                t.UpdatedAtUnixMs.ToString(CultureInfo.InvariantCulture),
                t.IsDefault ? "1" : "0",
                t.Milestones.Count.ToString(CultureInfo.InvariantCulture),
                t.Milestones.Count(m => m.Reached).ToString(CultureInfo.InvariantCulture));

    private void ApplySelection(StreamTimer? g)
    {
        string stamp = SelectionStamp(g);
        // A refresh that left the selected timer byte-identical came from activity
        // on some OTHER timer. The cheap projections below still re-run (live
        // readings, field seeding, property raises); the list teardown does not.
        bool unchanged = g is not null && string.Equals(_appliedStamp, stamp, StringComparison.Ordinal);

        _selected = g;
        _appliedStamp = stamp;

        if (g is null)
        {
            _liveRemainingMs = 0;
            _liveState = TimerRunState.Stopped;
            _liveProgress = 0;
        }
        else
        {
            _liveRemainingMs = _svc.GetRemainingMs(g.Slug);
            _liveState = _svc.GetState(g.Slug);
            _liveProgress = _svc.GetProgress(g.Slug);
        }

        // Seed settings fields (pristine drafts survive a same-timer refresh).
        foreach (var f in _allFields) f.SeedIfPristine(g);
        if (_hhSeededSlug != g?.Slug) SeedHappyHour(g);

        // Feedback cards re-seed on every pass, not just on a slug change: each card
        // skips itself while it holds focus, so this only ever overwrites text nobody is
        // editing, and it keeps the cards true to the store after any write.
        ZeroFeedback.Seed(g?.Feedback?.Zero);
        MilestoneFeedback.Seed(g?.Feedback?.Milestone);
        AddFeedback.Seed(g?.Feedback?.Add);

        RaiseDetailProperties();

        if (unchanged) return;

        // Milestones list — reconciled BY ID, never rebuilt.
        //
        // The old Clear + re-Add replaced every row object on each refresh. Now that a
        // row hosts editable label/target boxes that is fatal: SetMilestoneAsync bumps
        // UpdatedAtUnixMs, UpdatedAtUnixMs is part of SelectionStamp, so committing an
        // edit changes the stamp and lands right here — the row would tear itself out
        // from under its own caret. Reconciling in place also keeps scroll position and
        // focus across the unrelated refreshes other timers cause.
        ReconcileMilestones(g);

        // Activity feed.
        if (g is null) { Activity.Clear(); return; }
        _ = Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
            () => LoadActivityAsync(g.Slug), "TimerViewModel", "activity load");
    }

    private async Task LoadActivityAsync(string slug)
    {
        List<(string Time, string Kind, string Message)> list;
        try { list = await _svc.GetActivityAsync(slug).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "GetActivityAsync failed", ex); return; }

        _ui.Post(() =>
        {
            if (_disposed) return;
            if (!string.Equals(_selected?.Slug, slug, StringComparison.OrdinalIgnoreCase)) return;
            Activity.Clear();
            foreach (var a in list) Activity.Add(new TimerActivityRowVm(a.Time, a.Kind, a.Message));
        });
    }

    // ── Detail projections (left card) ──────────────────────────────────
    public bool HasSelection => _selected is not null;
    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    public string Name => _selected?.Name ?? string.Empty;
    public string MetaSlug => _selected?.Slug ?? string.Empty;

    // Timer kind + a badge label. Actions / Happy-Hour / Milestones only make
    // sense for a Subathon timer (the only mode that accrues time from stream
    // events), so those settings sections bind their Visibility to
    // SubathonSettingsVisibility and collapse for Countdown / Stopwatch.
    public TimerMode Mode => _selected?.Mode ?? TimerMode.Subathon;
    public string ModeLabel => Mode switch
    {
        TimerMode.Countdown => Localizer.T("panel.timer.mode.countdown", "Countdown"),
        TimerMode.Stopwatch => Localizer.T("panel.timer.mode.stopwatch", "Stopwatch"),
        _                   => Localizer.T("panel.timer.mode.subathon", "Subathon"),
    };
    public Visibility SubathonSettingsVisibility =>
        Mode == TimerMode.Subathon ? Visibility.Visible : Visibility.Collapsed;

    // COUNTDOWN LIMITS (Start duration + Max cap) apply to any count-DOWN mode —
    // both Subathon AND Countdown seed / clamp RemainingMs — so that block hides
    // only for a Stopwatch, which counts up from zero with no ceiling. (The
    // Per-add cap row and the max-cap progress bar stay Subathon-only via
    // SubathonSettingsVisibility: only a Subathon accrues from non-manual stream
    // events and climbs toward the cap.)
    public Visibility DurationLimitsVisibility =>
        Mode == TimerMode.Stopwatch ? Visibility.Collapsed : Visibility.Visible;

    // MILESTONES apply to ALL THREE MODES — there is deliberately no visibility
    // gate here.
    //
    // A property named MilestonesVisibility used to sit at this spot aliasing
    // SubathonSettingsVisibility, with a comment claiming milestones were
    // Subathon-only and that "leaving the panel visible let a user add goals that
    // could never fire". Two things were wrong with it: it was bound NOWHERE in
    // TimerView.xaml (dead code — the panel was always visible regardless), and the
    // premise no longer holds. TimerService now resolves every crossing against the
    // mode-aware DisplayMs, so a Stopwatch goal fires on ELAPSED time and a
    // Countdown goal fires exactly as a Subathon's does. Milestones work everywhere,
    // so nothing should hide them.

    // What the ADJUST strip (+ADD / -SUB / = SET) moves, per mode. All three act
    // on the mode's own authoritative field — elapsed for a Stopwatch, remaining
    // for a Countdown / Subathon — so the strip stays live in every mode; this is
    // a passive label naming the field, never a gate on the buttons.
    public string AdjustTargetHint => Mode == TimerMode.Stopwatch
        ? Localizer.T("panel.timer.adjust.target_hint.elapsed", "moves elapsed time")
        : Localizer.T("panel.timer.adjust.target_hint.remaining", "moves remaining time");

    // At-a-glance stat tiles (design-language clone of the Giveaway StateStat row).
    public string TotalAddedValue => TimerSvc.FormatDuration(_selected?.TotalAddedMs ?? 0, "short");
    public string StartDurationValue => _selected is null ? "—" : FormatDurationCompact(_selected.StartDurationMs);
    public string MaxCapValue => _selected is null ? "—" : (_selected.MaxCapMs <= 0 ? "∞" : FormatDurationCompact(_selected.MaxCapMs));
    public string MilestonesValue => _selected is null
        ? "0"
        : $"{_selected.Milestones.Count(m => m.Reached)}/{_selected.Milestones.Count}";

    // Big countdown — folds to D:HH:MM:SS past 24h (FormatDuration "short").
    public string CountdownText => TimerSvc.FormatDuration(_liveRemainingMs, "short");

    // Thin progress bar (remaining vs. Max cap). 0 when cap is unlimited.
    public GridLength CountdownProgressBarLength => new(Math.Clamp(_liveProgress, 0, 1), GridUnitType.Star);
    public GridLength CountdownProgressRestLength => new(1.0 - Math.Clamp(_liveProgress, 0, 1), GridUnitType.Star);

    // State pill.
    public bool IsRunning => _liveState == TimerRunState.Running;
    public string StatePillText => _liveState switch
    {
        TimerRunState.Running => Mode == TimerMode.Stopwatch
            ? Localizer.T("panel.timer.state.running_up", "running · counting up")
            : Localizer.T("panel.timer.state.running_down", "running · counting down"),
        TimerRunState.Paused  => Localizer.T("panel.timer.state.paused", "paused"),
        TimerRunState.Ended   => Localizer.T("panel.timer.state.ended", "ended · reached zero"),
        _                     => Localizer.T("panel.timer.state.stopped", "stopped"),
    };
    public Brush StatePillBrush => _liveState switch
    {
        TimerRunState.Running => TimerBrushes.Lookup("OkBrush",                0x6F, 0xA4, 0x6B),
        TimerRunState.Paused  => TimerBrushes.Lookup("EmberPrimaryBrush",      0xE5, 0xA2, 0x4E),
        TimerRunState.Ended   => TimerBrushes.Lookup("ErrBrush",               0xC8, 0x5C, 0x54),
        _                     => TimerBrushes.Lookup("CoalSecondaryTextBrush", 0x9C, 0x8A, 0x72),
    };
    public Visibility StateDotPulseVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;

    // Control-button enablement + labels.
    public bool CanStart => HasSelection && _liveState != TimerRunState.Running;
    public bool CanPauseResume => HasSelection && (_liveState == TimerRunState.Running || _liveState == TimerRunState.Paused);
    public bool CanReset => HasSelection;
    public bool CanStop => HasSelection && _liveState != TimerRunState.Stopped;
    public string PauseResumeLabel => _liveState == TimerRunState.Paused
        ? Localizer.T("panel.timer.controls.resume.label", "RESUME")
        : Localizer.T("panel.timer.controls.pause.label", "PAUSE");
    public bool CanDelete => HasSelection;

    private void RaiseStateProperties()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(StatePillText));
        Raise(nameof(StatePillBrush));
        Raise(nameof(StateDotPulseVisibility));
        Raise(nameof(CanStart));
        Raise(nameof(CanPauseResume));
        Raise(nameof(CanStop));
        Raise(nameof(PauseResumeLabel));
    }

    private void RaiseDetailProperties()
    {
        Raise(nameof(HasSelection));
        Raise(nameof(DetailVisibility));
        Raise(nameof(EmptyVisibility));
        Raise(nameof(Name));
        Raise(nameof(MetaSlug));
        Raise(nameof(Mode));
        Raise(nameof(ModeLabel));
        Raise(nameof(SubathonSettingsVisibility));
        Raise(nameof(DurationLimitsVisibility));
        Raise(nameof(AdjustTargetHint));
        Raise(nameof(TotalAddedValue));
        Raise(nameof(StartDurationValue));
        Raise(nameof(MaxCapValue));
        Raise(nameof(MilestonesValue));
        Raise(nameof(CountdownText));
        Raise(nameof(CountdownProgressBarLength));
        Raise(nameof(CountdownProgressRestLength));
        RaiseStateProperties();
        Raise(nameof(CanReset));
        Raise(nameof(CanDelete));
        Raise(nameof(IsDefault));
        Raise(nameof(DefaultToggleLabel));
        Raise(nameof(PauseWhenOffline));
        Raise(nameof(HappyHourStatusText));
        Raise(nameof(HasPickerSelection));
        Raise(nameof(PickerButtonTitle));
        Raise(nameof(PickerButtonId));
        Raise(nameof(PickerButtonDefaultVisibility));
    }

    private void RefreshLiveReadings()
    {
        var slug = _selected?.Slug;
        if (slug is null) return;
        long rem = _svc.GetRemainingMs(slug);
        var state = _svc.GetState(slug);
        double prog = _svc.GetProgress(slug);
        bool stateChanged = state != _liveState;
        _liveRemainingMs = rem;
        _liveProgress = prog;
        _liveState = state;
        Raise(nameof(CountdownText));
        Raise(nameof(CountdownProgressBarLength));
        Raise(nameof(CountdownProgressRestLength));
        Raise(nameof(HappyHourStatusText));
        if (stateChanged) RaiseStateProperties();
    }

    // ── Picker button (collapsed display in the action strip) ───────────
    public bool HasPickerSelection => _selected is not null;
    public string PickerButtonTitle => _selected?.Name ?? Localizer.T("panel.timer.picker.empty", "Pick a timer…");
    public string PickerButtonId => _selected?.Slug ?? string.Empty;
    public Visibility PickerButtonDefaultVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;

    public string PickerFilter
    {
        get => _pickerFilter;
        set { if (Set(ref _pickerFilter, value ?? string.Empty)) RebuildPicker(); }
    }

    // ── Settings-card collapse toggle ───────────────────────────────────
    private bool _settingsExpanded = true;
    public bool SettingsExpanded
    {
        get => _settingsExpanded;
        set
        {
            if (Set(ref _settingsExpanded, value))
            {
                Raise(nameof(SettingsBodyVisibility));
                Raise(nameof(SettingsChevronGlyph));
                Raise(nameof(SettingsToggleAutomationName));
            }
        }
    }
    public Visibility SettingsBodyVisibility => _settingsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string SettingsChevronGlyph => _settingsExpanded ? "" : "";
    public string SettingsToggleAutomationName => _settingsExpanded
        ? Localizer.T("panel.timer.settings.toggle.collapse.a11y", "Collapse timer settings (currently expanded)")
        : Localizer.T("panel.timer.settings.toggle.expand.a11y", "Expand timer settings (currently collapsed)");
    public void ToggleSettingsExpanded() => SettingsExpanded = !SettingsExpanded;

    // ── Default toggle (top strip) ──────────────────────────────────────
    public bool IsDefault => _selected?.IsDefault ?? false;
    public string DefaultToggleLabel => IsDefault
        ? Localizer.T("panel.timer.strip.default_toggle.on.label", "Default timer")
        : Localizer.T("panel.timer.strip.default_toggle.off.label", "Set as default");

    public async Task ToggleDefaultAsync()
    {
        var g = _selected;
        if (g is null) return;
        // There is always exactly one default (SetDefaultAsync clears the
        // others in one transaction). Clicking a non-default makes it the
        // default; clicking the current default is a no-op — there's nothing
        // to fall back to, so we don't offer an "un-default".
        if (g.IsDefault) return;
        try
        {
            await _svc.SetDefaultAsync(g.Slug).ConfigureAwait(false);
            await RefreshTimersAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "SetDefaultAsync failed", ex); }
    }

    // ── Pause-when-offline toggle (settings card) ───────────────────────
    public bool PauseWhenOffline => _selected?.PauseWhenOffline ?? true;

    public async Task TogglePauseWhenOfflineAsync()
    {
        var g = _selected;
        if (g is null) return;
        try
        {
            await _svc.SetPauseWhenOfflineAsync(g.Slug, !g.PauseWhenOffline).ConfigureAwait(false);
            await RefreshTimersAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "SetPauseWhenOfflineAsync failed", ex); }
    }

    // ── Control buttons ─────────────────────────────────────────────────
    // START goes through StartFromCurrentAsync, NOT StartAsync(slug, null): the null
    // overload re-arms to the configured Default Time unconditionally, which is the
    // documented contract of the Architect Timer.Start node's empty Duration socket but
    // the wrong answer for a button. It destroyed whatever "= SET" had just written (a
    // fresh Countdown snapped back to 4h) and zeroed TOTAL ADDED — and this button is
    // enabled while PAUSED, one button-width from RESUME, so a mis-click wiped a live
    // subathon's accrual. The panel entry point continues from the clock on screen and
    // only re-arms a count-down that has actually run dry.
    public Task StartAsync() => RunControlAsync("StartAsync", g => _svc.StartFromCurrentAsync(g.Slug));
    public Task StopAsync() => RunControlAsync("StopAsync", g => _svc.StopAsync(g.Slug));
    public Task ResetAsync() => RunControlAsync("ResetAsync", g => _svc.ResetAsync(g.Slug));

    public Task PauseResumeAsync() => RunControlAsync("PauseResumeAsync",
        g => _liveState == TimerRunState.Paused ? _svc.ResumeAsync(g.Slug) : _svc.PauseAsync(g.Slug));

    private async Task RunControlAsync(string label, Func<StreamTimer, Task> body)
    {
        var g = _selected;
        if (g is null) return;
        try { await body(g).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", $"{label} failed", ex); }
    }

    // ── Add / subtract / set time (control strip duration box) ──────────
    private string _addDurationDraft = "5m";
    public string AddDurationDraft
    {
        get => _addDurationDraft;
        set => Set(ref _addDurationDraft, value ?? string.Empty);
    }

    public async Task AddTimeAsync()
    {
        var g = _selected;
        if (g is null) return;
        long ms = ParseDurationToMs(_addDurationDraft);
        if (ms <= 0) { GlobalLogger.Log("Add-time amount invalid — ignoring.", "TimerViewModel", LogLevel.System); return; }
        try { await _svc.AddMsAsync(g.Slug, ms, "manual").ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "AddMsAsync failed", ex); }
    }

    public async Task SubtractTimeAsync()
    {
        var g = _selected;
        if (g is null) return;
        long ms = ParseDurationToMs(_addDurationDraft);
        if (ms <= 0) { GlobalLogger.Log("Subtract-time amount invalid — ignoring.", "TimerViewModel", LogLevel.System); return; }
        try { await _svc.SubtractMsAsync(g.Slug, ms).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "SubtractMsAsync failed", ex); }
    }

    public async Task SetTimeAsync()
    {
        var g = _selected;
        if (g is null) return;
        long ms = ParseDurationToMs(_addDurationDraft);
        if (ms < 0) { GlobalLogger.Log("Set-time amount invalid — ignoring.", "TimerViewModel", LogLevel.System); return; }
        try { await _svc.SetTimeMsAsync(g.Slug, ms).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "SetTimeMsAsync failed", ex); }
    }

    // ── Happy Hour form (settings card) ─────────────────────────────────
    private string? _hhSeededSlug;
    private string _hhMultiplierDraft = "2";
    private string _hhDurationDraft = "30m";
    private string _hhScope = "all";

    public IReadOnlyList<string> HappyHourScopes { get; } =
        new[] { "all", "subs", "bits", "tips", "follows", "raids" };

    public string HappyHourMultiplierDraft
    {
        get => _hhMultiplierDraft;
        set => Set(ref _hhMultiplierDraft, value ?? string.Empty);
    }
    public string HappyHourDurationDraft
    {
        get => _hhDurationDraft;
        set => Set(ref _hhDurationDraft, value ?? string.Empty);
    }
    public string HappyHourScope
    {
        get => _hhScope;
        set => Set(ref _hhScope, string.IsNullOrWhiteSpace(value) ? "all" : value);
    }

    public string HappyHourStatusText
    {
        get
        {
            var g = _selected;
            if (g is null) return string.Empty;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (g.HappyHourEndsAtUnixMs > now)
            {
                long leftMs = g.HappyHourEndsAtUnixMs - now;
                return string.Format(
                    Localizer.T("panel.timer.happy_hour.status.active", "active ×{0} · {1} left · {2}"),
                    FormatFactor(g.HappyHourMultiplier), FormatDurationCompact(leftMs), g.HappyHourScope);
            }
            return Localizer.T("panel.timer.happy_hour.status.inactive", "inactive");
        }
    }

    private void SeedHappyHour(StreamTimer? g)
    {
        _hhSeededSlug = g?.Slug;
        if (g is not null)
        {
            HappyHourMultiplierDraft = FormatFactor(g.HappyHourMultiplier <= 1.0 ? 2.0 : g.HappyHourMultiplier);
            HappyHourScope = g.HappyHourScope;
        }
    }

    public async Task ApplyHappyHourAsync()
    {
        var g = _selected;
        if (g is null) return;

        string raw = (_hhMultiplierDraft ?? string.Empty).Trim().TrimEnd('x', 'X', '×').Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double mult)
            || double.IsNaN(mult) || double.IsInfinity(mult))
        {
            GlobalLogger.Log("Happy Hour multiplier invalid — ignoring.", "TimerViewModel", LogLevel.System);
            return;
        }
        mult = Math.Clamp(mult, 1.0, 100.0);

        long durMs = ParseDurationToMs(_hhDurationDraft);
        if (durMs <= 0)
        {
            GlobalLogger.Log("Happy Hour duration invalid — ignoring.", "TimerViewModel", LogLevel.System);
            return;
        }
        string scope = string.IsNullOrWhiteSpace(_hhScope) ? "all" : _hhScope;

        try { await _svc.SetHappyHourAsync(g.Slug, mult, durMs, scope).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "SetHappyHourAsync failed", ex); }
    }

    // ── Milestones (right column) ───────────────────────────────────────
    private string _milestoneLabelDraft = string.Empty;
    private string _milestoneTargetDraft = "1h";
    public string MilestoneLabelDraft
    {
        get => _milestoneLabelDraft;
        set => Set(ref _milestoneLabelDraft, value ?? string.Empty);
    }
    public string MilestoneTargetDraft
    {
        get => _milestoneTargetDraft;
        set => Set(ref _milestoneTargetDraft, value ?? string.Empty);
    }

    public async Task AddMilestoneAsync()
    {
        var g = _selected;
        if (g is null) return;
        string label = (_milestoneLabelDraft ?? string.Empty).Trim();
        if (!TryReadMilestoneTargetSeconds(_milestoneTargetDraft, out long targetSeconds))
        {
            GlobalLogger.Log("Milestone target invalid — a goal needs at least one second; ignoring.",
                "TimerViewModel", LogLevel.System);
            return;
        }
        try
        {
            await _svc.AddMilestoneAsync(g.Slug, label, targetSeconds).ConfigureAwait(false);
            _ui.Post(() => { if (!_disposed) MilestoneLabelDraft = string.Empty; });
        }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "AddMilestoneAsync failed", ex); }
    }

    // The pill's canonical target rendering. "clock" rather than "short" on
    // purpose: the parser reads at most three colon segments, and "short" folds
    // a ≥24h value into D:HH:MM:SS — a pre-fill the pill would then refuse on
    // commit. "clock" keeps big targets as plain big-hours HH:MM:SS
    // ("25:00:00"), so EVERY pre-fill survives its own round trip. The two
    // styles render identically below 24h.
    private static string MilestoneTargetText(long targetSeconds)
        => TimerSvc.FormatDuration(targetSeconds * 1000L, "clock");

    // The milestone-target gate + the duration grammar both live on
    // TimerService (beside FormatDuration, whose output the pill pre-fills)
    // so the test suite can pin them without loading this WinUI-coupled
    // class - reflecting into it crashes a bare test host. These wrappers
    // keep the call sites reading naturally.
    private static bool TryReadMilestoneTargetSeconds(string? draft, out long targetSeconds)
        => TimerSvc.TryReadMilestoneTargetSeconds(draft, out targetSeconds);

    /// <summary>
    /// Brings <see cref="Milestones"/> in line with the timer's milestone list,
    /// matching existing rows by Id and reusing them. Order follows the source list.
    /// </summary>
    private void ReconcileMilestones(StreamTimer? g)
    {
        if (g is null) { Milestones.Clear(); return; }

        // Drop rows whose milestone is gone.
        for (int i = Milestones.Count - 1; i >= 0; i--)
            if (!g.Milestones.Exists(m => m.Id == Milestones[i].Id))
                Milestones.RemoveAt(i);

        for (int i = 0; i < g.Milestones.Count; i++)
        {
            var m = g.Milestones[i];
            string targetText = MilestoneTargetText(m.TargetSeconds);

            int at = -1;
            for (int j = 0; j < Milestones.Count; j++)
                if (Milestones[j].Id == m.Id) { at = j; break; }

            if (at < 0)
            {
                Milestones.Insert(Math.Min(i, Milestones.Count),
                    new TimerMilestoneRowVm(m, targetText, CommitMilestoneAsync,
                        LayerIds, GetTriggersFor, CommitMilestoneFeedbackAsync));
                continue;
            }

            Milestones[at].Update(m, targetText);          // in place — keeps focus
            if (at != i) Milestones.Move(at, i);
        }
    }

    /// <summary>
    /// Commits a row's edited label / target through the service. Parses the target
    /// with the same reader the ADJUST strip and the add-row use, so "1h30m", "90s",
    /// a bare number (seconds) and the clock form the pill itself pre-fills all mean
    /// here what they mean everywhere else.
    /// </summary>
    private async Task CommitMilestoneAsync(TimerMilestoneRowVm row)
    {
        var g = _selected;
        if (g is null || row is null) return;

        var current = g.Milestones.Find(m => m.Id == row.Id);
        if (current is null) return;

        string label = (row.LabelDraft ?? string.Empty).Trim();
        bool labelUnchanged = string.Equals(label, current.Label ?? string.Empty, StringComparison.Ordinal);

        // An untouched pill is not even parsed. Focus can pass straight through a
        // row (Tab, clicking elsewhere), and the draft is this VM's own canonical
        // pre-fill — parse-first meant a mere focus-through logged a warning and
        // "reverted" a value nobody had changed.
        if (labelUnchanged
            && string.Equals(row.TargetDraft, MilestoneTargetText(current.TargetSeconds), StringComparison.Ordinal))
            return;

        if (!TryReadMilestoneTargetSeconds(row.TargetDraft, out long targetSeconds))
        {
            // Malformed, zero, or sub-second — say why and put the last good value
            // back rather than writing a goal that would read as reached forever.
            GlobalLogger.Log(
                $"Milestone target \"{row.TargetDraft}\" is not a duration of at least one second — keeping the previous value.",
                "TimerViewModel", LogLevel.System);
            row.TargetDraft = MilestoneTargetText(current.TargetSeconds);
            return;
        }

        // An edit that lands on the stored value ("300" retyped for a 00:05:00
        // goal) — don't churn a checkpoint + RaiseTimers on a no-op commit.
        if (targetSeconds == current.TargetSeconds && labelUnchanged)
            return;

        try { await _svc.SetMilestoneAsync(g.Slug, row.Id, label, targetSeconds).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "SetMilestoneAsync failed", ex); }
    }

    public async Task RemoveMilestoneAsync(string id)
    {
        var g = _selected;
        if (g is null || string.IsNullOrEmpty(id)) return;
        try { await _svc.RemoveMilestoneAsync(g.Slug, id).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "RemoveMilestoneAsync failed", ex); }
    }

    // ── Feedback (chat + visual responses) ──────────────────────────────
    // Layer / trigger enumeration is a THIRD copy of the AlertsViewModel idiom
    // (SoundboardViewModel.RefreshLayers is the second). Those two are public instance
    // methods over an ObservableCollection their own VM owns, so reusing one would mean
    // constructing another panel's ViewModel — with its service subscriptions and its
    // debounce timer — purely to read the layer registry. Extracting the pair into a
    // shared helper is the right move now that there are three, and is deliberately left
    // as its own change rather than folded into this one.

    /// <summary>Re-reads the registered layer ids into <see cref="LayerIds"/>. No-ops
    /// when unchanged so an open drop-down isn't churned, and keeps the last-known-good
    /// list when the registry is unreachable. UI thread only.</summary>
    public void RefreshLayers()
    {
        var ids = new List<string>();
        try
        {
            foreach (var id in LayerRt.Instance.Registry.GetRegisteredLayerIds())
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        catch (Exception ex)
        {
            // LayerRuntime may not be initialised (design contexts / early boot) — keep
            // whatever list we last resolved rather than blanking the pickers.
            GlobalLogger.Error("TimerViewModel", "layer enumeration failed", ex);
            return;
        }
        ids.Sort(StringComparer.OrdinalIgnoreCase);

        bool same = ids.Count == LayerIds.Count;
        if (same)
        {
            for (int i = 0; i < ids.Count; i++)
                if (!string.Equals(ids[i], LayerIds[i], StringComparison.Ordinal)) { same = false; break; }
        }
        if (same) return;
        LayerIds.Clear();
        foreach (var id in ids) LayerIds.Add(id);
    }

    /// <summary>Deduped union of the trigger names across every widget of the given layer
    /// ("onStartup" / "onTrigger:&lt;x&gt;" / "trigger_&lt;8hex&gt;"). Empty on a blank or
    /// unknown layer, or when the registry is unreachable.</summary>
    public IReadOnlyList<string> GetTriggersFor(string layerId)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(layerId)) return result;
        try
        {
            var layer = LayerRt.Instance.Registry.GetLayer(layerId.Trim());
            if (layer?.Widgets is null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var widget in layer.Widgets)
            {
                if (widget?.Triggers is null) continue;
                foreach (var trigger in widget.Triggers)
                {
                    string name = trigger?.Name ?? "";
                    if (name.Length == 0) continue;
                    if (seen.Add(name)) result.Add(name);
                }
            }
        }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "trigger enumeration failed", ex); }
        return result;
    }

    private static int MinSecondsOf(StreamTimer? t) => t?.Feedback is null ? 0 : t.Feedback.AddMinSeconds;

    // The three cards plus the minimum-seconds box are ONE stored object, so every commit
    // writes the whole thing — the SetActionConfigAsync shape. minSeconds is threaded in
    // because AddMinSecondsField commits before its own draft has been seeded back.
    private TimerFeedbackSettings BuildFeedbackSettings(int? minSeconds = null)
    {
        int stored = MinSecondsOf(_selected);
        return new TimerFeedbackSettings
        {
            Zero = ZeroFeedback.ToConfig(),
            Milestone = MilestoneFeedback.ToConfig(),
            Add = AddFeedback.ToConfig(),
            AddMinSeconds = minSeconds ?? stored,
        };
    }

    /// <summary>The single write path behind all three cards' toggles and text boxes.</summary>
    private async Task CommitFeedbackAsync()
    {
        var g = _selected;
        if (g is null) return;
        try { await _svc.SetFeedbackAsync(g.Slug, BuildFeedbackSettings()).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "SetFeedbackAsync failed", ex); }
    }

    /// <summary>Persists one milestone row's per-goal overrides. Separate from
    /// <see cref="CommitMilestoneAsync"/> because that one re-seeds Reached against a new
    /// target; editing the text a goal announces must not touch the goal itself.</summary>
    private async Task CommitMilestoneFeedbackAsync(TimerMilestoneRowVm row)
    {
        var g = _selected;
        if (g is null || row is null) return;

        var current = g.Milestones.Find(m => m.Id == row.Id);
        if (current is null) return;

        // Don't churn a checkpoint + RaiseTimers when focus merely passed through.
        if (string.Equals(row.MessageDraft, current.Message ?? "", StringComparison.Ordinal)
            && string.Equals(row.LayerIdDraft, current.LayerId ?? "", StringComparison.Ordinal)
            && string.Equals(row.TriggerNameDraft, current.TriggerName ?? "", StringComparison.Ordinal))
            return;

        try
        {
            await _svc.SetMilestoneFeedbackAsync(
                g.Slug, row.Id, row.MessageDraft, row.LayerIdDraft, row.TriggerNameDraft).ConfigureAwait(false);
        }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "SetMilestoneFeedbackAsync failed", ex); }
    }

    // ── Create flow ─────────────────────────────────────────────────────
    private bool _createDialogOpen;
    private string _createNameDraft = string.Empty;
    private int _createModeIndex;   // 0 Subathon · 1 Countdown · 2 Stopwatch
    public bool CreateDialogOpen
    {
        get => _createDialogOpen;
        set => Set(ref _createDialogOpen, value);
    }
    public string CreateNameDraft
    {
        get => _createNameDraft;
        set => Set(ref _createNameDraft, value ?? string.Empty);
    }

    // Which kind of timer the create dialog will mint. Bound to a ComboBox whose
    // item order is Subathon / Countdown / Stopwatch.
    public int CreateModeIndex
    {
        get => _createModeIndex;
        set => Set(ref _createModeIndex, value);
    }
    private static TimerMode ModeFromIndex(int i) => i switch
    {
        1 => TimerMode.Countdown,
        2 => TimerMode.Stopwatch,
        _ => TimerMode.Subathon,
    };

    public void BeginCreate()
    {
        CreateNameDraft = string.Empty;
        CreateModeIndex = 0;
        CreateDialogOpen = true;
    }
    public void CancelCreate() => CreateDialogOpen = false;

    public async Task ConfirmCreateAsync()
    {
        string name = _createNameDraft?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return;
        var mode = ModeFromIndex(_createModeIndex);
        CreateDialogOpen = false;
        try
        {
            var created = await _svc.CreateAsync(name, mode).ConfigureAwait(false);
            _ui.Post(() => { if (!_disposed) _selected = created; });
            // Thread the new slug in so it wins over the refresh CreateAsync's own
            // TimersChanged kicks off concurrently — the just-created timer is always selected.
            await RefreshTimersAsync(created?.Slug).ConfigureAwait(false);
        }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "CreateAsync failed", ex); }
    }

    // ── Delete ──────────────────────────────────────────────────────────
    public async Task DeleteSelectedAsync()
    {
        var g = _selected;
        if (g is null) return;
        try
        {
            await _svc.DeleteAsync(g.Slug).ConfigureAwait(false);
            _ui.Post(() => { if (!_disposed) _selected = null; });
            await RefreshTimersAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { GlobalLogger.Error("TimerViewModel", "DeleteAsync failed", ex); }
    }

    // ── Service events ──────────────────────────────────────────────────
    private void OnTimersChanged(object? sender, EventArgs e)
        => _ = Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
            () => RefreshTimersAsync(), "TimerViewModel", "TimersChanged refresh");

    private void OnTimerTicked(object? sender, string slug)
    {
        if (_disposed) return;
        _ui.Post(() =>
        {
            if (_disposed) return;
            if (!string.Equals(_selected?.Slug, slug, StringComparison.OrdinalIgnoreCase)) return;
            RefreshLiveReadings();
        });
    }

    // ── Commit planners (shared by the settings fields) ─────────────────
    private static (bool Valid, bool Changed, Func<Task>? Save) PlanSeconds(string draft, int current, Func<int, Task> save)
    {
        if (!int.TryParse((draft ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return (false, false, null);
        v = Math.Max(0, v);
        if (v == current) return (true, false, null);
        return (true, true, () => save(v));
    }

    private static (bool Valid, bool Changed, Func<Task>? Save) PlanDuration(string draft, long currentMs, Func<long, Task> save)
    {
        long ms = ParseDurationToMs(draft);
        if (ms < 0) return (false, false, null);
        if (ms == currentMs) return (true, false, null);
        return (true, true, () => save(ms));
    }

    private static TimerActionConfig CloneActionsWith(TimerActionConfig src, Action<TimerActionConfig> mutate)
    {
        var c = new TimerActionConfig
        {
            SubT1Seconds         = src.SubT1Seconds,
            SubT2Seconds         = src.SubT2Seconds,
            SubT3Seconds         = src.SubT3Seconds,
            SubPrimeSeconds      = src.SubPrimeSeconds,
            BitsPer100Seconds    = src.BitsPer100Seconds,
            TipPerUnitSeconds    = src.TipPerUnitSeconds,
            FollowSeconds        = src.FollowSeconds,
            RaidPerViewerSeconds = src.RaidPerViewerSeconds,
        };
        mutate(c);
        return c;
    }

    // ── Duration parse / format helpers ─────────────────────────────────
    private static string FormatFactor(double f) => f.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a duration string into milliseconds. Bare number = seconds
    /// (accepts "1,5" / "1.5"); a colon clock value — "MM:SS" or "HH:MM:SS",
    /// leading segment unbounded — which is the form the milestone pill
    /// pre-fills; otherwise a sequence of &lt;number&gt;&lt;unit&gt;
    /// where unit ∈ d/h/m/s (e.g. "90s", "5m", "1h30m", "2h", "1d"). Returns
    /// -1 on malformed input; "0" → 0. Numeric-range clamp only, no grammar
    /// control, per the standing input-validation rule.
    /// </summary>
    private static long ParseDurationToMs(string? raw)
        => TimerSvc.ParseDurationToMs(raw);

    /// <summary>Renders ms as a compact "1d2h3m4s" string (0 → "0"), round-trips through ParseDurationToMs.</summary>
    private static string FormatDurationCompact(long ms)
    {
        if (ms <= 0) return "0";
        long totalSec = ms / 1000;
        long d = totalSec / 86_400; totalSec %= 86_400;
        long h = totalSec / 3_600;  totalSec %= 3_600;
        long m = totalSec / 60;
        long sec = totalSec % 60;
        var sb = new StringBuilder();
        if (d > 0) sb.Append(d).Append('d');
        if (h > 0) sb.Append(h).Append('h');
        if (m > 0) sb.Append(m).Append('m');
        if (sec > 0) sb.Append(sec).Append('s');
        return sb.Length == 0 ? "0" : sb.ToString();
    }
}
