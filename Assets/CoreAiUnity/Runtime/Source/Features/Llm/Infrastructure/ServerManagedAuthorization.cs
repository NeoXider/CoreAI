using System;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Session-wide hook for games that need to attach a dynamic JWT or backend token to ServerManagedApi requests.
    /// </summary>
    public static class ServerManagedAuthorization
    {
        private static IServerManagedAuthProvider _provider;
        private static IServerManagedAuthRefresher _refresher;

        /// <summary>
        /// Registers a provider used by all new and existing ServerManagedApi HTTP requests.
        /// </summary>
        public static void SetProvider(IServerManagedAuthProvider provider)
        {
            _provider = provider;
        }

        /// <summary>
        /// Registers an optional refresher invoked by <see cref="RefreshOnUnauthorizedDecorator"/>
        /// when the LLM proxy returns <c>401</c>. Pass <c>null</c> to clear (logout flow).
        /// </summary>
        public static void SetRefresher(IServerManagedAuthRefresher refresher)
        {
            _refresher = refresher;
        }

        /// <summary>Currently registered refresher, or <c>null</c> when none is configured.</summary>
        public static IServerManagedAuthRefresher Refresher => _refresher;

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
        /// Clears the registered provider and refresher. Intended for tests and logout flows.
        /// </summary>
        public static void ClearProvider()
        {
            _provider = null;
            _refresher = null;
        }

        /// <summary>
        /// Get authorization header.
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