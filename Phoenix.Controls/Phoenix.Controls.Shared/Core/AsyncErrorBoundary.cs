using System;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.Core
{
    // L76 — moved to Shared so Architect and Visualist can reference it without
    // depending on Hub. Hub.Core.AsyncErrorBoundary is a thin delegation stub
    // that preserves all existing Hub call-sites without change.
    //
    // Replaces the silent _ = X.ContinueWith(t => Log(...), OnlyOnFaulted)
    // pattern. Awaiting through SafeRunAsync routes any thrown exception
    // into GlobalLogger.Error so SystemLogWindow + LiveFeedWindow render it
    // and OnError subscribers see the original Exception with stack trace.
    //
    //  The original 3-arg overload swallowed EVERY
    // OperationCanceledException, which hid real per-request timeouts and
    // SQLite cancellations that arrived from unrelated tokens. The strict
    // 4-arg overload only swallows OCE whose CancellationToken matches the
    // caller-supplied expectedCt — any other OCE flows through to
    // GlobalLogger.Error. The 3-arg overload keeps its legacy semantics so
    // call sites that haven't been migrated continue to compile and behave
    // identically.
    public static class AsyncErrorBoundary
    {
        public static async Task SafeRunAsync(Func<Task> fn, string source, string label)
        {
            try
            {
                await fn().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected during shutdown — don't pollute the error channel.
                //  Legacy overload — preserved for call sites that
                // don't have a clear local CT to assert against. New code
                // should prefer the 4-arg overload.
            }
            catch (Exception ex)
            {
                GlobalLogger.Error(source, label, ex);
            }
        }

        /// <summary>
        ///  Strict overload. Only swallows
        /// <see cref="OperationCanceledException"/> when its
        /// <c>CancellationToken</c> matches <paramref name="expectedCt"/> (or
        /// when the expected token is already cancelled — graceful shutdown);
        /// any other OCE (e.g. a faulted upstream race, a SQLite per-request
        /// timeout that fires its own internal CTS) reaches
        /// <see cref="GlobalLogger.Error"/> so it shows up in System Log
        /// instead of disappearing.
        /// </summary>
        public static async Task SafeRunAsync(
            Func<Task> fn,
            string source,
            string label,
            CancellationToken expectedCt)
        {
            try
            {
                await fn().ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == expectedCt
                                                          || expectedCt.IsCancellationRequested)
            {
                // Cancellation matched the caller's expected token — graceful
                // shutdown, not an error condition.
            }
            catch (Exception ex)
            {
                GlobalLogger.Error(source, label, ex);
            }
        }
    }
}
