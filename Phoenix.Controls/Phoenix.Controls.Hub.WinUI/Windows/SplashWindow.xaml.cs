using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Phoenix.Controls.Hub.WinUI.Services;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace Phoenix.Controls.Hub.WinUI.Windows;

public sealed partial class SplashWindow : Window
{
    // Splash dimensions are authored in DIPs (the XAML sizing space).
    // AppWindow.Resize / Move expect PHYSICAL pixels, so we multiply by the
    // window's DPI scale before passing them through — without this the
    // splash rendered at 347×307 on 150% scale displays (Hub UI sweep P2
    // DPI-aware splash sizing).
    private const int SplashWidthDips  = 520;
    private const int SplashHeightDips = 460;
    // Progress-bar width = SplashWidthDips − 2 × 20 px margin (redesign R4
    // "92 %-width"). The XAML side uses HorizontalAlignment=Stretch with
    // 20 px insets; the fill width must follow so the reported Fraction
    // lands on the right pixel. 520 − 40 = 480 ≈ 92.3 % of the splash width.
    // ProgressFullWidth stays in DIPs — it drives the inner StackPanel,
    // not the AppWindow.
    private const double ProgressFullWidth = SplashWidthDips - 40;

    // user32!GetDpiForWindow — per-monitor DPI awareness, available since
    // Windows 10 1607. Returns 96 for 100% scale, 144 for 150%, etc.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Resolve the splash window's physical-pixel size from the DIP
    /// constants above. Defaults to 1.0 scale on the unlikely path where
    /// GetDpiForWindow returns 0 (HWND not realised, dpi-virtualised host).
    /// </summary>
    private static SizeInt32 ResolvePhysicalSize(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi >= 96 ? dpi / 96.0 : 1.0;
        return new SizeInt32(
            (int)Math.Round(SplashWidthDips  * scale),
            (int)Math.Round(SplashHeightDips * scale));
    }

    public IProgress<SplashProgress> Progress { get; }

    // Set on Closed so the IProgress callback can short-circuit cleanly.
    // BootAsync can keep reporting progress for one more dispatcher tick
    // after the splash closes; without this guard those reports tried to
    // mutate StatusText / ProgressFill on a torn-down window and threw.
    private bool _closed;

    public SplashWindow()
    {
        InitializeComponent();

        ConfigurePresenter();
        CenterOnSavedDisplayOrPrimary();
        VersionText.Text = ResolveVersionString();
        // IGNITING… eyebrow — redesign R4 "splash.boot" localization key.
        // DO NOT call Localizer.T here: the splash is constructed
        // BEFORE PillarBootstrap.RunHeavyPreUiAsync step 5 (Localizer.Init)
        // runs, so a lookup here always returns the English fallback even
        // for DE/FR/ES users whose bundle ships splash.boot. The XAML's
        // literal Text="IGNITING…" provides the seed string; the App layer
        // calls RefreshLocalizedStrings() once Localizer.Init has completed
        // so the localized value lands a single dispatcher tick later.
        Progress = new Progress<SplashProgress>(OnProgress);

        SplashRoot.Loaded += (_, _) =>
        {
            RunRiseAnimation();
            RunPulseAnimation();
        };
        this.Closed += (_, _) => _closed = true;
    }

    private void ConfigurePresenter()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // AppWindow.GetFromWindowId can return null (no AppWindow
        // backing the HWND yet on some startup paths — see MainWindow.ConfigureAppWindow).
        // Bail before dereferencing rather than NRE-ing on launch.
        if (appWindow is null)
        {
            GlobalLogger.Error("SplashWindow", "ConfigurePresenter",
                new InvalidOperationException("AppWindow.GetFromWindowId returned null; skipping presenter configuration."));
            return;
        }

