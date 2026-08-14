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
using SchedulingSvc = Phoenix.Controls.Hub.Core.SchedulingService;

namespace Phoenix.Controls.Hub.WinUI.Panels.SchedulingPanel;

/// <summary>
/// ViewModel for the Hub Scheduling page — the button-side front-end onto the same
/// recurring-timer store the SchedulingService tick loop drives. Like the Timer /
/// Counters VMs it reaches <see cref="SchedulingSvc.Instance"/> DIRECTLY for reads AND
/// writes and subscribes to its ConfigChanged / RuntimeChanged events — Hub.WinUI
/// already references the Hub runtime and the service is always-on, so no cross-lane
/// seam is needed.
///
/// Config editing model: the whole <see cref="SchedulingConfig"/> is deep-cloned into
/// per-row VMs + two toggle fields on construction. Every edit mutates a row def (or a
/// toggle) and schedules a debounced save; the save REBUILDS a fresh SchedulingConfig
/// from the current rows + toggles and hands it to <c>UpdateConfigAsync</c> (which
/// deep-owns it, re-arms, persists, and raises ConfigChanged). A self-triggered
/// ConfigChanged is detected by reference-equality against the last pushed instance and
/// skipped; a foreign ConfigChanged rebuilds the rows. Fire counts are NEVER stored in
/// the config — they are ephemeral service runtime state pulled in on load / RuntimeChanged.
///
/// PAGE SHELL (house two-column rebuild): the roster lives in the 0.9* live column and
/// the editable fields in the 1.4* detail column, so the page now carries a SELECTION —
/// <see cref="SelectedSchedule"/> — which it never had while every row was its own
/// inline editor. Selection is host state (ItemsRepeater has no selection model) and
/// survives a foreign config rebuild by Id, so an external edit does not yank the
/// streamer out of the schedule they were typing in.
/// </summary>
public sealed class SchedulingViewModel : ObservableObject, IDisposable
{
    private readonly SchedulingSvc _svc = SchedulingSvc.Instance;
    private readonly UiDispatcherPump _ui;
    private readonly DispatcherQueueTimer? _saveTimer;
    private readonly DispatcherQueueTimer? _pillTimer;

    private bool _masterEnabled;
    private bool _onlyWhenLive;
    private SchedulingConfig? _lastPushed;   // reference-equality self-trigger guard
    private bool _disposed;
    private bool _dirty;
    private bool _loaded;

    private ScheduleRowVm? _selected;

    // Last pill state we published, so the 5 s poll below raises nothing while
    // nothing has moved.
    private SchedulingSvc.SchedulingPillState _pillState;
    private string _pillHint = "";

