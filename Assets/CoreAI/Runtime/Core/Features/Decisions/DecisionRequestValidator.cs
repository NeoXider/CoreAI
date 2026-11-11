using System;
using System.Collections.Generic;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>One reason a request may not be sent.</summary>
    public sealed class DecisionValidationError
    {
        /// <summary>Creates a validation error.</summary>
        public DecisionValidationError(string code, string questionId, string message)
        {
            Code = code ?? "";
            QuestionId = questionId ?? "";
            Message = message ?? "";
        }

        /// <summary>Stable reason from <see cref="DecisionErrorReasons"/>.</summary>
        public string Code { get; }

        /// <summary>Question the error is about; empty for request-level errors.</summary>
        public string QuestionId { get; }

        /// <summary>English description for logs and developers.</summary>
        public string Message { get; }
    }

    /// <summary>Validates a request against <see cref="DecisionLimits"/> without contacting anything.</summary>
    public static class DecisionRequestValidator
    {
        /// <summary>Returns every validation error; empty when the request may be sent.</summary>
        public static IReadOnlyList<DecisionValidationError> Validate(DecisionRequest request)
        {
            List<DecisionValidationError> errors = new();
            if (request == null)
            {
                errors.Add(new DecisionValidationError(DecisionErrorReasons.RequestMissing, "",
                    "The decision request is null."));
                return errors;
            }

            if (request.Questions.Count == 0)
            {
                errors.Add(new DecisionValidationError(DecisionErrorReasons.NoQuestions, "",
                    "A decision request needs at least one question."));
            }

            HashSet<string> questionIds = new(StringComparer.Ordinal);
            foreach (DecisionQuestion question in request.Questions)
            {
                if (question == null)
                {
                    errors.Add(new DecisionValidationError(DecisionErrorReasons.QuestionMissing, "",
                        "A question entry is null."));
                    continue;
                }

                if (!IsValidQuestionId(question.Id))
                {
                    errors.Add(new DecisionValidationError(DecisionErrorReasons.InvalidQuestionId, question.Id,
                        $"Question id '{question.Id}' must be 1-{DecisionLimits.MaxQuestionIdLength} ASCII letters, digits, '_', '.' or '-'."));
                }
                else if (!questionIds.Add(question.Id))
                {
                    errors.Add(new DecisionValidationError(DecisionErrorReasons.DuplicateQuestionId, question.Id,
                        $"Question id '{question.Id}' is used twice."));
                }

                ValidateOptions(question, errors);
            }

            foreach (AiAttachment image in request.Images)
            {
                if (image == null || image.Category != AiAttachmentCategory.Image || !image.HasInlineData)
                {
                    errors.Add(new DecisionValidationError(DecisionErrorReasons.UnsupportedImage, "",
                        "Decision images must be inline image data (PNG, JPEG, WebP or GIF); text, URIs and other files are refused."));
                }
            }

            return errors;
        }

        /// <summary>True when <paramref name="id"/> is a valid question id.</summary>
        public static bool IsValidQuestionId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > DecisionLimits.MaxQuestionIdLength)
            {
                return false;
            }

            foreach (char character in id)
            {
                bool allowed = (character >= 'a' && character <= 'z') ||
                               (character >= 'A' && character <= 'Z') ||
                               (character >= '0' && character <= '9') ||
                               character == '_' || character == '.' || character == '-';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateOptions(DecisionQuestion question, List<DecisionValidationError> errors)
        {
            int count = question.Options.Count;
            if (question.Kind == DecisionQuestionKind.Choice)
            {
                if (count < DecisionLimits.MinChoiceOptions)
                {
                    errors.Add(new DecisionValidationError(DecisionErrorReasons.TooFewOptions, question.Id,
                        $"Choice question '{question.Id}' has no options; it needs {DecisionLimits.MinChoiceOptions}-{DecisionLimits.MaxChoiceOptions}."));
                }
                else if (count > DecisionLimits.MaxChoiceOptions)
                {
                    errors.Add(new DecisionValidationError(DecisionErrorReasons.TooManyOptions, question.Id,
                        $"Choice question '{question.Id}' has {count} options; at most {DecisionLimits.MaxChoiceOptions} are allowed."));
                }
            }
            else if (question.Kind == DecisionQuestionKind.Score &&
                     (count < DecisionLimits.MinScoreLevels || count > DecisionLimits.MaxScoreLevels))
            {
                errors.Add(new DecisionValidationError(DecisionErrorReasons.ScoreLevelsOutOfRange, question.Id,
                    $"Score question '{question.Id}' has {count} levels; it needs {DecisionLimits.MinScoreLevels}-{DecisionLimits.MaxScoreLevels}."));
            }

            HashSet<string> optionIds = new(StringComparer.Ordinal);
            foreach (DecisionOption option in question.Options)
            {
                if (option == null || string.IsNullOrWhiteSpace(option.Id))
                {
                    errors.Add(new DecisionValidationError(DecisionErrorReasons.InvalidOptionId, question.Id,
                        $"Question '{question.Id}' has an option without an id."));
                }
                else if (!optionIds.Add(option.Id))
                {
                    errors.Add(new DecisionValidationError(DecisionErrorReasons.DuplicateOptionId, question.Id,
                        $"Question '{question.Id}' uses option id '{option.Id}' twice."));
                }
            }
        }
    }
}
