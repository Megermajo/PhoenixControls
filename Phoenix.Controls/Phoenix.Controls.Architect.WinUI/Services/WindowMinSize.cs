using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using WinRT.Interop;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// 0.10.0 — enforce a hard minimum window size via the
/// classic WM_GETMINMAXINFO subclass. WindowsAppSDK 1.5 (this pin) doesn't
/// expose <c>OverlappedPresenter.PreferredMinimumWidth / PreferredMinimumHeight</c>
/// — those landed in 1.6. Mirrors the same pattern used by
/// <c>Phoenix.Controls.Hub.WinUI.Services.PopOutWindowFactory.EnforceMinimumSize</c>
/// but Architect-local per the per-pillar chrome rule
/// (chrome / Win32 plumbing stays per-pillar even when the pattern duplicates).
///
/// DIP-scaled by the window's current DPI so a 200% scale machine still
/// honours the same DIP-space floor.
/// </summary>
internal static class WindowMinSize
{
    private const uint WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("Comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("User32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    private static readonly UIntPtr s_subclassId = new(0xA12E_BABE);

    // Keep each window's SubclassProc instance alive for the window's lifetime
    // (same anchor pattern Hub's PopOutWindowFactory uses — without it the GC
    // reclaims the delegate and Win32 calls into freed memory).
    private static readonly Dictionary<IntPtr, SubclassProc> s_live = new();

    /// <summary>
    /// Install a WM_GETMINMAXINFO subclass on <paramref name="window"/>'s HWND
    /// so user-drag-resize is floored at the supplied DIP dimensions. Removes
    /// itself on Window.Closed.
    /// </summary>
    public static void Apply(Window window, int minWidthDips, int minHeightDips)
    {
        if (window is null) return;
        IntPtr hwnd;
        try { hwnd = WindowNative.GetWindowHandle(window); }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"WindowMinSize: GetWindowHandle failed ({ex.Message}); window can resize to any size.",
                "WindowMinSize", LogLevel.Debug);
            return;
        }
        if (hwnd == IntPtr.Zero) return;
        // Per-hwnd idempotency: removing+re-adding on repeat calls is fine.
        if (s_live.ContainsKey(hwnd)) return;

        SubclassProc proc = (h, msg, w, l, _, _) =>
        {
            if (msg == WM_GETMINMAXINFO && l != IntPtr.Zero)
            {
                int dpi = 96;
                try { dpi = GetDpiForWindow(h); } catch { /* fall back to 96 */ }
                if (dpi <= 0) dpi = 96;
                double scale = dpi / 96.0;

                try
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(l);
                    mmi.ptMinTrackSize.x = Math.Max(mmi.ptMinTrackSize.x, (int)Math.Round(minWidthDips  * scale));
                    mmi.ptMinTrackSize.y = Math.Max(mmi.ptMinTrackSize.y, (int)Math.Round(minHeightDips * scale));
                    Marshal.StructureToPtr(mmi, l, fDeleteOld: false);
                }
                catch
                {
                    // If marshaling fails we just defer to DefSubclassProc.
                }
                return IntPtr.Zero;
            }
            return DefSubclassProc(h, msg, w, l);
        };

        try
        {
            if (!SetWindowSubclass(hwnd, proc, s_subclassId, IntPtr.Zero))
            {
                GlobalLogger.Log(
                    $"WindowMinSize: SetWindowSubclass returned false (hwnd={hwnd}); window can resize to any size.",
                    "WindowMinSize", LogLevel.Debug);
                return;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"WindowMinSize: SetWindowSubclass threw ({ex.Message}); window can resize to any size.",
                "WindowMinSize", LogLevel.Debug);
            return;
        }

        s_live[hwnd] = proc;
        window.Closed += (_, _) =>
        {
            try { RemoveWindowSubclass(hwnd, proc, s_subclassId); }
            catch { /* shutdown best-effort */ }
            s_live.Remove(hwnd);
        };
    }
}
