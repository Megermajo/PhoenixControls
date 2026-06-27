using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// Lightweight rolling-file mirror of GlobalLogger's problem-level entries.
    /// Exists so a freeze / crash post-mortem doesn't depend on the SQLite
    /// <c>SystemHistory</c> table — which is the only place logs are persisted
    /// today and is frequently locked by the running suite (WAL) when you most
    /// need to read it. Captures <see cref="LogLevel.CriticalError"/> +
    /// <see cref="LogLevel.System"/> only (errors + startup/lifecycle context);
    /// the high-volume chat / debug / logic-execution firehose stays in the DB.
    ///
    /// Files live under <c>%AppData%/PhoenixControls/logs/phoenix-hub-YYYYMMDD.log</c>,
    /// one per day, rotated at <see cref="MaxFileBytes"/> and pruned after
    /// <see cref="RetentionDays"/>.
    ///
    /// <para/>
    /// [freeze sweep] <see cref="GlobalLogger.OnLogEntry"/> fires synchronously
    /// on the PRODUCER thread — which includes the UI thread. The original
    /// implementation took a lock and did <c>File.AppendAllText</c> (+ an
    /// occasional rotation <c>File.Move</c>) inline under that lock, so a UI
    /// thread that logged a System / CriticalError entry blocked on disk I/O —
    /// and worse, contended with any other thread mid-append. Under a burst
    /// (reconnect/error cascade) or slow disk (OneDrive / AV scan) that became
    /// a self-inflicted freeze, ironic for the instrument added to diagnose
    /// freezes. Now the producer only formats the line and drops it on a bounded
    /// queue (non-blocking, drop-on-overflow); a single background writer thread
    /// drains the queue and does all disk I/O off the producer's back.
    /// </summary>
    public static class DiagnosticFileLog
    {
        /// <summary>
        /// 0.11.x polish — resolve the path to today's rolling log file.
        /// Returns the path even when the file hasn't been created yet
        /// (the writer creates it lazily). The Help → Diagnose freeze…
        /// menu uses this to open the file so the user can scan for the
        /// "UI thread unresponsive" markers UiHangWatchdog emits when a
        /// stall happens.
        /// </summary>
        public static string TodayLogPath()
        {
            string dir = string.IsNullOrEmpty(_dir) ? Paths.RoamingAppData("logs") : _dir;
            return Path.Combine(dir, $"phoenix-hub-{DateTime.Now:yyyyMMdd}.log");
        }

        private const int  RetentionDays   = 7;
        private const long MaxFileBytes    = 8L * 1024 * 1024; // 8 MB → rotate
        private const int  QueueCapacity   = 4096;             // drop beyond this

        private static int _started;             // 0 = not started, 1 = started
        private static string _dir = string.Empty;
        private static BlockingCollection<string>? _queue;
        private static Thread? _writer;

        /// <summary>
        /// Idempotent. Resolves + creates the logs directory, prunes old files,
        /// starts the background writer, and begins mirroring problem-level
        /// GlobalLogger entries to disk. Best-effort: a failure here (e.g.
        /// AppData unwritable) silently leaves the SQLite sink as the sole
        /// record rather than breaking startup.
        /// </summary>
        public static void Start()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            try
            {
                _dir = Paths.RoamingAppData("logs");
                Directory.CreateDirectory(_dir);
                PruneOld();

                _queue = new BlockingCollection<string>(boundedCapacity: QueueCapacity);
                _writer = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "DiagnosticFileLog",
                };
                _writer.Start();

                GlobalLogger.OnLogEntry += OnLogEntry;
                Enqueue("DiagnosticFileLog", LogLevel.System,
                    $"--- diagnostic file log started (pid {Environment.ProcessId}) ---");
            }
            catch (Exception ex)
            {
                // Diagnostics must never break startup, but surface the failure
                // to the debugger so an unwritable AppData / thread-start fault
                // is diagnosable rather than vanishing silently.
                System.Diagnostics.Debug.WriteLine($"[DiagnosticFileLog] Start failed: {ex}");
            }
        }

        /// <summary>
        /// Idempotent graceful shutdown. Stops accepting new entries, signals
        /// the writer to drain the queue and exit, then joins it (bounded) so
        /// queued lines reach disk before the process terminates. Best-effort:
        /// shutdown must never throw. Wire this from the application shutdown
        /// path (e.g. App.xaml.cs / HubBootstrapper) before the process exits.
        /// </summary>
        public static void Stop()
        {
            if (Interlocked.CompareExchange(ref _started, 0, 1) != 1) return;
            try { GlobalLogger.OnLogEntry -= OnLogEntry; } catch { /* best-effort */ }

            var q = _queue;
            var w = _writer;
            try
            {
                // Signal end-of-production: the writer's GetConsumingEnumerable
                // drains remaining lines, then exits the loop (InvalidOperation
                // is already handled in WriterLoop).
                q?.CompleteAdding();
            }
            catch { /* already completed / disposed — fine */ }

            try { w?.Join(TimeSpan.FromSeconds(2)); }
            catch { /* never throw from shutdown */ }

            try { q?.Dispose(); }
            catch { /* already disposed — fine */ }

            _queue = null;
            _writer = null;
        }

        private static void OnLogEntry(Log entry)
        {
            // Mirror only problem + lifecycle-context levels; skip the firehose.
            if (entry.Level != LogLevel.CriticalError && entry.Level != LogLevel.System)
                return;
            Enqueue(entry.Source, entry.Level, entry.Message);
        }

        /// <summary>
        /// Producer side — runs on whatever thread logged (often the UI thread).
        /// Formats the line (so the timestamp reflects produce time, not write
        /// time) and hands it to the background writer. Never blocks: a bounded
        /// <c>TryAdd</c> drops the line if the queue is saturated rather than
        /// stalling the caller.
        /// </summary>
        private static void Enqueue(string source, LogLevel level, string message)
        {
            var q = _queue;
            if (q is null) return;
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {source}: {message}{Environment.NewLine}";
            try { q.TryAdd(line); }
            catch { /* queue completed / disposed during shutdown — fine */ }
        }

        /// <summary>
        /// Consumer side — the single background writer thread. Blocks on the
        /// queue, then coalesces every line currently available into one
        /// <c>File.AppendAllText</c> so a burst becomes a single disk write.
        /// All disk latency lives here, off the producer threads.
        /// </summary>
        private static void WriterLoop()
        {
            var q = _queue;
            if (q is null) return;
            var sb = new StringBuilder();
            try
            {
                foreach (var first in q.GetConsumingEnumerable())
                {
                    sb.Clear();
                    sb.Append(first);
                    // Drain anything else that piled up while we were blocked
                    // so a flood collapses into one open/append/close.
                    while (q.TryTake(out var more)) sb.Append(more);
                    WriteBatch(sb.ToString());
                }
            }
            catch (ObjectDisposedException) { /* shutdown */ }
            catch (InvalidOperationException) { /* CompleteAdding + drained */ }
            catch (Exception ex)
            {
                // Never throw from the logging writer, but surface I/O faults
                // (disk full, permissions, OneDrive sync) to the debugger so a
                // silently-failing diagnostic sink is itself diagnosable.
                System.Diagnostics.Debug.WriteLine($"[DiagnosticFileLog] writer loop failed: {ex}");
            }
        }

        private static void WriteBatch(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(_dir)) return;
                string path = Path.Combine(_dir, $"phoenix-hub-{DateTime.Now:yyyyMMdd}.log");
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxFileBytes)
                        File.Move(path, Path.Combine(_dir, $"phoenix-hub-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
                }
                catch { /* rotation is best-effort */ }

                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch { /* never throw from the logging path */ }
        }

        private static void PruneOld()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (var f in Directory.EnumerateFiles(_dir, "phoenix-hub-*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); }
                    catch { /* one stuck file shouldn't abort the sweep */ }
                }
            }
            catch { /* best-effort */ }
        }
    }
}
