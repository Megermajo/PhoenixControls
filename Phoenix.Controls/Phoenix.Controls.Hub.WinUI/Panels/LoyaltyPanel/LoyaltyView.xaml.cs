using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Animation;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// The Hub Loyalty page — the viewer points economy surfaced as a closable
/// in-Hub tab via the chrome's Pre-Builds tab (see MainWindow.OpenToolTab
/// → the Pre-Builds rail). UserControl + ViewModel pattern matching the Timer /
/// Giveaway pages; IDisposable so the Pre-Builds rail disposes the VM (which flushes
/// pending edits and unsubscribes from the LoyaltyService and Streamer.bot-link
/// events) when the tab is closed.
///
/// Since the master-detail rebuild the page is a star-width configuration
/// column — the six retired Pivot tabs behind a section nav — beside a fixed
/// 300 px live column carrying BALANCES / LEDGER / LEADERBOARD. Two
/// consequences for this file:
///
///   * Booleans render as display-only TogglePills, so every former CheckBox is
///     now a Button whose Click flips the bound field. There are four such
///     handlers, one per template that used to host a CheckBox.
///   * There is no page-side entrance animation any more. the Pre-Builds rail's FadeSlideIn
///     already runs on every tab activation; the entrance this file used to play
///     on the stat strip and the Pivot was the second of the two. The expand
///     animation on the collapsible cards is NOT that and stays.
///
/// <para><b>2026-08-14 — the header band.</b> The action strip, the pinned
/// state sentence, the three stat tiles and the 19-row CHAT COMMANDS block are
/// gone from the markup; this file now owns the 48 px
/// <see cref="ToolPageHeader"/> that replaced the first two, and the split of
/// the VM's one command collection into the economy verbs (shown under
/// Currency) and the moderator verbs (folded behind a disclosure row).</para>
/// </summary>
public sealed partial class LoyaltyView : UserControl, IDisposable
{
    public LoyaltyViewModel ViewModel { get; }
    private bool _disposed;

    /// <summary>
    /// The five viewer-facing economy verbs — balance, give, top, watch-time,
    /// redeem — rendered under CURRENCY, beside the currency they move.
    /// </summary>
    /// <remarks>
    /// A VIEW of <c>ViewModel.Commands</c>, not a copy: the entries are the same
    /// <see cref="LoyaltyCommandRowVm"/> instances, so every edit still writes
    /// through the working config and the one 400 ms debounced save. The split
    /// lives here rather than in the VM because it is a layout decision — which
    /// section a row is drawn in — and the VM's collection is what the service
    /// and a foreign config reload talk to.
    /// </remarks>
    public ObservableCollection<LoyaltyCommandRowVm> EconomyCommands { get; } = new();

    /// <summary>The four moderator repair verbs — add / set / remove / wipe.</summary>
    public ObservableCollection<LoyaltyCommandRowVm> ModeratorCommands { get; } = new();

    public LoyaltyView(LoyaltyViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        PageHeader.Title = Localizer.T("panel.loyalty.header.title", "Loyalty");
        PageHeader.EnableRefresh(Localizer.T("panel.loyalty.header.refresh.tip", "Refresh viewer lists"));
        // The page's ONE brass CTA, unchanged in behaviour: it still jumps the
        // detail column to Rewards so the row it appends is on screen.
        PageHeader.EnablePrimaryCta(Localizer.T("panel.loyalty.header.add_cta", "+ ADD REWARD"));
        PageHeader.MasterToggled += OnHeaderMasterToggled;
        PageHeader.RefreshRequested += OnHeaderRefreshRequested;
        PageHeader.PrimaryCtaInvoked += OnHeaderPrimaryCta;

        SyncHeader();
        RebuildCommandSplit();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // BuildCommands() Clear()s and refills this on a foreign config reload,
        // which would otherwise leave both split lists holding dead rows.
        ViewModel.Commands.CollectionChanged += OnCommandsChanged;

        Loaded += OnLoaded;
    }

