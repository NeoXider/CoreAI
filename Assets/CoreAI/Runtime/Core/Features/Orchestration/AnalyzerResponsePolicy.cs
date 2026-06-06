namespace CoreAI.Ai
{
    /// <summary>
    /// Structured-response policy for analyzer roles.
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
                    trimmed = trimmed.Substring(jsonStart, jsonEnd - jsonStart + 1);
                }
                else
                {
                    failureReason = "Expected JSON object with metrics or recommendations. Got plain text.";
                    return false;
                }
            }

            string lower = trimmed.ToLowerInvariant();
            bool hasMetricKey = lower.Contains("\"metric") ||
                                lower.Contains("\"recommendation") || lower.Contains("\"suggestion") ||
                                lower.Contains("\"analysis") || lower.Contains("\"status") ||
                                lower.Contains("\"finding") || lower.Contains("\"issue");

            if (!hasMetricKey)
            {
                failureReason =
                    "JSON should contain fields like 'metric', 'recommendation', 'analysis', or 'status'. None found.";
                return false;
            }

            failureReason = "";
            return true;
        }
    }
}