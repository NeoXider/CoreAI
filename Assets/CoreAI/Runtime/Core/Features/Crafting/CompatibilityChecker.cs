using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Проверка совместимости ингредиентов для CoreMechanicAI.
    /// Поддерживает:
    /// - Группы элементов (Fire, Water, Earth, Air и т.д.)
    /// - Правила совместимости любого размера (пары, тройки, четвёрки и т.д.)
    /// - Кастомные валидаторы (ICompatibilityValidator)
    /// - Расчёт общего CompatibilityScore
    /// 
    /// Алгоритм:
    /// 1. Сначала проверяются правила на полный набор (N элементов)
    /// 2. Затем правила на подмножества (N-1, N-2, ... до пар)
    /// 3. Правила с большим числом элементов имеют приоритет
    /// 4. Кастомные валидаторы применяются последними
    /// </summary>
    public sealed class CompatibilityChecker
    {
        private readonly Dictionary<string, string> _elementGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CompatibilityRule> _rules = new();
        private readonly List<ICompatibilityValidator> _validators = new();
        private readonly float _defaultScore;

        /// <summary>
        /// Создаёт CompatibilityChecker.
        /// </summary>
        /// <param name="defaultScore">Базовый score для комбинаций без явного правила (1.0 = нейтрально).</param>
        public CompatibilityChecker(float defaultScore = 1.0f)
        {
            _defaultScore = defaultScore;
        }

        /// <summary>
        /// Регистрирует элемент в группе (например: "IronOre" → "Metal", "WaterFlask" → "Water").
        /// </summary>
        public void RegisterElement(string element, string group)
        {
            if (string.IsNullOrEmpty(element)) throw new ArgumentNullException(nameof(element));
            if (string.IsNullOrEmpty(group)) throw new ArgumentNullException(nameof(group));
            _elementGroups[element] = group;
        }

        /// <summary>
        /// Добавляет правило совместимости.
        /// </summary>
        public void AddRule(CompatibilityRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            if (rule.Elements.Count < 2)
                throw new ArgumentException("Rule must have at least 2 elements", nameof(rule));
            _rules.Add(rule);
        }

        /// <summary>
        /// Добавляет парное правило совместимости (shortcut).
        /// </summary>
        public void AddRule(string elementA, string elementB, float score, string reason = null)
        {
            _rules.Add(CompatibilityRule.Pair(elementA, elementB, score, reason));
        }

        /// <summary>
        /// Добавляет правило для группы ингредиентов (3+).
        /// </summary>
        public void AddGroupRule(float score, string reason, params string[] elements)
        {
            AddRule(CompatibilityRule.Group(score, reason, elements));
        }

        /// <summary>
        /// Регистрирует кастомный валидатор.
        /// </summary>
        public void AddValidator(ICompatibilityValidator validator)
        {
            _validators.Add(validator ?? throw new ArgumentNullException(nameof(validator)));
        }

        /// <summary>
        /// Проверяет совместимость набора ингредиентов.
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

            // Нормализуем имена ингредиентов для поиска (элемент → группа, если есть)
            List<string> resolved = new();
            foreach (string ing in ingredients)
                resolved.Add(GetGroup(ing) ?? ing);

            // === Этап 1: Ищем правила, которые полностью матчат подмножество ингредиентов ===
            // Правила отсортированы: сначала по размеру DESC (правила на 4 элемента приоритетнее чем на 2)
            List<CompatibilityRule> sortedRules = _rules.OrderByDescending(r => r.Size).ToList();
            List<CompatibilityRule> matchedRules = new();

            foreach (CompatibilityRule rule in sortedRules)
            {
                if (IsSubsetMatch(rule.Elements, ingredients, resolved))
                {
                    matchedRules.Add(rule);
                }
            }

            // === Этап 2: Рассчитываем итоговый score ===
            float combinedScore;

            if (matchedRules.Count > 0)
            {
                // Используем взвешенное среднее: правила с большим размером весят больше
                float weightedSum = 0f;
                float totalWeight = 0f;

                foreach (CompatibilityRule rule in matchedRules)
                {
                    float weight = rule.Size; // вес = количество элементов в правиле
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
                // Нет правил — используем default
                combinedScore = _defaultScore;
            }

            // === Этап 3: Кастомные валидаторы ===
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

            // === Результат ===
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
        /// Проверяет совместимость (params shortcut).
        /// </summary>
        public CompatibilityResult Check(params string[] ingredients)
        {
            return Check((IReadOnlyList<string>)ingredients);
        }

        /// <summary>
        /// Проверяет, является ли набор элементов правила подмножеством ингредиентов.
        /// Учитывает как прямые имена, так и группы.
        /// </summary>
        private bool IsSubsetMatch(List<string> ruleElements, IReadOnlyList<string> ingredients,
            List<string> resolvedGroups)
        {
            if (ruleElements.Count > ingredients.Count) return false;

            // Для каждого элемента правила должен найтись ингредиент (по имени или группе)
            bool[] used = new bool[ingredients.Count];

            foreach (string ruleEl in ruleElements)
            {
                bool found = false;
                for (int i = 0; i < ingredients.Count; i++)
                {
                    if (used[i]) continue;

                    // Проверяем по прямому имени
                    if (ruleEl.Equals(ingredients[i], StringComparison.OrdinalIgnoreCase))
                    {
                        used[i] = true;
                        found = true;
                        break;
                    }

                    // Проверяем по группе
                    if (resolvedGroups[i] != null &&
                        ruleEl.Equals(resolvedGroups[i], StringComparison.OrdinalIgnoreCase))
                    {
                        used[i] = true;
                        found = true;
                        break;
                    }
                }

                if (!found) return false;
            }

            return true;
        }

        private string GetGroup(string element)
        {
            return _elementGroups.TryGetValue(element, out string group) ? group : null;
        }

        /// <summary>Количество зарегистрированных правил.</summary>
        public int RuleCount => _rules.Count;

        /// <summary>Количество зарегистрированных элементов.</summary>
        public int ElementCount => _elementGroups.Count;
    }
}
