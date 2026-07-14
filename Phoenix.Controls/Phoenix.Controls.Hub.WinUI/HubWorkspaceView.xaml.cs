using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Phoenix.Controls.Hub.WinUI.Panels.ChatPanel;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Hub.WinUI.Panels.EventLogPanel;
using Phoenix.Controls.Hub.WinUI.Panels.LiveFeedPanel;
using Phoenix.Controls.Hub.WinUI.Panels.ScriptPanel;
using Phoenix.Controls.Hub.WinUI.Panels.SystemLogPanel;
using Phoenix.Controls.Hub.WinUI.Panels.WebhookPanel;
using Phoenix.Controls.Hub.WinUI.Services;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI;

// Hub's panel workspace — extracted from MainWindow.xaml so the
// MainWindow.MainPaneRegion can swap between this and the embedded
// Architect / Visualist MainView UserControls (TODO.md P0 #3).
public sealed partial class HubWorkspaceView : UserControl, IDisposable
{
    private bool _disposed;

    // Track each open pop-out alongside its kind so the close path can
    // snapshot geometry for next-launch restore. Multiple pop-outs of the
    // same kind are allowed — every ↗ click opens a new window, and the
    // source-side multicast event delivers rows to every subscriber.
    private sealed class PopOutEntry
    {
        public Window Window = default!;
        public UserControl ChildPanel = default!;
        public PopOutStateStore.PopOutKind Kind;
    }

    private readonly List<PopOutEntry> _popOuts = new();

    // Per-region popout count — when at least one popout of a kind is open,
    // its source region in the Hub workspace is collapsed so the user sees
    // exactly one rendering of that panel, not two identical streams. The
    // count tracks fan-out (multiple popouts of the same kind are allowed);
    // the region restores when the LAST popout of that kind closes.
    private readonly Dictionary<PopOutStateStore.PopOutKind, int> _popOutCountByKind = new();

    // Per-panel handler refs so PopOutRequested can be unsubscribed on
    // Dispose. Pre-fix WirePopOut used a lambda `panel.PopOutRequested
    // += (_, _) => OpenPopOut(...)` which captured kind/title/this and
    // could never be `-=`'d — if SetPanels were ever called twice
    // (parent reload, future test rig) the handlers would double-fire
    // and IPopOutSource's documented single-subscriber rule would
    // silently break.
    private readonly Dictionary<IPopOutSource, EventHandler> _popOutHandlers = new();

    private IHubServices? _services;
    private IPanelFactory? _factory;

    // Parent panel references — kept so the close path can dispose them.
    // These once doubled as the primary-subscriber handoff
    // targets; that gate is gone, but the refs are still needed for the
    // workspace's own teardown (Dispose disposes each embedded panel).
    private LiveFeedView?  _liveFeed;
    private ChatView?      _chat;
    private ScriptView?    _script;
    private SystemLogView? _systemLog;

    public HubWorkspaceView()
    {
        InitializeComponent();
        // workspace.hint_strip exists in the lang bundles already; the prior
        // XAML literal carried the leading "↗" emoji glyph which was
        // replaced with prose so the icon-pack consolidation can choose
        // its own visual affordance later without re-translating four bundles.
        HintStripText.Text = Localizer.T("workspace.hint_strip",
            "each panel pops into its own window  ·  scripts hot-reload on file change  ·  errors logged · execution continues");
    }

