namespace CoreAI.Ai
{
    /// <summary>
    /// Provides auth/session context for server-managed LLM routes.
    /// </summary>
    public interface ILlmAuthContextProvider
    {
        /// <summary>Returns a bearer token or full authorization value for the current request context.</summary>
        string GetAuthorizationHeader();

        /// <summary>Tenant id, when known.</summary>
        string TenantId { get; }

        /// <summary>User id, when known.</summary>
        string UserId { get; }

        /// <summary>Session id, when known.</summary>
        string SessionId { get; }
    }
}
