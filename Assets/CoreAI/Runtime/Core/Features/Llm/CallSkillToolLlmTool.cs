using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;

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

                // First-registered wins (deterministic, matches the order read_skill enumerates skills).
                // Previously this was last-write-wins, so two skills exposing a same-named tool silently
                // shadowed each other: read_skill advertised skill A's tool while call_skill_tool ran
                // skill B's. Keeping the first and warning makes the collision visible and predictable.
                if (toolsByName.TryGetValue(descriptor.Name, out SkillToolDescriptor existing))
                {
                    Log.Instance.Warn(
                        $"[call_skill_tool] Duplicate skill tool name '{descriptor.Name}' from skill " +
                        $"'{descriptor.Skill?.Name}' shadowed by '{existing.Skill?.Name}' (first wins). " +
                        "Rename one of the tools to avoid silent misrouting.",
                        LogTag.Llm);
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
                object result = descriptor.JsonTool != null
                    ? await descriptor.JsonTool.InvokeJsonAsync(argumentsJson ?? "{}", cancellationToken)
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
            return ex.InnerException ?? ex;
        }
    }
}
