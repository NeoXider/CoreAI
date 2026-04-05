#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Поддерживает 2 режима:
    /// 1) Stateless (addToHistory: false) — каждый вызов независим, память через MemoryTool
    /// 2) ChatHistory (addToHistory: true) — LLMAgent сохраняет полный контекст диалога
    /// Вызовы должны идти с главного потока Unity — не используем <c>ConfigureAwait(false)</c>.
    /// </summary>
    public sealed class LlmUnityLlmClient : ILlmClient
    {
        private readonly LLMAgent _unityAgent;
        private readonly IGameLogger _logger;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly AgentMemoryPolicy _memoryPolicy;
        private readonly bool _useChatHistory;
        private string _currentRoleId;
        private IReadOnlyList<ILlmTool> _tools;

        public LLMAgent UnityAgent => _unityAgent;
        public LLM LLM => _unityAgent?.llm ?? _unityAgent?.GetComponent<LLM>();

        /// <param name="unityAgent">Агент LLMUnity на сцене (с привязанным <c>LLM</c>).</param>
        /// <param name="logger">Логгер.</param>
        /// <param name="memoryStore">Хранилище памяти (null = без сохранения истории).</param>
        /// <param name="memoryPolicy">Политика памяти (null = default).</param>
        /// <param name="useChatHistory">true = Тип 2: LLMAgent сохраняет контекст, false = Тип 1: MemoryTool.</param>
        public LlmUnityLlmClient(
            LLMAgent unityAgent,
            IGameLogger logger,
            IAgentMemoryStore memoryStore = null,
            AgentMemoryPolicy memoryPolicy = null,
            bool useChatHistory = false)
        {
            _unityAgent = unityAgent;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _memoryStore = memoryStore;
            _memoryPolicy = memoryPolicy;
            _useChatHistory = useChatHistory;
            _tools = Array.Empty<ILlmTool>();
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _tools = tools ?? Array.Empty<ILlmTool>();
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_unityAgent == null)
            {
                return new LlmCompletionResult { Ok = false, Error = "LLMAgent is null" };
            }

            LLM llm = _unityAgent.llm != null ? _unityAgent.llm : _unityAgent.GetComponent<LLM>();
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
                Task<bool> setupTask = LLM.WaitUntilModelSetup();
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
                        Error =
                            "LLMUnity: не удалась подготовка моделей (LLMManager / скачивание). См. консоль LLMUnity."
                    };
                }

                Task readyTask = llm.WaitUntilReady();
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

                bool prevReasoning = llm.reasoning;
                string prevSystem = _unityAgent.systemPrompt;
                try
                {
                    llm.reasoning = false;

                    // Tools are now injected by MeaiToolsLlmClientDecorator - just use request.SystemPrompt directly
                    if (!string.IsNullOrEmpty(request.SystemPrompt))
                    {
                        _unityAgent.systemPrompt = request.SystemPrompt;
                    }

                    _currentRoleId = request.AgentRoleId ?? "Unknown";

                    // addToHistory: true = LLMAgent сохраняет контекст (Тип 2)
                    // addToHistory: false = каждый вызов независим (Тип 1: MemoryTool)
                    bool addToHistory = _useChatHistory;

                    string text = await _unityAgent.Chat(request.UserPayload ?? string.Empty, addToHistory: addToHistory);
                    cancellationToken.ThrowIfCancellationRequested();

                    // ===== ТИП 2: Сохраняем диалог в хранилище =====
                    if (_useChatHistory && _memoryStore != null && !string.IsNullOrEmpty(text))
                    {
                        _memoryStore.AppendChatMessage(_currentRoleId, "user", request.UserPayload ?? "");
                        _memoryStore.AppendChatMessage(_currentRoleId, "assistant", text);
                    }

                    return new LlmCompletionResult { Ok = true, Content = text ?? "" };
                }
                finally
                {
                    _unityAgent.systemPrompt = prevSystem;
                    llm.reasoning = prevReasoning;
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