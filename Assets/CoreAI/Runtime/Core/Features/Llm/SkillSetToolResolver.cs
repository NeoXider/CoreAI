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
            Dictionary<string, object> normalized = new(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AIFunctionArguments(normalized);
            }

            JObject args = JObject.Parse(json);
            foreach (KeyValuePair<string, JToken> prop in args)
            {
                normalized[prop.Key] = NormalizeToken(prop.Value);
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

        private static object NormalizeToken(JToken token)
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
}
