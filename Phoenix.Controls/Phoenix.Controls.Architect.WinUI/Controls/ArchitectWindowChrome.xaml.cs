using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Architect.WinUI.Services;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using WinRT.Interop;

namespace Phoenix.Controls.Architect.WinUI.Controls;

/// <summary>
/// Shared custom-caption primitive for Architect's top-level windows
/// (sibling-graph windows + SubGraphWindow). Owns the gold
/// TitleAccentBar, drag region, and 46×32 Segoe Fluent caption buttons.
/// The hosting <see cref="Window"/> is responsible for wiring
/// <see cref="Bind(Microsoft.UI.Xaml.Window)"/> so that:
///   1. ExtendsContentIntoTitleBar is enabled.
///   2. <see cref="DragRegion"/> is registered as the window's title bar.
///   3. Min/Max/Close clicks route to the AppWindow presenter.
/// </summary>
public sealed partial class ArchitectWindowChrome : UserControl
{
    private Microsoft.UI.Xaml.Window? _window;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;

    // [S9-402/406] Active/inactive caption state. The baseline CaptionBar.cs
    // painted BgTitleActive for the focused window and a dimmer BgTitleInactive
    // when unfocused, and swapped the 2px gold accent bar for a 1px soft border.
    // We mirror that by swapping CaptionBarRoot.Background and TitleAccentBar.Fill
    // on the window's Activated / Deactivated events. _isActive tracks the
    // current state so a redundant apply is a no-op.
    private bool _isActive = true;

    // Design_Orders §4.7 — under Windows high-contrast themes, fall back to
    // the system title bar rather than render the custom coal/ember chrome
    // against an HC palette that would clash. Watcher fires on runtime HC
    // toggle so the fallback flips live, not only on next launch.
    private HighContrastWatcher? _hcWatcher;

    /// <summary>
    /// Left-side caption content (file-name stack / breadcrumb).
    /// DependencyProperty so consumers can author it via the property-
    /// element form <c>&lt;ArchitectWindowChrome.LeftCaptionContent&gt;</c>.
    /// </summary>
    public static readonly DependencyProperty LeftCaptionContentProperty =
        DependencyProperty.Register(
            nameof(LeftCaptionContent),
            typeof(object),
            typeof(ArchitectWindowChrome),
            new PropertyMetadata(null, OnLeftCaptionChanged));

    public object? LeftCaptionContent
    {
        get => GetValue(LeftCaptionContentProperty);
        set => SetValue(LeftCaptionContentProperty, value);
    }

    /// <summary>Right-side caption content (reveal-call-site button, status chips).</summary>
    public static readonly DependencyProperty RightCaptionContentProperty =
        DependencyProperty.Register(
            nameof(RightCaptionContent),
            typeof(object),
            typeof(ArchitectWindowChrome),
            new PropertyMetadata(null, OnRightCaptionChanged));

    public object? RightCaptionContent
    {
        get => GetValue(RightCaptionContentProperty);
        set => SetValue(RightCaptionContentProperty, value);
    }

    /// <summary>Below-caption content slot — File/Edit/View/Help menu bar for sibling windows.</summary>
    public static readonly DependencyProperty SecondaryContentProperty =
        DependencyProperty.Register(
            nameof(SecondaryContent),
            typeof(object),
            typeof(ArchitectWindowChrome),
            new PropertyMetadata(null, OnSecondaryContentChanged));

    public object? SecondaryContent
    {
        get => GetValue(SecondaryContentProperty);
        set => SetValue(SecondaryContentProperty, value);
    }

