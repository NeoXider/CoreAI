#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
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
        private readonly string _traceId;

        /// <param name="maxConsecutiveErrors">Сколько неудач подряд допустимо до прерывания агента.</param>
        public SmartToolCallingChatClient(MEAI.IChatClient innerClient, IGameLogger logger, ICoreAISettings settings,
            bool allowDuplicateToolCalls, IReadOnlyList<CoreAI.Ai.ILlmTool> tools, string roleId, int maxConsecutiveErrors = 3, string traceId = "")
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _allowDuplicateToolCalls = allowDuplicateToolCalls;
            _originalTools = tools ?? new List<CoreAI.Ai.ILlmTool>();
            _roleId = roleId ?? "Unknown";
            _traceId = traceId ?? "";
            _maxConsecutiveErrors = maxConsecutiveErrors;
        }

        /// <summary>
        /// Tool calls observed during the most recent <see cref="GetResponseAsync"/> invocation.
        /// Populated even when the model emitted JSON-as-text (handled identically to native
        /// FunctionCallContent), so the logging decorator can surface them.
        /// </summary>
        public IReadOnlyList<LlmToolCallTrace> LastExecutedToolCalls { get; private set; } =
            Array.Empty<LlmToolCallTrace>();

        public async Task<MEAI.ChatResponse> GetResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<MEAI.ChatMessage> messages = chatMessages.ToList();
            int iteration = 0;

            // Fresh policy per top-level request so duplicates reset between independent calls
            ToolExecutionPolicy policy = new(_logger, _settings, _originalTools,
                _allowDuplicateToolCalls, _roleId, _maxConsecutiveErrors, _traceId);

            try
            {
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

                    List<MEAI.AIContent> allContents =
                        response.Messages?.SelectMany(m => m.Contents ?? Enumerable.Empty<MEAI.AIContent>()).ToList()
                        ?? new List<MEAI.AIContent>();

                    List<MEAI.FunctionCallContent> nativeCalls = allContents.OfType<MEAI.FunctionCallContent>().ToList();

                    // Text-mode fallback: providers that emit tool calls as JSON inside an assistant
                    // text turn (Ollama, llama.cpp, LM Studio, some Qwen builds) — same recovery path
                    // as the streaming loop, so behaviour is identical regardless of mode.
                    List<MEAI.FunctionCallContent> textCalls = new();
                    string cleanedAssistantText = null;
                    bool hasTextExtraction = false;
                    if (nativeCalls.Count == 0 && (options?.Tools?.Count ?? 0) > 0)
                    {
                        string assistantText = ExtractAssistantText(response);
                        if (!string.IsNullOrEmpty(assistantText) &&
                            MeaiLlmClient.TryExtractToolCallsFromText(assistantText, out textCalls, out cleanedAssistantText))
                        {
                            hasTextExtraction = true;
                            if (_settings.LogMeaiToolCallingSteps)
                            {
                                _logger.LogInfo(GameLogFeature.Llm,
                                    $"[SmartToolCall] Iteration {iteration}: extracted {textCalls.Count} text-shaped tool call(s) from assistant text.");
                            }
                        }
                    }

                    List<MEAI.FunctionCallContent> toolCalls = nativeCalls.Count > 0 ? nativeCalls : textCalls;

                    if (toolCalls.Count == 0)
                    {
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
                            $"[SmartToolCall] Iteration {iteration}: {toolCalls.Count} tool call(s) ({(nativeCalls.Count > 0 ? "native" : "text")})");
                    }

                    ToolExecutionPolicy.BatchToolCallResult batch =
                        await policy.ExecuteBatchAsync(toolCalls, options, cancellationToken);

                    if (policy.IsMaxErrorsReached)
                    {
                        return policy.BuildMaxErrorsResponse();
                    }

                    // Build assistant turn for the next round. For text-mode extraction, we replace the
                    // raw assistant text with the *cleaned* version so the model does not see its own
                    // JSON tool call duplicated as text.
                    List<MEAI.AIContent> assistantContents = toolCalls.Cast<MEAI.AIContent>().ToList();
                    if (hasTextExtraction && !string.IsNullOrWhiteSpace(cleanedAssistantText))
                    {
                        assistantContents.Add(new MEAI.TextContent(cleanedAssistantText));
                    }

                    messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, assistantContents));
                    messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, batch.Results));
                }
            }
            finally
            {
                LastExecutedToolCalls = policy.ExecutedTraces.ToList();
            }
        }

        /// <summary>
        /// Concatenates every <see cref="MEAI.TextContent"/> in a non-streaming response into a single string.
        /// Used by the text-mode tool-call fallback. Returns empty string when the response has no text.
        /// </summary>
        private static string ExtractAssistantText(MEAI.ChatResponse response)
        {
            if (response?.Messages == null) return string.Empty;
            System.Text.StringBuilder sb = new();
            foreach (MEAI.ChatMessage m in response.Messages)
            {
                if (m.Contents == null) continue;
                foreach (MEAI.AIContent c in m.Contents)
                {
                    if (c is MEAI.TextContent tc && !string.IsNullOrEmpty(tc.Text))
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(tc.Text);
                    }
                }
            }
            return sb.ToString();
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
