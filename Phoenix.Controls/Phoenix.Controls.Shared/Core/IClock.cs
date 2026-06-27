using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.Core
{
    /// <summary>
    ///  Time-source seam used by production code paths that today
    /// rely on wall-clock <c>Thread.Sleep</c> / <c>Task.Delay</c> / direct
    /// <c>DateTime.UtcNow</c> reads. Lifted from the test assembly into
    /// Shared so production callers (ScriptEngine, HUDServer, ScriptManager,
    /// LayerWatcher, Bus, RemoteAuthManager, etc.) can take a dependency
    /// without round-tripping through InternalsVisibleTo. Tests swap in a
    /// VirtualClock (still in <c>Phoenix.Controls.Tests</c>) and advance it
    /// instead of burning wall-clock time.
    /// </summary>
    public interface IClock
    {
        DateTime UtcNow { get; }
        Task Delay(TimeSpan duration, CancellationToken ct = default);
    }

    /// <summary>
    ///  Real-clock implementation. Default for production code;
    /// every caller that doesn't accept an <see cref="IClock"/> parameter
    /// implicitly resolves through <see cref="Instance"/>.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new();
        public DateTime UtcNow => DateTime.UtcNow;
        public Task Delay(TimeSpan duration, CancellationToken ct = default)
            => Task.Delay(duration, ct);
    }
}
