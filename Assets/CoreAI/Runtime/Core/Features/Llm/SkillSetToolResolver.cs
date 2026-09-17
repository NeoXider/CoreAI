using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    internal interface ISkillSetMetaLlmTool : ILlmTool
    {
        bool ContainsSkillTool(string toolName);
        ILlmTool RestrictTo(IReadOnlyCollection<string> allowedToolNames);
    }

    internal sealed class SkillToolDescriptor
    {
        public SkillToolDescriptor(
            SkillSet skill,
            ILlmTool sourceTool,
            string name,
            string description,
            string parametersSchema,
            IJsonInvocableLlmTool jsonTool,
            AIFunction function)
        {
            Skill = skill;
            SourceTool = sourceTool;
            Name = name;
            Description = description;
            ParametersSchema = parametersSchema;
            JsonTool = jsonTool;
            Function = function;
        }

        public SkillSet Skill { get; }
        public ILlmTool SourceTool { get; }
        public string Name { get; }
        public string Description { get; }
        public string ParametersSchema { get; }
        public IJsonInvocableLlmTool JsonTool { get; }
        public AIFunction Function { get; }
        public bool CanInvoke => JsonTool != null || Function != null;
    }

    internal static class SkillSetToolResolver
    {
        internal static IReadOnlyCollection<string> IntersectAllowlist(IReadOnlyCollection<string> current,
            IReadOnlyCollection<string> requested)
        {
            if (current == null)
            {
                return requested == null ? null : new List<string>(requested).AsReadOnly();
            }
            if (requested == null)
            {
                return new List<string>(current).AsReadOnly();
            }
            HashSet<string> allowed = new(current, StringComparer.OrdinalIgnoreCase);
            List<string> result = new();
            foreach (string name in requested)
            {
                if (name != null && allowed.Contains(name.Trim()))
                {
                    result.Add(name.Trim());
                }
            }
            return result.AsReadOnly();
        }

        internal static void ValidateCatalog(IReadOnlyList<SkillSet> skills)
        {
            HashSet<string> skillNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (SkillSet skill in skills)
            {
                if (skill != null && !skillNames.Add(skill.Name))
                {
                    throw new ArgumentException($"Duplicate skill name '{skill.Name}'.", nameof(skills));
                }
            }
            Dictionary<string, ILlmTool> bindings = new(StringComparer.OrdinalIgnoreCase);
            foreach (SkillToolDescriptor descriptor in BuildDescriptors(skills))
            {
                if (bindings.TryGetValue(descriptor.Name, out ILlmTool existing) &&
                    !ReferenceEquals(existing, descriptor.SourceTool))
                {
                    throw new ArgumentException($"Skill tool name '{descriptor.Name}' has conflicting bindings.", nameof(skills));
                }
                bindings[descriptor.Name] = descriptor.SourceTool;
            }
        }

        public static IReadOnlyList<SkillToolDescriptor> BuildDescriptors(IReadOnlyList<SkillSet> skills)
        {
            List<SkillToolDescriptor> descriptors = new();
            if (skills == null)
            {
                return descriptors;
            }

            foreach (SkillSet skill in skills)
            {
                if (skill?.Tools == null)
                {
                    continue;
                }

                foreach (ILlmTool tool in skill.Tools)
                {
                    AddDescriptors(skill, tool, descriptors);
                }
            }

            return descriptors;
        }

        public static IReadOnlyList<SkillToolDescriptor> BuildDescriptors(SkillSet skill)
        {
            return BuildDescriptors(skill == null ? null : new[] { skill });
        }

        /// <summary>
        /// Describes bare tools that belong to no skill, so they can be invoked by the same
        /// name-to-binding machinery. <see cref="SkillToolDescriptor.Skill"/> is null for them —
        /// the only consumer of that field is a diagnostic message, which reads it defensively.
        /// </summary>
        public static IReadOnlyList<SkillToolDescriptor> BuildToolDescriptors(IEnumerable<ILlmTool> tools)
        {
            List<SkillToolDescriptor> descriptors = new();
            if (tools == null)
            {
                return descriptors;
            }

            foreach (ILlmTool tool in tools)
            {
                AddDescriptors(null, tool, descriptors);
            }

            return descriptors;
        }

        public static string[] BuildToolNames(IEnumerable<ILlmTool> tools)
        {
            if (tools == null)
            {
                return Array.Empty<string>();
            }

            List<string> names = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ILlmTool tool in tools)
            {
                foreach (string name in GetCallableToolNames(tool))
                {
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names.ToArray();
        }

        public static AIFunctionArguments CreateArguments(string json)
        {
            return CreateArguments(string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json));
        }

        /// <summary>
        /// Same normalization from an already-parsed object. A resolved skill call keeps its parsed
        /// arguments and builds from them every time it needs a fresh argument set: the duplicate-call
        /// signature, the MEAI invocation - formerly each of those re-parsed the JSON string.
        /// </summary>
        public static AIFunctionArguments CreateArguments(JObject args)
        {
            Dictionary<string, object> normalized = new(StringComparer.Ordinal);
            if (args == null)
            {
                return new AIFunctionArguments(normalized);
            }

            foreach (KeyValuePair<string, JToken> prop in args)
            {
                // WHY: shared chokepoint (LlmToolArgumentNormalizer) — same rule as the
                // native policy and the text-extracted path.
                normalized[prop.Key] = LlmToolArgumentNormalizer.NormalizeValue(prop.Value);
            }

            return new AIFunctionArguments(normalized);
        }

        public static string SerializeResult(object result)
        {
            if (result == null)
            {
                return JsonConvert.SerializeObject(new { success = true });
            }

            if (result is string text)
            {
                return string.IsNullOrWhiteSpace(text)
                    ? JsonConvert.SerializeObject(new { success = true })
                    : text;
            }

            if (result is JsonDocument document)
            {
                return SerializeJsonElement(document.RootElement);
            }

            if (result is JsonElement element)
            {
                return SerializeJsonElement(element);
            }

            return JsonConvert.SerializeObject(result);
        }

        public static string SerializeFailure(string message, IEnumerable<string> available = null)
        {
            return JsonConvert.SerializeObject(new
            {
                success = false,
                error = message,
                available = available == null ? null : new List<string>(available)
            });
        }

        /// <summary>
        /// Checks a skill tool call against the tool's JSON schema and returns an actionable error when
        /// a required parameter is absent (or JSON <c>null</c>), otherwise <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The text names the tool, the missing parameters, the keys the model sent that the tool does
        /// not know (usually the misspelled name of the missing one) and every expected parameter with
        /// its type, so a single retry can succeed. An empty string is a value, not a missing one: tools
        /// legitimately give "" a meaning (e.g. "the current item"). Unknown keys alone are not an error -
        /// the binder ignores them, and rejecting them would break calls that work today. A schema that
        /// cannot be read disables the check rather than blocking the call.
        /// </remarks>
        public static string DescribeMissingRequiredArguments(string toolName, string parametersSchema, JObject arguments)
        {
            if (string.IsNullOrWhiteSpace(parametersSchema))
            {
                return null;
            }

            JObject schema;
            try
            {
                schema = JObject.Parse(parametersSchema);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }

            List<string> requiredNames = LlmToolRequiredArguments.Read(schema);
            List<string> missing = LlmToolRequiredArguments.FindMissing(requiredNames, arguments);
            if (missing.Count == 0)
            {
                return null;
            }

            JObject properties = schema["properties"] as JObject;
            List<string> expected = new();
            if (properties != null)
            {
                foreach (JProperty property in properties.Properties())
                {
                    JToken typeToken = (property.Value as JObject)?["type"];
                    string type = typeToken switch
                    {
                        JValue value => value.ToString(),
                        JArray union => string.Join("|", union),
                        _ => "any"
                    };
                    string requirement = requiredNames.Contains(property.Name) ? "required" : "optional";
                    expected.Add($"{property.Name} ({type}, {requirement})");
                }
            }

            List<string> unknown = new();
            if (arguments != null && properties != null)
            {
                foreach (JProperty property in arguments.Properties())
                {
                    if (properties[property.Name] == null)
                    {
                        unknown.Add(property.Name);
                    }
                }
            }

            string message = $"Tool '{toolName}' is missing required argument(s): {string.Join(", ", missing)}.";
            if (unknown.Count > 0)
            {
                message += $" Unknown argument(s) ignored: {string.Join(", ", unknown)}.";
            }

            if (expected.Count > 0)
            {
                message += $" Expected parameters: {string.Join(", ", expected)}.";
            }

            return message +
                   $" The tool was NOT executed. Retry call_skill_tool with tool_name=\"{toolName}\" and an " +
                   "arguments_json object that uses exactly these parameter names.";
        }

        private static IEnumerable<string> GetCallableToolNames(ILlmTool tool)
        {
            if (tool == null)
            {
                yield break;
            }

            if (tool is IAIFunctionsLlmTool functionTools)
            {
                bool any = false;
                foreach (AIFunction function in SafeCreateFunctions(tool, functionTools))
                {
                    if (function != null && !string.IsNullOrWhiteSpace(function.Name))
                    {
                        any = true;
                        yield return function.Name;
                    }
                }

                if (!any)
                {
                    yield return tool.Name;
                }

                yield break;
            }

            if (tool is IAIFunctionLlmTool functionTool)
            {
                AIFunction function = SafeCreateFunction(tool, functionTool);
                yield return !string.IsNullOrWhiteSpace(function?.Name) ? function.Name : tool.Name;
                yield break;
            }

            yield return tool.Name;
        }

        private static void AddDescriptors(SkillSet skill, ILlmTool tool, List<SkillToolDescriptor> descriptors)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
            {
                return;
            }

            if (tool is IAIFunctionsLlmTool functionTools)
            {
                bool added = false;
                foreach (AIFunction function in SafeCreateFunctions(tool, functionTools))
                {
                    if (function == null || string.IsNullOrWhiteSpace(function.Name))
                    {
                        continue;
                    }

                    descriptors.Add(new SkillToolDescriptor(
                        skill,
                        tool,
                        function.Name,
                        string.IsNullOrWhiteSpace(function.Description) ? tool.Description : function.Description,
                        SafeSchema(function, tool.ParametersSchema),
                        null,
                        function));
                    added = true;
                }

                if (!added)
                {
                    descriptors.Add(UninvocableDescriptor(skill, tool));
                }

                return;
            }

            if (tool is IAIFunctionLlmTool functionTool)
            {
                AIFunction function = SafeCreateFunction(tool, functionTool);
                if (function != null && !string.IsNullOrWhiteSpace(function.Name))
                {
                    descriptors.Add(new SkillToolDescriptor(
                        skill,
                        tool,
                        function.Name,
                        string.IsNullOrWhiteSpace(function.Description) ? tool.Description : function.Description,
                        SafeSchema(function, tool.ParametersSchema),
                        tool as IJsonInvocableLlmTool,
                        function));
                }
                else
                {
                    descriptors.Add(UninvocableDescriptor(skill, tool));
                }

                return;
            }

            if (tool is IJsonInvocableLlmTool jsonTool)
            {
                descriptors.Add(new SkillToolDescriptor(
                    skill,
                    tool,
                    tool.Name,
                    tool.Description,
                    tool.ParametersSchema,
                    jsonTool,
                    null));
                return;
            }

            descriptors.Add(UninvocableDescriptor(skill, tool));
        }

        private static SkillToolDescriptor UninvocableDescriptor(SkillSet skill, ILlmTool tool)
        {
            return new SkillToolDescriptor(
                skill,
                tool,
                tool.Name,
                tool.Description,
                tool.ParametersSchema,
                null,
                null);
        }

        private static AIFunction SafeCreateFunction(ILlmTool tool, IAIFunctionLlmTool functionTool)
        {
            try
            {
                return functionTool.CreateAIFunction();
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<AIFunction> SafeCreateFunctions(ILlmTool tool, IAIFunctionsLlmTool functionTools)
        {
            IEnumerable<AIFunction> functions;
            try
            {
                functions = functionTools.CreateAIFunctions();
            }
            catch
            {
                yield break;
            }

            if (functions == null)
            {
                yield break;
            }

            foreach (AIFunction function in functions)
            {
                yield return function;
            }
        }

        private static string SafeSchema(AIFunction function, string fallback)
        {
            try
            {
                if (function == null)
                {
                    return fallback ?? "{}";
                }

                string schema = function.JsonSchema.ToString();
                return string.IsNullOrWhiteSpace(schema) ? fallback ?? "{}" : schema;
            }
            catch
            {
                return fallback ?? "{}";
            }
        }

        private static string SerializeJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Null => "",
                JsonValueKind.Undefined => "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.GetRawText()
            };
        }
    }
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
