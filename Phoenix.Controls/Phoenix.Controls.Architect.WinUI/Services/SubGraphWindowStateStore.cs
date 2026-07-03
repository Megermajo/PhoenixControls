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

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Per-sub-graph window-position + size persistence for Architect's
/// <see cref="Phoenix.Controls.Architect.WinUI.Canvas.SubGraphWindow"/>.
/// One JSON file per sub-graph identity at
/// <c>%AppData%/PhoenixControls/Architect/sub-graph-state-&lt;key&gt;.json</c>,
/// where <c>key</c> is sanitized from <c>{macroOrProcess}-{name}</c>.
///
/// Mirrors the shape of Hub's <c>MainWindowStateStore</c> but parameterised
/// by a key so each macro/process editor remembers its own footprint —
/// users authoring multiple macros side by side can pin them to specific
/// monitor positions and the rectangle restores per macro on next open.
///
/// Read / write failures are best-effort: a corrupt or unreadable file
/// logs to <see cref="GlobalLogger"/> and falls back to a centred default-
/// size launch instead of throwing into the window's Loaded path.
/// </summary>
internal static class SubGraphWindowStateStore
{
    private const int MinVisibleSize = 200;
    private const int DefaultWidth   = 1100;
    private const int DefaultHeight  = 720;

    private sealed class WindowRecord
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    /// <summary>
    /// Apply any saved geometry for <paramref name="key"/> to the window's
    /// AppWindow. Falls back to a centred default-sized launch when no
    /// record exists or the saved record is unusable. Safe to call before
    /// <see cref="Window.Activate"/>; uses the same WindowNative bridge as
    /// Hub's <c>MainWindowStateStore</c>.
    /// </summary>
    public static void Restore(Window window, string key)
    {
        if (window is null || string.IsNullOrWhiteSpace(key)) return;
        try
        {
            AppWindow appWindow = ResolveAppWindow(window);
            ApplySavedState(appWindow, key);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SubGraphWindowStateStore: restore failed for '{key}': {ex.Message}",
                "SubGraphWindowStateStore", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Capture the window's current rectangle and persist it under
    /// <paramref name="key"/>. Call from the AppWindow.Closing handler so
    /// the rect lives on across the window destruction.
    /// </summary>
    /// <param name="flushSync">Write synchronously
    /// (terminal close path) so the geometry lands before a host Environment.Exit
    /// kills a queued async write. Mid-session AppWindow.Closing persists stay async.</param>
    public static void Persist(Window window, string key, bool flushSync = false)
    {
        if (window is null || string.IsNullOrWhiteSpace(key)) return;
        try
        {
            AppWindow appWindow = ResolveAppWindow(window);
            Capture(appWindow, key, flushSync);
        }
        catch (Exception ex)
        {
            // Persistence is best-effort — never crash the close path over
            // an IO blip. Same posture as MainWindowStateStore.
            GlobalLogger.Log(
                $"SubGraphWindowStateStore: persist failed for '{key}': {ex.Message}",
                "SubGraphWindowStateStore", LogLevel.Debug);
        }
    }

    private static void ApplySavedState(AppWindow appWindow, string key)
    {
        WindowRecord? rec = TryLoadRecord(key);

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
    }

    private static void Capture(AppWindow appWindow, string key, bool flushSync = false)
    {
        int w = appWindow.Size.Width;
        int h = appWindow.Size.Height;
        if (w < MinVisibleSize || h < MinVisibleSize) return;

        SaveRecord(key, new WindowRecord
        {
            X = appWindow.Position.X,
            Y = appWindow.Position.Y,
            W = w,
            H = h,
        }, flushSync);
    }

    // ─── Persistence ─────────────────────────────────────────────────────

    private static string ResolvePath(string key)
    {
        try
        {
            string dir = Paths.RoamingAppData("Architect");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"sub-graph-state-{key}.json");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                $"sub-graph-state-{key}.json");
        }
    }

    private static WindowRecord? TryLoadRecord(string key)
    {
        try
        {
            string path = ResolvePath(key);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<WindowRecord>(json);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SubGraphWindowStateStore: load failed for '{key}' ({ex.Message}); using defaults.",
                "SubGraphWindowStateStore", LogLevel.System);
            return null;
        }
    }

    private static void SaveRecord(string key, WindowRecord rec, bool flushSync = false)
    {
        // [freeze sweep] Persist() runs from the AppWindow.Closing handler on
        // the UI thread; the synchronous ResolvePath (Directory.CreateDirectory)
        // + File.WriteAllText stalled the window close under OneDrive / AV
        // latency. Serialize the tiny record on the caller thread, then offload
        // the disk work to the thread pool. Best-effort — a lost geometry
        // write is cosmetic (same posture as the rest of this store).
        string json = JsonSerializer.Serialize(rec, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        // Terminal close path writes synchronously
        // so the geometry survives an immediate host Environment.Exit.
        if (flushSync)
        {
            try { File.WriteAllText(ResolvePath(key), json); }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"SubGraphWindowStateStore: sync persist write failed for '{key}': {ex.Message}",
                    "SubGraphWindowStateStore", LogLevel.Debug);
            }
            return;
        }
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(() =>
            {
                try { File.WriteAllText(ResolvePath(key), json); }
                catch (Exception ex)
                {
                    GlobalLogger.Log(
                        $"SubGraphWindowStateStore: persist write failed for '{key}': {ex.Message}",
                        "SubGraphWindowStateStore", LogLevel.Debug);
                }
            }),
            "SubGraphWindowStateStore", "SaveRecord");
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
            work.X + Math.Max(0, (work.Width  - width)  / 2),
            work.Y + Math.Max(0, (work.Height - height) / 2)));
    }

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
