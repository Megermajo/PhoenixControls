using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Phoenix.Controls.Hub.WinUI.Services;

/// <summary>
/// Sprint H — persists which Hub pop-out windows were open at the last
/// shutdown so they re-spawn at their saved geometry on the next launch.
///
/// Storage: one JSON file at
/// <c>%AppData%/PhoenixControls/Hub/popout-state.json</c>. Modeled after
/// <see cref="MainWindowStateStore"/> — same display-clamping rules,
/// same best-effort I/O posture (a transient write failure must never
/// crash the close path).
///
/// The store knows about panels by name (LiveFeed / Chat / Scripts /
/// SystemLog) — this is a Hub-internal contract and the matching ID
/// lives on <see cref="PopOutKind"/>. The HubWorkspaceView owns the
/// re-spawn loop; the store only handles serialisation + clamping.
/// </summary>
internal static class PopOutStateStore
{
    private const int MinVisibleSize  = 200;
    private const int DefaultWidth    = 720;
    private const int DefaultHeight   = 480;

    /// <summary>
    /// Identifies which factory method to invoke when re-spawning a saved
    /// pop-out. Stored as the string name in JSON — adding a new pop-out
    /// kind in a future build won't crash older state files (unrecognised
    /// names are dropped on load).
    /// </summary>
    public enum PopOutKind { LiveFeed, Chat, Scripts, SystemLog }

