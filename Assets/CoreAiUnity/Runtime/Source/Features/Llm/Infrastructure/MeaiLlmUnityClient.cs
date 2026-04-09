#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
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
    }
}
#endif