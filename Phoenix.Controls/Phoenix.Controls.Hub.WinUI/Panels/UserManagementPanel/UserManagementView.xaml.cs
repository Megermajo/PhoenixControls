using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Animation;
// ToolPageHeader / ToolStateKind — Panels.Common is a SIBLING namespace, so it is
// not in scope by enclosing-namespace lookup the way Panels.* would be.
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.UserManagementPanel;

/// <summary>
/// The Hub User Management page — first-message welcoming, the once-ever
/// greeting, a chat-driven viewer queue and group-granted role rights, surfaced
/// as a page on the Pre-Builds rail via the chrome's Pre-Builds tab (see
/// MainWindow.CreateToolPage → PreBuildsHostView). UserControl + ViewModel pattern
/// matching the Counters / Timer / Giveaway pages; IDisposable so the Pre-Builds rail
/// disposes the VM (which flushes pending edits and unsubscribes from the
/// UserManagementService and ViewerPresenceService events) when the tab is closed.
///
/// Since the master-detail rebuild the page is a star-width settings column behind
/// a section nav, with the live viewer queue in a fixed 300 px column. Four
/// consequences for this file:
///
///   * The section LABELS are assigned here, at Loaded, rather than in the VM:
///     six of the seven reuse lang keys the page already ships, and Localizer is
///     only guaranteed initialised by then. Those six keys are now the ONLY
///     reader of themselves — the section eyebrows that used to repeat them
///     inside each body are gone.
///   * The 48 px ToolPageHeader is driven from here (SyncHeader), not bound: it
///     takes plain properties, so the master switch is seeded and the state
///     phrase re-pushed from a PropertyChanged handler.
///   * The converted toggles (the ToggleSwitches and the one CheckBox became
///     TogglePills) flip from Click handlers, because TogglePill is
///     display-only — the host Button owns the gesture.
///   * The two EDITABLE group lists deliberately stay on ItemsControl: they host
///     the GroupCard / GroupBody visual-tree walk below, and both templates share
///     those x:Names on purpose, so they must never be merged into one repeater
///     with a template selector. The platform-group list is an ItemsRepeater and
///     may stay one — its card is flat, with no body to reveal and nothing to walk
///     the tree for.
///
/// The WATCH TIME section is the page's one hub-wide surface: its settings are
/// AppConfig rather than this tool's config (see the VM), and its row handlers act
/// on ViewerPresenceService through the VM.
///
/// GONE ON PURPOSE: the page-side entrance animation. the Pre-Builds rail's FadeSlideIn
/// already runs on every tab activation, so the page was double-animating. The
/// group-card expand animation is NOT gone — that one is a per-click reveal, not
/// a page entrance.
/// </summary>
public sealed partial class UserManagementView : UserControl, IDisposable
{
    public UserManagementViewModel ViewModel { get; }
    private bool _disposed;

    public UserManagementView(UserManagementViewModel vm)
    {
        ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
        InitializeComponent();

        // The header band's master switch and state phrase are pushed, not bound:
        // ToolPageHeader takes plain properties (see its doc — write-once labels
        // do not earn DP machinery), so this view has to keep them in step with
        // the VM by hand.
        PageHeader.MasterToggled += OnHeaderMasterToggled;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLocalizedText();
        // Seed before the load so the band is never blank; LoadAsync raises the
        // same properties again once the config is in, and the handler re-pushes.
        SyncHeader();
        try { await ViewModel.LoadAsync(); }
        catch (Exception ex) { GlobalLogger.Error("UserManagementView", "LoadAsync failed", ex); }
    }

    // ── Header band ─────────────────────────────────────────────────────
    // ★ THE STATE PHRASE IS THE PILL'S OWN PREDICATE, RENDERED SOMEWHERE ELSE.
    // UserManagementService.PillState and the five phrases the VM projects from it
    // are untouched, and so is PreBuildStatusPillTests: that pair carries the
    // honesty audit where every PARTIAL state was refused a green light, because
    // the master switch says nothing about the queue's own gate or the greeting
    // halves under it. This view invents no state of its own — it reads
    // StatusPillText for the words and StatusPulsing (true only for
    // LiveWithQueue, the one state where both halves have a beat) for the tier.
    private void SyncHeader()
    {
        PageHeader.IsOn = ViewModel.MasterEnabled;   // seeding; raises nothing
        PageHeader.SetState(
            ViewModel.StatusPillText,
            ViewModel.StatusPulsing ? ToolStateKind.Live : ToolStateKind.Dormant);
    }

    private void OnHeaderMasterToggled(object? sender, bool isOn)
        => ViewModel.MasterEnabled = isOn;

