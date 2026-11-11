using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// Summary of one measured series (frame milliseconds, bytes per frame, batches per frame): count,
    /// mean, minimum, maximum and the nearest-rank p50/p95/p99. Nearest rank, not interpolation, so every
    /// reported percentile is a value that was actually observed.
    /// </summary>
    public readonly struct PerfDistribution
    {
        private PerfDistribution(int count, double mean, double min, double max, double p50, double p95, double p99)
        {
            Count = count;
            Mean = mean;
            Min = min;
            Max = max;
            P50 = p50;
            P95 = p95;
            P99 = p99;
        }

        /// <summary>Samples summarised; 0 means the series was not measured.</summary>
        public int Count { get; }

        /// <summary>Arithmetic mean.</summary>
        public double Mean { get; }

        /// <summary>Smallest sample.</summary>
        public double Min { get; }

        /// <summary>Largest sample.</summary>
        public double Max { get; }

        /// <summary>Nearest-rank median.</summary>
        public double P50 { get; }

        /// <summary>Nearest-rank 95th percentile.</summary>
        public double P95 { get; }

        /// <summary>Nearest-rank 99th percentile.</summary>
        public double P99 { get; }

        /// <summary>A series that was not measured (no counter, or zero samples).</summary>
        public static PerfDistribution Empty => default;

        /// <summary>Summarises <paramref name="samples"/>; the list is not modified.</summary>
        public static PerfDistribution From(IReadOnlyList<double> samples)
        {
            if (samples == null || samples.Count == 0)
            {
                return Empty;
            }

            double[] sorted = new double[samples.Count];
            double sum = 0d;
            for (int index = 0; index < samples.Count; index++)
            {
                double value = samples[index];
                if (double.IsNaN(value))
                {
                    throw new ArgumentException("A measured sample cannot be NaN.", nameof(samples));
                }

                sorted[index] = value;
                sum += value;
            }

            Array.Sort(sorted);
            return new PerfDistribution(
                sorted.Length,
                sum / sorted.Length,
                sorted[0],
                sorted[sorted.Length - 1],
                NearestRank(sorted, 50d),
                NearestRank(sorted, 95d),
                NearestRank(sorted, 99d));
        }

        /// <summary>
        /// The nearest-rank percentile of an ascending array: the smallest sample with at least
        /// <paramref name="percentile"/> percent of the samples at or below it.
        /// </summary>
        public static double NearestRank(double[] ascending, double percentile)
        {
            if (ascending == null || ascending.Length == 0)
            {
                throw new ArgumentException("At least one sample is required.", nameof(ascending));
            }

            if (percentile <= 0d)
            {
                return ascending[0];
            }

            if (percentile >= 100d)
            {
                return ascending[ascending.Length - 1];
            }

            int rank = (int)Math.Ceiling(percentile / 100d * ascending.Length);
            return ascending[Math.Max(1, rank) - 1];
        }
    }
}
