#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Custom tool-calling loop: error counter resets on success, increments on failure.
    /// Delegates duplicate detection, error tracking and notification to <see cref="ToolExecutionPolicy"/>.
    /// </summary>
    public sealed class SmartToolCallingChatClient : MEAI.IChatClient
    {
        private readonly MEAI.IChatClient _innerClient;
        private readonly ILog _logger;
        private readonly int _maxConsecutiveErrors;
        private readonly ICoreAISettings _settings;
        private readonly IReadOnlyList<ILlmTool> _originalTools;
        private readonly bool _allowDuplicateToolCalls;
        private readonly string _roleId;
        private readonly string _traceId;
        private readonly IToolCallEventPublisher _eventPublisher;
        private readonly IToolExecutionNotifier _notifier;

        /// <param name="maxConsecutiveErrors">How many failures in a row are allowed before aborting.</param>
        public SmartToolCallingChatClient(MEAI.IChatClient innerClient, ILog logger, ICoreAISettings settings,
            bool allowDuplicateToolCalls, IReadOnlyList<ILlmTool> tools, string roleId, int maxConsecutiveErrors = 3,
            string traceId = "",
            IToolCallEventPublisher eventPublisher = null, IToolExecutionNotifier notifier = null)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _logger = logger ?? NullLog.Instance;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _allowDuplicateToolCalls = allowDuplicateToolCalls;
            _originalTools = tools ?? new List<ILlmTool>();
            _roleId = roleId ?? "Unknown";
            _traceId = traceId ?? "";
            _maxConsecutiveErrors = maxConsecutiveErrors;
            _eventPublisher = eventPublisher ?? NullToolCallEventPublisher.Instance;
            _notifier = notifier ?? NullToolExecutionNotifier.Instance;
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
                _allowDuplicateToolCalls, _roleId, _maxConsecutiveErrors, _traceId,
                _eventPublisher, _notifier);

            try
            {
                while (true)
                {
                    iteration++;

                    // === Max roundtrip safety valve ===
                    int maxRoundtrips = _settings.MaxToolCallRoundtrips;
                    if (maxRoundtrips > 0 && iteration > maxRoundtrips)
                    {
                        _logger.Warn(
                            $"[SmartToolCall] Warning: Max tool-call roundtrips ({maxRoundtrips}) reached. Stopping.",
                            LogTag.Llm);
                        return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                            $"Agent stopped: exceeded maximum of {maxRoundtrips} tool-call roundtrips."))
                        {
                            FinishReason = MEAI.ChatFinishReason.Stop
                        };
                    }

                    // can stall continuation chains on the single-threaded player loop. Inner awaits use
                    // ConfigureAwait(false) so continuations do not depend on capturing the sync context.

                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.Info(
                            $"[SmartToolCall] Iteration {iteration}: consecutiveErrors={policy.ConsecutiveErrors}/{_maxConsecutiveErrors}, msgs={messages.Count}",
                            LogTag.Llm);
                    }

                    // WebGL player builds: keep the continuation on the captured Unity SynchronizationContext.
                    // resumption to TaskScheduler.Default, where it never got pumped, so the chat panel's
                    // typing dots stayed up forever even though the HTTP response had already arrived.
#if UNITY_WEBGL && !UNITY_EDITOR
                    MEAI.ChatResponse response = await _innerClient
                        .GetResponseAsync(messages, options, cancellationToken);
#else
                    MEAI.ChatResponse response = await _innerClient
                        .GetResponseAsync(messages, options, cancellationToken)
                        .ConfigureAwait(false);
#endif

                    List<MEAI.AIContent> allContents = FlattenAssistantContents(response);

                    List<MEAI.FunctionCallContent>
                        nativeCalls = allContents.OfType<MEAI.FunctionCallContent>().ToList();

                    // Text-mode fallback: providers that emit tool calls as JSON inside an assistant
                    // as the streaming loop, so behaviour is identical regardless of mode.
                    List<MEAI.FunctionCallContent> textCalls = new();
                    string cleanedAssistantText = null;
                    bool hasTextExtraction = false;
                    if (nativeCalls.Count == 0 && (options?.Tools?.Count ?? 0) > 0)
                    {
                        string assistantText = ConcatenateAssistantTextContents(response);
                        if (!string.IsNullOrEmpty(assistantText) &&
                            TryExtractToolCallsFromText(assistantText, out textCalls, out cleanedAssistantText))
                        {
                            hasTextExtraction = true;
                            if (_settings.LogMeaiToolCallingSteps)
                            {
                                _logger.Info(
                                    $"[SmartToolCall] Iteration {iteration}: extracted {textCalls.Count} text-shaped tool call(s) from assistant text.",
                                    LogTag.Llm);
                            }
                        }
                    }

                    List<MEAI.FunctionCallContent> toolCalls = nativeCalls.Count > 0 ? nativeCalls : textCalls;

                    if (toolCalls.Count == 0)
                    {
                        if (_settings.LogMeaiToolCallingSteps)
                        {
                            _logger.Info(
                                $"[SmartToolCall] Iteration {iteration}: Text response, stopping.", LogTag.Llm);
                        }

                        // === Max response chars truncation ===
                        int maxResponseChars = _settings.MaxResponseChars;
                        if (maxResponseChars > 0)
                        {
                            TruncateResponseText(response, maxResponseChars);
                        }

                        return response;
                    }

                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.Info(
                            $"[SmartToolCall] Iteration {iteration}: {toolCalls.Count} tool call(s) ({(nativeCalls.Count > 0 ? "native" : "text")})",
                            LogTag.Llm);
                    }