    public sealed class PopOutRecord
    {
        public string Kind { get; set; } = string.Empty;
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; } = DefaultWidth;
        public int H { get; set; } = DefaultHeight;
    }

    /// <summary>
    /// Reads the saved pop-out list. Records with unrecognised Kind values
    /// or sub-minimum sizes are dropped silently. Returns an empty list when
    /// the file doesn't exist or fails to deserialise.
    /// </summary>
    public static IReadOnlyList<PopOutRecord> Load()
    {
        try
        {
            string path = ResolvePath();
            if (!File.Exists(path)) return Array.Empty<PopOutRecord>();
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PopOutRecord>();
            var records = JsonSerializer.Deserialize<List<PopOutRecord>>(json);
            if (records is null) return Array.Empty<PopOutRecord>();

            var filtered = new List<PopOutRecord>(records.Count);
            foreach (var r in records)
            {
                if (string.IsNullOrEmpty(r.Kind)) continue;
                if (!Enum.TryParse<PopOutKind>(r.Kind, ignoreCase: true, out _)) continue;
                if (r.W < MinVisibleSize || r.H < MinVisibleSize) continue;
                filtered.Add(r);
            }
            return filtered;
        }
        catch (Exception ex)
        {
            // Same posture as MainWindowStateStore — promote to System so a
            // corrupt state file is visible in Release; the user otherwise
            // sees their pop-outs silently fail to restore.
            GlobalLogger.Log(
                $"PopOutStateStore: load failed ({ex.Message}); no pop-outs restored.",
                "PopOutStateStore", LogLevel.System);
            return Array.Empty<PopOutRecord>();
        }
    }

    /// <summary>
    /// Writes the current set of open pop-outs to disk. Records below the
    /// minimum visible size are skipped (a transient zero-size during close
    /// would otherwise pin a usable pop-out into "won't restore" state).
    /// Best-effort — IO failures log and return without raising.
    /// </summary>
    public static void Save(IEnumerable<PopOutRecord> records)
    {
        try
        {
            string path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var clean = new List<PopOutRecord>();
            foreach (var r in records)
            {
                if (r is null) continue;
                if (string.IsNullOrEmpty(r.Kind)) continue;
                if (r.W < MinVisibleSize || r.H < MinVisibleSize) continue;
                clean.Add(r);
            }
            string json = JsonSerializer.Serialize(clean, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"PopOutStateStore: save failed ({ex.Message}); next launch won't restore pop-outs.",
                "PopOutStateStore", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Snapshots a live pop-out Window's geometry into a record. Returns
    /// null when the AppWindow can't be resolved (window already closed)
    /// or the geometry is unusably small.
    /// </summary>
    public static PopOutRecord? CaptureFromWindow(Window window, PopOutKind kind)
    {
        if (window is null) return null;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow is null) return null;

            int w = appWindow.Size.Width;
            int h = appWindow.Size.Height;
            if (w < MinVisibleSize || h < MinVisibleSize) return null;

            return new PopOutRecord
            {
                Kind = kind.ToString(),
                X = appWindow.Position.X,
                Y = appWindow.Position.Y,
                W = w,
                H = h,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Applies a saved record's geometry to a freshly-created pop-out
    /// Window. Clamps to the closest connected display so a record from
    /// a now-disconnected monitor lands on a visible screen instead of
    /// vanishing off-screen.
    /// </summary>
    public static void ApplyToWindow(Window window, PopOutRecord record)
    {
        if (window is null || record is null) return;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow is null) return;

            var clamped = ClampToVisibleBounds(new RectInt32(record.X, record.Y, record.W, record.H));
            if (clamped.Width < MinVisibleSize || clamped.Height < MinVisibleSize)
            {
                appWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
                return;
            }
            appWindow.MoveAndResize(clamped);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"PopOutStateStore: apply failed ({ex.Message}); pop-out spawned at default size.",
                "PopOutStateStore", LogLevel.Debug);
        }
    }

    // ─── Auto-save on geometry change + per-window close ──────
    //
    // Pre-fix: Save fired only from HubWorkspaceView.Dispose() (i.e. once
    // at app shutdown). Mid-session pop-out closes left no breadcrumb on
    // disk, and a Hub crash threw away every geometry edit since launch.
    // The auto-save plumbing here closes both gaps:
    //
    //   - Track() subscribes to AppWindow.Changed and writes a debounced
    //     snapshot 1s after the user stops dragging / resizing.
    //   - Closed-per-window fires a final synchronous save so the closed
    //     pop-out drops out of the persisted set immediately (the next
    //     launch only restores still-open pop-outs).
    //
    // All tracking state is held in a lock-protected dictionary keyed by
    // Window so unrelated pop-outs don't serialise on each other. Each
    // entry carries the kind, a DispatcherQueueTimer for debounce, and
    // the live snapshot record so AppWindow.Changed only has to read
    // current geometry.

    private sealed class TrackedWindow
    {
        public Window Window = default!;
        public PopOutKind Kind;
        public DispatcherQueueTimer? Timer;
        // Keep the AppWindow + the exact Changed delegate so a re-track
        // or Untrack can unsubscribe the prior handler instead of stacking
        // duplicate handlers on the same AppWindow.
        public AppWindow? AppWindow;
        public global::Windows.Foundation.TypedEventHandler<AppWindow, AppWindowChangedEventArgs>? ChangedHandler;
    }

    private static readonly object s_trackedLock = new();
    private static readonly Dictionary<Window, TrackedWindow> s_tracked = new();

    /// <summary>
    /// Debounce delay between the last geometry-change event and the
    /// snapshot-to-disk write. 1s is long enough to absorb a fast resize
    /// drag without writing hundreds of times, and short enough that a
    /// power-yank seconds later still preserves the user's last edit.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Begins auto-saving geometry for <paramref name="window"/>. The
    /// caller (HubWorkspaceView.OpenPopOut) wires the Closed handler that
    /// removes the entry from the workspace's live list and the matching
    /// Untrack call drops it from the persisted snapshot.
    ///
    /// Safe to call before <c>window.Activate()</c> — AppWindow is
    /// resolvable as soon as the Window is constructed.
    /// </summary>
    public static void Track(Window window, PopOutKind kind)
    {
        if (window is null) return;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow is null) return;

            var entry = new TrackedWindow { Window = window, Kind = kind, AppWindow = appWindow };

            // AppWindow.Changed fires for both position and size moves; we
            // only care about persistence on either, so the debounce path
            // is identical for both event flavours. Captured as a named
            // delegate so it can be unsubscribed on re-track / Untrack.
            entry.ChangedHandler = (sender, args) =>
            {
                if (!args.DidPositionChange && !args.DidSizeChange) return;
                ScheduleDebouncedSave(entry, window.DispatcherQueue);
            };

            lock (s_trackedLock)
            {
                // Idempotent: re-registering the same Window replaces the
                // entry rather than stacking duplicates on AppWindow.Changed.
                if (s_tracked.TryGetValue(window, out var prior))
                {
                    try { prior.Timer?.Stop(); } catch { /* best-effort */ }
                    // Unsubscribe the prior handler so it stops firing
                    // on every geometry change — otherwise re-tracks pile up
                    // handlers that all schedule redundant debounced saves.
                    if (prior.AppWindow is not null && prior.ChangedHandler is not null)
                    {
                        try { prior.AppWindow.Changed -= prior.ChangedHandler; } catch { /* best-effort */ }
                    }
                }
                s_tracked[window] = entry;
            }

            appWindow.Changed += entry.ChangedHandler;

            // Final save on Closed so a mid-session close drops
            // immediately, and a Hub crash later doesn't restore the now-
            // gone pop-out. Saves are best-effort (Save() already swallows
            // IO faults to a log entry).
            window.Closed += (_, _) => Untrack(window);
        }
        catch (Exception ex)
        {
            GlobalLogger.Log(
                $"PopOutStateStore: Track failed ({ex.Message}); auto-save disabled for this window.",
                "PopOutStateStore", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Stops auto-saving for a window and flushes the current set to disk.
    /// Called from the Window.Closed handler set up by <see cref="Track"/>.
    /// Idempotent — safe to call from a workspace tear-down loop that
    /// also receives the close fire.
    /// </summary>
    public static void Untrack(Window window)
    {
        if (window is null) return;
        TrackedWindow? removed = null;
        lock (s_trackedLock)
        {
            if (s_tracked.Remove(window, out removed))
            {
                try { removed.Timer?.Stop(); } catch { /* best-effort */ }
            }
        }
        FlushNow();
    }

    private static void ScheduleDebouncedSave(TrackedWindow entry, DispatcherQueue? queue)
    {
        // Fall back to immediate save if no dispatcher is available — that
        // path is hit by test rigs that construct Windows outside the UI
        // thread; better a noisy write than a dropped persistence.
        if (queue is null)
        {
            FlushNow();
            return;
        }

        lock (s_trackedLock)
        {
            if (entry.Timer is null)
            {
                entry.Timer = queue.CreateTimer();
                entry.Timer.Interval = DebounceDelay;
                entry.Timer.IsRepeating = false;
                entry.Timer.Tick += (_, _) => Flush(offloadWrite: true);
            }
            // Stop+Start restarts the debounce window so a continuous
            // drag coalesces to a single write 1s after it ends.
            try { entry.Timer.Stop(); } catch { /* timer may not have started */ }
            try { entry.Timer.Start(); } catch { /* dispatcher may be tearing down */ }
        }
    }

    /// <summary>
    /// Snapshots every currently-tracked window and writes the result.
    /// Public so HubWorkspaceView.Dispose can force a final save at app
    /// shutdown without depending on the per-window Closed handlers
    /// having all fired first.
    /// </summary>
    public static void FlushNow() => Flush(offloadWrite: false);

    /// <summary>
    /// Snapshot every tracked window (MUST run on the UI thread — CaptureFromWindow
    /// reads live AppWindow geometry) and persist. The debounce-timer path passes
    /// <paramref name="offloadWrite"/>=true so the blocking disk write runs off the
    /// UI thread (a slow disk no longer stalls the UI on every pop-out move/resize);
    /// the shutdown path (<see cref="FlushNow"/>) writes synchronously so the file is
    /// flushed to disk before the process exits.
    /// </summary>
    private static void Flush(bool offloadWrite)
    {
        List<PopOutRecord> records;
        lock (s_trackedLock)
        {
            records = new List<PopOutRecord>(s_tracked.Count);
            foreach (var (_, entry) in s_tracked)
            {
                var rec = CaptureFromWindow(entry.Window, entry.Kind);
                if (rec is not null) records.Add(rec);
            }
        }
        if (offloadWrite)
            _ = Task.Run(() => Save(records));
        else
            Save(records);
    }

    // ─── Persistence path ────────────────────────────────────────────────

    private static string ResolvePath()
    {
        try
        {
            string dir = Paths.RoamingAppData("Hub");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "popout-state.json");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "popout-state-Hub.json");
        }
    }

    // ─── Display clamping (mirrors MainWindowStateStore) ─────────────────

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
