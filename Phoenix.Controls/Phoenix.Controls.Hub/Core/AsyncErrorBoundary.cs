using System;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // L76 — real implementation lives in Phoenix.Controls.Shared.Core.AsyncErrorBoundary.
    // The Hub-side stub delegates so existing Hub call-sites (HubBootstrapper,
    // Bus, WS, ScriptManager) and test fixtures that reference Hub.Core
    // continue to compile without any using-directive changes.
    //
    //  The strict overload with the expected CancellationToken now
    // also lives in Shared (so Architect / Visualist callers can use it too).
    // The Hub stub forwards both overloads.
    public static class AsyncErrorBoundary
    {
        public static Task SafeRunAsync(Func<Task> fn, string source, string label)
            => Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(fn, source, label);

        /// <summary>
        ///  Strict overload — see
        /// <see cref="Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(Func{Task}, string, string, CancellationToken)"/>.
        /// </summary>
        public static Task SafeRunAsync(
            Func<Task> fn,
            string source,
            string label,
            CancellationToken expectedCt)
            => Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(fn, source, label, expectedCt);
    }
}
