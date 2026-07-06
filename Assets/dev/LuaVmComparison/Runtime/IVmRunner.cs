using System;

namespace LuaVmComparison
{
    /// <summary>
    /// Result of a micro-benchmark: mean wall-clock per iteration (microseconds) and
    /// managed allocations per iteration (bytes). <see cref="Ok"/> is false if the script errored.
    /// </summary>
    public readonly struct BenchResult
    {
        public readonly double MicrosPerIter;
        public readonly long BytesPerIter;
        public readonly bool Ok;
        public readonly string Error;

        public BenchResult(double microsPerIter, long bytesPerIter)
        {
            MicrosPerIter = microsPerIter;
            BytesPerIter = bytesPerIter;
            Ok = true;
            Error = null;
        }

        private BenchResult(string error)
        {
            MicrosPerIter = 0;
            BytesPerIter = 0;
            Ok = false;
            Error = error;
        }

        public static BenchResult Fail(string error) => new BenchResult(error);
    }

    /// <summary>
    /// A VM under test. Each implementation owns one sandboxed state (safe libraries only:
    /// base/string/table/math/coroutine/bitwise; no io/os/debug/package) with a registered
    /// host function <c>host_add(a, b)</c> for the host-call benchmark.
    /// </summary>
    public interface IVmRunner : IDisposable
    {
        string Name { get; }

        /// <summary>Run <paramref name="code"/> once and return the first result normalized to a string, or "ERR:...".</summary>
        string Eval(string code);

        /// <summary>True if a global named <paramref name="name"/> is reachable in the sandbox (used to prove io/os/debug are absent).</summary>
        bool HasGlobal(string name);

        /// <summary>Compile once, run <paramref name="iters"/> timed iterations after <paramref name="warmup"/> warmups.</summary>
        BenchResult Benchmark(string code, int warmup, int iters);

        /// <summary>
        /// Attempt to run <paramref name="code"/> with a host-imposed budget of <paramref name="budgetMs"/> ms.
        /// Returns true if the VM halted the runaway script (threw), false if it ran to completion or the VM
        /// has no budget mechanism. <paramref name="detail"/> describes the mechanism.
        /// </summary>
        bool HaltsWithBudget(string code, int budgetMs, out string detail);
    }
}
