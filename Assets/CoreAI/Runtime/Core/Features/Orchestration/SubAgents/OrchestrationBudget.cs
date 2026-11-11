using System;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>What one plan may spend: sub-agent runs, reported tokens and concurrent runs.</summary>
    public sealed class OrchestrationBudget
    {
        /// <summary>Creates a validated budget.</summary>
        /// <param name="maxSubAgentCalls">Most sub-agent runs the plan may start; at least 1.</param>
        /// <param name="maxTotalTokens">Token cap summed from reported usage; <c>0</c> means no cap.</param>
        /// <param name="maxParallelSubAgents">Most runs at the same time; at least 1.</param>
        public OrchestrationBudget(int maxSubAgentCalls, int maxTotalTokens, int maxParallelSubAgents)
        {
            if (maxSubAgentCalls < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSubAgentCalls), "A plan needs at least one call.");
            }

            if (maxTotalTokens < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTotalTokens), "Use 0 for no token cap.");
            }

            if (maxParallelSubAgents < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxParallelSubAgents), "Parallelism starts at 1.");
            }

            MaxSubAgentCalls = maxSubAgentCalls;
            MaxTotalTokens = maxTotalTokens;
            MaxParallelSubAgents = maxParallelSubAgents;
        }

        /// <summary>Most sub-agent runs the plan may start.</summary>
        public int MaxSubAgentCalls { get; }

        /// <summary>Token cap summed from reported usage; <c>0</c> means no cap.</summary>
        public int MaxTotalTokens { get; }

        /// <summary>Most runs at the same time.</summary>
        public int MaxParallelSubAgents { get; }
    }

    /// <summary>A point-in-time view of what a plan has spent, for the HUD and for tests.</summary>
    public readonly struct OrchestrationBudgetSnapshot
    {
        /// <summary>Creates a snapshot.</summary>
        public OrchestrationBudgetSnapshot(
            int maxSubAgentCalls,
            int maxTotalTokens,
            int maxParallelSubAgents,
            int callsStarted,
            int callsCompleted,
            long tokensUsed,
            int callsWithoutUsage)
        {
            MaxSubAgentCalls = maxSubAgentCalls;
            MaxTotalTokens = maxTotalTokens;
            MaxParallelSubAgents = maxParallelSubAgents;
            CallsStarted = callsStarted;
            CallsCompleted = callsCompleted;
            TokensUsed = tokensUsed;
            CallsWithoutUsage = callsWithoutUsage;
        }

        /// <summary>Most sub-agent runs the plan may start.</summary>
        public int MaxSubAgentCalls { get; }

        /// <summary>Token cap; <c>0</c> means no cap.</summary>
        public int MaxTotalTokens { get; }

        /// <summary>Most runs at the same time.</summary>
        public int MaxParallelSubAgents { get; }

        /// <summary>Runs reserved so far, including the ones waiting for a slot and the ones running.</summary>
        public int CallsStarted { get; }

        /// <summary>Runs that have finished, whatever the outcome.</summary>
        public int CallsCompleted { get; }

        /// <summary>Tokens the finished runs reported.</summary>
        public long TokensUsed { get; }

        /// <summary>Finished runs whose provider reported no usage, so <see cref="TokensUsed"/> undercounts them.</summary>
        public int CallsWithoutUsage { get; }
    }

    /// <summary>
    /// Thread-safe spending record of one plan. A call is reserved before it waits for a slot, so a
    /// delegation past the call budget is refused at once instead of after a wait; a reservation that never
    /// starts (cancelled while waiting, refused at admission) is refunded.
    /// </summary>
    internal sealed class OrchestrationBudgetLedger
    {
        private readonly object _gate = new();
        private int _callsStarted;
        private int _callsCompleted;
        private long _tokensUsed;
        private int _callsWithoutUsage;

        internal OrchestrationBudgetLedger(OrchestrationBudget budget)
        {
            Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        internal OrchestrationBudget Budget { get; }

        /// <summary>Reserves one call, or names the budget that refuses it.</summary>
        internal SubAgentErrorCode TryReserveCall()
        {
            lock (_gate)
            {
                if (IsTokenBudgetExhaustedLocked())
                {
                    return SubAgentErrorCode.TokenBudgetExhausted;
                }

                if (_callsStarted >= Budget.MaxSubAgentCalls)
                {
                    return SubAgentErrorCode.CallBudgetExhausted;
                }

                _callsStarted++;
                return SubAgentErrorCode.None;
            }
        }

        /// <summary>Gives back a reservation whose run never started.</summary>
        internal void Refund()
        {
            lock (_gate)
            {
                if (_callsStarted > 0)
                {
                    _callsStarted--;
                }
            }
        }

        /// <summary>True when the reported tokens have reached the cap.</summary>
        internal bool IsTokenBudgetExhausted
        {
            get
            {
                lock (_gate)
                {
                    return IsTokenBudgetExhaustedLocked();
                }
            }
        }

        /// <summary>Records a finished run and the tokens it reported (<c>null</c> when the provider reported none).</summary>
        internal void RecordCompletion(int? totalTokens)
        {
            lock (_gate)
            {
                _callsCompleted++;
                if (totalTokens.HasValue && totalTokens.Value > 0)
                {
                    _tokensUsed += totalTokens.Value;
                }
                else if (!totalTokens.HasValue)
                {
                    _callsWithoutUsage++;
                }
            }
        }

        internal OrchestrationBudgetSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new OrchestrationBudgetSnapshot(
                    Budget.MaxSubAgentCalls,
                    Budget.MaxTotalTokens,
                    Budget.MaxParallelSubAgents,
                    _callsStarted,
                    _callsCompleted,
                    _tokensUsed,
                    _callsWithoutUsage);
            }
        }

        private bool IsTokenBudgetExhaustedLocked()
        {
            return Budget.MaxTotalTokens > 0 && _tokensUsed >= Budget.MaxTotalTokens;
        }
    }
}
