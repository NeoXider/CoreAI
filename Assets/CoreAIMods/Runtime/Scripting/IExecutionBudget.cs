namespace CoreAI.Scripting
{
    /// <summary>
    /// Resource caps for one guarded script execution. Enforcement is best-effort and engine-specific
    /// (the Lua-CSharp adapter uses a per-instruction hook; another engine may only support coarser
    /// checkpoints) — the contract is that a runaway is cut, not the exact instruction it is cut on.
    /// </summary>
    public interface IExecutionBudget
    {
        /// <summary>Maximum wall-clock time for one guarded call, in milliseconds.</summary>
        int TimeoutMs { get; }

        /// <summary>Maximum VM instruction steps for one guarded call.</summary>
        long MaxSteps { get; }

        /// <summary>Maximum GC allocation (bytes) for one guarded call; &lt;= 0 disables the check.</summary>
        long MaxAllocatedBytes { get; }
    }

    /// <summary>Immutable <see cref="IExecutionBudget"/> with the historical sandbox defaults.</summary>
    public sealed class ExecutionBudget : IExecutionBudget
    {
        // WHY: Budgets are aligned to Roblox parity — Roblox terminates a script after ~10 s of continuous
        // (non-yielding) execution ("exhausted allowed execution time"), so a CoreAI mod must not be cut
        // sooner than a Luau script doing the same work. The wall-clock timeout is the real limiter; MaxSteps
        // is a high secondary net so the ~10 s clock (checked by the guard hook) trips first on a runaway.
        public const int DefaultTimeoutMs = 10_000;
        public const long DefaultMaxSteps = 50_000_000;
        public const long DefaultMaxAllocatedBytes = 256 * 1024 * 1024;

        public ExecutionBudget(
            int timeoutMs = DefaultTimeoutMs,
            long maxSteps = DefaultMaxSteps,
            long maxAllocatedBytes = DefaultMaxAllocatedBytes)
        {
            TimeoutMs = timeoutMs;
            MaxSteps = maxSteps;
            MaxAllocatedBytes = maxAllocatedBytes;
        }

        /// <inheritdoc />
        public int TimeoutMs { get; }

        /// <inheritdoc />
        public long MaxSteps { get; }

        /// <inheritdoc />
        public long MaxAllocatedBytes { get; }
    }
}