    // ── Header band ─────────────────────────────────────────────────────
    // ★ NO NEW PREDICATE. The two the status pill has always used are reused
    // verbatim: MasterEnabled (_working.Enabled) and StatusPulsing (Enabled &&
    // Streamer.bot connected). ToolStatusPill, LoyaltyService and
    // PreBuildStatusPillTests are untouched — only the render site moved.
    //
    // The middle state keeps the VM's own word, "degraded", rather than the
    // house "error": with the socket down the watch-time sweep pays nobody
    // (the active-viewer bridge returns an empty list outright), but event earn
    // — follow, sub, cheer, raid, tip — is not gated on it and keeps paying.
    // "error" would claim the tool is dead when half of it is still working.
    // The ERROR tier is used for the colour because the band has no warn tier
    // and grey would read as "off".
    private void SyncHeader()
    {
        PageHeader.IsOn = ViewModel.MasterEnabled;
        if (!ViewModel.MasterEnabled)
            PageHeader.SetState(Localizer.T("panel.loyalty.state.off", "off"), ToolStateKind.Dormant);
        else if (ViewModel.StatusPulsing)
            PageHeader.SetState(Localizer.T("panel.loyalty.state.earning", "live · earning"), ToolStateKind.Live);
        else
            PageHeader.SetState(Localizer.T("panel.loyalty.state.degraded", "degraded · no bot link"), ToolStateKind.Error);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // StatusPulsing is re-raised by RaiseStatusPill on every master flip and
        // every Streamer.bot connection change, so those two names cover the
        // whole state machine behind the phrase.
        if (e.PropertyName is nameof(LoyaltyViewModel.MasterEnabled)
                           or nameof(LoyaltyViewModel.StatusPulsing))
            SyncHeader();
    }

    private void OnHeaderMasterToggled(object? sender, bool isOn)
        // The VM setter uses SaveNow, bypassing the 400 ms debounce, so the tool
        // goes dormant on the press rather than 400 ms later.
        => ViewModel.MasterEnabled = isOn;

    private void OnHeaderPrimaryCta(object? sender, EventArgs e)
    {
        ViewModel.SelectedSection = LoyaltyViewModel.RewardsSectionIndex;
        ViewModel.AddReward();
    }

