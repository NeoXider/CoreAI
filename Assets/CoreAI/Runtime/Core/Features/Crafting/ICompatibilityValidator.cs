using System.Collections.Generic;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Интерфейс для пользовательской валидации совместимости.
    /// Позволяет игре добавить свои правила (физика, химия, магия и т.д.).
    /// </summary>
    public interface ICompatibilityValidator
    {
        /// <summary>
        /// Проверяет совместимость набора ингредиентов.
        /// Возвращает null если валидатор не имеет мнения по данным ингредиентам.
        /// </summary>
        CompatibilityResult Validate(IReadOnlyList<string> ingredients);
    }
}
