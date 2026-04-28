namespace CoreAI.Ai
{
    /// <summary>
    /// Provides runtime context for one configured agent role.
    /// </summary>
    public interface IAgentRuntimeContextProvider
    {
        /// <summary>Builds a prompt section for the current request.</summary>
        string BuildContext(AiTaskRequest request, string roleId, string traceId);
    }
}
