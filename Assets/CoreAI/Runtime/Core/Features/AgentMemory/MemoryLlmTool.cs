using CoreAI.Ai;

namespace CoreAI.AgentMemory
{
    /// <summary>
    /// LLM tool wrapper for agent memory operations.
    /// </summary>
    public sealed class MemoryLlmTool : LlmToolBase
    {
        public override string Name => "memory";

        public override string Description =>
            "Read, store, append, clear, or edit the persistent memory document for agent recall. " +
            "Actions: read returns the current memory document; write replaces the document; append adds a line; clear empties memory; " +
            "str_replace replaces exact old_text with new_text/content (first match unless replace_all=true); " +
            "insert adds content before a 1-based line, after an anchor line, or at the end; " +
            "delete removes exact old_text/content (first match unless replace_all=true); " +
            "rename changes the first leading 'key:' or '# key:' label named old_text to new_text/content.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "Action: read, write, append, clear, str_replace, insert, delete, or rename"),
            ("content", "string", false, "Memory content for write/append/insert, replacement fallback for str_replace/rename, or delete target fallback"),
            ("old_text", "string", false, "Exact text to replace/delete, or section/key label to rename"),
            ("new_text", "string", false, "Replacement text for str_replace, or new section/key label for rename"),
            ("anchor", "string", false, "For insert: exact anchor text; content is inserted after the anchor's line"),
            ("line", "integer", false, "For insert: 1-based line number to insert before; beyond end appends"),
            ("replace_all", "boolean", false, "For str_replace/delete: true edits all exact matches; false edits the first match")
        );
    }
}
