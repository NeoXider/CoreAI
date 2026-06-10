using System;
using System.Collections.Generic;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Pure (UnityEngine-free) aggregator behind the token-budget overlay: accumulates per-request
    /// token usage, keeps a rolling request/token window, and estimates session cost in USD from
    /// configurable per-1K-token prices. Thread-safe: usage events may arrive off the main thread.
    /// </summary>
    public sealed class TokenBudgetCalculator
    {
        private readonly object _sync = new object();
        private readonly double _windowSeconds;
        private readonly Queue<(double Timestamp, long Tokens)> _window = new Queue<(double, long)>();

        /// <param name="windowSeconds">Rolling window length in seconds (default 60).</param>
        public TokenBudgetCalculator(double windowSeconds = 60d)
        {
            _windowSeconds = windowSeconds > 0d ? windowSeconds : 60d;
        }

        /// <summary>Rolling window length in seconds.</summary>
        public double WindowSeconds => _windowSeconds;

        /// <summary>Total usage reports recorded this session.</summary>
        public long TotalRequests { get; private set; }

        /// <summary>Reports that carried at least one token count.</summary>
        public long RequestsWithUsage { get; private set; }

        /// <summary>Accumulated prompt/input tokens (unsplit totals count as prompt tokens).</summary>
        public long TotalPromptTokens { get; private set; }

        /// <summary>Accumulated completion/output tokens.</summary>
        public long TotalCompletionTokens { get; private set; }

        /// <summary>Accumulated total tokens.</summary>
        public long TotalTokens { get; private set; }

        /// <summary>Prompt tokens of the most recent report (-1 when unknown).</summary>
        public int LastPromptTokens { get; private set; } = -1;

        /// <summary>Completion tokens of the most recent report (-1 when unknown).</summary>
        public int LastCompletionTokens { get; private set; } = -1;

        /// <summary>Total tokens of the most recent report (-1 when unknown).</summary>
        public int LastTotalTokens { get; private set; } = -1;

        /// <summary>Mean total tokens per usage-bearing request, 0 when none recorded.</summary>
        public double AverageTokensPerRequest
        {
            get
            {
                lock (_sync)
                {
                    return RequestsWithUsage > 0 ? (double)TotalTokens / RequestsWithUsage : 0d;
                }
            }
        }

        /// <summary>
        /// Records one usage report. Missing counts are reconciled: an unsplit total is attributed to
        /// prompt tokens for cost purposes; a missing total is derived from prompt + completion.
        /// </summary>
        /// <param name="promptTokens">Prompt/input token count, when reported.</param>
        /// <param name="completionTokens">Completion/output token count, when reported.</param>
        /// <param name="totalTokens">Total token count, when reported.</param>
        /// <param name="timestampSeconds">Monotonic-ish timestamp in seconds for the rolling window.</param>
        public void RecordUsage(int? promptTokens, int? completionTokens, int? totalTokens, double timestampSeconds)
        {
            lock (_sync)
            {
                TotalRequests++;

                int prompt = promptTokens.GetValueOrDefault();
                int completion = completionTokens.GetValueOrDefault();
                int total = totalTokens ?? prompt + completion;
                if (!promptTokens.HasValue && !completionTokens.HasValue && totalTokens.HasValue)
                {
                    // Backend reported only a grand total; attribute to prompt for cost purposes.
                    prompt = totalTokens.Value;
                }

                bool hasUsage = promptTokens.HasValue || completionTokens.HasValue || totalTokens.HasValue;
                if (hasUsage)
                {
                    RequestsWithUsage++;
                    TotalPromptTokens += prompt < 0 ? 0 : prompt;
                    TotalCompletionTokens += completion < 0 ? 0 : completion;
                    TotalTokens += total < 0 ? 0 : total;
                }

                LastPromptTokens = promptTokens ?? -1;
                LastCompletionTokens = completionTokens ?? -1;
                LastTotalTokens = totalTokens ?? (hasUsage ? total : -1);

                _window.Enqueue((timestampSeconds, hasUsage && total > 0 ? total : 0L));
                Purge(timestampSeconds);
            }
        }

        /// <summary>Number of recorded requests inside the rolling window ending at <paramref name="nowSeconds"/>.</summary>
        public int GetRequestsInWindow(double nowSeconds)
        {
            lock (_sync)
            {
                Purge(nowSeconds);
                return _window.Count;
            }
        }

        /// <summary>Sum of total tokens inside the rolling window ending at <paramref name="nowSeconds"/>.</summary>
        public long GetTokensInWindow(double nowSeconds)
        {
            lock (_sync)
            {
                Purge(nowSeconds);
                long sum = 0;
                foreach ((double _, long tokens) in _window)
                {
                    sum += tokens;
                }

                return sum;
            }
        }

        /// <summary>True when at least one configured price is positive, enabling cost display.</summary>
        public static bool HasPricing(double inputPricePer1K, double outputPricePer1K)
        {
            return inputPricePer1K > 0d || outputPricePer1K > 0d;
        }

        /// <summary>Estimated session cost in USD from the accumulated token counters.</summary>
        public double EstimateSessionCostUsd(double inputPricePer1K, double outputPricePer1K)
        {
            lock (_sync)
            {
                return ComputeCostUsd(TotalPromptTokens, TotalCompletionTokens, inputPricePer1K, outputPricePer1K);
            }
        }

        /// <summary>Cost in USD for the given token counts at per-1K-token prices; never negative.</summary>
        public static double ComputeCostUsd(
            long promptTokens,
            long completionTokens,
            double inputPricePer1K,
            double outputPricePer1K)
        {
            double inPrice = inputPricePer1K > 0d ? inputPricePer1K : 0d;
            double outPrice = outputPricePer1K > 0d ? outputPricePer1K : 0d;
            long inTok = promptTokens > 0 ? promptTokens : 0;
            long outTok = completionTokens > 0 ? completionTokens : 0;
            return inTok / 1000d * inPrice + outTok / 1000d * outPrice;
        }

        /// <summary>Clears all counters and the rolling window.</summary>
        public void Reset()
        {
            lock (_sync)
            {
                TotalRequests = 0;
                RequestsWithUsage = 0;
                TotalPromptTokens = 0;
                TotalCompletionTokens = 0;
                TotalTokens = 0;
                LastPromptTokens = -1;
                LastCompletionTokens = -1;
                LastTotalTokens = -1;
                _window.Clear();
            }
        }

        private void Purge(double nowSeconds)
        {
            double cutoff = nowSeconds - _windowSeconds;
            while (_window.Count > 0 && _window.Peek().Timestamp < cutoff)
            {
                _window.Dequeue();
            }
        }
    }
}
