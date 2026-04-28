using System;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Session-wide hook for games that need to attach a dynamic JWT or backend token to ServerManagedApi requests.
    /// </summary>
    public static class ServerManagedAuthorization
    {
        private static IServerManagedAuthProvider _provider;

        /// <summary>
        /// Registers a provider used by all new and existing ServerManagedApi HTTP requests.
        /// </summary>
        public static void SetProvider(IServerManagedAuthProvider provider)
        {
            _provider = provider;
        }

        /// <summary>
        /// Registers a delegate that returns the full Authorization header value.
        /// </summary>
        public static void SetProvider(Func<string> authorizationHeaderFactory)
        {
            _provider = authorizationHeaderFactory == null
                ? null
                : new DelegateServerManagedAuthProvider(authorizationHeaderFactory);
        }

        /// <summary>
        /// Clears the registered provider. Intended for tests and logout flows.
        /// </summary>
        public static void ClearProvider()
        {
            _provider = null;
        }

        /// <summary>
        /// Gets the currently configured Authorization header value.
        /// </summary>
        public static string GetAuthorizationHeader()
        {
            return _provider?.GetAuthorizationHeader() ?? "";
        }

        private sealed class DelegateServerManagedAuthProvider : IServerManagedAuthProvider
        {
            private readonly Func<string> _factory;

            public DelegateServerManagedAuthProvider(Func<string> factory)
            {
                _factory = factory;
            }

            public string GetAuthorizationHeader()
            {
                return _factory?.Invoke() ?? "";
            }
        }
    }
}