    private async void OnHeaderRefreshRequested(object? sender, EventArgs e)
    {
        // Explicit user refresh — force past the mid-edit draft guard so the button
        // always repopulates the Balances list.
        try { await ViewModel.RefreshViewersAsync(force: true); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "Refresh viewers failed", ex); }
    }

    // ── Command split (economy vs moderator) ────────────────────────────
    // The four admin rows — add points, set points, remove points, wipe — carry
    // LoyaltyCommandRowVm.IsModerator, set where the VM builds them. The fold
    // still follows the data rather than a second list of labels kept in sync
    // by hand; it just reads a declared flag instead of the blurb's "Admin: "
    // prefix, which is English and therefore gone in a translated build.
    private void OnCommandsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildCommandSplit();

    private void RebuildCommandSplit()
    {
        EconomyCommands.Clear();
        ModeratorCommands.Clear();
        foreach (var row in ViewModel.Commands)
        {
            if (row.IsModerator)
                ModeratorCommands.Add(row);
            else
                EconomyCommands.Add(row);
        }
        ModeratorFold.Count = ModeratorCommands.Count.ToString(CultureInfo.InvariantCulture);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        PageHeader.MasterToggled -= OnHeaderMasterToggled;
        PageHeader.RefreshRequested -= OnHeaderRefreshRequested;
        PageHeader.PrimaryCtaInvoked -= OnHeaderPrimaryCta;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Commands.CollectionChanged -= OnCommandsChanged;
        ViewModel.Dispose();
    }

    // ── Booleans converted from CheckBox to TogglePill ──────────────────
    // All four resolve the row from the sender's DataContext, which is the item
    // the template was realized for. TogglePill is display-only and binds OneWay,
    // so writing through the field VM here is what repaints it.

    /// <summary>Generic labelled-field toggle (Currency, Earn, Command handling, Overlay).</summary>
    private void OnFieldToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LoyaltyLabeledField field } && field.Toggle is { } toggle)
            toggle.Value = !(toggle.Value == true);
    }

    private void OnCommandEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LoyaltyCommandRowVm row })
            row.EnabledField.Value = !(row.EnabledField.Value == true);
    }

    private void OnGameEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LoyaltyGameVm row })
            row.EnabledField.Value = !(row.EnabledField.Value == true);
    }

    private void OnRewardEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LoyaltyRewardRowVm row })
            row.EnabledField.Value = !(row.EnabledField.Value == true);
    }

    // ── Collapsible cards (Minigames / Rewards) ─────────────────────────
    // Expander removed after the hard crash at first Minigames realization;
    // these hand-rolled toggles flip per-row VM state and play the house
    // FadeSlideIn on expand (mirrors the Giveaway / Timer SettingsBody idiom).
    // Both lists remain non-virtualizing ItemsControls precisely so the walk
    // below meets a stable, fully realized subtree.

    private void OnToggleGameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LoyaltyGameVm row } el) return;
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded && FindBodyElement(el, "GameCard", "GameBody") is { } body)
            AnimateExtensions.FadeSlideIn(body, slideY: 6, durationMs: 150);
    }

    private void OnToggleRewardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LoyaltyRewardRowVm row } el) return;
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded && FindBodyElement(el, "RewardCard", "RewardBody") is { } body)
            AnimateExtensions.FadeSlideIn(body, slideY: 6, durationMs: 150);
    }

    // DataTemplate namescopes are per-realization, so FindName from the page is
    // unreliable here; instead ascend from the clicked button to this row's
    // card root, then descend to the named body — a bounded visual-tree walk
    // that always resolves the element belonging to THIS row instance.
    // ★ The four literals below are the x:Name values in the two templates.
    // Renaming either end kills the expand animation silently.
    private static FrameworkElement? FindBodyElement(FrameworkElement origin, string cardName, string bodyName)
    {
        DependencyObject? node = origin;
        FrameworkElement? card = null;
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.Name == cardName) { card = fe; break; }
            node = VisualTreeHelper.GetParent(node);
        }
        return card is null ? null : FindDescendantByName(card, bodyName);
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            var nested = FindDescendantByName(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    // ── Rewards ─────────────────────────────────────────────────────────
    // ADD REWARD is the header band's primary CTA (OnHeaderPrimaryCta above),
    // which also jumps the detail column to the Rewards section — otherwise a
    // press from Currency or Earn would append a row the streamer cannot see.
    private void OnDeleteRewardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LoyaltyRewardRowVm row })
            row.Delete();
    }

    // ── Viewers ─────────────────────────────────────────────────────────
    private async void OnSetBalanceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LoyaltyBalanceRowVm row }) return;
        try { await row.ApplyAsync(); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "Set balance failed", ex); }
    }

    private async void OnLookupClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LookupBalanceAsync(); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "Lookup failed", ex); }
    }

    private async void OnLookupAddClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LookupAddAsync(); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "Lookup add failed", ex); }
    }

    private async void OnLookupSetClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LookupSetAsync(); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "Lookup set failed", ex); }
    }

    private async void OnLookupRemoveClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.LookupRemoveAsync(); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "Lookup remove failed", ex); }
    }

    // ── Merge duplicate wallets ─────────────────────────────────────────
    // Preview is a pure read; Merge is the destructive half and runs ONLY from
    // this press — see LoyaltyService.MergeWalletsAsync for why nothing folds
    // these rows automatically.
    private async void OnPreviewWalletMerge(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.PreviewWalletMergeAsync(); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "Wallet merge preview failed", ex); }
    }

    private async void OnApplyWalletMerge(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ApplyWalletMergeAsync(); }
        catch (Exception ex) { GlobalLogger.Error("LoyaltyView", "Wallet merge failed", ex); }
    }
}