        appWindow.Resize(ResolvePhysicalSize(hwnd));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ExtendsContentIntoTitleBar = true;
    }

    private void CenterOnSavedDisplayOrPrimary()
    {
        // Prefer the display that contained the last MainWindow centre so a
        // Hub launch on a secondary monitor sees the splash on the same
        // monitor — without this the splash flashes on Primary and the
        // MainWindow snaps back to monitor 2 (TODO 2026-05-07 round 2 P2).
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // AppWindow.GetFromWindowId can return null on some startup
        // paths; without an AppWindow there is nothing to centre, so bail before the
        // .Move() dereference below rather than crashing the splash on launch.
        if (appWindow is null) return;

        DisplayArea? displayArea = null;
        var savedCenter = MainWindowStateStore.TryReadLastCenter();
        if (savedCenter is { } center)
        {
            try
            {
                // GetFromPoint returns the closest connected display when the
                // point sits outside any work area — so a saved monitor that's
                // since been disconnected falls back gracefully without us
                // re-implementing the bounds check.
                displayArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest);
            }
            catch { /* fall through to primary */ }
        }
        displayArea ??= DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);

        // WorkArea is in PHYSICAL pixels on a per-monitor-DPI-aware app,
        // so subtract the physical splash size rather than the DIP constant.
        // Without this the splash centred ~80 DIPs off-axis on 150% scale
        // (Hub UI sweep P2 DPI-aware splash sizing).
        var workArea = displayArea.WorkArea;
        var physicalSize = ResolvePhysicalSize(hwnd);
        appWindow.Move(new PointInt32(
            workArea.X + (workArea.Width  - physicalSize.Width)  / 2,
            workArea.Y + (workArea.Height - physicalSize.Height) / 2));
    }

    private static string ResolveVersionString()
    {
        return "v" + Phoenix.Controls.Shared.Services.GitInfoService.GetAssemblyVersion();
    }

    /// <summary>
    /// Re-resolve the splash's localized strings after
    /// <c>Localizer.Init</c> has finished loading bundles. Constructor
    /// runs before Init (the splash needs to be visible while the heavy
    /// pre-UI bootstrap — including Localizer.Init — runs on a worker),
    /// so the eyebrow is seeded from the XAML literal and re-set here
    /// once a real localized lookup is possible. Safe to call multiple
    /// times; cheap when the bundle hasn't changed.
    ///
    /// <para>The footer tagline rides the same path for the same reason: a
    /// markup-side <c>loc:Localize.Key</c> would resolve at the splash's
    /// Loaded, which fires BEFORE Localizer.Init completes on the worker, so
    /// it would bake in the English seed for every non-English user.</para>
    /// </summary>
    public void RefreshLocalizedStrings()
    {
        if (_closed) return;
        try
        {
            BootEyebrow.Text = Localizer.T("splash.boot", "IGNITING…");
            FooterTagline.Text = Localizer.T("splash.footer.tagline",
                                             "FORGED FOR STREAMERS · 2026");
        }
        catch { /* window torn down mid-call — silent no-op */ }
    }

    private void OnProgress(SplashProgress p)
    {
        if (_closed) return;
        try
        {
            StatusText.Text = p.Status;
            var clamped = Math.Clamp(p.Fraction, 0.0, 1.0);
            ProgressFill.Width = ProgressFullWidth * clamped;
        }
        catch { /* window torn down mid-dispatch — silent no-op */ }
    }

    /// <summary>
    /// Switch the splash into error mode: the boot-progress UI stays visible
    /// underneath an opaque overlay that names the failure and offers Close.
    /// Called by App.OnLaunched when HubBootstrapper.BootAsync throws or
    /// returns null services (TODO 2026-05-07 round 2 P0 #2). The overlay's
    /// Close button exits the application; closing the window directly
    /// raises the same Closed event the App handles.
    /// </summary>
    public void ShowError(string message)
    {
        // Splash error overlay is the one surface a non-English user
        // hits before any UI bundle has loaded — route the chrome
        // strings through Localizer.T so DE/FR/ES users see the local
        // language. The English fallbacks match the previous XAML
        // literals verbatim.
        ErrorHeading.Text    = Localizer.T("splash.error.heading", "BOOT FAILED");
        ErrorReasonLead.Text = Localizer.T("splash.error.reason_lead",
                                           "Phoenix Controls couldn't start. Reason:");
        ErrorLogHint.Text    = Localizer.T("splash.error.log_hint",
                                           "Hub log:  %AppData%/PhoenixControls/Hub/");
        CloseButton.Content  = Localizer.T("common.button.close", "Close");

        string unknown = Localizer.T("splash.error.unknown_reason", "(unknown error)");
        ErrorMessage.Text = string.IsNullOrEmpty(message) ? unknown : message;
        ErrorOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        // Drop always-on-top so the user can move other windows over the
        // error display while they read it; closing remains explicit.
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsAlwaysOnTop = false;
                p.SetBorderAndTitleBar(true, true);
            }
        }
        catch { /* presenter swap is best-effort; the overlay alone surfaces the error */ }
    }

    private void OnCloseClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { Application.Current.Exit(); }
        catch { Close(); }
    }

    private void RunRiseAnimation()
    {
        // Phoenix mark rises 40px and fades in over 2.4s — splash spec
        // calls this "phx-rise" (translateY + opacity, ease-out).
        var storyboard = new Storyboard();

        var translate = new DoubleAnimation
        {
            From = 40,
            To = 0,
            Duration = TimeSpan.FromSeconds(2.4),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(translate, PhoenixTransform);
        Storyboard.SetTargetProperty(translate, "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromSeconds(2.4),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, PhoenixMark);
        Storyboard.SetTargetProperty(fade, "Opacity");

        storyboard.Children.Add(translate);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    /// <summary>
    /// Gentle opacity pulse on the phoenix mark — redesign R4 "phx-pulse"
    /// keyframe. Starts at 2.4 s (after the rise completes) so the pulse
    /// kicks in just as the mark settles at its final position. The cycle
    /// is intentionally soft (0.95 ↔ 1.0 over 2.8 s, ease in-out) so it
    /// reads as living warmth rather than a strobe.
    /// </summary>
    private void RunPulseAnimation()
    {
        var storyboard = new Storyboard
        {
            BeginTime = TimeSpan.FromSeconds(2.4),
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
        };
        var pulse = new DoubleAnimation
        {
            From = 1.0,
            To = 0.95,
            Duration = TimeSpan.FromSeconds(2.8),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(pulse, PhoenixMark);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        storyboard.Children.Add(pulse);
        storyboard.Begin();
    }
}
