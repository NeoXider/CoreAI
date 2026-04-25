#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using LLMUnity;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// LLMUnity клиент. Делегирует в <see cref="MeaiLlmClient"/>.
    /// </summary>
    public sealed class MeaiLlmUnityClient : ILlmClient
    {
        private readonly MeaiLlmClient _client;
        private readonly LLMAgent _unityAgent;

        public LLMAgent UnityAgent => _unityAgent;
        public LLM? LLM => _unityAgent?.llm ?? _unityAgent?.GetComponent<LLM>();

        public MeaiLlmUnityClient(
            LLMAgent unityAgent,
            ICoreAISettings settings,
            IGameLogger logger,
            IAgentMemoryStore? memoryStore = null,
            AgentMemoryPolicy? memoryPolicy = null,
            bool useChatHistory = false)
        {
            if (unityAgent == null)
            {
                throw new ArgumentNullException(nameof(unityAgent));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _unityAgent = unityAgent;
            _client = MeaiLlmClient.CreateLlmUnity(unityAgent, logger, settings, memoryStore);
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

        /// <summary>
        /// Делегирует стриминг в <see cref="MeaiLlmClient.CompleteStreamingAsync"/>, который
        /// использует LLMUnity callback для дельт. Без этого override'а default-реализация
        /// интерфейса выдавала бы весь ответ одним чанком только после завершения генерации.
        /// </summary>
        public IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _client.CompleteStreamingAsync(request, cancellationToken);
        }
    }
}
#endif