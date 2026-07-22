using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Roblox.Datatypes
{
    /// <summary>
    /// Roblox-shaped deterministic PRNG (Random.new). Semantics per the official docs: the
    /// seed is floored to an integer (0, 0.99 and 0.5 produce identical generators), the
    /// unseeded constructor draws entropy, NextInteger is inclusive on both ends, Shuffle is
    /// Fisher-Yates with a spec-guaranteed NextInteger call count, Clone copies state.
    /// WHY xoshiro256**: Roblox does not document its algorithm, so sequence-level parity is
    /// impossible; what the architecture rules require is cross-platform determinism for a
    /// given seed, which this pure-C# implementation guarantees.
    /// </summary>
    public sealed class RbxRandom
    {
        private ulong _s0, _s1, _s2, _s3;

        /// <summary>Random.new() — entropy-seeded.</summary>
        public RbxRandom()
            : this(unchecked((long)DateTime.UtcNow.Ticks ^ Environment.TickCount))
        {
        }

        /// <summary>Random.new(seed) — seed floored to the nearest lower integer (Roblox parity).</summary>
        public RbxRandom(double seed)
            : this(checked((long)Math.Floor(seed)))
        {
        }

        private RbxRandom(long seed)
        {
            // WHY: splitmix64 expansion — the canonical way to seed xoshiro from one integer.
            ulong x = unchecked((ulong)seed);
            _s0 = SplitMix64(ref x);
            _s1 = SplitMix64(ref x);
            _s2 = SplitMix64(ref x);
            _s3 = SplitMix64(ref x);
        }

        private RbxRandom(ulong s0, ulong s1, ulong s2, ulong s3)
        {
            _s0 = s0; _s1 = s1; _s2 = s2; _s3 = s3;
        }

        /// <summary>Random:Clone() — a new generator with identical state.</summary>
        public RbxRandom Clone() => new RbxRandom(_s0, _s1, _s2, _s3);

        /// <summary>Random:NextNumber() — uniform in [0, 1).</summary>
        public double NextNumber() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>Random:NextNumber(min, max) — uniform in [min, max).</summary>
        public double NextNumber(double min, double max)
        {
            if (min > max)
            {
                throw RobloxApiStubException.BadArgument(
                    "NextNumber interval is empty.",
                    $"pass max >= min (got min={min}, max={max})");
            }

            return min + (max - min) * NextNumber();
        }

        /// <summary>Random:NextInteger(min, max) — uniform integer in [min, max], both inclusive.</summary>
        public long NextInteger(long min, long max)
        {
            if (min > max)
            {
                throw RobloxApiStubException.BadArgument(
                    "NextInteger interval is empty.",
                    $"pass max >= min (got min={min}, max={max})");
            }

            ulong range = unchecked((ulong)(max - min)) + 1UL;
            if (range == 0UL)
            {
                // WHY: full 64-bit span — every ulong is in range.
                return unchecked((long)NextUInt64());
            }

            // WHY: rejection sampling removes modulo bias so distributions are exactly uniform.
            ulong limit = ulong.MaxValue - ulong.MaxValue % range;
            ulong sample;
            do
            {
                sample = NextUInt64();
            }
            while (sample >= limit);

            return unchecked(min + (long)(sample % range));
        }

        /// <summary>Random:NextUnitVector() — uniformly distributed over the unit sphere.</summary>
        public RbxVector3 NextUnitVector()
        {
            // WHY: z uniform in [-1,1] + uniform azimuth is the standard area-preserving map.
            double z = NextNumber(-1.0, 1.0);
            double phi = NextNumber(0.0, 2.0 * Math.PI);
            double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
            return new RbxVector3((float)(r * Math.Cos(phi)), (float)(r * Math.Sin(phi)), (float)z);
        }

        /// <summary>
        /// Random:Shuffle(tb) — in-place Fisher-Yates over the list (the marshaller passes the
        /// table's array part). Call count of NextInteger is fixed for a given size (spec).
        /// </summary>
        public void Shuffle<T>(IList<T> list)
        {
            if (list == null)
            {
                throw RobloxApiStubException.BadArgument(
                    "Shuffle expects a table.",
                    "pass an array-like table at argument 1");
            }

            for (int i = list.Count - 1; i >= 1; i--)
            {
                int j = (int)NextInteger(0, i);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private ulong NextUInt64()
        {
            // WHY: xoshiro256** by Blackman & Vigna (public domain) — the scrambled generator step.
            ulong result = RotateLeft(_s1 * 5UL, 7) * 9UL;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = RotateLeft(_s3, 45);
            return result;
        }

        private static ulong RotateLeft(ulong value, int offset) =>
            (value << offset) | (value >> (64 - offset));

        private static ulong SplitMix64(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
