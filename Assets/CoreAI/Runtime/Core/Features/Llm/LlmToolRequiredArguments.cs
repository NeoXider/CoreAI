using System;
using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// The one rule for "a required tool argument is missing", shared by the direct tool path
    /// (<c>ToolExecutionPolicy</c>) and the skill proxy (<c>call_skill_tool</c>).
    /// </summary>
    /// <remarks>
    /// Missing means: the key is absent, or its value is <c>null</c> / JSON <c>null</c> / undefined.
    /// An empty or whitespace-only string is a PRESENT value - tools legitimately give "" a meaning
    /// ("the current item"), and the two paths used to disagree on it. Required names come from the
    /// schema's top-level <c>required</c> array; an unreadable schema yields no required names, so the
    /// check never blocks a call it cannot reason about.
    /// </remarks>
    internal static class LlmToolRequiredArguments
    {
        public static List<string> Read(string parametersSchema)
        {
            if (string.IsNullOrWhiteSpace(parametersSchema))
            {
                return new List<string>();
            }

            try
            {
                return Read(JObject.Parse(parametersSchema));
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return new List<string>();
            }
        }

        public static List<string> Read(JObject schema)
        {
            List<string> result = new();
            if (!(schema?["required"] is JArray required))
            {
                return result;
            }

            foreach (JToken token in required)
            {
                string name = token.Type == JTokenType.String ? token.Value<string>()?.Trim() : null;
                if (!string.IsNullOrEmpty(name) && !result.Contains(name))
                {
                    result.Add(name);
                }
            }

            return result;
        }

        /// <summary>
        /// Same reading of the schema MEAI itself binds with (<c>AIFunction.JsonSchema</c>): a wrapper that
        /// expands into several functions publishes <c>{}</c> as its tool metadata, and only the function's
        /// own schema knows which parameters the binder will demand.
        /// </summary>
        public static List<string> Read(JsonElement schema)
        {
            List<string> result = new();
            if (schema.ValueKind != JsonValueKind.Object ||
                !schema.TryGetProperty("required", out JsonElement required) ||
                required.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement token in required.EnumerateArray())
            {
                string name = token.ValueKind == JsonValueKind.String ? token.GetString()?.Trim() : null;
                if (!string.IsNullOrEmpty(name) && !result.Contains(name))
                {
                    result.Add(name);
                }
            }

            return result;
        }

        public static bool IsMissing(object value)
        {
            switch (value)
            {
                case null:
                    return true;
                case JToken token:
                    return token.Type == JTokenType.Null || token.Type == JTokenType.Undefined;
                case JsonElement element:
                    return element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined;
                default:
                    return false;
            }
        }

        public static List<string> FindMissing(IReadOnlyList<string> required, JObject arguments)
        {
            List<string> missing = new();
            foreach (string name in required)
            {
                if (arguments == null || !arguments.TryGetValue(name, StringComparison.Ordinal, out JToken value) ||
                    IsMissing(value))
                {
                    missing.Add(name);
                }
            }

            return missing;
        }

        public static List<string> FindMissing(IReadOnlyList<string> required, IDictionary<string, object> arguments)
        {
            List<string> missing = new();
            foreach (string name in required)
            {
                if (arguments == null || !arguments.TryGetValue(name, out object value) || IsMissing(value))
                {
                    missing.Add(name);
                }
            }

            return missing;
        }
    }
}
