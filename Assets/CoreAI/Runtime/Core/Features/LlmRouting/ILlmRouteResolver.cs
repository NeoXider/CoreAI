namespace CoreAI.Ai
{
    /// <summary>
    /// Resolves agent roles to portable LLM route profiles.
    /// </summary>
    public interface ILlmRouteResolver
    {
        /// <summary>Resolve a route for a role id.</summary>
        LlmRouteResolution Resolve(string roleId);
    }
}
