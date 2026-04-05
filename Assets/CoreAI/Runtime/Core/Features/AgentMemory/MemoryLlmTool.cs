using CoreAI.Ai;

namespace CoreAI.AgentMemory
{
    /// <summary>
    /// ILlmTool реализация для MemoryTool - позволяет LLM вызывать инструмент памяти.
    /// </summary>
    public sealed class MemoryLlmTool : LlmToolBase
    {
        public override string Name => "memory";

        public override string Description =>
            "Store, append, or clear persistent memory for agent. " +
            "Use 'write' to completely replace memory, 'append' to add to existing memory, " +
            "'clear' to erase all memory.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "Action: write, append, or clear"),
            ("content", "string", false, "Memory content for write/append actions")
        );
    }
}