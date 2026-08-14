using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using RanksSvc = Phoenix.Controls.Hub.Core.RanksService;

namespace Phoenix.Controls.Hub.WinUI.Panels.RanksPanel;

/// <summary>
/// The Hub Ranks page — a watch-time / points rank ladder, surfaced as a closable in-Hub
/// tab via the chrome's Pre-Builds tab (see MainWindow.CreateToolPage → PreBuildsHostView).
/// UserControl + ViewModel pattern matching the Polls / Alerts / Counters pages;
/// IDisposable so the Pre-Builds rail disposes the VM (which flushes pending edits and
/// unsubscribes from the RanksService events) when the tab is closed.
///
/// There is deliberately NO page-load entrance animation here. the Pre-Builds rail already runs
/// FadeSlideIn over the whole view on EVERY tab activation, so the staggered per-card
/// entrance this file used to run was a second animation on top of the host's.
/// </summary>
public sealed partial class RanksView : UserControl, IDisposable
{
    public RanksViewModel ViewModel { get; }
    private bool _disposed;

    public RanksView(RanksViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // ── The header band, wired where the action strip used to be ──
        // Seed first, subscribe second. ToolPageHeader.IsOn already suppresses
        // MasterToggled while it writes, so this order is belt-and-braces rather than
        // load-bearing — but a seed that reads back as a user gesture would save a
        // change the streamer never made, and that is worth two lines of ordering.
        PageHeader.Title = Localizer.T("panel.ranks.header.title", "Ranks");
        PageHeader.IsOn = ViewModel.MasterEnabled;
        PageHeader.EnableRefresh(Localizer.T("panel.ranks.header.refresh.tip", "Refresh the user group list"));
        PageHeader.EnablePrimaryCta(Localizer.T("panel.ranks.header.add_cta", "+ ADD RANK"));
        ApplyHeaderState();

        PageHeader.MasterToggled += OnHeaderMasterToggled;
        PageHeader.RefreshRequested += OnHeaderRefreshRequested;
        PageHeader.PrimaryCtaInvoked += OnHeaderPrimaryCtaInvoked;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        PageHeader.RefreshRequested -= OnHeaderRefreshRequested;
        PageHeader.PrimaryCtaInvoked -= OnHeaderPrimaryCtaInvoked;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // ── Header band ─────────────────────────────────────────────────────
    // The band replaces the action strip, the state-sentence band and the status
    // pill. The VM's setter still owns the save (SaveNow, not the 400 ms debounce);
    // the switch is only the gesture.
    private void OnHeaderMasterToggled(object? sender, bool isOn)
    {
        try { ViewModel.MasterEnabled = isOn; }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "master toggle failed", ex); }
    }

    // Re-read the User-Management group list — the strip's GROUPS verb.
    private void OnHeaderRefreshRequested(object? sender, EventArgs e)
    {
        try { ViewModel.RefreshGroups(); }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "RefreshGroups failed", ex); }
    }

    // Adds a rung one step above the highest threshold on the ladder.
    private void OnHeaderPrimaryCtaInvoked(object? sender, EventArgs e)
    {
        try { ViewModel.AddRank(); }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "AddRank failed", ex); }
    }

    // The VM re-reads the service's pill state on its own 2 s timer and raises
    // StatusPillText only when the answer MOVED, so riding that raise costs nothing
    // and cannot miss a transition (every non-dormant state depends on a fact —
    // Loyalty, the socket, the suite-wide minute counter — that raises no event here).
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(RanksViewModel.MasterEnabled)
                                      or nameof(RanksViewModel.StatusPillText))) return;
        try { ApplyHeaderState(); }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "header state refresh failed", ex); }
    }

    /// <summary>
    /// Renders the header band's switch and state phrase from the SAME predicate the
    /// status pill used — <c>RanksService.PillState</c>, the pure state machine
    /// PreBuildStatusPillTests pins. Nothing here re-derives a state from config: the
    /// phrase is a rewording of the pill's own text into the band's grammar
    /// (lowercase, three words or fewer, state AND consequence), and a state the
    /// service does not report cannot appear.
    /// </summary>
    private void ApplyHeaderState()
    {
        PageHeader.IsOn = ViewModel.MasterEnabled;

        RanksSvc.RanksPillState state;
        try { state = RanksSvc.Instance.PillState; }
        catch (Exception ex)
        {
            // Same posture as the VM's own reader: leave the last honest phrase up
            // rather than replace it with a guess.
            GlobalLogger.Error("RanksView", "pill state read failed", ex);
            return;
        }

        // ★ "frozen · loyalty off" is a POINTS-METRIC state only — on watch time the
        // accrual is independent of the Loyalty toggle, and the service's machine
        // never returns it there. Do not broaden any of this wording.
        var (phrase, kind) = state switch
        {
            // On, but the thing that feeds the ladder is not running. Enabled and
            // unable to work is exactly ToolStateKind.Error.
            RanksSvc.RanksPillState.FrozenLoyaltyOff      => (Localizer.T("panel.ranks.state.frozen_loyalty_off", "frozen · loyalty off"), ToolStateKind.Error),
            RanksSvc.RanksPillState.FrozenStreamerBotDown => (Localizer.T("panel.ranks.state.frozen_bot_down", "frozen · bot down"), ToolStateKind.Error),
            RanksSvc.RanksPillState.FrozenCountingOff     => (Localizer.T("panel.ranks.state.frozen_counting_off", "frozen · counting off"), ToolStateKind.Error),
            // Configured and correct, held only by its own online-only gate. Idle by
            // design is not an error, so it stays on the dormant tier.
            RanksSvc.RanksPillState.ArmedWaitingForLive   => (Localizer.T("panel.ranks.state.armed_off_air", "armed · off air"), ToolStateKind.Dormant),
            RanksSvc.RanksPillState.Accruing              => (Localizer.T("panel.ranks.state.climbing", "live · climbing"), ToolStateKind.Live),
            _                                             => (Localizer.T("panel.ranks.state.off", "off"), ToolStateKind.Dormant),
        };
        PageHeader.SetState(phrase, kind);
    }

    // The remove button lives inside the row DataTemplate, so the row VM comes off the
    // button's DataContext — the same route every per-row action in the tool panels takes,
    // and the route that keeps working under ItemsRepeater (which has no selection model
    // and no ItemContainer to walk).
    private void OnRemoveRankClick(object sender, RoutedEventArgs e)
    {
        try { (((FrameworkElement)sender).DataContext as RankRowVm)?.Delete(); }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "RemoveRank failed", ex); }
    }

    // Per-rung enabled switch — same DataContext route as the remove button.
    private void OnToggleRankEnabledClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (((FrameworkElement)sender).DataContext is RankRowVm row)
                row.Enabled = !row.Enabled;
        }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "rank enabled toggle failed", ex); }
    }

    // The board's own refresh, which stays IN the live column beside the list it
    // re-reads — it is not the header's groups verb and the two must not be merged.
    private async void OnRefreshBoardClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshLeaderboardAsync(); }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "RefreshLeaderboard failed", ex); }
    }

    // ── Converted checkboxes (TogglePill hosts) ─────────────────────────
    // Each of these was a CheckBox with a TwoWay IsChecked binding; the pill is display
    // only, so the flip lives here and the VM property still owns the guard, the coupled
    // raises and the save.
    //
    // The two watch-time handlers that used to head this block are gone with their
    // toggles: recording is no longer a Ranks setting (it is AppConfig's, read by the
    // always-on ViewerPresenceService), and the card now states what it is doing rather
    // than setting it.
    private void OnToggleChatEnabledClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.ChatEnabled = !ViewModel.ChatEnabled; }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "chat toggle failed", ex); }
    }

    private void OnToggleAnnounceEnabledClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.AnnounceEnabled = !ViewModel.AnnounceEnabled; }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "announce toggle failed", ex); }
    }

    private void OnToggleOverlayEnabledClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.OverlayEnabled = !ViewModel.OverlayEnabled; }
        catch (Exception ex) { GlobalLogger.Error("RanksView", "overlay toggle failed", ex); }
    }
}