    /// <summary>
    /// Populates the four panels and the status strip. Called by Hub.MainWindow
    /// after HubBootstrapper.BootAsync returns. Wires each panel's
    /// PopOutRequested event so the chrome's ↗ button opens a fresh panel
    /// instance bound to the same IHubServices in its own Window
    /// (TODO P1 #2).
    /// </summary>
    public void SetPanels(IHubServices services, IPanelFactory factory)
    {
        _services = services;
        _factory  = factory;

        // Factory fallback. Without these guards a single
        // panel-construction fault (DB lookup throws, a service
        // bootstrap fails mid-construction) wipes the entire workspace:
        // the throw bubbles out of SetPanels, Hub.MainWindow catches it
        // at the bootstrapper level, and the user is left with an empty
        // pillar tab. Building each region in its own try/catch lets a
        // failed panel collapse to a placeholder while the rest of the
        // workspace renders normally.
        UserControl? statusStrip = SafeCreate(
            () => factory.CreateStatusStrip(services),
            failureLabel: "Status Strip",
            failureKey: "panel.failure.statusstrip");
        StatusStripRegion.Content = statusStrip
            ?? BuildPlaceholderPanel("Status Strip", "panel.failure.statusstrip");

        _liveFeed  = SafeCreate(() => (LiveFeedView)  factory.CreateLiveFeedPanel(services),  "Live Feed",  "panel.failure.livefeed");
        _chat      = SafeCreate(() => (ChatView)      factory.CreateChatPanel(services),      "Chat",       "panel.failure.chat");
        _script    = SafeCreate(() => (ScriptView)    factory.CreateScriptPanel(services),    "Scripts",    "panel.failure.scripts");
        _systemLog = SafeCreate(() => (SystemLogView) factory.CreateSystemLogPanel(services), "System Log", "panel.failure.systemlog");

        LiveFeedRegion.Content  = (UserControl?)_liveFeed  ?? BuildPlaceholderPanel("Live Feed",  "panel.failure.livefeed");
        ChatRegion.Content      = (UserControl?)_chat      ?? BuildPlaceholderPanel("Chat",       "panel.failure.chat");
        ScriptRegion.Content    = (UserControl?)_script    ?? BuildPlaceholderPanel("Scripts",    "panel.failure.scripts");
        SystemLogRegion.Content = (UserControl?)_systemLog ?? BuildPlaceholderPanel("System Log", "panel.failure.systemlog");

        // Pop-out wiring is panel-specific: a panel that fell back to a
        // placeholder has no PopOutRequested event to subscribe to, so
        // skip it. The placeholder itself is non-interactive.
        if (_liveFeed  is not null) WirePopOut(_liveFeed,  PopOutStateStore.PopOutKind.LiveFeed,  "popout.title.livefeed",  "Live Feed");
        if (_chat      is not null) WirePopOut(_chat,      PopOutStateStore.PopOutKind.Chat,      "popout.title.chat",      "Chat");
        if (_script    is not null) WirePopOut(_script,    PopOutStateStore.PopOutKind.Scripts,   "popout.title.scripts",   "Scripts");
        if (_systemLog is not null) WirePopOut(_systemLog, PopOutStateStore.PopOutKind.SystemLog, "popout.title.systemlog", "System Log");

        // Restore last session's pop-outs. Every restored window is just
        // another subscriber; no primary handoff is required, so this can
        // run in any order.
        RestoreSavedPopOuts();

        // Short staggered entrance so the four headline panels slide
        // into place when the workspace first populates, rather than snapping
        // in. Mirrors the pop-out window's existing open fade.
        PlayWorkspaceEntrance();
    }

