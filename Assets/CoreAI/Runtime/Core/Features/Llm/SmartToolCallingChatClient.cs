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
        private readonly int? _maxRoundtripsOverride;

        /// <param name="maxConsecutiveErrors">How many failures in a row are allowed before aborting.</param>
        /// <param name="maxRoundtripsOverride">
        /// Per-request override for the tool-call roundtrip cap. <c>null</c> = inherit
        /// <see cref="ICoreAISettings.MaxToolCallRoundtrips"/>; <c>0</c> = UNLIMITED; positive = that cap.
        /// </param>
        public SmartToolCallingChatClient(MEAI.IChatClient innerClient, ILog logger, ICoreAISettings settings,
            bool allowDuplicateToolCalls, IReadOnlyList<ILlmTool> tools, string roleId, int maxConsecutiveErrors = 3,
            string traceId = "",
            IToolCallEventPublisher eventPublisher = null, IToolExecutionNotifier notifier = null,
            int? maxRoundtripsOverride = null)
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
            _maxRoundtripsOverride = maxRoundtripsOverride;
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
            int missingRequiredToolResponses = 0;
            int emptyResponsesAfterToolCall = 0;
            bool executedToolCallInRequest = false;
            // Distinct from executedToolCallInRequest (which means "attempted", used to leave
            // required-tool-mode below): the empty-response nudge must only fire after a GENUINE success,
            // not after a batch that entirely failed - that case belongs to the tool-error retry path.
            bool anyToolCallSucceeded = false;

            // Assistant/Tool message pairs from iterations where *every* tool call failed.
            // They exist only as error feedback so the model can retry; once a later iteration
            // succeeds they are obsolete and removed from history to stop wasting tokens.
            List<MEAI.ChatMessage> pendingErrorFeedback = new();

            // Whole-turn usage: providers report usage once per roundtrip; sum across the loop so
            // the returned response carries the total tokens the turn burned, not just the last
            // roundtrip's (parity with the streaming loop's AccumulateTurnUsage behavior).
            MEAI.UsageDetails cumulativeUsage = null;

            // Fresh policy per top-level request so duplicates reset between independent calls
            ToolExecutionPolicy policy = new(_logger, _settings, _originalTools,
                _allowDuplicateToolCalls, _roleId, _maxConsecutiveErrors, _traceId,
                _eventPublisher, _notifier);

            try
            {
                while (true)
                {
                    iteration++;
                    // Per-request override wins when supplied (null = inherit global; 0 = unlimited).
                    int maxRoundtrips = _maxRoundtripsOverride ?? _settings.MaxToolCallRoundtrips;
                    if (maxRoundtrips > 0 && iteration > maxRoundtrips)
                    {
                        // Explain WHY it stopped and exactly how to raise/remove the cap, so a developer
                        // hitting this on a legitimately long task (e.g. a big build) knows the lever.
                        string source = _maxRoundtripsOverride.HasValue
                            ? "per-agent/per-call override"
                            : "global ICoreAISettings.MaxToolCallRoundtrips";
                        _logger.Warn(
                            $"[SmartToolCall] Role '{_roleId}' hit the tool-call roundtrip cap ({maxRoundtrips}, " +
                            $"from {source}) and was stopped to prevent an infinite tool-calling loop. " +
                            "If this task legitimately needs more tool calls, raise the limit or set it to 0 " +
                            "(unlimited) via AgentBuilder.WithMaxToolCallRoundtrips(0), " +
                            "AiTaskRequest.MaxToolCallRoundtrips, or the global CoreAI settings.",
                            LogTag.Llm);

                        // Give the model ONE tools-disabled turn to summarize what it accomplished
                        // (Claude/Cursor parity: a capped run ends in prose, not a canned string).
                        MEAI.ChatResponse capSummary = await TryRunFinalNoToolsSummaryAsync(
                            messages, options, cancellationToken);
                        if (capSummary != null)
                        {
                            cumulativeUsage = LlmUsageAccumulator.Accumulate(cumulativeUsage, capSummary.Usage);
                            AttachCumulativeUsage(capSummary, cumulativeUsage);
                            return capSummary;
                        }

                        MEAI.ChatResponse capResponse = new(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                            $"Agent stopped: exceeded maximum of {maxRoundtrips} tool-call roundtrips " +
                            "(raise or disable via WithMaxToolCallRoundtrips / settings)."))
                        {
                            FinishReason = MEAI.ChatFinishReason.Stop
                        };
                        AttachCumulativeUsage(capResponse, cumulativeUsage);
                        return capResponse;
                    }

                    // can stall continuation chains on the single-threaded player loop. Inner awaits use
                    // ConfigureAwait(false) so continuations do not depend on capturing the sync context.

                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.Info(
                            $"[SmartToolCall] Iteration {iteration}: consecutiveErrors={policy.ConsecutiveErrors}/{_maxConsecutiveErrors}, msgs={messages.Count}",
                            LogTag.Llm);
                    }

                    MEAI.ChatOptions iterationOptions = options;
                    if (executedToolCallInRequest &&
                        options?.ToolMode != null &&
                        options.ToolMode is not MEAI.AutoChatToolMode)
                    {
                        iterationOptions = CloneOptionsWithAutoToolMode(options);
                    }

                    // WebGL player builds: keep the continuation on the captured Unity SynchronizationContext.
                    // resumption to TaskScheduler.Default, where it never got pumped, so the chat panel's
                    // typing dots stayed up forever even though the HTTP response had already arrived.
