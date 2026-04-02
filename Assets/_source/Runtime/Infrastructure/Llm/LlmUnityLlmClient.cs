#if !COREAI_NO_LLM
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using LLMUnity;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Адаптер <see cref="ILlmClient"/> к компоненту <see cref="LLMUnity.LLMAgent"/> в сцене.
    /// </summary>
    public sealed class LlmUnityLlmClient : ILlmClient
    {
        private readonly LLMAgent _unityAgent;

        public LlmUnityLlmClient(LLMAgent unityAgent)
        {
            _unityAgent = unityAgent;
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            var prevSystem = _unityAgent.systemPrompt;
            try
            {
                if (!string.IsNullOrEmpty(request.SystemPrompt))
                    _unityAgent.systemPrompt = request.SystemPrompt;

                var text = await _unityAgent.Chat(request.UserPayload ?? string.Empty, addToHistory: false)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new LlmCompletionResult { Ok = true, Content = text ?? "" };
            }
            finally
            {
                _unityAgent.systemPrompt = prevSystem;
            }
        }
    }
}
#endif
