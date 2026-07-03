using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Services;

// System Log panel feed. Subscribes to GlobalLogger.OnLogEntry and exposes
// the existing 2000-entry ring through Snapshot. The level-mask filter is
// applied client-side: at 2000 entries max the cost is negligible and a
// server-side filter would break GetRecentLogs() for other callers.
//
// Producer-side dispatcher coalescing. The previous code did
// _ui.Post() per entry, which under a 1000-logs/sec burst (script-engine
// debug fan-out, bus log spam) queued one dispatcher work-item per row.
// We now buffer pending entries under a lock and schedule a SINGLE Post
// to drain the whole batch on the UI thread — subsequent EntryAdded
// invocations within the same dispatcher tick join the in-flight batch
// instead of scheduling a new hop. The downstream VM (SystemLogViewModel)
// has its own batching layer; coalescing at the source means even multi-
// consumer fan-out shares the per-tick cap.
public sealed class SystemLogSource : ISystemLogSource, IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly Action<Log> _onLog;
    // `volatile` so the GlobalLogger producer thread (which
    // reads _filter on every entry) sees writes from SetLevelFilter
    // (UI thread, driven by SystemLogViewModel chip toggles) without
    // tearing. SystemLogLevel is a [Flags] int enum — single-word read /
    // write — so `volatile` is sufficient (no Interlocked needed).
    // Without this the JIT was free to cache the field into a register
    // for the lifetime of the OnLogEntry handler, which under sustained
    // burst could ignore a mid-burst filter change indefinitely.
    private volatile SystemLogLevel _filter = SystemLogLevel.All;
    // Fan-out: every subscriber to EntryAdded
    // receives every log entry; no primary-subscriber gate.
    private int _disposed;

    // Pending entries staged on the producer side. EntryAdded is
    // fan-out, so the batch isn't per-subscriber; we hand the whole list
    // off to the UI thread and the dispatcher walks it once. A simple
    // List + lock is faster than a Channel/BlockingCollection here because
    // the producer never blocks (drop-on-overflow is fine for logs).
    private readonly object _pendingLock = new();
    private List<SystemLogEntry> _pendingEntries = new();
    private bool _flushScheduled;

    public SystemLogSource(IUiDispatcher uiDispatcher)
    {
        _ui = uiDispatcher;
        _onLog = entry =>
        {
            var mapped = Map(entry);
            // Read the filter once — a concurrent SetLevelFilter call could
            // otherwise change the mask between the check and a follow-up
            // read (torn evaluation when the filter widens / narrows).
            var filter = _filter;
            if ((filter & mapped.Level) == 0) return;

            bool needsSchedule;
            lock (_pendingLock)
            {
                _pendingEntries.Add(mapped);
                needsSchedule = !_flushScheduled;
                _flushScheduled = true;
            }
            if (!needsSchedule) return;

            // One dispatcher hop per batch — every entry that arrives
            // before the hop runs joins the in-flight list. Bursts of N
            // entries per dispatcher tick collapse to a single Post.
            _ui.Post(FlushPending);
        };
        GlobalLogger.OnLogEntry += _onLog;
    }

    private void FlushPending()
    {
        List<SystemLogEntry> batch;
        lock (_pendingLock)
        {
            batch = _pendingEntries;
            _pendingEntries = new List<SystemLogEntry>();
            _flushScheduled = false;
        }
        var handler = EntryAdded;
        if (handler is null || batch.Count == 0) return;
        // The level filter may have widened or narrowed between the
        // producer-side check and now; re-evaluate to honour the latest
        // mask. Cheaper than holding the lock across the handler.
        var filter = _filter;
        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            if ((filter & entry.Level) == 0) continue;
            handler.Invoke(this, entry);
        }
    }

    public IReadOnlyList<SystemLogEntry> Snapshot()
    {
        var ring = GlobalLogger.GetRecentLogs();
        if (ring.Count == 0) return Array.Empty<SystemLogEntry>();
        // Snapshot the filter once so each ring entry sees the same predicate
        // (a SetLevelFilter call mid-loop would otherwise produce a result
        // list with rows from two different filter masks).
        var filter = _filter;
        var result = new List<SystemLogEntry>(ring.Count);
        foreach (var entry in ring)
        {
            var mapped = Map(entry);
            if ((filter & mapped.Level) != 0) result.Add(mapped);
        }
        return result;
    }

    public event EventHandler<SystemLogEntry>? EntryAdded;

    public void SetLevelFilter(SystemLogLevel mask) => _filter = mask;

    private static SystemLogEntry Map(Log entry) => new(
        Timestamp: ToUtcOffset(entry.Timestamp),
        Level:     MapLevel(entry.Level),
        Source:    string.IsNullOrEmpty(entry.Source) ? "system" : entry.Source,
        Message:   entry.Message ?? "",
        // The Log entry now carries the original Exception (populated
        // by GlobalLogger.Error). Threading it through the panel contract lets
        // the row's "View Last Error" affordance render an actual stack trace
        // instead of constant placeholder text. Still null on non-Error rows.
        Exception: entry.Exception);

    private static SystemLogLevel MapLevel(LogLevel level) => level switch
    {
        LogLevel.Debug          => SystemLogLevel.Debug,
        LogLevel.CriticalError  => SystemLogLevel.Error,
        // LogLevel.Warning was added in the same QC item so the
        // WARN chip in SystemLogPanel actually has a level to filter to.
        // Pre-fix the chip filtered to a level no source level mapped to,
        // so flipping it on hid every row regardless of content.
        LogLevel.Warning        => SystemLogLevel.Warn,
        // System / Communication / StreamEvent / LogicExecution / VisualEvent
        // are all informational from the panel's perspective.
        _                                => SystemLogLevel.Info,
    };

    private static DateTimeOffset ToUtcOffset(DateTime dt) =>
        dt.Kind switch
        {
            DateTimeKind.Utc        => new DateTimeOffset(dt, TimeSpan.Zero),
            DateTimeKind.Local      => new DateTimeOffset(dt),
            _                       => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)),
        };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        GlobalLogger.OnLogEntry -= _onLog;
    }
}
