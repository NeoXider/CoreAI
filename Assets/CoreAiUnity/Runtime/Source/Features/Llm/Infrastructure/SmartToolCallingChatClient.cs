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
    /// </summary>
    public sealed class SmartToolCallingChatClient : MEAI.IChatClient
    {
        private readonly MEAI.IChatClient _innerClient;
        private readonly IGameLogger _logger;
        private readonly int _maxConsecutiveErrors;
        private readonly ICoreAISettings _settings;

        /// <param name="maxConsecutiveErrors">Сколько неудач подряд допустимо до прерывания агента.</param>
        public SmartToolCallingChatClient(MEAI.IChatClient innerClient, IGameLogger logger, ICoreAISettings settings,
            int maxConsecutiveErrors = 3)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _maxConsecutiveErrors = maxConsecutiveErrors;
        }

        public async Task<MEAI.ChatResponse> GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<MEAI.ChatMessage> messages = chatMessages.ToList();
            int consecutiveErrors = 0;
            int iteration = 0;
            HashSet<string> executedSignatures = new();

            while (true)
            {
                iteration++;
                await Task.Yield(); // Force async boundary to ensure previous LLMUnity states (like isGenerating) are fully flushed

                if (_settings.LogMeaiToolCallingSteps)
                {
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"[SmartToolCall] Iteration {iteration}: consecutiveErrors={consecutiveErrors}/{_maxConsecutiveErrors}, msgs={messages.Count}");
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

                // Защита от застревания: если агент вызывает ровно те же тулзы с теми же аргументами в рамках этой же сессии
                string currentSignature = string.Join("|", toolCalls.Select(t =>
                    $"{t.Name}:{string.Join(",", t.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? Enumerable.Empty<string>())}"));

                bool isDuplicate = !string.IsNullOrEmpty(currentSignature) && !executedSignatures.Add(currentSignature);

                // Выполняем тулы
                List<MEAI.AIContent> toolResults = new();
                bool anyFailed = false;

                if (isDuplicate)
                {
                    anyFailed = true;
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"[SmartToolCall] ⚠ DUPLICATE TOOL CALL DETECTED: {currentSignature}. Rejecting.");
                    foreach (MEAI.FunctionCallContent fc in toolCalls)
                    {
                        toolResults.Add(new MEAI.FunctionResultContent(fc.CallId,
                            "Error: You just executed this exact same tool call with the exact same arguments on the previous step. " +
                            "Do not repeat identical steps. Proceed to the NEXT step or provide a final text response."));
                    }
                }
                else
                {
                    foreach (MEAI.FunctionCallContent fc in toolCalls)
                    {
                        MEAI.AIFunction aiFunc =
                            options?.Tools.OfType<MEAI.AIFunction>().FirstOrDefault(f => f.Name == fc.Name);
                        if (aiFunc == null)
                        {
                            anyFailed = true;
                            toolResults.Add(new MEAI.FunctionResultContent(fc.CallId, $"Tool '{fc.Name}' not found"));
                            continue;
                        }

                        try
                        {
                            MEAI.AIFunctionArguments args = fc.Arguments != null
                                ? new MEAI.AIFunctionArguments(fc.Arguments)
                                : null;
                            object result = await aiFunc.InvokeAsync(args, cancellationToken);
                            string resultText = result?.ToString() ?? "";
                            bool succeeded = !resultText.Contains("\"Success\":false") &&
                                             !resultText.Contains("\"success\":false");

                            if (!succeeded)
                            {
                                anyFailed = true;
                            }

                            if (_settings.LogMeaiToolCallingSteps)
                            {
                                _logger.LogInfo(GameLogFeature.Llm,
                                    $"[SmartToolCall] {fc.Name}: {(succeeded ? "SUCCESS" : "FAILED")}");
                            }

                            toolResults.Add(new MEAI.FunctionResultContent(fc.CallId, resultText));
                        }
                        catch (Exception ex)
                        {
                            anyFailed = true;
                            toolResults.Add(new MEAI.FunctionResultContent(fc.CallId, $"Error: {ex.Message}"));
                            _logger.LogError(GameLogFeature.Llm, $"[SmartToolCall] {fc.Name} threw: {ex.Message}");
                        }
                    }
                } // Закрытие блока else (non duplicate)

                // Обновляем счётчик
                if (!anyFailed)
                {
                    consecutiveErrors = 0; // СБРОС при успехе
                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            "[SmartToolCall] ✓ All succeeded, error counter reset to 0");
                    }
                }
                else
                {
                    consecutiveErrors++; // КОПИМ при ошибке
                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            $"[SmartToolCall] ✗ Some failed, error counter={consecutiveErrors}/{_maxConsecutiveErrors}");
                    }

                    if (consecutiveErrors >= _maxConsecutiveErrors)
                    {
                        _logger.LogWarning(GameLogFeature.Llm,
                            $"[SmartToolCall] ⚠ Max consecutive errors ({_maxConsecutiveErrors}), stopping.");

                        return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                            "{\"error\":\"Agent aborted due to hitting maximum consecutive tool processing errors.\"}"))
                        {
                            FinishReason = MEAI.ChatFinishReason.Stop
                        };
                    }
                }

                // Добавляем результаты в историю
                messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, toolCalls.Cast<MEAI.AIContent>().ToList()));
                messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, toolResults));
            }

            MEAI.ChatMessage lastAssistant = messages.LastOrDefault(m => m.Role == MEAI.ChatRole.Assistant);
            return new MEAI.ChatResponse(lastAssistant ?? new MEAI.ChatMessage(MEAI.ChatRole.Assistant, ""))
            {
                FinishReason = MEAI.ChatFinishReason.Stop
            };
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
