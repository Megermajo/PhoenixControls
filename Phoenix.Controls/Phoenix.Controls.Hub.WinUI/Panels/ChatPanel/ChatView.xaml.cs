using System;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Windows.System;

namespace Phoenix.Controls.Hub.WinUI.Panels.ChatPanel;

public sealed partial class ChatView : UserControl, IDisposable,
    Phoenix.Controls.Hub.WinUI.Panels.Common.IPopOutSource,
    Phoenix.Controls.Hub.WinUI.Panels.Common.IPopOutAware
{
    public ChatViewModel ViewModel { get; }
    private bool _disposed;

    // Within this many DIPs of the bottom we treat the user as "at bottom"
    // and re-arm autoscroll. Mirrors the SystemLog pattern so all three
    // scrolling panels share one mental model. See SystemLogView.cs for
    // the longer rationale. Hub UI/UX sweeps converged on the v5 direction-
    // aware pause + coalesced ChangeView shape originally proven in
    // SystemLogView.
    private const double BottomThresholdDips = 4.0;

    private bool _autoScrollPaused;
    private bool _scrollRequestPending;
    private double _prevVerticalOffset;
    // [P2 fix] _prevVerticalOffset starts at 0.0; without this guard the first
    // non-intermediate ViewChanged compares the live offset against that stale
    // seed and can falsely pause autoscroll on initial load. Mirrors the
    // SystemLogView QC11-08 pattern.
    private bool _prevOffsetInitialized;

    // QC10-02/03 — the ScrollViewer.ViewChanged subscription is declared in
    // ChatView.xaml ("ViewChanged=\"OnChatScrollViewChanged\"") and is the
    // single source of truth. The Loaded hook below ONLY arms the first
    // autoscroll once the ScrollViewer's ScrollableHeight is realised; it
    // does NOT re-subscribe ViewChanged (used to do +=, which made the
    // handler fire twice and the matching Dispose -= only removed one).
    // _loadedHandlerRan guards against Loaded re-firing on tab/pop-out
    // reparents (panels are detached + re-attached across pillar swaps,
    // each cycle raises Loaded again).
    private bool _loadedHandlerRan;

    // Hold the root-level wheel handler so Dispose can pair AddHandler with
    // RemoveHandler — perf-review M23 parity with SystemLogView.
    private readonly PointerEventHandler _onRootWheelHandler;

    public event EventHandler? PopOutRequested;

    public ChatView(ChatViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // C1 (2026-05-14): VM owns its dispatcher via ctor injection — no
        // shared static slot to capture from the View side anymore.
        ApplyLocalizedStrings();
        ViewModel.Rows.CollectionChanged += OnRowsChanged;
        Loaded += OnViewLoaded;
        //  Mirror the SystemLogView pattern: ActualThemeChanged fires
        // on OS high-contrast engage, parent RequestedTheme override, or a
        // future in-app settings toggle. Without this subscription buffered
        // rows stay coloured by the *old* merged theme until they scroll out.
        ActualThemeChanged += OnActualThemeChanged;

        // Hook wheel at UserControl root (handledEventsToo) — matches the
        // SystemLogView pattern so wheel events caught by inner controls
        // still drive the scroller.
        _onRootWheelHandler = new PointerEventHandler(OnRootWheel);
        this.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _onRootWheelHandler,
            handledEventsToo: true);
    }

    /// <summary>
    ///  Runtime theme-swap handler — marshal RefreshBrushes onto the
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
            // [Hub panel audit 2026-05-31, Lane D P3] Surface a dropped brush refresh
            // if the enqueue fails, rather than a silent no-repaint on theme change.
            if (!dq.TryEnqueue(() =>
            {
                if (_disposed) return;
                ViewModel.RefreshBrushes();
            }))
            {
                GlobalLogger.Log("ChatView: failed to queue RefreshBrushes on theme change.", "ChatView", Phoenix.Controls.Shared.Models.LogLevel.Debug);
            }
        }
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        // Guard against Loaded re-firing on tab/pop-out reparents — the XAML
        // ViewChanged attribute already wired ChatScrollViewer.ViewChanged, so
        // the only reason this handler exists is the initial autoscroll arm.
        if (_loadedHandlerRan) return;
        _loadedHandlerRan = true;
        // [P2 fix] Seed the previous-offset baseline from the realised
        // ScrollViewer so the first ViewChanged compares against a real value,
        // not the 0.0 field default. Matches SystemLogView's QC11-08 guard.
        if (ChatScrollViewer is not null)
        {
            _prevVerticalOffset = ChatScrollViewer.VerticalOffset;
            _prevOffsetInitialized = true;
        }
        RequestAutoScroll();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.Rows.CollectionChanged -= OnRowsChanged;
        Loaded -= OnViewLoaded;
        ActualThemeChanged -= OnActualThemeChanged;
        // QC10-02/03 — XAML ViewChanged="OnChatScrollViewChanged" tears down
        // with the visual tree, so no explicit -= needed here. Leaving it
        // would only matter if we kept the second (code-behind) subscription.
        // [Hub panel audit 2026-05-31, Lane D P2] Log instead of silently swallowing —
        // a RemoveHandler throw here would otherwise hide a shutdown-path defect.
        try { this.RemoveHandler(UIElement.PointerWheelChangedEvent, _onRootWheelHandler); }
        catch (Exception ex) { GlobalLogger.Log($"ChatView RemoveHandler failed during Dispose: {ex.Message}", "ChatView", Phoenix.Controls.Shared.Models.LogLevel.Debug); }
        ViewModel.Dispose();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            RequestAutoScroll();
    }

    private void OnRootWheel(object sender, PointerRoutedEventArgs e)
    {
        if (ChatScrollViewer is null) return;
        var ptInSv = e.GetCurrentPoint(ChatScrollViewer);
        var pos = ptInSv.Position;
        if (pos.X < 0 || pos.Y < 0
            || pos.X > ChatScrollViewer.ActualWidth
            || pos.Y > ChatScrollViewer.ActualHeight)
            return;
        int delta = ptInSv.Properties.MouseWheelDelta;
        if (delta == 0) return;
        double dy = -delta * 50.0 / 120.0;
        double newOffset = ChatScrollViewer.VerticalOffset + dy;
        ChatScrollViewer.ChangeView(null, newOffset, null, disableAnimation: false);
        e.Handled = true;
    }

    private void OnChatScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        var sv = ChatScrollViewer;
        if (sv is null) return;

        double offset             = sv.VerticalOffset;
        double scrollable         = sv.ScrollableHeight;
        double distanceFromBottom = scrollable - offset;

        // [P2 fix] Only run the direction-aware pause test once we have a real
        // previous offset to compare against — otherwise the first ViewChanged
        // measures against the 0.0 default and can falsely pause autoscroll.
        if (distanceFromBottom <= BottomThresholdDips)
            _autoScrollPaused = false;
        else if (_prevOffsetInitialized && offset < _prevVerticalOffset - 0.5)
            _autoScrollPaused = true;

        _prevVerticalOffset = offset;
        _prevOffsetInitialized = true;
    }

    /// <summary>
    /// Coalesce autoscroll requests across a burst of message arrivals —
    /// raids and chat spam fire many CollectionChanged events in the same
    /// dispatcher tick; we want one ChangeView, not N.
    /// </summary>
    private void RequestAutoScroll()
    {
        if (_autoScrollPaused) return;
        if (_scrollRequestPending) return;
        // [Hub panel audit 2026-05-31, Lane D P1] Guard the dispatcher (null on a
        // pop-out / test host) and reset _scrollRequestPending if the enqueue fails.
        // The flag is otherwise only cleared inside the queued callback, so a failed
        // TryEnqueue would leave it stuck true and permanently gate every future
        // autoscroll. Mirrors the defensive pattern in OnActualThemeChanged.
        var dq = DispatcherQueue;
        if (dq is null) return;
        _scrollRequestPending = true;
        bool queued = dq.TryEnqueue(() =>
        {
            _scrollRequestPending = false;
            if (_autoScrollPaused) return;
            // [P2 fix] The control tree can detach during WinUI 3 reparenting
            // (tab/pop-out swap), nulling ChatScrollViewer between the enqueue and
            // this callback. Make that explicit instead of relying on the bare
            // catch to swallow the resulting NullReferenceException.
            if (ChatScrollViewer is null) return;
            try
            {
                ChatScrollViewer.UpdateLayout();
                ChatScrollViewer.ChangeView(null, ChatScrollViewer.ScrollableHeight, null, disableAnimation: true);
            }
            catch
            {
                /* ScrollViewer not realised yet, or view tearing down. */
            }
        });
        if (!queued) _scrollRequestPending = false;
    }

    // SendDraftAsync ultimately calls IChatSource.SendAsBotAsync (a network
    // hop to Streamer.bot/Twitch) — that can throw on transient failures.
    // An async-void handler that lets the exception escape would crash the
    // dispatcher, so route faults through GlobalLogger instead.
    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SendDraftAsync(); }
        catch (Exception ex) { GlobalLogger.Error("ChatView", "Send failed", ex); }
    }

    private async void OnDraftKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try { await ViewModel.SendDraftAsync(); }
            catch (Exception ex) { GlobalLogger.Error("ChatView", "Send failed (Enter)", ex); }
        }
    }

    private void OnPopOutClick(object sender, RoutedEventArgs e)
        => PopOutRequested?.Invoke(this, EventArgs.Empty);

    // Hub UI sweep P2 — pop-out child dead-↗ fix. See LiveFeedView.MarkAsPopOutChild.
    public void MarkAsPopOutChild() => PopOutButton.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Resolves composer placeholder, Send button label, pop-out chrome, and
    /// automation strings through <see cref="Localizer.T"/>. Called once in
    /// the ctor (Hub UI sweep — localization batch).
    /// </summary>
    private void ApplyLocalizedStrings()
    {
        DraftBox.PlaceholderText = Localizer.T("panel.chat.composer.placeholder", "Send as bot…");
        SendButton.Content       = Localizer.T("panel.chat.button.send",          "SEND");

        // Hub UI sweep 2026-05-22 — visible "pop-out" label next to the icon.
        PopOutLabel.Text = Localizer.T("panel.common.button.popout", "pop-out");
        ToolTipService.SetToolTip(PopOutButton,
            Localizer.T("panel.common.popout.tooltip", "Pop-out"));
        AutomationProperties.SetName(PopOutButton,
            Localizer.T("panel.chat.popout.aria", "Pop out Chat panel"));

        AutomationProperties.SetName(this,
            Localizer.T("panel.chat.aria.name", "Chat panel"));
        AutomationProperties.SetHelpText(this,
            Localizer.T("panel.chat.aria.help",
                "Twitch chat messages with role badges. Type in the composer at the bottom and press Enter or Send to message the channel as the configured bot account."));
    }
}
