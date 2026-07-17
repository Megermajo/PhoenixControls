using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// The consolidated, human-readable freeze report — the artifact that makes a
    /// freeze EXPLAIN ITSELF instead of just leaving a raw stack file. One
    /// <c>ui-freeze-report-*.txt</c> per freeze gathers, in one place: the stall
    /// metadata, a synthesized "LIKELY CAUSE" line, the UI-thread top frames, the
    /// GPU driver + any display-reset/TDR events, and the last N log breadcrumbs
    /// showing what was running. The raw <c>.dmp</c> / <c>.txt</c> stay the
    /// ground truth for WinDbg; this is the at-a-glance "what happened and why".
    /// </summary>
    public static class FreezeReport
    {
        private const string ReportFilePrefix = "ui-freeze-report-";
        private static readonly TimeSpan ReportFileMaxAge = TimeSpan.FromDays(14);
        private const int MaxRetainedReports = 20;

        /// <summary>Stall metadata the watchdog already knows at trip time.</summary>
        public readonly record struct Context(
            double StalledSeconds,
            double ThresholdSeconds,
            string LastActivity,
            string LastActivityAge,
            string ScopeState,
            int UiManagedThreadId,
            uint UiOsThreadId);

        /// <summary>
        /// Turn the UI-thread frames + TDR scan into a one-paragraph plain-language
        /// verdict. PURE (no IO) so it's unit-testable — it's the "highlight the
        /// issue" core. Decides native-vs-managed by whether any UI-thread frame is
        /// real app/framework code above the message-pump entry chain.
        /// </summary>
        public static string SynthesizeLikelyCause(
            IReadOnlyList<string> uiThreadFrames,
            IReadOnlyList<GpuTdrProbe.TdrHit> tdrHits)
        {
            string? active = FirstActiveFrame(uiThreadFrames);
            bool tdr = tdrHits is { Count: > 0 };

            if (active is null)
            {
                // No managed app frames above Application.Start → the UI thread is
                // blocked in NATIVE code (this is every recorded Phoenix freeze).
                if (tdr)
                {
                    var h = tdrHits[0];
                    return $"GPU DRIVER STALL (TDR). The UI thread has no managed frames above the message " +
                           $"pump — it is blocked in native GPU/rendering code — AND the Windows System log recorded a " +
                           $"display-driver reset ('{h.Provider}' event {h.EventId}) at {h.TimeUtc:HH:mm:ss}Z, right around the " +
                           $"freeze. That reset IS the freeze: the display driver stopped responding and recovered, stalling " +
                           $"the render thread. This is a driver/GPU issue (update or roll back the display driver, or reduce " +
                           $"GPU load), NOT a Phoenix logic bug.";
                }
                return "NATIVE UI-THREAD BLOCK. The UI thread has no managed frames above the message pump " +
                       "(Application.Start) — it is wedged in a native call (most likely GPU present/composition, " +
                       "possibly DirectWrite or another synchronous OS call). No display-driver TDR was found in the scan " +
                       "window, so if this recurs open the .dmp in WinDbg — the UI thread's native stack names the exact " +
                       "blocking module (dxgi/d3d*, the vendor driver, dwrite, …).";
            }

            if (LooksLikeWait(active))
                return $"MANAGED BLOCKING WAIT on the UI thread: {active}. The UI thread is parked on a lock / " +
                       "semaphore / sync-over-async wait — it is not a GPU stall. Trace back from this frame to what it is " +
                       "waiting on (a background task, a lock held elsewhere).";

            return $"MANAGED UI-THREAD WORK: {active}. The UI thread is actively executing (or looping in) this managed " +
                   "frame rather than pumping messages — a long-running or infinite operation on the UI thread. This is a " +
                   "Phoenix code path, not a GPU stall.";
        }

        /// <summary>Assemble the full report text.</summary>
        public static string Build(
            Context ctx,
            IReadOnlyList<string> uiThreadFrames,
            IReadOnlyList<Log> breadcrumbs,
            IReadOnlyList<string> gpuModules,
            IReadOnlyList<GpuTdrProbe.TdrHit> tdrHits,
            TimeSpan tdrWindow,
            string? dumpPath,
            string? textPath,
            int maxBreadcrumbs = 30)
        {
            var sb = new StringBuilder(16 * 1024);
            sb.AppendLine("Phoenix Controls FREEZE REPORT");
            sb.AppendLine($"Time:      {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} local / {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
            sb.AppendLine($"Process:   {Environment.ProcessId}");
            sb.AppendLine($"Stall:     ~{ctx.StalledSeconds.ToString("F1", CultureInfo.InvariantCulture)}s unresponsive " +
                          $"(trip threshold {ctx.ThresholdSeconds.ToString("F0", CultureInfo.InvariantCulture)}s)");
            sb.AppendLine($"UI thread: managed={ctx.UiManagedThreadId} os=0x{ctx.UiOsThreadId:X}");
            sb.AppendLine($"Last traced UI activity: '{ctx.LastActivity}' (started {ctx.LastActivityAge}; {ctx.ScopeState})");
            sb.AppendLine();

            sb.AppendLine(">>> LIKELY CAUSE <<<");
            sb.AppendLine(SynthesizeLikelyCause(uiThreadFrames, tdrHits));
            sb.AppendLine();

            sb.AppendLine("UI THREAD top frames:");
            if (uiThreadFrames is { Count: > 0 })
                foreach (var f in uiThreadFrames.Take(16)) sb.AppendLine($"    {f}");
            else
                sb.AppendLine("    (none captured)");
            sb.AppendLine();

            sb.AppendLine($"GPU / display (TDR scan window: last {tdrWindow.TotalMinutes:F0} min):");
            sb.AppendLine("  Loaded graphics modules:");
            if (gpuModules is { Count: > 0 })
                foreach (var m in gpuModules) sb.AppendLine($"    - {m}");
            else
                sb.AppendLine("    (none identified)");
            sb.AppendLine("  Display-reset / TDR events:");
            if (tdrHits is { Count: > 0 })
                foreach (var h in tdrHits)
                    sb.AppendLine($"    [{h.TimeUtc:HH:mm:ss}Z] {h.Provider}/{h.EventId}: {h.Message}");
            else
                sb.AppendLine("    none found in window");
            sb.AppendLine();

            sb.AppendLine($"Recent activity (last {maxBreadcrumbs} log entries before the freeze):");
            if (breadcrumbs is { Count: > 0 })
            {
                foreach (var e in breadcrumbs.Skip(Math.Max(0, breadcrumbs.Count - maxBreadcrumbs)))
                    sb.AppendLine($"    {e.Timestamp:HH:mm:ss.fff} [{e.Level}] {e.Source}: {OneLine(e.Message)}");
            }
            else
            {
                sb.AppendLine("    (log ring empty)");
            }
            sb.AppendLine();

            sb.AppendLine("Artifacts:");
            sb.AppendLine($"  Native minidump: {dumpPath ?? "(not written)"}");
            sb.AppendLine($"  Managed stacks:  {textPath ?? "(not written)"}");
            return sb.ToString();
        }

        /// <summary>
        /// Write the report into <paramref name="directory"/> as
        /// <c>ui-freeze-report-*.txt</c> and prune old siblings. Returns the path,
        /// or null with <paramref name="error"/> set. Never throws.
        /// </summary>
        public static string? TryWriteToFile(string directory, string reportText, out string? error)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(
                    directory, $"{ReportFilePrefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
                File.WriteAllText(path, reportText);
                Prune(directory);
                error = null;
                return path;
            }
            catch (Exception ex)
            {
                string detail = ex.ToString();
                error = detail.Length > 600 ? detail[..600] : detail;
                return null;
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────

        // Frames that are the message-pump entry chain or a managed↔native
        // transition marker — NOT evidence of the UI thread running app code.
        private static bool IsEntryOrTransitionFrame(string f)
        {
            if (string.IsNullOrEmpty(f)) return true;
            return f.Contains("InlinedCallFrame", StringComparison.Ordinal)
                || f.Contains("Application.Start", StringComparison.Ordinal)
                || f.Contains("IApplicationStaticsMethods", StringComparison.Ordinal)
                || f.Contains("Program.Main", StringComparison.Ordinal)
                || f.Contains("DebuggerU2MCatchHandlerFrame", StringComparison.Ordinal);
        }

        // The topmost frame that is real app/framework work above the pump entry;
        // null when the whole stack is just the entry chain (= native block).
        private static string? FirstActiveFrame(IReadOnlyList<string> frames)
        {
            if (frames is null) return null;
            foreach (var f in frames)
                if (!IsEntryOrTransitionFrame(f)) return f;
            return null;
        }

        private static bool LooksLikeWait(string frame)
        {
            return frame.Contains("Monitor.Wait", StringComparison.Ordinal)
                || frame.Contains("WaitHandle", StringComparison.Ordinal)
                || frame.Contains("SemaphoreSlim", StringComparison.Ordinal)
                || frame.Contains(".Wait(", StringComparison.Ordinal)
                || frame.Contains("GetResult", StringComparison.Ordinal)
                || frame.Contains(".Result", StringComparison.Ordinal)
                || frame.Contains("ManualResetEvent", StringComparison.Ordinal)
                || frame.Contains("Thread.Sleep", StringComparison.Ordinal);
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length > 300 ? s[..300] + "…" : s;
        }

        private static void Prune(string directory)
        {
            try
            {
                var files = Directory.EnumerateFiles(directory, ReportFilePrefix + "*.txt").ToList();
                var cutoff = DateTime.UtcNow - ReportFileMaxAge;
                foreach (var file in files.ToList())
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) { File.Delete(file); files.Remove(file); }
                    }
                    catch { /* locked / gone */ }
                }
                if (files.Count > MaxRetainedReports)
                {
                    foreach (var file in files
                                 .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                                 .Skip(MaxRetainedReports))
                    {
                        try { File.Delete(file); } catch { /* best effort */ }
                    }
                }
            }
            catch { /* pruning is best-effort */ }
        }
    }
}
