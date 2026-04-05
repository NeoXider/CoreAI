namespace CoreAI.Ai
{
    /// <summary>
    /// Политика валидации ответов Analyzer: требует JSON с метриками или рекомендациями.
    /// </summary>
    public sealed class AnalyzerResponsePolicy : IRoleStructuredResponsePolicy
    {
        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            return roleId == BuiltInAgentRoleIds.Analyzer;
        }

        /// <inheritdoc />
        public bool TryValidate(string roleId, string rawContent, out string failureReason)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                failureReason = "Response is empty or whitespace.";
                return false;
            }

            var trimmed = rawContent.Trim();

            // Извлекаем JSON из markdown если нужно
            if (trimmed.StartsWith("```json"))
            {
                var endFence = trimmed.IndexOf("```", 7);
                if (endFence > 0)
                {
                    trimmed = trimmed.Substring(7, endFence - 7).Trim();
                }
            }

            // Должен быть JSON объект
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                var jsonStart = trimmed.IndexOf('{');
                var jsonEnd = trimmed.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    trimmed = trimmed.Substring(jsonStart, jsonEnd - jsonStart + 1);
                }
                else
                {
                    failureReason = "Expected JSON object with metrics or recommendations. Got plain text.";
                    return false;
                }
            }

            // Проверяем наличие ключевых полей для Analyzer
            var lower = trimmed.ToLowerInvariant();
            var hasMetricKey = lower.Contains("\"metric") ||
                               lower.Contains("\"recommendation") || lower.Contains("\"suggestion") ||
                               lower.Contains("\"analysis") || lower.Contains("\"status") ||
                               lower.Contains("\"finding") || lower.Contains("\"issue");

            if (!hasMetricKey)
            {
                failureReason = "JSON should contain fields like 'metric', 'recommendation', 'analysis', or 'status'. None found.";
                return false;
            }

            failureReason = "";
            return true;
        }
    }
}