    public SchedulingViewModel(DispatcherQueue? dispatcher)
    {
        _ui = new UiDispatcherPump(dispatcher);
        var cfg = Clone(_svc.Config);
        _masterEnabled = cfg.Enabled;
        _onlyWhenLive = cfg.OnlyWhenLive;

        if (dispatcher is not null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveWorkingAsync();

            // The pill's ARMED-WAITING-FOR-LIVE arm hangs off SchedulingService's
            // stream-live latch, and going offline raises NO event this VM can hear:
            // while the gate holds, nothing posts, so RuntimeChanged never fires
            // either. Without a heartbeat the chip would keep claiming "posting"
            // through a whole offline stretch. The tick is cheap by construction —
            // RefreshPill reads one enum and returns without raising unless the
            // state (or the failure reason) actually changed.
            _pillTimer = dispatcher.CreateTimer();
            _pillTimer.Interval = TimeSpan.FromSeconds(5);
            _pillTimer.IsRepeating = true;
            _pillTimer.Tick += (_, _) => RefreshPill();
            _pillTimer.Start();
        }

        _pillState = _svc.PillState;
        _pillHint = _svc.LastSendFailure;

        BuildRows(cfg);

        _svc.ConfigChanged += OnConfigChanged;
        _svc.RuntimeChanged += OnRuntimeChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.ConfigChanged -= OnConfigChanged;
        _svc.RuntimeChanged -= OnRuntimeChanged;
        _saveTimer?.Stop();
        _pillTimer?.Stop();
        // Flush any pending edit so the last keystroke isn't lost on close.
        if (_dirty)
        {
            _dirty = false;
            var cfg = BuildConfig();
            _lastPushed = cfg;
            // Parked, not dropped: at shutdown MainWindow's coordinator ends in
            // Environment.Exit(0), which killed this write mid-flight. The tracker
            // lets PreBuildsHostView.DisposeAllTools hand it to the coordinator as a
            // tracked step; mid-session tab closes behave exactly as before.
            Phoenix.Controls.Hub.WinUI.Controls.ToolConfigFlushTracker.Register(
                Phoenix.Controls.Hub.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => _svc.UpdateConfigAsync(cfg), "SchedulingViewModel", "final config flush"));
        }
    }

    // ── Bound collections (rebuilt wholesale on external reload) ─────────
    public ObservableCollection<ScheduleRowVm> Schedules { get; } = new();

    // ── Selection (NEW with the two-column shell) ────────────────────────
    public ScheduleRowVm? SelectedSchedule
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            if (_selected is not null) _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null) _selected.IsSelected = true;
            Raise(nameof(SelectedSchedule));
            Raise(nameof(HasSelection));
            Raise(nameof(DetailVisibility));
            Raise(nameof(EmptyVisibility));
            Raise(nameof(EmptyTitle));
            Raise(nameof(EmptySubtitle));
        }
    }

    public bool HasSelection => _selected is not null;
    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The empty pane says two different things, because "nothing exists yet"
    /// and "nothing is picked" are different problems with different next steps. The
    /// first is the sentence the pre-rebuild page showed under its list.</summary>
    public string EmptyTitle => Schedules.Count == 0
        ? Localizer.T("panel.scheduling.empty.title", "No schedules yet")
        : Localizer.T("panel.scheduling.empty.title_unselected", "No schedule selected");

    public string EmptySubtitle => Schedules.Count == 0
        ? Localizer.T("panel.scheduling.empty.body", "Add a schedule to start posting a chat line on an interval.")
        : Localizer.T("panel.scheduling.empty.body_unselected", "Pick one from the list, or add a new schedule.");

    // ── Master switch (immediate save, like CountersViewModel.MasterEnabled) ─
    public bool MasterEnabled
    {
        get => _masterEnabled;
        set
        {
            if (_masterEnabled == value) return;
            _masterEnabled = value;
            Raise(nameof(MasterEnabled));
            SaveNow();
            RefreshPill();
        }
    }

    // ── Only-while-live gate (immediate save) ────────────────────────────
    public bool OnlyWhenLive
    {
        get => _onlyWhenLive;
        set
        {
            if (_onlyWhenLive == value) return;
            _onlyWhenLive = value;
            Raise(nameof(OnlyWhenLive));
            Raise(nameof(LiveGateText));
            SaveNow();
            RefreshPill();
        }
    }

    /// <summary>The live-gate sentence that used to sit under the ONLY WHILE LIVE
    /// switch. It carries the non-obvious half of that setting: with no live detection
    /// wired up the service's latch defaults to LIVE, so "only while live" does not
    /// silently mute a channel that never reports going live.</summary>
    public string LiveGateText => _onlyWhenLive
        ? Localizer.T("panel.scheduling.live_gate.on", "ONLY WHILE LIVE is on — posts are held while the stream is offline. With no live detection wired up, Phoenix treats you as live.")
        : Localizer.T("panel.scheduling.live_gate.off", "ONLY WHILE LIVE is off — schedules post whenever the bot is connected.");

    public bool HasSchedules => Schedules.Count > 0;

    /// <summary>Roster header count. Live-column header text, not a stat tile.</summary>
    public string CountText
    {
        get
        {
            int n = Schedules.Count, on = 0;
            foreach (var row in Schedules) if (row.Enabled) on++;
            // InvariantCulture is kept from the hand-built form: both arguments used to
            // be formatted invariantly, so the rendered number is byte-identical.
            return string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("panel.scheduling.roster.count", "{0} total · {1} on"),
                n, on);
        }
    }

    // UI-thread only — callers are row edits (ScheduleSave), add/remove, and the
    // _ui.Post continuation of the runtime/config event handlers.
    private void RaiseStats()
    {
        Raise(nameof(CountText));
    }

    // ── Send-health hint (passive) ───────────────────────────────────────
    // A dropped post — Streamer.bot Chat Action name never set, link down, body over the
    // 500-char cap — never reaches chat and is deliberately NOT counted as a fire, so
    // without this line the page would just look idle for no visible reason. Read-only:
    // it never blocks an edit or a save. The service derives it from whichever schedules
    // are CURRENTLY failing, so it survives a healthy schedule posting alongside a broken
    // one and clears only once every schedule is landing again.
    public string SendFailureText
    {
        get
        {
            string reason = _svc.LastSendFailure;
            // The reason itself comes from the service verbatim and is NOT translated —
            // only the sentence around it carries a key.
            return reason.Length == 0
                ? ""
                : string.Format(
                    CultureInfo.InvariantCulture,
                    Localizer.T("panel.scheduling.send.failure", "Last send failed — {0}"),
                    reason);
        }
    }

    public Visibility SendFailureVisibility
        => _svc.LastSendFailure.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    // ── Status pill ──────────────────────────────────────────────────────
    // The state itself is decided service-side (SchedulingService.PillState) so the
    // panel projects ONE enum instead of recomputing a state machine from five
    // fields — and so the state machine is reachable from the test suite, which
    // cannot construct this ViewModel at all.
    //
    // Translating the PHRASES is safe here, unlike CustomCommandsViewModel's pill:
    // nothing string-matches this property. SchedulingView.ApplyHeaderState switches on
    // SchedulingService.PillState (the enum) directly, and RefreshPill below compares the
    // enum plus the failure reason — never the rendered words. The waiting-for-live arm
    // reuses the header band's key, whose English is the same sentence.
    public string StatusPillText => _pillState switch
    {
        SchedulingSvc.SchedulingPillState.Dormant => Localizer.T("panel.scheduling.pill.dormant", "dormant"),
        SchedulingSvc.SchedulingPillState.Failing => Localizer.T("panel.scheduling.pill.failing", "failing"),
        SchedulingSvc.SchedulingPillState.ArmedWaitingForLive => Localizer.T("panel.scheduling.state.waiting_live", "armed · waiting for live"),
        SchedulingSvc.SchedulingPillState.NothingConfigured => Localizer.T("panel.scheduling.pill.nothing_configured", "live · nothing configured"),
        _ => Localizer.T("panel.scheduling.pill.posting", "posting"),
    };

    // Re-reads the service state and raises ONLY when something moved. UI thread only.
    // The failure hint stays in the guard: the Failing PHRASE cannot carry the reason,
    // but a reason change still re-applies the header via the StatusPillText raise.
    private void RefreshPill()
    {
        if (_disposed) return;
        var state = _svc.PillState;
        string hint = _svc.LastSendFailure;
        if (state == _pillState && string.Equals(hint, _pillHint, StringComparison.Ordinal)) return;
        _pillState = state;
        _pillHint = hint;
        Raise(nameof(StatusPillText));
    }

    // ── Load / refresh runtime ───────────────────────────────────────────
    public Task LoadAsync()
    {
        if (_loaded) return Task.CompletedTask;
        _loaded = true;
        RefreshRuntime();   // OnLoaded runs on the UI thread — safe to touch rows/stats directly
        return Task.CompletedTask;
    }

    private void BuildRows(SchedulingConfig cfg)
    {
        // Keep the streamer where they were: a foreign ConfigChanged rebuilds every row
        // object, so re-point the selection at the SAME schedule by its stable Id.
        string previousId = _selected?.Id ?? "";

        _selected = null;
        Schedules.Clear();
        cfg.Schedules ??= new List<ScheduleItem>();
        foreach (var item in cfg.Schedules)
            Schedules.Add(new ScheduleRowVm(item, ScheduleSave));
        // Seed each row's fire count from the live service runtime state.
        foreach (var row in Schedules)
            row.SetFireCount(_svc.GetFireCount(row.Id));

        if (previousId.Length > 0)
        {
            foreach (var row in Schedules)
            {
                if (!string.Equals(row.Id, previousId, StringComparison.Ordinal)) continue;
                _selected = row;
                row.IsSelected = true;
                break;
            }
        }

        Raise(nameof(HasSchedules));
        Raise(nameof(SelectedSchedule));
        Raise(nameof(HasSelection));
        Raise(nameof(DetailVisibility));
        Raise(nameof(EmptyVisibility));
        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptySubtitle));
        RaiseStats();
    }

    // Pull the ephemeral fire counts (per row + the aggregate) and the send-health hint
    // from the service. UI-thread only (called from LoadAsync / the marshalled
    // RuntimeChanged handler — the service raises RuntimeChanged on BOTH outcomes of a
    // send, so the hint re-reads on the same beat whether the post landed or dropped).
    private void RefreshRuntime()
    {
        foreach (var row in Schedules)
            row.SetFireCount(_svc.GetFireCount(row.Id));
        RaiseStats();
        Raise(nameof(SendFailureText));
        Raise(nameof(SendFailureVisibility));
        RefreshPill();
    }

    // ── Add / remove schedules ───────────────────────────────────────────
    public void AddSchedule()
    {
        var item = new ScheduleItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "",
            Enabled = true,
            Message = "",
            IntervalMinutes = 15,
            MinChatLines = 0,
        };
        var row = new ScheduleRowVm(item, ScheduleSave);
        Schedules.Add(row);
        Raise(nameof(HasSchedules));
        // The editable fields live in the detail column now, so a freshly added
        // schedule MUST become the selection or the streamer adds a row and lands on
        // an empty pane with nothing to type in.
        SelectedSchedule = row;
        ScheduleSave();
    }

    public void RemoveSchedule(ScheduleRowVm row)
    {
        if (row is null) return;
        int index = Schedules.IndexOf(row);
        // The config is rebuilt from the live rows on every save, so dropping the row
        // from the collection is enough — UpdateConfigAsync prunes its runtime state.
        Schedules.Remove(row);
        if (ReferenceEquals(_selected, row))
        {
            // Land on the row that took its place (or the new last one) rather than
            // dumping the streamer into the empty pane after every delete.
            ScheduleRowVm? next = null;
            if (Schedules.Count > 0)
                next = Schedules[Math.Min(Math.Max(index, 0), Schedules.Count - 1)];
            SelectedSchedule = next;
        }
        Raise(nameof(HasSchedules));
        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptySubtitle));
        ScheduleSave();
    }

    /// <summary>Deletes whatever the detail column is showing. The strip's danger slot
    /// acts on the selection, because the roster row no longer carries its own DELETE.</summary>
    public void RemoveSelected()
    {
        if (_selected is { } row) RemoveSchedule(row);
    }

    // ── Persistence ──────────────────────────────────────────────────────
    private void ScheduleSave()
    {
        _dirty = true;
        // Every row edit funnels through here (row _changed) on the UI thread —
        // cheapest single hook to keep the roster count live (an Enabled flip
        // moves the "M on" half of CountText).
        RaiseStats();
        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptySubtitle));
        // An Enabled flip can move the pill between "nothing configured" and "posting".
        RefreshPill();
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Start(); }
        else _ = SaveWorkingAsync();
    }

    private void SaveNow()
    {
        _dirty = true;
        _saveTimer?.Stop();
        _ = SaveWorkingAsync();
    }

    private async Task SaveWorkingAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        // Build the fresh config on the current (UI) thread BEFORE awaiting so the
        // snapshot is taken from a stable, non-mutating view of the rows.
        var cfg = BuildConfig();
        _lastPushed = cfg;
        try { await _svc.UpdateConfigAsync(cfg).ConfigureAwait(false); }
        catch (Exception ex) { GlobalLogger.Error("SchedulingViewModel", "UpdateConfigAsync failed", ex); }
    }

    // The single place the UI's editable state is projected back into a config blob.
    private SchedulingConfig BuildConfig()
    {
        var cfg = new SchedulingConfig
        {
            Enabled = _masterEnabled,
            OnlyWhenLive = _onlyWhenLive,
            Schedules = new List<ScheduleItem>(Schedules.Count),
        };
        foreach (var row in Schedules)
            cfg.Schedules.Add(row.ToSnapshot());
        return cfg;
    }

    // ── Service events (SafeEvent — raised on a background thread; MUST marshal) ─
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Self-triggered save assigns our pushed instance as the live config → skip.
        if (ReferenceEquals(_svc.Config, _lastPushed)) return;
        _ui.Post(() =>
        {
            var cfg = Clone(_svc.Config);
            _masterEnabled = cfg.Enabled;
            _onlyWhenLive = cfg.OnlyWhenLive;
            BuildRows(cfg);
            Raise(nameof(MasterEnabled));
            Raise(nameof(OnlyWhenLive));
            Raise(nameof(LiveGateText));
            RefreshPill();
        });
    }

    private void OnRuntimeChanged(object? sender, EventArgs e) => _ui.Post(RefreshRuntime);

    // Deep clone through JSON round-trip (mirrors the Counters / Timer VMs) so the
    // working copy is fully detached from the live service config.
    private static SchedulingConfig Clone(SchedulingConfig src)
    {
        try
        {
            string json = JsonSerializer.Serialize(src ?? new SchedulingConfig());
            return JsonSerializer.Deserialize<SchedulingConfig>(json) ?? new SchedulingConfig();
        }
        catch { return new SchedulingConfig(); }
    }
}