    private static T? SafeCreate<T>(Func<T> factory, string failureLabel, string failureKey) where T : class
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubWorkspaceView",
                $"Failed to construct {failureLabel} panel — showing placeholder.", ex);
            return null;
        }
    }

    /// <summary>
    /// Renders an inert placeholder panel when a factory call faulted.
    /// Uses the same outer Border chrome the headline panels use so the
    /// workspace's grid geometry doesn't collapse around the dead cell.
    /// The text flows through Localizer so DE/FR/ES bundles can replace
    /// the verbatim English fallback later.
    /// </summary>
    private static UserControl BuildPlaceholderPanel(string panelLabel, string localizationKey)
    {
        var res = Application.Current?.Resources;
        Brush surface = res?.TryGetValue("CoalSurfaceBrush", out var s) == true && s is Brush sb ? sb : new SolidColorBrush(Microsoft.UI.Colors.Black);
        Brush divider = res?.TryGetValue("CoalDividerBrush", out var d) == true && d is Brush db ? db : new SolidColorBrush(Microsoft.UI.Colors.DimGray);
        Brush textErr = res?.TryGetValue("ErrBrush",         out var e) == true && e is Brush eb ? eb : new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        Brush textBody = res?.TryGetValue("CoalSecondaryTextBrush", out var b) == true && b is Brush bb ? bb : new SolidColorBrush(Microsoft.UI.Colors.Silver);

        string heading = Localizer.T("panel.failure.heading",
            "{0} couldn't be loaded.");
        string detail = Localizer.T(localizationKey,
            "See the System Log for details. Restart Hub once the underlying issue is resolved.");

        var headingText = new TextBlock
        {
            Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, heading, panelLabel),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = textErr,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var detailText = new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Foreground = textBody,
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360,
        };
        stack.Children.Add(headingText);
        stack.Children.Add(detailText);

        var border = new Border
        {
            Background = surface,
            BorderBrush = divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(24),
            Child = stack,
        };
        return new UserControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment   = VerticalAlignment.Stretch,
            Content = border,
        };
    }

    private void WirePopOut(IPopOutSource panel, PopOutStateStore.PopOutKind kind, string titleKey, string fallbackTitle)
    {
        // Detach any prior handler for this panel before
        // attaching a new one. Idempotent against future re-entry into
        // SetPanels (theme reload, hot-restart) — a future second wire-up
        // would otherwise leave both delegates on PopOutRequested and fire
        // twice per click.
        if (_popOutHandlers.TryGetValue(panel, out var existingHandler))
        {
            panel.PopOutRequested -= existingHandler;
        }

        // Named local function so we can `-=` in Dispose. Every ↗ click
        // opens a NEW window — the previous single-instance gate that
        // foregrounded an existing pop-out has been removed. Fan-out is
        // source-side: each new VM subscribes to the source's multicast
        // event and receives every row.
        void Handler(object? sender, EventArgs e)
        {
            OpenPopOut(kind, titleKey, fallbackTitle, geometry: null);
        }
        _popOutHandlers[panel] = Handler;
        panel.PopOutRequested += Handler;
    }

    private void UnwirePopOuts()
    {
        foreach (var (panel, handler) in _popOutHandlers)
        {
            try { panel.PopOutRequested -= handler; } catch { /* shutdown best-effort */ }
        }
        _popOutHandlers.Clear();
    }

    /// <summary>
    /// Opens a fresh panel instance in its own Window. <paramref name="geometry"/>
    /// is non-null only on session-restore — a user-driven pop-out lets WinUI
    /// place the window at its default position.
    /// </summary>
    private void OpenPopOut(
        PopOutStateStore.PopOutKind kind,
        string titleKey,
        string fallbackTitle,
        PopOutStateStore.PopOutRecord? geometry)
    {
        if (_services is null || _factory is null) return;

        // Multiple pop-outs of the same kind are allowed. Each new child VM
        // subscribes to the source's multicast event and renders rows
        // independently; no primary-subscriber handoff is needed (the gate
        // was deleted in this change).
        UserControl content = kind switch
        {
            PopOutStateStore.PopOutKind.LiveFeed  => _factory.CreateLiveFeedPanel(_services),
            PopOutStateStore.PopOutKind.Chat      => _factory.CreateChatPanel(_services),
            PopOutStateStore.PopOutKind.Scripts   => _factory.CreateScriptPanel(_services),
            PopOutStateStore.PopOutKind.SystemLog => _factory.CreateSystemLogPanel(_services),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown pop-out kind."),
        };

        var window = PopOutWindowFactory.Create(content, titleKey, fallbackTitle);

        var entry = new PopOutEntry
        {
            Window = window,
            ChildPanel = content,
            Kind = kind,
        };
        _popOuts.Add(entry);

        // Collapse the embedded source region on the first popout of this
        // kind so the user doesn't see two identical streams side-by-side.
        // Restored when the last popout of the same kind closes.
        SetRegionVisibility(kind, popOutOpen: true);

        window.Closed += (_, _) =>
        {
            _popOuts.Remove(entry);
            SetRegionVisibility(kind, popOutOpen: false);
            // Disposing the child panel disposes the child VM, which
            // unsubscribes from the source's multicast event. Other
            // surviving subscribers (the embedded parent + any sibling
            // pop-outs) keep receiving rows.
            if (content is IDisposable d) d.Dispose();
        };

        // Apply saved geometry BEFORE Activate so a session-restore
        // doesn't flash the window at WinUI's default position then snap to
        // the saved rect. AppWindow is resolvable from the WindowId via
        // Win32Interop as soon as the Window has been constructed — Activate
        // is not required for MoveAndResize to take effect on WindowsAppSDK
        // 1.5+, contrary to the older comment that lived here.
        if (geometry is not null)
            PopOutStateStore.ApplyToWindow(window, geometry);

        // Subscribe the auto-save plumbing BEFORE Activate so the
        // first AppWindow.Changed (from Activate's initial layout pass) is
        // a no-op against the debounce, and any user drag/resize from this
        // point forward flushes to disk 1s after the gesture ends. Final
        // save on Close is also handled by Track().
        PopOutStateStore.Track(window, kind);

        WindowFront.Show(window);
    }

    // EventLog pop-outs live alongside the PopOutKind-tracked windows but
    // are NOT persisted by PopOutStateStore (the enum is closed; EventLog
    // is a one-off rather than one of the four headline panels). Tracking
    // is kept here so the workspace's Dispose can still close every EventLog
    // pop-out at shutdown without leaking the panel's polling DispatcherTimer.
    private readonly List<Window> _eventLogPopOuts = new();

    // Webhook pop-outs use the same shape as the EventLog pop-outs above
    // (out-of-band of the PopOutKind enum + state-restore plumbing). Tracking
    // lives here so Dispose closes every still-open pop-out and unsubscribes
    // its VM from HUDServer.OnWebhookFired.
    private readonly List<Window> _webhookPopOuts = new();

    /// <summary>
    /// Spawn a fresh EventLog tail panel in its own pop-out window.
    /// Reachable via Hub's View → Event Log menu (no fixed slot in the
    /// 4-pane workspace grid). Each click opens a new window; multiple
    /// instances are allowed because each VM owns its own polling timer
    /// (same fan-out semantic as the headline pop-outs).
    /// </summary>
    /// <summary>
    /// Spawn the Webhook tail panel in its own pop-out window. Reachable via
    /// Hub's View → Recent Webhooks menu. Same shape as <see cref="OpenEventLogPopOut"/>:
    /// each click opens a fresh window, the panel VM owns its own
    /// HUDServer.OnWebhookFired subscription, Dispose unhooks it.
    /// </summary>
    public void OpenWebhookPopOut()
    {
        if (_services is null || _factory is null) return;
        try
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var vm = new WebhookPanelViewModel(dispatcher);
            var view = new WebhookView(vm);
            var window = PopOutWindowFactory.Create(view, "popout.title.webhook", "Recent Webhooks");

            _webhookPopOuts.Add(window);

            // Disposing the view disposes the VM, which unsubscribes from
            // HUDServer.OnWebhookFired. Without this the VM would keep
            // receiving fires (and adding them to a now-orphan buffer)
            // until process exit.
            window.Closed += (_, _) =>
            {
                _webhookPopOuts.Remove(window);
                try { view.Dispose(); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("HubWorkspaceView", "Webhook pop-out dispose failed", ex);
                }
            };

            WindowFront.Show(window);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubWorkspaceView", "OpenWebhookPopOut failed", ex);
        }
    }

    public void OpenEventLogPopOut()
    {
        if (_services is null || _factory is null) return;
        try
        {
            // Capture the dispatcher on the calling (UI) thread so the
            // panel VM marshals refresh-merge back to the right queue.
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var vm = new EventLogPanelViewModel(dispatcher);
            var view = new EventLogView(vm);
            var window = PopOutWindowFactory.Create(view, "popout.title.eventlog", "Event Log");

            _eventLogPopOuts.Add(window);

            // Disposing the view disposes the VM, which stops the polling
            // DispatcherTimer. Failing to dispose would leave the 2 Hz
            // timer firing against the (now-orphaned) AppData DB long
            // after the window closes.
            window.Closed += (_, _) =>
            {
                _eventLogPopOuts.Remove(window);
                try { view.Dispose(); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("HubWorkspaceView", "EventLog pop-out dispose failed", ex);
                }
            };

            WindowFront.Show(window);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("HubWorkspaceView", "OpenEventLogPopOut failed", ex);
        }
    }

    private void RestoreSavedPopOuts()
    {
        var saved = PopOutStateStore.Load();
        if (saved.Count == 0) return;

        foreach (var rec in saved)
        {
            if (!Enum.TryParse<PopOutStateStore.PopOutKind>(rec.Kind, ignoreCase: true, out var kind))
                continue;
            (string key, string fallback) = kind switch
            {
                PopOutStateStore.PopOutKind.LiveFeed  => ("popout.title.livefeed",  "Live Feed"),
                PopOutStateStore.PopOutKind.Chat      => ("popout.title.chat",      "Chat"),
                PopOutStateStore.PopOutKind.Scripts   => ("popout.title.scripts",   "Scripts"),
                PopOutStateStore.PopOutKind.SystemLog => ("popout.title.systemlog", "System Log"),
                _ => (string.Empty, kind.ToString()),
            };
            OpenPopOut(kind, key, fallback, rec);
        }
    }

    /// <summary>
    /// Snapshot the currently-open pop-outs to disk so the next
    /// launch can restore them. Called from MainWindow.Closed (via Dispose).
    /// Post : PopOutStateStore.Track auto-writes on every
    /// geometry change and on each Window.Closed, so this is a final
    /// safety flush against the scenario where the user resizes a pop-out
    /// and then exits Hub before the debounce timer ticks.
    /// </summary>
    private void PersistOpenPopOuts() => PopOutStateStore.FlushNow();

    /// <summary>
    /// Disposes the panel views (and their VMs, which unsubscribe from source
    /// events). Called from MainWindow.Closed at app shutdown so the
    /// Hub-source backends in HubServices can be torn down without leaving
    /// orphan event subscriptions behind. Idempotent — safe to call once
    /// from the window's Closed handler regardless of whether SetPanels ran.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe PopOutRequested handlers before disposing the
        // panels — without this the panel VM still holds a strong
        // delegate ref to a handler that closes over `this` (the
        // workspace), which keeps the workspace alive past Dispose for
        // GC purposes.
        UnwirePopOuts();

        // Capture pop-out geometry BEFORE closing the windows.
        // Window.Close() invalidates AppWindow.Position/Size on some WinUI 3
        // builds, so the capture has to run while the windows are still live.
        PersistOpenPopOuts();

        // Close every still-open pop-out window before tearing down the
        // workspace's own panels — closing a Window fires its Closed
        // handler which disposes the pop-out's panel + VM.
        foreach (var entry in _popOuts.ToArray())
        {
            try { entry.Window.Close(); }
            catch { /* shutdown best-effort */ }
        }
        _popOuts.Clear();

        // Close out EventLog pop-outs the same way. Their VMs own a
        // DispatcherTimer; failing to close them would leak both the
        // timer and a strong reference back into the panel VM until
        // process exit.
        foreach (var window in _eventLogPopOuts.ToArray())
        {
            try { window.Close(); }
            catch { /* shutdown best-effort */ }
        }
        _eventLogPopOuts.Clear();

        // Close out Webhook pop-outs alongside the EventLog ones.
        // Their VMs hold a HUDServer.OnWebhookFired subscription; closing
        // the window invokes view.Dispose(), which removes the handler.
        foreach (var window in _webhookPopOuts.ToArray())
        {
            try { window.Close(); }
            catch { /* shutdown best-effort */ }
        }
        _webhookPopOuts.Clear();

        DisposeRegion(StatusStripRegion);
        DisposeRegion(LiveFeedRegion);
        DisposeRegion(ChatRegion);
        DisposeRegion(ScriptRegion);
        DisposeRegion(SystemLogRegion);
    }

    private static void DisposeRegion(ContentControl region)
    {
        if (region.Content is IDisposable d) d.Dispose();
    }

    // ─── Short panel entrance animation ───────────────────────────────────
    // A panel "opens" in the Hub workspace two ways: the four headline panels
    // appear when the workspace first populates, and a panel region restores to
    // the grid when its last pop-out closes. Both get a short fade + slight
    // upward slide (~170 ms ease-out) so the panel reads as settling into place
    // rather than snapping in — mirrors the pop-out window's existing open fade
    // (PopOutWindowFactory.ApplyOpenFadeIn). Opacity + RenderTransform animate
    // off the layout path, so this stays cheap.
    private const double PanelEntranceMs = 170.0;

    private void PlayWorkspaceEntrance()
    {
        (ContentControl Region, double Delay)[] regions =
        {
            (LiveFeedRegion,  0),
            (ChatRegion,      35),
            (SystemLogRegion, 70),
            (ScriptRegion,    105),
        };
        foreach (var (region, delay) in regions)
        {
            if (region is { Visibility: Visibility.Visible, Content: not null })
                AnimatePanelRegionIn(region, delay);
        }
    }

    private static void AnimatePanelRegionIn(FrameworkElement region, double delayMs = 0)
    {
        if (region is null) return;
        try
        {
            var translate = new TranslateTransform { Y = 8 };
            region.RenderTransform = translate;
            region.Opacity = 0;

            var fade = new DoubleAnimation
            {
                From = 0, To = 1,
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                Duration = TimeSpan.FromMilliseconds(PanelEntranceMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, region);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var slide = new DoubleAnimation
            {
                From = 8, To = 0,
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                Duration = TimeSpan.FromMilliseconds(PanelEntranceMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(slide, translate);
            Storyboard.SetTargetProperty(slide, "Y");

            var sb = new Storyboard();
            sb.Children.Add(fade);
            sb.Children.Add(slide);
            sb.Completed += (_, _) =>
            {
                // Land cleanly — clear the transform so it can't interfere with
                // later layout, and pin opacity in case the storyboard was cut.
                // try/catch: a pop-out-return region could be torn down again
                // (rapid pop-out churn) before this fires.
                try { region.Opacity = 1; region.RenderTransform = null; }
                catch { /* region detached — nothing to restore */ }
            };
            sb.Begin();
        }
        catch
        {
            // Decorative — if the tree isn't ready, just show the region.
            region.Opacity = 1;
            region.RenderTransform = null;
        }
    }

    /// <summary>
    /// Collapse the embedded source region for <paramref name="kind"/> when at
    /// least one popout of that kind is open; restore it when all popouts have
    /// closed. Eliminates the duplicate-render confusion of seeing the same
    /// panel content in two places simultaneously while preserving the
    /// multi-popout contract.
    /// </summary>
    private void SetRegionVisibility(PopOutStateStore.PopOutKind kind, bool popOutOpen)
    {
        int count = _popOutCountByKind.TryGetValue(kind, out var c) ? c : 0;
        count += popOutOpen ? 1 : -1;
        if (count < 0) count = 0;
        _popOutCountByKind[kind] = count;

        ContentControl? region = kind switch
        {
            PopOutStateStore.PopOutKind.LiveFeed  => LiveFeedRegion,
            PopOutStateStore.PopOutKind.Chat      => ChatRegion,
            PopOutStateStore.PopOutKind.Scripts   => ScriptRegion,
            PopOutStateStore.PopOutKind.SystemLog => SystemLogRegion,
            _ => null,
        };
        if (region is null) return;
        bool wasVisible = region.Visibility == Visibility.Visible;
        bool show = count == 0;
        region.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // When the last pop-out of this kind closes the panel returns to
        // the grid — fade + slide it back in rather than snapping.
        if (show && !wasVisible) AnimatePanelRegionIn(region);
    }
}
