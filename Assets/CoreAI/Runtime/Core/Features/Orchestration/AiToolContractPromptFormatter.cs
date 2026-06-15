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

            if (supportsNativeToolCalling)
            {
                AppendForcedToolInstruction(sb, task);
                return sb.ToString();
            }

            bool hasMemoryTool = false;
            IReadOnlyList<ILlmTool> canonicalTools = AiToolOrder.Canonical(tools);
            foreach (ILlmTool tool in canonicalTools)
            {
                if (tool != null &&
                    string.Equals(tool.Name?.Trim(), "memory", StringComparison.OrdinalIgnoreCase))
                {
                    hasMemoryTool = true;
                    break;
                }
            }

            if (hasMemoryTool)
            {
                sb.AppendLine(
                    "Example memory tool call for text-shaped backends: {\"name\":\"memory\",\"arguments\":{\"action\":\"append\",\"content\":\"fact to remember\"}}");
            }

            AppendForcedToolInstruction(sb, task);

            sb.AppendLine("Available tools:");
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
                    sb.AppendLine(SingleLine(CanonicalizeSchemaOrRaw(tool.ParametersSchema), 800));
                }
            }

            return sb.ToString();
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
