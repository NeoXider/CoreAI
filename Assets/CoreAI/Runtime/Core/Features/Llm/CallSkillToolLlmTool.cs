using System;
using System.Collections.Generic;
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
        /// Creates the <c>call_skill_tool</c> <see cref="DelegateLlmTool"/>.
        /// </summary>
        public static DelegateLlmTool Create(IReadOnlyList<SkillSet> skills)
        {
            if (skills == null)
            {
                throw new ArgumentNullException(nameof(skills));
            }

            /* Implementation note in English. */
            Dictionary<string, ToolEntry> toolsByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (SkillSet skill in skills)
            {
                if (skill?.Tools == null)
                {
                    continue;
                }

                foreach (ILlmTool tool in skill.Tools)
                {
                    if (tool != null && !string.IsNullOrWhiteSpace(tool.Name))
                    {
                        toolsByName[tool.Name] = new ToolEntry(skill, tool);
                    }
                }
            }

            object CallSkillToolFn(string tool_name, string arguments_json)
            {
                return Execute(tool_name, arguments_json, toolsByName);
            }

            DelegateLlmTool proxy = new(
                "call_skill_tool",
                "Call a tool from a skill. First call read_skill to learn available tools and their parameters. " +
                "Then call this with tool_name and arguments_json (a JSON object string with the tool's parameters).",
                new Func<string, string, object>(CallSkillToolFn));

            proxy.AllowDuplicates = true;
            return proxy;
        }

        private static object Execute(string toolName, string argumentsJson,
            Dictionary<string, ToolEntry> toolsByName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return new
                {
                    error = "tool_name is required.",
                    available = new List<string>(toolsByName.Keys)
                };
            }

            string trimmed = toolName.Trim();

            if (!toolsByName.TryGetValue(trimmed, out ToolEntry entry))
            {
                return new
                {
                    error = $"Tool '{trimmed}' not found.",
                    available = new List<string>(toolsByName.Keys)
                };
            }

            // Execute the real tool via its delegate
            ILlmTool tool = entry.Tool;
            if (tool is DelegateLlmTool delegateTool)
            {
                try
                {
                    return InvokeDelegateWithJson(delegateTool, argumentsJson ?? "{}");
                }
                catch (Exception ex)
                {
                    return new { error = $"Tool execution failed: {ex.Message}" };
                }
            }

            return new { error = $"Tool '{trimmed}' is not a DelegateLlmTool — direct invocation not supported." };
        }

        /// <summary>
        /// Invoke a DelegateLlmTool by parsing JSON arguments and mapping them to the delegate's parameters.
        /// </summary>
        private static object InvokeDelegateWithJson(DelegateLlmTool tool, string json)
        {
            Delegate action = tool.ActionDelegate;
            System.Reflection.ParameterInfo[] parameters = action.Method.GetParameters();

            if (parameters.Length == 0)
            {
                return action.DynamicInvoke();
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
                    return action.DynamicInvoke(json);
                }

                return new { error = $"Invalid JSON: {json}" };
            }

            object[] invokeArgs = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                System.Reflection.ParameterInfo param = parameters[i];
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

            return action.DynamicInvoke(invokeArgs);
        }

        private readonly struct ToolEntry
        {
            public readonly SkillSet Skill;
            public readonly ILlmTool Tool;

            public ToolEntry(SkillSet skill, ILlmTool tool)
            {
                Skill = skill;
                Tool = tool;
            }
        }
    }
}
