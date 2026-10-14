#if COREAI_LLM
namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// User-role prompts the tool-calling loops add on their fallback paths. The core
    /// <see cref="SmartToolCallingChatClient"/> and the streaming <c>MeaiLlmClient</c> in
    /// <c>com.neoxider.coreaiunity</c> both read them from here, so the wording cannot drift between the loops.
    /// </summary>
    internal static class ToolLoopFallbackPrompts
    {
        /// <summary>
        /// Added after a response with no text and no tool call that followed a successful tool call. It does not
        /// ask the model to narrate tool output, because some hosts keep tool results engine-only.
        /// </summary>
        internal const string EmptyResponseNudge =
            "Your last response had no text and no tool call. If the task is not finished, " +
            "continue with the next tool call now. If it is finished, reply briefly to the user; " +
            "do not claim results the tools did not return.";

        /// <summary>
        /// Added to the one final tools-disabled roundtrip after the roundtrip cap or the consecutive-tool-error
        /// guard stops the loop. That turn is a status report: what is done and what remains.
        /// </summary>
        internal const string ToolBudgetExhaustedFinalTurn =
            "Tool budget exhausted. Do not call any more tools. Reply in plain text from the real tool results: " +
            "what is done and what remains to be done; do not claim results the tools did not return.";
    }
}
#endif
