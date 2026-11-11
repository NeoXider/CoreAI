using System;
using System.Collections.Generic;

namespace CoreAI.Decisions
{
    /// <summary>One action (or goal) a policy may choose: a machine id the actuator understands and guidance text.</summary>
    public sealed class DecisionActionDefinition
    {
        /// <summary>Creates an action.</summary>
        public DecisionActionDefinition(string id, string description = "")
        {
            Id = id ?? "";
            Description = description ?? "";
        }

        /// <summary>Machine id passed to the actuator.</summary>
        public string Id { get; }

        /// <summary>Guidance for the model.</summary>
        public string Description { get; }
    }

    /// <summary>
    /// A closed list of actions, defined as data. A policy never acts outside it: the model chooses one of its ids
    /// and the fallback must return one of them too.
    /// </summary>
    public sealed class DecisionActionSet
    {
        private readonly List<DecisionActionDefinition> _actions;

        /// <summary>Creates a set (copied).</summary>
        public DecisionActionSet(IEnumerable<DecisionActionDefinition> actions)
        {
            _actions = new List<DecisionActionDefinition>(actions ?? Array.Empty<DecisionActionDefinition>());
        }

        /// <summary>A set of bare ids.</summary>
        public static DecisionActionSet Of(params string[] ids)
        {
            List<DecisionActionDefinition> actions = new();
            foreach (string id in ids ?? Array.Empty<string>())
            {
                actions.Add(new DecisionActionDefinition(id));
            }

            return new DecisionActionSet(actions);
        }

        /// <summary>Actions in order.</summary>
        public IReadOnlyList<DecisionActionDefinition> Actions => _actions;

        /// <summary>True when <paramref name="id"/> is an action of this set.</summary>
        public bool Contains(string id)
        {
            foreach (DecisionActionDefinition action in _actions)
            {
                if (action != null && string.Equals(action.Id, id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The choice question over this set.</summary>
        public DecisionQuestion ToChoiceQuestion(string questionId, string instructions)
        {
            List<DecisionOption> options = new();
            foreach (DecisionActionDefinition action in _actions)
            {
                options.Add(new DecisionOption(action?.Id ?? "", action?.Description ?? ""));
            }

            return DecisionQuestion.Choice(questionId, instructions, options);
        }

        /// <summary>Validation errors (empty set, more than 255 actions, empty or duplicate ids).</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new();
            if (_actions.Count < DecisionLimits.MinChoiceOptions)
            {
                errors.Add("The action set is empty.");
            }

            if (_actions.Count > DecisionLimits.MaxChoiceOptions)
            {
                errors.Add($"The action set has {_actions.Count} actions; at most {DecisionLimits.MaxChoiceOptions} are allowed.");
            }

            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (DecisionActionDefinition action in _actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Id))
                {
                    errors.Add("An action has no id.");
                }
                else if (!ids.Add(action.Id))
                {
                    errors.Add($"Action id '{action.Id}' is used twice.");
                }
            }

            return errors;
        }
    }
}