    private static void OnLeftCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArchitectWindowChrome c && c.LeftCaptionSlot is not null)
            c.LeftCaptionSlot.Content = e.NewValue;
    }

    private static void OnRightCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArchitectWindowChrome c && c.RightCaptionSlot is not null)
            c.RightCaptionSlot.Content = e.NewValue;
    }

    private static void OnSecondaryContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArchitectWindowChrome c && c.SecondarySlot is not null)
            c.SecondarySlot.Content = e.NewValue;
    }

    public ArchitectWindowChrome()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Bind this chrome to a hosting window. Enables custom title bar,
    /// registers the drag region, and hooks the maximize-glyph swap to
    /// the presenter's state. Call after Window.Activate so the AppWindow
    /// is resolvable.
    /// </summary>
    public void Bind(Microsoft.UI.Xaml.Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));

        try
        {
            _window.ExtendsContentIntoTitleBar = true;
            _window.SetTitleBar(DragRegion);
        }
        catch
        {
            // ExtendsContentIntoTitleBar can throw on hosts that aren't
            // fully initialized yet. The buttons + presenter still work;
            // we just lose the click-drag in the centre strip until a
            // later attempt sticks.
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _presenter = _appWindow?.Presenter as OverlappedPresenter;

            if (_appWindow is not null)
            {
                _appWindow.Changed += OnAppWindowChanged;
                ApplyMaximizeGlyph();
            }
        }
        catch
        {
            // No presenter — buttons fall back to no-ops (logged below).
        }

        // [S9-402/406] Track focus so the caption dims when the window loses
        // activation. WinUI fires Window.Activated with a Deactivated state on
        // blur and PointerActivated / CodeActivated on focus. Subscribe once;
        // Unbind() detaches. Apply the initial (active) state immediately so a
        // window created already-unfocused doesn't render at full brightness.
        try
        {
            _window.Activated += OnWindowActivated;
            ApplyActiveState(true);
        }
        catch
        {
            // best-effort — focus dimming is decoration only.
        }

        // HC accessibility fallback — must run after the initial
        // ExtendsContentIntoTitleBar attachment above so the first toggle
        // (if HC was already active at launch) sees a settled baseline.
        // The watcher's callback can fire on a non-UI thread; marshal back
        // onto the chrome's DispatcherQueue before touching window state.
        _hcWatcher = new HighContrastWatcher(() =>
        {
            var dq = DispatcherQueue;
            if (dq is null || dq.HasThreadAccess) ApplyHighContrastFallback();
            else dq.TryEnqueue(ApplyHighContrastFallback);
        });
        ApplyHighContrastFallback();
    }

    /// <summary>
    /// Design_Orders §4.7 high-contrast fallback — collapse the custom
    /// chrome and flip <see cref="Microsoft.UI.Xaml.Window.ExtendsContentIntoTitleBar"/>
    /// off so WinUI restores the system title bar under HC themes.
    /// </summary>
    private void ApplyHighContrastFallback()
    {
        // Drop LeftRail's static theme-hex cache so rail swatches re-resolve
        // against the post-toggle resource dictionary (every HC/theme retune
        // funnels through here — ctor settle-baseline + watcher callback).
        LeftRail.InvalidateThemeColorCache();

        if (_window is null) return;
        bool hc = _hcWatcher?.IsHighContrast == true;
        try
        {
            Visibility = hc ? Visibility.Collapsed : Visibility.Visible;
            _window.ExtendsContentIntoTitleBar = !hc;
            if (hc) _window.SetTitleBar(null);
            else _window.SetTitleBar(DragRegion);

            // Suppress the system caption when our custom coal chrome is
            // active — the chrome paints over the title bar, but the OS
            // still hit-tests the default Win32 min / max / close buttons
            // through the painted overlay, leaving ghost buttons in the
            // top-right corner. SetBorderAndTitleBar(true, false) keeps
            // the resize border but drops the caption (and its buttons).
            // Under HC we restore the system caption so the user gets
            // proper accessible affordances. Mirrors the pattern in
            // Hub's MainWindow.ConfigureAppWindow.
            if (_presenter is not null)
            {
                try
                {
                    _presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: hc);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Log(
                        $"ArchitectWindowChrome: SetBorderAndTitleBar failed ({ex.Message}); system caption may remain visible.",
                        "ArchitectWindowChrome", LogLevel.Debug);
                }
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ArchitectWindowChrome", "ApplyHighContrastFallback failed", ex);
        }
    }

    /// <summary>
    /// Detach from the bound window. Call from the window's Closed
    /// handler so the chrome doesn't keep the AppWindow alive via its
    /// Changed subscription.
    /// </summary>
    public void Unbind()
    {
        try
        {
            if (_appWindow is not null)
                _appWindow.Changed -= OnAppWindowChanged;
            // [S9-402/406] Detach the focus tracker so the chrome doesn't keep
            // the window alive via its Activated subscription.
            if (_window is not null)
                _window.Activated -= OnWindowActivated;
            _hcWatcher?.Dispose();
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            _appWindow = null;
            _presenter = null;
            _window = null;
            _hcWatcher = null;
        }
    }

    /// <summary>
    /// Hide the maximize button entirely. Used by sub-graph windows
    /// where snapping the editor across the whole screen isn't a useful
    /// affordance.
    /// </summary>
    public bool AllowMaximize
    {
        get => MaximizeButton.Visibility == Visibility.Visible;
        set => MaximizeButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange)
            ApplyMaximizeGlyph();
    }

    private void ApplyMaximizeGlyph()
    {
        try
        {
            if (_presenter is null) return;
            // E922 = Maximize (glyph rendered while window is windowed),
            // E923 = Restore (rendered while window is maximized).
            MaximizeButton.Content = _presenter.State == OverlappedPresenterState.Maximized
                ? ""
                : "";
        }
        catch
        {
            // glyph swap is decoration only — never crash the chrome over it.
        }
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
    {
        try { _presenter?.Minimize(); }
        catch
        {
            // best-effort — chrome stays usable if the presenter call fails
        }
    }

    private void OnMaximizeClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_presenter is null) return;
            if (_presenter.State == OverlappedPresenterState.Maximized)
                _presenter.Restore();
            else
                _presenter.Maximize();
        }
        catch
        {
            // best-effort
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        try { _window?.Close(); }
        catch
        {
            // best-effort
        }
    }

    // ─── [S9-402/406] Active / inactive caption state ───────────────────────

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // Deactivated → dim; PointerActivated / CodeActivated → restore.
        bool active = args.WindowActivationState != WindowActivationState.Deactivated;
        ApplyActiveState(active);
    }

    /// <summary>
    /// Swap the caption background + accent bar between focused and unfocused
    /// looks. Active: CoalRaisedBrush bar + 2px brass accent at full opacity.
    /// Inactive: dimmer CoalSurfaceBrush bar + soft BorderSoftBrush accent and
    /// slightly reduced caption-content opacity (mirrors the baseline's
    /// text/icon dimming). Brushes are resolved defensively — a missing theme
    /// key falls back to leaving the current brush in place rather than
    /// throwing.
    /// </summary>
    private void ApplyActiveState(bool active)
    {
        _isActive = active;
        try
        {
            if (CaptionBarRoot is not null)
            {
                var bg = ResolveBrush(active ? "CoalRaisedBrush" : "CoalSurfaceBrush");
                if (bg is not null) CaptionBarRoot.Background = bg;
                // Dim caption content (file name / breadcrumb / caption buttons)
                // when unfocused so the whole bar reads as inactive, not just
                // its background — the baseline reduced text/icon opacity too.
                CaptionBarRoot.Opacity = active ? 1.0 : 0.78;
            }
            if (TitleAccentBar is not null)
            {
                var accent = active
                    ? ResolveBrush("BrassGradientBrush")
                    : ResolveBrush("BorderSoftBrush");
                if (accent is not null) TitleAccentBar.Fill = accent;
                // Soften the accent further when inactive so the 2px gold strip
                // reads like the baseline's 1px soft border without a re-layout.
                TitleAccentBar.Opacity = active ? 1.0 : 0.6;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"ArchitectWindowChrome: ApplyActiveState failed ({ex.Message}); caption focus dimming skipped.",
                "ArchitectWindowChrome", LogLevel.Debug);
        }
    }

    private static Microsoft.UI.Xaml.Media.Brush? ResolveBrush(string key)
    {
        try
        {
            return Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var resource)
                && resource is Microsoft.UI.Xaml.Media.Brush b
                ? b
                : null;
        }
        catch { return null; }
    }

    // ─── [S9-403/407] Double-click-to-maximize on the caption drag region ───

    /// <summary>
    /// Toggle maximize / restore on a double-tap of the caption drag strip —
    /// the standard Windows title-bar gesture. WinUI's SetTitleBar gives us
    /// OS-level drag but not this gesture, so we mirror OnMaximizeClicked's
    /// presenter logic here. No-op when the window has no maximize affordance
    /// (AllowMaximize == false, e.g. SubGraphWindow) so a double-click can't
    /// maximize a window whose explicit maximize button is hidden.
    /// </summary>
    private void OnDragRegionDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        try
        {
            if (_presenter is null) return;
            if (!AllowMaximize) return;
            if (!_presenter.IsMaximizable && _presenter.State != OverlappedPresenterState.Maximized) return;
            if (_presenter.State == OverlappedPresenterState.Maximized)
                _presenter.Restore();
            else
                _presenter.Maximize();
        }
        catch
        {
            // best-effort — gesture failure must never crash the chrome.
        }
    }
}
