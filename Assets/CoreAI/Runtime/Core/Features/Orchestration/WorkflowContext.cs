using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Контекст выполнения мульти-агентного воркфлоу: передаёт данные между шагами.
    /// </summary>
    public sealed class WorkflowContext
    {
        /// <summary>Переменные для передачи данных между агентами.</summary>
        public Dictionary<string, string> Variables { get; } = new();

        /// <summary>TraceId текущего воркфлоу.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Результаты выполненных шагов: stepKey → published command payload.</summary>
        public Dictionary<string, string> StepResults { get; } = new();

        /// <summary>Ошибки шагов: stepKey → error message.</summary>
        public Dictionary<string, string> StepErrors { get; } = new();

        /// <summary>
        /// Получает переменную или значение по умолчанию.
        /// </summary>
        public string GetVariable(string key, string defaultValue = "")
        {
            return Variables.TryGetValue(key, out var value) ? value : defaultValue;
        }

        /// <summary>
        /// Устанавливает переменную.
        /// </summary>
        public void SetVariable(string key, string value)
        {
            Variables[key] = value;
        }

        /// <summary>
        /// Проверяет что шаг завершился успешно.
        /// </summary>
        public bool WasStepSuccessful(string stepKey)
        {
            return StepResults.ContainsKey(stepKey) && !StepErrors.ContainsKey(stepKey);
        }

        /// <summary>
        /// Очищает контекст для переиспользования.
        /// </summary>
        public void Clear()
        {
            Variables.Clear();
            StepResults.Clear();
            StepErrors.Clear();
            TraceId = "";
        }
    }
}
