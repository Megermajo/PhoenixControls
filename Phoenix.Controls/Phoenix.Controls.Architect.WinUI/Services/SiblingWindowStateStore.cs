using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
/// Per-file-path window-position + size persistence for Architect's
/// <see cref="Phoenix.Controls.Architect.WinUI.Hosting.ArchitectSiblingWindow"/>. One JSON file per .phxg
/// path at <c>%AppData%/PhoenixControls/Architect/sibling-state-{hash}.json</c>
/// — hashed so absolute paths with spaces / Unicode round-trip safely
/// without colliding with sub-graph state keys.
///
/// Mirrors <see cref="SubGraphWindowStateStore"/>'s posture (best-effort,
/// log-and-continue on IO failure, clamp to visible bounds) — the two
/// stores are intentionally parallel so a future "shared window-state
/// store" rewrite can collapse them without rediscovering the
/// invariants.
/// </summary>
internal static class SiblingWindowStateStore
{
    private const int MinVisibleSize = 240;
    private const int DefaultWidth   = 1280;
    private const int DefaultHeight  = 820;

    // In-memory copy of every record this process has read or written,
    // keyed by the same hash the on-disk file name uses — Restore for a
    // path we've already touched never re-hits disk (the blocking
    // File.ReadAllText on the UI thread stalled window activation under
    // OneDrive / AV latency).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WindowRecord>
        s_recordCache = new(StringComparer.Ordinal);

    // Serialises the actual disk writes so a queued thread-pool persist
    // can't interleave with the terminal flushSync write. The per-key
    // sequence check keeps a stale queued write (captured before a newer
    // Persist) from landing after — and clobbering — the newer geometry.
    private static readonly object s_writeGate = new();
    private static readonly System.Collections.Generic.Dictionary<string, long>
        s_lastWrittenSeq = new(StringComparer.Ordinal);
    private static long s_saveSeq;

