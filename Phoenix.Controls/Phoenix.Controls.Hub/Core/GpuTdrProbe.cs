using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using SysProcess = System.Diagnostics.Process;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Windows-side GPU context for the freeze report: whether the display
    /// driver reset (a TDR — "Timeout Detection &amp; Recovery") near the freeze,
    /// and which graphics driver is loaded. Every recorded freeze is a native
    /// UI-thread wait with the leading hypothesis "GPU present/composition stall";
    /// a TDR event in the Windows System log at the freeze time is the direct
    /// smoking gun for that, and the loaded vendor driver + version names who to
    /// point at (a driver update, a known-bad build). All best-effort: any
    /// failure (event-log access denied, module walk error) yields empty, never
    /// throws — a diagnostics probe must not add a fault to an already-frozen app.
    /// </summary>
    public static class GpuTdrProbe
    {
        /// <summary>A display-driver reset / TDR event pulled from the System log.</summary>
        public readonly record struct TdrHit(DateTime TimeUtc, string Provider, int EventId, string Message);

        // Substrings that mark a graphics-related event provider or loaded module.
        private static readonly string[] GraphicsMarkers =
        {
            "nvlddmkm", "nvwgf2", "nvd3d", "nvldumd", "nvapi", "nvcuda", // NVIDIA
            "amdkmdag", "amdwddmg", "amdxc", "atidx", "atiumd", "amdvlk", // AMD
            "igd", "igfx", "igc", "intelocl",                            // Intel
            "dxgkrnl", "dxgmms", "dxgi", "d3d10", "d3d11", "d3d12", "d3d9",
            "d2d1", "dcomp", "dwmapi", "dxcore", "display", "vidmm",
        };

        /// <summary>
        /// Scan the Windows System event log for display-driver reset / TDR events
        /// in the last <paramref name="window"/>. Catches both the canonical
        /// Display/4101 ("display driver stopped responding and has recovered")
        /// and vendor-specific driver errors. Returns newest-first, capped.
        /// </summary>
        public static IReadOnlyList<TdrHit> RecentDisplayResets(TimeSpan window, int max = 10)
        {
            var hits = new List<TdrHit>();
            try
            {
                long windowMs = (long)Math.Max(1000, window.TotalMilliseconds);
                // Level 1=Critical 2=Error 3=Warning. TDR-recovered (Display/4101)
                // is a Warning; vendor TDRs are Errors. timediff() is ms since the
                // event, so <= windowMs keeps only the recent tail.
                string xpath =
                    "*[System[(Level=1 or Level=2 or Level=3) and " +
                    $"TimeCreated[timediff(@SystemTime) <= {windowMs.ToString(CultureInfo.InvariantCulture)}]]]";
                var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                for (EventRecord? rec = reader.ReadEvent(); rec is not null && hits.Count < max; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        string provider = rec.ProviderName ?? "";
                        int id = rec.Id;
                        bool isDisplayTdr = provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && id == 4101;
                        bool isGraphicsProvider = LooksGraphics(provider);
                        if (!isDisplayTdr && !isGraphicsProvider) continue;

                        DateTime utc = (rec.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();
                        string msg;
                        try { msg = rec.FormatDescription() ?? ""; }
                        catch { msg = ""; }
                        if (msg.Length > 300) msg = msg[..300];
                        hits.Add(new TdrHit(utc, provider, id, msg.Replace('\r', ' ').Replace('\n', ' ').Trim()));
                    }
                }
            }
            catch
            {
                // Access denied / log unavailable / query invalid — skip silently.
            }
            return hits;
        }

        /// <summary>
        /// The graphics-family modules currently loaded into this process, with
        /// file versions — names the GPU vendor + driver build behind a native
        /// rendering stall. Best-effort; empty on any failure.
        /// </summary>
        public static IReadOnlyList<string> LoadedGraphicsModules(int max = 20)
        {
            var list = new List<string>();
            try
            {
                foreach (System.Diagnostics.ProcessModule m in SysProcess.GetCurrentProcess().Modules)
                {
                    try
                    {
                        string name = m.ModuleName ?? "";
                        if (!LooksGraphics(name)) continue;
                        string ver = "";
                        try { ver = m.FileVersionInfo?.FileVersion ?? ""; } catch { }
                        list.Add(string.IsNullOrEmpty(ver) ? name : $"{name} (v{ver})");
                        if (list.Count >= max) break;
                    }
                    catch { /* per-module best effort */ }
                }
            }
            catch { /* module walk failed */ }
            // Vendor drivers first (more informative than the always-present d3d/dxgi).
            return list
                .OrderByDescending(s => IsVendorDriver(s))
                .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool LooksGraphics(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string lower = s.ToLowerInvariant();
            return GraphicsMarkers.Any(m => lower.Contains(m));
        }

        private static bool IsVendorDriver(string s)
        {
            string lower = s.ToLowerInvariant();
            return lower.Contains("nv") || lower.Contains("amd") || lower.Contains("ati") || lower.Contains("igd") || lower.Contains("igfx");
        }
    }
}
