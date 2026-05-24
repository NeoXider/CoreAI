using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Result returned by JSON schema validation.
    /// </summary>
    public sealed class JsonValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();

        /// <summary>Parsed JSON object produced by validation.</summary>
        public JObject ParsedObject { get; set; }

        /// <summary>Error summary.</summary>
        public string ErrorSummary => Errors.Count > 0 ? string.Join("; ", Errors) : null;
    }
}