    // The VM raises the pill trio from RaisePillState and MasterEnabled from both
    // the setter and the foreign-ConfigChanged reload, so this covers a flip made
    // in another window as well as one made here.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UserManagementViewModel.MasterEnabled):
            case nameof(UserManagementViewModel.StatusPillText):
            case nameof(UserManagementViewModel.StatusPulsing):
                SyncHeader();
                break;
        }
    }

    // Resolved at Loaded (Localizer.Init has run by then — the PanelHeader.TitleKey
    // convention); idempotent on tab re-attach. The XAML literals double as the
    // English fallbacks, SystemLogView-style.
    private void ApplyLocalizedText()
    {
        // ★ Six of the seven labels reuse keys the page already ships for the very
        // headings they navigate to — and since those headings were deleted as exact
        // duplicates of these labels, this call is now the only place each key is
        // read. Deleting a key here would silently un-translate the nav itself.
        // WELCOMING was the one chip with no key of its own — its eyebrow had been a
        // literal with no x:Name, so there was nothing to reuse. It gets its own key
        // here rather than staying English, which is what a translated build was
        // rendering: six chips in German and this one not.
        SectionSelector.Sections = new List<string>
        {
            Localizer.T("panel.usermgmt.welcoming.section", "WELCOMING"),
            Localizer.T("panel.usermgmt.greeting.section", "GREETING"),
            Localizer.T("panel.usermgmt.queue.section", "VIEWER QUEUE"),
            Localizer.T("panel.usermgmt.queue.overlay_eyebrow", "OVERLAY"),
            Localizer.T("panel.usermgmt.queue.replies_eyebrow", "REPLIES"),
            Localizer.T("panel.usermgmt.groups.section", "GROUPS"),
            Localizer.T("panel.usermgmt.watchtime.section", "WATCH TIME"),
        };

        // The page's own name, in the header band. It reuses the Pre-Builds rail's
        // label rather than minting a key, so the tab and the page it opens can
        // never disagree in a translated build.
        PageHeader.Title = Localizer.T("pillartab.prebuilds.usermanagement", "User Management");

        // ★ NINE assignments left with the elements they addressed, and every one
        // of them had to: an assignment to a deleted x:Name is a CS0103, not a
        // silent no-op. Their KEYS all stay in the four lang bundles — six of the
        // seven section-eyebrow keys are read three lines above, as the nav's own
        // labels, so this is a re-point, not a loss:
        //   PageScopeHint          panel.usermgmt.page_scope        (banner deleted)
        //   GreetingSectionEyebrow panel.usermgmt.greeting.section  (= nav label)
        //   QueueSectionEyebrow    panel.usermgmt.queue.section     (= nav label)
        //   QueueOverlayEyebrow    panel.usermgmt.queue.overlay_eyebrow (= nav label)
        //   QueueRepliesEyebrow    panel.usermgmt.queue.replies_eyebrow (= nav label)
        //   GroupsSectionEyebrow   panel.usermgmt.groups.section    (= nav label)
        //   WatchTimeSectionEyebrow panel.usermgmt.watchtime.section (= nav label)
        //   GroupsSectionHint is NOT in that list — it survived; only its heading went.
        //   QueueEmptyTitle        panel.usermgmt.queue.empty_title (one-line empty state)
        GreetingCardEyebrow.Text = Localizer.T("panel.usermgmt.greeting.card_eyebrow", GreetingCardEyebrow.Text);
        GreetingCardHint.Text = Localizer.T("panel.usermgmt.greeting.card_hint", GreetingCardHint.Text);
        GreetingMessageLabel.Text = Localizer.T("panel.usermgmt.greeting.message_label", GreetingMessageLabel.Text);
        GreetingTokensHint.Text = Localizer.T("panel.usermgmt.greeting.tokens_hint", GreetingTokensHint.Text);
        ResetMemoryButtonText.Text = Localizer.T("panel.usermgmt.greeting.reset_button", ResetMemoryButtonText.Text);

        QueueCardEyebrow.Text = Localizer.T("panel.usermgmt.queue.card_eyebrow", QueueCardEyebrow.Text);
        QueueCardHint.Text = Localizer.T("panel.usermgmt.queue.card_hint", QueueCardHint.Text);
        QueueNameLabel.Text = Localizer.T("panel.usermgmt.queue.name_label", QueueNameLabel.Text);
        QueueJoinLabel.Text = Localizer.T("panel.usermgmt.queue.join_label", QueueJoinLabel.Text);
        QueueLeaveLabel.Text = Localizer.T("panel.usermgmt.queue.leave_label", QueueLeaveLabel.Text);
        QueueListLabel.Text = Localizer.T("panel.usermgmt.queue.list_label", QueueListLabel.Text);
        QueuePositionLabel.Text = Localizer.T("panel.usermgmt.queue.position_label", QueuePositionLabel.Text);
        QueueMaxLabel.Text = Localizer.T("panel.usermgmt.queue.max_label", QueueMaxLabel.Text);
        QueueSubPrioLabel.Text = Localizer.T("panel.usermgmt.queue.sub_priority_label", QueueSubPrioLabel.Text);
        QueueVipPrioLabel.Text = Localizer.T("panel.usermgmt.queue.vip_priority_label", QueueVipPrioLabel.Text);
        QueueCooldownLabel.Text = Localizer.T("panel.usermgmt.queue.cooldown_label", QueueCooldownLabel.Text);
        QueuePriorityHint.Text = Localizer.T("panel.usermgmt.queue.priority_hint", QueuePriorityHint.Text);
        QueueJoinRolesLabel.Text = Localizer.T("panel.usermgmt.queue.join_roles_label", QueueJoinRolesLabel.Text);
        QueueModRolesLabel.Text = Localizer.T("panel.usermgmt.queue.mod_roles_label", QueueModRolesLabel.Text);
        QueueOverlayHint.Text = Localizer.T("panel.usermgmt.queue.overlay_hint", QueueOverlayHint.Text);
        QueueOverlaySizeLabel.Text = Localizer.T("panel.usermgmt.queue.overlay_size_label", QueueOverlaySizeLabel.Text);
        QueueRepliesTokens.Text = Localizer.T("panel.usermgmt.queue.replies_tokens", QueueRepliesTokens.Text);
        QueueMsgJoinedLabel.Text = Localizer.T("panel.usermgmt.queue.msg_joined", QueueMsgJoinedLabel.Text);
        QueueMsgAlreadyLabel.Text = Localizer.T("panel.usermgmt.queue.msg_already", QueueMsgAlreadyLabel.Text);
        QueueMsgFullLabel.Text = Localizer.T("panel.usermgmt.queue.msg_full", QueueMsgFullLabel.Text);
        QueueMsgLeftLabel.Text = Localizer.T("panel.usermgmt.queue.msg_left", QueueMsgLeftLabel.Text);
        QueueMsgNotQueuedLabel.Text = Localizer.T("panel.usermgmt.queue.msg_not_queued", QueueMsgNotQueuedLabel.Text);
        QueueMsgPositionLabel.Text = Localizer.T("panel.usermgmt.queue.msg_position", QueueMsgPositionLabel.Text);
        QueueMsgListLabel.Text = Localizer.T("panel.usermgmt.queue.msg_list", QueueMsgListLabel.Text);
        QueueMsgEmptyLabel.Text = Localizer.T("panel.usermgmt.queue.msg_empty", QueueMsgEmptyLabel.Text);
        QueueMsgNextLabel.Text = Localizer.T("panel.usermgmt.queue.msg_next", QueueMsgNextLabel.Text);
        QueueMsgRemovedLabel.Text = Localizer.T("panel.usermgmt.queue.msg_removed", QueueMsgRemovedLabel.Text);
        QueueMsgClearedLabel.Text = Localizer.T("panel.usermgmt.queue.msg_cleared", QueueMsgClearedLabel.Text);
        QueueNowWaitingEyebrow.Text = Localizer.T("panel.usermgmt.queue.now_waiting", QueueNowWaitingEyebrow.Text);
        QueueClearButtonText.Text = Localizer.T("panel.usermgmt.queue.clear_button", QueueClearButtonText.Text);
        QueueEmptyBody.Text = Localizer.T("panel.usermgmt.queue.empty_body", QueueEmptyBody.Text);

        // The four moderator sub-verbs — new fields this pass, and the reason the
        // words next / pick / remove / clear stopped being unchangeable literals.
        QueueModVerbsLabel.Text = Localizer.T("panel.usermgmt.queue.mod_verbs_label", QueueModVerbsLabel.Text);
        QueueNextSubLabel.Text = Localizer.T("panel.usermgmt.queue.next_sub_label", QueueNextSubLabel.Text);
        QueuePickSubLabel.Text = Localizer.T("panel.usermgmt.queue.pick_sub_label", QueuePickSubLabel.Text);
        QueueRemoveSubLabel.Text = Localizer.T("panel.usermgmt.queue.remove_sub_label", QueueRemoveSubLabel.Text);
        QueueClearSubLabel.Text = Localizer.T("panel.usermgmt.queue.clear_sub_label", QueueClearSubLabel.Text);

        GroupsSectionHint.Text = Localizer.T("panel.usermgmt.groups.section_hint", GroupsSectionHint.Text);
        GroupsPlatformEyebrow.Text = Localizer.T("panel.usermgmt.groups.platform_eyebrow", GroupsPlatformEyebrow.Text);
        GroupsYoursEyebrow.Text = Localizer.T("panel.usermgmt.groups.yours_eyebrow", GroupsYoursEyebrow.Text);

        WatchTimeCardEyebrow.Text = Localizer.T("panel.usermgmt.watchtime.card_eyebrow", WatchTimeCardEyebrow.Text);
        WatchTimeCardHint.Text = Localizer.T("panel.usermgmt.watchtime.card_hint", WatchTimeCardHint.Text);
        WatchTimeLiveOnlyLabel.Text = Localizer.T("panel.usermgmt.watchtime.live_only_label", WatchTimeLiveOnlyLabel.Text);
        WatchTimePollLabel.Text = Localizer.T("panel.usermgmt.watchtime.poll_label", WatchTimePollLabel.Text);
        WatchTimePollHint.Text = Localizer.T("panel.usermgmt.watchtime.poll_hint", WatchTimePollHint.Text);
        TopWatchersEyebrow.Text = Localizer.T("panel.usermgmt.watchtime.top_eyebrow", TopWatchersEyebrow.Text);
        TopWatchersEmptyText.Text = Localizer.T("panel.usermgmt.watchtime.top_empty", TopWatchersEmptyText.Text);
        WatchBrowserEyebrow.Text = Localizer.T("panel.usermgmt.watchtime.browser_eyebrow", WatchBrowserEyebrow.Text);
        WatchBrowserHint.Text = Localizer.T("panel.usermgmt.watchtime.browser_hint", WatchBrowserHint.Text);
        // PlaceholderText, not Text — the search box has no caption of its own.
        WatchSearchBox.PlaceholderText =
            Localizer.T("panel.usermgmt.watchtime.search_placeholder", WatchSearchBox.PlaceholderText);
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

    // ── Section gates (ToggleSwitch → TogglePill; all five stayed in-section) ──
    private void OnToggleWelcomingClick(object sender, RoutedEventArgs e)
        => ViewModel.WelcomingEnabled = !ViewModel.WelcomingEnabled;

    private void OnToggleGeneralWelcomeClick(object sender, RoutedEventArgs e)
        => ViewModel.GeneralWelcomeEnabled = !ViewModel.GeneralWelcomeEnabled;

    private void OnToggleGreetingClick(object sender, RoutedEventArgs e)
        => ViewModel.GreetingEnabled = !ViewModel.GreetingEnabled;

    private void OnToggleQueueClick(object sender, RoutedEventArgs e)
        => ViewModel.QueueEnabled = !ViewModel.QueueEnabled;

    private void OnToggleQueueOverlayClick(object sender, RoutedEventArgs e)
        => ViewModel.QueueOverlayEnabled = !ViewModel.QueueOverlayEnabled;

    // ── Personalized welcomes ───────────────────────────────────────────
    private void OnAddWelcomeClick(object sender, RoutedEventArgs e) => ViewModel.AddWelcome();

    private void OnDeleteWelcomeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WelcomeRowVm row })
            ViewModel.RemoveWelcome(row);
    }

    private void OnToggleWelcomeEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WelcomeRowVm row })
            row.Enabled = !row.Enabled;
    }

    // The one converted CheckBox on this page. The VM keeps its bool? property
    // (and its null→false coercion) for the snapshot path; this routes through it.
    private void OnToggleWelcomeShoutoutClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WelcomeRowVm row })
            row.ToggleAutoShoutout();
    }

    // ── First-time greeting ─────────────────────────────────────────────
    // The one modal in this half of the panel — sanctioned because the reset is
    // genuinely irreversible (forgets every known chatter), not a repeatable
    // rejection. DefaultButton=Close so Enter cannot destroy.
    private async void OnResetKnownChattersClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = Localizer.T("panel.usermgmt.greeting.reset_confirm_title", "Forget every known chatter?"),
                Content = Localizer.T("panel.usermgmt.greeting.reset_confirm_body",
                    "Every chatter, including regulars, is greeted as brand-new again. This cannot be undone."),
                PrimaryButtonText = Localizer.T("panel.usermgmt.greeting.reset_confirm_yes", "Reset memory"),
                CloseButtonText = Localizer.T("panel.usermgmt.greeting.reset_confirm_cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.ResetKnownChattersAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("UserManagementView", "known-chatters reset failed", ex);
        }
    }

    // ── Viewer queue ────────────────────────────────────────────────────
    private async void OnRemoveQueueRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QueueRowVm row }) return;
        try { await ViewModel.RemoveFromQueueAsync(row); }
        catch (Exception ex) { GlobalLogger.Error("UserManagementView", "queue remove failed", ex); }
    }

    // The second sanctioned modal on this page — same test as the memory reset: clearing
    // the queue is irreversible (the line is gone, and everyone has to re-join), not a
    // repeatable rejection.
    private async void OnClearQueueClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = Localizer.T("panel.usermgmt.queue.clear_confirm_title", "Clear the queue?"),
                Content = Localizer.T("panel.usermgmt.queue.clear_confirm_body",
                    "Everyone waiting is removed and has to join again. This cannot be undone."),
                PrimaryButtonText = Localizer.T("panel.usermgmt.queue.clear_confirm_yes", "Clear queue"),
                CloseButtonText = Localizer.T("panel.usermgmt.queue.clear_confirm_cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.ClearQueueAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("UserManagementView", "queue clear failed", ex);
        }
    }

    // ── Watch time (hub-wide settings + the manual corrections) ─────────
    // The two pills write AppConfig through the VM, not this tool's config — see the
    // WATCH TIME block in UserManagementViewModel for why that separation is
    // load-bearing rather than tidy.
    private void OnToggleWatchTrackingClick(object sender, RoutedEventArgs e)
        => ViewModel.WatchTimeTrackingEnabled = !ViewModel.WatchTimeTrackingEnabled;

    private void OnToggleWatchLiveOnlyClick(object sender, RoutedEventArgs e)
        => ViewModel.WatchTimeOnlyWhenLive = !ViewModel.WatchTimeOnlyWhenLive;

    // Filters as you type. A TwoWay binding would not: TextBox.Text commits on
    // LostFocus in WinUI, so the list would only move once focus left the box.
    private void OnWatchSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) ViewModel.WatchSearch = box.Text ?? "";
    }

    private async void OnApplyWatchTimeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WatchTimeRowVm row }) return;
        try { await ViewModel.ApplyWatchTimeAsync(row); }
        catch (Exception ex) { GlobalLogger.Error("UserManagementView", "watch-time apply failed", ex); }
    }

    // The third sanctioned modal on this page, by the same test as the memory reset
    // and the queue clear: zeroing a viewer's watch time is irreversible — the hours
    // are gone and can only be re-earned in real time — and it can silently drop them
    // out of every watch-hour group rule at once. Not a repeatable rejection.
    // DefaultButton=Close so Enter cannot destroy.
    private async void OnResetWatchTimeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WatchTimeRowVm row }) return;
        try
        {
            var dialog = new ContentDialog
            {
                Title = Localizer.T("panel.usermgmt.watchtime.reset_confirm_title", "Reset this viewer's watch time?"),
                Content = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    Localizer.T("panel.usermgmt.watchtime.reset_confirm_body",
                        "{0} goes back to zero and loses any group they had earned through watch hours. This cannot be undone."),
                    row.Display),
                PrimaryButtonText = Localizer.T("panel.usermgmt.watchtime.reset_confirm_yes", "Reset to zero"),
                CloseButtonText = Localizer.T("panel.usermgmt.watchtime.reset_confirm_cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.ResetWatchTimeAsync(row);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("UserManagementView", "watch-time reset failed", ex);
        }
    }

    // ── Groups ──────────────────────────────────────────────────────────
    private void OnAddGroupClick(object sender, RoutedEventArgs e) => ViewModel.AddCustomGroup();

    private void OnDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupRowVm row })
            ViewModel.RemoveCustomGroup(row);
    }

    // Hand-rolled collapsible card (Expander removed after the hard crash at first
    // Minigames realization; house pattern per Loyalty/Giveaway/Timer). Both the
    // fixed and custom group templates name their root "GroupCard" and body
    // "GroupBody", so one toggle handler serves both lists.
    private void OnToggleGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GroupRowVm row } el) return;
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded && FindBodyElement(el, "GroupCard", "GroupBody") is { } body)
            AnimateExtensions.FadeSlideIn(body, slideY: 6, durationMs: 150);
    }

    // DataTemplate namescopes are per-realization, so FindName from the page is
    // unreliable here; instead ascend from the clicked button to this row's
    // card root, then descend to the named body — a bounded visual-tree walk
    // that always resolves the element belonging to THIS row instance.
    //
    // ★ This is why both group lists are still ItemsControls: ItemsRepeater
    // recycles containers, which makes an ascend-then-descend walk resolve
    // against whatever item the container currently holds.
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
}
