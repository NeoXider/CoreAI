using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.Decisions
{
    /// <summary>Shape of one decision question.</summary>
    public enum DecisionQuestionKind
    {
        /// <summary>A yes/no question or a true/false statement (System One type <c>noul</c>).</summary>
        YesNo = 0,

        /// <summary>One of 1-255 labelled options (System One type <c>choice</c>).</summary>
        Choice = 1,

        /// <summary>One of 1-10 ordered levels, lowest first (System One type <c>score</c>).</summary>
        Score = 2
    }

    /// <summary>One labelled option of a question. The id is the machine value the game acts on.</summary>
    public sealed class DecisionOption
    {
        /// <summary>Creates an option.</summary>
        /// <param name="id">Stable machine id, unique within its question (for example <c>attack</c>).</param>
        /// <param name="description">Optional guidance for the model; empty lets the id speak for itself.</param>
        public DecisionOption(string id, string description = "")
        {
            Id = id ?? "";
            Description = description ?? "";
        }

        /// <summary>Stable machine id, unique within its question.</summary>
        public string Id { get; }

        /// <summary>Guidance for the model; may be empty.</summary>
        public string Description { get; }
    }

    /// <summary>
    /// One typed question. Every kind is a closed list of options: a yes/no question has the options
    /// <see cref="YesOptionId"/> and <see cref="NoOptionId"/> in that order, a score question has one option
    /// per level whose id is the zero-based level index.
    /// </summary>
    public sealed class DecisionQuestion
    {
        /// <summary>Option id of the "yes" / "true" answer; index 0 of a yes/no question.</summary>
        public const string YesOptionId = "yes";

        /// <summary>Option id of the "no" / "false" answer; index 1 of a yes/no question.</summary>
        public const string NoOptionId = "no";

        private DecisionQuestion(string id, DecisionQuestionKind kind, string instructions,
            IReadOnlyList<DecisionOption> options)
        {
            Id = id ?? "";
            Kind = kind;
            Instructions = instructions ?? "";
            Options = options;
        }

        /// <summary>Question id, unique within its request; also the key of its answer.</summary>
        public string Id { get; }

        /// <summary>Question kind.</summary>
        public DecisionQuestionKind Kind { get; }

        /// <summary>The question text; may be empty when the options say everything.</summary>
        public string Instructions { get; }

        /// <summary>Options in answer order; the probability vector of the answer is aligned with it.</summary>
        public IReadOnlyList<DecisionOption> Options { get; }

        /// <summary>Creates a yes/no question.</summary>
        /// <param name="id">Question id.</param>
        /// <param name="instructions">The question or statement.</param>
        /// <param name="yesMeans">Optional description of when the answer is yes.</param>
        /// <param name="noMeans">Optional description of when the answer is no.</param>
        public static DecisionQuestion YesNo(string id, string instructions, string yesMeans = "", string noMeans = "")
        {
            return new DecisionQuestion(id, DecisionQuestionKind.YesNo, instructions, new[]
            {
                new DecisionOption(YesOptionId, yesMeans),
                new DecisionOption(NoOptionId, noMeans)
            });
        }

        /// <summary>Creates a choice question over labelled options (1-255; validated by the request).</summary>
        public static DecisionQuestion Choice(string id, string instructions, IReadOnlyList<DecisionOption> options)
        {
            List<DecisionOption> copy = new(options ?? Array.Empty<DecisionOption>());
            return new DecisionQuestion(id, DecisionQuestionKind.Choice, instructions, copy.AsReadOnly());
        }

        /// <summary>Creates a choice question whose options are bare ids without descriptions.</summary>
        public static DecisionQuestion Choice(string id, string instructions, params string[] optionIds)
        {
            List<DecisionOption> options = new();
            foreach (string optionId in optionIds ?? Array.Empty<string>())
            {
                options.Add(new DecisionOption(optionId));
            }

            return new DecisionQuestion(id, DecisionQuestionKind.Choice, instructions, options.AsReadOnly());
        }

        /// <summary>
        /// Creates a score question. <paramref name="levels"/> describes each level, lowest first (1-10; validated
        /// by the request); the option id of level <c>i</c> is <c>i</c> written in invariant digits.
        /// </summary>
        public static DecisionQuestion Score(string id, string instructions, IReadOnlyList<string> levels)
        {
            List<DecisionOption> options = new();
            IReadOnlyList<string> source = levels ?? Array.Empty<string>();
            for (int i = 0; i < source.Count; i++)
            {
                options.Add(new DecisionOption(i.ToString(CultureInfo.InvariantCulture), source[i]));
            }

            return new DecisionQuestion(id, DecisionQuestionKind.Score, instructions, options.AsReadOnly());
        }

        /// <summary>Index of the option with <paramref name="optionId"/>, or -1.</summary>
        public int IndexOf(string optionId)
        {
            if (optionId == null)
            {
                return -1;
            }

            for (int i = 0; i < Options.Count; i++)
            {
                if (Options[i] != null && string.Equals(Options[i].Id, optionId, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
