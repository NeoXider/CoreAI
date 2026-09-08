using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Single chokepoint for Newtonsoft-to-CLR tool argument normalization. MEAI's
    /// <c>AIFunctionFactory</c> cannot bind <see cref="JObject"/>/<see cref="JArray"/>
    /// to string parameters, so nested JSON tokens become compact JSON strings before
    /// invocation. Shared by the native policy, the text-extracted path and the skill
    /// resolver so the rule cannot drift between call shapes.
    /// </summary>
    internal static class LlmToolArgumentNormalizer
    {
        /// <summary>
        /// Normalizes one argument value: JSON null/undefined becomes <c>null</c>,
        /// objects and arrays become their compact JSON string, scalar tokens unwrap
        /// to the CLR value. Any other value passes through untouched.
        /// </summary>
        internal static object NormalizeValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                return token.ToString(Formatting.None);
            }

            return token is JValue value ? value.Value : token.ToObject<object>();
        }

        /// <summary>
        /// In-place normalization of a deserialized argument dictionary: every
        /// <see cref="JObject"/>/<see cref="JArray"/> value becomes its compact JSON
        /// string so delegates expecting string parameters receive proper strings
        /// instead of raw Newtonsoft tokens. Other values are left as is.
        /// </summary>
        internal static void NormalizeDictionaryValues(IDictionary<string, object> arguments)
        {
            if (arguments == null)
            {
                return;
            }

            List<string> keys = new(arguments.Keys);
            foreach (string key in keys)
            {
                if (arguments[key] is JObject jo)
                {
                    arguments[key] = jo.ToString(Formatting.None);
                }
                else if (arguments[key] is JArray ja)
                {
                    arguments[key] = ja.ToString(Formatting.None);
                }
            }
        }

        /// <summary>
        /// Copies the argument dictionary and normalizes the copy, leaving the source
        /// untouched. Use on shared dictionaries (e.g. <c>FunctionCallContent.Arguments</c>)
        /// that must keep the raw tokens for tracing.
        /// </summary>
        internal static Dictionary<string, object> NormalizedCopy(IDictionary<string, object> arguments)
        {
            if (arguments == null)
            {
                return null;
            }

            Dictionary<string, object> copy = new(arguments.Count);
            foreach (KeyValuePair<string, object> pair in arguments)
            {
                copy[pair.Key] = pair.Value;
            }

            NormalizeDictionaryValues(copy);
            return copy;
        }
    }
}
