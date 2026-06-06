namespace CoreAI.Ai
{
    /// <summary>
    /// Structured-response policy for creator roles.
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

            string trimmed = rawContent.Trim();

            if (trimmed.StartsWith("```json"))
            {
                int endFence = trimmed.IndexOf("```", 7);
                if (endFence > 0)
                {
                    trimmed = trimmed.Substring(7, endFence - 7).Trim();
                }
            }

            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                int jsonStart = trimmed.IndexOf('{');
                int jsonEnd = trimmed.LastIndexOf('}');

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