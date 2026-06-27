using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// Persists each top-level Form's last position, size, and Normal/Maximized
    /// WindowState across sessions. Storage is one JSON file per app under
    /// <c>%AppData%/PhoenixControls/&lt;App&gt;/window-state.json</c>; the key
    /// inside the file defaults to the form's <c>Type.FullName</c> so each
    /// tracked top-level Form gets an independent slot.
    ///
    /// Restoration is bounded by <see cref="Screen.AllScreens"/> virtual bounds
    /// — a saved record from a now-disconnected monitor is clamped onto a
    /// visible screen rather than leaving the window invisible.
    ///
    /// Retained for the WinForms-based Viewer host. The WinUI pillars use
    /// their own per-window stores (<c>MainWindowStateStore</c>,
    /// <c>SubGraphWindowStateStore</c>, <c>SiblingWindowStateStore</c>) — the
    /// retired DockPanelSuite dock layout is no longer involved.
    /// </summary>
    public static class WindowStateStore
    {
        private const int MinVisibleSize = 100;

        private sealed class WindowRecord
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int W { get; set; }
            public int H { get; set; }
            public bool Maximized { get; set; }
        }

        private sealed class FileShape
        {
            public Dictionary<string, WindowRecord> Windows { get; set; }
                = new(StringComparer.Ordinal);
        }

        private static readonly object _ioLock = new();
        private static readonly Dictionary<string, FileShape> _cache
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Wires <paramref name="form"/> so its bounds + WindowState are restored
        /// before the first paint and re-saved when the form closes. Safe to call
        /// from the form's constructor.
        /// </summary>
        /// <param name="form">The form to track.</param>
        /// <param name="app">App identifier, e.g. "Hub", "Architect", "Visualist", "Viewer".</param>
        /// <param name="key">Optional key override; defaults to <c>form.GetType().FullName</c>.</param>
        public static void Attach(Form form, string app, string? key = null)
        {
            if (form is null) throw new ArgumentNullException(nameof(form));
            if (string.IsNullOrWhiteSpace(app)) throw new ArgumentException("app required", nameof(app));

            string resolvedKey = key ?? form.GetType().FullName ?? form.GetType().Name;

            // Restore on Load — Bounds set before Load fires won't survive
            // InitializeComponent's own size assignments on every form, so we
            // wait for Load. Setting StartPosition=Manual lets the explicit
            // bounds win over the default centre-screen behaviour the form
            // may have configured in its ctor.
            void OnLoad(object? s, EventArgs e)
            {
                form.Load -= OnLoad;
                ApplySavedState(form, app, resolvedKey);
            }
            form.Load += OnLoad;

            // FormClosing fires before disposal, while RestoreBounds is still
            // valid for a maximized window. FormClosed would also work but
            // some shutdown paths cancel the close and run their own teardown
            // — capturing here means the record is written even if
            // Application.Exit() short-circuits later events.
            void OnClosing(object? s, FormClosingEventArgs e)
            {
                CaptureCurrentState(form, app, resolvedKey);
            }
            form.FormClosing += OnClosing;
        }

        private static void ApplySavedState(Form form, string app, string key)
        {
            WindowRecord? rec = TryLoadRecord(app, key);
            if (rec is null) return;
            if (rec.W < MinVisibleSize || rec.H < MinVisibleSize) return;

            Rectangle desired = new(rec.X, rec.Y, rec.W, rec.H);
            Rectangle clamped = ClampToVisibleBounds(desired);
            if (clamped.Width < MinVisibleSize || clamped.Height < MinVisibleSize) return;

            // Honour MinimumSize so a corrupt or shrunk record can't drop the
            // form below its design minimum. MaximumSize is normally Empty
            // (== unlimited); only constrain when the form set one explicitly.
            Size min = form.MinimumSize;
            if (min.Width > 0 && clamped.Width < min.Width)
                clamped = new Rectangle(clamped.Location, new Size(min.Width, clamped.Height));
            if (min.Height > 0 && clamped.Height < min.Height)
                clamped = new Rectangle(clamped.Location, new Size(clamped.Width, min.Height));

            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = clamped;
            form.WindowState = rec.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }

        private static void CaptureCurrentState(Form form, string app, string key)
        {
            // Prefer RestoreBounds when maximized: that's the size+position the
            // form would snap back to if un-maximized — the right thing to
            // store so a later restore preserves both the maximized flag and
            // the underlying "window if unmaximized" rect. Minimized state is
            // never persisted (it would re-open invisible to the taskbar);
            // fall back to RestoreBounds in that case too.
            Rectangle bounds = form.WindowState == FormWindowState.Normal
                ? form.Bounds
                : form.RestoreBounds;

            if (bounds.Width < MinVisibleSize || bounds.Height < MinVisibleSize) return;

            var rec = new WindowRecord
            {
                X = bounds.X,
                Y = bounds.Y,
                W = bounds.Width,
                H = bounds.Height,
                Maximized = form.WindowState == FormWindowState.Maximized,
            };
            SaveRecord(app, key, rec);
        }

        private static WindowRecord? TryLoadRecord(string app, string key)
        {
            FileShape file = LoadFile(app);
            return file.Windows.TryGetValue(key, out var rec) ? rec : null;
        }

        private static void SaveRecord(string app, string key, WindowRecord rec)
        {
            try
            {
                lock (_ioLock)
                {
                    FileShape file = LoadFile(app);
                    file.Windows[key] = rec;
                    string path = ResolvePath(app);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    string json = JsonSerializer.Serialize(file, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    });
                    File.WriteAllText(path, json);
                }
            }
            catch (Exception ex)
            {
                // Persistence is best-effort. A failure here must never crash
                // the close path — the user closing the app would lose the
                // window over a transient IO error otherwise.
                GlobalLogger.Log(
                    $"WindowStateStore: failed to save '{app}/{key}': {ex.Message}",
                    "WindowStateStore",
                    Phoenix.Controls.Shared.Models.LogLevel.Debug);
            }
        }

        private static FileShape LoadFile(string app)
        {
            lock (_ioLock)
            {
                if (_cache.TryGetValue(app, out var cached)) return cached;

                FileShape loaded = new();
                try
                {
                    string path = ResolvePath(app);
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            loaded = JsonSerializer.Deserialize<FileShape>(json) ?? new FileShape();
                            loaded.Windows ??= new(StringComparer.Ordinal);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Corrupt JSON or transient read error — start fresh
                    // rather than refusing to lay out windows.
                    GlobalLogger.Log(
                        $"WindowStateStore: failed to load '{app}': {ex.Message}",
                        "WindowStateStore",
                        Phoenix.Controls.Shared.Models.LogLevel.Debug);
                    loaded = new FileShape();
                }
                _cache[app] = loaded;
                return loaded;
            }
        }

        private static string ResolvePath(string app)
        {
            try
            {
                string dir = Phoenix.Controls.Shared.Core.Paths.RoamingAppData(app);
                if (!string.IsNullOrEmpty(dir))
                    return Path.Combine(dir, "window-state.json");
            }
            catch { /* fall through */ }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"window-state-{app}.json");
        }

        // Returns the largest portion of <paramref name="desired"/> that lies
        // on a connected screen. If the rect is fully off-screen (typical
        // after disconnecting the monitor it was last on) we shift it onto
        // the screen with the largest overlap to its centre, then onto the
        // primary screen as a final fallback.
        private static Rectangle ClampToVisibleBounds(Rectangle desired)
        {
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) return desired;

            // Fast path: any screen that contains the centre point keeps the
            // rect as-is (still inside that monitor's working area).
            Point centre = new(desired.X + desired.Width / 2, desired.Y + desired.Height / 2);
            foreach (Screen s in screens)
            {
                if (s.WorkingArea.Contains(centre))
                {
                    return ClampInside(desired, s.WorkingArea);
                }
            }

            // Slow path: find the screen with the largest intersection.
            Screen best = Screen.PrimaryScreen ?? screens[0];
            int bestArea = 0;
            foreach (Screen s in screens)
            {
                Rectangle isect = Rectangle.Intersect(desired, s.WorkingArea);
                int area = isect.Width * isect.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = s;
                }
            }
            return ClampInside(desired, best.WorkingArea);
        }

        private static Rectangle ClampInside(Rectangle r, Rectangle area)
        {
            int w = Math.Min(r.Width, area.Width);
            int h = Math.Min(r.Height, area.Height);
            int x = Math.Max(area.Left, Math.Min(r.X, area.Right - w));
            int y = Math.Max(area.Top,  Math.Min(r.Y, area.Bottom - h));
            return new Rectangle(x, y, w, h);
        }
    }
}
