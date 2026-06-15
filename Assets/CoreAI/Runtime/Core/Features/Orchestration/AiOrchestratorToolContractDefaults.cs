using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Built-in tool-contract guidance injected into the system prompt when the role exposes tools.
    /// Product-specific additions belong in <see cref="ICoreAISettings.ToolContractAdditionalInstructions"/>.
    /// </summary>
    internal static class AiOrchestratorToolContractDefaults
    {
        internal static void AppendNativeSection(StringBuilder sb)
        {
            sb.AppendLine("## Tool Contract");
            sb.AppendLine(
                "You have native tool-calling available for this role. When the user/task asks to use or call a tool, call the matching tool through the tool interface; do not claim that the tool is unavailable, and do not simulate successful execution in prose.");
            sb.AppendLine(
                "Pass arguments as structured tool arguments matching the schema. Required values mentioned in the task (for example targetName, itemName, quantity, action) must be passed as tool arguments, not only described in text.");
            sb.AppendLine("After a tool succeeds, summarize the real tool result briefly for the user.");
            sb.AppendLine(
                "Natural-language-only descriptions (for example that you \"used memory\" or \"called append\") never execute tools and never persist data - they must not replace an actual invocation.");
        }

        internal static void AppendStandardSection(StringBuilder sb)
        {
            sb.AppendLine("## Tool Contract");
            sb.AppendLine(
                "You have native tool-calling available for this role. When the user/task asks to use or call a tool, call the matching tool through the tool interface; do not claim that the tool is unavailable, and do not simulate successful execution in prose.");
            sb.AppendLine(
                "Pass arguments as structured tool arguments matching the schema. Required values mentioned in the task (for example targetName, itemName, quantity, action) must be passed as tool arguments, not only described in text.");
            sb.AppendLine("After a tool succeeds, summarize the real tool result briefly for the user.");
            sb.AppendLine(
                "Natural-language-only descriptions (for example that you \"used memory\" or \"called append\") never execute tools and never persist data — they must not replace an actual invocation.");
            sb.AppendLine(
                "If the backend only receives tools via assistant text (common with local GGUF servers), include a parseable JSON object with \"name\" and \"arguments\" keys in your reply when you intend to call a tool — do not replace it with a prose-only summary.");
        }
    }
}
