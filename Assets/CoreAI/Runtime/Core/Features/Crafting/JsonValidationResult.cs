using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Результат валидации JSON.
    /// </summary>
    public sealed class JsonValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();

        /// <summary>Парсированный JSON объект (если валидация успешна).</summary>
        public JObject ParsedObject { get; set; }

        /// <summary>Строка с описанием всех ошибок для отправки обратно LLM.</summary>
        public string ErrorSummary => Errors.Count > 0 ? string.Join("; ", Errors) : null;
    }
}
