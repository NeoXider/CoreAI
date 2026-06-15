using System;
using System.Collections.Generic;
using System.Text;

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
            foreach (ILlmTool tool in tools)
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
            foreach (ILlmTool tool in tools)
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
                    sb.AppendLine(SingleLine(tool.ParametersSchema, 800));
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
    }
}
