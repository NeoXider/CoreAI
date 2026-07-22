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
        // WHY: Defaults mirror the Lua-CSharp guard's long-standing values so swapping call sites from the
        // concrete guard to the seam cannot silently change any budget.
        public const int DefaultTimeoutMs = 2000;
        public const long DefaultMaxSteps = 200_000;
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
