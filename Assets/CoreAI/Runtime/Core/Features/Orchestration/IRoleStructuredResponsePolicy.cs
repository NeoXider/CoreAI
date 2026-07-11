namespace CoreAI.Ai
{
    /// <summary>
    /// Validates role-specific structured LLM responses.
    /// </summary>
    public interface IRoleStructuredResponsePolicy
    {
        /// <summary>Returns whether structured-response validation should run for the given role.</summary>
        bool ShouldValidate(string roleId);

        /// <summary>Validates raw LLM content for the given role and returns a failure reason when invalid.</summary>
        bool TryValidate(string roleId, string rawContent, out string failureReason);
    }
}
