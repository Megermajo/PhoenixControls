using System.Collections.Generic;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.WinUI.Controls;

/// <summary>
/// Parking lot for the final config-flush Tasks the pre-build tool ViewModels
/// start from their <c>Dispose()</c> (all of them except Timer, which holds no
/// editable config of its own).
///
/// Each VM ends Dispose with an <c>AsyncErrorBoundary.SafeRunAsync(() =>
/// _svc.SaveConfigAsync(...))</c>. That call is correctly fire-and-forget for a
/// mid-session close — nobody is waiting, and the write lands a few milliseconds
/// later. At SHUTDOWN it is a data-loss bug: MainWindow disposes every tool view
/// synchronously, its coordinator awaits a fixed set of tear-downs under a 4 s
/// cap, flushes the log sinks (DiagnosticFileLog.Stop → GlobalLogger.Stop), and
/// then calls <c>Environment.Exit(0)</c>, which kills any SQLite write still in
/// flight. Registering here lets
/// <see cref="PreBuildsHostView.DisposeAllTools"/> hand the coordinator ONE more
/// tracked step covering all of them.
///
/// Registration is deliberately push-from-the-VM rather than pull-from-the-View:
/// the host only ever sees the <c>UserControl</c>, and nothing in the View layer
/// exposes its VM's flush.
///
/// UI-thread-only in practice (Dispose runs on the dispatcher), but the lock is
/// cheap insurance against a future off-thread teardown, and <c>DrainAsync</c> is
/// read from the shutdown coordinator's thread-pool task.
///
/// (Lives in its own file since the Pre-Builds rail replaced ToolTabHost, which
/// used to declare it. Twelve tool ViewModels call it fully qualified, so the
/// namespace is load-bearing — moving the class out of this namespace would
/// touch all of them for no gain.)
/// </summary>
internal static class ToolConfigFlushTracker
{
    private static readonly object s_gate = new();
    private static readonly List<Task> s_pending = new();

    /// <summary>
    /// Parks a flush Task so a later <see cref="DrainAsync"/> can await it.
    /// Already-finished tasks are ignored — the tracker only ever needs the
    /// writes still in flight.
    /// </summary>
    public static void Register(Task? flush)
    {
        if (flush is null || flush.IsCompleted) return;
        lock (s_gate)
        {
            // Prune as we go so a long session of open/close churn can't grow the
            // list without bound.
            s_pending.RemoveAll(static t => t.IsCompleted);
            s_pending.Add(flush);
        }
    }

    /// <summary>
    /// Awaits every flush still in flight and clears the list. Returns an
    /// already-completed Task when there is nothing outstanding, so the
    /// happy-path shutdown pays nothing. Never faults: every registered task
    /// comes from <c>AsyncErrorBoundary.SafeRunAsync</c>, which logs and
    /// swallows its own exceptions.
    /// </summary>
    public static Task DrainAsync()
    {
        Task[] snapshot;
        lock (s_gate)
        {
            s_pending.RemoveAll(static t => t.IsCompleted);
            if (s_pending.Count == 0) return Task.CompletedTask;
            snapshot = s_pending.ToArray();
            s_pending.Clear();
        }
        return Task.WhenAll(snapshot);
    }
}
