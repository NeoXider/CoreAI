using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// One micro-benchmark: <see cref="Run"/> performs the measured operation <c>ops</c> times and returns a
    /// checksum the harness keeps alive, so the JIT cannot drop the work.
    /// </summary>
    internal sealed class MicroCase
    {
        public MicroCase(string group, string name, Func<int, long> run, string notes = null,
            Action setup = null, Action teardown = null, int fixedOps = 0, Func<string> details = null)
        {
            Group = group;
            Name = name;
            Run = run ?? throw new ArgumentNullException(nameof(run));
            Notes = notes ?? "";
            Setup = setup;
            Teardown = teardown;
            FixedOps = fixedOps;
            Details = details;
        }

        /// <summary>
        /// Read after the trials, before the teardown: what the case measured besides its time per operation (a
        /// sliced encode's slice count and longest slice); null for most cases.
        /// </summary>
        public Func<string> Details { get; }

        public string Group { get; }

        public string Name { get; }

        public Func<int, long> Run { get; }

        public string Notes { get; }

        public Action Setup { get; }

        public Action Teardown { get; }

        /// <summary>When positive, every trial runs exactly this many operations (for operations that take milliseconds).</summary>
        public int FixedOps { get; }
    }

    /// <summary>The measured cost of one <see cref="MicroCase"/>.</summary>
    internal sealed class MicroResult
    {
        public string Group;
        public string Name;
        public string Notes;
        public int OpsPerTrial;
        public int Trials;
        public double MedianNanosecondsPerOp;
        public double MinNanosecondsPerOp;
        public double MaxNanosecondsPerOp;

        /// <summary>Median managed bytes allocated per operation; negative when the runtime does not count.</summary>
        public double BytesPerOp = -1d;

        public int Gen0Collections;
        public string Error;

        /// <summary>What <see cref="MicroCase.Details"/> answered; empty when the case has none.</summary>
        public string Details = "";
    }

    /// <summary>
    /// Runs micro-benchmarks: warm-up, calibration to a target trial length, then a fixed number of timed
    /// trials. Reports the median time per operation and the median allocated bytes per operation
    /// (exact on CoreCLR, unavailable on Mono).
    /// </summary>
    internal static class MicroBench
    {
        public static MicroResult Run(MicroCase microCase, double targetTrialSeconds, int trials, bool countAllocations)
        {
            MicroResult result = new()
            {
                Group = microCase.Group,
                Name = microCase.Name,
                Notes = microCase.Notes,
                Trials = trials
            };

            try
            {
                microCase.Setup?.Invoke();
                long checksum = 0;
                int ops = microCase.FixedOps > 0 ? microCase.FixedOps : Calibrate(microCase, targetTrialSeconds, ref checksum);
                result.OpsPerTrial = ops;
                checksum += microCase.Run(ops);

                List<double> nanoseconds = new(trials);
                List<double> bytes = new(trials);
                int gen0Before = GC.CollectionCount(0);
                for (int trial = 0; trial < trials; trial++)
                {
                    long allocatedBefore = countAllocations ? BenchEnvironment.AllocatedBytes() : 0;
                    long start = Stopwatch.GetTimestamp();
                    checksum += microCase.Run(ops);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    long allocated = countAllocations ? BenchEnvironment.AllocatedBytes() - allocatedBefore : 0;
                    nanoseconds.Add(elapsed * 1_000_000_000d / Stopwatch.Frequency / ops);
                    bytes.Add((double)allocated / ops);
                }

                result.Gen0Collections = GC.CollectionCount(0) - gen0Before;
                nanoseconds.Sort();
                bytes.Sort();
                result.MedianNanosecondsPerOp = nanoseconds[nanoseconds.Count / 2];
                result.MinNanosecondsPerOp = nanoseconds[0];
                result.MaxNanosecondsPerOp = nanoseconds[nanoseconds.Count - 1];
                result.BytesPerOp = countAllocations ? bytes[bytes.Count / 2] : -1d;
                result.Details = microCase.Details?.Invoke() ?? "";
                GC.KeepAlive(checksum);
            }
            catch (Exception exception)
            {
                result.Error = exception.GetType().Name + ": " + exception.Message;
            }
            finally
            {
                try
                {
                    microCase.Teardown?.Invoke();
                }
                catch (Exception exception)
                {
                    result.Error ??= "teardown: " + exception.GetType().Name + ": " + exception.Message;
                }
            }

            return result;
        }

        private static int Calibrate(MicroCase microCase, double targetTrialSeconds, ref long checksum)
        {
            // WHY a warm-up before the first timed probe: the first calls JIT-compile the path (and fill its
            // caches), so a probe that includes them reads milliseconds for a microsecond operation and the
            // trials end up a single operation long.
            long warmupEnd = Stopwatch.GetTimestamp() + (long)(Math.Min(0.2d, targetTrialSeconds) * Stopwatch.Frequency);
            int warmupCalls = 0;
            while (warmupCalls < 3 || Stopwatch.GetTimestamp() < warmupEnd)
            {
                checksum += microCase.Run(1);
                warmupCalls++;
            }

            int ops = 1;
            while (true)
            {
                long start = Stopwatch.GetTimestamp();
                checksum += microCase.Run(ops);
                double seconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                if (seconds >= targetTrialSeconds / 8d || ops >= 1 << 26)
                {
                    double scaled = ops * targetTrialSeconds / Math.Max(seconds, 1e-9);
                    return (int)Math.Max(1d, Math.Min(scaled, 1 << 28));
                }

                ops *= 4;
            }
        }
    }
}
