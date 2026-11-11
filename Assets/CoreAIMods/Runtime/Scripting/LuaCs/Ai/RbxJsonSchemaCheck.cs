using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The <c>TextGenerator</c> <c>JsonSchema</c> contract: the schema must be JSON, and the model's answer must parse
    /// as JSON that conforms to it. A post-check, so it holds for every endpoint, including servers that ignore a
    /// requested response format. Covers the json-schema.org keywords structured output uses: <c>type</c> (one or a
    /// list), <c>properties</c>, <c>required</c>, <c>additionalProperties: false</c>, <c>items</c>, <c>enum</c>,
    /// <c>const</c>, <c>minimum</c>/<c>maximum</c>, <c>minLength</c>/<c>maxLength</c>, <c>minItems</c>/<c>maxItems</c>.
    /// Other keywords are accepted and not enforced.
    /// </summary>
    public static class RbxJsonSchemaCheck
    {
        private const int MaxDepth = 32;

        /// <summary>Parses <paramref name="schemaText"/>; false with the reason when it is not a JSON object.</summary>
        public static bool TryParseSchema(string schemaText, out JObject schema, out string reason)
        {
            schema = null;
            try
            {
                JToken token = JToken.Parse(schemaText ?? "");
                schema = token as JObject;
                reason = schema == null ? "the schema must be a JSON object" : "";
                return schema != null;
            }
            catch (JsonException ex)
            {
                reason = "the schema is not valid JSON (" + ex.Message + ")";
                return false;
            }
        }

        /// <summary>
        /// The JSON text of <paramref name="answer"/> when it parses and conforms to <paramref name="schema"/>; strips a
        /// code fence and surrounding prose the model added. False with the first violation otherwise.
        /// </summary>
        public static bool TryConform(string answer, JObject schema, out string json, out string reason)
        {
            json = "";
            if (!TryParseAnswer(answer, out JToken value))
            {
                reason = "the answer is not JSON";
                return false;
            }

            List<string> violations = new();
            Validate(value, schema, "$", violations, 0);
            if (violations.Count > 0)
            {
                reason = violations[0];
                return false;
            }

            json = value.ToString(Formatting.None);
            reason = "";
            return true;
        }

        private static bool TryParseAnswer(string answer, out JToken value)
        {
            value = null;
            string text = StripFence((answer ?? "").Trim());
            if (TryParse(text, out value))
            {
                return true;
            }

            int start = IndexOfAny(text, '{', '[');
            int end = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
            return start >= 0 && end > start && TryParse(text.Substring(start, end - start + 1), out value);
        }

        private static bool TryParse(string text, out JToken value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                value = JToken.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string StripFence(string text)
        {
            if (!text.StartsWith("```", StringComparison.Ordinal))
            {
                return text;
            }

            int firstLine = text.IndexOf('\n');
            int close = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine < 0 || close <= firstLine)
            {
                return text.Trim('`').Trim();
            }

            return text.Substring(firstLine + 1, close - firstLine - 1).Trim();
        }

        private static int IndexOfAny(string text, char a, char b)
        {
            int ia = text.IndexOf(a);
            int ib = text.IndexOf(b);
            if (ia < 0)
            {
                return ib;
            }

            return ib < 0 ? ia : Math.Min(ia, ib);
        }

        private static void Validate(JToken value, JObject schema, string path, List<string> violations, int depth)
        {
            if (schema == null || violations.Count > 0)
            {
                return;
            }

            if (depth > MaxDepth)
            {
                violations.Add(path + " is nested deeper than " + MaxDepth + " levels");
                return;
            }

            if (schema.TryGetValue("type", out JToken typeToken) && !MatchesType(value, typeToken))
            {
                violations.Add(path + " must be " + typeToken.ToString(Formatting.None) + ", got " + Describe(value));
                return;
            }

            if (schema.TryGetValue("enum", out JToken enumToken) && enumToken is JArray choices)
            {
                bool found = false;
                foreach (JToken choice in choices)
                {
                    found |= JToken.DeepEquals(choice, value);
                }

                if (!found)
                {
                    violations.Add(path + " must be one of " + choices.ToString(Formatting.None));
                    return;
                }
            }

            if (schema.TryGetValue("const", out JToken constant) && !JToken.DeepEquals(constant, value))
            {
                violations.Add(path + " must be " + constant.ToString(Formatting.None));
                return;
            }

            switch (value.Type)
            {
                case JTokenType.Object:
                    ValidateObject((JObject)value, schema, path, violations, depth);
                    break;
                case JTokenType.Array:
                    ValidateArray((JArray)value, schema, path, violations, depth);
                    break;
                case JTokenType.String:
                    ValidateLength(((string)value)?.Length ?? 0, schema, "minLength", "maxLength", path, "characters",
                        violations);
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    ValidateRange(value.Value<double>(), schema, path, violations);
                    break;
            }
        }

        private static void ValidateObject(JObject value, JObject schema, string path, List<string> violations, int depth)
        {
            JObject properties = schema["properties"] as JObject;
            if (schema["required"] is JArray required)
            {
                foreach (JToken name in required)
                {
                    string key = name.Type == JTokenType.String ? (string)name : name.ToString();
                    if (!value.ContainsKey(key))
                    {
                        violations.Add(path + " is missing the required property \"" + key + "\"");
                        return;
                    }
                }
            }

            bool closed = schema["additionalProperties"] is JValue additional && additional.Type == JTokenType.Boolean &&
                          !(bool)additional;
            foreach (JProperty property in value.Properties())
            {
                JObject child = properties?[property.Name] as JObject;
                if (child == null)
                {
                    if (closed)
                    {
                        violations.Add(path + " has the property \"" + property.Name + "\" the schema does not allow");
                        return;
                    }

                    continue;
                }

                Validate(property.Value, child, path + "." + property.Name, violations, depth + 1);
                if (violations.Count > 0)
                {
                    return;
                }
            }
        }

        private static void ValidateArray(JArray value, JObject schema, string path, List<string> violations, int depth)
        {
            ValidateLength(value.Count, schema, "minItems", "maxItems", path, "items", violations);
            if (violations.Count > 0 || !(schema["items"] is JObject items))
            {
                return;
            }

            for (int index = 0; index < value.Count; index++)
            {
                Validate(value[index], items, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                    violations, depth + 1);
                if (violations.Count > 0)
                {
                    return;
                }
            }
        }

        private static void ValidateLength(int length, JObject schema, string minKey, string maxKey, string path,
            string unit, List<string> violations)
        {
            if (schema[minKey] is JValue min && IsNumber(min) && length < min.Value<double>())
            {
                violations.Add(path + " has " + length + " " + unit + ", fewer than " + min);
            }
            else if (schema[maxKey] is JValue max && IsNumber(max) && length > max.Value<double>())
            {
                violations.Add(path + " has " + length + " " + unit + ", more than " + max);
            }
        }

        private static void ValidateRange(double number, JObject schema, string path, List<string> violations)
        {
            if (schema["minimum"] is JValue min && IsNumber(min) && number < min.Value<double>())
            {
                violations.Add(path + " is below the minimum " + min);
            }
            else if (schema["maximum"] is JValue max && IsNumber(max) && number > max.Value<double>())
            {
                violations.Add(path + " is above the maximum " + max);
            }
        }

        private static bool MatchesType(JToken value, JToken typeToken)
        {
            if (typeToken is JArray types)
            {
                foreach (JToken type in types)
                {
                    if (MatchesType(value, type))
                    {
                        return true;
                    }
                }

                return false;
            }

            switch (typeToken.Type == JTokenType.String ? (string)typeToken : "")
            {
                case "object": return value.Type == JTokenType.Object;
                case "array": return value.Type == JTokenType.Array;
                case "string": return value.Type == JTokenType.String;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "null": return value.Type == JTokenType.Null;
                case "integer":
                    return value.Type == JTokenType.Integer ||
                           (value.Type == JTokenType.Float && Math.Abs(value.Value<double>() % 1d) < double.Epsilon);
                case "number": return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
                default: return true;
            }
        }

        private static bool IsNumber(JValue value)
        {
            return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
        }

        private static string Describe(JToken value)
        {
            switch (value.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return "a number";
                case JTokenType.String:
                    return "a string";
                case JTokenType.Boolean:
                    return "a boolean";
                case JTokenType.Object:
                    return "an object";
                case JTokenType.Array:
                    return "an array";
                case JTokenType.Null:
                    return "null";
                default:
                    return value.Type.ToString();
            }
        }
    }
}
