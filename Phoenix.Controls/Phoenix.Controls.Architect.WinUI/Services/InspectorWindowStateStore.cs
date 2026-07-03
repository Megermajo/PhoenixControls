using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Per-restart geometry persistence for Architect's
/// floating <see cref="Phoenix.Controls.Architect.WinUI.Hosting.InspectorWindow"/>.
/// Single JSON file at <c>%AppData%/PhoenixControls/Architect/inspector-state.json</c>
/// — only one inspector window can exist at a time, so unlike
/// <see cref="SiblingWindowStateStore"/> (per-path) there's no hashing.
///
/// Mirrors the existing *WindowStateStore pattern (best-effort, log-and-
/// continue on IO failure, clamp to visible bounds) so a future
/// "shared window-state store" rewrite can collapse all three stores
/// without rediscovering the invariants.
/// </summary>
internal static class InspectorWindowStateStore
{
    private const int MinVisibleSize = 200;
    private const int DefaultWidth   = 340;
    private const int DefaultHeight  = 520;

    private sealed class WindowRecord
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>
    /// Apply saved geometry to the inspector window's AppWindow. Falls back
    /// to a default-size launch in the top-right corner of the primary
    /// display when no record exists. Safe to call before
    /// <see cref="Window.Activate"/>.
    /// </summary>
    public static void Restore(Window window)
    {
        if (window is null) return;
        try
        {
            // Resolve the AppWindow + capture the dispatcher on the caller
            // (UI) thread — both touch the HWND / dispatcher affinity. The
            // disk read itself is offloaded inside ApplySavedState.
            var appWindow  = ResolveAppWindow(window);
            var dispatcher = window.DispatcherQueue;
            ApplySavedState(appWindow, dispatcher);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"InspectorWindowStateStore: restore failed: {ex.Message}",
                "InspectorWindowStateStore", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Capture the inspector window's current rectangle + maximized state
    /// and persist it. Call from the AppWindow.Closing handler so the rect
    /// lives on across destruction.
    /// </summary>
    public static void Persist(Window window)
    {
        if (window is null) return;
        try
        {
            var appWindow = ResolveAppWindow(window);
            Capture(appWindow);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"InspectorWindowStateStore: persist failed: {ex.Message}",
                "InspectorWindowStateStore", LogLevel.Debug);
        }
    }

    private static void ApplySavedState(AppWindow appWindow, DispatcherQueue? dispatcher)
    {
        // [freeze sweep / S10 P2] TryLoadRecord() does blocking File.Exists +
        // File.ReadAllText + JSON deserialize. Restore() runs on the UI thread
        // from InspectorWindow.OnActivatedOnce, so under OneDrive / AV latency
        // this stalled window activation + geometry restore. Offload the read
        // to the thread pool (matching the SaveRecord() write-path fix), then
        // marshal the AppWindow geometry calls — which require UI-thread /
        // dispatcher affinity — back via the captured dispatcher.
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(() =>
            {
                var rec = TryLoadRecord();
                ApplyRecordOnDispatcher(appWindow, dispatcher, rec);
            }),
            "InspectorWindowStateStore", "ApplySavedState");
    }

    /// <summary>
    /// Apply <paramref name="rec"/> (or defaults when null / too small) to the
    /// AppWindow. The actual geometry mutation is marshalled onto
    /// <paramref name="dispatcher"/> so it runs on the UI thread; the disk read
    /// that produced <paramref name="rec"/> already happened off-thread.
    /// </summary>
    private static void ApplyRecordOnDispatcher(
        AppWindow appWindow, DispatcherQueue? dispatcher, WindowRecord? rec)
    {
        void Apply()
        {
            try
            {
                if (rec is null || rec.W < MinVisibleSize || rec.H < MinVisibleSize)
                {
                    appWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
                    PositionTopRightOnPrimaryDisplay(appWindow, DefaultWidth, DefaultHeight);
                    return;
                }

                var clamped = ClampToVisibleBounds(new RectInt32(rec.X, rec.Y, rec.W, rec.H));
                if (clamped.Width < MinVisibleSize || clamped.Height < MinVisibleSize)
                {
                    appWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
                    PositionTopRightOnPrimaryDisplay(appWindow, DefaultWidth, DefaultHeight);
                    return;
                }

                appWindow.MoveAndResize(clamped);

                if (rec.Maximized && appWindow.Presenter is OverlappedPresenter presenter)
                {
                    try { presenter.Maximize(); } catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"InspectorWindowStateStore: apply geometry failed: {ex.Message}",
                    "InspectorWindowStateStore", LogLevel.Debug);
            }
        }

        if (dispatcher is null)
        {
            // No dispatcher captured — we are on the Task.Run pool thread with no way
            // to marshal back to the UI thread. AppWindow geometry calls have strict
            // UI-thread affinity, so attempting Apply() here would only throw. Skip
            // the restore (best-effort, matching this store's other failure paths).
            GlobalLogger.Log(
                "InspectorWindowStateStore: no dispatcher; skipping geometry restore.",
                "InspectorWindowStateStore", LogLevel.Debug);
            return;
        }
        if (dispatcher.HasThreadAccess)
            Apply();
        else
            dispatcher.TryEnqueue(Apply);
    }

    private static void Capture(AppWindow appWindow)
    {
        int w = appWindow.Size.Width;
        int h = appWindow.Size.Height;
        if (w < MinVisibleSize || h < MinVisibleSize) return;

        bool maximized = false;
        if (appWindow.Presenter is OverlappedPresenter p)
            maximized = p.State == OverlappedPresenterState.Maximized;

        SaveRecord(new WindowRecord
        {
            X = appWindow.Position.X,
            Y = appWindow.Position.Y,
            W = w,
            H = h,
            Maximized = maximized,
        });
    }

    private static string ResolvePath()
    {
        try
        {
            string dir = Paths.RoamingAppData("Architect");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "inspector-state.json");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "inspector-state.json");
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
            GlobalLogger.Log(
                $"InspectorWindowStateStore: load failed ({ex.Message}); using defaults.",
                "InspectorWindowStateStore", LogLevel.System);
            return null;
        }
    }

    private static void SaveRecord(WindowRecord rec)
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
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(() =>
            {
                try { File.WriteAllText(ResolvePath(), json); }
                catch (Exception ex)
                {
                    GlobalLogger.Log(
                        $"InspectorWindowStateStore: persist write failed: {ex.Message}",
                        "InspectorWindowStateStore", LogLevel.Debug);
                }
            }),
            "InspectorWindowStateStore", "SaveRecord");
    }

    private static AppWindow ResolveAppWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    /// <summary>
    /// First-launch placement — top-right of the primary display, 16 px
    /// from the work-area edges. Inspector windows traditionally dock to
    /// the right of the editor, so the default position mirrors that
    /// muscle memory even though the window is freely movable.
    /// </summary>
    private static void PositionTopRightOnPrimaryDisplay(AppWindow appWindow, int width, int height)
    {
        var primary = DisplayArea.Primary;
        if (primary is null) return;
        var work = primary.WorkArea;
        // Clamp the right-edge offset: on a display narrower than the window
        // (width > work.Width - 16) the bare subtraction goes negative and
        // pushes the window off-screen left of work.X. Math.Max keeps X within
        // the work area's left edge.
        appWindow.Move(new PointInt32(
            work.X + Math.Max(0, work.Width - width - 16),
            work.Y + 16));
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
