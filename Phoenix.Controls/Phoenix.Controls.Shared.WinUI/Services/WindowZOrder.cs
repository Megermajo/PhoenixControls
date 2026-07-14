using System;
using System.Runtime.InteropServices;

namespace Phoenix.Controls.Shared.WinUI.Services;

/// <summary>
/// Win32 z-order helper for secondary windows. WinUI's <c>Window.Activate()</c> does not
/// force a foreground transition once another window of the same process holds focus, so
/// freshly opened sub-windows (giveaway, sibling graph editors, pop-outs) can appear
/// BEHIND the main window. Callers resolve their own HWND via
/// <c>WinRT.Interop.WindowNative.GetWindowHandle(window)</c> and pass it here — this
/// project deliberately carries no WindowsAppSDK reference, so the surface is IntPtr-only
/// (same constraint as PillarBootstrap's foreground helper).
/// </summary>
public static class WindowZOrder
{
    /// <summary>
    /// Restores the window if minimized and forces it to the top of the z-order with
    /// keyboard focus. Safe to call right after <c>Window.Activate()</c>; swallows all
    /// failures (a missed foreground transition degrades to the pre-fix behavior).
    /// </summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);

            // Both calls together give a reliable transition: BringWindowToTop fixes the
            // z-order even when the shell declines the focus switch, SetForegroundWindow
            // moves keyboard focus (allowed here because the calling process owns the
            // current foreground window — the user just clicked in it).
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Never let a z-order nicety take down a window-open path.
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
