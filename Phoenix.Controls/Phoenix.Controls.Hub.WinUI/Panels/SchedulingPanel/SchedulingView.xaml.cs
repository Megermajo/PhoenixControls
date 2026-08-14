using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using SchedulingSvc = Phoenix.Controls.Hub.Core.SchedulingService;

namespace Phoenix.Controls.Hub.WinUI.Panels.SchedulingPanel;

/// <summary>
/// The Hub Scheduling page — StreamElements-style recurring timed chat messages,
/// surfaced as a page on the Pre-Builds rail via the chrome's Pre-Builds tab (see
/// MainWindow.CreateToolPage → PreBuildsHostView). UserControl + ViewModel pattern matching
/// the Counters / Timer / Loyalty pages; IDisposable so the Pre-Builds rail disposes the VM
/// (which flushes pending edits and unsubscribes from the SchedulingService events) when
/// the tab is closed.
///
/// NO PAGE-LOAD ENTRANCE ANIMATION. the Pre-Builds rail already runs FadeSlideIn on the whole
/// view on EVERY tab activation, so the staggered per-card entrance this page used to
/// run was a second animation on top of the host's — removed, not ported.
/// </summary>
public sealed partial class SchedulingView : UserControl, IDisposable
{
    public SchedulingViewModel ViewModel { get; }
    private bool _disposed;

    public SchedulingView(SchedulingViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // ── Header band ──────────────────────────────────────────────────
        // The 48 px band replaces what used to be three stacked rows: the action
        // strip, the pinned state-sentence band and the ToolStatusPill. Wired here,
        // where the strip's controls used to be wired.
        //
        // IsOn is SEEDING — ToolPageHeader suppresses MasterToggled for it, so
        // restoring the saved state cannot look like a user gesture and write back
        // a change the streamer never made.
        PageHeader.Title = Localizer.T("panel.scheduling.header.title", "Scheduling");
        PageHeader.IsOn = ViewModel.MasterEnabled;
        PageHeader.EnablePrimaryCta(Localizer.T("panel.scheduling.header.add_cta", "+ ADD SCHEDULE"));
        PageHeader.MasterToggled += OnHeaderMasterToggled;
        PageHeader.PrimaryCtaInvoked += OnHeaderPrimaryCta;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyHeaderState();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("SchedulingView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        PageHeader.PrimaryCtaInvoked -= OnHeaderPrimaryCta;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // ── Header band ─────────────────────────────────────────────────────
    // The master switch routes through the VM's SaveNow path (no 400 ms debounce
    // on a switch), exactly as the strip's chip did.
    private void OnHeaderMasterToggled(object? sender, bool isOn) => ViewModel.MasterEnabled = isOn;

    private void OnHeaderPrimaryCta(object? sender, EventArgs e) => ViewModel.AddSchedule();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Both raises reach us on the UI thread: RefreshPill is UI-thread-only and
        // the service events are marshalled through the VM's dispatcher pump.
        switch (e.PropertyName)
        {
            case nameof(SchedulingViewModel.MasterEnabled):
                PageHeader.IsOn = ViewModel.MasterEnabled;   // seeding — raises nothing
                ApplyHeaderState();
                break;
            case nameof(SchedulingViewModel.StatusPillText):
                ApplyHeaderState();
                break;
        }
    }

    /// <summary>
    /// Renders the header's one state phrase from the SAME predicate the status pill
    /// used — <c>SchedulingService.PillState</c>, the service-side state machine that
    /// <c>PreBuildStatusPillTests</c> owns. Nothing about that machine changes here:
    /// this is a rendering map (enum → phrase + foreground tier), and the VM still
    /// projects the pill text for any watch surface that wants the chip.
    ///
    /// The phrase carries state AND consequence, because "enabled" alone never told
    /// anyone why nothing had posted: the gate can be armed and waiting for the
    /// stream to go live, or on with nothing configured, and both look identical to
    /// a boolean.
    /// </summary>
    private void ApplyHeaderState()
    {
        (string phrase, ToolStateKind kind) = SchedulingSvc.Instance.PillState switch
        {
            SchedulingSvc.SchedulingPillState.Dormant             => (Localizer.T("panel.scheduling.state.off", "off"), ToolStateKind.Dormant),
            SchedulingSvc.SchedulingPillState.Failing             => (Localizer.T("panel.scheduling.state.failing", "error · sends failing"), ToolStateKind.Error),
            SchedulingSvc.SchedulingPillState.ArmedWaitingForLive => (Localizer.T("panel.scheduling.state.waiting_live", "armed · waiting for live"), ToolStateKind.Dormant),
            SchedulingSvc.SchedulingPillState.NothingConfigured   => (Localizer.T("panel.scheduling.state.no_schedules", "on · no schedules"), ToolStateKind.Dormant),
            _                                                     => (Localizer.T("panel.scheduling.state.posting", "live · posting"), ToolStateKind.Live),
        };
        PageHeader.SetState(phrase, kind);
    }

    // ── Page-level gate ─────────────────────────────────────────────────
    // TogglePill is display-only, so the host Button owns the flip. Routes through
    // the VM's SaveNow path (no 400 ms debounce on a switch). The chip itself moved
    // from the action strip into the detail column's POSTING card when the strip was
    // replaced by the header band — the header hosts exactly one switch, the master,
    // and ONLY WHILE LIVE is a setting, not a status.
    private void OnToggleOnlyWhenLiveClick(object sender, RoutedEventArgs e)
        => ViewModel.OnlyWhenLive = !ViewModel.OnlyWhenLive;

    /// <summary>Deletes the schedule the detail column is showing. Came down from the
    /// action strip's danger slot to sit under the editor it acts on; still disabled
    /// while nothing is selected.</summary>
    private void OnDeleteScheduleClick(object sender, RoutedEventArgs e) => ViewModel.RemoveSelected();

    // ── Roster rows ─────────────────────────────────────────────────────
    // ItemsRepeater has no selection model: the picked row is the ViewModel's state
    // and the row reports its own DataContext on click.
    private void OnSelectScheduleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleRowVm row })
            ViewModel.SelectedSchedule = row;
    }

    private void OnRowEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleRowVm row })
            row.Enabled = !row.Enabled;
    }
}
