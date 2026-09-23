using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
            Schema = SkillToolSchema.Parse(parametersSchema);
            PreflightFunction = jsonTool == null || jsonTool is DelegateLlmTool ? function : null;
        }

        public SkillSet Skill { get; }
        public ILlmTool SourceTool { get; }
        public string Name { get; }
        public string Description { get; }
        public string ParametersSchema { get; }
        public IJsonInvocableLlmTool JsonTool { get; }
        public AIFunction Function { get; }
        public bool CanInvoke => JsonTool != null || Function != null;

        /// <summary>
        /// <see cref="ParametersSchema"/> read once, at descriptor build time; <c>null</c> when the schema
        /// is blank or not JSON. Every call through the proxy used to re-parse the schema string.
        /// </summary>
        public SkillToolSchema Schema { get; }

        /// <summary>
        /// The MEAI function the invocation will bind its arguments through, or <c>null</c> when it will
        /// not: the argument preflight is a proof only against the binder that actually runs. A pure
        /// <see cref="Function"/> route binds through it by definition; a <see cref="DelegateLlmTool"/>
        /// JSON route is documented as "the same MEAI binding used by direct tools"; any other
        /// JSON-invocable tool parses its arguments itself, so nothing can be proven ahead of it.
        /// </summary>
        public AIFunction PreflightFunction { get; }
    }

    /// <summary>A tool's JSON parameter schema reduced to what the call-shape checks need.</summary>
    internal sealed class SkillToolSchema
    {
        private readonly HashSet<string> _propertyNames;

        private SkillToolSchema(List<string> required, bool declaresProperties, List<string> propertyNames,
            List<string> expected)
        {
            Required = required;
            DeclaresProperties = declaresProperties;
            PropertyNames = propertyNames;
            ExpectedParameters = expected;
            _propertyNames = new HashSet<string>(propertyNames, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Required { get; }

        /// <summary>True when the schema carries a <c>properties</c> object, even an empty one.</summary>
        public bool DeclaresProperties { get; }

        /// <summary>Declared property names in schema order; empty when the schema declares none.</summary>
        public IReadOnlyList<string> PropertyNames { get; }

        /// <summary>One <c>name (type, required|optional)</c> entry per declared property, in schema order.</summary>
        public IReadOnlyList<string> ExpectedParameters { get; }

        public bool DeclaresProperty(string name)
        {
            return name != null && _propertyNames.Contains(name);
        }

        /// <summary>Returns <c>null</c> for a blank or unreadable schema, so a check can stand down instead of blocking.</summary>
        public static SkillToolSchema Parse(string parametersSchema)
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

            List<string> required = LlmToolRequiredArguments.Read(schema);
            List<string> names = new();
            List<string> expected = new();
            JObject properties = schema["properties"] as JObject;
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
                    string requirement = required.Contains(property.Name) ? "required" : "optional";
                    names.Add(property.Name);
                    expected.Add($"{property.Name} ({type}, {requirement})");
                }
            }

            return new SkillToolSchema(required, properties != null, names, expected);
        }
    }

    /// <summary>
    /// A multi-function wrapper as a request allowlist exposes it: the same tool - name, description, schema
    /// and every per-tool setting are the wrapped tool's - offering only the allowed functions.
    /// </summary>
    internal sealed class AllowlistedFunctionsLlmTool : IAIFunctionsLlmTool
    {
        private readonly IAIFunctionsLlmTool _inner;
        private readonly HashSet<string> _allowed;

        public AllowlistedFunctionsLlmTool(IAIFunctionsLlmTool inner, IReadOnlyList<string> callableNames)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            CallableNames = callableNames ?? Array.Empty<string>();
            _allowed = new HashSet<string>(CallableNames, StringComparer.Ordinal);
        }

        /// <summary>The allowed function names, in the wrapped tool's order.</summary>
        public IReadOnlyList<string> CallableNames { get; }

        public string Name => _inner.Name;
        public string Description => _inner.Description;
        public string ParametersSchema => _inner.ParametersSchema;
        public bool AllowDuplicates => _inner.AllowDuplicates;
        public int? ToolTimeoutMsOverride => _inner.ToolTimeoutMsOverride;
        public bool EndsTurn => _inner.EndsTurn;
        public bool IsMutating => _inner.IsMutating;

        public IEnumerable<AIFunction> CreateAIFunctions()
        {
            IEnumerable<AIFunction> functions = _inner.CreateAIFunctions();
            if (functions == null)
            {
                yield break;
            }

            foreach (AIFunction function in functions)
            {
                if (function != null && function.Name != null && _allowed.Contains(function.Name))
                {
                    yield return function;
                }
            }
        }
    }

    internal static class SkillSetToolResolver
    {
        private static readonly ConditionalWeakTable<ILlmTool, string[]> CallableNameCache = new();

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
        public static string DescribeMissingRequiredArguments(SkillToolDescriptor descriptor, JObject arguments)
        {
            return DescribeMissingRequiredArguments(descriptor.Name, descriptor.Schema, arguments);
        }

        /// <summary>Same check from a raw schema string, parsed on the spot (tests and one-off callers).</summary>
        public static string DescribeMissingRequiredArguments(string toolName, string parametersSchema, JObject arguments)
        {
            return DescribeMissingRequiredArguments(toolName, SkillToolSchema.Parse(parametersSchema), arguments);
        }

        private static string DescribeMissingRequiredArguments(string toolName, SkillToolSchema schema, JObject arguments)
        {
            if (schema == null)
            {
                return null;
            }

            List<string> missing = LlmToolRequiredArguments.FindMissing(schema.Required, arguments);
            if (missing.Count == 0)
            {
                return null;
            }

            List<string> unknown = new();
            if (arguments != null && schema.DeclaresProperties)
            {
                foreach (JProperty property in arguments.Properties())
                {
                    if (!schema.DeclaresProperty(property.Name))
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

            return message + ExpectedParametersSuffix(schema) + NotExecutedSuffix(toolName,
                "an arguments_json object that uses exactly these parameter names");
        }

        /// <summary>
        /// The refusal for an argument whose value cannot bind to the target's parameter type, from the
        /// structural preflight's own finding (<paramref name="bindingError"/> already names the tool,
        /// the argument and the binder's reason). Carries the same expected-parameter list and retry
        /// instruction as the missing-argument refusal, so a single retry can fix the value.
        /// </summary>
        public static string DescribeArgumentTypeMismatch(SkillToolDescriptor descriptor, string bindingError)
        {
            return bindingError + ExpectedParametersSuffix(descriptor.Schema) + NotExecutedSuffix(descriptor.Name,
                "an arguments_json object whose values have exactly these types");
        }

        private static string ExpectedParametersSuffix(SkillToolSchema schema)
        {
            return schema == null || schema.ExpectedParameters.Count == 0
                ? ""
                : $" Expected parameters: {string.Join(", ", schema.ExpectedParameters)}.";
        }

        private static string NotExecutedSuffix(string toolName, string retryShape)
        {
            return $" The tool was NOT executed. Retry call_skill_tool with tool_name=\"{toolName}\" and {retryShape}.";
        }

        /// <summary>
        /// The function names the provider is offered for <paramref name="tool"/>: every function of an
        /// <see cref="IAIFunctionsLlmTool"/> wrapper (only the allowed ones for a wrapper narrowed by
        /// <see cref="RestrictToAllowedFunctions"/>), the bound function's name for an
        /// <see cref="IAIFunctionLlmTool"/>, otherwise <see cref="ILlmTool.Name"/>. A wrapper whose functions
        /// cannot be built falls back to its own name.
        /// </summary>
        /// <remarks>
        /// WHY a wrapper's names are cached per instance: building them runs <c>AIFunctionFactory.Create</c>
        /// (reflection plus JSON schema generation) for every function, and the names are asked for on every
        /// request - by the prompt formatter, by each <c>ToolExecutionPolicy</c>, by the request
        /// allowlist - on top of the provider client's own expansion. A wrapper's function set is fixed for its
        /// lifetime (the built-in camera and scene wrappers yield a constant list); a wrapper whose set changes
        /// must be registered as a new instance. An enumeration that threw is not cached: the names built
        /// before the failure are still reported, and the next request builds them again.
        /// </remarks>
        internal static IReadOnlyList<string> GetCallableToolNames(ILlmTool tool)
        {
            switch (tool)
            {
                case null:
                    return Array.Empty<string>();
                case AllowlistedFunctionsLlmTool narrowed:
                    return narrowed.CallableNames;
                case IAIFunctionsLlmTool functionTools:
                    return GetWrapperFunctionNames(tool, functionTools);
                case IAIFunctionLlmTool functionTool:
                    AIFunction function = SafeCreateFunction(tool, functionTool);
                    return new[] { !string.IsNullOrWhiteSpace(function?.Name) ? function.Name : tool.Name };
                default:
                    return new[] { tool.Name };
            }
        }

        /// <summary>
        /// <paramref name="tool"/>, a multi-function wrapper whose own name a request allowlist does NOT
        /// contain, as that allowlist exposes it: <c>null</c> when none of its function names is allowed, the
        /// wrapper itself when all of them are, otherwise an <see cref="AllowlistedFunctionsLlmTool"/> that offers
        /// the provider, the execution policy and the prompt only the allowed functions.
        /// </summary>
        internal static ILlmTool RestrictToAllowedFunctions(IAIFunctionsLlmTool tool, ICollection<string> allowed)
        {
            if (tool == null || allowed == null || allowed.Count == 0)
            {
                return null;
            }

            IReadOnlyList<string> callable = GetCallableToolNames(tool);
            List<string> kept = new();
            foreach (string name in callable)
            {
                if (allowed.Contains(name))
                {
                    kept.Add(name);
                }
            }

            if (kept.Count == 0)
            {
                return null;
            }

            return kept.Count == callable.Count ? tool : new AllowlistedFunctionsLlmTool(tool, kept);
        }

        private static IReadOnlyList<string> GetWrapperFunctionNames(ILlmTool tool, IAIFunctionsLlmTool functionTools)
        {
            if (CallableNameCache.TryGetValue(tool, out string[] cached))
            {
                return cached;
            }

            List<string> names = new();
            List<AIFunction> functions = SafeCreateFunctions(functionTools, out bool complete);
            foreach (AIFunction function in functions)
            {
                if (function != null && !string.IsNullOrWhiteSpace(function.Name))
                {
                    names.Add(function.Name);
                }
            }

            if (names.Count == 0)
            {
                names.Add(tool.Name);
            }

            string[] built = names.ToArray();
            if (complete)
            {
                // WHY AddOrUpdate: two requests may build the same wrapper's names at once; both lists are equal.
                CallableNameCache.AddOrUpdate(tool, built);
            }

            return built;
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
                foreach (AIFunction function in SafeCreateFunctions(functionTools, out _))
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

        /// <param name="complete">False when building the functions threw; the ones built before that are returned.</param>
        private static List<AIFunction> SafeCreateFunctions(IAIFunctionsLlmTool functionTools, out bool complete)
        {
            List<AIFunction> created = new();
            complete = false;
            try
            {
                // WHY the enumeration is inside the try: CreateAIFunctions is usually an iterator, so its
                // body - and a failure to build one function - runs while enumerating, not on the call.
                // Outside the try that failure escaped to whoever asked for the names. The functions built
                // before the failure are still reported.
                IEnumerable<AIFunction> functions = functionTools.CreateAIFunctions();
                if (functions != null)
                {
                    foreach (AIFunction function in functions)
                    {
                        created.Add(function);
                    }
                }

                complete = true;
            }
            catch
            {
            }

            return created;
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
}
