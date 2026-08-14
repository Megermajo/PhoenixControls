using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.PollsPanel;

/// <summary>
/// The Hub Polls page — a chat poll with an optional points side-bet, surfaced as a
/// page on the Pre-Builds rail via the chrome's Pre-Builds tab (see MainWindow.OpenToolTab
/// → the Pre-Builds rail). UserControl + ViewModel pattern matching the Song Request / Quotes /
/// Counters pages; IDisposable so the Pre-Builds rail disposes the VM (which flushes pending
/// edits, stops the countdown tick and unsubscribes from the PollsService events) when
/// the tab is closed.
///
/// There is deliberately NO page-load entrance animation here. the Pre-Builds rail already runs
/// FadeSlideIn over the whole view on EVERY tab activation, so the staggered per-card
/// entrance this file used to run was a second animation on top of the host's.
///
/// The page still hand-paints nothing, but it is no longer subscription-free: the 48 px
/// <c>ToolPageHeader</c> that replaced the action strip, the state band and the status pill
/// exposes its switch and its state phrase as PLAIN properties and a method, not
/// DependencyProperties — every consumer seeds them once from code-behind — so the two
/// values that do move at runtime (the master switch on a foreign config reload, and the
/// state phrase on every poll tick) are pushed from a PropertyChanged handler. It is
/// detached in Dispose; without that this view is rooted by its own view-model.
/// </summary>
public sealed partial class PollsView : UserControl, IDisposable
{
    public PollsViewModel ViewModel { get; }
    private bool _disposed;

    public PollsView(PollsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // The header band. Title and the master switch are identity + the one boolean;
        // the state phrase carries what the pill and the banner used to say between them.
        //
        // ★ No EnablePrimaryCta and no EnableRefresh here. START POLL is a brass CTA
        // already — it lives at the foot of the START A POLL form, where its IsEnabled
        // can stay bound to CanStart so the two-option floor is visible before the click —
        // and ToolShellStyles allows exactly ONE brass CTA per visible surface. There is
        // nothing to refresh: the poll is in-memory and a 1 Hz tick already reads it.
        PageHeader.Title = Localizer.T("panel.polls.header.title", "Polls");
        PageHeader.IsOn = ViewModel.MasterEnabled;   // seeding — raises no event
        PushHeaderState();
        PageHeader.MasterToggled += OnHeaderMasterToggled;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // ── Header band ─────────────────────────────────────────────────────
    // SetState is a method rather than a bindable property, so the phrase and its colour
    // tier are pushed on change. Both VM members are raised together by
    // RefreshStatusPill, and the call is idempotent, so either name does the whole job.
    private void PushHeaderState()
        => PageHeader.SetState(ViewModel.HeaderStateText, ViewModel.HeaderStateKind);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(PollsViewModel.HeaderStateText):
                case nameof(PollsViewModel.HeaderStateKind):
                    PushHeaderState();
                    break;
                // A foreign config reload (another window, an import) rebuilds the
                // settings and raises this; the switch has to follow it. IsOn seeds
                // without raising MasterToggled, so this cannot echo back as an edit.
                case nameof(PollsViewModel.MasterEnabled):
                    PageHeader.IsOn = ViewModel.MasterEnabled;
                    break;
            }
        }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "header state push failed", ex); }
    }

    // ── Master switch ───────────────────────────────────────────────────
    // The VM's setter saves immediately (SaveNow) rather than through the 400 ms debounce,
    // exactly as it did behind the deleted chip button.
    private void OnHeaderMasterToggled(object? sender, bool isOn)
    {
        try { ViewModel.MasterEnabled = isOn; }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "master toggle failed", ex); }
    }

    // ── Poll controls ───────────────────────────────────────────────────
    private async void OnStartPollClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.StartPollAsync(); }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "Start failed", ex); }
    }

    private async void OnClosePollClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ClosePollAsync(); }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "Close failed", ex); }
    }

    private async void OnCancelPollClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CancelPollAsync(); }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "Cancel failed", ex); }
    }

    // ── Converted checkboxes (TogglePill hosts) ─────────────────────────
    // Each of these was a CheckBox with a TwoWay IsChecked binding; the pill is display
    // only, so the flip lives here and the VM property still owns the guard, the coupled
    // raises and the save.
    private void OnToggleStartBettingClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.StartBetting = !ViewModel.StartBetting; }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "take-bets toggle failed", ex); }
    }

    private void OnToggleAllowVoteChangeClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.AllowVoteChange = !ViewModel.AllowVoteChange; }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "vote-change toggle failed", ex); }
    }

    private void OnToggleAnnounceInChatClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.AnnounceInChat = !ViewModel.AnnounceInChat; }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "announce toggle failed", ex); }
    }

    private void OnToggleBettingEnabledClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.BettingEnabled = !ViewModel.BettingEnabled; }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "betting toggle failed", ex); }
    }

    private void OnToggleMirrorToNativeClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.MirrorToNative = !ViewModel.MirrorToNative; }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "mirror toggle failed", ex); }
    }

    private void OnTogglePublishOverlayClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.PublishOverlay = !ViewModel.PublishOverlay; }
        catch (Exception ex) { GlobalLogger.Error("PollsView", "overlay toggle failed", ex); }
    }
}