#if UNITY_WEBGL && !UNITY_EDITOR
                    MEAI.ChatResponse response = await _innerClient
                        .GetResponseAsync(messages, iterationOptions, cancellationToken);
#else
                    MEAI.ChatResponse response = await _innerClient
                        .GetResponseAsync(messages, iterationOptions, cancellationToken)
                        .ConfigureAwait(false);
#endif

                    cumulativeUsage = LlmUsageAccumulator.Accumulate(cumulativeUsage, response.Usage);

                    List<MEAI.AIContent> allContents = FlattenAssistantContents(response);

                    List<MEAI.FunctionCallContent>
                        nativeCalls = allContents.OfType<MEAI.FunctionCallContent>().ToList();

                    // Text-mode fallback: providers that emit tool calls as JSON inside an assistant
                    // as the streaming loop, so behaviour is identical regardless of mode.
                    List<MEAI.FunctionCallContent> textCalls = new();
                    string cleanedAssistantText = null;
                    bool hasTextExtraction = false;
                    if (nativeCalls.Count == 0 && (iterationOptions?.Tools?.Count ?? 0) > 0)
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
                        if (TryGetRequiredToolName(iterationOptions?.ToolMode, out string requiredToolName) &&
                            (iterationOptions?.Tools?.Count ?? 0) > 0)
                        {
                            missingRequiredToolResponses++;
                            string assistantText = ConcatenateAssistantTextContents(response);
                            if (_settings.LogMeaiToolCallingSteps)
                            {
                                _logger.Warn(
                                    $"[SmartToolCall] Iteration {iteration}: required tool call was not emitted; retrying ({missingRequiredToolResponses}/{_maxConsecutiveErrors}).",
                                    LogTag.Llm);
                            }

                            if (missingRequiredToolResponses > _maxConsecutiveErrors)
                            {
                                MEAI.ChatResponse missingToolResponse =
                                    BuildMissingRequiredToolResponse(requiredToolName);
                                AttachCumulativeUsage(missingToolResponse, cumulativeUsage);
                                return missingToolResponse;
                            }

                            if (!string.IsNullOrWhiteSpace(assistantText))
                            {
                                messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, assistantText));
                            }

                            messages.Add(new MEAI.ChatMessage(
                                MEAI.ChatRole.User,
                                BuildMissingRequiredToolInstruction(requiredToolName)));
                            continue;
                        }

                        // A COMPLETELY empty response (no text, no tool call) after this same request has
                        // already executed at least one SUCCESSFUL tool call is the model trailing off
                        // mid-task, not a deliberate "I'm done" - unlike a text-only response, which is
                        // always a legitimate stop. Left alone, this used to end the whole task immediately
                        // even when a generous roundtrip budget (e.g. the G6 free-build's 1000-call cap,
                        // meant for a long iterative session) had barely been touched. Nudge the model to
                        // continue or say it is finished, bounded the same way tool-error retries are,
                        // instead of surfacing a hard failure on the very first empty turn. Gated on
                        // anyToolCallSucceeded (not executedToolCallInRequest) so a batch that failed
                        // entirely and then trailed into an empty response still falls through to the
                        // ordinary failure/stop path below rather than being nudged as if it were progress.
                        string thisTurnText = ConcatenateAssistantTextContents(response);
                        if (string.IsNullOrWhiteSpace(thisTurnText) && anyToolCallSucceeded &&
                            emptyResponsesAfterToolCall < Math.Max(1, _maxConsecutiveErrors))
                        {
                            emptyResponsesAfterToolCall++;
                            if (_settings.LogMeaiToolCallingSteps)
                            {
                                _logger.Warn(
                                    $"[SmartToolCall] Iteration {iteration}: empty response after a tool call; " +
                                    $"nudging to continue ({emptyResponsesAfterToolCall}/{_maxConsecutiveErrors}).",
                                    LogTag.Llm);
                            }

                            messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User,
                                "Your last response had no text and no tool call. If the task is not finished, " +
                                "continue with the next tool call now. If it is finished, reply with a short summary."));
                            continue;
                        }

                        if (_settings.LogMeaiToolCallingSteps)
                        {
                            _logger.Info(
                                $"[SmartToolCall] Iteration {iteration}: Text response, stopping.", LogTag.Llm);
                        }

                        int maxResponseChars = _settings.MaxResponseChars;
                        if (maxResponseChars > 0)
                        {
                            TruncateResponseText(response, maxResponseChars);
                        }

                        AttachCumulativeUsage(response, cumulativeUsage);
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
                        await policy.ExecuteBatchAsync(toolCalls, iterationOptions, cancellationToken);
