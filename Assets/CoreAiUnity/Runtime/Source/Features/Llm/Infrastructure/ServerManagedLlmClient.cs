#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    public sealed class ServerManagedLlmClient : ILlmClient, ILlmRequestHeaderScope
    {
        private readonly MeaiLlmClient _client;
        private readonly ServerManagedAuthorizationSettings _authorizationSettings;

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

            _authorizationSettings = new ServerManagedAuthorizationSettings(settings);
            _client = MeaiLlmClient.CreateHttp(
                _authorizationSettings,
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
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            using (_authorizationSettings.BeginRequestHeaders(request))
            {
                return await _client.CompleteAsync(request, cancellationToken);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            using (_authorizationSettings.BeginRequestHeaders(request))
            {
                await foreach (LlmStreamChunk chunk in _client.CompleteStreamingAsync(request, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }

        IDisposable ILlmRequestHeaderScope.BeginRequestHeaders(LlmCompletionRequest request)
        {
            return _authorizationSettings.BeginRequestHeaders(request);
        }

        private sealed class ServerManagedAuthorizationSettings : IOpenAiHttpSettings
        {
            private readonly IOpenAiHttpSettings _inner;

            public ServerManagedAuthorizationSettings(IOpenAiHttpSettings inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                HeaderProvider = new CompositeRequestHeaderProvider(_inner.HeaderProvider);
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

            /// <summary>
            /// Forwarded so an empty <see cref="Model"/> reads as "the backend picks the model" instead of
            /// the strict client-owned contract that rejects it.
            /// </summary>
            public LlmExecutionMode ExecutionMode => _inner.ExecutionMode;

            public float Temperature => _inner.Temperature;

            public string ExtraBodyJson => _inner.ExtraBodyJson;

            public LlmReasoningMode ReasoningMode => _inner.ReasoningMode;

            public int ThinkingBudgetTokens => _inner.ThinkingBudgetTokens;

            public int RequestTimeoutSeconds => _inner.RequestTimeoutSeconds;

            public int MaxTokens => _inner.MaxTokens;

            public bool LogLlmInput => _inner.LogLlmInput;

            public bool LogLlmOutput => _inner.LogLlmOutput;

            public bool EnableHttpDebugLogging => _inner.EnableHttpDebugLogging;

            public IRequestHeaderProvider HeaderProvider { get; }

            public IDisposable BeginRequestHeaders(LlmCompletionRequest request)
            {
                return ((CompositeRequestHeaderProvider)HeaderProvider).BeginLogicalRequest(request);
            }

            private sealed class CompositeRequestHeaderProvider : IRequestHeaderProvider
            {
                private static readonly HashSet<string> ReservedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
                {
                    "Authorization",
                    "Content-Type",
                    "Idempotency-Key",
                    "X-Request-Id"
                };

                private readonly IRequestHeaderProvider _inner;
                private readonly ConditionalWeakTable<LlmRequestContextFrame, HeaderSnapshot> _snapshots = new();
                private readonly AsyncLocal<HeaderSnapshot> _activeSnapshot = new();

                public CompositeRequestHeaderProvider(IRequestHeaderProvider inner)
                {
                    _inner = inner;
                }

                public IReadOnlyList<KeyValuePair<string, string>> GetHeaders()
                {
                    return ResolveSnapshot().Headers;
                }

                public string IdempotencyKey => ResolveSnapshot().IdempotencyKey;

                public string RequestId => ResolveSnapshot().RequestId;

                public IDisposable BeginLogicalRequest(LlmCompletionRequest request)
                {
                    if (request == null)
                    {
                        throw new ArgumentNullException(nameof(request));
                    }

                    HeaderSnapshot previous = _activeSnapshot.Value;
                    _activeSnapshot.Value = previous ?? BuildSnapshot();
                    return new SnapshotScope(this, previous);
                }

                private HeaderSnapshot ResolveSnapshot()
                {
                    HeaderSnapshot active = _activeSnapshot.Value;
                    if (active != null)
                    {
                        return active;
                    }

                    LlmRequestContextFrame frame = LlmRequestContext.Current;
                    return frame == null
                        ? BuildSnapshot()
                        : _snapshots.GetValue(frame, _ => BuildSnapshot());
                }

                private HeaderSnapshot BuildSnapshot()
                {
                    IRequestHeaderProvider dynamicProvider = ServerManagedAuthorization.RequestHeaderProvider;
                    List<KeyValuePair<string, string>> headers = new();
                    AppendHeaders(headers, _inner?.GetHeaders());
                    if (!ReferenceEquals(dynamicProvider, _inner))
                    {
                        AppendHeaders(headers, dynamicProvider?.GetHeaders());
                    }

                    return new HeaderSnapshot(
                        headers.ToArray(),
                        _inner?.IdempotencyKey ?? "",
                        _inner?.RequestId ?? "");
                }

                private static void AppendHeaders(
                    List<KeyValuePair<string, string>> destination,
                    IReadOnlyList<KeyValuePair<string, string>> source)
                {
                    if (source == null)
                    {
                        return;
                    }

                    foreach (KeyValuePair<string, string> header in source)
                    {
                        string name = header.Key?.Trim() ?? "";
                        if (name.Length == 0 || ReservedHeaderNames.Contains(name) || Contains(destination, name))
                        {
                            continue;
                        }

                        destination.Add(new KeyValuePair<string, string>(name, header.Value ?? ""));
                    }
                }

                private static bool Contains(List<KeyValuePair<string, string>> headers, string name)
                {
                    foreach (KeyValuePair<string, string> header in headers)
                    {
                        if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                private sealed class HeaderSnapshot
                {
                    public HeaderSnapshot(
                        IReadOnlyList<KeyValuePair<string, string>> headers,
                        string idempotencyKey,
                        string requestId)
                    {
                        Headers = headers;
                        IdempotencyKey = idempotencyKey;
                        RequestId = requestId;
                    }

                    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

                    public string IdempotencyKey { get; }

                    public string RequestId { get; }
                }

                private readonly struct SnapshotScope : IDisposable
                {
                    private readonly CompositeRequestHeaderProvider _owner;
                    private readonly HeaderSnapshot _previous;

                    public SnapshotScope(CompositeRequestHeaderProvider owner, HeaderSnapshot previous)
                    {
                        _owner = owner;
                        _previous = previous;
                    }

                    public void Dispose()
                    {
                        if (_owner != null)
                        {
                            _owner._activeSnapshot.Value = _previous;
                        }
                    }
                }
            }
        }
    }
}
#endif
