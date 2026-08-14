using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.AutomodPanel;

/// <summary>
/// The Hub Automod page — the spam-filter settings surface, opened as a
/// page on the Pre-Builds rail via the chrome's Pre-Builds tab (see
/// MainWindow.CreateToolPage → PreBuildsHostView). UserControl + ViewModel pattern
/// matching the Counters / Loyalty pages; IDisposable so the Pre-Builds rail disposes
/// the VM (which flushes pending edits and unsubscribes from the AutomodService
/// events) when the tab is closed.
///
/// Layout is the house tool shell: a 48 px <see cref="ToolPageHeader"/> band over a
/// * / 300 body. The left column carries the four config sections behind a 40 px
/// text nav; the right column carries the moderation log and the Pardon control,
/// ungated.
/// </summary>
public sealed partial class AutomodView : UserControl, IDisposable
{
    public AutomodViewModel ViewModel { get; }
    private bool _disposed;

    public AutomodView(AutomodViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // The header band is seeded and wired here, where the retired action strip's
        // master chip and status pill used to bind. Title first, then IsOn (a plain
        // setter that deliberately does NOT raise MasterToggled, so seeding cannot
        // look like a user gesture), then the state phrase.
        PageHeader.Title = Localizer.T("panel.automod.header.title", "Automod");
        PageHeader.IsOn = ViewModel.MasterEnabled;
        ApplyHeaderState();

        PageHeader.MasterToggled += OnHeaderMasterToggled;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("AutomodView", "LoadAsync failed", ex); }
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
    //
    // ★ THE PREDICATE IS THE STATUS PILL'S OWN, REPRODUCED, NOT REPLACED.
    //
    // AutomodViewModel.StatusPillText branches in this exact order — not enabled →
    // dry-run → Streamer.bot down → filtering — and that order is deliberate: dry-run
    // outranks the connection state because dry-run dispatches nothing at all, so a
    // downed Streamer.bot changes nothing about what the tool does. Those four states
    // (and the three that were REFUSED as dishonest) are the honesty audit;
    // ToolStatusPill, the VM's four predicates and PreBuildStatusPillTests are all
    // untouched by this conversion.
    //
    // The chain below reads only PUBLIC VM members and reproduces that branch order
    // exactly. StatusPulsing is literally `Enabled && !DryRun && SbConnected`, so by
    // the time the first two branches have been taken, a false StatusPulsing can only
    // mean the socket is down. No new predicate is introduced here, and nothing
    // service-side was touched: only WHERE the answer renders changed.
    //
    // Phrasing rule for the band: lowercase, three words or fewer, state AND
    // consequence. The pill's own sentences are longer because a pill has no title
    // beside it to lean on.
    private void ApplyHeaderState()
    {
        if (!ViewModel.MasterEnabled)
            PageHeader.SetState(Localizer.T("panel.automod.state.off", "off · nothing scanned"), ToolStateKind.Dormant);
        else if (ViewModel.DryRun)
            PageHeader.SetState(Localizer.T("panel.automod.state.observing", "observing · no action"), ToolStateKind.Dormant);
        else if (ViewModel.StatusPulsing)
            PageHeader.SetState(Localizer.T("panel.automod.state.filtering", "live · filtering"), ToolStateKind.Live);
        else
            PageHeader.SetState(Localizer.T("panel.automod.state.no_bot_link", "error · no bot link"), ToolStateKind.Error);
    }

    private void OnHeaderMasterToggled(object? sender, bool isOn)
        => ViewModel.MasterEnabled = isOn;

