using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Reserves completion headroom plus fixed prompt, assigns remaining tokens to chat history.
    /// On <see cref="ContextBudgetRequest.ContextRetryLevel"/> &gt;= 1, halves history budget (minimum floor).
    /// </summary>
    public sealed class DefaultContextBudgetPolicy : IContextBudgetPolicy
    {
        private const int MinHistoryBudget = 32;
        private const int AbsoluteMinHistoryBudgetOnRetry = 16;
        private const int MinCompletionReserve = 64;
        private const int SlackDefault = 64;

        /// <inheritdoc />
        public ContextBudget Compute(ContextBudgetRequest request, ITokenEstimator estimator)
        {
            estimator ??= new HeuristicTokenEstimator();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            int maxCtx = Math.Max(256, request.MaxContextTokens <= 0 ? 8192 : request.MaxContextTokens);
            int reservedCompletion = ResolveCompletionReserve(maxCtx, request.MaxOutputTokens);

            int systemEst = estimator.EstimateText(request.SystemPrompt ?? "");
            int userEst = estimator.EstimateText(request.UserPayload ?? "");
            int toolsEst = EstimateToolsTokens(request.Tools, estimator);
            int slack = SlackDefault;
            int fixedTotal = systemEst + userEst + toolsEst + slack;

            int usable = maxCtx - reservedCompletion - fixedTotal;
            if (usable < MinHistoryBudget)
            {
                slack = Math.Max(0, slack + usable - MinHistoryBudget);
                fixedTotal = systemEst + userEst + toolsEst + slack;
                usable = maxCtx - reservedCompletion - fixedTotal;
            }

            int historyBudget = Math.Max(MinHistoryBudget, usable);
            if (request.ContextRetryLevel >= 1)
            {
                historyBudget = Math.Max(AbsoluteMinHistoryBudgetOnRetry, historyBudget / 2);
            }

            return new ContextBudget(
                maxCtx,
                reservedCompletion,
                systemEst + userEst + toolsEst,
                historyBudget,
                slack);
        }

        private static int ResolveCompletionReserve(int maxContext, int? maxOutputTokens)
        {
            if (maxOutputTokens.HasValue && maxOutputTokens.Value > 0)
            {
                return Math.Clamp(maxOutputTokens.Value, MinCompletionReserve, maxContext / 2);
            }

            int quarter = Math.Max(MinCompletionReserve, maxContext / 4);
            return Math.Min(quarter, maxContext / 2);
        }

        private static int EstimateToolsTokens(IReadOnlyList<ILlmTool> tools, ITokenEstimator estimator)
        {
            if (tools == null || tools.Count == 0)
            {
                return 0;
            }

            int sum = 0;
            for (int i = 0; i < tools.Count; i++)
            {
                ILlmTool t = tools[i];
                if (t == null)
                {
                    continue;
                }

                sum += estimator.EstimateText(t.Name ?? "");
                sum += estimator.EstimateText(t.Description ?? "");
                sum += estimator.EstimateText(t.ParametersSchema ?? "");
                sum += 8;
            }

            return sum;
        }
    }
}
