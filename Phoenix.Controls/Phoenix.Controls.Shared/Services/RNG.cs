using System;
using System.Security.Cryptography;

namespace Phoenix.Controls.Shared.Services
{
    public static class RNG
    {
        private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

        public static int Next(int min, int max)
        {
            if (min >= max) return min;

            byte[] data = new byte[4];
            _rng.GetBytes(data);
            uint value = BitConverter.ToUInt32(data, 0);

            // Normalize to [0, 1) then scale. Divide by 2^32 (one past uint.MaxValue)
            // so the result is strictly < 1.0 — dividing by uint.MaxValue would let
            // the maximum sampled value normalise to exactly 1.0 and produce `max`,
            // breaking the documented [min, max) contract (off-by-one at the
            // uint.MaxValue draw).
            double normalized = value / 4294967296.0; // 2^32
            return (int)(min + (normalized * (max - min)));
        }

        public static bool Chance(int percentage)
        {
            // Next(1, 101) yields 1..100 inclusive; <= percentage is decisive across
            // the full 0..100 range. percent=0 → always false; percent=100 → always true.
            return Next(1, 101) <= percentage;
        }

        /// <summary>
        /// Probability check for a fractional percentage in [0.0, 100.0]. Out-of-range
        /// inputs clamp. percent=33.33 means "fire 33.33% of calls". H2 — Math.Chance
        /// node template advertises a Float socket, so the runtime needs to honor it
        /// rather than reject sub-integer values via int.TryParse.
        /// </summary>
        public static bool Chance(double percentage)
        {
            if (percentage <= 0)   return false;
            if (percentage >= 100) return true;
            byte[] data = new byte[4];
            _rng.GetBytes(data);
            uint value = BitConverter.ToUInt32(data, 0);
            // Divide by 2^32 (one past uint.MaxValue) so the upper bound is
            // strictly < 1.0 — see note on Next() for rationale.
            double normalized = value / 4294967296.0; // [0, 1)
            return normalized * 100.0 < percentage;
        }
    }
}
