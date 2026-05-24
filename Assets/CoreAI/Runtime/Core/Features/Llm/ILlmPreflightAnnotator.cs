namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Optional portable contract: an <see cref="CoreAI.Ai.ILlmClient"/> that supports
    /// pre-request annotation (e.g. routing metadata). The portable
    /// <see cref="LoggingLlmClientDecorator"/> checks for this interface instead of
    /// a concrete Unity-side type.
    /// </summary>
    public interface ILlmPreflightAnnotator
    {
        /// <summary>
        /// Annotate the request with routing or context metadata before logging/executing.
        /// Called once before the first attempt.
        /// </summary>
        void PreflightAnnotate(Ai.LlmCompletionRequest request);
    }
}