using System;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Shared.WinUI.Services;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Activates a secondary window AND forces it in front of the host window.
/// A bare <c>Window.Activate()</c> can leave a freshly opened sibling/sub-graph
/// window behind the already-focused main window; every user-facing show/refocus
/// site routes through here instead. (Per-pillar copy by design.)
/// </summary>
internal static class WindowFront
{
    public static void Show(Window window)
    {
        if (window is null)
            return;

        window.Activate();

        try
        {
            WindowZOrder.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(window));
        }
        catch
        {
            // HWND resolution can fail during teardown races; Activate already ran.
        }
    }
}
