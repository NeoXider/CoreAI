namespace CoreAI.Ai
{
    using System;
    using System.Text.Json;

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
            if (!StructuredResponseJson.TryParseObject(rawContent, out JsonDocument document, out failureReason))
            {
                return false;
            }
            using (document)
            {
                if (!StructuredResponseJson.ContainsProperty(document.RootElement, IsMetricProperty))
                {
                    failureReason = "JSON should contain fields like 'metric', 'recommendation', 'analysis', or 'status'. None found.";
                    return false;
                }
            }
            failureReason = "";
            return true;
        }

        private static bool IsMetricProperty(JsonProperty property)
        {
            string name = property.Name;
            return name.StartsWith("metric", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("recommendation", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("suggestion", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("analysis", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("status", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("finding", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("issue", StringComparison.OrdinalIgnoreCase);
        }
    }
}