    private sealed class WindowRecord
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>
    /// Apply saved geometry for <paramref name="absolutePath"/> to the
    /// window's AppWindow. Falls back to a centred default-size launch
    /// when no record exists or the saved record is unusable. Safe to
    /// call before <see cref="Window.Activate"/>.
    /// </summary>
    public static void Restore(Window window, string absolutePath)
    {
        if (window is null || string.IsNullOrWhiteSpace(absolutePath)) return;
        try
        {
            var appWindow = ResolveAppWindow(window);
            ApplySavedState(appWindow, absolutePath);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SiblingWindowStateStore: restore failed for '{absolutePath}': {ex.Message}",
                "SiblingWindowStateStore", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Capture the window's current rectangle + maximized state and
    /// persist it. Call from the AppWindow.Closing handler so the rect
    /// lives on across destruction.
    /// </summary>
    /// <param name="flushSync">
    /// When true, the geometry is written
    /// synchronously instead of offloaded to the thread pool — used by the
    /// terminal window-close handler so the write lands before the host process
    /// can call Environment.Exit(0) (which would kill a still-queued Task.Run).
    /// The cancel-able AppWindow.Closing mid-session persists keep the async
    /// path so they don't stall the close under disk latency.
    /// </param>
    public static void Persist(Window window, string absolutePath, bool flushSync = false)
    {
        if (window is null || string.IsNullOrWhiteSpace(absolutePath)) return;
        try
        {
            var appWindow = ResolveAppWindow(window);
            Capture(appWindow, absolutePath, flushSync);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SiblingWindowStateStore: persist failed for '{absolutePath}': {ex.Message}",
                "SiblingWindowStateStore", LogLevel.Debug);
        }
    }

    private static void ApplySavedState(AppWindow appWindow, string absolutePath)
    {
        var rec = TryLoadRecord(absolutePath);

        if (rec is null || rec.W < MinVisibleSize || rec.H < MinVisibleSize)
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

        if (rec.Maximized && appWindow.Presenter is OverlappedPresenter presenter)
        {
            try { presenter.Maximize(); } catch { /* best-effort */ }
        }
    }

    private static void Capture(AppWindow appWindow, string absolutePath, bool flushSync = false)
    {
        int w = appWindow.Size.Width;
        int h = appWindow.Size.Height;
        if (w < MinVisibleSize || h < MinVisibleSize) return;

        bool maximized = false;
        if (appWindow.Presenter is OverlappedPresenter p)
            maximized = p.State == OverlappedPresenterState.Maximized;

        SaveRecord(absolutePath, new WindowRecord
        {
            X = appWindow.Position.X,
            Y = appWindow.Position.Y,
            W = w,
            H = h,
            Maximized = maximized,
        }, flushSync);
    }

    private static string ResolvePath(string absolutePath)
    {
        string key = HashPath(absolutePath);
        try
        {
            string dir = Paths.RoamingAppData("Architect");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"sibling-state-{key}.json");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                $"sibling-state-{key}.json");
        }
    }

    private static string HashPath(string path)
    {
        // SHA-1 truncated to 12 hex chars — collision risk is irrelevant
        // for per-user MRU-grade state, and Path.GetInvalidFileNameChars
        // would otherwise force us to encode every UNC + Unicode segment
        // by hand.
        using var sha = SHA1.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        var sb = new StringBuilder(12);
        for (int i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static WindowRecord? TryLoadRecord(string absolutePath)
    {
        string key = HashPath(absolutePath);
        // Cache-first: also covers a Persist whose thread-pool write is
        // still in flight — the cache already holds the newest geometry.
        if (s_recordCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            string path = ResolvePath(absolutePath);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var rec = JsonSerializer.Deserialize<WindowRecord>(json);
            if (rec is not null) s_recordCache[key] = rec;
            return rec;
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"SiblingWindowStateStore: load failed for '{absolutePath}' ({ex.Message}); using defaults.",
                "SiblingWindowStateStore", LogLevel.System);
            return null;
        }
    }

    private static void SaveRecord(string absolutePath, WindowRecord rec, bool flushSync = false)
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
        string key = HashPath(absolutePath);
        long seq = System.Threading.Interlocked.Increment(ref s_saveSeq);
        // Publish to the in-memory cache immediately so a Restore racing the
        // (possibly deferred) disk write sees the newest geometry.
        s_recordCache[key] = rec;
        // On the terminal close path, write
        // synchronously: the host process may Environment.Exit(0) immediately
        // after, killing a still-queued Task.Run and losing the geometry. The
        // record is tiny so the synchronous write is negligible at close time.
        if (flushSync)
        {
            try { WriteRecordGated(absolutePath, key, seq, json); }
            catch (Exception ex)
            {
                GlobalLogger.Log(
                    $"SiblingWindowStateStore: sync persist write failed for '{absolutePath}': {ex.Message}",
                    "SiblingWindowStateStore", LogLevel.Debug);
            }
            return;
        }
        _ = AsyncErrorBoundary.SafeRunAsync(
            () => System.Threading.Tasks.Task.Run(() =>
            {
                try { WriteRecordGated(absolutePath, key, seq, json); }
                catch (Exception ex)
                {
                    GlobalLogger.Log(
                        $"SiblingWindowStateStore: persist write failed for '{absolutePath}': {ex.Message}",
                        "SiblingWindowStateStore", LogLevel.Debug);
                }
            }),
            "SiblingWindowStateStore", "SaveRecord");
    }

    private static void WriteRecordGated(string absolutePath, string key, long seq, string json)
    {
        lock (s_writeGate)
        {
            // A newer Persist for this path already landed — writing this
            // stale snapshot would roll the geometry back.
            if (s_lastWrittenSeq.TryGetValue(key, out long last) && last >= seq) return;
            File.WriteAllText(ResolvePath(absolutePath), json);
            s_lastWrittenSeq[key] = seq;
        }
    }

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
