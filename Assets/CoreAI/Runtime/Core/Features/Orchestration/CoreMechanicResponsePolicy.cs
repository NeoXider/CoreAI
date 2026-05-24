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

            /* Implementation note in English. */
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                /* Implementation note in English. */
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

            /* Implementation note in English. */
            /* Implementation note in English. */
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
