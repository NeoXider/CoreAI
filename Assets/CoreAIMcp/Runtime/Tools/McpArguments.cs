using System;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// Defensive readers for MCP <c>tools/call</c> arguments.
    /// <para>
    /// WHY: models routinely fill every declared optional parameter with an explicit JSON null
    /// (<c>{"action":"list","mod_id":null,"revision":null}</c>). A plain
    /// <c>arguments["revision"] != null ? arguments["revision"].Value&lt;int&gt;() : fallback</c> throws on
    /// that, because a JSON null token is a live <see cref="JToken"/> of type
    /// <see cref="JTokenType.Null"/> - not a C# null - and the whole call fails instead of using the
    /// default. Every reader here treats missing, null, and unconvertible tokens alike and returns the
    /// caller's fallback.
    /// </para>
    /// </summary>
    public static class McpArguments
    {
        /// <summary>Reads a string argument, or <paramref name="fallback"/> when absent or JSON null.</summary>
        public static string String(JObject arguments, string key, string fallback = null)
        {
            JToken token = Token(arguments, key);
            return token == null ? fallback : token.ToString();
        }

        /// <summary>Reads an int argument, or <paramref name="fallback"/> when absent, null, or not numeric.</summary>
        public static int Int(JObject arguments, string key, int fallback)
        {
            JToken token = Token(arguments, key);
            if (token == null)
            {
                return fallback;
            }

            try
            {
                return token.Value<int>();
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>Reads a long argument, or <paramref name="fallback"/> when absent, null, or not numeric.</summary>
        public static long Long(JObject arguments, string key, long fallback)
        {
            JToken token = Token(arguments, key);
            if (token == null)
            {
                return fallback;
            }

            try
            {
                return token.Value<long>();
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>Reads a float argument, or <paramref name="fallback"/> when absent, null, or not numeric.</summary>
        public static float Float(JObject arguments, string key, float fallback)
        {
            float? value = FloatOrNull(arguments, key);
            return value ?? fallback;
        }

        /// <summary>Reads an optional float argument; null means "not supplied" (leave the field alone).</summary>
        public static float? FloatOrNull(JObject arguments, string key)
        {
            JToken token = Token(arguments, key);
            if (token == null)
            {
                return null;
            }

            try
            {
                return token.Value<float>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Reads a bool argument, or <paramref name="fallback"/> when absent, null, or not a bool.</summary>
        public static bool Bool(JObject arguments, string key, bool fallback = false)
        {
            JToken token = Token(arguments, key);
            if (token == null)
            {
                return fallback;
            }

            try
            {
                return token.Value<bool>();
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static JToken Token(JObject arguments, string key)
        {
            if (arguments == null || string.IsNullOrEmpty(key))
            {
                return null;
            }

            JToken token = arguments[key];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            return token;
        }
    }
}
