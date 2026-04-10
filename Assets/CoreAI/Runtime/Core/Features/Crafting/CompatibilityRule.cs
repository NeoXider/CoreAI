using System.Collections.Generic;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Правило совместимости для произвольного набора элементов/групп.
    /// Поддерживает пары (2 элемента), тройки, четвёрки — любое количество.
    /// 
    /// Пример пара: Elements = {"Fire", "Water"}, Score = 0 → несовместимо
    /// Пример тройка: Elements = {"Fire", "Earth", "Air"}, Score = 1.5 → бонусная синергия
    /// Пример четвёрка: Elements = {"Iron", "Carbon", "Fire", "Water"}, Score = 2.0 → мастер-рецепт
    /// </summary>
    public sealed class CompatibilityRule
    {
        /// <summary>
        /// Набор элементов (имена или группы), к которым относится правило.
        /// Порядок не важен. Минимум 2 элемента.
        /// </summary>
        public List<string> Elements { get; set; } = new();

        /// <summary>Совместимость: 1.0 = нейтрально, 0.0 = несовместимо, >1.0 = бонус синергии.</summary>
        public float Score { get; set; } = 1.0f;

        /// <summary>Причина (для логирования и LLM).</summary>
        public string Reason { get; set; }

        /// <summary>Является ли это правило блокирующим (completely incompatible).</summary>
        public bool IsBlocking => Score <= 0f;

        /// <summary>Количество элементов в правиле.</summary>
        public int Size => Elements.Count;

        /// <summary>
        /// Создаёт правило из двух элементов (shortcut для парных правил).
        /// </summary>
        public static CompatibilityRule Pair(string a, string b, float score, string reason = null)
        {
            return new CompatibilityRule
            {
                Elements = new List<string> { a, b },
                Score = score,
                Reason = reason
            };
        }

        /// <summary>
        /// Создаёт правило из нескольких элементов.
        /// </summary>
        public static CompatibilityRule Group(float score, string reason, params string[] elements)
        {
            return new CompatibilityRule
            {
                Elements = new List<string>(elements),
                Score = score,
                Reason = reason
            };
        }
    }
}
