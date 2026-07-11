namespace CoreAI.Ai
{
    /// <summary>
    /// Process-wide registry for the active <see cref="ILlmAuthContextProvider"/>.
    /// HTTP transports read this to inject <c>X-Tenant-Id</c>, <c>X-User-Id</c>, and
    /// <c>X-Session-Id</c> headers on every server-managed request. Setting the provider
    /// is idempotent: callers register once at composition time, swap on login/logout,
    /// and clear on shutdown. The registry is intentionally portable (no Unity types) so
    /// that the same auth surface works on standalone .NET hosts.
    /// </summary>
    public static class LlmAuthContextRegistry
    {
        private static ILlmAuthContextProvider _current;

        /// <summary>Currently registered provider, or <c>null</c> when no auth context is bound.</summary>
        public static ILlmAuthContextProvider Current => _current;

        /// <summary>Registers a provider. Pass <c>null</c> to clear.</summary>
        public static void SetProvider(ILlmAuthContextProvider provider)
        {
            _current = provider;
        }

        /// <summary>Clears the registered provider.</summary>
        public static void ClearProvider()
        {
            _current = null;
        }
    }
}
