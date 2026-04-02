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
        /// <summary>Отключить категорию или пустая маска.</summary>
        None = 0,

        /// <summary>Общие сообщения ядра (не DI/сцена).</summary>
        Core = 1 << 0,

        /// <summary>VContainer, lifetime scope, bootstrap.</summary>
        Composition = 1 << 1,

        /// <summary>Шина команд, Lua-пайплайн, подписки MessagePipe.</summary>
        MessagePipe = 1 << 2,

        /// <summary>Пример Roguelite-арены (отладочные логи демо).</summary>
        ExampleRoguelite = 1 << 3,
        /// <summary>Запросы/ответы LLM (декоратор в CoreAI.Infrastructure.Llm).</summary>
        Llm = 1 << 4,

        /// <summary>Метрики оркестратора (<see cref="CoreAI.Infrastructure.Ai.LoggingAiOrchestrationMetrics"/>).</summary>
        Metrics = 1 << 5,

        /// <summary>Все встроенные категории (для дефолтного asset).</summary>
        AllBuiltIn = Core | Composition | MessagePipe | ExampleRoguelite | Llm,

        /// <summary>Зарезервировано под пользовательские биты в asset (или расширяйте enum).</summary>
        CustomA = 1 << 8,
        CustomB = 1 << 9,

        All = AllBuiltIn | CustomA | CustomB
    }
}
