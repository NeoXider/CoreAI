using System;
using System.Collections.Generic;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>
    /// The answer to one question: the chosen option, the probability of every option (aligned with
    /// <see cref="DecisionQuestion.Options"/>, summing to 1), a confidence and whether the probabilities are the
    /// model's own (<see cref="Calibrated"/>) or reconstructed from chat logprobs.
    /// </summary>
    public sealed class DecisionAnswer
    {
        private static readonly IReadOnlyList<double> NoProbabilities = Array.Empty<double>();
        private static readonly IReadOnlyList<string> NoOptions = Array.Empty<string>();

        /// <summary>Creates an answer.</summary>
        public DecisionAnswer(
            string questionId,
            DecisionQuestionKind kind,
            IReadOnlyList<string> optionIds,
            IReadOnlyList<double> probabilities,
            int chosenIndex,
            double confidence,
            bool calibrated,
            double? expectedScore = null)
        {
            QuestionId = questionId ?? "";
            Kind = kind;
            OptionIds = optionIds ?? NoOptions;
            Probabilities = probabilities ?? NoProbabilities;
            ChosenIndex = chosenIndex;
            ChosenOptionId = chosenIndex >= 0 && chosenIndex < OptionIds.Count ? OptionIds[chosenIndex] : "";
            Confidence = double.IsNaN(confidence) ? 0d : Math.Max(0d, Math.Min(1d, confidence));
            Calibrated = calibrated;
            ExpectedScore = expectedScore;
        }

        /// <summary>Id of the question this answers.</summary>
        public string QuestionId { get; }

        /// <summary>Kind of the question.</summary>
        public DecisionQuestionKind Kind { get; }

        /// <summary>Option ids, aligned with <see cref="Probabilities"/>.</summary>
        public IReadOnlyList<string> OptionIds { get; }

        /// <summary>Probability of each option, summing to 1; empty for a refusal.</summary>
        public IReadOnlyList<double> Probabilities { get; }

        /// <summary>Index of the chosen option; -1 for a refusal.</summary>
        public int ChosenIndex { get; }

        /// <summary>Id of the chosen option; empty for a refusal.</summary>
        public string ChosenOptionId { get; }

        /// <summary>Confidence in 0..1 as reported by the backend, or the top probability where none is reported.</summary>
        public double Confidence { get; }

        /// <summary>
        /// True when the probabilities come from a decision model itself; false when they were reconstructed (the
        /// logprob fallback, or a server that returned a choice without a distribution). Even a calibrated model
        /// is calibrated on its own training data, not necessarily on a game's.
        /// </summary>
        public bool Calibrated { get; }

        /// <summary>For a score question, the probability-weighted zero-based level; null otherwise.</summary>
        public double? ExpectedScore { get; }

        /// <summary>True when the backend declined this question (OpenRouter's <c>refusal</c> answer).</summary>
        public bool Refused => ChosenIndex < 0;

        /// <summary>For a yes/no question, true when the answer is yes.</summary>
        public bool IsYes => Kind == DecisionQuestionKind.YesNo && ChosenIndex == 0;

        /// <summary>Probability of <paramref name="optionId"/>, or 0 when it is not an option.</summary>
        public double ProbabilityOf(string optionId)
        {
            for (int i = 0; i < OptionIds.Count && i < Probabilities.Count; i++)
            {
                if (string.Equals(OptionIds[i], optionId, StringComparison.Ordinal))
                {
                    return Probabilities[i];
                }
            }

            return 0d;
        }

        /// <summary>An answer the backend declined to give.</summary>
        public static DecisionAnswer Refusal(DecisionQuestion question)
        {
            List<string> ids = new();
            if (question != null)
            {
                foreach (DecisionOption option in question.Options)
                {
                    ids.Add(option?.Id ?? "");
                }
            }

            return new DecisionAnswer(question?.Id ?? "", question?.Kind ?? DecisionQuestionKind.Choice, ids,
                NoProbabilities, -1, 0d, false);
        }
    }

    /// <summary>
    /// Outcome of one <see cref="IDecisionClient.DecideAsync"/> call: answers in question order, usage and, on
    /// failure, a typed error code with a named reason.
    /// </summary>
    public sealed class DecisionResult
    {
        private static readonly IReadOnlyList<DecisionAnswer> NoAnswers = Array.Empty<DecisionAnswer>();

        private DecisionResult(
            IReadOnlyList<DecisionAnswer> answers,
            LlmUsageRecord usage,
            string model,
            string backend,
            LlmErrorCode errorCode,
            string errorReason,
            string error,
            int? httpStatus)
        {
            Answers = answers ?? NoAnswers;
            Usage = usage ?? new LlmUsageRecord();
            Model = model ?? "";
            Backend = backend ?? "";
            ErrorCode = errorCode;
            ErrorReason = errorReason ?? "";
            Error = error ?? "";
            HttpStatus = httpStatus;
        }

        /// <summary>True when every question was answered (an individual answer may still be a refusal).</summary>
        public bool Ok => ErrorCode == LlmErrorCode.None;

        /// <summary>Answers in request question order; empty on failure.</summary>
        public IReadOnlyList<DecisionAnswer> Answers { get; }

        /// <summary>Token usage of the call; never null.</summary>
        public LlmUsageRecord Usage { get; }

        /// <summary>Model that answered, as the server reported it.</summary>
        public string Model { get; }

        /// <summary>Backend that produced the result (for example <c>systemone</c>, <c>openrouter</c>, <c>logprobs</c>).</summary>
        public string Backend { get; }

        /// <summary>Typed failure category; <see cref="LlmErrorCode.None"/> on success.</summary>
        public LlmErrorCode ErrorCode { get; }

        /// <summary>Named reason from <see cref="DecisionErrorReasons"/>; empty on success.</summary>
        public string ErrorReason { get; }

        /// <summary>English, credential-free failure description; empty on success.</summary>
        public string Error { get; }

        /// <summary>HTTP status of a failed exchange, when there was one.</summary>
        public int? HttpStatus { get; }

        /// <summary>True when there is at least one answer and every answer is calibrated.</summary>
        public bool Calibrated
        {
            get
            {
                if (Answers.Count == 0)
                {
                    return false;
                }

                foreach (DecisionAnswer answer in Answers)
                {
                    if (!answer.Calibrated)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>The answer to <paramref name="questionId"/>, or null.</summary>
        public DecisionAnswer Find(string questionId)
        {
            foreach (DecisionAnswer answer in Answers)
            {
                if (string.Equals(answer.QuestionId, questionId, StringComparison.Ordinal))
                {
                    return answer;
                }
            }

            return null;
        }

        /// <summary>A successful result.</summary>
        public static DecisionResult Success(IReadOnlyList<DecisionAnswer> answers, LlmUsageRecord usage,
            string model, string backend)
        {
            if (usage != null)
            {
                usage.Success = true;
            }

            return new DecisionResult(answers, usage, model, backend, LlmErrorCode.None, "", "", null);
        }

        /// <summary>A failed result. <paramref name="errorCode"/> must not be <see cref="LlmErrorCode.None"/>.</summary>
        public static DecisionResult Failure(LlmErrorCode errorCode, string errorReason, string error,
            string backend = "", LlmUsageRecord usage = null, int? httpStatus = null)
        {
            LlmErrorCode code = errorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : errorCode;
            if (usage != null)
            {
                usage.Success = false;
            }

            return new DecisionResult(NoAnswers, usage, "", backend, code, errorReason, error, httpStatus);
        }

        /// <summary>The refusal of an invalid request, built from its first validation error.</summary>
        public static DecisionResult Invalid(IReadOnlyList<DecisionValidationError> errors, string backend = "")
        {
            DecisionValidationError first = errors != null && errors.Count > 0
                ? errors[0]
                : new DecisionValidationError(DecisionErrorReasons.RequestMissing, "", "The decision request is invalid.");
            string message = first.Message;
            if (errors != null && errors.Count > 1)
            {
                message += $" ({errors.Count - 1} more validation error(s).)";
            }

            return Failure(LlmErrorCode.InvalidRequest, first.Code, message, backend);
        }
    }
}
