using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Инструмент, оборачивающий произвольный C# Delegate.
    /// Позволяет передавать лямбда-выражения и методы с параметрами напрямую в модель,
    /// а MEAI (Microsoft.Extensions.AI) автоматически генерирует для них JSON схему.
    /// </summary>
    public sealed class DelegateLlmTool : ILlmTool
    {
        public string Name { get; }
        public string Description { get; }

        /// <summary>
        /// Всегда возвращает "{}" т.к. реальная схема генерируется в MeaiLlmClient 
        /// через AIFunctionFactory на основе Delegate.
        /// </summary>
        public string ParametersSchema => "{}";
        
        public bool AllowDuplicates { get; set; }

        public Delegate ActionDelegate { get; }

        public DelegateLlmTool(string name, string description, Delegate action)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            ActionDelegate = action ?? throw new ArgumentNullException(nameof(action));
        }
    }
}