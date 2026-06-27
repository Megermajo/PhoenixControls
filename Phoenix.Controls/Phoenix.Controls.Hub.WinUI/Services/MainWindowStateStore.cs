using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Phoenix.Controls.Hub.WinUI.Services;

/// <summary>
/// Persists Hub.MainWindow's last position, size, and Maximized flag across
/// sessions. Storage is one JSON file at
/// <c>%AppData%/PhoenixControls/Hub/window-state.json</c> — same family as the
/// retired WinForms <c>WindowStateStore</c> but routed through WinUI 3
/// AppWindow / DisplayArea APIs instead of <c>System.Windows.Forms</c>.
///
/// Restoration is bounded by the closest connected DisplayArea — a saved
/// record from a now-disconnected monitor falls back onto whichever display
/// contains the window's centre point, then the primary display. Geometry
/// outside <see cref="MinVisibleSize"/> is ignored so a corrupt record can't
/// drop the window below a usable size.
/// </summary>
internal static class MainWindowStateStore
{
    private const int MinVisibleSize = 200;
    private const int DefaultWidth   = 1480;
    private const int DefaultHeight  = 920;

    private sealed class WindowRecord
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>
    /// Apply any saved geometry to <paramref name="window"/>'s AppWindow and
    /// hook a Closed handler that captures and writes the final state. Falls
    /// back to a centred default-size launch when no record exists or the
    /// saved record is unusable.
    /// </summary>
    public static void Attach(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        AppWindow appWindow = ResolveAppWindow(window);
        ApplySavedState(appWindow);

        window.Closed += (_, _) => Capture(appWindow);
    }

    /// <summary>
    /// Returns the centre point (in screen-pixels) of the last saved Hub
    /// MainWindow rectangle, or null if no usable record exists. Used by
    /// SplashWindow so a Hub launch on a secondary monitor sees the splash
    /// on the same monitor instead of flashing on Primary first
    /// (TODO 2026-05-07 round 2 P2 — Splash position ignores last MainWindow
    /// display).
    /// </summary>
    public static PointInt32? TryReadLastCenter()
    {
        WindowRecord? rec = TryLoadRecord();
        if (rec is null) return null;
        if (rec.W < MinVisibleSize || rec.H < MinVisibleSize) return null;
        return new PointInt32(rec.X + rec.W / 2, rec.Y + rec.H / 2);
    }

    private static void ApplySavedState(AppWindow appWindow)
    {
        WindowRecord? rec = TryLoadRecord();

        if (rec is null
            || rec.W < MinVisibleSize
            || rec.H < MinVisibleSize)
        {
            appWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
            CenterOnPrimaryDisplay(appWindow, DefaultWidth, DefaultHeight);
            return;
        }

        var clamped = ClampToVisibleBounds(new RectInt32(rec.X, rec.Y, rec.W, rec.H));
        if (clamped.Width < MinVisibleSize || clamped.Height < MinVisibleSize)
        {
            appWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
            CenterOnPrimaryDisplay(appWindow, DefaultWidth, DefaultHeight);
            return;
        }

        appWindow.MoveAndResize(clamped);

        if (rec.Maximized
            && appWindow.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.Maximize();
        }
    }

    private static void Capture(AppWindow appWindow)
    {
        try
        {
            bool maximized = appWindow.Presenter is OverlappedPresenter op
                             && op.State == OverlappedPresenterState.Maximized;

            // For a maximized window AppWindow.Position/Size are the maximized
            // bounds — saving those would re-launch maximized at exact monitor
            // pixels even if the user un-maximized first next session. Persist
            // the un-maximized rect that the OverlappedPresenter restores into
            // by reading the current size/position only when Normal; otherwise
            // skip the rect update and only flip the Maximized flag.
            WindowRecord? prev = TryLoadRecord();

            int x = !maximized ? appWindow.Position.X : prev?.X ?? appWindow.Position.X;
            int y = !maximized ? appWindow.Position.Y : prev?.Y ?? appWindow.Position.Y;
            int w = !maximized ? appWindow.Size.Width  : prev?.W ?? appWindow.Size.Width;
            int h = !maximized ? appWindow.Size.Height : prev?.H ?? appWindow.Size.Height;

            if (w < MinVisibleSize || h < MinVisibleSize) return;

            SaveRecord(new WindowRecord
            {
                X = x,
                Y = y,
                W = w,
                H = h,
                Maximized = maximized,
            });
        }
        catch (Exception ex)
        {
            // Persistence is best-effort. A failure here must never crash the
            // close path — the user closing Hub would lose their layout over
            // a transient IO error otherwise.
            GlobalLogger.Log(
                $"MainWindowStateStore: capture failed: {ex.Message}",
                "MainWindowStateStore", LogLevel.Debug);
        }
    }

    // ─── Persistence ─────────────────────────────────────────────────────

    private static string ResolvePath()
    {
        try
        {
            string dir = Paths.RoamingAppData("Hub");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "window-state.json");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window-state-Hub.json");
        }
    }

    private static WindowRecord? TryLoadRecord()
    {
        try
        {
            string path = ResolvePath();
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<WindowRecord>(json);
        }
        catch (Exception ex)
        {
            // Promote to System so the corruption surfaces in Release builds
            // — Debug filtered the entry out by default and the user with a
            // tiny centred window had no clue why their layout reset
            // (TODO 2026-05-07 round 2 P2 — Corrupt window-state.json silent
            // fallback).
            GlobalLogger.Log(
                $"MainWindowStateStore: load failed ({ex.Message}); using defaults.",
                "MainWindowStateStore", LogLevel.System);
            return null;
        }
    }

    // MainWindowStateStore.Attach() makes its own AppWindow when called via
    // window.Closed, so internal classes need access to the same field that
    // ResolveAppWindow returns. Below: also expose internally so the splash
    // multi-monitor path can call TryReadLastCenter without Window context.

    private static void SaveRecord(WindowRecord rec)
    {
        string path = ResolvePath();
        string json = JsonSerializer.Serialize(rec, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(path, json);
    }

    // ─── Display clamping ────────────────────────────────────────────────

    private static AppWindow ResolveAppWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private static void CenterOnPrimaryDisplay(AppWindow appWindow, int width, int height)
    {
        var primary = DisplayArea.Primary;
        if (primary is null) return;
        var work = primary.WorkArea;
        appWindow.Move(new PointInt32(
            work.X + (work.Width  - width)  / 2,
            work.Y + (work.Height - height) / 2));
    }

    // Returns the input rect clamped onto the closest connected display. If
    // the rect is fully off-screen (typical after disconnecting the monitor
    // it was last on) we shift it onto the display containing its centre,
    // then onto the primary display as a final fallback.
    private static RectInt32 ClampToVisibleBounds(RectInt32 desired)
    {
        var centre = new PointInt32(
            desired.X + desired.Width  / 2,
            desired.Y + desired.Height / 2);

        var area = DisplayArea.GetFromPoint(centre, DisplayAreaFallback.Nearest)
                   ?? DisplayArea.Primary;
        if (area is null) return desired;

        var work = area.WorkArea;
        int w = Math.Min(desired.Width,  work.Width);
        int h = Math.Min(desired.Height, work.Height);
        int x = Math.Max(work.X, Math.Min(desired.X, work.X + work.Width  - w));
        int y = Math.Max(work.Y, Math.Min(desired.Y, work.Y + work.Height - h));
        return new RectInt32(x, y, w, h);
    }
}
