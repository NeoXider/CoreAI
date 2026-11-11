using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>Whether a policy accepts a model answer, and if not, why.</summary>
    public readonly struct DecisionVerdict
    {
        /// <summary>Creates a verdict.</summary>
        public DecisionVerdict(bool accepted, string optionId, double confidence, bool calibrated,
            string fallbackReason, LlmErrorCode errorCode, string errorReason)
        {
            Accepted = accepted;
            OptionId = optionId ?? "";
            Confidence = confidence;
            Calibrated = calibrated;
            FallbackReason = fallbackReason ?? "";
            ErrorCode = errorCode;
            ErrorReason = errorReason ?? "";
        }

        /// <summary>True when the model's choice stands.</summary>
        public bool Accepted { get; }

        /// <summary>The model's choice (also set below the threshold, for diagnostics).</summary>
        public string OptionId { get; }

        /// <summary>The answer's confidence; 0 without an answer.</summary>
        public double Confidence { get; }

        /// <summary>Whether the answer was calibrated.</summary>
        public bool Calibrated { get; }

        /// <summary>Reason from <see cref="DecisionFallbackReasons"/> when not accepted.</summary>
        public string FallbackReason { get; }

        /// <summary>Error code of a failed call.</summary>
        public LlmErrorCode ErrorCode { get; }

        /// <summary>Named error reason of a failed call.</summary>
        public string ErrorReason { get; }
    }

    /// <summary>Request building and the confidence gate shared by the choice-based policies.</summary>
    public static class DecisionChoice
    {
        /// <summary>The state text: the host's description followed by the recent events, oldest first.</summary>
        public static string ComposeState(string description, IReadOnlyList<string> recentEvents)
        {
            StringBuilder state = new(description ?? "");
            if (recentEvents != null && recentEvents.Count > 0)
            {
                if (state.Length > 0)
                {
                    state.Append("\n\n");
                }

                state.Append("Recent events:");
                foreach (string line in recentEvents)
                {
                    state.Append("\n- ").Append(line);
                }
            }

            return state.ToString();
        }

        /// <summary>
        /// Accepts the answer to <paramref name="questionId"/> when the call succeeded, the answer is not a refusal,
        /// names an action of <paramref name="actions"/> and has a confidence of at least <paramref name="threshold"/>.
        /// </summary>
        public static DecisionVerdict Evaluate(DecisionResult result, string questionId, DecisionActionSet actions,
            double threshold)
        {
            if (result == null || !result.Ok)
            {
                return new DecisionVerdict(false, "", 0d, false, DecisionFallbackReasons.CallFailed,
                    result?.ErrorCode ?? LlmErrorCode.ProviderError, result?.ErrorReason ?? DecisionErrorReasons.ClientThrew);
            }

            DecisionAnswer answer = result.Find(questionId);
            if (answer == null)
            {
                return new DecisionVerdict(false, "", 0d, false, DecisionFallbackReasons.NoAnswer, LlmErrorCode.None, "");
            }

            if (answer.Refused)
            {
                return new DecisionVerdict(false, "", 0d, false, DecisionFallbackReasons.Refused, LlmErrorCode.None, "");
            }

            if (!actions.Contains(answer.ChosenOptionId))
            {
                return new DecisionVerdict(false, answer.ChosenOptionId, answer.Confidence, answer.Calibrated,
                    DecisionFallbackReasons.UnknownAction, LlmErrorCode.None, "");
            }

            bool confident = answer.Confidence >= threshold;
            return new DecisionVerdict(confident, answer.ChosenOptionId, answer.Confidence, answer.Calibrated,
                confident ? "" : DecisionFallbackReasons.LowConfidence, LlmErrorCode.None, "");
        }
    }
}
