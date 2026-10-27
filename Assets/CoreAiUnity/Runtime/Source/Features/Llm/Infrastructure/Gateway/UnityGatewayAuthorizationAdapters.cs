using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
#if COREAI_LLM
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;
#endif

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Opt-in scoped server-client construction; the legacy path works when omitted.</summary>
    public interface IUnityGatewayClientBinding
    {
        /// <summary>Creates a new client whose gateway lifetime transfers to the calling route owner.</summary>
        ILlmClient CreateClient(IOpenAiHttpSettings settings, ICoreAISettings coreSettings,
            IGameLogger logger, IAgentMemoryStore memoryStore, string routingProfileId = "");
    }

    /// <summary>Explicit ownership capability; registry retirement never infers ownership from IDisposable.</summary>
    internal interface IOwnedGatewayClient : IDisposable
    {
        bool OwnsGatewayResources { get; }
    }

#if COREAI_LLM
    /// <summary>Preserves one owned leaf across borrowed decorators and cancels admitted work on retirement.</summary>
    internal sealed class OwnedGatewayClientChain : ILlmClient, ILlmRequestHeaderScope, IOwnedGatewayClient
    {
        private readonly ILlmClient _inner;
        private readonly IOwnedGatewayClient _owned;
        private readonly CancellationTokenSource _retirement = new CancellationTokenSource();
        private readonly CancellationToken _stop;
        private int _disposed;

        public OwnedGatewayClientChain(ILlmClient inner, IOwnedGatewayClient owned)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _owned = owned ?? throw new ArgumentNullException(nameof(owned));
            _stop = _retirement.Token;
        }

        public bool OwnsGatewayResources => true;
        public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;
        public bool SupportsNativeToolCallingForRole(string roleId) => _inner.SupportsNativeToolCallingForRole(roleId);
        public bool SupportsNativeToolCallingForRole(string roleId, string profileId) =>
            _inner.SupportsNativeToolCallingForRole(roleId, profileId);
        public int? ResolveContextWindowTokensForRole(string roleId, string profileId) =>
            _inner.ResolveContextWindowTokensForRole(roleId, profileId);
        public void ResolveRequestSettings(LlmCompletionRequest request) => _inner.ResolveRequestSettings(request);
        public void SetTools(IReadOnlyList<ILlmTool> tools) => _inner.SetTools(tools);

        public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfRetired();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop);
            return await _inner.CompleteAsync(request, linked.Token);
        }

        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfRetired();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop);
            await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, linked.Token))
                yield return chunk;
        }

        IDisposable ILlmRequestHeaderScope.BeginRequestHeaders(LlmCompletionRequest request)
        {
            ThrowIfRetired();
            return LlmRequestHeaderScopes.Begin(_inner, request);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _retirement.Cancel(); }
            finally
            {
                try { _owned.Dispose(); }
                finally { _retirement.Dispose(); }
            }
        }

        private void ThrowIfRetired()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new LlmClientException("The owned gateway route has retired.", LlmErrorCode.BackendUnavailable);
        }
    }

    /// <summary>Owns one host lifetime, without owning caller-provided providers or transports.</summary>
    public sealed class UnityGatewayClientBinding : IUnityGatewayClientBinding, IDisposable
    {
        private readonly GatewayClientOptions _options;
        private readonly ILlmAuthContextProvider _auth;
        private readonly IRequestHeaderProvider _headers;
        private readonly IGatewayAuthRefresher _refresher;
        private readonly ILlmEntitlementPolicy _policy;
        private readonly ILlmUsageSink _sink;
        private readonly IGatewayHttpTransport _serviceTransport;
        private readonly ILog _log;
        private readonly Func<IReadOnlyList<MEAI.ChatMessage>, MEAI.ChatOptions, GatewayRequestContext> _contextFactory;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly CancellationToken _stop;
        private int _disposed;

        public UnityGatewayClientBinding(GatewayClientOptions options,
            ILlmAuthContextProvider auth = null, IRequestHeaderProvider headers = null,
            IGatewayAuthRefresher refresher = null, ILlmEntitlementPolicy policy = null,
            ILlmUsageSink sink = null, IGatewayHttpTransport serviceTransport = null, ILog log = null,
            Func<IReadOnlyList<MEAI.ChatMessage>, MEAI.ChatOptions, GatewayRequestContext> contextFactory = null)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
            _auth = auth; _headers = headers; _refresher = refresher; _policy = policy; _sink = sink;
            _serviceTransport = serviceTransport ?? new UnityWebRequestGatewayTransport();
            _log = log ?? NullLog.Instance;
            _contextFactory = contextFactory;
            _stop = _lifetime.Token;
        }

        public ILlmClient CreateClient(IOpenAiHttpSettings settings, ICoreAISettings coreSettings,
            IGameLogger logger, IAgentMemoryStore memoryStore, string routingProfileId = "")
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UnityGatewayClientBinding));
            _stop.ThrowIfCancellationRequested();
            return MeaiLlmClient.CreateHttp(settings, coreSettings, logger, true, memoryStore,
                (http, completionTransport) => GatewayClientFactory.Create(http, completionTransport,
                    _options, _auth, _headers, _refresher, _policy, _sink, _serviceTransport,
                    (messages, chatOptions) =>
                    {
                        GatewayRequestContext context = _contextFactory == null
                            ? GatewayRequestContext.FromMessages(messages, chatOptions)
                            : _contextFactory(messages, chatOptions);
                        if (context == null) throw new InvalidOperationException("Gateway context factory returned no context.");
                        context = context.Snapshot(messages);
                        if (!string.IsNullOrWhiteSpace(routingProfileId))
                        {
                            context.Request.RoutingProfileId = routingProfileId;
                            context.Profile.ProfileId = routingProfileId;
                        }
                        context.Profile.Mode = LlmExecutionMode.ServerManagedApi;
                        context.Profile.Model = http.Model;
                        return context;
                    }, _log, UnityMainThreadLlmAsyncMarshaler.Instance, true, _stop));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _lifetime.Cancel(); }
            finally { _lifetime.Dispose(); }
        }
    }

    /// <summary>Adapts existing host auth interfaces without changing their static compatibility APIs.</summary>
    public sealed class UnityGatewayAuthorizationAdapter : ILlmAuthContextProvider, IGatewayAuthRefresher
    {
        private readonly IServerManagedAuthProvider _auth;
        private readonly IServerManagedAuthRefresher _refresher;
        public UnityGatewayAuthorizationAdapter(IServerManagedAuthProvider auth,
            IServerManagedAuthRefresher refresher = null)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _refresher = refresher;
        }
        public string GetAuthorizationHeader() => _auth.GetAuthorizationHeader() ?? "";
        public string TenantId => "";
        public string UserId => "";
        public string SessionId => "";
        public Task<bool> RefreshAsync(CancellationToken cancellationToken) =>
            _refresher == null ? Task.FromResult(false) : _refresher.RefreshAsync(cancellationToken);
    }
#endif
}
