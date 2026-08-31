using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    public static class CallSkillToolLlmTool
    {
        /// <summary>
        /// Creates the <c>call_skill_tool</c> tool.
        /// </summary>
        public static ILlmTool Create(IReadOnlyList<SkillSet> skills)
        {
            return Create(skills, null, null);
        }

        /// <summary>
        /// Creates the <c>call_skill_tool</c> tool that also accepts the agent's OWN top-level tool
        /// names, resolved through <paramref name="directToolsProvider"/> at call time.
        /// </summary>
        /// <remarks>
        /// WHY: a skill's instructions teach the model to reach its tools through this wrapper, and the
        /// model generalises — it wraps top-level tools by analogy. Refusing that call is pedantry with
        /// a real cost: the refusal is an ordinary tool RESULT, not an error, so the model reads
        /// "not found", apologises in prose and moves on. Downstream this looked like "the model rarely
        /// spawns a quiz" — the model asked for it every time and we declined. The wrapper knows what
        /// was meant, so it does it. The provider is a callback, not a list, because tools are still
        /// being registered while this tool is built.
        /// </remarks>
        public static ILlmTool Create(
            IReadOnlyList<SkillSet> skills,
            Func<IReadOnlyList<ILlmTool>> directToolsProvider)
        {
            return Create(skills, null, directToolsProvider);
        }

        internal static ILlmTool Create(
            IReadOnlyList<SkillSet> skills,
            IReadOnlyCollection<string> allowedToolNames,
            Func<IReadOnlyList<ILlmTool>> directToolsProvider)
        {
            return new CallSkillToolProxy(skills, allowedToolNames, directToolsProvider);
        }

        private sealed class CallSkillToolProxy : LlmToolBase, IAIFunctionLlmTool, ISkillSetMetaLlmTool
        {
            private readonly IReadOnlyList<SkillSet> _skills;

            private readonly IReadOnlyCollection<string> _allowedToolNames;

            private readonly Func<IReadOnlyList<ILlmTool>> _directToolsProvider;

            // WHY: When the backing list is a live MutableSkillCatalog (skill authoring), the tool map is
            // rebuilt per call so a tool exposed by a just-authored skill is immediately invocable.
            private readonly bool _isLive;
            private readonly Dictionary<string, SkillToolDescriptor> _toolsByName;

            public CallSkillToolProxy(
                IReadOnlyList<SkillSet> skills,
                IReadOnlyCollection<string> allowedToolNames,
                Func<IReadOnlyList<ILlmTool>> directToolsProvider)
            {
                _skills = skills ?? throw new ArgumentNullException(nameof(skills));
                _allowedToolNames = allowedToolNames;
                _directToolsProvider = directToolsProvider;
                _isLive = skills is MutableSkillCatalog;
                _toolsByName = _isLive ? null : BuildToolMap(_skills, allowedToolNames);
            }

            private Dictionary<string, SkillToolDescriptor> ResolveToolMap()
            {
                return _isLive ? BuildToolMap(_skills, _allowedToolNames) : _toolsByName;
            }

            public override string Name => "call_skill_tool";

            public override string Description =>
                "Call a tool from a skill. First call read_skill to learn available tools and their parameters. " +
                "Then call this with tool_name and arguments_json (a JSON object string with the tool's parameters).";

            public override string ParametersSchema =>
                "{\"type\":\"object\",\"properties\":{\"tool_name\":{\"type\":\"string\",\"description\":\"Skill tool name returned by read_skill.\"},\"arguments_json\":{\"type\":\"string\",\"description\":\"JSON object string with the skill tool parameters.\"}},\"required\":[\"tool_name\",\"arguments_json\"]}";

            // WHY: call_skill_tool dispatches to an arbitrary resolved skill tool whose effect the outer
            // policy cannot see, so it is treated conservatively as mutating: AllowDuplicates=false so
            // ToolExecutionPolicy suppresses only a CROSS-TURN byte-identical echo (structured no-op)
            // while still allowing intra-turn repeats and never suppressing the retry of a FAILED call.
            public override bool AllowDuplicates => false;

            public bool ContainsSkillTool(string toolName)
            {
                return !string.IsNullOrWhiteSpace(toolName) && ResolveToolMap().ContainsKey(toolName.Trim());
            }

            public ILlmTool RestrictTo(IReadOnlyCollection<string> allowedToolNames)
            {
                return new CallSkillToolProxy(_skills, allowedToolNames, _directToolsProvider);
            }

            /// <summary>
            /// Finds a top-level tool of the same agent by name, or null when there is none.
            /// </summary>
            /// <remarks>
            /// Runs only after the skill map missed, so building the bindings here costs nothing on the
            /// normal path. Meta-tools are skipped: dispatching the wrapper into itself would recurse.
            /// The session allowlist still applies — a tool the turn is not allowed to call must not
            /// become callable just because the name arrived wrapped.
            /// </remarks>
            private SkillToolDescriptor ResolveDirectTool(string toolName)
            {
                IReadOnlyList<ILlmTool> tools = _directToolsProvider?.Invoke();
                if (tools == null || tools.Count == 0 || string.IsNullOrWhiteSpace(toolName))
                {
                    return null;
                }

                List<ILlmTool> candidates = new();
                foreach (ILlmTool tool in tools)
                {
                    if (tool != null && !(tool is ISkillSetMetaLlmTool))
                    {
                        candidates.Add(tool);
                    }
                }

                foreach (SkillToolDescriptor descriptor in SkillSetToolResolver.BuildToolDescriptors(candidates))
                {
                    if (descriptor == null || !descriptor.CanInvoke)
                    {
                        continue;
                    }

                    if (!string.Equals(descriptor.Name, toolName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return IsAllowed(descriptor.Name, _allowedToolNames) ? descriptor : null;
                }

                return null;
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

            private Task<string> ExecuteAsync(
                [Description("Skill tool name returned by read_skill.")]
                string tool_name,
                [Description("JSON object string with the skill tool parameters.")]
                string arguments_json,
                CancellationToken cancellationToken = default)
            {
                return CallSkillToolLlmTool.ExecuteAsync(tool_name, arguments_json, ResolveToolMap(),
                    ResolveDirectTool, cancellationToken);
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

                // WHY: First-registered wins (deterministic, matches the order read_skill enumerates skills).
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
            Dictionary<string, SkillToolDescriptor> toolsByName,
            Func<string, SkillToolDescriptor> resolveDirectTool, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                Log.Instance.Warn("[call_skill_tool] Called without tool_name; nothing was invoked.", LogTag.Llm);
                return SkillSetToolResolver.SerializeFailure(
                    "tool_name is required.",
                    toolsByName.Keys);
            }

            string trimmed = toolName.Trim();

            if (!toolsByName.TryGetValue(trimmed, out SkillToolDescriptor descriptor))
            {
                descriptor = resolveDirectTool?.Invoke(trimmed);
                if (descriptor == null)
                {
                    // WHY: this refusal reaches the model as an ordinary tool RESULT, so nothing throws
                    // and nothing surfaces — the user sees only that the action never happened. Without
                    // this line a wrong tool name is invisible and can only be argued about by guesswork.
                    Log.Instance.Warn(
                        $"[call_skill_tool] Tool '{trimmed}' not found and not invoked. " +
                        $"Available: {string.Join(", ", toolsByName.Keys)}.",
                        LogTag.Llm);
                    return SkillSetToolResolver.SerializeFailure(
                        $"Tool '{trimmed}' not found.",
                        toolsByName.Keys);
                }

                Log.Instance.Info(
                    $"[call_skill_tool] '{trimmed}' is a top-level tool, not a skill tool; invoked it anyway.",
                    LogTag.Llm);
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
            catch (OperationCanceledException)
            {
                // WHY: ToolExecutionPolicy detects a per-tool timeout by CATCHING this. Collapsing it into a
                // plain {"success":false} hid the timeout, skipped the "timed out after Nms" path, and let
                // RecordFailure grow the consecutive-error counter until the turn aborted with a bogus
                // "maximum consecutive tool processing errors". Mirrors DelegateLlmTool.
                throw;
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
