#if COREAI_LLM
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

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// MEAI native function loop with CoreAI policy hooks; a host-context fallback supports threadless WebGL.
    /// Duplicate detection, error tracking and notification belong to <see cref="ToolExecutionPolicy"/>.
    /// </summary>
    public sealed class SmartToolCallingChatClient : MEAI.IChatClient
    {
        private readonly MEAI.IChatClient _innerClient;
        private readonly ILog _logger;
        private readonly int _maxConsecutiveErrors;
        private readonly ICoreAISettings _settings;
        private readonly IReadOnlyList<ILlmTool> _originalTools;
        private readonly bool _allowDuplicateToolCalls;
        private readonly string _actorId;
        private readonly string _roleId;
        private readonly string _traceId;
        private readonly IToolCallEventPublisher _eventPublisher;
        private readonly IToolExecutionNotifier _notifier;
        private readonly int? _maxRoundtripsOverride;
        private readonly bool _allowTextShapedToolCalls;

        /// <param name="maxConsecutiveErrors">How many failures in a row are allowed before aborting.</param>
        /// <param name="maxRoundtripsOverride">
        /// Per-request override for the tool-call roundtrip cap. <c>null</c> = inherit
        /// <see cref="ICoreAISettings.MaxToolCallRoundtrips"/>; <c>0</c> = UNLIMITED; positive = that cap.
        /// </param>
        /// <param name="allowTextShapedToolCalls">
        /// Whether the assistant's PROSE may be read as tool calls. Defaults to <c>false</c>, and that
        /// default is the point: interpreting prose means acting on what the model SAID instead of on the
        /// channel it said it through, and a tutor explaining JSON writes objects shaped exactly like a
        /// call — the framework would execute the example instead of showing it. The caller decides by
        /// CHANNEL: <c>MeaiLlmClient</c> passes <c>true</c> only when the endpoint has no native tool
        /// channel or the request explicitly enables compatibility parsing on a native endpoint.
        /// A request with ToolMode=None never interprets prose as executable calls.
        /// </param>
        public SmartToolCallingChatClient(MEAI.IChatClient innerClient, ILog logger, ICoreAISettings settings,
            bool allowDuplicateToolCalls, IReadOnlyList<ILlmTool> tools, string roleId, int maxConsecutiveErrors = 3,
            string traceId = "",
            IToolCallEventPublisher eventPublisher = null, IToolExecutionNotifier notifier = null,
            int? maxRoundtripsOverride = null,
            string actorId = "",
            bool allowTextShapedToolCalls = false)
        {
            _allowTextShapedToolCalls = allowTextShapedToolCalls;
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _logger = logger ?? NullLog.Instance;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _allowDuplicateToolCalls = allowDuplicateToolCalls;
            _originalTools = tools ?? new List<ILlmTool>();
            _actorId = actorId ?? "";
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

        /// <summary>
        /// Provider-reported usage of the LAST model roundtrip of the most recent
        /// <see cref="GetResponseAsync"/> invocation. The returned response's
        /// <c>Usage</c> is the whole-turn cumulative sum (usage/cost metrics); this exposes the
        /// final roundtrip so callers can report the ACTUAL context width (prompt tokens) instead
        /// of the roundtrip-inflated cumulative input count. Null when the provider sent no usage.
        /// </summary>
        public MEAI.UsageDetails LastRoundtripUsage { get; private set; }

        /// <summary>Whether a successful tool intentionally completed this request without another model turn.</summary>
        public bool LastTurnEndedByTool { get; private set; }

        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (options?.ToolMode is MEAI.NoneChatToolMode)
            {
                return GetToolsDisabledResponseAsync(chatMessages, options, cancellationToken);
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            // WHY: MEAI 10.9's function processor resumes with ConfigureAwait(false). Keep the proven
            // host-context loop on threadless WebGL until asynchronous browser execution is verified.
            return GetWebGlResponseAsync(chatMessages, options, cancellationToken);
#else
            return GetMeaiResponseAsync(chatMessages, options, cancellationToken);
#endif
        }

        private async Task<MEAI.ChatResponse> GetToolsDisabledResponseAsync(
            IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options, CancellationToken cancellationToken)
        {
            LastRoundtripUsage = null;
            LastTurnEndedByTool = false;
            LastExecutedToolCalls = Array.Empty<LlmToolCallTrace>();
            MEAI.ChatOptions providerOptions = options.Clone();
            providerOptions.Tools = null;
            // WHY: Even an approval response in history cannot authorize execution in a disabled request.
            MEAI.ChatResponse response = await _innerClient.GetResponseAsync(messages, providerOptions, cancellationToken);
            LastRoundtripUsage = CopyUsage(response.Usage);
            TruncateResponseText(response, _settings.MaxResponseChars);
            return response;
        }

        private async Task<MEAI.ChatResponse> GetMeaiResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
            MEAI.ChatOptions options, CancellationToken cancellationToken)
        {
            List<MEAI.ChatMessage> original = chatMessages.ToList();
            ToolExecutionPolicy policy = new(_logger, _settings, _originalTools, _allowDuplicateToolCalls,
                _roleId, _maxConsecutiveErrors, _traceId, _eventPublisher, _notifier, _actorId);
            LastRoundtripUsage = null;
            LastTurnEndedByTool = false;
            LastExecutedToolCalls = Array.Empty<LlmToolCallTrace>();
            NativePolicyClient boundary = new(this, policy);
            MEAI.FunctionInvokingChatClient client = new(boundary)
            {
                AllowConcurrentInvocation = false,
                MaximumIterationsPerRequest = int.MaxValue,
                MaximumConsecutiveErrorsPerRequest = int.MaxValue,
                AdditionalTools = boundary.FailureBindings,
                FunctionInvoker = boundary.InvokeAsync
            };
            try
            {
                MEAI.ChatResponse response = await client.GetResponseAsync(original, options, cancellationToken);
                if (policy.IsMaxErrorsReached && !policy.TurnEndingToolSucceeded)
                {
                    List<MEAI.ChatMessage> history = new(original);
                    history.AddRange(response.Messages);
                    MEAI.ChatResponse summary = await TryRunFinalNoToolsSummaryAsync(history, options, cancellationToken);
                    if (summary != null)
                    {
                        MEAI.UsageDetails total = LlmUsageAccumulator.Accumulate(response.Usage, summary.Usage);
                        if (summary.Usage != null)
                        {
                            LastRoundtripUsage = CopyUsage(summary.Usage);
                        }
                        response = summary;
                        response.Usage = total;
                    }
                    else
                    {
                        MEAI.UsageDetails total = response.Usage;
                        response = policy.BuildMaxErrorsResponse();
                        response.Usage = total;
                    }
                }
                else
                {
                    // WHY: MEAI returns the whole tool transcript; only the terminal assistant turn
                    // belongs in this completion, otherwise earlier prose and tool output repeat in UI.
                    List<MEAI.ChatMessage> visibleMessages = boundary.LastAssistantMessages;
                    HashSet<MEAI.AIContent> visibleContents = new(visibleMessages.SelectMany(message => message.Contents));
                    List<MEAI.AIContent> approvals = response.Messages.SelectMany(message => message.Contents)
                        .OfType<MEAI.ToolApprovalRequestContent>()
                        .Where(approval => !visibleContents.Contains(approval)).Cast<MEAI.AIContent>().ToList();
                    if (approvals.Count > 0)
                    {
                        // WHY: MEAI manufactures approval requests after the provider boundary returns.
                        // Preserve those typed controls without replaying prior assistant prose or tool results.
                        visibleMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, approvals));
                    }
                    response.Messages = visibleMessages;
                }

                TruncateResponseText(response, _settings.MaxResponseChars);
                if (policy.TurnEndingToolSucceeded)
                {
                    response.FinishReason = MEAI.ChatFinishReason.Stop;
                }
                return response;
            }
            finally
            {
                await boundary.CompleteApprovalsAsync(cancellationToken);
                LastExecutedToolCalls = policy.ExecutedTraces.ToArray();
                LastTurnEndedByTool = policy.TurnEndingToolSucceeded;
            }
        }

        private static MEAI.UsageDetails CopyUsage(MEAI.UsageDetails usage)
        {
            if (usage == null) return null;
            MEAI.UsageDetails copy = new();
            copy.Add(usage);
            return copy;
        }

        /// <summary>CoreAI policy hooks around MEAI's native history, invocation and usage loop.</summary>
        private sealed class NativePolicyClient : MEAI.DelegatingChatClient
        {
            private readonly SmartToolCallingChatClient _owner;
            private readonly ToolExecutionPolicy _policy;
            private List<MEAI.FunctionCallContent> _calls = new();
            private MEAI.ChatOptions _callOptions;
            private ToolExecutionPolicy.BatchToolCallResult _batch;
            private bool _batchReady;
            private bool _anySuccess;
            private int _requests;
            private int _missingRequired;
            private int _emptyAfterSuccess;
            private readonly List<MEAI.ChatMessage> _failedFeedback = new();
            private bool _previousAllFailed;
            private bool _removeFailedFeedback;
            private ToolExecutionPolicy.StreamedTurn _approvalTurn;
            public List<MEAI.AITool> FailureBindings { get; } = new();
            public List<MEAI.ChatMessage> LastAssistantMessages { get; private set; } = new();

            public NativePolicyClient(SmartToolCallingChatClient owner, ToolExecutionPolicy policy)
                : base(owner._innerClient)
            {
                _owner = owner;
                _policy = policy;
            }

            public async ValueTask<object> InvokeAsync(MEAI.FunctionInvocationContext context,
                CancellationToken cancellationToken)
            {
                if (_requests == 0)
                {
                    // WHY: MEAI invokes previously approved calls before its first provider request.
                    // Only callbacks validated by MEAI enter this path; raw approval history is never executed here.
                    _approvalTurn ??= _policy.BeginStreamedTurn();
                    ToolExecutionPolicy.ToolCallResult? immediate = await _policy.ExecuteStreamedAsync(
                        _approvalTurn, context.CallContent, context.Options, cancellationToken);
                    if (!immediate.HasValue)
                    {
                        await Task.WhenAll(_approvalTurn.InFlight);
                        if (!_approvalTurn.Slots[_approvalTurn.Slots.Count - 1].TryGet(
                                out ToolExecutionPolicy.ToolCallResult completed))
                        {
                            throw new InvalidOperationException("The approved tool did not publish its result.");
                        }
                        immediate = completed;
                    }
                    if (context.FunctionCallIndex == context.FunctionCount - 1)
                    {
                        await CompleteApprovalsAsync(cancellationToken);
                        context.Terminate = _policy.TurnEndingToolSucceeded || _policy.IsMaxErrorsReached;
                    }
                    return immediate.Value.Result;
                }
                if (!_batchReady)
                {
                    _batch = await _policy.ExecuteBatchAsync(_calls, _callOptions, cancellationToken);
                    _batchReady = true;
                    _anySuccess |= !_batch.AllFailed && !_batch.AllDuplicates;
                    _previousAllFailed = _batch.AllFailed;
                    _removeFailedFeedback = !_batch.AnyFailed && !_batch.AllDuplicates;
                }

                // WHY: Finish the MEAI result pairing for this batch before terminating its loop.
                context.Terminate = context.FunctionCallIndex == context.FunctionCount - 1 &&
                                    (_policy.TurnEndingToolSucceeded || _policy.IsMaxErrorsReached);
                return _batch.Results[context.FunctionCallIndex];
            }

            public async Task CompleteApprovalsAsync(CancellationToken cancellationToken)
            {
                if (_approvalTurn == null) return;
                ToolExecutionPolicy.StreamedTurn turn = _approvalTurn;
                _approvalTurn = null;
                ToolExecutionPolicy.BatchToolCallResult approved =
                    await _policy.CompleteStreamedTurnAsync(turn, cancellationToken);
                _anySuccess |= !approved.AllFailed && !approved.AllDuplicates;
                // WHY: Approval results may precede trailing user messages; they are not the last
                // assistant/tool pair used by the normal transient-error pruning optimization.
                _previousAllFailed = false;
                _removeFailedFeedback = false;
            }

            public override async Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                await CompleteApprovalsAsync(cancellationToken);
                List<MEAI.ChatMessage> messages = chatMessages as List<MEAI.ChatMessage> ?? chatMessages.ToList();
                if (_previousAllFailed && messages.Count >= 2)
                {
                    _failedFeedback.Add(messages[messages.Count - 2]);
                    _failedFeedback.Add(messages[messages.Count - 1]);
                }
                if (_removeFailedFeedback)
                {
                    ToolCallHistoryTrimmer.RemoveResolvedErrorFeedback(messages, _failedFeedback);
                }
                _previousAllFailed = false;
                _removeFailedFeedback = false;
                if (_owner._settings.MaxToolCallHistoryMessages > 0)
                {
                    _owner.TrimToolCallHistory(messages, _owner._settings.MaxToolCallHistoryMessages);
                }

                MEAI.UsageDetails retryUsage = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _requests++;
                    int limit = _owner._maxRoundtripsOverride ?? _owner._settings.MaxToolCallRoundtrips;
                    bool finalSummary = limit > 0 && _requests > limit;
                    MEAI.ChatOptions requestOptions = options;
                    if (finalSummary)
                    {
                        requestOptions = options?.Clone() ?? new MEAI.ChatOptions();
                        requestOptions.Tools = null;
                        requestOptions.ToolMode = MEAI.ChatToolMode.None;
                        messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User,
                            "Tool budget exhausted. Do not call any more tools. Summarize in plain text " +
                            "what you accomplished and what remains to be done."));
                    }
                    MEAI.ChatResponse response;
                    try
                    {
                        response = await base.GetResponseAsync(messages, requestOptions, cancellationToken);
                    }
                    catch (Exception ex) when (finalSummary && ex is not OperationCanceledException)
                    {
                        _owner._logger.Warn($"[SmartToolCall] Final summary failed: {ex.Message}", LogTag.Llm);
                        response = new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                            $"Agent stopped: exceeded maximum of {limit} tool-call roundtrips."));
                    }
                    if (response.Usage != null)
                    {
                        _owner.LastRoundtripUsage = CopyUsage(response.Usage);
                    }
                    retryUsage = LlmUsageAccumulator.Accumulate(retryUsage, response.Usage);
                    string text = ConcatenateAssistantTextContents(response);
                    HashSet<string> serverHandled = response.Messages.SelectMany(message => message.Contents)
                        .OfType<MEAI.FunctionResultContent>().Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
                    bool hasNativeCalls = FlattenAssistantContents(response).OfType<MEAI.FunctionCallContent>().Any();
                    _calls = FlattenAssistantContents(response).OfType<MEAI.FunctionCallContent>()
                        .Where(call => !call.InformationalOnly && !serverHandled.Contains(call.CallId) &&
                            requestOptions?.Tools?.FirstOrDefault(tool => tool.Name == call.Name)
                                ?.GetService(typeof(MEAI.ApprovalRequiredAIFunction)) == null).ToList();
                    if (_owner._allowTextShapedToolCalls && _calls.Count == 0 && !finalSummary &&
                        (requestOptions?.Tools?.Count ?? 0) > 0 &&
                        TryExtractToolCallsFromText(text, out List<MEAI.FunctionCallContent> extracted,
                            out string cleaned, _owner._logger, _owner._originalTools.Select(tool => tool.Name).ToArray()))
                    {
                        _calls = extracted;
                        List<MEAI.AIContent> contents = extracted.Cast<MEAI.AIContent>().ToList();
                        if (!string.IsNullOrWhiteSpace(cleaned)) contents.Add(new MEAI.TextContent(cleaned));
                        response.Messages = new List<MEAI.ChatMessage> { new(MEAI.ChatRole.Assistant, contents) };
                        text = cleaned;
                    }
                    if (finalSummary)
                    {
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            text = $"Agent stopped: exceeded maximum of {limit} tool-call roundtrips.";
                        }
                        response.Messages = new List<MEAI.ChatMessage> { new(MEAI.ChatRole.Assistant, text) };
                        _calls.Clear();
                    }
                    else if (_calls.Count == 0 && !hasNativeCalls && TryGetRequiredToolName(requestOptions?.ToolMode, out string required) &&
                             (requestOptions?.Tools?.Count ?? 0) > 0)
                    {
                        if (++_missingRequired <= _owner._maxConsecutiveErrors)
                        {
                            messages.AddRange(response.Messages);
                            messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, BuildMissingRequiredToolInstruction(required)));
                            continue;
                        }
                        response = BuildMissingRequiredToolResponse(required);
                    }
                    else if (_calls.Count == 0 && !hasNativeCalls && string.IsNullOrWhiteSpace(text) && _anySuccess &&
                             _emptyAfterSuccess++ < Math.Max(1, _owner._maxConsecutiveErrors))
                    {
                        messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User,
                            "Your last response had no text and no tool call. If the task is not finished, " +
                            "continue with the next tool call now. If it is finished, reply with a short summary."));
                        continue;
                    }
                    FailureBindings.Clear();
                    foreach (MEAI.FunctionCallContent call in _calls)
                    {
                        if (requestOptions?.Tools?.OfType<MEAI.AIFunction>().Any(f => f.Name == call.Name) != true &&
                            !FailureBindings.Any(f => f.Name == call.Name))
                        {
                            // WHY: MEAI normally bypasses FunctionInvoker for unknown names. This local
                            // dispatch marker routes the error through policy without granting any tool authority.
                            FailureBindings.Add(MEAI.AIFunctionFactory.Create((Func<string>)(() => "Error: unbound tool"),
                                new MEAI.AIFunctionFactoryOptions { Name = call.Name }));
                        }
                    }
                    _callOptions = requestOptions;
                    _batchReady = false;
                    LastAssistantMessages = response.Messages.Where(message => message.Role == MEAI.ChatRole.Assistant)
                        .Select(message => new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                            message.Contents.Where(content => content is not MEAI.FunctionCallContent).ToList())
                        { MessageId = message.MessageId }).ToList();
                    response.Usage = retryUsage;
                    return response;
                }
            }
        }

        private async Task<MEAI.ChatResponse> GetWebGlResponseAsync(
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
                _eventPublisher, _notifier, _actorId);
            LastRoundtripUsage = null;
            LastTurnEndedByTool = false;

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
                            if (capSummary.Usage != null)
                            {
                                LastRoundtripUsage = capSummary.Usage;
                            }

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
                    if (response.Usage != null)
                    {
                        LastRoundtripUsage = response.Usage;
                    }

                    List<MEAI.AIContent> allContents = FlattenAssistantContents(response);

                    List<MEAI.FunctionCallContent>
                        nativeCalls = allContents.OfType<MEAI.FunctionCallContent>().ToList();

                    // Text-mode fallback: providers that emit tool calls as JSON inside an assistant
                    // as the streaming loop, so behaviour is identical regardless of mode.
                    List<MEAI.FunctionCallContent> textCalls = new();
                    string cleanedAssistantText = null;
                    bool hasTextExtraction = false;
                    if (_allowTextShapedToolCalls &&
                        nativeCalls.Count == 0 &&
                        (iterationOptions?.Tools?.Count ?? 0) > 0)
                    {
                        string assistantText = ConcatenateAssistantTextContents(response);
                        if (!string.IsNullOrEmpty(assistantText) &&
                            TryExtractToolCallsFromText(assistantText, out textCalls, out cleanedAssistantText,
                                _logger, _originalTools.Select(tool => tool.Name).ToArray()))
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
                    // Ход из одних no-op'ов эха — не успех: ничего не исполнялось, и «подтолкнуть»
                    // модель после пустого ответа на его основании нельзя.
                    if (!batch.AllFailed && !batch.AllDuplicates)
                    {
                        anyToolCallSucceeded = true;
                    }

                    if (policy.IsMaxErrorsReached)
                    {
                        // Same graceful ending as the roundtrip cap: one tools-disabled turn so the
                        // model can explain what happened. If that turn yields no text, the fallback
                        // below is still plain prose for the user (BuildMaxErrorsResponse), never a
                        // raw service JSON shown as the assistant's reply.
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
                            if (errorSummary.Usage != null)
                            {
                                LastRoundtripUsage = errorSummary.Usage;
                            }

                            AttachCumulativeUsage(errorSummary, cumulativeUsage);
                            return errorSummary;
                        }

                        MEAI.ChatResponse maxErrorsResponse = policy.BuildMaxErrorsResponse();
                        AttachCumulativeUsage(maxErrorsResponse, cumulativeUsage);
                        return maxErrorsResponse;
                    }

                    // A tool that declares ILlmTool.EndsTurn and SUCCEEDED closes the turn here: its
                    // result ("card shown, waiting for the student") is not material the model can
                    // continue from, and one more roundtrip makes it react to an answer nobody gave yet.
                    // Whatever prose the model said BEFORE the call is carried out as the turn's text; a
                    // turn with no prose at all stays empty on purpose, because the caller already
                    // synthesizes a tool-only completion line from the traces.
                    if (policy.TurnEndingToolSucceeded)
                    {
                        string endedText = hasTextExtraction
                            ? cleanedAssistantText
                            : ConcatenateAssistantTextContents(response);
                        MEAI.ChatResponse endedResponse = new(new MEAI.ChatMessage(
                            MEAI.ChatRole.Assistant, endedText ?? string.Empty))
                        {
                            FinishReason = MEAI.ChatFinishReason.Stop
                        };
                        int endedMaxResponseChars = _settings.MaxResponseChars;
                        if (endedMaxResponseChars > 0)
                        {
                            TruncateResponseText(endedResponse, endedMaxResponseChars);
                        }

                        if (_settings.LogMeaiToolCallingSteps)
                        {
                            _logger.Info(
                                $"[SmartToolCall] Iteration {iteration}: a turn-ending tool succeeded; " +
                                "closing the turn without sending the tool result back to the model.",
                                LogTag.Llm);
                        }

                        AttachCumulativeUsage(endedResponse, cumulativeUsage);
                        return endedResponse;
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
                    else if (!batch.AnyFailed && !batch.AllDuplicates && pendingErrorFeedback.Count > 0)
                    {
                        // Только РЕАЛЬНЫЙ успех делает прежние ошибки устаревшими; эхо-ход ничего не
                        // исполнял и не отвечает на них.
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
                LastTurnEndedByTool = policy.TurnEndingToolSucceeded;
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
            out string cleanedText,
            ILog logger = null,
            IReadOnlyCollection<string> knownToolNames = null)
        {
            toolCalls = new List<MEAI.FunctionCallContent>();
            cleanedText = text ?? string.Empty;

            if (!LlmToolCallTextExtractor.TryExtract(text, knownToolNames, out List<LlmToolCallTextExtractor.Match> matches,
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

                    // WHY: shared chokepoint (LlmToolArgumentNormalizer) — the native policy
                    // and the skill resolver apply the same rule; a local copy would drift.
                    LlmToolArgumentNormalizer.NormalizeDictionaryValues(arguments);

                    string callId = $"stream_call_{m.Name}_{Guid.NewGuid():N}";
                    toolCalls.Add(new MEAI.FunctionCallContent(callId, m.Name, arguments));
                }
                catch (Exception ex)
                {
                    // WHY: A silently dropped extraction leaves the model believing it called the tool
                    // while nothing executed - leave a trace of exactly what was discarded and why.
                    (logger ?? NullLog.Instance).Warn(
                        $"[SmartToolCall] Dropped text-extracted tool call '{m.Name}': arguments JSON could not be " +
                        $"converted ({ex.Message}). Raw arguments: {m.ArgumentsJson}",
                        LogTag.Llm);
                }
            }

            return toolCalls.Count > 0;
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
                if (m?.Contents == null || m.Role != MEAI.ChatRole.Assistant)
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

            MEAI.ChatOptions clone = source.Clone();
            clone.ToolMode = MEAI.ChatToolMode.Auto;
            return clone;
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

            return _innerClient.GetService(serviceType, serviceKey);
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
                MEAI.ChatOptions summaryOptions = options?.Clone() ?? new MEAI.ChatOptions();
                summaryOptions.Tools = null;
                summaryOptions.ToolMode = MEAI.ChatToolMode.None;

#if UNITY_WEBGL && !UNITY_EDITOR
                MEAI.ChatResponse summary = await _innerClient
                    .GetResponseAsync(summaryMessages, summaryOptions, cancellationToken);
#else
                MEAI.ChatResponse summary = await _innerClient
                    .GetResponseAsync(summaryMessages, summaryOptions, cancellationToken)
                    .ConfigureAwait(false);
#endif

                if (summary?.Usage != null)
                {
                    LastRoundtripUsage = CopyUsage(summary.Usage);
                }
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

namespace CoreAI.Ai
{
    /// <summary>One endpoint policy for interpreting or removing tool calls written in assistant prose.</summary>
    public static class LlmToolChannelPolicy
    {
        public static bool InterpretsProse(bool supportsNativeToolCalling, bool? explicitNativeFallback,
            LlmToolChoiceMode toolMode = LlmToolChoiceMode.Auto)
        {
            return toolMode != LlmToolChoiceMode.None && (!supportsNativeToolCalling || explicitNativeFallback == true);
        }
    }
}
