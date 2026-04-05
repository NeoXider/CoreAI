namespace CoreAI.Ai
{
    /// <summary>
    /// Политика валидации ответов AINpc: мягкая проверка (JSON или непустой текст).
    /// </summary>
    public sealed class AINpcResponsePolicy : IRoleStructuredResponsePolicy
    {
        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            return roleId == BuiltInAgentRoleIds.AiNpc;
        }

        /// <inheritdoc />
        public bool TryValidate(string roleId, string rawContent, out string failureReason)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                failureReason = "Response is empty or whitespace.";
                return false;
            }

            // NPC может отвечать либо JSON, либо просто текстом (реплика)
            string trimmed = rawContent.Trim();
            if (trimmed.Length < 2)
            {
                failureReason = "NPC response too short (less than 2 characters).";
                return false;
            }

            failureReason = "";
            return true;
        }
    }
}