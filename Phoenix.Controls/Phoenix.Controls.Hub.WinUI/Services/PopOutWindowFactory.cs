using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Phoenix.Controls.Hub.WinUI.Controls;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using WinRT.Interop;

namespace Phoenix.Controls.Hub.WinUI.Services;

/// <summary>
/// Builds dark-chromed pop-out windows for <see cref="Panels.Common.IPopOutSource"/>
/// consumers. Centralises the AppWindow.TitleBar
/// customisation that previously lived inline in HubWorkspaceView so every
/// pop-out picks up the coal background + ember caption-button hover the
/// MainWindow uses, instead of the default light-grey system frame.
///
/// Mica/Acrylic is intentionally NOT applied — the four headline panels
/// already paint flat coal surfaces, and adding system backdrop on top
/// produces a translucent halo that fights the panel's own card chrome.
///
/// Pop-outs fan out — the workspace allows
/// arbitrary depth / multiple instances per panel kind. Each Create() call
/// returns a fresh Window with its own VM lifecycle; closing one does NOT
/// affect the embedded panel or sibling pop-outs. The per-panel
/// <see cref="Panels.Common.IPopOutAware.MarkAsPopOutChild"/> hook hides
/// the pop-out's own ↗ button so the child window itself can't spawn
/// further pop-outs — fan-out flows from the embedded workspace only.
/// </summary>
internal static class PopOutWindowFactory
{
    // Subset of MainWindow.ConfigureCaptionButtons — same coal/ember palette
    // so a pop-out reads as a child of the same window family. Held as static
    // Color values rather than read from Application.Current.Resources so the
    // factory can build a Window before App.OnLaunched finishes merging
    // PhoenixDark.xaml (defence-in-depth; today the call sites all run after
    // SetPanels, but the cost of inlining the literal is six lines).
    private static readonly global::Windows.UI.Color CoalSurface  = global::Windows.UI.Color.FromArgb(0xFF, 0x14, 0x11, 0x0D);
    private static readonly global::Windows.UI.Color CoalHover    = global::Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x25, 0x1D);
    private static readonly global::Windows.UI.Color CoalDivider  = global::Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x31, 0x27);
    private static readonly global::Windows.UI.Color CoalBodyText = global::Windows.UI.Color.FromArgb(0xFF, 0xA8, 0x96, 0x83);

    /// <summary>
    /// Creates a fresh pop-out Window wrapping <paramref name="content"/>.
    /// The caller is responsible for hooking Window.Closed to dispose the
    /// content (the panels implement <see cref="IDisposable"/>) and
    /// persisting geometry through <see cref="PopOutStateStore"/>.
    ///
    /// <paramref name="panelNameKey"/> resolves the bare panel name
    /// ("Live Feed", "Chat", …) via Localizer. "Phoenix Controls" is the
    /// retail brand and
    /// stays in the host language as a literal prefix — it's not a
    /// translatable token.
    /// </summary>
    public static Window Create(UserControl content, string panelNameKey, string panelNameFallback)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));

        // Pop-out child dead-↗ fix. The panel inside a
        // pop-out hides its own ↗ button so fan-out spawning is anchored
        // to the embedded workspace (see IPopOutAware doc-comment).
        if (content is Panels.Common.IPopOutAware aware)
        {
            aware.MarkAsPopOutChild();
        }

        string panelName = Localizer.T(panelNameKey, panelNameFallback);
        string title = $"Phoenix Controls — {panelName}";

        // 0.10.8 — wrap the panel in a Grid hosting a slim Phoenix-branded
        // PopOutChrome on top + the panel content beneath. The chrome
        // replaces the system OS caption (which Majo flagged as "default
        // App-header instead of the custom ones") with the same dark
        // coal / ember palette HubChrome uses, including the custom
        // TrafficLightButton trio so the close / max / min stay branded.
        // The chrome is attached after AppWindow lookup so its
        // AttachTo() can drive presenter.SetBorderAndTitleBar /
        // window.SetTitleBar.
        var chrome = new PopOutChrome();
        chrome.SetTitle(title);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(chrome, 0);
        Grid.SetRow(content, 1);
        root.Children.Add(chrome);
        root.Children.Add(content);

        var window = new Window
        {
            Title = title,
            Content = root,
        };

        ConfigureCustomChrome(window, chrome);
        EnforceMinimumSize(window);
        ApplyOpenFadeIn(content);

        // Register the pop-out Window with the activation
        // tracker so ScriptStatusDot pulses inside a popped-out ScriptPanel
        // pause when the pop-out is deactivated (e.g. user is interacting
        // with the main Hub window). Window.Content was assigned above so
        // XamlRoot is reachable from inside Register.
        WindowActivationTracker.Register(window);
        return window;
    }

    // ─── Min-size enforcement ───────────────────────────────────────────
    //
    // WindowsAppSDK 1.5 (the version this csproj is pinned to) doesn't
    // expose OverlappedPresenter.PreferredMinimumWidth / .PreferredMinimumHeight
    // — those landed in 1.6. Until the SDK bump, the only way to floor a
    // pop-out window's user-drag-resize is the classic WM_GETMINMAXINFO
    // subclass: we install a SUBCLASSPROC against the window's HWND and
    // patch ptMinTrackSize so the OS refuses to let the user drag the
    // window smaller than the minimum. Floors are DIPs-scaled by the
    // window's current DPI so a 200% scale machine doesn't end up with
    // tiny pop-outs that nominally satisfy the 360×240 floor at 100%.

    private const int MinClientWidthDips  = 360;
    private const int MinClientHeightDips = 240;
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

    // Stable subclass identity — any non-zero value works; we just need the
    // same value on Set / Remove so the OS pairs the install with its tear-down.
    private static readonly UIntPtr s_subclassId = new(0xC0FFEEu);

    // Keep each window's SubclassProc instance alive for the window's lifetime.
    // Without this anchor, the GC reclaims the delegate and Win32 calls into
    // freed memory the first time WM_GETMINMAXINFO fires.
    //
    // ConcurrentDictionary so a pop-out close racing with another
    // pop-out's creation can't tear the bucket list (Window.Closed runs on
    // the dispatcher but the lookup table itself is process-wide static).
    // The map is keyed by HWND, which is an OS-recycled value: in the narrow
    // window between RemoveWindowSubclass and the entry being dropped here,
    // a brand-new pop-out's freshly-allocated HWND could collide with the
    // stale key. RemoveSubclass(hwnd) below is called from each Window's
    // Closed handler precisely to close that gap; the residual risk is
    // benign (worst case: a brand-new pop-out inherits the prior delegate
    // anchor, which still satisfies the min-size contract because every
    // pop-out uses the same MinClientWidthDips / MinClientHeightDips).
    private static readonly ConcurrentDictionary<IntPtr, SubclassProc> s_liveSubclassProcs = new();

    /// <summary>
    /// Explicit cleanup hook called from each pop-out Window's Closed
    /// handler. Removes the subclass and drops the delegate anchor so the
    /// GC can reclaim the closure and Win32 stops dispatching into freed
    /// memory if the OS recycles the HWND. Best-effort — failure to remove
    /// the subclass is benign (the window is about to die).
    /// </summary>
    private static void RemoveSubclass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!s_liveSubclassProcs.TryRemove(hwnd, out var proc)) return;
        try { RemoveWindowSubclass(hwnd, proc, s_subclassId); }
        catch { /* shutdown best-effort */ }
    }

    private static void EnforceMinimumSize(Window window)
    {
        IntPtr hwnd;
        try { hwnd = WindowNative.GetWindowHandle(window); }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"PopOutWindowFactory: GetWindowHandle failed ({ex.Message}); pop-out can resize to any size.",
                "PopOutWindowFactory", LogLevel.Debug);
            return;
        }
        if (hwnd == IntPtr.Zero) return;

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
                    mmi.ptMinTrackSize.x = Math.Max(mmi.ptMinTrackSize.x, (int)Math.Round(MinClientWidthDips  * scale));
                    mmi.ptMinTrackSize.y = Math.Max(mmi.ptMinTrackSize.y, (int)Math.Round(MinClientHeightDips * scale));
                    Marshal.StructureToPtr(mmi, l, fDeleteOld: false);
                }
                catch
                {
                    // If marshaling fails we just defer to DefSubclassProc.
                    // Worst case: the user can drag the window tiny.
                }
                return IntPtr.Zero; // we handled it
            }
            return DefSubclassProc(h, msg, w, l);
        };

        try
        {
            if (!SetWindowSubclass(hwnd, proc, s_subclassId, IntPtr.Zero))
            {
                GlobalLogger.Log(
                    $"PopOutWindowFactory: SetWindowSubclass returned false (hwnd={hwnd}); pop-out can resize to any size.",
                    "PopOutWindowFactory", LogLevel.Debug);
                return;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"PopOutWindowFactory: SetWindowSubclass threw ({ex.Message}); pop-out can resize to any size.",
                "PopOutWindowFactory", LogLevel.Debug);
            return;
        }

        s_liveSubclassProcs[hwnd] = proc;
        window.Closed += (_, _) => RemoveSubclass(hwnd);
    }

    /// <summary>
    /// 0.10.8 chrome wiring — drops the system caption and hands the title
    /// bar to <see cref="PopOutChrome"/>. The chrome's DragRegion is
    /// registered with Window.SetTitleBar so the OS still owns
    /// window-drag / double-click-maximize / right-click-system-menu
    /// without us re-implementing them. AppWindow.TitleBar colours are
    /// still tinted as a defence-in-depth — on legacy DWM paths where
    /// SetBorderAndTitleBar(false) fails to suppress the caption the
    /// fall-through still reads dark-on-dark instead of light grey.
    /// </summary>
    private static void ConfigureCustomChrome(Window window, PopOutChrome chrome)
    {
        AppWindow? appWindow = null;
        OverlappedPresenter? presenter = null;

        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != IntPtr.Zero)
            {
                appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
                presenter = appWindow?.Presenter as OverlappedPresenter;

                if (appWindow?.TitleBar is { } bar)
                {
                    var transparent = global::Windows.UI.Color.FromArgb(0, 0, 0, 0);
                    bar.BackgroundColor               = CoalSurface;
                    bar.InactiveBackgroundColor       = CoalSurface;
                    bar.ButtonBackgroundColor         = transparent;
                    bar.ButtonInactiveBackgroundColor = transparent;
                    bar.ButtonForegroundColor         = CoalBodyText;
                    bar.ButtonInactiveForegroundColor = CoalBodyText;
                    bar.ButtonHoverBackgroundColor    = CoalHover;
                    bar.ButtonHoverForegroundColor    = CoalBodyText;
                    bar.ButtonPressedBackgroundColor  = CoalDivider;
                    bar.ButtonPressedForegroundColor  = CoalBodyText;
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"PopOutWindowFactory: AppWindow / TitleBar lookup failed ({ex.Message}); chrome still attaches.",
                "PopOutWindowFactory", LogLevel.Debug);
        }

        try
        {
            chrome.AttachTo(window, appWindow, presenter);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"PopOutWindowFactory: PopOutChrome.AttachTo failed ({ex.Message}); using default frame.",
                "PopOutWindowFactory", LogLevel.Debug);
        }
    }

    // Motion budget — ~120 ms ease-out fade on first paint. The
    // Composition API gives smoother results than a
    // Storyboard for this short an animation; fall back silently if the
    // visual isn't available yet (some WinUI 3 reentry edge cases).
    private static void ApplyOpenFadeIn(UserControl content)
    {
        try
        {
            content.Opacity = 0;
            void Run(object? s, RoutedEventArgs e)
            {
                content.Loaded -= Run;
                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(120),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                var sb = new Storyboard();
                Storyboard.SetTarget(sb, content);
                Storyboard.SetTargetProperty(anim, "Opacity");
                Storyboard.SetTarget(anim, content);
                sb.Children.Add(anim);
                sb.Begin();
            }
            content.Loaded += Run;
        }
        catch
        {
            // Fade is decorative — if the panel doesn't expose Loaded
            // (already-loaded edge case), leave it fully opaque.
            content.Opacity = 1;
        }
    }
}
