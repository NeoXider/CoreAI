using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Shared formatter for the tool-contract system prompt section used by orchestrator requests and diagnostics.
    /// </summary>
    internal static class AiToolContractPromptFormatter
    {
        public static string AppendToolContract(
            string system,
            IReadOnlyList<ILlmTool> tools,
            AiTaskRequest task,
            ICoreAISettings settings,
            bool supportsNativeToolCalling = false)
        {
            return AppendToolContractCore(
                system,
                tools,
                task,
                settings,
                supportsNativeToolCalling,
                !supportsNativeToolCalling,
                true,
                "Available tools:");
        }

        /// <summary>
        /// Appends the deterministic, role-wide tool contract used by the cacheable provider prefix.
        /// </summary>
        public static string AppendStableRoleToolContract(
            string system,
            IReadOnlyList<ILlmTool> roleTools,
            ICoreAISettings settings,
            bool supportsNativeToolCalling = false)
        {
            return AppendToolContractCore(
                system,
                roleTools,
                null,
                settings,
                supportsNativeToolCalling,
                true,
                false,
                "Role tool definitions:");
        }

        /// <summary>
        /// Builds deterministic current-turn tool availability guidance for a system-role tail message.
        /// </summary>
        public static string BuildRequestToolAvailabilityMessage(
            IReadOnlyList<ILlmTool> requestTools,
            AiTaskRequest task)
        {
            bool hasTools = requestTools != null && requestTools.Count > 0;
            bool hasPerRequestPolicy = task != null &&
                                       (task.AllowedToolNames != null ||
                                        task.ForcedToolMode != LlmToolChoiceMode.Auto ||
                                        !string.IsNullOrWhiteSpace(task.RequiredToolName));
            if (!hasTools && !hasPerRequestPolicy)
            {
                return "";
            }

            StringBuilder sb = new();
            sb.AppendLine("## Tool Availability (current request)");
            sb.AppendLine("Available tools:");
            AppendCanonicalToolNames(sb, requestTools);

            if (task?.AllowedToolNames != null)
            {
                sb.AppendLine("Request allowlist entries:");
                AppendCanonicalNames(sb, task.AllowedToolNames);
            }

            LlmToolChoiceMode mode = task?.ForcedToolMode ?? LlmToolChoiceMode.Auto;
            switch (mode)
            {
                case LlmToolChoiceMode.None:
                    sb.AppendLine("Tool selection mode: none. Do not emit a tool call.");
                    break;
                case LlmToolChoiceMode.RequireAny:
                    sb.AppendLine("Tool selection mode: require any. Call at least one available tool.");
                    break;
                case LlmToolChoiceMode.RequireSpecific:
                    sb.AppendLine("Tool selection mode: require specific.");
                    if (!string.IsNullOrWhiteSpace(task?.RequiredToolName))
                    {
                        sb.Append("Required tool: '").Append(task.RequiredToolName.Trim()).AppendLine("'.");
                    }

                    break;
                default:
                    sb.AppendLine("Tool selection mode: auto.");
                    if (!string.IsNullOrWhiteSpace(task?.RequiredToolName))
                    {
                        sb.Append("Inactive required-tool value: '")
                            .Append(task.RequiredToolName.Trim())
                            .AppendLine("'.");
                    }

                    break;
            }

            sb.AppendLine(
                "Do not call any tool not listed under Available tools, even if it appears in the shared role contract.");
            return sb.ToString().TrimEnd();
        }

        private static string AppendToolContractCore(
            string system,
            IReadOnlyList<ILlmTool> tools,
            AiTaskRequest task,
            ICoreAISettings settings,
            bool supportsNativeToolCalling,
            bool includeFullDefinitions,
            bool includeRequestGuidance,
            string definitionsHeading)
        {
            if (tools == null || tools.Count == 0)
            {
                return system;
            }

            StringBuilder sb = new();
            sb.Append(string.IsNullOrWhiteSpace(system) ? "" : system.Trim());
            sb.AppendLine();
            sb.AppendLine();
            if (supportsNativeToolCalling)
            {
                AiOrchestratorToolContractDefaults.AppendNativeSection(sb);
            }
            else
            {
                AiOrchestratorToolContractDefaults.AppendStandardSection(sb);
            }

            string extra = settings?.ToolContractAdditionalInstructions?.Trim();
            if (!string.IsNullOrEmpty(extra))
            {
                sb.AppendLine(extra);
            }

            IReadOnlyList<ILlmTool> canonicalTools = AiToolOrder.Canonical(tools);
            bool hasMemoryTool = false;
            foreach (ILlmTool tool in canonicalTools)
            {
                if (tool != null &&
                    string.Equals(tool.Name?.Trim(), "memory", StringComparison.OrdinalIgnoreCase))
                {
                    hasMemoryTool = true;
                    break;
                }
            }

            // WHY: Positive memory trigger — applies to BOTH native and text-shaped tool-calling. Without it a role
            // whose base prompt never mentions memory (e.g. Creator) silently ignores "remember the ..." because
            // nothing maps that soft verb to the memory tool. This guidance previously lived only AFTER the
            // native early-return below, so native tool-calling roles received no memory instruction at all.
            if (hasMemoryTool)
            {
                sb.AppendLine(
                    "Memory: when memory is listed as available for the current request and the task asks you to " +
                    "remember, save, note, record, or persist something, you MUST " +
                    "call the memory tool (action \"append\" or \"store\") to persist it before answering — prose or " +
                    "reasoning alone never saves anything.");
            }

            if (!supportsNativeToolCalling && hasMemoryTool)
            {
                sb.AppendLine(
                    "Example memory tool call for text-shaped backends: {\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"fact to remember\"}}");
            }

            if (includeRequestGuidance)
            {
                AppendForcedToolInstruction(sb, task);
            }

            if (includeFullDefinitions)
            {
                sb.AppendLine(definitionsHeading);
                foreach (ILlmTool tool in canonicalTools)
                {
                    if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
                    {
                        continue;
                    }

                    sb.Append("- ");
                    sb.Append(tool.Name.Trim());
                    if (!string.IsNullOrWhiteSpace(tool.Description))
                    {
                        sb.Append(": ");
                        sb.Append(SingleLine(tool.Description, 500));
                    }

                    sb.AppendLine();

                    if (!string.IsNullOrWhiteSpace(tool.ParametersSchema) && tool.ParametersSchema.Trim() != "{}")
                    {
                        sb.Append("  schema: ");
                        sb.AppendLine(SingleLine(CanonicalizeSchemaOrRaw(tool.ParametersSchema), 0));
                    }
                }
            }

            return sb.ToString();
        }

        private static void AppendCanonicalToolNames(StringBuilder sb, IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                sb.AppendLine("- (none)");
                return;
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ILlmTool tool in AiToolOrder.Canonical(tools))
            {
                string name = tool?.Name?.Trim();
                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                {
                    sb.Append("- ").AppendLine(name);
                }
            }

            if (seen.Count == 0)
            {
                sb.AppendLine("- (none)");
            }
        }

        private static void AppendCanonicalNames(StringBuilder sb, IReadOnlyList<string> names)
        {
            List<string> canonical = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            if (names != null)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    string name = names[i]?.Trim();
                    if (!string.IsNullOrEmpty(name) && seen.Add(name))
                    {
                        canonical.Add(name);
                    }
                }
            }

            canonical.Sort(StringComparer.Ordinal);
            if (canonical.Count == 0)
            {
                sb.AppendLine("- (none)");
                return;
            }

            for (int i = 0; i < canonical.Count; i++)
            {
                sb.Append("- ").AppendLine(canonical[i]);
            }
        }

        private static void AppendForcedToolInstruction(StringBuilder sb, AiTaskRequest task)
        {
            if (task != null && task.ForcedToolMode == LlmToolChoiceMode.RequireSpecific &&
                !string.IsNullOrWhiteSpace(task.RequiredToolName))
            {
                sb.AppendLine($"This request requires calling tool '{task.RequiredToolName.Trim()}'.");
            }
            else if (task != null && task.ForcedToolMode == LlmToolChoiceMode.RequireAny)
            {
                sb.AppendLine("This request requires calling at least one available tool.");
            }
        }

        private static string SingleLine(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (normalized.Contains("  ", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("  ", " ");
            }

            if (maxChars > 0 && normalized.Length > maxChars)
            {
                return normalized.Substring(0, maxChars) + "...";
            }

            return normalized;
        }

        internal static string CanonicalizeSchemaOrRaw(string schema)
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                return schema ?? "";
            }

            try
            {
                JToken token = JToken.Parse(schema);
                JToken sorted = SortObjectKeys(token);
                using StringWriter stringWriter = new(CultureInfo.InvariantCulture);
                using JsonTextWriter jsonWriter = new(stringWriter)
                {
                    Formatting = Formatting.None,
                    Culture = CultureInfo.InvariantCulture
                };

                sorted.WriteTo(jsonWriter);
                return stringWriter.ToString();
            }
            catch
            {
                return schema;
            }
        }

        private static JToken SortObjectKeys(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                {
                    JObject sorted = new();
                    List<JProperty> properties = new(obj.Properties());
                    properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                    foreach (JProperty property in properties)
                    {
                        sorted.Add(property.Name, SortObjectKeys(property.Value));
                    }

                    return sorted;
                }
                case JArray array:
                {
                    JArray sorted = new();
                    foreach (JToken item in array)
                    {
                        sorted.Add(SortObjectKeys(item));
                    }

                    return sorted;
                }
                default:
                    return token.DeepClone();
            }
        }
    }
}
