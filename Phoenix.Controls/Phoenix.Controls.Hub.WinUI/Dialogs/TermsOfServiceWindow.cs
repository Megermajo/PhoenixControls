using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.WinUI.Services;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

/// <summary>
/// The first-launch (and post-revision) Terms-of-Service consent gate window.
///
/// <para>A WebView2 renders the styled <c>Docs/terms-of-service.html</c> — the
/// same offline "usual styling" pipeline the <see cref="DocViewerWindow"/> uses
/// (a <c>phoenix.docs</c> virtual-host mapping so the page + tokens.css load with
/// no network). Below the document a native WinUI action bar carries the only two
/// exits: <b>I Accept</b> and <b>Decline &amp; Exit</b>. There is no other way
/// out — closing the window via the title-bar X counts as a decline.</para>
///
/// <para>The gate is <b>fail-closed</b> ("the safe way"): the window is shown by
/// <see cref="TermsOfServiceGate"/> BEFORE any Hub service boots and before the
/// MainWindow exists, so while it is open the app is genuinely non-functional —
/// nothing is connected, nothing executes. The buttons are native controls that
/// work even if the WebView2 fails to initialise; when the document cannot be
/// rendered a plain-text fallback of the same terms is shown so the user still
/// sees what they are agreeing to.</para>
///
/// <para>Await <see cref="Completion"/> for the decision: <c>true</c> = accepted,
/// <c>false</c> = declined / closed. The caller persists the accepted version and
/// proceeds, or exits the process.</para>
/// </summary>
// Code-only Window (no .xaml): the WinUI XAML markup compiler crashes on a
// WebView2 hosted under a Window root, so — like DocViewerWindow — the visual
// tree is built in code. Behaviour is identical to the equivalent XAML.
public sealed class TermsOfServiceWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly WebView2 _web;
    private readonly Grid _fallbackPanel;
    private readonly TextBlock _fallbackText;
    private readonly string _docsDir;
    private readonly string _fallbackContent;

    private AppWindow? _appWindow;
    private bool _coreInitializing;
    private int _decided; // Interlocked guard so a double-click / X race resolves once.

    /// <summary>Resolves to the user's decision: true = accepted, false = declined/closed.</summary>
    public Task<bool> Completion => _tcs.Task;

    public TermsOfServiceWindow(string title, string docsDir, string fallbackContent)
    {
        _docsDir = docsDir;
        _fallbackContent = fallbackContent;
        Title = string.IsNullOrEmpty(title)
            ? Localizer.T("dialog.tos.window.title", "Phoenix Controls — Terms of Service")
            : title;

        _web = new WebView2 { Visibility = Visibility.Visible };

        // Native plain-text fallback — hidden unless the WebView2 can't render.
        _fallbackText = new TextBlock
        {
            Text = fallbackContent,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = TryFont("SansFont"),
            FontSize = 13,
            Foreground = Brush("CoalBodyTextBrush", 0xFF, 0xA8, 0x96, 0x83),
            Margin = new Thickness(28, 24, 28, 24),
        };
        _fallbackPanel = new Grid { Visibility = Visibility.Collapsed };
        _fallbackPanel.Children.Add(new ScrollViewer
        {
            Content = _fallbackText,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        // Document region (row 0) stacks the WebView2 and the fallback in one
        // cell — exactly one is ever visible.
        var docRegion = new Grid();
        docRegion.Children.Add(_web);
        docRegion.Children.Add(_fallbackPanel);

        var root = new Grid
        {
            // Obsidian shell (= --coal-0) so there's no white flash before paint.
            Background = Brush("CoalShellBrush", 0xFF, 0x0B, 0x09, 0x07),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(docRegion, 0);
        root.Children.Add(docRegion);

        var actionBar = BuildActionBar();
        Grid.SetRow(actionBar, 1);
        root.Children.Add(actionBar);

        Content = root;

        ConfigureAppWindow();

        Closed += (_, _) =>
        {
            // A close that beat an explicit button click is a decline.
            Decide(false);
            try { _web.Close(); } catch { /* already torn down */ }
        };

        // Show first so the WebView2 sits in a live, arranged visual tree before
        // EnsureCoreWebView2Async — a never-activated WebView2 gets no compositor
        // surface and returns a null CoreWebView2 (the documented trap). Then
        // kick the async core init.
        WindowFront.Show(this);
        _ = AsyncErrorBoundary.SafeRunAsync(InitCoreAsync, "TermsOfServiceWindow", "InitCore");
    }

    // ── action bar ───────────────────────────────────────────────────────
    private Border BuildActionBar()
    {
        var hint = new TextBlock
        {
            Text = Localizer.T("dialog.tos.action.hint",
                "You must accept these terms to use Phoenix Controls. Declining closes the app."),
            FontFamily = TryFont("SansFont"),
            FontSize = 12,
            Foreground = Brush("CoalSecondaryTextBrush", 0xFF, 0x9C, 0x8A, 0x72),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(hint, 0);

        var declineBtn = new Button
        {
            Content = Localizer.T("dialog.tos.button.decline", "Decline & Exit"),
            MinWidth = 128,
        };
        ApplyStyle(declineBtn, "CoalSecondaryButtonStyle");
        declineBtn.Click += (_, _) => Decide(false);

        var acceptBtn = new Button
        {
            Content = Localizer.T("dialog.tos.button.accept", "I Accept"),
            MinWidth = 128,
            Margin = new Thickness(10, 0, 0, 0),
        };
        ApplyStyle(acceptBtn, "EmberPrimaryButtonStyle");
        acceptBtn.Click += (_, _) => Decide(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(declineBtn);
        buttons.Children.Add(acceptBtn);
        Grid.SetColumn(buttons, 1);

        var grid = new Grid { Margin = new Thickness(20, 12, 20, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(hint);
        grid.Children.Add(buttons);

        return new Border
        {
            // Raised coal band with a divider hairline separating it from the
            // document — mirrors the Hub panel-header seam.
            Background = Brush("CoalRaisedBrush", 0xFF, 0x1B, 0x17, 0x13),
            BorderBrush = Brush("CoalDividerBrush", 0xFF, 0x3A, 0x31, 0x27),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    // Resolve one decision. Interlocked so the Closed handler's decline can't
    // clobber an explicit Accept (or vice-versa) in a race.
    private void Decide(bool accepted)
    {
        if (System.Threading.Interlocked.Exchange(ref _decided, 1) != 0) return;
        _tcs.TrySetResult(accepted);
        // Close on an explicit button click; the Closed-handler path is already
        // inside teardown so calling Close again there is a harmless no-op.
        try { Close(); } catch { /* window may already be closing */ }
    }

    // ── WebView2 bring-up ────────────────────────────────────────────────
    private async Task InitCoreAsync()
    {
        if (_coreInitializing) return;
        _coreInitializing = true;
        try
        {
            await _web.EnsureCoreWebView2Async();
            var core = _web.CoreWebView2;
            if (core is null)
            {
                GlobalLogger.Log(
                    "TermsOfServiceWindow: CoreWebView2 was null after EnsureCoreWebView2Async — showing plain-text fallback.",
                    "TermsOfServiceWindow", LogLevel.CriticalError);
                ShowFallback();
                return;
            }

            core.SetVirtualHostNameToFolderMapping(
                "phoenix.docs", _docsDir, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // Keep navigation inside the bundle; any outbound link (e.g. the
            // imprint URL, if the user copies it out) opens in the real browser
            // instead of hijacking the consent window.
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += OnNavigationCompleted;

            core.Navigate("https://phoenix.docs/terms-of-service.html");
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("TermsOfServiceWindow", "InitCoreAsync", ex);
            ShowFallback();
        }
        finally
        {
            _coreInitializing = false;
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        // A missing / unreadable terms-of-service.html leaves the document blank;
        // fall back to the plain-text terms so the user always sees something to
        // consent to.
        if (!args.IsSuccess)
        {
            GlobalLogger.Log(
                $"TermsOfServiceWindow: navigation to terms-of-service.html failed ({args.WebErrorStatus}) — showing plain-text fallback.",
                "TermsOfServiceWindow", LogLevel.CriticalError);
            ShowFallback();
        }
    }

    private void ShowFallback()
    {
        try
        {
            // Prefer the bundled Docs/terms-of-service.txt (kept in lockstep with
            // the HTML) so the fallback carries the full terms; fall back to the
            // essentials string handed in by the gate if even that read fails.
            string text = _fallbackContent;
            try
            {
                string txtPath = Path.Combine(_docsDir, "terms-of-service.txt");
                if (File.Exists(txtPath))
                {
                    string fromFile = File.ReadAllText(txtPath);
                    if (!string.IsNullOrWhiteSpace(fromFile)) text = fromFile;
                }
            }
            catch { /* keep the handed-in essentials */ }

            _fallbackText.Text = text;
            _fallbackPanel.Visibility = Visibility.Visible;
            _web.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("TermsOfServiceWindow", "ShowFallback", ex);
        }
    }

    private static void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        var uri = args.Uri ?? string.Empty;
        if (uri.StartsWith("https://phoenix.docs/", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || uri.Length == 0)
        {
            return;
        }
        args.Cancel = true;
        TryOpenExternal(uri);
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        TryOpenExternal(args.Uri);
    }

    private static void TryOpenExternal(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return; // never shell-execute non-web schemes from document content
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GlobalLogger.Log($"TermsOfServiceWindow: failed to open external link '{uri}': {ex.Message}", "TermsOfServiceWindow", LogLevel.Debug);
        }
    }

    // ── window chrome ────────────────────────────────────────────────────
    private const int DefaultWidthDip = 800;
    private const int DefaultHeightDip = 880;

    private void ConfigureAppWindow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            if (_appWindow is null) return;

            try { _appWindow.SetIcon("Assets/app.ico"); } catch { /* path differs in some configs */ }

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = false;
                // Consent gate: keep it above the (still-animating) splash so the
                // terms can never hide behind another surface.
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
            }

            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi >= 96 ? dpi / 96.0 : 1.0;
            int w = (int)Math.Round(DefaultWidthDip * scale);
            int h = (int)Math.Round(DefaultHeightDip * scale);
            _appWindow.Resize(new SizeInt32(w, h));

            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            if (area is not null)
            {
                var work = area.WorkArea;
                // Clamp height to the work area so the action bar is never pushed
                // off-screen on short displays.
                if (h > work.Height) { h = work.Height; _appWindow.Resize(new SizeInt32(w, h)); }
                _appWindow.Move(new PointInt32(
                    work.X + (work.Width - w) / 2,
                    work.Y + (work.Height - h) / 2));
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("TermsOfServiceWindow", "ConfigureAppWindow", ex);
        }
    }

    // ── theme resource helpers (code-only window can't use {StaticResource}) ─
    private static void ApplyStyle(Button btn, string key)
    {
        try
        {
            if (Application.Current?.Resources?[key] is Style s) btn.Style = s;
        }
        catch { /* style not merged (e.g. a test host) — leave default chrome */ }
    }

    private static SolidColorBrush Brush(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources?[key] is SolidColorBrush sb) return sb;
        }
        catch { /* fall through to literal */ }
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(a, r, g, b));
    }

    private static FontFamily TryFont(string key)
    {
        try
        {
            if (Application.Current?.Resources?[key] is string s && !string.IsNullOrEmpty(s))
                return new FontFamily(s);
        }
        catch { /* fall through */ }
        return new FontFamily("Segoe UI");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
