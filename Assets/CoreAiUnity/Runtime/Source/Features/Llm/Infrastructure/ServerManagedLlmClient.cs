#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// OpenAI-compatible client for backend-managed LLM proxy calls with dynamic authorization.
    /// Relies on <see cref="LlmAuthContextRegistry"/> and <see cref="LlmRequestContext"/> for headers.
    /// </summary>
    public sealed class ServerManagedLlmClient : ILlmClient
    {
        private readonly MeaiLlmClient _client;

        /// <summary>
        /// Creates a backend-managed proxy client.
        /// </summary>
        public ServerManagedLlmClient(
            IOpenAiHttpSettings settings,
            ICoreAISettings coreSettings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (coreSettings == null)
            {
                throw new ArgumentNullException(nameof(coreSettings));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _client = MeaiLlmClient.CreateHttp(
                new ServerManagedAuthorizationSettings(settings),
                coreSettings,
                logger,
                memoryStore);
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _client.SetTools(tools);
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _client.SupportsNativeToolCalling;

        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _client.CompleteAsync(request, cancellationToken);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _client.CompleteStreamingAsync(request, cancellationToken);
        }

        private sealed class ServerManagedAuthorizationSettings : IOpenAiHttpSettings
        {
            private readonly IOpenAiHttpSettings _inner;

            public ServerManagedAuthorizationSettings(IOpenAiHttpSettings inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public string ApiBaseUrl => _inner.ApiBaseUrl;

            public string ApiKey => _inner.ApiKey;

            public string AuthorizationHeader
            {
                get
                {
                    string explicitHeader = _inner.AuthorizationHeader;
                    if (!string.IsNullOrWhiteSpace(explicitHeader))
                    {
                        return explicitHeader.Trim();
                    }

                    return ServerManagedAuthorization.GetAuthorizationHeader();
                }
            }

            public string Model => _inner.Model;

            public float Temperature => _inner.Temperature;

            public int RequestTimeoutSeconds => _inner.RequestTimeoutSeconds;

            public int MaxTokens => _inner.MaxTokens;

            public bool LogLlmInput => _inner.LogLlmInput;

            public bool LogLlmOutput => _inner.LogLlmOutput;

            public bool EnableHttpDebugLogging => _inner.EnableHttpDebugLogging;

            public IRequestHeaderProvider HeaderProvider => _inner.HeaderProvider;
        }
    }
}
#endif
