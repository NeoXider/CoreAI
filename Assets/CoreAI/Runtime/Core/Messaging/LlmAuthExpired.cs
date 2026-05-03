namespace CoreAI.Messaging
{
    /// <summary>
    /// Published when the server-managed LLM proxy returns <c>401</c>/<c>AuthExpired</c>
    /// and the registered <see cref="CoreAI.Infrastructure.Llm.IServerManagedAuthRefresher"/>
    /// either fails to refresh or is not registered. UI layers subscribe to show a re-login
    /// screen or surface a "session expired" notice.
    /// </summary>
    public readonly struct LlmAuthExpired
    {
        /// <summary>Creates an immutable auth-expired event.</summary>
        public LlmAuthExpired(string traceId, string roleId, bool refreshAttempted, bool refreshSucceeded)
        {
            TraceId = traceId ?? "";
            RoleId = roleId ?? "";
            RefreshAttempted = refreshAttempted;
            RefreshSucceeded = refreshSucceeded;
        }

        /// <summary>End-to-end trace id of the failing request.</summary>
        public string TraceId { get; }

        /// <summary>Agent role id that hit the auth boundary.</summary>
        public string RoleId { get; }

        /// <summary>True when an <c>IServerManagedAuthRefresher</c> was invoked.</summary>
        public bool RefreshAttempted { get; }

        /// <summary>True when refresh completed without throwing and reported success.</summary>
        public bool RefreshSucceeded { get; }
    }
}
