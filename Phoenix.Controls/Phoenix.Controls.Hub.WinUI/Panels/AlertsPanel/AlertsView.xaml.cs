using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.AlertsPanel;

/// <summary>
/// The Hub Alerts page — StreamElements-style event responses (ten families:
/// Follow / Sub / Gift Sub / Gift Bomb / Bits / Raid / Charity / Shoutout
/// Received / Watch Streak / Tip, each with size tiers), surfaced as a closable
/// in-Hub tab. UserControl + ViewModel pattern matching the Counters / Timer /
/// Giveaway pages; IDisposable so the tab host disposes the VM (which flushes
/// pending edits and unsubscribes from the AlertsService and Streamer.bot
/// connection events) when the tab is closed.
///
/// Since the master-detail rebuild the page is a 1.4* detail column driven by a
/// 0.9* family roster. Two consequences for this file:
///
///   * The roster row's Click sets ViewModel.SelectedEvent from the sender's
///     DataContext — ItemsRepeater has no selection model of its own, so the
///     click IS the selection. The row's enable pill is a sibling Button, so
///     switching a family on never moves the selection.
///   * Handlers for the detail column act on ViewModel.SelectedEvent rather than
///     on a row resolved from the sender, because that column is a plain subtree
///     bound through SelectedEvent, not a DataTemplate. The TIER handlers still
///     resolve their row from the sender — those controls really are inside a
///     DataTemplate.
///
/// GONE ON PURPOSE, and both are removals rather than regressions:
///   * the EventCard / EventBody visual-tree walk and its expand animation — the
///     collapsible card it opened no longer exists (its body is the detail
///     column now), so the walk had nothing left to find;
///   * the page-side entrance animation — the Pre-Builds rail's FadeSlideIn already runs
///     on every tab activation, so the page was double-animating.
///
/// <para><b>The header band is the one thing this file drives by hand.</b>
/// Everything else on the page still paints itself from an x:Bind — TogglePill
/// reads its dependency properties, the detail column reads SelectedEvent. But
/// <see cref="ToolPageHeader"/> is deliberately plain properties, not
/// DependencyProperties (write-once from the host), so the view subscribes to
/// <c>ViewModel.PropertyChanged</c> and pushes the two live values into it:
/// the master switch position and the state phrase. That subscription is the
/// ONLY reason this view listens to the VM at all, and its -= is in
/// <see cref="Dispose"/>.</para>
///
/// <para>The state phrase is not a new predicate. It is
/// <c>AlertsViewModel.HeaderStateText</c> / <c>HeaderStateKind</c>, which
/// project the same <c>AlertsService.PillState</c> the status pill projected —
/// the pill's four-state machine, its honesty audit and PreBuildStatusPillTests
/// are all untouched. Only the render site moved.</para>
/// </summary>
public sealed partial class AlertsView : UserControl, IDisposable
{
    public AlertsViewModel ViewModel { get; }
    private bool _disposed;

    public AlertsView(AlertsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // ── Header band (replaces the action strip + state band + status pill) ──
        // Seeded here, where the three deleted controls used to be bound. IsOn is
        // a seed, not a gesture: ToolPageHeader suppresses MasterToggled while the
        // host writes it, so restoring config state cannot save a change the
        // streamer never made.
        PageHeader.Title = Localizer.T("panel.alerts.header.title", "Alerts");
        PageHeader.IsOn = ViewModel.MasterEnabled;
        // The page's ONE brass verb, unchanged in behaviour: it adds a tier to the
        // SELECTED family. No refresh verb — this page's only ↻ is the per-tier
        // one, which needs its row's DataContext and cannot be hoisted.
        PageHeader.EnablePrimaryCta(Localizer.T("panel.alerts.header.add_cta", "+ ADD TIER"));
        PageHeader.MasterToggled += OnHeaderMasterToggled;
        PageHeader.PrimaryCtaInvoked += OnAddTierInvoked;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncHeaderState();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("AlertsView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        PageHeader.PrimaryCtaInvoked -= OnAddTierInvoked;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // ── Header band ─────────────────────────────────────────────────────
    // The switch flip writes MasterEnabled, whose setter uses SaveNow and so
    // bypasses the 400 ms debounce — exactly what the old chip Button did.
    private void OnHeaderMasterToggled(object? sender, bool isOn)
        => ViewModel.MasterEnabled = isOn;

    // ADD TIER, formerly the action strip's brass button. AlertEventVm.AddTier
    // still auto-computes MinValue = highest + 1 with its int.MaxValue guard, and
    // still re-runs the reachability pass.
    //
    // The old button carried IsEnabled="{x:Bind ViewModel.HasSelection}";
    // ToolPageHeader's CTA has no such hook, so the guard is the null-conditional
    // below instead. Not a behaviour change a streamer can reach: the roster is
    // TEN FIXED families and BuildCards always lands on one, which is why the
    // page's own empty state is documented as unreachable in practice.
    private void OnAddTierInvoked(object? sender, EventArgs e)
        => ViewModel.SelectedEvent?.AddTier();

    // Pushes the VM's two live header values into the band. MasterEnabled moves
    // on a foreign ConfigChanged (a second window editing the same config);
    // HeaderStateText moves on that, on RuntimeChanged, and on a Streamer.bot
    // connect/disconnect — the same three sources that used to repaint the pill.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        switch (e.PropertyName)
        {
            case nameof(AlertsViewModel.MasterEnabled):
                PageHeader.IsOn = ViewModel.MasterEnabled;
                SyncHeaderState();
                break;
            case nameof(AlertsViewModel.HeaderStateText):
                SyncHeaderState();
                break;
        }
    }

    private void SyncHeaderState()
        => PageHeader.SetState(ViewModel.HeaderStateText, ViewModel.HeaderStateKind);

    // ── Roster ──────────────────────────────────────────────────────────
    private void OnEventRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlertEventVm card })
            ViewModel.SelectedEvent = card;
    }

    // Serves BOTH pills bound to the same boolean: the roster row's (DataContext
    // is that row's card) and the detail column's (no DataContext, so fall back
    // to the selection). One handler, because both write the same property and
    // AlertEventVm.Enabled saves immediately either way.
    private void OnToggleEventEnabledClick(object sender, RoutedEventArgs e)
    {
        var card = sender is FrameworkElement { DataContext: AlertEventVm row }
            ? row
            : ViewModel.SelectedEvent;
        if (card is not null) card.Enabled = !card.Enabled;
    }

    // ── Detail: the raid-only auto-shoutout ─────────────────────────────
    private void OnToggleAutoShoutoutClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedEvent is { } card) card.AutoShoutout = !card.AutoShoutout;
    }

    // ── Detail: tier rows (inside a DataTemplate — resolve from the sender) ──
    private void OnToggleTierEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlertTierRowVm row })
            row.Enabled = !row.Enabled;
    }

    private void OnDeleteTierClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlertTierRowVm row })
            row.Delete();
    }

    // ── Layer / trigger pickers (lazy population) ───────────────────────
    // Both refreshes no-op when the resolved list is unchanged, specifically so
    // an open drop-down is not churned.
    private void OnRefreshLayersClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshLayers();
        if (sender is FrameworkElement { DataContext: AlertTierRowVm row })
            row.RefreshTriggerOptions();
    }

    private void OnLayerComboOpened(object sender, object e) => ViewModel.RefreshLayers();

    private void OnTriggerComboOpened(object sender, object e)
    {
        if (sender is FrameworkElement { DataContext: AlertTierRowVm row })
            row.RefreshTriggerOptions();
    }
}
