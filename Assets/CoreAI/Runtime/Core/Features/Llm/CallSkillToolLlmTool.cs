using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>Resolves a proxy call once so execution policy and invocation use the same permitted target.</summary>
    public interface IResolvedLlmToolCallProvider
    {
        bool TryResolveInvocation(IDictionary<string, object> arguments,
            out ResolvedLlmToolInvocation invocation, out string error);
    }

    /// <summary>A bound target and argument snapshot; resolving a later catalog version cannot change this call.</summary>
    public sealed class ResolvedLlmToolInvocation
    {
        private readonly SkillToolDescriptor _descriptor;
        private readonly string _argumentsJson;

        internal ResolvedLlmToolInvocation(SkillToolDescriptor descriptor, string argumentsJson)
        {
            _descriptor = descriptor;
            _argumentsJson = argumentsJson;
        }

        public ILlmTool SourceTool => _descriptor.SourceTool;
        public string Name => _descriptor.Name;
        public IDictionary<string, object> Arguments => new ReadOnlyDictionary<string, object>(
            SkillSetToolResolver.CreateArguments(_argumentsJson));

        /// <summary>Invokes the captured binding, honoring cancellation before any tool body is entered.</summary>
        public async Task<object> InvokeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object result = _descriptor.JsonTool != null
                ? await _descriptor.JsonTool.InvokeJsonAsync(_argumentsJson, cancellationToken)
                : await _descriptor.Function.InvokeAsync(SkillSetToolResolver.CreateArguments(_argumentsJson),
                    cancellationToken);
            return SkillSetToolResolver.SerializeResult(result);
        }
    }

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

        private sealed class CallSkillToolProxy : LlmToolBase, IAIFunctionLlmTool, ISkillSetMetaLlmTool,
            IResolvedLlmToolCallProvider
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
                if (skills == null) throw new ArgumentNullException(nameof(skills));
                _skills = skills is MutableSkillCatalog ? skills : new List<SkillSet>(skills).AsReadOnly();
                SkillSetToolResolver.ValidateCatalog(_skills);
                _allowedToolNames = allowedToolNames == null ? null : new List<string>(allowedToolNames).AsReadOnly();
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

            // WHY: unresolved proxies are conservative; policy uses the captured target contract once resolved.
            public override bool AllowDuplicates => false;

            public bool ContainsSkillTool(string toolName)
            {
                return !string.IsNullOrWhiteSpace(toolName) && ResolveToolMap().ContainsKey(toolName.Trim());
            }

            public ILlmTool RestrictTo(IReadOnlyCollection<string> allowedToolNames)
            {
                return new CallSkillToolProxy(_skills,
                    SkillSetToolResolver.IntersectAllowlist(_allowedToolNames, allowedToolNames), _directToolsProvider);
            }

            public bool TryResolveInvocation(IDictionary<string, object> arguments,
                out ResolvedLlmToolInvocation invocation, out string error)
            {
                invocation = null;
                error = null;
                if (!TryReadString(arguments, "tool_name", out string name) || string.IsNullOrWhiteSpace(name))
                {
                    error = "tool_name must be a non-empty string.";
                    return false;
                }
                if (!TryReadString(arguments, "arguments_json", out string json))
                {
                    error = "arguments_json must be a JSON object string.";
                    return false;
                }
                string trimmed = name.Trim();
                Dictionary<string, SkillToolDescriptor> map = ResolveToolMap();
                if (!map.TryGetValue(trimmed, out SkillToolDescriptor descriptor))
                {
                    descriptor = ResolveDirectTool(trimmed);
                }
                if (descriptor == null || !descriptor.CanInvoke)
                {
                    error = $"Tool '{trimmed}' is unavailable for this skill call.";
                    return false;
                }
                try
                {
                    JObject parsed = JObject.Parse(json);
                    invocation = new ResolvedLlmToolInvocation(descriptor, parsed.ToString(Formatting.None));
                    return true;
                }
                catch (JsonException ex)
                {
                    error = $"Invalid JSON arguments: {ex.Message}";
                    return false;
                }
            }

            private static bool TryReadString(IDictionary<string, object> arguments, string name, out string value)
            {
                value = null;
                if (arguments == null || !arguments.TryGetValue(name, out object raw))
                {
                    return false;
                }
                if (raw is string text)
                {
                    value = text;
                    return true;
                }
                if (raw is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    value = element.GetString();
                    return true;
                }
                if (raw is JValue token && token.Type == JTokenType.String)
                {
                    value = token.Value<string>();
                    return true;
                }
                return false;
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

            private async Task<string> ExecuteAsync(
                [Description("Skill tool name returned by read_skill.")]
                string tool_name,
                [Description("JSON object string with the skill tool parameters.")]
                string arguments_json,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dictionary<string, object> arguments = new()
                {
                    ["tool_name"] = tool_name,
                    ["arguments_json"] = arguments_json
                };
                if (!TryResolveInvocation(arguments, out ResolvedLlmToolInvocation invocation, out string error))
                {
                    return SkillSetToolResolver.SerializeFailure(error, ResolveToolMap().Keys);
                }
                try
                {
                    return (await invocation.InvokeAsync(cancellationToken))?.ToString();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return SkillSetToolResolver.SerializeFailure($"Tool execution failed: {Unwrap(ex).Message}");
                }
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

                // WHY: shared instances may belong to several skills, but different bindings must never shadow schemas.
                if (toolsByName.TryGetValue(descriptor.Name, out SkillToolDescriptor existing))
                {
                    if (!ReferenceEquals(existing.SourceTool, descriptor.SourceTool))
                    {
                        throw new InvalidOperationException($"Skill tool name '{descriptor.Name}' has conflicting bindings.");
                    }
                    continue;
                }

                toolsByName[descriptor.Name] = descriptor;
            }

            return toolsByName;
        }

        private static bool IsAllowed(string toolName, IReadOnlyCollection<string> allowedToolNames)
        {
            if (allowedToolNames == null)
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
