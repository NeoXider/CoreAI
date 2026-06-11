using System;
using CoreAI.Ai;
using static System.FormattableString;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Pure (UnityEngine-free) text formatting shared by token-budget UIs: the IMGUI overlay and
    /// custom UGUI views render the same strings from a <see cref="TokenBudgetCalculator"/>.
    /// </summary>
    public static class TokenBudgetTextFormatter
    {
        /// <summary>Last-request / session / request-count token lines.</summary>
        public static string FormatTokens(TokenBudgetCalculator calc)
        {
            if (calc == null)
            {
                return string.Empty;
            }

            return
                $"Last request: {FormatTokenCount(calc.LastPromptTokens)} in / {FormatTokenCount(calc.LastCompletionTokens)} out / {FormatTokenCount(calc.LastTotalTokens)} total\n" +
                $"Session: {calc.TotalPromptTokens} in / {calc.TotalCompletionTokens} out / {calc.TotalTokens} total\n" +
                Invariant($"Requests: {calc.TotalRequests} (with usage: {calc.RequestsWithUsage}) | avg {calc.AverageTokensPerRequest:F0} tok/req");
        }

        /// <summary>
        /// Session/last-request cost lines, or a "prices not set" hint when both prices are unset.
        /// </summary>
        public static string FormatCost(TokenBudgetCalculator calc, double inputPricePer1K, double outputPricePer1K)
        {
            if (calc == null)
            {
                return string.Empty;
            }

            if (!TokenBudgetCalculator.HasPricing(inputPricePer1K, outputPricePer1K))
            {
                return "Prices not set (CoreAISettings > Debug > Token budget overlay)";
            }

            double sessionCost = calc.EstimateSessionCostUsd(inputPricePer1K, outputPricePer1K);
            double lastCost = TokenBudgetCalculator.ComputeCostUsd(
                Math.Max(calc.LastPromptTokens, 0),
                Math.Max(calc.LastCompletionTokens, 0),
                inputPricePer1K, outputPricePer1K);
            // Invariant culture: cost strings must render "." decimals regardless of OS locale.
            return Invariant($"Session: ${sessionCost:F4} | last request: ${lastCost:F4}\n") +
                   Invariant($"(in ${inputPricePer1K:F4}/1K, out ${outputPricePer1K:F4}/1K)");
        }

        /// <summary>
        /// Rate-limiter and rolling-window load lines. <paramref name="nearLimit"/> is true when the
        /// chat limiter window is saturated, so UIs can switch to an alert color.
        /// </summary>
        public static string FormatLoad(
            TokenBudgetCalculator calc,
            RateLimiterMetrics rate,
            double nowSeconds,
            out bool nearLimit)
        {
            nearLimit = false;
            if (calc == null)
            {
                return string.Empty;
            }

            string limiterLine;
            if (rate.MaxRequestsPerWindow > 0)
            {
                nearLimit = rate.AcceptedInWindow >= rate.MaxRequestsPerWindow;
                limiterLine =
                    $"Chat limiter: {rate.AcceptedInWindow}/{rate.MaxRequestsPerWindow} per {rate.WindowSeconds}s {FormatLoadBar(rate.AcceptedInWindow, rate.MaxRequestsPerWindow)}\n" +
                    $"Rejected total: {rate.TotalRejected}";
            }
            else
            {
                limiterLine = "Chat limiter: n/a (no IInGameLlmChatService / limit off)";
            }

            int requestsInWindow = calc.GetRequestsInWindow(nowSeconds);
            long tokensInWindow = calc.GetTokensInWindow(nowSeconds);
            return limiterLine +
                   $"\nAll LLM usage: {requestsInWindow} req / {tokensInWindow} tok in last {(int)calc.WindowSeconds}s";
        }

        /// <summary>Renders a 10-segment text load bar, e.g. <c>[###.......]</c>.</summary>
        public static string FormatLoadBar(int value, int max)
        {
            if (max <= 0)
            {
                return "";
            }

            double ratio = value / (double)max;
            int filled = (int)Math.Round(ratio * 10d, MidpointRounding.AwayFromZero);
            filled = filled < 0 ? 0 : filled > 10 ? 10 : filled;
            return "[" + new string('#', filled) + new string('.', 10 - filled) + "]";
        }

        /// <summary>Token count for display: negative (unknown) renders as <c>-</c>.</summary>
        public static string FormatTokenCount(int value)
        {
            return value < 0 ? "-" : value.ToString();
        }
    }
}
