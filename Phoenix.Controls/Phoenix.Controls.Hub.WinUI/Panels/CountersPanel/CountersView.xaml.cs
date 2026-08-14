using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using CountersSvc = Phoenix.Controls.Hub.Core.CountersService;

namespace Phoenix.Controls.Hub.WinUI.Panels.CountersPanel;

/// <summary>
/// The Hub Counters page — named tally counters surfaced as a closable in-Hub
/// tab via the chrome's Pre-Builds tab (see MainWindow.OpenToolTab →
/// the Pre-Builds rail). UserControl + ViewModel pattern matching the Timer / Giveaway
/// pages; IDisposable so the Pre-Builds rail disposes the VM (which flushes pending
/// edits and unsubscribes from the CountersService events) when the tab is
/// closed.
///
/// Since the master-detail rebuild the page is a star-width detail column driven
/// by a fixed 300 px roster. Two consequences for this file:
///
///   * The roster's row Click sets ViewModel.SelectedCounter from the sender's
///     DataContext — ItemsRepeater has no selection model of its own, so the
///     click IS the selection.
///   * Every other handler acts on ViewModel.SelectedCounter rather than on a
///     row resolved from the sender, because the detail column is a plain
///     subtree bound through SelectedCounter, not a DataTemplate.
///
/// No entrance animation: the Pre-Builds rail's FadeSlideIn already runs on every tab
/// activation, and the page-side entrance this file used to run was the second
/// of the two.
///
/// <para><b>The one PropertyChanged subscription.</b> Everything on this page
/// still repaints itself through x:Bind — except the header band.
/// <see cref="ToolPageHeader"/> is deliberately plain properties rather than
/// DependencyProperties (write-once labels), so its switch and state phrase are
/// pushed imperatively and have to be re-pushed when the ViewModel moves. That is
/// what <see cref="OnViewModelPropertyChanged"/> does. It is detached in
/// <see cref="Dispose"/> alongside the two header event subscriptions — the VM is
/// disposed on the next line either way, but a handler left attached to a VM that
/// is still being flushed can be re-entered during that flush, and the symmetry
/// is what makes that impossible to get wrong later.</para>
/// </summary>
public sealed partial class CountersView : UserControl, IDisposable
{
    public CountersViewModel ViewModel { get; }
    private bool _disposed;

    public CountersView(CountersViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // ── Header band ─────────────────────────────────────────────────
        // Wired where the old action strip's three controls were: the master
        // chip, the ADD COUNTER brass CTA and the status pill. The strip, the
        // pill and the pinned master-state banner in the left column are all
        // gone from the markup; this is where their behaviour re-lands.
        PageHeader.Title = Localizer.T("panel.counters.header.title", "Counters");
        PageHeader.EnablePrimaryCta(Localizer.T("panel.counters.header.add_cta", "+ ADD COUNTER"));
        // Seeding, NOT a user gesture — ToolPageHeader suppresses MasterToggled
        // for this write, so restoring config state cannot save a change the
        // streamer never made.
        PageHeader.IsOn = ViewModel.MasterEnabled;
        ApplyHeaderState();

        PageHeader.MasterToggled += OnHeaderMasterToggled;
        PageHeader.PrimaryCtaInvoked += OnHeaderPrimaryCta;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("CountersView", "LoadAsync failed", ex); }
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
    // The switch writes through the same VM property the old chip did, so the
    // flip still goes through SaveNow and bypasses the 400 ms debounce.
    private void OnHeaderMasterToggled(object? sender, bool isOn)
        => ViewModel.MasterEnabled = isOn;

    private void OnHeaderPrimaryCta(object? sender, EventArgs e) => ViewModel.AddCounter();

    /// <summary>
    /// Pushes the header's state phrase from the SAME predicate the status pill
    /// read — <c>CountersService.PillState</c>, the service-owned state machine
    /// that <c>PreBuildStatusPillTests</c> guards. Nothing about that machine
    /// changes here; only where its result renders. It has exactly two honest
    /// states (there is no error state to report: a counter that has not moved in
    /// an hour is perfectly healthy), so there is no <c>ToolStateKind.Error</c>
    /// branch to write.
    ///
    /// <para>Read from the service rather than decoded back out of the VM's pill
    /// STRINGS: a string match would silently fall through to "off" if that text
    /// were ever reworded, which is precisely the dishonest-green-light class of
    /// bug the pill audit fixed.</para>
    /// </summary>
    private void ApplyHeaderState()
    {
        bool live = CountersSvc.Instance.PillState
                    == CountersSvc.CountersPillState.CommandsAndOverlayLive;
        PageHeader.SetState(
            live
                ? Localizer.T("panel.counters.state.live", "live · commands + overlay")
                : Localizer.T("panel.counters.state.off", "off · databank writable"),
            live ? ToolStateKind.Live : ToolStateKind.Dormant);
    }

    // The header is pushed, not bound, so it needs the two signals the pill and
    // the chip used to get through x:Bind: MasterEnabled (also raised by a
    // FOREIGN config change, e.g. a second window) and the pill-state raise the
    // VM fires on both branches of OnConfigChanged.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CountersViewModel.MasterEnabled):
                // No feedback loop: ToolPageHeader.IsOn returns early when the
                // value already matches, and suppresses MasterToggled when it
                // does not.
                PageHeader.IsOn = ViewModel.MasterEnabled;
                ApplyHeaderState();
                break;
            case nameof(CountersViewModel.StatusPillText):
                ApplyHeaderState();
                break;
        }
    }

    // ── Detail: delete ──────────────────────────────────────────────────
    // Re-homed from the old action strip to the foot of the detail column; it acts
    // on the selection and the button is disabled without one. RemoveCounter still
    // performs BOTH halves of the delete — the config row and the backing databank
    // row — so a re-added counter starts at 0.
    private void OnDeleteCounterClick(object sender, RoutedEventArgs e)
        => ViewModel.RemoveCounter(ViewModel.SelectedCounter);

    // ── Roster ──────────────────────────────────────────────────────────
    private void OnCounterRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CounterRowVm row })
            ViewModel.SelectedCounter = row;
    }

    // ── Detail: booleans converted from CheckBox to TogglePill ──────────
    private void OnFloorAtZeroClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCounter is { } row) row.FloorAtZero = !row.FloorAtZero;
    }

    private void OnChatEnabledClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCounter is { } row) row.ChatEnabled = !row.ChatEnabled;
    }

    // ── Detail: live count controls ─────────────────────────────────────
    // Deliberately NOT gated on the master switch — the databank stays writable
    // while the tool is dormant, and these four are how the streamer reaches it.
    // Each silently no-ops on a blank counter name (VM guard), which is the
    // existing behaviour.
    private async void OnSetClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SetCountAsync(ViewModel.SelectedCounter); }
        catch (Exception ex) { GlobalLogger.Error("CountersView", "Set failed", ex); }
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ResetCountAsync(ViewModel.SelectedCounter); }
        catch (Exception ex) { GlobalLogger.Error("CountersView", "Reset failed", ex); }
    }

    private async void OnStepUpClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.StepCountAsync(ViewModel.SelectedCounter, +1); }
        catch (Exception ex) { GlobalLogger.Error("CountersView", "Step up failed", ex); }
    }

    private async void OnStepDownClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.StepCountAsync(ViewModel.SelectedCounter, -1); }
        catch (Exception ex) { GlobalLogger.Error("CountersView", "Step down failed", ex); }
    }
}
