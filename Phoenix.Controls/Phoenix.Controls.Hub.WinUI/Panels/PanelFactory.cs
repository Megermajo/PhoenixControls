using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.WinUI.Panels.ChatPanel;
using Phoenix.Controls.Hub.WinUI.Panels.LiveFeedPanel;
using Phoenix.Controls.Hub.WinUI.Panels.ScriptPanel;
using Phoenix.Controls.Hub.WinUI.Panels.StatusStrip;
using Phoenix.Controls.Hub.WinUI.Panels.SystemLogPanel;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels;

public sealed class PanelFactory : IPanelFactory
{
    // C1 (2026-05-14): each VM ctor now takes the DispatcherQueue explicitly.
    // The factory captures the queue from the constructing thread (UI thread —
    // panels are built from MainWindow.SetPanels and HubWorkspaceView, both of
    // which run on the dispatcher). Capturing per-call lets a pop-out window on
    // its own dispatcher build a fresh VM bound to its own queue rather than
    // inheriting whichever dispatcher won the prior static-slot race.
    public UserControl CreateLiveFeedPanel(IHubServices services)
        => new LiveFeedView(new LiveFeedViewModel(Guard(services).LiveFeed, CurrentDispatcher()));

    public UserControl CreateChatPanel(IHubServices services)
    {
        var guarded = Guard(services);
        return new ChatView(new ChatViewModel(guarded.Chat, CurrentDispatcher(), guarded.Status));
    }

    public UserControl CreateScriptPanel(IHubServices services)
        => new ScriptView(new ScriptViewModel(Guard(services).Scripts, CurrentDispatcher()));

    public UserControl CreateSystemLogPanel(IHubServices services)
        => new SystemLogView(new SystemLogViewModel(Guard(services).SystemLog, CurrentDispatcher()));

    public UserControl CreateStatusStrip(IHubServices services)
        => new StatusStripView(new StatusStripViewModel(Guard(services).Status, CurrentDispatcher()));

    private static IHubServices Guard(IHubServices services)
        => services ?? throw new ArgumentNullException(nameof(services));

    // DispatcherQueue.GetForCurrentThread returns null when the calling thread
    // has no DispatcherQueue (e.g. a test harness without a WinUI dispatcher).
    // The VMs tolerate null and fall back to running actions inline — see
    // UiDispatcherPump.Post.
    private static DispatcherQueue? CurrentDispatcher() => DispatcherQueue.GetForCurrentThread();
}
