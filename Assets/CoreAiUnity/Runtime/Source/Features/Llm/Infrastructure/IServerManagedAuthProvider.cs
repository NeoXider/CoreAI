namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Provides dynamic authorization for backend-managed LLM proxy calls.
    /// </summary>
    public interface IServerManagedAuthProvider
    {
        /// <summary>
        /// Returns the full Authorization header value, for example <c>Bearer eyJ...</c>.
        /// Return an empty string when the backend does not require a header.
        /// </summary>
        string GetAuthorizationHeader();
    }
}
