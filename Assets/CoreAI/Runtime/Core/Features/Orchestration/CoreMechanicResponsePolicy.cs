namespace CoreAI.Ai
{
    /// <summary>
    /// Structured-response policy for core mechanic roles.
    /// </summary>
    public sealed class CoreMechanicResponsePolicy : IRoleStructuredResponsePolicy
    {
        /// <inheritdoc />
        public bool ShouldValidate(string roleId)
        {
            return roleId == BuiltInAgentRoleIds.CoreMechanic;
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
                    failureReason = "Expected JSON object with game mechanics parameters. Got plain text.";
                    return false;
                }
            }

            bool hasNumeric = System.Text.RegularExpressions.Regex.IsMatch(
                trimmed,
                @"""[^""]+""\s*:\s*\d+\.?\d*");

            if (!hasNumeric)
            {
                failureReason = "JSON must contain at least one numeric field for game mechanics. No numbers found.";
                return false;
            }

            failureReason = "";
            return true;
        }
    }
}