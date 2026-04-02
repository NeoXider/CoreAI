using System;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Категория логов (фича / подсистема). В инспекторе ScriptableObject отмечаются нужные флаги.
    /// Добавляйте свои значения по мере роста проекта (степень двойки).
    /// </summary>
    [Flags]
    public enum GameLogFeature
    {
        None = 0,
        Core = 1 << 0,
        Composition = 1 << 1,
        MessagePipe = 1 << 2,
        ExampleRoguelite = 1 << 3,
        /// <summary>Запросы/ответы LLM (декоратор в CoreAI.Infrastructure.Llm).</summary>
        Llm = 1 << 4,

        /// <summary>Все встроенные категории (для дефолтного asset).</summary>
        AllBuiltIn = Core | Composition | MessagePipe | ExampleRoguelite | Llm,

        /// <summary>Зарезервировано под пользовательские биты в asset (или расширяйте enum).</summary>
        CustomA = 1 << 8,
        CustomB = 1 << 9,

        All = AllBuiltIn | CustomA | CustomB
    }
}
