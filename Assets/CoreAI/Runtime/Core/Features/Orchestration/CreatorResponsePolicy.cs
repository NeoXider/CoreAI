namespace CoreAI.Ai
{
    using System;
    using System.Text.Json;

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
            bool valid = StructuredResponseJson.TryParseObject(rawContent, out JsonDocument document, out failureReason);
            document?.Dispose();
            return valid;
        }
    }

    internal static class StructuredResponseJson
    {
        internal static bool TryParseObject(string rawContent, out JsonDocument document, out string failureReason)
        {
            document = null;
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                failureReason = "Response is empty or whitespace.";
                return false;
            }
            string content = rawContent.Trim();
            int objectStart = content.IndexOf('{');
            int fenceStart = content.IndexOf("```", StringComparison.Ordinal);
            if (fenceStart >= 0 && (objectStart < 0 || fenceStart < objectStart))
            {
                int lineEnd = content.IndexOf('\n', fenceStart);
                string header = lineEnd >= 0 ? content.Substring(fenceStart, lineEnd - fenceStart).Trim() : "";
                if ((header != "```" && !string.Equals(header, "```json", StringComparison.OrdinalIgnoreCase)) ||
                    lineEnd < 0 || !content.EndsWith("```", StringComparison.Ordinal))
                {
                    failureReason = "Expected JSON object in a complete JSON code fence.";
                    return false;
                }
                content = content.Substring(lineEnd + 1, content.Length - lineEnd - 4).Trim();
            }
            else if (objectStart > 0)
            {
                content = content.Substring(objectStart);
            }
            try
            {
                document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    failureReason = "";
                    return true;
                }
                document.Dispose();
                document = null;
            }
            catch (JsonException)
            {
                document?.Dispose();
                document = null;
            }
            failureReason = "Expected JSON object with valid syntax and no trailing content.";
            return false;
        }

        internal static bool ContainsProperty(JsonElement value, Func<JsonProperty, bool> predicate)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (predicate(property) || ContainsProperty(property.Value, predicate)) return true;
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (ContainsProperty(item, predicate)) return true;
                }
            }
            return false;
        }
    }
}
