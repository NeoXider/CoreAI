namespace CoreAI.Ai
{
    /// <summary>
    /// Политика валидации ответов Creator: требует JSON объект с командой.
    /// </summary>
    public sealed class CreatorResponsePolicy : IRoleStructuredResponsePolicy
    {
        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            return roleId == BuiltInAgentRoleIds.Creator;
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
                    failureReason = "";
                    return true;
                }

                failureReason = "Expected JSON command object. Got plain text instead.";
                return false;
            }

            failureReason = "";
            return true;
        }
    }
}
