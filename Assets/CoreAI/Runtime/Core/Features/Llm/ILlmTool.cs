using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Инструмент (функция), который может вызвать LLM.
    /// Используется для tool calling - модель может запросить выполнение инструмента.
    /// </summary>
    public interface ILlmTool
    {
        /// <summary>Уникальное имя инструмента.</summary>
        string Name { get; }

        /// <summary>Описание что делает инструмент.</summary>
        string Description { get; }

        /// <summary>JSON schema параметров инструмента.</summary>
        string ParametersSchema { get; }
    }

    /// <summary>
    /// Базовый класс для простых инструментов с JSON параметрами.
    /// </summary>
    public abstract class LlmToolBase : ILlmTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual string ParametersSchema => "{}";

        protected static string JsonParams(params (string name, string type, bool required, string desc)[] p)
        {
            var props = new List<string>();
            foreach (var (name, type, required, desc) in p)
            {
                props.Add($"\"{name}\":{{\"type\":\"{type}\",\"description\":\"{desc}\"{(required ? ",\"required\":true" : "")}}}");
            }
            return $"{{\"type\":\"object\",\"properties\":{{ {string.Join(",",props)} }}}}";
        }
    }
}