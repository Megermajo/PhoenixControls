using System;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Windows.ApplicationModel.DataTransfer;

namespace Phoenix.Controls.Hub.WinUI.Panels.LiveFeedPanel;

public sealed partial class LiveFeedView : UserControl, IDisposable,
    Phoenix.Controls.Hub.WinUI.Panels.Common.IPopOutSource,
    Phoenix.Controls.Hub.WinUI.Panels.Common.IPopOutAware
{
    public LiveFeedViewModel ViewModel { get; }
    private bool _disposed;

    // Guard against Loaded re-firing on tab / pop-out reparents
    // (panels detach + re-attach across pillar swaps, each cycle raises
    // Loaded again). Without this, FeedScrollViewer.ViewChanged accrues
    // duplicate subscriptions and the Dispose -= only removes one. Mirrors
    // ChatView's _loadedHandlerRan pattern.
    private bool _loadedHandlerRan;

    // Autoscroll machinery — mirrors SystemLogView's v5 direction-aware
    // pause + coalesced ChangeView. LiveFeed streams chat/sub/raid/visual
    // events throughout a session; without autoscroll the user has to
    // drag the scroll-thumb manually after every new entry. Hub UI + UX
    // sweeps converged on the same shape used in ChatView / SystemLogView.
    private const double BottomThresholdDips = 4.0;
    private bool _autoScrollPaused;
    private bool _scrollRequestPending;
    private double _prevVerticalOffset;

    // Hold the root-level wheel handler so Dispose can pair AddHandler with
    // RemoveHandler — parity with SystemLogView.
    private readonly PointerEventHandler _onRootWheelHandler;

    // HubWorkspaceView listens and opens a Window with a fresh panel instance.
    public event EventHandler? PopOutRequested;

    public LiveFeedView(LiveFeedViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // VM owns its dispatcher via ctor injection — no
        // shared static slot to capture from the View side anymore.
        ApplyLocalizedStrings();
        ViewModel.Rows.CollectionChanged += OnRowsChanged;
        Loaded += OnViewLoaded;
        // Mirror the SystemLogView pattern: ActualThemeChanged fires
        // on OS high-contrast engage, parent RequestedTheme override, or a
        // future in-app settings toggle.
        ActualThemeChanged += OnActualThemeChanged;

        // Hook wheel at UserControl root (handledEventsToo) — the inner
        // ItemsRepeater can claim PointerWheelChanged in some layouts so
        // we guarantee visibility by listening at the root. Matches the
        // SystemLogView pattern.
        _onRootWheelHandler = new PointerEventHandler(OnRootWheel);
        this.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _onRootWheelHandler,
            handledEventsToo: true);
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        // Guard against Loaded re-firing on tab/pop-out reparents.
        // Subscribing ViewChanged on every Loaded would accumulate duplicate
        // handlers and the Dispose -= only removes one.
        if (_loadedHandlerRan) return;
        _loadedHandlerRan = true;
        FeedScrollViewer.ViewChanged += OnFeedScrollViewChanged;
        RequestAutoScroll();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) RequestAutoScroll();
    }

    private void OnRootWheel(object sender, PointerRoutedEventArgs e)
    {
        if (FeedScrollViewer is null) return;
        var ptInSv = e.GetCurrentPoint(FeedScrollViewer);
        var pos = ptInSv.Position;
        if (pos.X < 0 || pos.Y < 0
            || pos.X > FeedScrollViewer.ActualWidth
            || pos.Y > FeedScrollViewer.ActualHeight)
            return;
        int delta = ptInSv.Properties.MouseWheelDelta;
        if (delta == 0) return;
        double dy = -delta * 50.0 / 120.0;
        double newOffset = FeedScrollViewer.VerticalOffset + dy;
        FeedScrollViewer.ChangeView(null, newOffset, null, disableAnimation: false);
        e.Handled = true;
    }

    private void OnFeedScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        var sv = FeedScrollViewer;
        // FeedScrollViewer can be null during control lifecycle transitions
        // (pop-out teardown, tab reparenting). OnRootWheel guards the same way.
        if (sv is null) return;
        double offset = sv.VerticalOffset;
        double scrollable = sv.ScrollableHeight;
        double distanceFromBottom = scrollable - offset;
        if (distanceFromBottom <= BottomThresholdDips)
            _autoScrollPaused = false;
        else if (offset < _prevVerticalOffset - 0.5)
            _autoScrollPaused = true;
        _prevVerticalOffset = offset;
    }

    private void RequestAutoScroll()
    {
        if (_autoScrollPaused) return;
        if (_scrollRequestPending) return;
        // Guard the dispatcher (null on a
        // pop-out/test host) and — critically — reset _scrollRequestPending if the
        // enqueue FAILS. The flag is otherwise only cleared inside the queued
        // callback; a failed TryEnqueue would leave it stuck true and permanently
        // gate every future autoscroll (the next OnRowsChanged early-returns on the
        // pending flag). Mirrors the defensive DispatcherQueue pattern in
        // OnActualThemeChanged.
        var dq = DispatcherQueue;
        if (dq is null) return;
        _scrollRequestPending = true;
        bool queued = dq.TryEnqueue(() =>
        {
            _scrollRequestPending = false;
            if (_autoScrollPaused) return;
            // The view can be torn down
            // (pop-out tear-down / tab swap) between enqueue and dispatch, leaving
            // FeedScrollViewer disposed-or-null. Bail before touching it so a
            // genuine teardown isn't routed through the catch-all below (which
            // exists for the "not realised yet" early-lifecycle path).
            if (_disposed || FeedScrollViewer is null) return;
            try
            {
                FeedScrollViewer.UpdateLayout();
                FeedScrollViewer.ChangeView(null, FeedScrollViewer.ScrollableHeight, null, true);
            }
            catch { /* not realised yet or shutting down */ }
        });
        if (!queued) _scrollRequestPending = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.Rows.CollectionChanged -= OnRowsChanged;
        Loaded -= OnViewLoaded;
        ActualThemeChanged -= OnActualThemeChanged;
        if (FeedScrollViewer is not null)
            FeedScrollViewer.ViewChanged -= OnFeedScrollViewChanged;
        try { this.RemoveHandler(UIElement.PointerWheelChangedEvent, _onRootWheelHandler); } catch { /* shutdown best-effort */ }
        ViewModel.Dispose();
    }

    /// <summary>
    /// Runtime theme-swap handler — marshal RefreshBrushes onto the
    /// UI thread (ActualThemeChanged usually fires there but stay defensive).
    /// </summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_disposed) return;
        var dq = DispatcherQueue;
        if (dq is null || dq.HasThreadAccess)
        {
            ViewModel.RefreshBrushes();
        }
        else
        {
            dq.TryEnqueue(() =>
            {
                if (_disposed) return;
                ViewModel.RefreshBrushes();
            });
        }
    }

    // ToggleButton.Click handlers route into the VM. The
    // chip's IsChecked is bound OneWay to ViewModel.Is*Selected so the
    // toggle state flows from VM truth on the way back, which keeps a
    // user double-clicking the same chip from toggling it OFF (the VM
    // never enters "no filter" — there's always a SelectedFilter).
    private void OnChipAllClick(object sender, RoutedEventArgs e)    => ViewModel.SelectAll();
    // OnChipChatClick retired alongside ChipChat — the source never
    // emits Kind=Chat by design (chat surfaces in the Chat panel).
    private void OnChipSubsClick(object sender, RoutedEventArgs e)   => ViewModel.SelectSubs();
    private void OnChipRaidsClick(object sender, RoutedEventArgs e)  => ViewModel.SelectRaids();
    private void OnChipVisualClick(object sender, RoutedEventArgs e) => ViewModel.SelectVisual();
    // Pair the new REDEEM + FOLLOW chips with VM selectors.
    private void OnChipRedeemClick(object sender, RoutedEventArgs e) => ViewModel.SelectRedeem();
    private void OnChipFollowClick(object sender, RoutedEventArgs e) => ViewModel.SelectFollow();
    // Errors chip handler. Mutually exclusive
    // with the other chips; the VM toggles via SelectErrors.
    private void OnChipErrorsClick(object sender, RoutedEventArgs e) => ViewModel.SelectErrors();

    /// <summary>
    /// Clear feed button. No confirmation dialog
    /// because the LiveFeed is non-destructive — every row reappears on
    /// restart from the source's persistent stream, and no on-disk data
    /// is touched. Mirrors SystemLog's confirm exception which only
    /// applies to destructive ops.
    /// </summary>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        try { ViewModel.ClearBuffer(); }
        catch (Exception ex) { GlobalLogger.Error("LiveFeedView", "Clear feed failed", ex); }
    }

    /// <summary>
    /// Row right-click menu. Copy row text
    /// (system clipboard) + "Filter to user: &lt;Who&gt;" / "Clear user
    /// filter" entries. Disabled-state on filter-to-user when the
    /// row has no Who value so the menu stays honest.
    /// </summary>
    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not LiveFeedRowVm row) return;
        var flyout = new MenuFlyout();

        // Copy — emits the same "[HH:mm:ss] Icon Who · Detail" shape the
        // ItemsRepeater renders so the user gets exactly what they see.
        var copy = new MenuFlyoutItem { Text = Localizer.T("panel.livefeed.menu.copy", "Copy") };
        copy.Click += (_, _) =>
        {
            try
            {
                var pkg = new DataPackage();
                string line = $"[{row.TimestampText}] {row.Icon} {row.Who} · {row.Detail}";
                pkg.SetText(line);
                Clipboard.SetContent(pkg);
            }
            catch (Exception ex) { GlobalLogger.Error("LiveFeedView", "Copy row failed", ex); }
        };
        flyout.Items.Add(copy);

        if (!string.IsNullOrEmpty(row.Who))
        {
            var filterToUser = new MenuFlyoutItem
            {
                Text = string.Format(Localizer.T("panel.livefeed.menu.filter_to_user", "Filter to user: {0}"), row.Who),
            };
            string capture = row.Who;
            filterToUser.Click += (_, _) => ViewModel.UserFilter = capture;
            flyout.Items.Add(filterToUser);
        }

        if (ViewModel.HasUserFilter)
        {
            var clearUser = new MenuFlyoutItem
            {
                Text = Localizer.T("panel.livefeed.menu.clear_user_filter", "Clear user filter"),
            };
            clearUser.Click += (_, _) => ViewModel.UserFilter = null;
            flyout.Items.Add(clearUser);
        }

        flyout.ShowAt(fe, e.GetPosition(fe));
        e.Handled = true;
    }

    private void OnPopOutClick(object sender, RoutedEventArgs e)
        => PopOutRequested?.Invoke(this, EventArgs.Empty);

    // Pop-out child dead-↗ fix. When this view is itself
    // the content of a pop-out window, hide the ↗ button so pop-out
    // spawning flows from the embedded workspace only. The workspace
    // allows arbitrary-depth fan-out, but
    // anchoring spawning to the embedded panel keeps PopOutStateStore
    // single-rooted on the workspace.
    public void MarkAsPopOutChild() => PopOutButton.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Resolves the filter-chip labels, the pop-out button tooltip, and the
    /// automation surface through <see cref="Localizer.T"/>. Called once in
    /// the ctor — Localizer doesn't expose a LanguageChanged event yet, so a
    /// language flip currently needs a restart (same as the rest of the suite).
    /// </summary>
    private void ApplyLocalizedStrings()
    {
        ChipAllLabel.Text    = Localizer.T("panel.livefeed.chip.all",    "ALL");
        // ChipChatLabel retired alongside the CHAT chip.
        ChipSubsLabel.Text   = Localizer.T("panel.livefeed.chip.subs",   "SUBS");
        ChipRaidsLabel.Text  = Localizer.T("panel.livefeed.chip.raids",  "RAIDS");
        ChipVisualLabel.Text = Localizer.T("panel.livefeed.chip.visual", "VISUAL");
        // New REDEEM + FOLLOW chip labels.
        ChipRedeemLabel.Text = Localizer.T("panel.livefeed.chip.redeem", "REDEEM");
        ChipFollowLabel.Text = Localizer.T("panel.livefeed.chip.follow", "FOLLOW");
        // Errors chip label.
        ChipErrorsLabel.Text = Localizer.T("panel.livefeed.chip.errors", "ERRORS");
        // Clear feed button label + tooltip.
        ClearLabel.Text = Localizer.T("panel.livefeed.button.clear", "clear");
        ToolTipService.SetToolTip(ClearButton,
            Localizer.T("panel.livefeed.button.clear.tooltip",
                "Clear the Live Feed panel buffer (does not delete on-disk logs)"));
        AutomationProperties.SetName(ClearButton,
            Localizer.T("panel.livefeed.button.clear.aria", "Clear Live Feed panel buffer"));

        AutomationProperties.SetName(ChipAll,    Localizer.T("panel.livefeed.chip.all.aria",    "Filter to all events"));
        AutomationProperties.SetName(ChipSubs,   Localizer.T("panel.livefeed.chip.subs.aria",   "Filter to subscription events"));
        AutomationProperties.SetName(ChipRaids,  Localizer.T("panel.livefeed.chip.raids.aria",  "Filter to raid events"));
        AutomationProperties.SetName(ChipVisual, Localizer.T("panel.livefeed.chip.visual.aria", "Filter to visual trigger events"));
        AutomationProperties.SetName(ChipRedeem, Localizer.T("panel.livefeed.chip.redeem.aria", "Filter to channel point redemption events"));
        AutomationProperties.SetName(ChipFollow, Localizer.T("panel.livefeed.chip.follow.aria", "Filter to follow events"));
        AutomationProperties.SetName(ChipErrors, Localizer.T("panel.livefeed.chip.errors.aria", "Filter to critical-error events"));

        // Visible "pop-out" label next to the
        // icon (matches SystemLogView pattern).
        PopOutLabel.Text = Localizer.T("panel.common.button.popout", "pop-out");
        ToolTipService.SetToolTip(PopOutButton,
            Localizer.T("panel.common.popout.tooltip", "Pop-out"));
        AutomationProperties.SetName(PopOutButton,
            Localizer.T("panel.livefeed.popout.aria", "Pop out Live Feed panel"));

        AutomationProperties.SetName(this,
            Localizer.T("panel.livefeed.aria.name", "Live Feed panel"));
        AutomationProperties.SetHelpText(this,
            Localizer.T("panel.livefeed.aria.help",
                "Recent Twitch events — chat, subscriptions, raids, redemptions, and visual triggers. Use the filter chips in the header to narrow the feed."));

        EmptyStateText.Text = Localizer.T("panel.livefeed.empty_state",
            "Waiting for stream activity…");
    }
}
