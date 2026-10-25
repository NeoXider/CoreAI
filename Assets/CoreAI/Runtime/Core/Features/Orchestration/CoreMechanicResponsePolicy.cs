namespace CoreAI.Ai
{
    using System.Text.Json;

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
            if (!StructuredResponseJson.TryParseObject(rawContent, out JsonDocument document, out failureReason))
            {
                return false;
            }
            using (document)
            {
                if (!StructuredResponseJson.ContainsProperty(document.RootElement, property => property.Value.ValueKind == JsonValueKind.Number))
                {
                    failureReason = "JSON must contain at least one numeric field for game mechanics. No numbers found.";
                    return false;
                }
            }

            failureReason = "";
            return true;
        }
    }
}
