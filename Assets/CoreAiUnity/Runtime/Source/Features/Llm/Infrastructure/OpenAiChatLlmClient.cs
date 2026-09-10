#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// OpenAI-compatible chat completion client.
    /// </summary>
    public sealed class OpenAiChatLlmClient : ILlmClient
    {
        private readonly MeaiLlmClient _client;

        public OpenAiChatLlmClient(OpenAiHttpLlmSettings settings, bool supportsNativeToolCalling, IAgentMemoryStore? memoryStore = null)
            : this(settings, CoreAISettingsAsset.Instance, GameLoggerUnscopedFallback.Instance, supportsNativeToolCalling, memoryStore)
        {
        }

        public OpenAiChatLlmClient(CoreAISettingsAsset settings, bool supportsNativeToolCalling, IAgentMemoryStore? memoryStore = null)
            : this(new HttpSettingsAdapter(settings), settings, GameLoggerUnscopedFallback.Instance, supportsNativeToolCalling, memoryStore)
        {
        }

        /// <param name="supportsNativeToolCalling">
        /// The endpoint's channel after a probe or an explicit setting. This synchronous constructor does
        /// not check the server: pass the runtime factory's decision, or a capability known for the
        /// specific adapter.
        /// </param>
        public OpenAiChatLlmClient(IOpenAiHttpSettings settings, ICoreAISettings coreSettings, IGameLogger logger,
            bool supportsNativeToolCalling, IAgentMemoryStore? memoryStore = null)
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

            _client = MeaiLlmClient.CreateHttp(settings,
                coreSettings,
                logger,
                supportsNativeToolCalling: supportsNativeToolCalling,
                memoryStore: memoryStore);
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _client.SetTools(tools);
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _client.SupportsNativeToolCalling;

        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _client.CompleteAsync(request, cancellationToken);
        }

        /// <summary>
        /// Streams a completion through the OpenAI-compatible HTTP client.
        /// </summary>
        public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _client.CompleteStreamingAsync(request, cancellationToken);
        }

        private sealed class HttpSettingsAdapter : IOpenAiHttpSettings
        {
            private readonly CoreAISettingsAsset _s;

            public HttpSettingsAdapter(CoreAISettingsAsset s)
            {
                _s = s;
            }

            public string ApiBaseUrl => _s.ApiBaseUrl;
            public string ApiKey => _s.ApiKey;
            public string AuthorizationHeader => "";
            public string Model => _s.ModelName;
            public LlmExecutionMode ExecutionMode => _s.ExecutionMode;
            public float Temperature => _s.Temperature;
            public int RequestTimeoutSeconds => _s.EffectiveHttpRequestTimeoutSeconds;
            public int MaxTokens => _s.MaxTokens;
            public string ExtraBodyJson => _s.ExtraBodyJson;
            public LlmReasoningMode ReasoningMode => _s.ReasoningMode;
            public int ThinkingBudgetTokens => _s.ThinkingBudgetTokens;
            public bool LogLlmInput => _s.LogLlmInput;
            public bool LogLlmOutput => _s.LogLlmOutput;
            public bool EnableHttpDebugLogging => _s.EnableHttpDebugLogging;

            public IRequestHeaderProvider? HeaderProvider => null;
        }
    }
}
#endif
