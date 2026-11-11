using System;
using System.Collections.Generic;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>Protocol-wide limits every backend enforces before a request leaves the process.</summary>
    public static class DecisionLimits
    {
        /// <summary>Fewest options a choice question may have.</summary>
        public const int MinChoiceOptions = 1;

        /// <summary>Most options a choice question may have.</summary>
        public const int MaxChoiceOptions = 255;

        /// <summary>Fewest levels a score question may have.</summary>
        public const int MinScoreLevels = 1;

        /// <summary>Most levels a score question may have.</summary>
        public const int MaxScoreLevels = 10;

        /// <summary>
        /// Longest question id. Question ids are restricted to ASCII letters, digits, <c>_</c>, <c>.</c> and
        /// <c>-</c>, the strictest rule among the servers that speak the protocol (Ollama).
        /// </summary>
        public const int MaxQuestionIdLength = 100;
    }

    /// <summary>
    /// One decision call: the state the questions refer to, the ordered questions and optional images.
    /// The request keeps the caller's lists without copying them; keep them unchanged until the call completes.
    /// </summary>
    public sealed class DecisionRequest
    {
        /// <summary>Creates a request.</summary>
        /// <param name="state">The content every question refers to; null becomes empty.</param>
        /// <param name="questions">Questions in answer order.</param>
        /// <param name="images">Optional inline images (the protocol's assets).</param>
        public DecisionRequest(string state, IReadOnlyList<DecisionQuestion> questions,
            IReadOnlyList<AiAttachment> images = null)
        {
            State = state ?? "";
            Questions = questions ?? Array.Empty<DecisionQuestion>();
            Images = images ?? Array.Empty<AiAttachment>();
        }

        /// <summary>Creates a text-only request.</summary>
        public DecisionRequest(string state, params DecisionQuestion[] questions)
            : this(state, (IReadOnlyList<DecisionQuestion>)(questions ?? Array.Empty<DecisionQuestion>()))
        {
        }

        /// <summary>The content every question refers to.</summary>
        public string State { get; }

        /// <summary>Questions in answer order.</summary>
        public IReadOnlyList<DecisionQuestion> Questions { get; }

        /// <summary>Inline images; empty for a text-only request.</summary>
        public IReadOnlyList<AiAttachment> Images { get; }

        /// <summary>Consumer that asks (for example <c>npc-brain</c>); recorded as the usage role.</summary>
        public string ConsumerId { get; set; } = "";

        /// <summary>Optional trace id recorded on the usage record.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>The question with <paramref name="questionId"/>, or null.</summary>
        public DecisionQuestion FindQuestion(string questionId)
        {
            foreach (DecisionQuestion question in Questions)
            {
                if (question != null && string.Equals(question.Id, questionId, StringComparison.Ordinal))
                {
                    return question;
                }
            }

            return null;
        }

        /// <summary>Validation errors; empty when the request may be sent.</summary>
        public IReadOnlyList<DecisionValidationError> Validate()
        {
            return DecisionRequestValidator.Validate(this);
        }
    }
}