#else
                    ToolExecutionPolicy.BatchToolCallResult batch =
                        await policy.ExecuteBatchAsync(toolCalls, iterationOptions, cancellationToken)
                            .ConfigureAwait(false);
#endif
                    executedToolCallInRequest = true;
                    if (!batch.AllFailed)
                    {
                        anyToolCallSucceeded = true;
                    }

                    if (policy.IsMaxErrorsReached)
                    {
                        // Same graceful ending as the roundtrip cap: one tools-disabled turn so the
                        // model can explain what happened instead of a canned JSON error string.
                        // Append the final failed exchange first so the summary turn can see it
                        // (this branch returns before the regular assistant/tool append below).
                        List<MEAI.AIContent> failedAssistantContents = toolCalls.Cast<MEAI.AIContent>().ToList();
                        if (hasTextExtraction && !string.IsNullOrWhiteSpace(cleanedAssistantText))
                        {
                            failedAssistantContents.Add(new MEAI.TextContent(cleanedAssistantText));
                        }

                        messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, failedAssistantContents));
                        messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, batch.Results));
                        MEAI.ChatResponse errorSummary = await TryRunFinalNoToolsSummaryAsync(
                            messages, options, cancellationToken);
                        if (errorSummary != null)
                        {
                            cumulativeUsage = LlmUsageAccumulator.Accumulate(cumulativeUsage, errorSummary.Usage);
                            AttachCumulativeUsage(errorSummary, cumulativeUsage);
                            return errorSummary;
                        }

                        MEAI.ChatResponse maxErrorsResponse = policy.BuildMaxErrorsResponse();
                        AttachCumulativeUsage(maxErrorsResponse, cumulativeUsage);
                        return maxErrorsResponse;
                    }

                    // Build assistant turn for the next round. For text-mode extraction, we replace the
                    // raw assistant text with the *cleaned* version so the model does not see its own
                    // JSON tool call duplicated as text.
                    List<MEAI.AIContent> assistantContents = toolCalls.Cast<MEAI.AIContent>().ToList();
                    if (hasTextExtraction && !string.IsNullOrWhiteSpace(cleanedAssistantText))
                    {
                        assistantContents.Add(new MEAI.TextContent(cleanedAssistantText));
                    }

                    MEAI.ChatMessage assistantTurn = new(MEAI.ChatRole.Assistant, assistantContents);
                    MEAI.ChatMessage toolTurn = new(MEAI.ChatRole.Tool, batch.Results);
                    messages.Add(assistantTurn);
                    messages.Add(toolTurn);
                    // Track all-failed iterations as removable error feedback; once an iteration
                    // succeeds, drop the obsolete failed pairs (whole Assistant+Tool pairs, so
                    // tool-call / tool-result pairing stays OpenAI-valid).
                    if (batch.AllFailed)
                    {
                        pendingErrorFeedback.Add(assistantTurn);
                        pendingErrorFeedback.Add(toolTurn);
                    }
                    else if (!batch.AnyFailed && pendingErrorFeedback.Count > 0)
                    {
                        int removedFeedback =
                            ToolCallHistoryTrimmer.RemoveResolvedErrorFeedback(messages, pendingErrorFeedback);
                        if (removedFeedback > 0 && _settings.LogMeaiToolCallingSteps)
                        {
                            _logger.Info(
                                $"[SmartToolCall] Iteration {iteration}: removed {removedFeedback} obsolete error-feedback message(s) after successful retry.",
                                LogTag.Llm);
                        }
                    }

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

        private static bool TryGetRequiredToolName(MEAI.ChatToolMode toolMode, out string requiredToolName)
        {
            requiredToolName = "";
            if (toolMode is not MEAI.RequiredChatToolMode required)
            {
                return false;
            }

            requiredToolName = required.RequiredFunctionName ?? "";
            return true;
        }

        private static string BuildMissingRequiredToolInstruction(string requiredToolName)
        {
            string target = string.IsNullOrWhiteSpace(requiredToolName)
                ? "one of the available tools"
                : $"the '{requiredToolName}' tool";
            return "Tool-call contract violation: call " + target +
                   " now with valid arguments. Do not answer with plain text.";
        }

        private static MEAI.ChatResponse BuildMissingRequiredToolResponse(string requiredToolName)
        {
            string target = string.IsNullOrWhiteSpace(requiredToolName)
                ? "a required tool"
                : $"required tool '{requiredToolName}'";
            return new MEAI.ChatResponse(new MEAI.ChatMessage(
                MEAI.ChatRole.Assistant,
                "Required tool call missing: " + target + "."))
            {
                FinishReason = MEAI.ChatFinishReason.Stop
            };
        }

        private static MEAI.ChatOptions CloneOptionsWithAutoToolMode(MEAI.ChatOptions source)
        {
            if (source == null)
            {
                return null;
            }

            return new MEAI.ChatOptions
            {
                Temperature = source.Temperature,
                MaxOutputTokens = source.MaxOutputTokens,
                Tools = source.Tools,
                ToolMode = MEAI.ChatToolMode.Auto
            };
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
                                ? tc.Text.Substring(0, remaining) + "\n...[response truncated at " + maxChars +
                                  " chars]"
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
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
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
        /// Delegates to the shared <see cref="ToolCallHistoryTrimmer"/> (also used by the streaming
        /// loop in <c>MeaiLlmClient</c>) and logs the removal, keeping trim semantics identical
        /// across both tool-calling modes. See the trimmer for the pairing/ordering guarantees.
        /// </summary>
        private void TrimToolCallHistory(List<MEAI.ChatMessage> messages, int maxToolMessages)
        {
            int removed = ToolCallHistoryTrimmer.Trim(messages, maxToolMessages);
            if (removed > 0 && _settings.LogMeaiToolCallingSteps)
            {
                _logger.Info(
                    $"[SmartToolCall] Trimmed {removed} old tool call message(s), keeping {messages.Count} total.",
                    LogTag.Llm);
            }
        }

        /// <summary>
        /// Attaches the accumulated whole-turn usage to a terminal response so callers report the
        /// total tokens the turn burned across every roundtrip, not just the final one.
        /// A <c>null</c> total leaves whatever the provider set untouched.
        /// </summary>
        private static void AttachCumulativeUsage(MEAI.ChatResponse response, MEAI.UsageDetails cumulativeUsage)
        {
            if (response != null && cumulativeUsage != null)
            {
                response.Usage = cumulativeUsage;
            }
        }

        /// <summary>
        /// Final no-tools summarization turn: when the roundtrip cap or the max-consecutive-errors
        /// guard ends the loop, ask the model for EXACTLY ONE more completion with tools disabled so
        /// the user gets real prose about what was accomplished instead of a canned string.
        /// Calls <see cref="_innerClient"/> directly (never this client), so it cannot re-enter the
        /// tool loop or execute further tools. Returns <c>null</c> when the extra completion fails
        /// or produces no text - callers then fall back to the canned terminal response.
        /// </summary>
        private async Task<MEAI.ChatResponse> TryRunFinalNoToolsSummaryAsync(
            List<MEAI.ChatMessage> messages,
            MEAI.ChatOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                List<MEAI.ChatMessage> summaryMessages = new(messages)
                {
                    new MEAI.ChatMessage(MEAI.ChatRole.User,
                        "Tool budget exhausted. Do not call any more tools. Summarize in plain text " +
                        "what you accomplished and what remains to be done.")
                };

                // Tools deliberately omitted (not just ToolMode=None): the model physically cannot
                // emit another native tool call, so this extra turn can never loop.
                MEAI.ChatOptions summaryOptions = new()
                {
                    Temperature = options?.Temperature,
                    MaxOutputTokens = options?.MaxOutputTokens,
                    ToolMode = MEAI.ChatToolMode.None
                };

#if UNITY_WEBGL && !UNITY_EDITOR
                MEAI.ChatResponse summary = await _innerClient
                    .GetResponseAsync(summaryMessages, summaryOptions, cancellationToken);
#else
                MEAI.ChatResponse summary = await _innerClient
                    .GetResponseAsync(summaryMessages, summaryOptions, cancellationToken)
                    .ConfigureAwait(false);
#endif

                string text = summary?.Text;
                if (string.IsNullOrEmpty(text))
                {
                    text = ConcatenateAssistantTextContents(summary);
                }

                return string.IsNullOrWhiteSpace(text) ? null : summary;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    $"[SmartToolCall] Final no-tools summary turn failed ({ex.GetType().Name}: {ex.Message}); " +
                    "falling back to the canned terminal response.", LogTag.Llm);
                return null;
            }
        }
    }
}
#endif
