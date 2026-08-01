using System;
using System.Threading;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Session-wide hook for games that need to attach a dynamic JWT or backend token to ServerManagedApi requests.
    /// </summary>
    public static class ServerManagedAuthorization
    {
        private static IServerManagedAuthProvider _provider;
        private static IServerManagedAuthRefresher _refresher;
        private static IRequestHeaderProvider _requestHeaderProvider;

        /// <summary>
        /// Registers a provider used by all new and existing ServerManagedApi HTTP requests.
        /// </summary>
        public static void SetProvider(IServerManagedAuthProvider provider)
        {
            Volatile.Write(ref _provider, provider);
        }

        /// <summary>
        /// Registers an optional refresher invoked by <see cref="RefreshOnUnauthorizedDecorator"/>
        /// when the LLM proxy returns <c>401</c>. Pass <c>null</c> to clear (logout flow).
        /// </summary>
        public static void SetRefresher(IServerManagedAuthRefresher refresher)
        {
            Volatile.Write(ref _refresher, refresher);
        }

        /// <summary>Currently registered refresher, or <c>null</c> when none is configured.</summary>
        public static IServerManagedAuthRefresher Refresher => Volatile.Read(ref _refresher);

        /// <summary>
        /// Registers a delegate that returns the full Authorization header value.
        /// </summary>
        public static void SetProvider(Func<string> authorizationHeaderFactory)
        {
            Volatile.Write(ref _provider, authorizationHeaderFactory == null
                ? null
                : new DelegateServerManagedAuthProvider(authorizationHeaderFactory));
        }

        /// <summary>
        /// Registers dynamic custom headers for all new and existing <c>ServerManagedApi</c> clients.
        /// The provider is sampled once per logical request; transport retries reuse that snapshot.
        /// </summary>
        /// <remarks>
        /// <c>Authorization</c>, <c>Content-Type</c>, <c>Idempotency-Key</c>, and <c>X-Request-Id</c>
        /// are transport-owned and are ignored when returned as custom headers. Pass <c>null</c> to clear.
        /// </remarks>
        public static void SetRequestHeaderProvider(IRequestHeaderProvider provider)
        {
            Volatile.Write(ref _requestHeaderProvider, provider);
        }

        /// <summary>Clears only the dynamic custom-header provider.</summary>
        public static void ClearRequestHeaderProvider()
        {
            Volatile.Write(ref _requestHeaderProvider, null);
        }

        /// <summary>
        /// Clears the registered provider and refresher. Intended for tests and logout flows.
        /// </summary>
        public static void ClearProvider()
        {
            Volatile.Write(ref _provider, null);
            Volatile.Write(ref _refresher, null);
        }

        /// <summary>
        /// Get authorization header.
        /// </summary>
        public static string GetAuthorizationHeader()
        {
            return Volatile.Read(ref _provider)?.GetAuthorizationHeader() ?? "";
        }

        internal static IRequestHeaderProvider RequestHeaderProvider =>
            Volatile.Read(ref _requestHeaderProvider);

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
