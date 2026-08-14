using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Hub.WinUI.Animation;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Windows.System;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.TimerPanel;

/// <summary>
/// The Hub Timer page — a subathon-style countdown surfaced as a closable
/// in-Hub tab via the chrome's Pre-Builds tab (see MainWindow.OpenToolTab
/// → the Pre-Builds rail). UserControl + ViewModel pattern matching the other Hub
/// panels and cloning the Giveaway shell's coal/ember design language;
/// IDisposable so the Pre-Builds rail can dispose the VM (which unsubscribes from the
/// TimerService events) when the tab is closed.
/// </summary>
public sealed partial class TimerView : UserControl, IDisposable
{
    public TimerViewModel ViewModel { get; }
    private bool _disposed;

    public TimerView(TimerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        PageHeader.Title = Localizer.T("panel.timer.header.title", "Timer");
        PageHeader.MasterToggled += OnMasterRunToggled;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDefaultToggleVisual();
        ApplyPauseOfflineToggleVisual();
        ApplyFeedbackToggleVisuals();
        ApplyHeaderState();
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "LoadAsync failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PageHeader.MasterToggled -= OnMasterRunToggled;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded -= OnLoaded;
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TimerViewModel.IsDefault):
                ApplyDefaultToggleVisual();
                break;
            case nameof(TimerViewModel.PauseWhenOffline):
                ApplyPauseOfflineToggleVisual();
                break;
            case nameof(TimerViewModel.HasSelection):
                ApplyDefaultToggleVisual();
                ApplyPauseOfflineToggleVisual();
                // Raised on EVERY ApplySelection, which is also where the feedback cards
                // are re-seeded — so the three gate pills repaint from the freshly loaded
                // config without the view having to observe each card VM separately.
                ApplyFeedbackToggleVisuals();
                ApplyHeaderState();
                break;
            case nameof(TimerViewModel.IsRunning):
            case nameof(TimerViewModel.StatePillText):
                ApplyHeaderState();
                break;
            case nameof(TimerViewModel.CreateDialogOpen):
                ApplyCreateOverlay();
                break;
            case nameof(TimerViewModel.SettingsBodyVisibility):
                // Collapsible SETTINGS card: fade + short slide the body in on
                // expand (mirrors GiveawayView; composited, no height animation).
                if (ViewModel.SettingsBodyVisibility == Visibility.Visible)
                    AnimateExtensions.FadeSlideIn(SettingsBody, slideY: 6, durationMs: 150);
                break;
        }
    }

    // ── Top action strip ────────────────────────────────────────────────

    private void OnCreateClick(object sender, RoutedEventArgs e) => ViewModel.BeginCreate();

    private void OnPickerItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string slug })
        {
            ViewModel.SelectTimer(slug);
            PickerFlyout.Hide();
        }
    }

    private void OnPickerCreateClick(object sender, RoutedEventArgs e)
    {
        PickerFlyout.Hide();
        ViewModel.BeginCreate();
    }

    private async void OnToggleDefaultClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ToggleDefaultAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Toggle default failed", ex); }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.DeleteSelectedAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Delete timer failed", ex); }
    }

    // ── Control buttons ─────────────────────────────────────────────────

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.StartAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Start failed", ex); }
    }

    private async void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.PauseResumeAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Pause/Resume failed", ex); }
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ResetAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Reset failed", ex); }
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.StopAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Stop failed", ex); }
    }

    // ── Manual add / subtract / set ─────────────────────────────────────

    private async void OnAddTimeClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.AddTimeAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Add time failed", ex); }
    }

    private async void OnSubtractTimeClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SubtractTimeAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Subtract time failed", ex); }
    }

    private async void OnSetTimeClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SetTimeAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Set time failed", ex); }
    }

    // The ADJUST box sits between three buttons (+ADD / –SUB / = SET), so a bare Enter
    // is ambiguous and used to resolve SILENTLY to ADD — a streamer who typed a value
    // meaning SET added it to a live subathon instead, with nothing on screen naming
    // which of the three had run.
    //
    // Enter stays +ADD rather than becoming SET: of the three it is the only
    // non-destructive one (SET overwrites the whole clock, SUB can end a subathon), so
    // the keyboard default must not be the one that throws time away. What the fix
    // removes is the SILENCE — the +ADD button carries the ↵ glyph that names it as this
    // box's default action, and the box's tooltip says so, so the binding is attributed
    // to the button that owns it instead of being invisible.
    private async void OnAddDurationKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        try { await ViewModel.AddTimeAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Add time (Enter) failed", ex); }
    }

    // ── Settings card ───────────────────────────────────────────────────

    private void OnToggleSettingsClick(object sender, RoutedEventArgs e)
        => ViewModel.ToggleSettingsExpanded();

    // Shared handlers for every draft field — the field itself rides in Tag.
    private async void OnDraftFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TimerDraftField f })
        {
            try { await f.CommitAsync(ViewModel.SelectedTimer); }
            catch (Exception ex) { GlobalLogger.Error("TimerView", "Commit setting failed", ex); }
        }
    }

    private async void OnDraftFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimerDraftField f }) return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try { await f.CommitAsync(ViewModel.SelectedTimer); }
            catch (Exception ex) { GlobalLogger.Error("TimerView", "Commit setting failed", ex); }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            f.Revert(ViewModel.SelectedTimer);
        }
    }

    private async void OnTogglePauseOfflineClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.TogglePauseWhenOfflineAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Toggle pause-when-offline failed", ex); }
    }

    private async void OnApplyHappyHourClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ApplyHappyHourAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Apply Happy Hour failed", ex); }
    }

    // ── Feedback cards (chat + visual per timer event) ──────────────────
    // Text boxes and the two editable combos share one commit path: the whole per-timer
    // feedback config is written at once, so which control changed does not matter — only
    // that focus left it (or Enter was pressed). The card rides in Tag, exactly as the
    // settings fields carry their TimerDraftField.

    private static TimerFeedbackCardVm? FeedbackCardOf(object sender)
        => (sender as FrameworkElement)?.Tag as TimerFeedbackCardVm;

    private void OnFeedbackFieldGotFocus(object sender, RoutedEventArgs e)
    {
        if (FeedbackCardOf(sender) is { } card) card.IsEditing = true;
    }

    private async void OnFeedbackFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (FeedbackCardOf(sender) is not { } card) return;
        // Clear BEFORE committing: the refresh the commit triggers must be free to
        // re-seed the card from the authoritative config.
        card.IsEditing = false;
        try { await card.CommitAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Commit feedback failed", ex); }
    }

    private async void OnFeedbackFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (FeedbackCardOf(sender) is not { } card) return;
        card.IsEditing = false;
        try { await card.CommitAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Commit feedback (Enter) failed", ex); }
    }

    private void OnFeedbackLayerComboOpened(object sender, object e) => ViewModel.RefreshLayers();

    private void OnFeedbackTriggerComboOpened(object sender, object e)
    {
        if (FeedbackCardOf(sender) is { } card) card.RefreshTriggerOptions();
    }

    private void OnRefreshFeedbackLayersClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshLayers();
        ViewModel.ZeroFeedback.RefreshTriggerOptions();
        ViewModel.MilestoneFeedback.RefreshTriggerOptions();
        ViewModel.AddFeedback.RefreshTriggerOptions();
        foreach (var row in ViewModel.Milestones) row.RefreshTriggerOptions();
    }

    private async void OnToggleZeroFeedbackClick(object sender, RoutedEventArgs e)
        => await ToggleFeedbackAsync(ViewModel.ZeroFeedback, "on-zero");

    private async void OnToggleMilestoneFeedbackClick(object sender, RoutedEventArgs e)
        => await ToggleFeedbackAsync(ViewModel.MilestoneFeedback, "milestone");

    private async void OnToggleAddFeedbackClick(object sender, RoutedEventArgs e)
        => await ToggleFeedbackAsync(ViewModel.AddFeedback, "time-added");

    private async Task ToggleFeedbackAsync(TimerFeedbackCardVm card, string label)
    {
        try { await card.ToggleEnabledAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", $"Toggle {label} response failed", ex); }
        ApplyFeedbackToggleVisuals();
    }

    private void ApplyFeedbackToggleVisuals()
    {
        ApplyPillToggle(ZeroFeedbackPill, ZeroFeedbackKnob, ViewModel.ZeroFeedback.Enabled);
        ApplyPillToggle(MilestoneFeedbackPill, MilestoneFeedbackKnob, ViewModel.MilestoneFeedback.Enabled);
        ApplyPillToggle(AddFeedbackPill, AddFeedbackKnob, ViewModel.AddFeedback.Enabled);
    }

    // ── Milestones ──────────────────────────────────────────────────────

    private async void OnAddMilestoneClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.AddMilestoneAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Add milestone failed", ex); }
    }

    private async void OnMilestoneTargetKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        try { await ViewModel.AddMilestoneAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Add milestone (Enter) failed", ex); }
    }

    private async void OnRemoveMilestoneClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        try { await ViewModel.RemoveMilestoneAsync(id); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Remove milestone failed", ex); }
    }

    // ── Inline milestone editing (label + the time "pill") ──────────────
    // Deliberately NOT the shared OnDraftFieldLostFocus / OnDraftFieldKeyDown pair:
    // those pattern-match `Tag: TimerDraftField`, and a milestone row's Tag is the
    // milestone's string Id (the remove button consumes the same Tag).
    //
    // The IsEditing flags exist so the Id-keyed reconciler leaves a box alone while it
    // holds focus. Committing bumps UpdatedAtUnixMs, which is part of SelectionStamp,
    // so a commit lands straight back in ApplySelection — without the flags the row
    // would overwrite the very text that triggered it.

    private static TimerMilestoneRowVm? MilestoneRowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as TimerMilestoneRowVm;

    private void OnMilestoneTargetGotFocus(object sender, RoutedEventArgs e)
    {
        if (MilestoneRowOf(sender) is { } row) row.IsEditingTarget = true;
    }

    private void OnMilestoneLabelGotFocus(object sender, RoutedEventArgs e)
    {
        if (MilestoneRowOf(sender) is { } row) row.IsEditingLabel = true;
    }

    private async void OnMilestoneRowLostFocus(object sender, RoutedEventArgs e)
    {
        if (MilestoneRowOf(sender) is not { } row) return;
        // Clear the flags BEFORE committing: the refresh the commit triggers must be
        // free to rewrite the row from the authoritative milestone.
        row.IsEditingTarget = false;
        row.IsEditingLabel = false;
        try { await row.CommitAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Commit milestone failed", ex); }
    }

    private async void OnMilestoneRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (MilestoneRowOf(sender) is not { } row) return;
        row.IsEditingTarget = false;
        row.IsEditingLabel = false;
        try { await row.CommitAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Commit milestone (Enter) failed", ex); }
    }

    // Per-goal feedback overrides — a separate commit from the label/target pair above,
    // because that one re-seeds Reached against the target and editing the text a goal
    // announces must not disturb the goal itself.

    private void OnToggleMilestoneRowFeedbackClick(object sender, RoutedEventArgs e)
    {
        if (MilestoneRowOf(sender) is { } row) row.ToggleFeedbackExpanded();
    }

    private void OnMilestoneFeedbackGotFocus(object sender, RoutedEventArgs e)
    {
        if (MilestoneRowOf(sender) is { } row) row.IsEditingFeedback = true;
    }

    private async void OnMilestoneFeedbackLostFocus(object sender, RoutedEventArgs e)
    {
        if (MilestoneRowOf(sender) is not { } row) return;
        row.IsEditingFeedback = false;
        try { await row.CommitFeedbackAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Commit milestone feedback failed", ex); }
    }

    private async void OnMilestoneFeedbackKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (MilestoneRowOf(sender) is not { } row) return;
        row.IsEditingFeedback = false;
        try { await row.CommitFeedbackAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Commit milestone feedback (Enter) failed", ex); }
    }

    private void OnMilestoneLayerComboOpened(object sender, object e) => ViewModel.RefreshLayers();

    private void OnMilestoneTriggerComboOpened(object sender, object e)
    {
        if (MilestoneRowOf(sender) is { } row) row.RefreshTriggerOptions();
    }

    // ── Create overlay ──────────────────────────────────────────────────

    private void ApplyCreateOverlay()
    {
        bool open = ViewModel.CreateDialogOpen;
        CreateOverlay.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
            DispatcherQueue.TryEnqueue(() => CreateNameBox.Focus(FocusState.Programmatic));
    }

    private void OnCreateCancelClick(object sender, RoutedEventArgs e) => ViewModel.CancelCreate();

    private async void OnCreateConfirmClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ConfirmCreateAsync(); }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Create timer failed", ex); }
    }

    private async void OnCreateNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try { await ViewModel.ConfirmCreateAsync(); }
            catch (Exception ex) { GlobalLogger.Error("TimerView", "Create (Enter) failed", ex); }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.CancelCreate();
        }
    }

    // ── Pill-toggle visuals (default + pause-when-offline) ──────────────

    private static readonly Color EmberPrimary  = Color.FromArgb(0xFF, 0xE5, 0xA2, 0x4E);
    private static readonly Color CoalSecondary = Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72);
    private static readonly Color CoalShell     = Color.FromArgb(0xFF, 0x0B, 0x09, 0x07);
    private static readonly Color CoalDivider   = Color.FromArgb(0xFF, 0x3A, 0x31, 0x27);

    private void ApplyDefaultToggleVisual()
    {
        bool on = ViewModel.IsDefault;
        ApplyPillToggle(DefaultTogglePill, DefaultToggleKnob, on);
        DefaultToggleLabel.Foreground = new SolidColorBrush(on ? EmberPrimary : CoalSecondary);
    }

    private void ApplyPauseOfflineToggleVisual()
        => ApplyPillToggle(PauseOfflinePill, PauseOfflineKnob, ViewModel.PauseWhenOffline);

    private static void ApplyPillToggle(Border pill, Ellipse knob, bool on)
    {
        knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        knob.Margin = on ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        knob.Fill = new SolidColorBrush(on ? CoalShell : CoalSecondary);
        pill.Background = new SolidColorBrush(on ? EmberPrimary : CoalDivider);
    }

    // ── Header band: state phrase + master run switch ───────────────────
    //
    // This replaces the hand-rolled status pill (and its pulsing-dot storyboard)
    // that used to sit under the big countdown. Nothing about the STATE is
    // re-derived here: the phrase is TimerViewModel.StatePillText verbatim and
    // the three colour tiers are the same Ok / Err / secondary split
    // StatePillBrush already made. Only the render site moved — a page that says
    // "running · counting down" in the band AND again 200 px lower is exactly the
    // triple-rendering this pass exists to remove.

    private void ApplyHeaderState()
    {
        PageHeader.IsOn = ViewModel.IsRunning;

        if (!ViewModel.HasSelection)
        {
            // The detail column is collapsed in this case, so the run state of a
            // timer that isn't there would be a false reading.
            PageHeader.SetState(Localizer.T("panel.timer.state.no_timer", "no timer"), ToolStateKind.Dormant);
            return;
        }

        // Ended is the one run state with no dedicated VM predicate. CanStop
        // covers Running | Paused | Ended, so removing the two that DO have one
        // (IsRunning, CanPauseResume) leaves exactly Ended. Composed from the
        // existing public projections — no service call, no new predicate.
        bool ended = ViewModel.CanStop && !ViewModel.IsRunning && !ViewModel.CanPauseResume;

        ToolStateKind kind = ViewModel.IsRunning ? ToolStateKind.Live
                           : ended               ? ToolStateKind.Error
                                                 : ToolStateKind.Dormant;

        PageHeader.SetState(ViewModel.StatePillText, kind);
    }

    // The band's master switch is the CLOCK GATE. Timer has no tool-wide enable,
    // and of the page's candidate booleans this is the only honest two-way one:
    // "is this timer's clock ticking".
    //
    // OFF deliberately PAUSES rather than stops. Stop is the destructive end of a
    // subathon and it already has its own button one row down; a master switch
    // that could throw a live accrual away on a stray click would be a new
    // hazard, not a tidier one.
    private async void OnMasterRunToggled(object? sender, bool on)
    {
        try
        {
            if (on)
            {
                // The switch was off, so Running is excluded — CanPauseResume can
                // therefore only mean Paused, which resumes. Stopped and Ended
                // start from the clock on screen (StartFromCurrentAsync).
                if (ViewModel.CanPauseResume) await ViewModel.PauseResumeAsync();
                else await ViewModel.StartAsync();
            }
            else if (ViewModel.IsRunning)
            {
                await ViewModel.PauseResumeAsync();
            }
        }
        catch (Exception ex) { GlobalLogger.Error("TimerView", "Master run toggle failed", ex); }

        // Re-seed from the authoritative state: a toggle the service refuses (no
        // selection, for one) must not leave the switch asserting something the
        // timer is not. The IsOn setter suppresses the event, so this cannot loop.
        ApplyHeaderState();
    }
}
