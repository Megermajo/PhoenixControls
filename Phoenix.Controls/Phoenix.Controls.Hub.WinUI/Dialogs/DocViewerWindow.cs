using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Windows.Graphics;
using WinRT.Interop;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

/// <summary>
/// The suite's single in-app HTML documentation viewer. A WebView2 window that
/// renders the offline doc bundle copied to <c>bin/Docs/</c>, served through a
/// virtual-host mapping (<c>https://phoenix.docs/…</c>) so the pages load with
/// no network.
///
/// <para>This is the HTML-viewer replacement the TODO marked node-docs as
/// "dropped pending" — it supersedes the native TreeView
/// <c>NodeDocumentationWindow</c> and also hosts the Hub Help → README /
/// Changelog pages.</para>
///
/// <para>Opened exclusively through <see cref="DocViewerHost"/>: Hub wires
/// <see cref="OpenOrFocus"/> into <c>DocViewerHost.Opener</c> at startup, and
/// every caller (Architect's F1 / Help → Node Reference, Hub's Help menu) goes
/// through the seam so the design-time pillars never reference WebView2.</para>
///
/// <para>Singleton-ish per the existing window patterns: a second request
/// re-navigates / re-anchors the existing window rather than spawning a
/// duplicate. The catalogue injected for the Node Reference is process-stable
/// (NodeRegistry never changes at runtime), so a re-open of the same page skips
/// the heavy re-injection and just re-jumps to the requested anchor.</para>
/// </summary>
// Code-only Window (no .xaml): the WinUI XAML markup compiler crashes on a
// WebView2 hosted directly under a Window root, so the trivial Grid+WebView2
// tree is built in code instead. Behaviour is identical to the equivalent XAML.
public sealed class DocViewerWindow : Window
{
    private static DocViewerWindow? s_instance;

    // The hosted WebView2, created in code. Named with a capital so the rest of
    // the file reads like the x:Name field a XAML version would have generated.
    private readonly WebView2 Web;

    private bool _coreReady;
    private bool _coreInitializing;
    private DocViewRequest? _pending;
    private string? _currentPage;
    private string? _injectionScriptId;

    private AppWindow? _appWindow;

