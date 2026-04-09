#if !COREAI_NO_LLM
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
    /// HTTP OpenAI-compatible клиент. Делегирует в <see cref="MeaiLlmClient"/>.
    /// </summary>
    public sealed class OpenAiChatLlmClient : ILlmClient
    {
        private readonly MeaiLlmClient _client;

        public OpenAiChatLlmClient(OpenAiHttpLlmSettings settings, IAgentMemoryStore? memoryStore = null)
            : this(settings, CoreAISettingsAsset.Instance, GameLoggerUnscopedFallback.Instance, memoryStore)
        {
        }

        public OpenAiChatLlmClient(CoreAISettingsAsset settings, IAgentMemoryStore? memoryStore = null)
            : this(new HttpSettingsAdapter(settings), settings, GameLoggerUnscopedFallback.Instance, memoryStore)
        {
        }

        public OpenAiChatLlmClient(IOpenAiHttpSettings settings, ICoreAISettings coreSettings, IGameLogger logger, IAgentMemoryStore? memoryStore)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (coreSettings == null) throw new ArgumentNullException(nameof(coreSettings));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            _client = MeaiLlmClient.CreateHttp(settings, coreSettings, logger, memoryStore);
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _client.SetTools(tools);
        }

        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _client.CompleteAsync(request, cancellationToken);
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
            public string Model => _s.ModelName;
            public float Temperature => _s.Temperature;
            public int RequestTimeoutSeconds => _s.RequestTimeoutSeconds;
            public int MaxTokens => _s.MaxTokens;
            public bool LogLlmInput => _s.LogLlmInput;
            public bool LogLlmOutput => _s.LogLlmOutput;
            public bool EnableHttpDebugLogging => _s.EnableHttpDebugLogging;
        }
    }
}
#endif