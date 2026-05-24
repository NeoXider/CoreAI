using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Evaluates crafting compatibility rules for requested element combinations.
    /// </summary>
    public sealed class CompatibilityChecker
    {
        private readonly Dictionary<string, string> _elementGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CompatibilityRule> _rules = new();
        private readonly List<ICompatibilityValidator> _validators = new();
        private readonly float _defaultScore;

        /// <summary>
        /// Initializes a new instance of CompatibilityChecker.
        /// </summary>
        /// <param name="defaultScore">The default score value.</param>
        public CompatibilityChecker(float defaultScore = 1.0f)
        {
            _defaultScore = defaultScore;
        }

        /// <summary>
/// Executes RegisterElement API operation.
        /// </summary>
        public void RegisterElement(string element, string group)
        {
            if (string.IsNullOrEmpty(element))
            {
                throw new ArgumentNullException(nameof(element));
            }

            if (string.IsNullOrEmpty(group))
            {
                throw new ArgumentNullException(nameof(group));
            }

            _elementGroups[element] = group;
        }

        /// <summary>
/// Executes AddRule API operation.
        /// </summary>
        public void AddRule(CompatibilityRule rule)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            if (rule.Elements.Count < 2)
            {
                throw new ArgumentException("Rule must have at least 2 elements", nameof(rule));
            }

            _rules.Add(rule);
        }

        /// <summary>
/// Executes AddRule API operation.
        /// </summary>
        public void AddRule(string elementA, string elementB, float score, string reason = null)
        {
            _rules.Add(CompatibilityRule.Pair(elementA, elementB, score, reason));
        }

        /// <summary>
/// Executes AddGroupRule API operation.
        /// </summary>
        public void AddGroupRule(float score, string reason, params string[] elements)
        {
            AddRule(CompatibilityRule.Group(score, reason, elements));
        }

        /// <summary>
/// Executes AddValidator API operation.
        /// </summary>
        public void AddValidator(ICompatibilityValidator validator)
        {
            _validators.Add(validator ?? throw new ArgumentNullException(nameof(validator)));
        }

        /// <summary>
/// Executes Check API operation.
        /// </summary>
        public CompatibilityResult Check(IReadOnlyList<string> ingredients)
        {
            if (ingredients == null || ingredients.Count == 0)
            {
                return new CompatibilityResult
                {
                    IsCompatible = false,
                    CompatibilityScore = 0f,
                    Reason = "No ingredients provided"
                };
            }

            if (ingredients.Count == 1)
            {
                return new CompatibilityResult
                {
                    IsCompatible = true,
                    CompatibilityScore = 1.0f,
                    Reason = "Single ingredient is always compatible"
                };
            }

            List<string> warnings = new();
            List<string> bonuses = new();
            bool hasBlocking = false;
            string blockingReason = null;

            /* Implementation note in English. */
            List<string> resolved = new();
            foreach (string ing in ingredients)
            {
                resolved.Add(GetGroup(ing) ?? ing);
            }

            /* Implementation note in English. */
            /* Implementation note in English. */
            List<CompatibilityRule> sortedRules = _rules.OrderByDescending(r => r.Size).ToList();
            List<CompatibilityRule> matchedRules = new();

            foreach (CompatibilityRule rule in sortedRules)
            {
                if (IsSubsetMatch(rule.Elements, ingredients, resolved))
                {
                    matchedRules.Add(rule);
                }
            }

            /* Implementation note in English. */
            float combinedScore;

            if (matchedRules.Count > 0)
            {
                /* Implementation note in English. */
                float weightedSum = 0f;
                float totalWeight = 0f;

                foreach (CompatibilityRule rule in matchedRules)
                {
                    float weight = rule.Size; // Weight is the number of rule elements.
                    weightedSum += rule.Score * weight;
                    totalWeight += weight;

                    if (rule.IsBlocking)
                    {
                        hasBlocking = true;
                        string elements = string.Join(", ", rule.Elements);
                        blockingReason = rule.Reason ?? $"Combination [{elements}] is incompatible";
                        warnings.Add(blockingReason);
                    }
                    else if (rule.Score > 1.0f)
                    {
                        string elements = string.Join(", ", rule.Elements);
                        bonuses.Add(rule.Reason ?? $"[{elements}] synergy bonus (x{rule.Score:F1})");
                    }
                    else if (rule.Score < 1.0f)
                    {
                        string elements = string.Join(", ", rule.Elements);
                        warnings.Add(rule.Reason ?? $"[{elements}] reduced compatibility ({rule.Score:F1})");
                    }
                }

                combinedScore = totalWeight > 0 ? weightedSum / totalWeight : _defaultScore;
            }
            else
            {
                /* Implementation note in English. */
                combinedScore = _defaultScore;
            }

            /* Implementation note in English. */
            foreach (ICompatibilityValidator validator in _validators)
            {
                CompatibilityResult customResult = validator.Validate(ingredients);
                if (customResult != null)
                {
                    if (!customResult.IsCompatible)
                    {
                        hasBlocking = true;
                        blockingReason = customResult.Reason ?? "Custom validator rejected combination";
                    }

                    combinedScore *= customResult.CompatibilityScore;
                    warnings.AddRange(customResult.Warnings);
                    bonuses.AddRange(customResult.Bonuses);
                }
            }

            /* Implementation note in English. */
            if (hasBlocking)
            {
                return new CompatibilityResult
                {
                    IsCompatible = false,
                    CompatibilityScore = 0f,
                    Reason = blockingReason,
                    Warnings = warnings,
                    Bonuses = bonuses
                };
            }

            return new CompatibilityResult
            {
                IsCompatible = true,
                CompatibilityScore = Math.Min(combinedScore, 2.0f),
                Reason = combinedScore > 1.0f
                    ? "Ingredients have bonus synergy"
                    : "Ingredients are compatible",
                Warnings = warnings,
                Bonuses = bonuses
            };
        }

        /// <summary>
/// Executes Check API operation.
        /// </summary>
        public CompatibilityResult Check(params string[] ingredients)
        {
            return Check((IReadOnlyList<string>)ingredients);
        }

        /// <summary>
/// Executes IsSubsetMatch API operation.
        ///
        /// </summary>
        private bool IsSubsetMatch(List<string> ruleElements, IReadOnlyList<string> ingredients,
            List<string> resolvedGroups)
        {
            if (ruleElements.Count > ingredients.Count)
            {
                return false;
            }

            /* Implementation note in English. */
            bool[] used = new bool[ingredients.Count];

            foreach (string ruleEl in ruleElements)
            {
                bool found = false;
                for (int i = 0; i < ingredients.Count; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    /* Implementation note in English. */
                    if (ruleEl.Equals(ingredients[i], StringComparison.OrdinalIgnoreCase))
                    {
                        used[i] = true;
                        found = true;
                        break;
                    }

                    /* Implementation note in English. */
                    if (resolvedGroups[i] != null &&
                        ruleEl.Equals(resolvedGroups[i], StringComparison.OrdinalIgnoreCase))
                    {
                        used[i] = true;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        private string GetGroup(string element)
        {
            return _elementGroups.TryGetValue(element, out string group) ? group : null;
        }

        /// <summary>Rule count.</summary>
        public int RuleCount => _rules.Count;

        /// <summary>Element count.</summary>
        public int ElementCount => _elementGroups.Count;
    }
}