#if UNITY_WEBGL && !UNITY_EDITOR
                    ToolExecutionPolicy.BatchToolCallResult batch =
                        await policy.ExecuteBatchAsync(toolCalls, options, cancellationToken);
#else
                    ToolExecutionPolicy.BatchToolCallResult batch =
                        await policy.ExecuteBatchAsync(toolCalls, options, cancellationToken)
                            .ConfigureAwait(false);
#endif

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

                    // === Tool call history truncation ===
                    // Prevent unbounded message growth during long tool-calling loops.
                    // Only count tool-related messages (Assistant with FunctionCallContent + Tool result).
                    int maxHistoryMsgs = _settings.MaxToolCallHistoryMessages;
                    if (maxHistoryMsgs > 0)
                    {
                        TrimToolCallHistory(messages, maxHistoryMsgs);
                    }
                }
            }
            finally
            {
                LastExecutedToolCalls = policy.ExecutedTraces.ToList();
            }
        }

        /// <summary>
        /// Collects every <see cref="MEAI.AIContent"/> from assistant messages in <paramref name="response"/>.
        /// In Microsoft.Extensions.AI 10.x, <see cref="MEAI.ChatMessage.Contents"/> is a non-generic
        /// <see cref="System.Collections.IList"/>; LINQ <c>SelectMany</c> combined with
        /// a generic cast can skip provider-specific content wrappers. Explicit iteration keeps
        /// <see cref="MEAI.FunctionCallContent"/> visible so the loop does not mis-classify turns
        /// as text-only or text-shaped tools.
        /// </summary>
        private static List<MEAI.AIContent> FlattenAssistantContents(MEAI.ChatResponse response)
        {
            List<MEAI.AIContent> result = new();
            if (response?.Messages == null)
            {
                return result;
            }

            foreach (MEAI.ChatMessage m in response.Messages)
            {
                if (m?.Contents == null || m.Role != MEAI.ChatRole.Assistant)
                {
                    continue;
                }

                foreach (object obj in m.Contents)
                {
                    if (obj is MEAI.AIContent ai)
                    {
                        result.Add(ai);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Extracts tool calls from assistant text using the portable <see cref="LlmToolCallTextExtractor"/>.
        /// Converts the generic matches into MEAI <see cref="MEAI.FunctionCallContent"/> objects.
        /// </summary>
        internal static bool TryExtractToolCallsFromText(
            string text,
            out List<MEAI.FunctionCallContent> toolCalls,
            out string cleanedText)
        {
            toolCalls = new List<MEAI.FunctionCallContent>();
            cleanedText = text ?? string.Empty;

            if (!LlmToolCallTextExtractor.TryExtract(text, out List<LlmToolCallTextExtractor.Match> matches,
                    out cleanedText))
            {
                return false;
            }

            foreach (LlmToolCallTextExtractor.Match m in matches)
            {
                try
                {
                    Dictionary<string, object> arguments =
                        JsonConvert.DeserializeObject<Dictionary<string, object>>(m.ArgumentsJson)
                        ?? new Dictionary<string, object>();

                    // MEAI's AIFunctionFactory cannot convert JObject/JArray to string parameters.
                    // Normalize: serialize any nested JSON tokens to strings so the delegate
                    // (e.g. call_skill_tool(string tool_name, string arguments_json)) receives
                    // proper string values instead of raw Newtonsoft tokens.
                    NormalizeJTokenValues(arguments);

                    string callId = $"stream_call_{m.Name}_{Guid.NewGuid():N}";
                    toolCalls.Add(new MEAI.FunctionCallContent(callId, m.Name, arguments));
                }
                catch
                {
                    // skip malformed matches
                }
            }

            return toolCalls.Count > 0;
        }

        /// <summary>
        /// Converts any <see cref="JObject"/>/<see cref="JArray"/> values in the dictionary to
        /// their JSON string representation. MEAI's <c>AIFunctionFactory</c> cannot convert
        /// Newtonsoft tokens directly, so this normalization
        /// ensures text-mode extracted arguments work with delegates that expect string parameters.
        /// </summary>
        private static void NormalizeJTokenValues(Dictionary<string, object> arguments)
        {
            if (arguments == null)
            {
                return;
            }

            List<string> keys = new(arguments.Keys);
            foreach (string key in keys)
            {
                if (arguments[key] is JObject jo)
                {
                    arguments[key] = jo.ToString(Formatting.None);
                }
                else if (arguments[key] is JArray ja)
                {
                    arguments[key] = ja.ToString(Formatting.None);
                }
            }
        }

        /// <summary>
        /// Concatenates every <see cref="MEAI.TextContent"/> in <paramref name="response"/> messages.
        /// Use when <see cref="MEAI.ChatResponse.Text"/> is empty but <see cref="MEAI.ChatMessage"/> items
        /// still carry text (some providers / MEAI versions).
        /// </summary>
        public static string ConcatenateAssistantTextContents(MEAI.ChatResponse response)
        {
            if (response?.Messages == null)
            {
                return string.Empty;
            }

            System.Text.StringBuilder sb = new();
            foreach (MEAI.ChatMessage m in response.Messages)
            {
                if (m?.Contents == null)
                {
                    continue;
                }

                foreach (object obj in m.Contents)
                {
                    if (obj is MEAI.TextContent tc && !string.IsNullOrEmpty(tc.Text))
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }

                        sb.Append(tc.Text);
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Truncates all <see cref="MEAI.TextContent"/> in assistant messages to stay within
        /// <paramref name="maxChars"/> total characters. Mutates in-place.
        /// </summary>
        private void TruncateResponseText(MEAI.ChatResponse response, int maxChars)
        {
            if (response?.Messages == null || maxChars <= 0)
            {
                return;
            }

            int remaining = maxChars;
            foreach (MEAI.ChatMessage m in response.Messages)
            {
                if (m?.Contents == null || m.Role != MEAI.ChatRole.Assistant)
                {
                    continue;
                }

                for (int i = 0; i < m.Contents.Count; i++)
                {
                    if (m.Contents[i] is MEAI.TextContent tc && !string.IsNullOrEmpty(tc.Text))
                    {
                        if (tc.Text.Length <= remaining)
                        {
                            remaining -= tc.Text.Length;
                        }
                        else
                        {
                            string truncated = remaining > 0
                                ? tc.Text.Substring(0, remaining) + "\n...[response truncated at " + maxChars + " chars]"
                                : "...[response truncated]";
                            m.Contents[i] = new MEAI.TextContent(truncated);
                            remaining = 0;
                            _logger.Info(
                                $"[SmartToolCall] Response truncated at {maxChars} chars", LogTag.Llm);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Streams the inner chat client response. Tool-calling loops are not executed in this path;
        /// native FunctionCallContent in stream updates will pass through unexecuted.
        /// When tools are present, callers should prefer non-streaming
        /// (<see cref="GetResponseAsync"/>) or enforce non-streaming at the orchestrator level.
        /// </summary>
        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Streaming bypasses the non-streaming tool loop, duplicate detection, and error guard.
            if (options?.Tools != null && options.Tools.Count > 0)
            {
                _logger.Warn(
                    $"[SmartToolCall] Warning: Streaming requested with {options.Tools.Count} tool(s) active. " +
                    "Tool calling loop is NOT supported in streaming mode - tool calls will pass through unexecuted. " +
                    "Consider using non-streaming mode when tools are registered.", LogTag.Llm);
            }

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

        /// <summary>
        /// Removes the oldest tool call message pairs (Assistant + Tool) from the middle of the list
        /// to keep total tool-related messages within <paramref name="maxToolMessages"/>.
        /// System and original user messages at the start are preserved.
        /// </summary>
        private void TrimToolCallHistory(List<MEAI.ChatMessage> messages, int maxToolMessages)
        {
            // Count tool-related messages: any with role Tool, or Assistant with FunctionCallContent.
            int toolMessageCount = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == MEAI.ChatRole.Tool)
                {
                    toolMessageCount++;
                }
                else if (messages[i].Role == MEAI.ChatRole.Assistant && HasFunctionCallContent(messages[i]))
                {
                    toolMessageCount++;
                }
            }

            if (toolMessageCount <= maxToolMessages)
            {
                return;
            }

            int toRemove = toolMessageCount - maxToolMessages;
            int removed = 0;

            // Remove oldest tool call pairs from the list (skip system/user at the beginning).
            for (int i = 0; i < messages.Count && removed < toRemove;)
            {
                bool isToolMsg = messages[i].Role == MEAI.ChatRole.Tool;
                bool isToolAssistant =
                    messages[i].Role == MEAI.ChatRole.Assistant && HasFunctionCallContent(messages[i]);

                if (isToolMsg || isToolAssistant)
                {
                    messages.RemoveAt(i);
                    removed++;
                }
                else
                {
                    i++;
                }
            }

            if (removed > 0 && _settings.LogMeaiToolCallingSteps)
            {
                _logger.Info(
                    $"[SmartToolCall] Trimmed {removed} old tool call message(s), keeping {messages.Count} total.",
                    LogTag.Llm);
            }
        }

        private static bool HasFunctionCallContent(MEAI.ChatMessage message)
        {
            if (message.Contents == null)
            {
                return false;
            }

            foreach (object item in message.Contents)
            {
                if (item is MEAI.FunctionCallContent)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
