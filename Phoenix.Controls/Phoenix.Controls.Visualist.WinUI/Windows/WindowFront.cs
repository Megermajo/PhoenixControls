using System;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Shared.WinUI.Services;

// Folder is Windows/ but the sibling-window classes here historically use the
// Hosting namespace (see VisualistSiblingWindow) — matched so every call site
// already has the using in scope.
namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// Activates a secondary window AND forces it in front of the host window.
/// A bare <c>Window.Activate()</c> can leave a freshly opened sibling/preview
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
