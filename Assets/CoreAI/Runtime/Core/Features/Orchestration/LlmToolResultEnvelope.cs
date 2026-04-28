using Newtonsoft.Json;

namespace CoreAI.Ai
{
    /// <summary>
    /// Semi-structured tool result envelope that can be returned to an agent and asserted in tests.
    /// </summary>
    public sealed class LlmToolResultEnvelope
    {
        /// <summary>Source tool name.</summary>
        public string ToolName { get; set; } = "";

        /// <summary>Action hint such as feedback_only, advance_allowed, retry_required, or stop_chaining.</summary>
        public string Action { get; set; } = "";

        /// <summary>Whether the tool-side task succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Optional score or normalized success ratio.</summary>
        public float? Score { get; set; }

        /// <summary>Compact human-readable summary.</summary>
        public string Summary { get; set; } = "";

        /// <summary>Additional domain payload as JSON.</summary>
        public string PayloadJson { get; set; } = "";

        /// <summary>Serializes the envelope to JSON.</summary>
        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }

        /// <summary>Parses a JSON envelope.</summary>
        public static bool TryParse(string json, out LlmToolResultEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                envelope = JsonConvert.DeserializeObject<LlmToolResultEnvelope>(json);
                return envelope != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
