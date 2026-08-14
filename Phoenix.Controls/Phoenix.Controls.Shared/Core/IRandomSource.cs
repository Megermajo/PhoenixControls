using System;

namespace Phoenix.Controls.Shared.Core
{
    // Randomness seam for the Loyalty minigames. Production wraps a thread-safe
    // RNG; tests inject a deterministic stub so gamble/slots/duel/roulette/raffle
    // outcomes are reproducible and the "50% is actually 50%" property is testable
    // (one of the RandomEvents review lessons — fairness must be verifiable).
    public interface IRandomSource
    {
        /// <summary>Uniform double in [0,1).</summary>
        double NextDouble();
        /// <summary>Uniform int in [0, maxExclusive).</summary>
        int Next(int maxExclusive);
    }

    /// <summary>Thread-safe production RNG (shared static Random behind a lock).</summary>
    public sealed class DefaultRandomSource : IRandomSource
    {
        public static readonly DefaultRandomSource Instance = new();
        private readonly Random _rng = new();
        private readonly object _gate = new();

        public double NextDouble() { lock (_gate) return _rng.NextDouble(); }
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) return 0;
            lock (_gate) return _rng.Next(maxExclusive);
        }
    }
}
