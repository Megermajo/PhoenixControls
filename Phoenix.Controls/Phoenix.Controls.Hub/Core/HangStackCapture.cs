using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// All-thread managed stack capture for UI-hang diagnosis. The
    /// UiHangWatchdog knows THAT the UI thread is stuck, but the breadcrumb
    /// tracer can only name instrumented scopes — both recorded freezes
    /// (2026-07-01, 2026-07-14) latched "scope already CLOSED", i.e. the stall
    /// lived in uninstrumented code where no breadcrumb reaches. This
    /// snapshots the running process (ClrMD snapshot-and-attach — the
    /// documented self-inspection path; a live invasive attach to the own
    /// process is not allowed) and walks every managed thread's stack, so a
    /// freeze names its exact blocked frame even inside framework calls.
    ///
    /// Runs entirely on a background thread while the UI thread is wedged.
    /// PssCaptureSnapshot clones the process copy-on-write, so the walk reads
    /// a consistent image without keeping the live process suspended.
    /// </summary>
    public static class HangStackCapture
    {
        // A hung thread's stack is rarely deep, but a runaway recursion is one
        // of the hang shapes this exists to catch — keep enough frames to see
        // the repeating cycle, then truncate.
        private const int MaxFramesPerThread = 128;

        private const string CaptureFilePrefix = "ui-hang-stacks-";
        private static readonly TimeSpan CaptureFileMaxAge = TimeSpan.FromDays(14);

        /// <summary>Structured capture — the text block plus the UI thread's own
        /// frames, so a single snapshot feeds both the .txt file and the freeze
        /// report's cause synthesis (a second snapshot would double the cost and
        /// risk two concurrent process reads).</summary>
        public sealed record CaptureResult(string Text, IReadOnlyList<string> UiThreadFrames);

        /// <summary>
        /// Capture every managed thread's stack into one diagnostic text block.
        /// <paramref name="uiManagedThreadId"/> / <paramref name="uiOsThreadId"/>
        /// mark the UI thread in the output; pass 0 when unknown (a stall before
        /// the first heartbeat was ever serviced). Throws on failure — callers
        /// wanting the guarded form route through <see cref="TryCaptureToFile"/>.
        /// </summary>
        public static string CaptureText(string reason, int uiManagedThreadId, uint uiOsThreadId)
            => Capture(reason, uiManagedThreadId, uiOsThreadId).Text;

        /// <summary>
        /// As <see cref="CaptureText"/>, but also returns the UI thread's own
        /// frames (top-of-stack first) for the freeze report. One snapshot.
        /// </summary>
        public static CaptureResult Capture(string reason, int uiManagedThreadId, uint uiOsThreadId)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine("Phoenix Controls UI-hang stack capture");
            sb.AppendLine($"Reason:    {reason}");
            sb.AppendLine($"Time:      {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} local / {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
            sb.AppendLine($"Process:   {Environment.ProcessId}");
            sb.AppendLine($"UI thread: managed={uiManagedThreadId} os=0x{uiOsThreadId:X} (0 = unknown)");
            sb.AppendLine();

            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            if (target.ClrVersions.Length == 0)
                throw new InvalidOperationException("No CLR runtime found in the process snapshot.");
            using var runtime = target.ClrVersions[0].CreateRuntime();

            // Materialize (thread, frames) up front so the report can order the
            // UI thread first, then other threads that have managed frames, then
            // a one-line summary of the frameless rest (native-only threads —
            // invisible to ClrMD, but their ids still matter when pairing a
            // deadlock against a full dump).
            var entries = new List<(ClrThread Thread, IReadOnlyList<string> Frames, bool IsUi)>();
            foreach (var thread in runtime.Threads)
            {
                bool isUi = (uiOsThreadId != 0 && thread.OSThreadId == uiOsThreadId)
                         || (uiManagedThreadId != 0 && thread.ManagedThreadId == uiManagedThreadId);
                var frames = new List<string>();
                try
                {
                    foreach (var frame in thread.EnumerateStackTrace())
                    {
                        if (frames.Count >= MaxFramesPerThread)
                        {
                            frames.Add("... (truncated)");
                            break;
                        }
                        frames.Add(frame?.ToString() ?? "<unknown frame>");
                    }
                }
                catch (Exception ex)
                {
                    // A single unwalkable thread must not void the capture.
                    frames.Add($"<stack walk failed: {ex.GetType().Name}: {ex.Message}>");
                }
                entries.Add((thread, frames, isUi));
            }

            int capturingManagedId = Environment.CurrentManagedThreadId;
            foreach (var (thread, frames, isUi) in entries
                         .OrderByDescending(e => e.IsUi)
                         .ThenByDescending(e => e.Frames.Count > 0))
            {
                if (frames.Count == 0)
                {
                    sb.AppendLine($"— Thread managed={thread.ManagedThreadId} os=0x{thread.OSThreadId:X}: (no managed frames)");
                    continue;
                }
                if (isUi) sb.AppendLine(">>> UI THREAD <<<");
                string note = thread.ManagedThreadId == capturingManagedId ? " (capture thread)"
                            : thread.IsFinalizer ? " (finalizer)"
                            : string.Empty;
                // LockCount reads uint.MaxValue ("unknown") under snapshot
                // readers — only print it when the runtime actually knows it.
                string lockInfo = thread.LockCount is > 0 and < uint.MaxValue
                    ? $" lockCount={thread.LockCount}" : string.Empty;
                sb.AppendLine($"— Thread managed={thread.ManagedThreadId} os=0x{thread.OSThreadId:X}{lockInfo}{note}");
                foreach (var f in frames)
                    sb.AppendLine($"    at {f}");
                sb.AppendLine();
            }

            // Hand the UI thread's own frames back for the freeze-report cause
            // synthesis (top-of-stack first). Empty when the UI thread wasn't
            // matched (unknown id) or had no managed frames.
            var uiEntry = entries.FirstOrDefault(e => e.IsUi);
            IReadOnlyList<string> uiFrames = uiEntry.Frames ?? Array.Empty<string>();
            return new CaptureResult(sb.ToString(), uiFrames);
        }

        /// <summary>
        /// Guarded capture-to-file. Writes the capture into
        /// <paramref name="directory"/> as <c>ui-hang-stacks-*.txt</c> and prunes
        /// siblings older than 14 days. Returns the written path, or null with
        /// <paramref name="error"/> set — never throws (a diagnostics failure
        /// must not add a second fault to an already-frozen app).
        /// </summary>
        public static string? TryCaptureToFile(
            string directory, string reason, int uiManagedThreadId, uint uiOsThreadId, out string? error)
        {
            try
            {
                string text = CaptureText(reason, uiManagedThreadId, uiOsThreadId);
                return TryWriteText(directory, text, out error);
            }
            catch (Exception ex)
            {
                string detail = ex.ToString();
                error = detail.Length > 600 ? detail[..600] : detail;
                return null;
            }
        }

        /// <summary>
        /// Write pre-captured stack text into <paramref name="directory"/> as
        /// <c>ui-hang-stacks-*.txt</c> and prune old siblings. Lets a caller that
        /// already ran <see cref="Capture"/> (to reuse its UI frames) persist the
        /// text without a second snapshot. Returns the path or null; never throws.
        /// </summary>
        public static string? TryWriteText(string directory, string text, out string? error)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(
                    directory, $"{CaptureFilePrefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
                File.WriteAllText(path, text);
                PruneOldCaptures(directory);
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

        // Keep the logs folder from accumulating stale captures — hangs are
        // rare, but every capture is tens of KB and nothing else cleans them.
        private static void PruneOldCaptures(string directory)
        {
            try
            {
                var cutoff = DateTime.UtcNow - CaptureFileMaxAge;
                foreach (var file in Directory.EnumerateFiles(directory, CaptureFilePrefix + "*.txt"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                    }
                    catch { /* locked / already gone — skip */ }
                }
            }
            catch { /* pruning is best-effort */ }
        }
    }
}
