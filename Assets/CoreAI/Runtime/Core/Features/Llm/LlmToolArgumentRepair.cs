using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Repairs the arguments of one tool call before they are bound, so the shapes a small model gets wrong are
    /// accepted, or refused in one sentence, instead of reaching the binder: a scalar of the wrong JSON kind is
    /// coerced to the declared type ("True" or 1 for a boolean, "5" or 5.0 for an integer, 5 for a string); a key
    /// that differs from a declared name only in case, underscores, hyphens or spaces is renamed to it; a key that
    /// resembles a declared name the call left out (<c>start_line</c> next to an absent <c>from_line</c>) refuses
    /// the call with that name, because running it would answer something the model did not ask; any other
    /// unknown key is left for the binder to ignore and reported in <see cref="Outcome.Ignored"/>.
    /// Shared by the direct tool path (<c>ToolExecutionPolicy</c>) and the skill proxy (<c>call_skill_tool</c>).
    /// </summary>
    internal static class LlmToolArgumentRepair
    {
        /// <summary>Longest parameter description a refusal quotes.</summary>
        private const int MaxQuotedDescriptionChars = 100;

        /// <summary>Longest argument value a refusal quotes.</summary>
        private const int MaxQuotedValueChars = 40;

        private static readonly ConditionalWeakTable<AIFunction, Signature> Signatures = new();

        /// <summary>What <see cref="Repair"/> found.</summary>
        internal sealed class Outcome
        {
            /// <summary>The refusal sentence (no "Error:" prefix) when the call must not run; null otherwise.</summary>
            public string Refusal { get; set; }

            /// <summary>Keys the call sent that the tool does not declare and that resemble none of its names.</summary>
            public List<string> Ignored { get; } = new();
        }

        private sealed class Declared
        {
            public string Name;
            public Type ClrType;
            public string SchemaType;
            public string Description;
        }

        private sealed class Signature
        {
            public readonly List<Declared> Parameters = new();

            /// <summary>
            /// True when <see cref="Parameters"/> is exactly what the function binds (a reflection-built function
            /// without a catch-all parameter), so a key outside it is known to have no effect.
            /// </summary>
            public bool IsExact;

            public Declared Find(string name)
            {
                foreach (Declared parameter in Parameters)
                {
                    if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
                    {
                        return parameter;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Repairs <paramref name="arguments"/> in place for <paramref name="function"/>. A function without
        /// declared parameters is left alone: it has nothing a key could be mistaken for.
        /// </summary>
        internal static Outcome Repair(AIFunction function, IDictionary<string, object> arguments)
        {
            Outcome outcome = new();
            if (function == null || arguments == null || arguments.Count == 0)
            {
                return outcome;
            }

            Signature signature = Signatures.GetValue(function, Describe);
            if (signature.Parameters.Count == 0)
            {
                return outcome;
            }

            List<string> keys = new(arguments.Keys);
            foreach (string key in keys)
            {
                // WHY: "__"-prefixed keys are pipeline markers (ToolCallArgumentMarkers), never model arguments.
                if (key == null || key.StartsWith("__", StringComparison.Ordinal) || signature.Find(key) != null)
                {
                    continue;
                }

                Declared variant = FindVariant(signature, key);
                if (variant != null && !arguments.ContainsKey(variant.Name))
                {
                    arguments[variant.Name] = arguments[key];
                    arguments.Remove(key);
                    continue;
                }

                if (!signature.IsExact)
                {
                    continue;
                }

                List<string> near = FindNear(signature, key, arguments);
                if (near.Count > 0)
                {
                    outcome.Refusal = $"Tool '{function.Name}' was not run: it has no argument '{key}'. Did you mean " +
                                      Quoted(near, " or ") + "? Send the call again with that name. Its arguments: " +
                                      Names(signature) + ".";
                    return outcome;
                }

                outcome.Ignored.Add(key);
            }

            foreach (Declared parameter in signature.Parameters)
            {
                if (arguments.TryGetValue(parameter.Name, out object value) &&
                    TryCoerce(value, TargetType(parameter), out object coerced))
                {
                    arguments[parameter.Name] = coerced;
                }
            }

            return outcome;
        }

        /// <summary>The note a result carries for keys the tool ignored, e.g. for a model that guessed a name.</summary>
        internal static string DescribeIgnored(string toolName, IReadOnlyList<string> ignored)
        {
            return ignored.Count == 1
                ? $"argument '{ignored[0]}' is not one of {toolName}'s arguments and had no effect."
                : $"arguments {Quoted(ignored, ", ")} are not among {toolName}'s arguments and had no effect.";
        }

        /// <summary>
        /// The refusal for required arguments a call left out: one sentence naming each with its type and
        /// description from <paramref name="parametersSchema"/> when it has them.
        /// </summary>
        internal static string DescribeMissing(string toolName, string parametersSchema, IReadOnlyList<string> missing)
        {
            JObject properties = ReadProperties(parametersSchema);
            StringBuilder text = new();
            text.Append("Tool '").Append(toolName).Append("' is missing required argument(s): ")
                .Append(string.Join(", ", missing)).Append(". It was not run; send the call again with ");
            for (int i = 0; i < missing.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(i == missing.Count - 1 ? " and " : ", ");
                }

                text.Append(missing[i]);
                JObject property = properties?[missing[i]] as JObject;
                string kind = SchemaTypeOf(property);
                string description = Clip(property?["description"]?.Type == JTokenType.String
                    ? property["description"].Value<string>()
                    : null, MaxQuotedDescriptionChars);
                if (kind != null || !string.IsNullOrEmpty(description))
                {
                    text.Append(" (").Append(kind ?? "")
                        .Append(kind != null && !string.IsNullOrEmpty(description) ? ": " : "")
                        .Append(description ?? "").Append(')');
                }
            }

            return text.Append(" set.").ToString();
        }

        private static JObject ReadProperties(string parametersSchema)
        {
            if (string.IsNullOrWhiteSpace(parametersSchema))
            {
                return null;
            }

            try
            {
                return JObject.Parse(parametersSchema)["properties"] as JObject;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// What a value of <paramref name="type"/> must be, in words a model can act on: "true or false", "a whole
        /// number", "one of: Red, Green".
        /// </summary>
        internal static string DescribeExpected(Type type)
        {
            Type target = Nullable.GetUnderlyingType(type) ?? type;
            if (target.IsEnum)
            {
                return "one of: " + string.Join(", ", Enum.GetNames(target));
            }

            string kind = KindName(target);
            switch (kind)
            {
                case "boolean":
                    return "true or false";
                case "integer":
                    return "a whole number";
                case "number":
                    return "a number";
                case "string":
                    return "a string";
            }

            if (target == typeof(Guid))
            {
                return "a GUID";
            }

            if (target == typeof(DateTime) || target == typeof(DateTimeOffset))
            {
                return "an ISO 8601 date-time";
            }

            return "a JSON value of type " + target.Name;
        }

        /// <summary><paramref name="value"/> as a refusal quotes it: strings in quotes, clipped.</summary>
        internal static string ShowValue(object value)
        {
            object plain = Unwrap(value);
            string shown = plain switch
            {
                null => "null",
                string text => "\"" + text + "\"",
                bool flag => flag ? "true" : "false",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                _ => plain.ToString()
            };
            return Clip(shown, MaxQuotedValueChars);
        }

        /// <summary>
        /// "a (string, required), b (integer)" from a JSON parameter schema, or empty when it declares none or
        /// cannot be read.
        /// </summary>
        internal static string ParameterList(string parametersSchema)
        {
            if (string.IsNullOrWhiteSpace(parametersSchema))
            {
                return "";
            }

            JObject schema;
            try
            {
                schema = JObject.Parse(parametersSchema);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return "";
            }

            if (!(schema["properties"] is JObject properties) || properties.Count == 0)
            {
                return "";
            }

            List<string> required = LlmToolRequiredArguments.Read(schema);
            List<string> items = new();
            foreach (JProperty property in properties.Properties())
            {
                string type = SchemaTypeOf(property.Value as JObject);
                string flags = (type ?? "") + (required.Contains(property.Name) ? (type == null ? "" : ", ") + "required" : "");
                items.Add(flags.Length == 0 ? property.Name : property.Name + " (" + flags + ")");
            }

            return string.Join(", ", items);
        }

        private static Signature Describe(AIFunction function)
        {
            Signature signature = new();
            Dictionary<string, (string Type, string Description)> fromSchema = ReadSchema(function.JsonSchema);
            MethodInfo method = function.UnderlyingMethod;
            if (method == null)
            {
                foreach (KeyValuePair<string, (string Type, string Description)> entry in fromSchema)
                {
                    signature.Parameters.Add(new Declared
                    {
                        Name = entry.Key,
                        SchemaType = entry.Value.Type,
                        Description = entry.Value.Description
                    });
                }

                return signature;
            }

            signature.IsExact = true;
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Type type = parameter.ParameterType;
                if (type == typeof(AIFunctionArguments))
                {
                    // WHY: this parameter receives every key, so none of them is unknown.
                    signature.IsExact = false;
                    continue;
                }

                if (string.IsNullOrEmpty(parameter.Name) || type == typeof(CancellationToken) ||
                    type == typeof(IServiceProvider))
                {
                    continue;
                }

                fromSchema.TryGetValue(parameter.Name, out (string Type, string Description) described);
                signature.Parameters.Add(new Declared
                {
                    Name = parameter.Name,
                    ClrType = type,
                    SchemaType = described.Type,
                    Description = described.Description
                });
            }

            return signature;
        }

        private static Dictionary<string, (string Type, string Description)> ReadSchema(JsonElement schema)
        {
            Dictionary<string, (string Type, string Description)> result = new(StringComparer.Ordinal);
            if (schema.ValueKind != JsonValueKind.Object ||
                !schema.TryGetProperty("properties", out JsonElement properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (JsonProperty property in properties.EnumerateObject())
            {
                string type = null;
                string description = null;
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    if (property.Value.TryGetProperty("type", out JsonElement typeElement))
                    {
                        type = FirstNonNullType(typeElement);
                    }

                    if (property.Value.TryGetProperty("description", out JsonElement descriptionElement) &&
                        descriptionElement.ValueKind == JsonValueKind.String)
                    {
                        description = descriptionElement.GetString();
                    }
                }

                result[property.Name] = (type, description);
            }

            return result;
        }

        private static string FirstNonNullType(JsonElement type)
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                return type.GetString();
            }

            if (type.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in type.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() != "null")
                    {
                        return item.GetString();
                    }
                }
            }

            return null;
        }

        private static string SchemaTypeOf(JObject property)
        {
            JToken type = property?["type"];
            if (type is JValue value && value.Type == JTokenType.String)
            {
                return value.Value<string>();
            }

            if (type is JArray array)
            {
                foreach (JToken item in array)
                {
                    if (item.Type == JTokenType.String && item.Value<string>() != "null")
                    {
                        return item.Value<string>();
                    }
                }
            }

            return null;
        }

        private static Type TargetType(Declared parameter)
        {
            if (parameter.ClrType != null)
            {
                return Nullable.GetUnderlyingType(parameter.ClrType) ?? parameter.ClrType;
            }

            switch (parameter.SchemaType)
            {
                case "boolean":
                    return typeof(bool);
                case "integer":
                    return typeof(long);
                case "number":
                    return typeof(double);
                case "string":
                    return typeof(string);
                default:
                    return null;
            }
        }

        private static string KindName(Type type)
        {
            if (type == null)
            {
                return null;
            }

            if (type == typeof(bool))
            {
                return "boolean";
            }

            if (IsIntegral(type))
            {
                return "integer";
            }

            if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            {
                return "number";
            }

            return type == typeof(string) ? "string" : null;
        }

        private static bool IsIntegral(Type type)
        {
            return type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
                   type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort);
        }

        /// <summary>
        /// The value <paramref name="target"/> needs, when <paramref name="value"/> reads unambiguously as one;
        /// false leaves the value to the binder, whose refusal then names the argument.
        /// </summary>
        private static bool TryCoerce(object value, Type target, out object coerced)
        {
            coerced = null;
            object plain = Unwrap(value);
            if (target == null || plain == null || target.IsEnum || target == typeof(object))
            {
                return false;
            }

            if (target.IsInstanceOfType(plain))
            {
                // WHY: a wrapped scalar (a Newtonsoft token from the skill path) binds only through the
                // binder's JSON round trip; the plain value binds directly.
                coerced = plain;
                return !ReferenceEquals(plain, value);
            }

            if (target == typeof(bool))
            {
                bool? flag = ReadBoolean(plain);
                coerced = flag;
                return flag.HasValue;
            }

            if (IsIntegral(target))
            {
                return TryReadIntegral(plain, target, out coerced);
            }

            if (target == typeof(double) || target == typeof(float) || target == typeof(decimal))
            {
                double? number = plain switch
                {
                    string text when double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double parsed) => parsed,
                    long whole => whole,
                    int whole => whole,
                    double real => real,
                    float real => real,
                    decimal real => (double)real,
                    _ => null
                };
                if (!number.HasValue || double.IsNaN(number.Value) || double.IsInfinity(number.Value))
                {
                    return false;
                }

                return TryChangeType(number.Value, target, out coerced);
            }

            if (target == typeof(string))
            {
                coerced = plain switch
                {
                    bool flag => flag ? "true" : "false",
                    double real => real.ToString("R", CultureInfo.InvariantCulture),
                    float real => real.ToString("R", CultureInfo.InvariantCulture),
                    IFormattable number when IsNumber(plain) => number.ToString(null, CultureInfo.InvariantCulture),
                    _ => null
                };
                return coerced != null;
            }

            return false;
        }

        private static bool? ReadBoolean(object value)
        {
            switch (value)
            {
                case string text:
                    // WHY not "yes"/"no": a word is a guess at the meaning, so it stays a refusal the model corrects.
                    switch (text.Trim().ToLowerInvariant())
                    {
                        case "true":
                        case "1":
                            return true;
                        case "false":
                        case "0":
                            return false;
                        default:
                            return null;
                    }
                case long whole:
                    return whole == 1 ? true : whole == 0 ? false : (bool?)null;
                case int whole:
                    return whole == 1 ? true : whole == 0 ? false : (bool?)null;
                case double real:
                    return real == 1d ? true : real == 0d ? false : (bool?)null;
                default:
                    return null;
            }
        }

        private static bool TryReadIntegral(object value, Type target, out object coerced)
        {
            coerced = null;
            switch (value)
            {
                case string text:
                    string trimmed = text.Trim();
                    if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                    {
                        return TryChangeType(whole, target, out coerced);
                    }

                    return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture,
                               out double parsed) &&
                           TryIntegralFromReal(parsed, target, out coerced);
                case double real:
                    return TryIntegralFromReal(real, target, out coerced);
                case float single:
                    return TryIntegralFromReal(single, target, out coerced);
                case decimal exact:
                    return decimal.Truncate(exact) == exact && TryChangeType(exact, target, out coerced);
                case long _:
                case int _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                    return TryChangeType(value, target, out coerced);
                default:
                    return false;
            }
        }

        private static bool TryIntegralFromReal(double real, Type target, out object coerced)
        {
            coerced = null;
            return !double.IsNaN(real) && !double.IsInfinity(real) && Math.Floor(real) == real &&
                   Math.Abs(real) < 9.0e15 && TryChangeType((long)real, target, out coerced);
        }

        private static bool TryChangeType(object value, Type target, out object coerced)
        {
            try
            {
                coerced = Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is OverflowException || ex is InvalidCastException || ex is FormatException)
            {
                coerced = null;
                return false;
            }
        }

        private static bool IsNumber(object value)
        {
            return value is long || value is int || value is short || value is byte || value is uint ||
                   value is ulong || value is decimal;
        }

        /// <summary>A Newtonsoft or System.Text.Json scalar as its CLR value; anything else as it is.</summary>
        private static object Unwrap(object value)
        {
            switch (value)
            {
                case JValue token:
                    return token.Value;
                case JsonElement element:
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.String:
                            return element.GetString();
                        case JsonValueKind.Number:
                            return element.TryGetInt64(out long whole) ? whole : (object)element.GetDouble();
                        case JsonValueKind.True:
                            return true;
                        case JsonValueKind.False:
                            return false;
                        case JsonValueKind.Null:
                        case JsonValueKind.Undefined:
                            return null;
                        default:
                            return value;
                    }
                default:
                    return value;
            }
        }

        private static Declared FindVariant(Signature signature, string key)
        {
            string wanted = Fold(key);
            Declared found = null;
            foreach (Declared parameter in signature.Parameters)
            {
                if (Fold(parameter.Name) != wanted)
                {
                    continue;
                }

                if (found != null)
                {
                    return null;
                }

                found = parameter;
            }

            return found;
        }

        /// <summary>
        /// Declared names the call left out that <paramref name="key"/> resembles: a short edit distance, one
        /// containing the other, or a shared word of three letters or more (<c>start_line</c> and <c>from_line</c>).
        /// </summary>
        private static List<string> FindNear(Signature signature, string key, IDictionary<string, object> arguments)
        {
            string folded = Fold(key);
            HashSet<string> words = Words(key);
            List<string> near = new();
            foreach (Declared parameter in signature.Parameters)
            {
                if (arguments.ContainsKey(parameter.Name))
                {
                    continue;
                }

                string candidate = Fold(parameter.Name);
                int shorter = Math.Min(folded.Length, candidate.Length);
                bool resembles = Distance(folded, candidate) <= (shorter <= 4 ? 1 : shorter <= 8 ? 2 : 3) ||
                                 shorter >= 3 && (candidate.Contains(folded) || folded.Contains(candidate)) ||
                                 words.Overlaps(Words(parameter.Name));
                if (resembles)
                {
                    near.Add(parameter.Name);
                }
            }

            return near;
        }

        private static string Fold(string name)
        {
            StringBuilder folded = new(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    folded.Append(char.ToLowerInvariant(c));
                }
            }

            return folded.ToString();
        }

        private static HashSet<string> Words(string name)
        {
            HashSet<string> words = new(StringComparer.Ordinal);
            StringBuilder word = new();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool boundary = !char.IsLetterOrDigit(c) ||
                                i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]);
                if (boundary && word.Length > 0)
                {
                    AddWord(words, word);
                }

                if (char.IsLetterOrDigit(c))
                {
                    word.Append(char.ToLowerInvariant(c));
                }
            }

            AddWord(words, word);
            return words;
        }

        private static void AddWord(HashSet<string> words, StringBuilder word)
        {
            if (word.Length >= 3)
            {
                words.Add(word.ToString());
            }

            word.Clear();
        }

        private static int Distance(string a, string b)
        {
            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }

        private static string Names(Signature signature)
        {
            List<string> names = new(signature.Parameters.Count);
            foreach (Declared parameter in signature.Parameters)
            {
                names.Add(parameter.Name);
            }

            return string.Join(", ", names);
        }

        private static string Quoted(IReadOnlyList<string> names, string separator)
        {
            StringBuilder text = new();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(separator);
                }

                text.Append('\'').Append(names[i]).Append('\'');
            }

            return text.ToString();
        }

        private static string Clip(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }

            return text.Substring(0, maxChars - 1).TrimEnd() + "…";
        }
    }
}