    private DocViewerWindow(DocViewRequest initial)
    {
        // Obsidian backdrop matches the bundle's --coal-0 so there's no white
        // flash between window show and first paint.
        Web = new WebView2();
        var root = new Grid
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x0B, 0x09, 0x07)),
        };
        root.Children.Add(Web);
        Content = root;

        _pending = initial;
        Title = string.IsNullOrEmpty(initial.Title)
            ? "Phoenix Controls — Documentation"
            : initial.Title!;

        ConfigureAppWindow();

        Closed += (_, _) =>
        {
            try { PersistGeometry(); } catch { /* best-effort */ }
            if (ReferenceEquals(s_instance, this)) s_instance = null;
        };

        // Show first so the WebView2 is in a live, arranged visual tree before
        // EnsureCoreWebView2Async — a never-activated / collapsed WebView2 gets
        // no compositor surface and returns a null CoreWebView2 (the trap the
        // Visualist preview panels document). Then kick the async core init.
        Activate();
        _ = AsyncErrorBoundary.SafeRunAsync(InitCoreAsync, "DocViewerWindow", "InitCore");
    }

    /// <summary>
    /// Show (or re-target) the documentation viewer for <paramref name="request"/>.
    /// Safe to call from any pillar via <see cref="DocViewerHost"/>.
    /// </summary>
    public static void OpenOrFocus(DocViewRequest request)
    {
        if (request is null) return;
        try
        {
            if (s_instance is null)
            {
                s_instance = new DocViewerWindow(request);
                return;
            }
            s_instance.Activate();
            s_instance.NavigateTo(request);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("DocViewerWindow", "OpenOrFocus", ex);
            // If Activate threw on a torn-down window, drop the stale instance so
            // the next request can recover with a fresh window.
            s_instance = null;
        }
    }

    private async System.Threading.Tasks.Task InitCoreAsync()
    {
        if (_coreReady || _coreInitializing) return;
        _coreInitializing = true;
        try
        {
            await Web.EnsureCoreWebView2Async();
            var core = Web.CoreWebView2;
            if (core is null)
            {
                GlobalLogger.Log(
                    "DocViewerWindow: CoreWebView2 was null after EnsureCoreWebView2Async — docs viewer unavailable.",
                    "DocViewerWindow", LogLevel.CriticalError);
                return;
            }

            string docsDir = ResolveDocsDir();
            // Map the bundle folder to a synthetic https host so relative asset
            // URLs (tokens.css / node-reference.js / readme.html …) resolve and
            // the page runs in a secure context, all from local disk.
            core.SetVirtualHostNameToFolderMapping(
                "phoenix.docs", docsDir, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // Keep navigation inside the bundle. External links (GitHub, the
            // streamer's site) open in the user's real browser instead of
            // hijacking the doc window.
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;

            _coreReady = true;

            var pending = _pending;
            _pending = null;
            if (pending is not null) NavigateTo(pending);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("DocViewerWindow", "InitCoreAsync", ex);
        }
        finally
        {
            _coreInitializing = false;
        }
    }

    private void NavigateTo(DocViewRequest request)
    {
        if (!string.IsNullOrEmpty(request.Title)) Title = request.Title!;

        if (!_coreReady)
        {
            // Core not up yet — remember the latest request; InitCoreAsync will
            // process it once the WebView2 is ready.
            _pending = request;
            return;
        }

        var core = Web.CoreWebView2;
        if (core is null) return;

        // Same page already loaded: don't reload (the injected catalogue is
        // process-stable). Just re-jump to the requested anchor, if any.
        if (string.Equals(_currentPage, request.Page, StringComparison.OrdinalIgnoreCase))
        {
            JumpToAnchor(request.Anchor);
            return;
        }

        // Different page: swap the document-created injection (the Node
        // Reference passes a window.PHX catalogue; static pages pass none) then
        // navigate.
        if (_injectionScriptId is not null)
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(_injectionScriptId); } catch { /* id may be stale */ }
            _injectionScriptId = null;
        }

        _currentPage = request.Page;

        if (!string.IsNullOrEmpty(request.InjectionScript))
        {
            // Add the injection, THEN navigate, so it is registered for the new
            // document. Fire-and-forget with the navigate chained on completion.
            _ = AsyncErrorBoundary.SafeRunAsync(async () =>
            {
                _injectionScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(request.InjectionScript);
                core.Navigate(BuildUrl(request.Page));
            }, "DocViewerWindow", "Inject+Navigate");
        }
        else
        {
            core.Navigate(BuildUrl(request.Page));
        }
    }

    private void JumpToAnchor(string? anchor)
    {
        if (string.IsNullOrEmpty(anchor) || !_coreReady) return;
        try
        {
            string js = "window.phxNavigate && window.phxNavigate(" + JsonSerializer.Serialize(anchor) + ");";
            _ = Web.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log($"DocViewerWindow: anchor jump failed: {ex.Message}", "DocViewerWindow", LogLevel.Debug);
        }
    }

    private static string BuildUrl(string page) => "https://phoenix.docs/" + page.TrimStart('/');

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // Allow only in-bundle navigations (the phoenix.docs virtual host) and
        // the about:blank initial doc. Anything else is an outbound link —
        // cancel it here and hand it to the OS browser.
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

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
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
            return; // never shell-execute non-web schemes from doc content
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GlobalLogger.Log($"DocViewerWindow: failed to open external link '{uri}': {ex.Message}", "DocViewerWindow", LogLevel.Debug);
        }
    }

    private static string ResolveDocsDir()
    {
        // Content glob copies Docs/** to bin/Docs/. AppContext.BaseDirectory is
        // the running exe's bin folder.
        return Path.Combine(AppContext.BaseDirectory, "Docs");
    }

    // ── window chrome + geometry persistence ─────────────────────────────
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
                presenter.IsMinimizable = true;
                presenter.IsAlwaysOnTop = false;
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
            }

            ApplySavedGeometryOrDefault(hwnd);
            _appWindow.Closing += OnAppWindowClosing;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("DocViewerWindow", "ConfigureAppWindow", ex);
        }
    }

    private const int DefaultWidthDip = 1040;
    private const int DefaultHeightDip = 760;
    private const int MinVisible = 480;

    private void ApplySavedGeometryOrDefault(IntPtr hwnd)
    {
        if (_appWindow is null) return;
        var rec = TryLoadGeometry();

        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi >= 96 ? dpi / 96.0 : 1.0;
        int defW = (int)Math.Round(DefaultWidthDip * scale);
        int defH = (int)Math.Round(DefaultHeightDip * scale);

        if (rec is null || rec.W < MinVisible || rec.H < MinVisible)
        {
            _appWindow.Resize(new SizeInt32(defW, defH));
            CenterOnActiveDisplay(defW, defH);
            return;
        }

        var clamped = ClampToVisibleBounds(new RectInt32(rec.X, rec.Y, rec.W, rec.H));
        if (clamped.Width < MinVisible || clamped.Height < MinVisible)
        {
            _appWindow.Resize(new SizeInt32(defW, defH));
            CenterOnActiveDisplay(defW, defH);
            return;
        }
        _appWindow.MoveAndResize(clamped);
        if (rec.Maximized && _appWindow.Presenter is OverlappedPresenter p)
        {
            try { p.Maximize(); } catch { /* best-effort */ }
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try { PersistGeometry(); } catch { /* best-effort */ }
    }

    private void PersistGeometry()
    {
        if (_appWindow is null) return;
        int w = _appWindow.Size.Width, h = _appWindow.Size.Height;
        if (w < MinVisible || h < MinVisible) return;
        bool maximized = _appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized;
        SaveGeometry(new GeometryRecord
        {
            X = _appWindow.Position.X,
            Y = _appWindow.Position.Y,
            W = w,
            H = h,
            Maximized = maximized,
        });
    }

    private void CenterOnActiveDisplay(int width, int height)
    {
        if (_appWindow is null) return;
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        if (area is null) return;
        var work = area.WorkArea;
        _appWindow.Move(new PointInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2));
    }

    private static RectInt32 ClampToVisibleBounds(RectInt32 desired)
    {
        var centre = new PointInt32(desired.X + desired.Width / 2, desired.Y + desired.Height / 2);
        var area = DisplayArea.GetFromPoint(centre, DisplayAreaFallback.Nearest) ?? DisplayArea.Primary;
        if (area is null) return desired;
        var work = area.WorkArea;
        int w = Math.Min(desired.Width, work.Width);
        int h = Math.Min(desired.Height, work.Height);
        int x = Math.Max(work.X, Math.Min(desired.X, work.X + work.Width - w));
        int y = Math.Max(work.Y, Math.Min(desired.Y, work.Y + work.Height - h));
        return new RectInt32(x, y, w, h);
    }

    private sealed class GeometryRecord
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public bool Maximized { get; set; }
    }

    private static string GeometryPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PhoenixControls");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "docviewer-state.json");
    }

    private static GeometryRecord? TryLoadGeometry()
    {
        try
        {
            string path = GeometryPath();
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<GeometryRecord>(json);
        }
        catch { return null; }
    }

    private static void SaveGeometry(GeometryRecord rec)
    {
        try
        {
            string json = JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GeometryPath(), json);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log($"DocViewerWindow: geometry persist failed: {ex.Message}", "DocViewerWindow", LogLevel.Debug);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
