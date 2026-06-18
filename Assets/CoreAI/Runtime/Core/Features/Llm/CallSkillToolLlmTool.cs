using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool that invokes a named runtime skill.
    /// </summary>
    internal static class CallSkillToolLlmTool
    {
        /// <summary>
        /// Creates the <c>call_skill_tool</c> tool.
        /// </summary>
        public static ILlmTool Create(IReadOnlyList<SkillSet> skills)
        {
            return Create(skills, null);
        }

        internal static ILlmTool Create(IReadOnlyList<SkillSet> skills, IReadOnlyCollection<string> allowedToolNames)
        {
            return new CallSkillToolProxy(skills, allowedToolNames);
        }

        private sealed class CallSkillToolProxy : LlmToolBase, IAIFunctionLlmTool, ISkillSetMetaLlmTool
        {
            private readonly IReadOnlyList<SkillSet> _skills;
            private readonly Dictionary<string, SkillToolDescriptor> _toolsByName;

            public CallSkillToolProxy(IReadOnlyList<SkillSet> skills, IReadOnlyCollection<string> allowedToolNames)
            {
                _skills = skills ?? throw new ArgumentNullException(nameof(skills));
                _toolsByName = BuildToolMap(_skills, allowedToolNames);
            }

            public override string Name => "call_skill_tool";

            public override string Description =>
                "Call a tool from a skill. First call read_skill to learn available tools and their parameters. " +
                "Then call this with tool_name and arguments_json (a JSON object string with the tool's parameters).";

            public override string ParametersSchema =>
                "{\"type\":\"object\",\"properties\":{\"tool_name\":{\"type\":\"string\",\"description\":\"Skill tool name returned by read_skill.\"},\"arguments_json\":{\"type\":\"string\",\"description\":\"JSON object string with the skill tool parameters.\"}},\"required\":[\"tool_name\",\"arguments_json\"]}";

            public override bool AllowDuplicates => true;

            public bool ContainsSkillTool(string toolName)
            {
                return !string.IsNullOrWhiteSpace(toolName) && _toolsByName.ContainsKey(toolName.Trim());
            }

            public ILlmTool RestrictTo(IReadOnlyCollection<string> allowedToolNames)
            {
                return new CallSkillToolProxy(_skills, allowedToolNames);
            }

            public AIFunction CreateAIFunction()
            {
                return AIFunctionFactory.Create(
                    (Func<string, string, CancellationToken, Task<string>>)ExecuteAsync,
                    new AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }

            private Task<string> ExecuteAsync(string tool_name, string arguments_json,
                CancellationToken cancellationToken = default)
            {
                return CallSkillToolLlmTool.ExecuteAsync(tool_name, arguments_json, _toolsByName, cancellationToken);
            }
        }

        private static Dictionary<string, SkillToolDescriptor> BuildToolMap(
            IReadOnlyList<SkillSet> skills,
            IReadOnlyCollection<string> allowedToolNames)
        {
            Dictionary<string, SkillToolDescriptor> toolsByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (SkillToolDescriptor descriptor in SkillSetToolResolver.BuildDescriptors(skills))
            {
                if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Name))
                {
                    continue;
                }

                if (!IsAllowed(descriptor.Name, allowedToolNames))
                {
                    continue;
                }

                toolsByName[descriptor.Name] = descriptor;
            }

            return toolsByName;
        }

        private static async Task<string> ExecuteAsync(string toolName, string argumentsJson,
            Dictionary<string, SkillToolDescriptor> toolsByName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return SkillSetToolResolver.SerializeFailure(
                    "tool_name is required.",
                    toolsByName.Keys);
            }

            string trimmed = toolName.Trim();

            if (!toolsByName.TryGetValue(trimmed, out SkillToolDescriptor descriptor))
            {
                return SkillSetToolResolver.SerializeFailure(
                    $"Tool '{trimmed}' not found.",
                    toolsByName.Keys);
            }

            if (!descriptor.CanInvoke)
            {
                return SkillSetToolResolver.SerializeFailure(
                    $"Tool '{trimmed}' is registered in a skill but does not expose an invocable MEAI binding.",
                    toolsByName.Keys);
            }

            try
            {
                object result = descriptor.DelegateTool != null
                    ? await InvokeDelegateWithJsonAsync(descriptor.DelegateTool, argumentsJson ?? "{}")
                        .ConfigureAwait(false)
                    : await descriptor.Function
                        .InvokeAsync(SkillSetToolResolver.CreateArguments(argumentsJson ?? "{}"), cancellationToken)
                        .ConfigureAwait(false);

                return SkillSetToolResolver.SerializeResult(result);
            }
            catch (JsonException ex)
            {
                return SkillSetToolResolver.SerializeFailure($"Invalid JSON arguments: {ex.Message}", toolsByName.Keys);
            }
            catch (Exception ex)
            {
                return SkillSetToolResolver.SerializeFailure($"Tool execution failed: {Unwrap(ex).Message}");
            }
        }

        /// <summary>
        /// Invoke a DelegateLlmTool by parsing JSON arguments and mapping them to the delegate's parameters.
        /// </summary>
        private static async Task<object> InvokeDelegateWithJsonAsync(DelegateLlmTool tool, string json)
        {
            Delegate action = tool.ActionDelegate;
            ParameterInfo[] parameters = action.Method.GetParameters();

            if (parameters.Length == 0)
            {
                return await AwaitIfTask(action.DynamicInvoke()).ConfigureAwait(false);
            }

            JObject args;
            try
            {
                args = JObject.Parse(json);
            }
            catch
            {
                // Try treating the whole string as a single argument
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    return await AwaitIfTask(action.DynamicInvoke(json)).ConfigureAwait(false);
                }

                throw new JsonReaderException($"Invalid JSON: {json}");
            }

            object[] invokeArgs = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo param = parameters[i];
                JToken token = null;

                // Try exact name match, then case-insensitive
                foreach (KeyValuePair<string, JToken> prop in args)
                {
                    if (string.Equals(prop.Key, param.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        token = prop.Value;
                        break;
                    }
                }

                if (token != null && token.Type != JTokenType.Null)
                {
                    // When the delegate expects a string but the model passes a JSON object/array
                    // (e.g. call_skill_tool's arguments_json parameter), serialize to string.
                    if (param.ParameterType == typeof(string) &&
                        (token.Type == JTokenType.Object || token.Type == JTokenType.Array))
                    {
                        invokeArgs[i] = token.ToString(Formatting.None);
                    }
                    else
                    {
                        invokeArgs[i] = token.ToObject(param.ParameterType);
                    }
                }
                else if (param.HasDefaultValue)
                {
                    invokeArgs[i] = param.DefaultValue;
                }
                else if (param.ParameterType == typeof(string))
                {
                    invokeArgs[i] = null;
                }
                else
                {
                    invokeArgs[i] = param.ParameterType.IsValueType
                        ? Activator.CreateInstance(param.ParameterType)
                        : null;
                }
            }

            return await AwaitIfTask(action.DynamicInvoke(invokeArgs)).ConfigureAwait(false);
        }

        private static async Task<object> AwaitIfTask(object result)
        {
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                Type taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    PropertyInfo resultProperty = taskType.GetProperty("Result");
                    return resultProperty?.GetValue(task);
                }

                return null;
            }

            return result;
        }

        private static bool IsAllowed(string toolName, IReadOnlyCollection<string> allowedToolNames)
        {
            if (allowedToolNames == null || allowedToolNames.Count == 0)
            {
                return true;
            }

            foreach (string allowed in allowedToolNames)
            {
                if (string.Equals(allowed?.Trim(), toolName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static Exception Unwrap(Exception ex)
        {
            return ex is TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException
                : ex;
        }
    }
}
