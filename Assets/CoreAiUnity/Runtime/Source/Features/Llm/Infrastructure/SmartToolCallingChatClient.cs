#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Logging;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Кастомный tool-calling цикл: счётчик ОШИБОК сбрасывается при успехе, копится при провале.
    /// Делегирует duplicate detection, error tracking и notification в <see cref="ToolExecutionPolicy"/>.
    /// </summary>
    public sealed class SmartToolCallingChatClient : MEAI.IChatClient
    {
        private readonly MEAI.IChatClient _innerClient;
        private readonly IGameLogger _logger;
        private readonly int _maxConsecutiveErrors;
        private readonly ICoreAISettings _settings;
        private readonly IReadOnlyList<CoreAI.Ai.ILlmTool> _originalTools;
        private readonly bool _allowDuplicateToolCalls;
        private readonly string _roleId;

        /// <param name="maxConsecutiveErrors">Сколько неудач подряд допустимо до прерывания агента.</param>
        public SmartToolCallingChatClient(MEAI.IChatClient innerClient, IGameLogger logger, ICoreAISettings settings,
            bool allowDuplicateToolCalls, IReadOnlyList<CoreAI.Ai.ILlmTool> tools, string roleId, int maxConsecutiveErrors = 3)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _allowDuplicateToolCalls = allowDuplicateToolCalls;
            _originalTools = tools ?? new List<CoreAI.Ai.ILlmTool>();
            _roleId = roleId ?? "Unknown";
            _maxConsecutiveErrors = maxConsecutiveErrors;
        }

        public async Task<MEAI.ChatResponse> GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<MEAI.ChatMessage> messages = chatMessages.ToList();
            int iteration = 0;

            // Fresh policy per top-level request so duplicates reset between independent calls
            ToolExecutionPolicy policy = new(_logger, _settings, _originalTools,
                _allowDuplicateToolCalls, _roleId, _maxConsecutiveErrors);

            while (true)
            {
                iteration++;
                await Task.Yield(); // Force async boundary to ensure previous LLMUnity states (like isGenerating) are fully flushed

                if (_settings.LogMeaiToolCallingSteps)
                {
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"[SmartToolCall] Iteration {iteration}: consecutiveErrors={policy.ConsecutiveErrors}/{_maxConsecutiveErrors}, msgs={messages.Count}");
                }

                MEAI.ChatResponse response = await _innerClient.GetResponseAsync(messages, options, cancellationToken);

                // Проверяем tool calls
                List<MEAI.AIContent> allContents =
                    response.Messages?.SelectMany(m => m.Contents ?? Enumerable.Empty<MEAI.AIContent>()).ToList()
                    ?? new List<MEAI.AIContent>();

                List<MEAI.FunctionCallContent> toolCalls = allContents.OfType<MEAI.FunctionCallContent>().ToList();
                if (toolCalls.Count == 0)
                {
                    // Модель вернула текст — выходим
                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            $"[SmartToolCall] Iteration {iteration}: Text response, stopping.");
                    }

                    return response;
                }

                if (_settings.LogMeaiToolCallingSteps)
                {
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"[SmartToolCall] Iteration {iteration}: {toolCalls.Count} tool call(s)");
                }

                // Execute via shared policy (handles duplicates, errors, notifications)
                ToolExecutionPolicy.BatchToolCallResult batch =
                    await policy.ExecuteBatchAsync(toolCalls, options, cancellationToken);

                // Check max errors guard
                if (policy.IsMaxErrorsReached)
                {
                    return policy.BuildMaxErrorsResponse();
                }

                // Добавляем результаты в историю
                messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, toolCalls.Cast<MEAI.AIContent>().ToList()));
                messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, batch.Results));
            }
        }

        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (MEAI.ChatResponseUpdate u in _innerClient.GetStreamingResponseAsync(chatMessages, options,
                               cancellationToken))
            {
                yield return u;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType == typeof(SmartToolCallingChatClient))
            {
                return this;
            }

            if (serviceType == typeof(MEAI.IChatClient))
            {
                return _innerClient;
            }

            return null;
        }

        public void Dispose()
        {
            _innerClient.Dispose();
        }
    }
}
#endif
