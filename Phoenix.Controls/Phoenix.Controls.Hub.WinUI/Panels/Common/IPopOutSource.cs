using System;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Marker interface for a Hub panel that can be popped out into its own
/// Window. The owning workspace (today: HubWorkspaceView) subscribes to
/// PopOutRequested and instantiates a fresh panel instance bound to the
/// same IHubServices in a separate Window.
///
/// Closing the pop-out Window disposes the panel content (which disposes
/// the per-pop-out ViewModel and unhooks its source-event subscription),
/// so multiple pop-outs are safe — each owns its own VM lifecycle.
/// </summary>
public interface IPopOutSource
{
    event EventHandler? PopOutRequested;
}
