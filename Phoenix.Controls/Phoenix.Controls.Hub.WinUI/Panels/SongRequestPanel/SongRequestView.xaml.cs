using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using SongSvc = Phoenix.Controls.Hub.Core.SongRequestService;

namespace Phoenix.Controls.Hub.WinUI.Panels.SongRequestPanel;

/// <summary>
/// The Hub Song Requests page — a YouTube request queue surfaced as a page on the Pre-Builds rail
/// via the chrome's Pre-Builds tab (see MainWindow.CreateToolPage → PreBuildsHostView).
/// UserControl + ViewModel pattern matching the Quotes / Counters / Loyalty pages;
/// IDisposable so the Pre-Builds rail disposes the VM (which flushes pending config edits
/// and unsubscribes from the SongRequestService events) when the tab is closed.
///
/// NO PAGE-LOAD ENTRANCE ANIMATION. the Pre-Builds rail already runs FadeSlideIn on the whole
/// view on EVERY tab activation, so the staggered per-card entrance this page used to run
/// was a second animation on top of the host's — removed, not ported.
/// </summary>
public sealed partial class SongRequestView : UserControl, IDisposable
{
    public SongRequestViewModel ViewModel { get; }
    private bool _disposed;

    public SongRequestView(SongRequestViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // ── The 48 px header band ───────────────────────────────────────
        // Wired here, where the action strip's master chip, its PLAY CTA and its REFRESH
        // button used to be wired. CLEAR is unchanged and still a XAML Click handler —
        // it moved to the queue column's header, not into this band, because it empties
        // the queue rather than acting on the tool.
        PageHeader.Title = Localizer.T("panel.songrequest.header.title", "Song Requests");
        PageHeader.IsOn = ViewModel.MasterEnabled;   // seeds only — raises no MasterToggled
        PageHeader.EnableRefresh(Localizer.T("panel.songrequest.header.refresh.tip",
            "Re-read the queue, the player state and the API-key / Loyalty hints"));
        PageHeader.EnablePrimaryCta(Localizer.T("panel.songrequest.header.play_cta", "PLAY"));
        PageHeader.MasterToggled += OnMasterToggled;
        PageHeader.RefreshRequested += OnRefreshRequested;
        PageHeader.PrimaryCtaInvoked += OnPlayInvoked;
        SyncHeaderState();

        // The band's phrase follows the SAME state the pill followed, so it has to
        // repaint on the same notifications the pill's bindings did.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    // ── Header state ────────────────────────────────────────────────────
    /// <summary>
    /// Projects the tool's ONE state onto the band's one lowercase phrase.
    ///
    /// <para>The predicate is untouched: <c>SongRequestService.ComputePillState</c> is
    /// still the only thing that decides what this tool's state IS, and
    /// <c>PreBuildStatusPillTests</c> still guards it — including the deliberate ranking
    /// that puts <c>QueueingNothingRenders</c> ABOVE the transport states, which is what
    /// stops this page ever saying "playing" while not a single browser surface is
    /// attached to any layer. Only the render site changed: the same enum used to reach
    /// a ToolStatusPill on the action strip and now reaches the header band.</para>
    ///
    /// <para>Read from the service enum rather than switched on the ViewModel's pill
    /// LABEL: a string switch over "queueing · nothing will play" would fall silently to
    /// the default arm the day that wording is edited, and a wrong state phrase is
    /// exactly the dishonesty the pill audit existed to prevent.</para>
    ///
    /// <para>Three tiers where the pill had five colours, so the collapse is stated
    /// plainly: green ONLY while a track is actually running; red for the one state that
    /// is a genuine fault (on, queueing, nothing can render); grey for off, idle and
    /// held — all three of which are honest, working states with the queue open.</para>
    /// </summary>
    private void SyncHeaderState()
    {
        SongSvc.SongRequestPillState state;
        try { state = SongSvc.Instance.PillState; }
        catch (Exception ex)
        {
            GlobalLogger.Error("SongRequestView", "pill state read failed", ex);
            return;   // leave the last honest phrase standing rather than guess a new one
        }

        (string phrase, ToolStateKind kind) = state switch
        {
            SongSvc.SongRequestPillState.Dormant => (Localizer.T("panel.songrequest.state.off", "off"), ToolStateKind.Dormant),
            SongSvc.SongRequestPillState.QueueingNothingRenders => (Localizer.T("panel.songrequest.state.nothing_plays", "queueing · nothing plays"), ToolStateKind.Error),
            SongSvc.SongRequestPillState.Playing => (Localizer.T("panel.songrequest.state.playing", "live · playing"), ToolStateKind.Live),
            SongSvc.SongRequestPillState.Paused => (Localizer.T("panel.songrequest.state.paused", "paused · queue open"), ToolStateKind.Dormant),
            _ => (Localizer.T("panel.songrequest.state.idle", "idle · queue open"), ToolStateKind.Dormant),
        };
        PageHeader.SetState(phrase, kind);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SongRequestViewModel.MasterEnabled):
                PageHeader.IsOn = ViewModel.MasterEnabled;   // no-op when already in step
                SyncHeaderState();
                break;
            case nameof(SongRequestViewModel.StatusPillText):
                SyncHeaderState();
                break;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        PageHeader.MasterToggled -= OnMasterToggled;
        PageHeader.RefreshRequested -= OnRefreshRequested;
        PageHeader.PrimaryCtaInvoked -= OnPlayInvoked;
        ViewModel.Dispose();
    }

    // ── Header band verbs ───────────────────────────────────────────────
    // The master switch still bypasses the 400 ms debounce (the VM's setter calls
    // SaveNow) exactly as it did behind the strip's TogglePill chip.
    private void OnMasterToggled(object? sender, bool isOn)
        => ViewModel.MasterEnabled = isOn;

    private void OnRefreshRequested(object? sender, EventArgs e)
    {
        try { ViewModel.Refresh(); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "Refresh failed", ex); }
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ClearQueueAsync(); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "Clear failed", ex); }
    }

    // ── Settings ────────────────────────────────────────────────────────
    private void OnToggleRequireApprovalClick(object sender, RoutedEventArgs e)
        => ViewModel.RequireApproval = !ViewModel.RequireApproval;

    // ── Transport ───────────────────────────────────────────────────────
    // PLAY is the band's single brass CTA rather than a third button beside PAUSE / SKIP:
    // it starts the QUEUE, not the track the PLAYER card names, and it has to stay
    // reachable while the SETTINGS section is showing.
    private async void OnPlayInvoked(object? sender, EventArgs e)
    {
        try { await ViewModel.PlayAsync(); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "Play failed", ex); }
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.PausePlayer(); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "Pause failed", ex); }
    }

    private async void OnSkipClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SkipAsync(); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "Skip failed", ex); }
    }

    // ── Per-row actions ─────────────────────────────────────────────────
    // Every one addresses the entry by its stable Id (SongRequestRowVm.Id), never by
    // position: a chat request landing between the read and the click shifts positions.
    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SongRequestRowVm row }) return;
        try { await ViewModel.RemoveAsync(row); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "Remove failed", ex); }
    }

    private void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SongRequestRowVm row }) return;
        try { ViewModel.ApproveRow(row); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "Approve failed", ex); }
    }

    private async void OnDenyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SongRequestRowVm row }) return;
        try { await ViewModel.DenyAsync(row); }
        catch (Exception ex) { GlobalLogger.Error("SongRequestView", "Deny failed", ex); }
    }
}