    // StatusPillText / StatusPillColor / StatusPulsing are raised together by the VM's
    // RaiseStatusPill, which every state-changing path funnels through (the master
    // switch, the dry-run toggle, the Streamer.bot socket callback, a foreign
    // ConfigChanged). Listening to any one of them plus MasterEnabled — which drives
    // the switch's own position — covers all four.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        switch (e.PropertyName)
        {
            case nameof(AutomodViewModel.MasterEnabled):
                PageHeader.IsOn = ViewModel.MasterEnabled;   // seeding setter; raises nothing
                ApplyHeaderState();
                break;
            case nameof(AutomodViewModel.DryRun):
            case nameof(AutomodViewModel.StatusPillText):
            case nameof(AutomodViewModel.StatusPulsing):
                ApplyHeaderState();
                break;
        }
    }

    // ── Motion ──────────────────────────────────────────────────────────
    // There is deliberately NO page-side entrance animation. The old per-section
    // FadeSlideIn existed only because a Pivot realizes its tab bodies lazily, so
    // a body appearing mid-session had no entrance of its own. With the Pivot gone
    // the four sections are all in the tree from the first realization, and
    // the Pre-Builds rail already runs FadeSlideIn on the whole view on every tool
    // activation — re-adding one here would double-animate the page.

    // ── General section toggles ─────────────────────────────────────────
    private void OnScopeTwitchClick(object sender, RoutedEventArgs e)
        => ViewModel.ScopeTwitch = !ViewModel.ScopeTwitch;

    private void OnScopeYouTubeClick(object sender, RoutedEventArgs e)
        => ViewModel.ScopeYouTube = !ViewModel.ScopeYouTube;

    private void OnScopeKickClick(object sender, RoutedEventArgs e)
        => ViewModel.ScopeKick = !ViewModel.ScopeKick;

    private void OnDryRunClick(object sender, RoutedEventArgs e)
        => ViewModel.DryRun = !ViewModel.DryRun;

    private void OnSuppressDispatchClick(object sender, RoutedEventArgs e)
        => ViewModel.SuppressDispatchWhenModerated = !ViewModel.SuppressDispatchWhenModerated;

    // ── Rules section per-row toggles ───────────────────────────────────
    // The house row idiom: pattern-match the realized element's DataContext.
    // ItemsRepeater has no selection model and no ItemContainer, so the row VM is
    // only ever reached this way — never through a container lookup.
    private void OnRuleEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutomodRuleRowVm row })
            row.Enabled = !row.Enabled;
    }

    private void OnRuleToggleValueClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutomodRuleRowVm row })
            row.ToggleValue = !row.ToggleValue;
    }

    private void OnRuleUseLadderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutomodRuleRowVm row })
            row.UseLadder = !row.UseLadder;
    }

    // Puts one rule back on the section's default action (the escalation ladder).
    // Routed through the row VM so the save schedule stays in one place.
    private void OnRuleResetActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutomodRuleRowVm row })
            row.ResetActionToDefault();
    }

    // ── Rules accordion ─────────────────────────────────────────────────
    //
    // One open at a time, and the PAGE owns that because only the page can see the
    // siblings — the row VM cannot close a peer it has no reference to. Closing the
    // others first also keeps the section's height stable: two open expansions would
    // put the eight rules back over a screen, which is the whole thing this replaced.
    //
    // Toggling a row that is already open just closes it, so the gesture is
    // symmetrical and there is always a state with nothing expanded.
    private void OnRuleFoldClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutomodRuleRowVm row }) return;

        bool open = !row.IsExpanded;
        foreach (var other in ViewModel.Rules)
            if (!ReferenceEquals(other, row)) other.IsExpanded = false;
        row.IsExpanded = open;
    }

    // ── Ladder ──────────────────────────────────────────────────────────
    private void OnAddLadderStepClick(object sender, RoutedEventArgs e) => ViewModel.AddLadderStep();

    private void OnRemoveLadderStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutomodLadderStepVm row })
            ViewModel.RemoveLadderStep(row);
    }

    // ── Moderation column ───────────────────────────────────────────────
    // The one modal on this page — sanctioned because a pardon irreversibly wipes a
    // chatter's whole escalation history (the sibling panels confirm their destructive
    // actions the same way). Defaults to Cancel; an empty name never opens it, so this
    // can't degrade into a repeatable rejection dialog.
    //
    // ⚠ RequestedTheme="Dark" is set because a ContentDialog without it renders
    // light-grey Fluent chrome even under the app-wide dark lock. That was missing
    // here before the rebuild; it is flagged in the delivery report rather than
    // folded in silently.
    private async void OnPardonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = (ViewModel.PardonName ?? "").Trim();
            if (name.Length == 0) return;

            var dialog = new ContentDialog
            {
                Title = Localizer.T("panel.automod.pardon.dialog.title", "Pardon this user?"),
                Content = string.Format(
                    Localizer.T("panel.automod.pardon.dialog.body",
                        "Clears every strike {0} has. Their next violation starts at rung 1."),
                    name),
                PrimaryButtonText = Localizer.T("panel.automod.pardon.dialog.primary", "Pardon"),
                CloseButtonText = Localizer.T("common.cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ElementTheme.Dark,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.PardonAsync();
        }
        catch (Exception ex) { GlobalLogger.Error("AutomodView", "Pardon failed", ex); }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshAsync(); }
        catch (Exception ex) { GlobalLogger.Error("AutomodView", "Refresh failed", ex); }
    }
}
