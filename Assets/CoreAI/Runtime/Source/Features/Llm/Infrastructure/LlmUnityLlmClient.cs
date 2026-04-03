#if !COREAI_NO_LLM
using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Адаптер <see cref="ILlmClient"/> к <see cref="LLMUnity.LLMAgent"/> по контракту LLMUnity:
    /// дождаться <see cref="LLM.WaitUntilModelSetup"/> (скачивание/подготовка моделей, см. README),
    /// затем готовности локального <see cref="LLM"/> (сервер inference), затем <see cref="LLMAgent.Chat"/>.
    /// Вызовы должны идти с главного потока Unity — не используем <c>ConfigureAwait(false)</c>.
    /// </summary>
    public sealed class LlmUnityLlmClient : ILlmClient
    {
        private readonly LLMAgent _unityAgent;
        private readonly IGameLogger _logger;

        /// <param name="unityAgent">Агент LLMUnity на сцене (с привязанным <c>LLM</c>).</param>
        public LlmUnityLlmClient(LLMAgent unityAgent, IGameLogger logger)
        {
            _unityAgent = unityAgent;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_unityAgent == null)
                return new LlmCompletionResult { Ok = false, Error = "LLMAgent is null" };

            var llm = _unityAgent.llm != null ? _unityAgent.llm : _unityAgent.GetComponent<LLM>();
            if (llm == null)
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = "LLMAgent: не назначен компонент LLM (Inspector → LLM Agent → Llm)."
                };
            }

            try
            {
                var setupTask = LLM.WaitUntilModelSetup();
                while (!setupTask.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (!await setupTask)
                {
                    return new LlmCompletionResult
                    {
                        Ok = false,
                        Error = "LLMUnity: не удалась подготовка моделей (LLMManager / скачивание). См. консоль LLMUnity."
                    };
                }

                var readyTask = llm.WaitUntilReady();
                while (!readyTask.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                await readyTask;
                cancellationToken.ThrowIfCancellationRequested();

                if (llm.failed)
                {
                    return new LlmCompletionResult
                    {
                        Ok = false,
                        Error = "LLMUnity: сервер LLM не поднялся (failed). Проверьте model, логи компонента LLM."
                    };
                }

                var prevSystem = _unityAgent.systemPrompt;
                try
                {
                    if (!string.IsNullOrEmpty(request.SystemPrompt))
                        _unityAgent.systemPrompt = request.SystemPrompt;

                    var text = await _unityAgent.Chat(request.UserPayload ?? string.Empty, addToHistory: false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new LlmCompletionResult { Ok = true, Content = text ?? "" };
                }
                finally
                {
                    _unityAgent.systemPrompt = prevSystem;
                }
            }
            catch (OperationCanceledException)
            {
                return new LlmCompletionResult { Ok = false, Error = "Cancelled" };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Llm, "LlmUnityLlmClient: " + ex.Message);
                return new LlmCompletionResult { Ok = false, Error = ex.Message };
            }
        }
    }
}
#endif
