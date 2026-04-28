namespace CoreAI.Ai
{
    /// <summary>
    /// Adds per-request runtime context to an agent prompt without mutating the static role configuration.
    /// </summary>
    public interface IAiPromptContextProvider
    {
        /// <summary>
        /// Builds a prompt section for the current request. Return an empty string when no context applies.
        /// </summary>
        string BuildContext(AiTaskRequest request, string roleId, string traceId);
    }
}
