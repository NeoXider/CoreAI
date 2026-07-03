#if !COREAI_NO_LLM
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Infrastructure.World;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif
using LuaLlmTool = CoreAI.Ai.LuaLlmTool;
using WorldLlmTool = CoreAI.Infrastructure.Llm.WorldLlmTool;
using MEAI = Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Microsoft.Extensions.AI client wrapper used by CoreAI orchestration.
    /// </summary>
    public sealed class MeaiLlmClient : ILlmClient
    {
        private readonly MEAI.IChatClient _innerClient;
        private readonly IGameLogger _logger;
        private readonly IAgentMemoryStore? _memoryStore;
        private readonly ICoreAISettings _settings;
        private readonly bool _supportsNativeToolCalling;
        private string _currentRoleId = "";

        /// <summary>
        /// When the gateway sends one long <c>delta.content</c> per frame, fan out to the consumer so UI and
        /// Initializes a new instance of the current component.
        /// </summary>
        private const int LiveUiStreamMaxCharsPerChunk = 48;
        private const int HybridToolJsonHeldTailMaxChars = 64 * 1024;

        public MeaiLlmClient(MEAI.IChatClient innerClient, IGameLogger logger, ICoreAISettings settings,
            IAgentMemoryStore? memoryStore = null, bool supportsNativeToolCalling = false)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _memoryStore = memoryStore;
            _supportsNativeToolCalling = supportsNativeToolCalling;
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _supportsNativeToolCalling;

        /// <summary>
        /// Creates an OpenAI-compatible HTTP-backed MEAI client.
        /// </summary>
        public static MeaiLlmClient CreateHttp(
            IOpenAiHttpSettings openAiSettings,
            ICoreAISettings settings,
            IGameLogger logger,
            IAgentMemoryStore? memoryStore = null)
        {
            if (openAiSettings == null)
            {
                throw new ArgumentNullException(nameof(openAiSettings));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            IOpenAiHttpTransport transport;
#if UNITY_WEBGL && !UNITY_EDITOR
            bool nativeStreaming = false;
            bool sameOrigin = false;
            if (settings is CoreAISettingsAsset asset)
            {
                nativeStreaming = asset.WebGlNativeStreaming;
                sameOrigin = asset.SameOriginCredentials;
            }
            // Native streaming uses the fetch SSE bridge for chat, but that transport cannot serve
            // non-streaming completions (internal agents like TeacherLessonFeedback). Wrap it in a
            // composite that routes non-streaming POSTs to UnityWebRequest so both paths work.
            transport = nativeStreaming
                ? (IOpenAiHttpTransport)new WebGlCompositeOpenAiTransport(
                    new FetchSseOpenAiTransport(sameOrigin),
                    new UnityWebRequestOpenAiTransport())
                : new UnityWebRequestOpenAiTransport();
#else
            transport = new HttpClientOpenAiTransport();
#endif
            MeaiOpenAiChatClient innerClient = new(openAiSettings, transport);
            return new MeaiLlmClient(innerClient, logger, settings, memoryStore, true);
        }

        /// <summary>
        /// Creates an OpenAI-compatible HTTP-backed MEAI client from a Unity settings asset.
        /// </summary>
        public static MeaiLlmClient CreateHttp(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore? memoryStore = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            HttpSettingsAdapter adapter = new(settings);
            return CreateHttp(adapter, settings, logger, memoryStore);
        }

        /// <summary>
        /// Creates a MEAI client that delegates completions to an LLMUnity agent.
        /// </summary>
        public static MeaiLlmClient CreateLlmUnity(
#if UNITY_WEBGL || !COREAI_HAS_LLMUNITY
            object unityAgent,
#else
            LLMAgent unityAgent,
#endif
            IGameLogger logger,
            ICoreAISettings settings,
            IAgentMemoryStore? memoryStore = null)
        {
#if UNITY_WEBGL || !COREAI_HAS_LLMUNITY
            throw new NotSupportedException("LLMUnity backend is not supported on WebGL.");
#else
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

            LlmUnityMeaiChatClient innerClient = new(unityAgent, logger);
            return new MeaiLlmClient(innerClient, logger, settings, memoryStore, false);
#endif
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            _currentRoleId = request.AgentRoleId ?? "Unknown";
            using LlmRequestContext.Scope ctxScope = LlmRequestContext.Begin(
                _currentRoleId,
                request.TraceId,
                EnsureIdempotencyKey(request));
            List<MEAI.AIFunction> aiTools = BuildAIFunctions(request.Tools, _currentRoleId);

            if (_settings.LogMeaiToolCallingSteps)
            {
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: SmartToolCallingChatClient created with {aiTools.Count} tools, max consecutive errors={_settings.MaxToolCallRetries}");
            }

            bool allowDuplicates = request.AllowDuplicateToolCalls ?? _settings.AllowDuplicateToolCalls;
            SmartToolCallingChatClient functionClient = new(_innerClient, Log.Instance, _settings, allowDuplicates,
                request.Tools, _currentRoleId, _settings.MaxToolCallRetries, request.TraceId,
                MessagePipeToolCallEventPublisher.Instance, CoreAiToolExecutionNotifier.Instance,
                request.MaxToolCallRoundtrips);

            List<MEAI.ChatMessage> chatMessages = BuildMeaiChatMessages(request);

            _logger.LogInfo(GameLogFeature.Llm,
                $"MeaiLlmClient: Initial prompt (system={chatMessages[0].Contents?.Count ?? 0} parts, user={chatMessages[1].Contents?.Count ?? 0} parts)");

            if (aiTools.Count > 0)
            {
                foreach (MEAI.AIFunction tool in aiTools)
                {
                    _logger.LogInfo(GameLogFeature.Llm, $"MeaiLlmClient: Tool: {tool.Name}");
                }
            }

            MEAI.ChatOptions chatOptions = new()
            {
                MaxOutputTokens = ResolveMaxOutputTokens(request.MaxOutputTokens)
            };
            if (request.SendTemperature)
            {
                chatOptions.Temperature = request.Temperature;
            }

            if (aiTools.Count > 0)
            {
                chatOptions.Tools = aiTools.Cast<MEAI.AITool>().ToList();
                ApplyForcedToolMode(chatOptions, request, aiTools);
            }

            MEAI.ChatResponse response;
            try
            {
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: Calling GetResponseAsync with {chatMessages.Count} messages, {aiTools.Count} tools");
                response = await functionClient
                    .GetResponseAsync(chatMessages, chatOptions, cancellationToken);
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: GetResponseAsync completed, has {response.Messages?.Count ?? 0} messages in response");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Llm, $"MeaiLlmClient: {ex.Message}");
                return FromException(ex);
            }

            if (response.Messages != null)
            {
                foreach (MEAI.ChatMessage msg in response.Messages)
                {
                    string role = msg.Role.ToString();
                    string content = msg.Contents != null
                        ? string.Join(" | ", msg.Contents.Select(c => c.ToString()))
                        : "(empty)";
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"MeaiLlmClient: Response message role={role}, content={content.Substring(0, Math.Min(200, content.Length))}...");
                }
            }

            if (_settings?.EnableMeaiDebugLogging == true)
            {
                _logger.LogInfo(GameLogFeature.Llm, $"MeaiLlmClient: Final response: {response.Text}");
                if (response.Usage != null)
                {
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"MeaiLlmClient: Tokens - Input: {response.Usage.InputTokenCount}, Output: {response.Usage.OutputTokenCount}, Total: {response.Usage.TotalTokenCount}");
                }
            }

            string text = response.Text;
            if (string.IsNullOrEmpty(text))
            {
                text = SmartToolCallingChatClient.ConcatenateAssistantTextContents(response);
            }

            text = SanitizeAssistantVisibleText(text, request);

            if (string.IsNullOrEmpty(text))
            {
                // Even though the FINAL assistant text is empty, earlier iterations of this same
                // non-streaming tool loop may have already executed real tools (e.g. a model that spawns
                // an object, then goes blank instead of summarizing). Carry those traces out here too -
                // same as the success path below and every terminal streaming chunk already does - so
                // callers (orchestrator history, benchmarks, telemetry) don't silently lose evidence that
                // a tool ran just because the wrapping turn ended in an empty-response error.
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = "Empty response from LLM",
                    ErrorCode = LlmErrorCode.EmptyResponse,
                    Model = ResolveModelName(),
                    ExecutedToolCalls = functionClient.LastExecutedToolCalls
                };
            }

            LlmCompletionResult result = new() { Ok = true, Content = text, Model = ResolveModelName() };
            if (response.Usage != null)
            {
                result.PromptTokens = (int)(response.Usage.InputTokenCount ?? 0);
                result.CompletionTokens = (int)(response.Usage.OutputTokenCount ?? 0);
                result.TotalTokens = (int)(response.Usage.TotalTokenCount ?? 0);
                (result.CacheReadTokens, result.CacheWriteTokens) =
                    ExtractCacheTokenCounts(response.Usage.AdditionalCounts);
            }

            // Carry the tool-call diagnostic out of the smart-tool client so the logging
            // decorator can render the same `tools=[...]` line for stream and non-stream paths.
            result.ExecutedToolCalls = functionClient.LastExecutedToolCalls;
            return result;
        }

        private LlmCompletionResult FromException(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = ex.Message,
                    ErrorCode = LlmErrorCode.Cancelled,
                    Model = ResolveModelName()
                };
            }

            if (ex is LlmClientException llmEx)
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = llmEx.Message,
                    ErrorCode = llmEx.ErrorCode,
                    HttpStatus = llmEx.HttpStatus,
                    RetryAfterSeconds = llmEx.RetryAfterSeconds,
                    ProviderErrorBody = llmEx.ProviderErrorBody,
                    Model = ResolveModelName()
                };
            }

            return new LlmCompletionResult
            {
                Ok = false,
                Error = ex.Message,
                ErrorCode = LlmErrorCode.ProviderError,
                Model = ResolveModelName()
            };
        }

        private static List<MEAI.ChatMessage> BuildMeaiChatMessages(LlmCompletionRequest request)
        {
            List<MEAI.ChatMessage> chatMessages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.System, request.SystemPrompt ?? "")
            };

            if (request.ChatHistory != null && request.ChatHistory.Count > 0)
            {
                foreach (MEAI.ChatMessage message in request.ChatHistory)
                {
                    chatMessages.Add(NormalizeChatHistoryMessageForProvider(message));
                }

                if (!string.IsNullOrWhiteSpace(request.UserPayload))
                {
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, request.UserPayload));
                }
            }
            else
            {
                chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, request.UserPayload ?? ""));
            }

            return chatMessages;
        }

        private static MEAI.ChatMessage NormalizeChatHistoryMessageForProvider(MEAI.ChatMessage message)
        {
            if (message.Role != MEAI.ChatRole.System)
            {
                return message;
            }

            string text = message.Text;
            if (string.IsNullOrEmpty(text) && message.Contents != null)
            {
                text = string.Join(
                    "\n",
                    message.Contents
                        .Select(content => content?.ToString())
                        .Where(content => !string.IsNullOrWhiteSpace(content)));
            }

            return new MEAI.ChatMessage(MEAI.ChatRole.User, "System context update:\n" + (text ?? string.Empty));
        }

        private string ResolveModelName()
        {
            return _settings switch
            {
                CoreAISettingsAsset asset => asset.ModelName,
                _ => ""
            };
        }

        /// <summary>
        /// Streams a completion and preserves CoreAI tool-call semantics across MEAI backends.
        /// <para>
        /// The streaming path handles three provider shapes:
        /// <list type="number">
        /// <item><description>native text chunks;</description></item>
        /// <item><description>tool calls surfaced as MEAI function calls;</description></item>
        /// <item><description>tool calls encoded as text-shaped JSON.</description></item>
        /// </list>
        /// </para>
        /// <para>
        /// Tool execution can require additional model turns, so the method yields assistant text,
        /// tool-call chunks, and final completion chunks as they become available.
        /// </para>
        /// <para>
        /// WebGL transports may use the browser fetch bridge while editor and standalone players use
        /// <c>UnityWebRequest</c>.
        /// </para>
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            _currentRoleId = request.AgentRoleId ?? "Unknown";
            using LlmRequestContext.Scope ctxScope = LlmRequestContext.Begin(
                _currentRoleId,
                request.TraceId,
                EnsureIdempotencyKey(request));

            List<MEAI.ChatMessage> chatMessages = BuildMeaiChatMessages(request);

            List<MEAI.AIFunction> aiTools = BuildAIFunctions(request.Tools, _currentRoleId);
            MEAI.ChatOptions chatOptions = new()
            {
                MaxOutputTokens = ResolveMaxOutputTokens(request.MaxOutputTokens)
            };
            if (request.SendTemperature)
            {
                chatOptions.Temperature = request.Temperature;
            }

            if (aiTools.Count > 0)
            {
                chatOptions.Tools = aiTools.Cast<MEAI.AITool>().ToList();
                ApplyForcedToolMode(chatOptions, request, aiTools);
            }

            _logger.LogInfo(GameLogFeature.Llm,
                $"MeaiLlmClient: Starting streaming with {chatMessages.Count} messages");

            int maxToolIterations = ResolveStreamingMaxToolRoundtrips(request.MaxToolCallRoundtrips, _settings);
            int toolIteration = 0;
            bool emittedAnyVisibleText = false;

            if (aiTools.Count == 0 && (request.Tools?.Count ?? 0) > 0)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"MeaiLlmClient: Streaming role='{_currentRoleId}' requested {request.Tools?.Count ?? 0} tool(s) but 0 AIFunction(s) were bound. " +
                    "Tool calls will be stripped from output without execution. Verify tool registration.");
            }

            // Shared policy for the entire streaming session
            bool allowDuplicates = request.AllowDuplicateToolCalls ?? _settings.AllowDuplicateToolCalls;
            ToolExecutionPolicy policy = new(Log.Instance, _settings, request.Tools, allowDuplicates,
                _currentRoleId, _settings.MaxToolCallRetries, request.TraceId,
                MessagePipeToolCallEventPublisher.Instance, CoreAiToolExecutionNotifier.Instance);
            string? pendingFailedToolRetryInstruction = null;
            int emptyResponsesAfterToolFailure = 0;
            int emptyResponsesAfterToolSuccess = 0;
            // True only after a batch with at least one GENUINE success (!batch.AllFailed) - an
            // all-failed batch must fall through to the emptyResponsesAfterToolFailure path above instead.
            bool anyToolCallSucceededInStream = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                toolIteration++;
                string streamModel = ResolveModelName();
                if (maxToolIterations > 0 && toolIteration > maxToolIterations)
                {
                    IReadOnlyList<LlmToolCallTrace> executedToolCalls = policy.ExecutedTraces.ToList();
                    // Any successful tool call is enough for a clean completion — visible text must NOT be
                    // required here: a ToolsOnly agent (e.g. the G6 free-build) never emits visible text, so
                    // requiring it sent every capped-but-successful build down the error path, which dropped
                    // the whole run's capture stats (turns/tool-calls/tokens all read 0 in the report while
                    // the world clearly held the build).
                    if (executedToolCalls.Any(t => t.Success))
                    {
                        _logger.LogWarning(GameLogFeature.Llm,
                            "MeaiLlmClient: Streaming tool loop reached the iteration guard after successful tool calls; completing without surfacing an internal guard error.");
                        yield return new LlmStreamChunk
                        {
                            IsDone = true,
                            Text = string.Empty,
                            ExecutedToolCalls = executedToolCalls
                        };
                        yield break;
                    }

                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = "tool loop exceeded max iterations",
                        ExecutedToolCalls = executedToolCalls
                    };
                    yield break;
                }

                // ForcedToolMode applies ONLY to the first iteration.
                // After we feed tool results back to the model, it must decide naturally
                // tool-choice constraint would loop forever (model is forced to re-call a tool,
                // we feed its result, model is forced again, ...).
                MEAI.ChatOptions iterationOptions = chatOptions;
                if (toolIteration > 1 && chatOptions.ToolMode != null &&
                    chatOptions.ToolMode is not MEAI.AutoChatToolMode)
                {
                    iterationOptions = CloneOptionsWithAutoToolMode(chatOptions);
                }

                ThinkBlockStreamFilter thinkFilter = new();
                List<string> visibleChunks = new();
                System.Text.StringBuilder iterationVisible = new();
                System.Text.StringBuilder rawIterationText = new();
                List<MEAI.FunctionCallContent> nativeToolCalls = new();
                // Execute-as-you-stream: when the SSE parser surfaces a complete native tool call
                // mid-stream (see MeaiOpenAiChatClient's DrainCompleted), run it IMMEDIATELY instead
                // of holding the whole turn's calls for one end-of-stream batch — a long build turn
                // then mutates the world call by call while the model is still generating. The turn
                // still closes with the exact batch protocol (assistant tool_calls + one tool-role
                // results message), so the model sees no difference.
                ToolExecutionPolicy.StreamedTurn streamedTurn = null;
                // Local mirror of the streamed turn's executed-call count. StreamedTurn.Count is
                // internal to CoreAI.Core (not visible from this assembly), and the mid-stream
                // failure handler below must know whether any call already mutated the world.
                int streamedExecutedCallCount = 0;
                int chunkCount = 0;
                MEAI.UsageDetails iterationUsage = null;
                bool toolsDeclared = (request.Tools?.Count ?? 0) > 0;
                bool fullIterationBuffer =
                    toolsDeclared && request.BufferFullStreamingIterationWhenToolsDeclared == true;
                bool hybridToolJsonHold = toolsDeclared && !fullIterationBuffer;
                bool streamLiveNoTools = !toolsDeclared;
                bool unboundToolsRequested = toolsDeclared && aiTools.Count == 0;
                int hybridRawExclusiveEndEmitted = 0;
                bool emittedHybridHoldTypingHint = false;
                bool emittedToolProgressTypingHint = false;
                bool streamedVisibleToConsumer = false;
                bool hybridToolJsonCandidateOverflow = false;

                if (hybridToolJsonHold)
                {
                    if (unboundToolsRequested)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            "MeaiLlmClient: Unbound-tool streaming - tools are declared but no MEAI AIFunctions are bound for this role. " +
                            "Prose may stream incrementally; output from the opening `{` of a text-shaped tool call is held until the JSON object completes or the turn ends, then stripped if applicable.");
                    }
                    else
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            "MeaiLlmClient: Hybrid tool-json hold (bound tools) - assistant text streams only through the safe prefix; " +
                            "from the opening `{` of a text-shaped tool call, output is held until the JSON object completes or the turn ends, then stripped before user-visible emission.");
                    }

                    yield return new LlmStreamChunk { BufferedStreamingNoToolBinding = true };
                }

                // Hybrid tool-json hold, on-the-fly (Kilo/Cline-style): given the accumulated visible text so
                // far, emit every prose range that is already resolvable as live tokens, hide every completed
                // text-shaped tool-call JSON span, and stop before the first still-incomplete `{...}` object.
                // Prose AFTER a closed tool JSON resumes streaming live - only the tool-call JSON is hidden,
                // never the surrounding prose/preamble. `hybridRawExclusiveEndEmitted` advances over both
                // emitted prose and skipped JSON, so downstream suffix reconciliation stays in sync.
                IEnumerable<LlmStreamChunk> DrainHybridSafeSegments(string full)
                {
                    int scanStart = Math.Min(hybridRawExclusiveEndEmitted, full.Length);
                    string scan = full.Substring(scanStart);
                    List<HybridProseSegment> segments = GetHybridSafeSegments(scan, out int relativeSafeEnd);
                    int safeEnd = scanStart + relativeSafeEnd;
                    int heldLength = scan.Length - relativeSafeEnd;
                    if (heldLength > HybridToolJsonHeldTailMaxChars)
                    {
                        hybridToolJsonCandidateOverflow = true;
                        _logger.LogWarning(GameLogFeature.Llm,
                            $"MeaiLlmClient: Held text-shaped tool-call candidate exceeded {HybridToolJsonHeldTailMaxChars} chars; failing closed instead of streaming the raw JSON tail.");
                    }

                    foreach (HybridProseSegment segment in segments)
                    {
                        int absoluteStart = scanStart + segment.Start;
                        if (absoluteStart + segment.Length <= hybridRawExclusiveEndEmitted)
                        {
                            continue; // already consumed in a previous update
                        }

                        int from = Math.Max(absoluteStart, hybridRawExclusiveEndEmitted);
                        int len = absoluteStart + segment.Length - from;
                        if (len <= 0)
                        {
                            continue;
                        }

                        if (!segment.IsToolJson)
                        {
                            string prose = full.Substring(from, len);
                            foreach (string part in SplitForLiveUiStreaming(prose, LiveUiStreamMaxCharsPerChunk))
                            {
                                emittedAnyVisibleText = true;
                                streamedVisibleToConsumer = true;
                                yield return new LlmStreamChunk { Text = part };
                            }
                        }
                        // else: completed tool-call JSON span - hidden (never emitted), only the cursor advances.
                    }

                    if (safeEnd > hybridRawExclusiveEndEmitted)
                    {
                        hybridRawExclusiveEndEmitted = safeEnd;
                    }

                    if (safeEnd < full.Length && !emittedHybridHoldTypingHint)
                    {
                        emittedHybridHoldTypingHint = true;
                        _logger.LogInfo(GameLogFeature.Llm,
                            "MeaiLlmClient: Tool-json hold started - emitting only the safe prose; the trailing `{...}` is hidden behind the tool progress indicator until the object closes or the stream ends.");
                        yield return new LlmStreamChunk
                        {
                            BufferedStreamingNoToolBinding = true,
                            BufferedStreamingUseToolProgressHint = true
                        };
                    }
                }

                // Manual enumeration instead of `await foreach`: MoveNextAsync sits inside a
                // try/catch (yield-free, so legal in an iterator) to intercept mid-stream
                // transport failures AFTER tool calls have already executed. Without this, an
                // abandoned streamedTurn skipped CompleteStreamedTurn — no consecutive-error
                // record, no echo-signature registration — and a partially-mutated world
                // surfaced to callers as a clean, blind-retryable transport failure. The
                // chunk-yielding body stays OUTSIDE the catch; the `await using` declaration
                // disposes the enumerator on every exit path (try/finally is yield-safe).
                IAsyncEnumerable<MEAI.ChatResponseUpdate> updateStream =
                    _innerClient.GetStreamingResponseAsync(chatMessages, iterationOptions, cancellationToken);
                await using IAsyncEnumerator<MEAI.ChatResponseUpdate> updateEnumerator =
                    updateStream.GetAsyncEnumerator(cancellationToken);
                string? midStreamFailureMessage = null;
                while (true)
                {
                    MEAI.ChatResponseUpdate update;
                    try
                    {
                        if (!await updateEnumerator.MoveNextAsync())
                        {
                            break;
                        }

                        update = updateEnumerator.Current;
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (Exception ex)
                    {
                        if (streamedTurn == null || streamedExecutedCallCount == 0)
                        {
                            // No tool has mutated the world yet: preserve legacy behavior and let
                            // the exception escape (FallbackLlmClientDecorator relies on
                            // pre-first-chunk failures propagating so it can fall back).
                            throw;
                        }

                        // Tool calls already ran mid-stream: close the turn so the
                        // consecutive-error counter records once and the turn signature is
                        // registered (echo guard, batch parity) before the failure surfaces.
                        policy.CompleteStreamedTurn(streamedTurn);
                        _logger.LogWarning(GameLogFeature.Llm,
                            $"MeaiLlmClient: Streaming transport failed after {streamedExecutedCallCount} tool call(s) " +
                            "already executed mid-stream; finalizing the partial turn instead of abandoning it " +
                            $"({ex.GetType().Name}: {ex.Message}).");

                        if (ex is OperationCanceledException)
                        {
                            // Consumer is gone; turn state is finalized, cancellation propagates as before.
                            throw;
                        }

                        // A partially-applied turn must surface as a graded failure WITH traces,
                        // not as a retryable transport exception a caller might blindly replay.
                        midStreamFailureMessage =
                            $"stream failed after {streamedExecutedCallCount} executed tool call(s): {ex.Message}";
                        break;
                    }

                    int nativeToolCountBeforeUpdate = nativeToolCalls.Count;
                    if (update.Contents != null)
                    {
                        foreach (MEAI.AIContent content in update.Contents)
                        {
                            if (content is MEAI.FunctionCallContent fcc)
                            {
                                nativeToolCalls.Add(fcc);
                                if (aiTools.Count > 0)
                                {
                                    streamedTurn ??= policy.BeginStreamedTurn();
                                    await policy.ExecuteStreamedAsync(
                                        streamedTurn, fcc, chatOptions, cancellationToken);
                                    streamedExecutedCallCount++;
                                }
                            }
                            else if (content is MEAI.UsageContent usageContent && usageContent.Details != null)
                            {
                                iterationUsage = usageContent.Details;
                            }
                        }
                    }

                    if (aiTools.Count > 0 &&
                        nativeToolCalls.Count > nativeToolCountBeforeUpdate &&
                        !emittedToolProgressTypingHint)
                    {
                        emittedToolProgressTypingHint = true;
                        yield return new LlmStreamChunk
                        {
                            BufferedStreamingNoToolBinding = true,
                            BufferedStreamingUseToolProgressHint = true
                        };
                    }

                    string raw = GetStreamingUpdateText(update);
                    if (string.IsNullOrEmpty(raw))
                    {
                        continue;
                    }

                    rawIterationText.Append(raw);
                    string visible = thinkFilter.ProcessChunk(raw);
                    if (string.IsNullOrEmpty(visible))
                    {
                        continue;
                    }

                    chunkCount++;
                    iterationVisible.Append(visible);
                    visibleChunks.Add(visible);
                    if (streamLiveNoTools)
                    {
                        foreach (string part in SplitForLiveUiStreaming(visible, LiveUiStreamMaxCharsPerChunk))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            emittedAnyVisibleText = true;
                            yield return new LlmStreamChunk { Text = part };
                        }

                        streamedVisibleToConsumer = true;
                    }
                    else if (hybridToolJsonHold)
                    {
                        foreach (LlmStreamChunk part in DrainHybridSafeSegments(iterationVisible.ToString()))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return part;
                        }
                    }
                }

                if (midStreamFailureMessage != null)
                {
                    // Mirror the max-errors terminal chunk: the consumer gets a graded failure
                    // carrying every executed trace so the partial world mutation is visible and
                    // attributable instead of looking like a retryable transport error.
                    LlmStreamChunk streamFailChunk = new()
                    {
                        IsDone = true,
                        Error = midStreamFailureMessage,
                        ExecutedToolCalls = policy.ExecutedTraces.ToList()
                    };
                    ApplyStreamingUsageFields(streamFailChunk, iterationUsage, streamModel);
                    yield return streamFailChunk;
                    yield break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                string tail = thinkFilter.Flush();
                if (!string.IsNullOrEmpty(tail))
                {
                    iterationVisible.Append(tail);
                    visibleChunks.Add(tail);
                    if (streamLiveNoTools)
                    {
                        foreach (string part in SplitForLiveUiStreaming(tail, LiveUiStreamMaxCharsPerChunk))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            emittedAnyVisibleText = true;
                            yield return new LlmStreamChunk { Text = part };
                        }

                        streamedVisibleToConsumer = true;
                    }
                    else if (hybridToolJsonHold)
                    {
                        foreach (LlmStreamChunk part in DrainHybridSafeSegments(iterationVisible.ToString()))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return part;
                        }
                    }
                }

                string visibleText = iterationVisible.ToString();
                bool hiddenThinkToolCall = toolsDeclared &&
                                           ContainsCompleteThinkBlockToolCall(rawIterationText.ToString());
                if (string.IsNullOrWhiteSpace(visibleText) &&
                    nativeToolCalls.Count == 0 &&
                    !string.IsNullOrWhiteSpace(pendingFailedToolRetryInstruction) &&
                    emptyResponsesAfterToolFailure < Math.Max(1, _settings.MaxToolCallRetries))
                {
                    emptyResponsesAfterToolFailure++;
                    chatMessages.Add(new MEAI.ChatMessage(
                        MEAI.ChatRole.User,
                        pendingFailedToolRetryInstruction));
                    _logger.LogWarning(GameLogFeature.Llm,
                        "MeaiLlmClient: Streaming model returned an empty response after a failed tool call; " +
                        $"feeding explicit tool-error retry instruction ({emptyResponsesAfterToolFailure}/{Math.Max(1, _settings.MaxToolCallRetries)}).");
                    continue;
                }

                // Empty response after a genuinely SUCCESSFUL tool call (not a failed one, handled above)
                // mid-task. Left alone this used to end the whole request immediately even when a generous
                // roundtrip budget (e.g. the G6 free-build's 1000-call cap, meant for a long iterative
                // session) had barely been used - the model did something, then trailed off. Nudge it to
                // continue or say it is finished, bounded the same way tool-error retries are. Gated on
                // anyToolCallSucceededInStream so a genuinely disengaged first response (no tool ever
                // attempted), or a batch that failed entirely, still falls through to the normal
                // empty-response path below instead of being nudged as if it were progress.
                if (string.IsNullOrWhiteSpace(visibleText) &&
                    nativeToolCalls.Count == 0 &&
                    anyToolCallSucceededInStream &&
                    emptyResponsesAfterToolSuccess < Math.Max(1, _settings.MaxToolCallRetries))
                {
                    emptyResponsesAfterToolSuccess++;
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User,
                        "Your last response had no text and no tool call. If the task is not finished, " +
                        "continue with the next tool call now. If it is finished, reply with a short summary."));
                    _logger.LogWarning(GameLogFeature.Llm,
                        "MeaiLlmClient: Streaming model returned an empty response after a successful tool call; " +
                        $"nudging to continue ({emptyResponsesAfterToolSuccess}/{Math.Max(1, _settings.MaxToolCallRetries)}).");
                    continue;
                }

                // === Path 1: Native tool calls from SSE delta.tool_calls ===
                if (nativeToolCalls.Count > 0 && aiTools.Count > 0)
                {
                    if (!emittedToolProgressTypingHint)
                    {
                        emittedToolProgressTypingHint = true;
                        yield return new LlmStreamChunk
                        {
                            BufferedStreamingNoToolBinding = true,
                            BufferedStreamingUseToolProgressHint = true
                        };
                    }

                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            $"MeaiLlmClient: Streaming detected {nativeToolCalls.Count} NATIVE tool call(s), executing...");
                    }

                    // Emit any visible text that preceded the tool calls (skip if already streamed token-by-token).
                    if (!streamedVisibleToConsumer && !string.IsNullOrWhiteSpace(visibleText))
                    {
                        emittedAnyVisibleText = true;
                        yield return new LlmStreamChunk { Text = visibleText };
                    }

                    List<MEAI.AIContent> assistantContents = nativeToolCalls.Cast<MEAI.AIContent>().ToList();
                    if (!string.IsNullOrWhiteSpace(visibleText))
                    {
                        assistantContents.Add(new MEAI.TextContent(visibleText));
                    }

                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, assistantContents));

                    // Streamed turn: every call already ran the moment it arrived — close the turn
                    // (one consecutive-error record + echo signature, batch parity) and reuse its
                    // results. Otherwise fall back to the classic end-of-stream batch.
                    ToolExecutionPolicy.BatchToolCallResult batch = streamedTurn != null
                        ? policy.CompleteStreamedTurn(streamedTurn)
                        : await policy.ExecuteBatchAsync(nativeToolCalls, chatOptions, cancellationToken);
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, batch.Results));
                    if (!batch.AllFailed)
                    {
                        anyToolCallSucceededInStream = true;
                    }
                    pendingFailedToolRetryInstruction = batch.AllFailed
                        ? BuildFailedToolRetryInstruction(policy.ExecutedTraces)
                        : null;
                    if (!batch.AnyFailed)
                    {
                        emptyResponsesAfterToolFailure = 0;
                    }

                    if (policy.IsMaxErrorsReached)
                    {
                        LlmStreamChunk errChunk = new()
                        {
                            IsDone = true,
                            Error = "max consecutive tool errors reached",
                            ExecutedToolCalls = policy.ExecutedTraces.ToList()
                        };
                        ApplyStreamingUsageFields(errChunk, iterationUsage, streamModel);
                        yield return errChunk;
                        yield break;
                    }

                    continue;
                }

                // === Path 2: Text-based tool call extraction (primary for local models) ===
                // Run extraction whenever the *request* declared tools, not just when the
                // backend successfully bound AIFunctions. This way, a tool that was requested
                // but couldn't be bound (e.g., MemoryLlmTool with a null store) still has its
                // Resolve and cache required local values.
                bool requestHadTools = toolsDeclared;
                if (requestHadTools && TryExtractToolCallsFromText(visibleText,
                        out List<MEAI.FunctionCallContent> toolCalls, out string cleanedText))
                {
                    if (aiTools.Count == 0)
                    {
                        // Tool was requested but no AIFunction is bound. Strip the JSON, warn,
                        // must not loop forever asking a model that has no tools to call them.
                        foreach (MEAI.FunctionCallContent fc in toolCalls)
                        {
                            policy.RecordSyntheticTrace(fc.Name ?? "", false, 0d, "missing");
                        }

                        _logger.LogWarning(GameLogFeature.Llm,
                            $"MeaiLlmClient: Streaming saw {toolCalls.Count} text-shaped tool call(s) but no AIFunction is bound for this role. " +
                            "Stripping JSON and emitting cleaned text. Check tool registration / IAgentMemoryStore wiring.");

                        if (!string.IsNullOrWhiteSpace(cleanedText))
                        {
                            if (!streamedVisibleToConsumer)
                            {
                                emittedAnyVisibleText = true;
                                yield return new LlmStreamChunk { Text = cleanedText };
                            }
                            else if (hybridToolJsonHold && hybridRawExclusiveEndEmitted > 0)
                            {
                                string suffix = GetHybridUnemittedSuffix(
                                    visibleText, hybridRawExclusiveEndEmitted);
                                if (!string.IsNullOrEmpty(suffix))
                                {
                                    foreach (string part in SplitForLiveUiStreaming(suffix,
                                                 LiveUiStreamMaxCharsPerChunk))
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        emittedAnyVisibleText = true;
                                        yield return new LlmStreamChunk { Text = part };
                                    }
                                }
                            }
                        }

                        LlmStreamChunk doneStrip = new()
                        {
                            IsDone = true,
                            Text = string.Empty,
                            ExecutedToolCalls = policy.ExecutedTraces.ToList()
                        };
                        ApplyStreamingUsageFields(doneStrip, iterationUsage, streamModel);
                        yield return doneStrip;
                        _logger.LogInfo(GameLogFeature.Llm,
                            $"MeaiLlmClient: Streaming completed (text-only, JSON stripped, length={cleanedText?.Length ?? 0})");
                        yield break;
                    }

                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            $"MeaiLlmClient: Streaming detected {toolCalls.Count} text-extracted tool call(s), executing...");
                    }

                    if (!streamedVisibleToConsumer && !string.IsNullOrWhiteSpace(cleanedText))
                    {
                        emittedAnyVisibleText = true;
                        yield return new LlmStreamChunk { Text = cleanedText };
                    }
                    else if (streamedVisibleToConsumer && hybridToolJsonHold && hybridRawExclusiveEndEmitted > 0 &&
                             !string.IsNullOrWhiteSpace(cleanedText))
                    {
                        string suffix = GetHybridUnemittedSuffix(
                            visibleText, hybridRawExclusiveEndEmitted);
                        if (!string.IsNullOrEmpty(suffix))
                        {
                            foreach (string part in SplitForLiveUiStreaming(suffix, LiveUiStreamMaxCharsPerChunk))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                emittedAnyVisibleText = true;
                                yield return new LlmStreamChunk { Text = part };
                            }
                        }
                    }

                    List<MEAI.AIContent> assistantContents = toolCalls.Cast<MEAI.AIContent>().ToList();
                    if (!string.IsNullOrWhiteSpace(cleanedText))
                    {
                        assistantContents.Add(new MEAI.TextContent(cleanedText));
                    }

                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, assistantContents));

                    if (!emittedToolProgressTypingHint)
                    {
                        emittedToolProgressTypingHint = true;
                        yield return new LlmStreamChunk
                        {
                            BufferedStreamingNoToolBinding = true,
                            BufferedStreamingUseToolProgressHint = true
                        };
                    }

                    // Execute through shared policy (same as non-streaming)
                    ToolExecutionPolicy.BatchToolCallResult batch =
                        await policy.ExecuteBatchAsync(toolCalls, chatOptions, cancellationToken);
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, batch.Results));
                    if (!batch.AllFailed)
                    {
                        anyToolCallSucceededInStream = true;
                    }
                    pendingFailedToolRetryInstruction = batch.AllFailed
                        ? BuildFailedToolRetryInstruction(policy.ExecutedTraces)
                        : null;
                    if (!batch.AnyFailed)
                    {
                        emptyResponsesAfterToolFailure = 0;
                    }

                    if (policy.IsMaxErrorsReached)
                    {
                        LlmStreamChunk errChunk2 = new()
                        {
                            IsDone = true,
                            Error = "max consecutive tool errors reached",
                            ExecutedToolCalls = policy.ExecutedTraces.ToList()
                        };
                        ApplyStreamingUsageFields(errChunk2, iterationUsage, streamModel);
                        yield return errChunk2;
                        yield break;
                    }

                    continue;
                }

                if (hiddenThinkToolCall)
                {
                    _logger.LogWarning(GameLogFeature.Llm,
                        "MeaiLlmClient: Streaming dropped a complete text-shaped tool call inside a <think> block. " +
                        "The hidden reasoning text was not streamed or executed; move tool-call JSON outside <think>.");
                }

                if (requestHadTools && TryBuildMalformedTextToolCall(
                        visibleText,
                        request.Tools,
                        aiTools,
                        out MEAI.FunctionCallContent malformedToolCall,
                        out string malformedCleanedText,
                        out string malformedReason,
                        hybridToolJsonCandidateOverflow))
                {
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"MeaiLlmClient: Streaming held malformed/truncated text-shaped tool JSON ({malformedReason}); failing closed without emitting it as assistant text.");

                    if (!streamedVisibleToConsumer && !string.IsNullOrWhiteSpace(malformedCleanedText))
                    {
                        string sanitizedMalformedCleaned = SanitizeAssistantVisibleText(malformedCleanedText, request);
                        if (!string.IsNullOrWhiteSpace(sanitizedMalformedCleaned))
                        {
                            emittedAnyVisibleText = true;
                            yield return new LlmStreamChunk { Text = sanitizedMalformedCleaned };
                        }
                    }

                    if (aiTools.Count == 0)
                    {
                        policy.RecordSyntheticTrace(malformedToolCall.Name ?? "", false, 0d, "parse-error",
                            "Text-shaped tool JSON was malformed/truncated and no AIFunction is bound.");
                        LlmStreamChunk doneParseStrip = new()
                        {
                            IsDone = true,
                            Text = string.Empty,
                            ExecutedToolCalls = policy.ExecutedTraces.ToList()
                        };
                        ApplyStreamingUsageFields(doneParseStrip, iterationUsage, streamModel);
                        yield return doneParseStrip;
                        yield break;
                    }

                    if (!emittedToolProgressTypingHint)
                    {
                        emittedToolProgressTypingHint = true;
                        yield return new LlmStreamChunk
                        {
                            BufferedStreamingNoToolBinding = true,
                            BufferedStreamingUseToolProgressHint = true
                        };
                    }

                    List<MEAI.FunctionCallContent> malformedCalls = new() { malformedToolCall };
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                        malformedCalls.Cast<MEAI.AIContent>().ToList()));
                    ToolExecutionPolicy.BatchToolCallResult malformedBatch =
                        await policy.ExecuteBatchAsync(malformedCalls, chatOptions, cancellationToken);
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, malformedBatch.Results));
                    if (!malformedBatch.AllFailed)
                    {
                        anyToolCallSucceededInStream = true;
                    }
                    pendingFailedToolRetryInstruction = malformedBatch.AllFailed
                        ? BuildFailedToolRetryInstruction(policy.ExecutedTraces)
                        : null;

                    if (policy.IsMaxErrorsReached)
                    {
                        LlmStreamChunk errChunk3 = new()
                        {
                            IsDone = true,
                            Error = "max consecutive tool errors reached",
                            ExecutedToolCalls = policy.ExecutedTraces.ToList()
                        };
                        ApplyStreamingUsageFields(errChunk3, iterationUsage, streamModel);
                        yield return errChunk3;
                        yield break;
                    }

                    continue;
                }

                if (!streamedVisibleToConsumer)
                {
                    string sanitizedFull = SanitizeAssistantVisibleText(visibleText, request);
                    if (!string.Equals(visibleText, sanitizedFull, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(sanitizedFull))
                        {
                            emittedAnyVisibleText = true;
                            yield return new LlmStreamChunk { Text = sanitizedFull };
                        }
                    }
                    else
                    {
                        foreach (string chunk in visibleChunks)
                        {
                            emittedAnyVisibleText = true;
                            yield return new LlmStreamChunk { Text = chunk };
                        }
                    }
                }
                else if (hybridToolJsonHold && hybridRawExclusiveEndEmitted < visibleText.Length)
                {
                    string rest = visibleText.Substring(hybridRawExclusiveEndEmitted);
                    if (!string.IsNullOrEmpty(rest))
                    {
                        string restSan = SanitizeAssistantVisibleText(rest, request);
                        if (!string.IsNullOrEmpty(restSan))
                        {
                            foreach (string part in SplitForLiveUiStreaming(restSan, LiveUiStreamMaxCharsPerChunk))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                emittedAnyVisibleText = true;
                                yield return new LlmStreamChunk { Text = part };
                            }
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                LlmStreamChunk terminal = new()
                {
                    IsDone = true,
                    Text = string.Empty,
                    ExecutedToolCalls = policy.ExecutedTraces.ToList()
                };
                ApplyStreamingUsageFields(terminal, iterationUsage, streamModel);
                yield return terminal;
                string sanitizedForLog = SanitizeAssistantVisibleText(visibleText, request);
                int emittedLen = sanitizedForLog.Length;
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: Streaming completed ({chunkCount} raw deltas, raw length={visibleText.Length}, sanitized length={emittedLen}, streamed live={streamedVisibleToConsumer})");
                yield break;
            }
        }

        private static string BuildFailedToolRetryInstruction(IReadOnlyList<LlmToolCallTrace> traces)
        {
            LlmToolCallTrace failed = default;
            bool found = false;
            if (traces != null)
            {
                for (int i = traces.Count - 1; i >= 0; i--)
                {
                    if (!traces[i].Success)
                    {
                        failed = traces[i];
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                return
                    "The previous tool call failed. Inspect the tool error, fix the arguments or code, and retry with a corrected tool call. Do not return an empty response.";
            }

            string name = string.IsNullOrWhiteSpace(failed.Name) ? "tool" : failed.Name.Trim();
            string detail = ExtractToolTraceMessage(failed.Detail);
            return string.IsNullOrWhiteSpace(detail)
                ? $"The previous `{name}` tool call failed. Fix the arguments or code and retry with a corrected tool call. Do not return an empty response."
                : $"The previous `{name}` tool call failed with this error: {detail}. Fix the arguments or code and retry with a corrected tool call. Do not return an empty response.";
        }

        private static string ExtractToolTraceMessage(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return "";
            }

            string trimmed = detail.Trim();
            try
            {
                JObject json = JObject.Parse(trimmed);
                JToken token = json["message"] ?? json["Message"] ?? json["error"] ?? json["Error"];
                if (token != null)
                {
                    string message = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message.Trim();
                    }
                }
            }
            catch
            {
                // Tool details may be plain text.
            }

            const int maxChars = 240;
            return trimmed.Length <= maxChars ? trimmed : trimmed.Substring(0, maxChars) + "...";
        }

        private static void ApplyStreamingUsageFields(LlmStreamChunk chunk, MEAI.UsageDetails usage, string model)
        {
            if (chunk == null || usage == null)
            {
                return;
            }

            chunk.PromptTokens = (int)(usage.InputTokenCount ?? 0);
            chunk.CompletionTokens = (int)(usage.OutputTokenCount ?? 0);
            chunk.TotalTokens = (int)(usage.TotalTokenCount ?? 0);
            (chunk.CacheReadTokens, chunk.CacheWriteTokens) =
                ExtractCacheTokenCounts(usage.AdditionalCounts);
            if (!string.IsNullOrEmpty(model))
            {
                chunk.Model = model;
            }
        }

        internal static (int CacheReadTokens, int CacheWriteTokens) ExtractCacheTokenCounts(
            MEAI.AdditionalPropertiesDictionary<long>? additionalCounts)
        {
            if (additionalCounts == null || additionalCounts.Count == 0)
            {
                return (0, 0);
            }

            long cacheRead = 0;
            long cacheWrite = 0;
            foreach (KeyValuePair<string, long> count in additionalCounts)
            {
                string key = count.Key;
                if (string.IsNullOrEmpty(key) ||
                    key.IndexOf("cache", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool isRead = key.IndexOf("read", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              key.IndexOf("cached", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isWrite = key.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               key.IndexOf("creation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               key.IndexOf("create", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isRead)
                {
                    cacheRead += Math.Max(0, count.Value);
                }

                if (isWrite)
                {
                    cacheWrite += Math.Max(0, count.Value);
                }
            }

            return (ClampTokenCount(cacheRead), ClampTokenCount(cacheWrite));
        }

        private static int ClampTokenCount(long value)
        {
            if (value <= 0)
            {
                return 0;
            }

            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        /// <summary>
        /// OpenAI-style streaming usually fills <see cref="MEAI.ChatResponseUpdate.Text"/>; some stacks only append
        /// <see cref="MEAI.TextContent"/> to <see cref="MEAI.ChatResponseUpdate.Contents"/>.
        /// </summary>
        private static string GetStreamingUpdateText(MEAI.ChatResponseUpdate update)
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                return update.Text;
            }

            if (update.Contents == null || update.Contents.Count == 0)
            {
                return string.Empty;
            }

            foreach (MEAI.AIContent c in update.Contents)
            {
                if (c is MEAI.TextContent tc && !string.IsNullOrEmpty(tc.Text))
                {
                    return tc.Text;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Splits a single provider-visible string into smaller outward chunks. Surrogate pairs are not split.
        /// </summary>
        private static IEnumerable<string> SplitForLiveUiStreaming(string visible, int maxChars)
        {
            if (string.IsNullOrEmpty(visible))
            {
                yield break;
            }

            if (maxChars <= 0 || visible.Length <= maxChars)
            {
                yield return visible;
                yield break;
            }

            int i = 0;
            while (i < visible.Length)
            {
                int take = Math.Min(maxChars, visible.Length - i);
                while (take > 1 && char.IsHighSurrogate(visible[i + take - 1]) && i + take < visible.Length)
                {
                    take--;
                }

                yield return visible.Substring(i, take);
                i += take;
            }
        }

        private string SanitizeAssistantVisibleText(string text, LlmCompletionRequest request)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string stripped = LlmResponseSanitizer.StripLeadingSystemPromptEcho(text, request.SystemPrompt);
            if (!string.Equals(text, stripped, StringComparison.Ordinal))
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"MeaiLlmClient: Removed leading system-prompt echo from assistant text ({text.Length} -> {stripped.Length} chars).");
            }

            return stripped;
        }

        internal static int ResolveStreamingMaxToolRoundtrips(int? requestMaxToolCallRoundtrips,
            ICoreAISettings settings)
        {
            return requestMaxToolCallRoundtrips ?? settings.MaxToolCallRoundtrips;
        }

        internal static bool ContainsCompleteThinkBlockToolCall(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return false;
            }

            foreach (Match match in Regex.Matches(rawText, @"<think\b[^>]*>([\s\S]*?)</think>",
                         RegexOptions.IgnoreCase))
            {
                string hidden = match.Groups.Count > 1 ? match.Groups[1].Value : string.Empty;
                if (string.IsNullOrWhiteSpace(hidden))
                {
                    continue;
                }

                if (FindToolCallJsonSpans(StripCodeBlocks(hidden)).Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool TryBuildMalformedTextToolCall(
            string text,
            IReadOnlyList<ILlmTool> requestedTools,
            IReadOnlyList<MEAI.AIFunction> aiTools,
            out MEAI.FunctionCallContent toolCall,
            out string cleanedText,
            out string reason,
            bool forceMalformed = false)
        {
            toolCall = null;
            cleanedText = text ?? string.Empty;
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string search = StripCodeBlocks(text);
            int incompleteStart = GetFirstIncompleteBraceStart(search);
            if (incompleteStart >= 0 && incompleteStart < search.Length)
            {
                string candidate = text.Substring(incompleteStart);
                if (forceMalformed || LooksLikeJsonObjectPrefix(candidate))
                {
                    string name = ResolveMalformedToolName(candidate, requestedTools, aiTools);
                    toolCall = CreateMalformedToolCall(name, candidate);
                    cleanedText = text.Substring(0, incompleteStart).TrimEnd();
                    reason = forceMalformed
                        ? "candidate-too-large"
                        : "incomplete-json-object";
                    return true;
                }
            }

            foreach (JsonSpan span in FindBalancedObjectSpans(search))
            {
                if (span.Start >= text.Length || span.Start + span.Length > text.Length)
                {
                    continue;
                }

                string candidate = text.Substring(span.Start, span.Length);
                if (!LooksLikeToolCallObject(candidate))
                {
                    continue;
                }

                if (TryParseToolCallJson(candidate))
                {
                    continue;
                }

                string name = ResolveMalformedToolName(candidate, requestedTools, aiTools);
                toolCall = CreateMalformedToolCall(name, candidate);
                cleanedText = (text.Substring(0, span.Start) +
                               text.Substring(span.Start + span.Length)).Trim();
                reason = "malformed-tool-json";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pattern-aware tool call extraction from text.
        /// Matches JSON objects that contain both "name" and "arguments" keys (outside fenced ``` blocks),
        /// then falls back to <see cref="LlmToolCallTextExtractor"/> for pseudo-syntax memory writes
        /// (e.g. Qwen: <c>Action=write content="..."</c>).
        /// </summary>
        internal static bool TryExtractToolCallsFromText(
            string text,
            out List<MEAI.FunctionCallContent> toolCalls,
            out string cleanedText)
        {
            toolCalls = new List<MEAI.FunctionCallContent>();
            cleanedText = text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // Strip fenced code blocks to avoid matching JSON inside them when giving **non-tool** examples.
            string textForSearch = StripCodeBlocks(text);

            // Find all balanced JSON objects that look like tool calls (only outside fenced ``` blocks;
            // StripCodeBlocks blanks ```...``` so examples like ```json {"name":...} ``` are not matched).
            List<JsonSpan> candidates = FindToolCallJsonSpans(textForSearch);

            if (candidates.Count == 0)
            {
                return TryPortableToolExtract(text, out toolCalls, out cleanedText);
            }

            // Build cleaned text by removing all found tool-call JSON spans (from original text)
            // *hidden* from search, the positions still correspond to the original text.
            System.Text.StringBuilder cleanBuilder = new(text.Length);
            int lastEnd = 0;
            foreach (JsonSpan span in candidates)
            {
                // Verify span is valid in original text too
                if (span.Start >= text.Length || span.Start + span.Length > text.Length)
                {
                    continue;
                }

                string originalFragment = text.Substring(span.Start, span.Length);

                // Re-validate the fragment in the original text
                if (!IsValidToolCallJson(originalFragment))
                {
                    continue;
                }

                try
                {
                    JObject json = JObject.Parse(originalFragment);
                    string functionName = json["name"]?.ToString()?.Trim();
                    // Support both "arguments" and "arguments_json" (Qwen3.5 via LLMUnity).
                    JToken argsToken = json["arguments"] ?? json["arguments_json"];
                    if (string.IsNullOrWhiteSpace(functionName) || argsToken == null)
                    {
                        continue;
                    }

                    // If args is a string (e.g. "arguments_json": "{...}"), parse it as JSON.
                    string argsStr = argsToken.Type == JTokenType.String
                        ? argsToken.ToString()
                        : argsToken.ToString(Formatting.None);

                    Dictionary<string, object?> arguments =
                        JsonConvert.DeserializeObject<Dictionary<string, object?>>(argsStr)
                        ?? new Dictionary<string, object?>();

                    // Normalize JObject/JArray values to strings for MEAI compatibility.
                    NormalizeJTokenValues(arguments);

                    string callId = $"stream_call_{functionName}_{Guid.NewGuid():N}";
                    toolCalls.Add(new MEAI.FunctionCallContent(callId, functionName, arguments));

                    cleanBuilder.Append(text, lastEnd, span.Start - lastEnd);
                    lastEnd = span.Start + span.Length;
                }
                catch
                {
                }
            }

            if (toolCalls.Count == 0)
            {
                return TryPortableToolExtract(text, out toolCalls, out cleanedText);
            }

            // Append remaining text after last tool call
            if (lastEnd < text.Length)
            {
                cleanBuilder.Append(text, lastEnd, text.Length - lastEnd);
            }

            cleanedText = cleanBuilder.ToString().Trim();
            return true;
        }

        /// <summary>
        /// Delegates to <see cref="LlmToolCallTextExtractor"/> for parity with <see cref="SmartToolCallingChatClient"/>
        /// and picks up pseudo-syntax memory writes (Qwen / llama.cpp) when brace-count JSON extraction finds nothing.
        /// </summary>
        private static bool TryPortableToolExtract(
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
                    Dictionary<string, object?> arguments =
                        JsonConvert.DeserializeObject<Dictionary<string, object?>>(m.ArgumentsJson)
                        ?? new Dictionary<string, object?>();

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
        /// Strips embedded tool-call JSON (<c>name</c> + <c>arguments</c>) from assistant text for chat UI.
        /// Used when the orchestrator could not separate tool JSON from prose in the streaming path
        /// (same rules as <see cref="TryExtractToolCallsFromText"/>; does not execute tools).
        /// </summary>
        public static string StripEmbeddedToolCallJsonForDisplay(string assistantText)
        {
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return assistantText ?? string.Empty;
            }

            return TryExtractToolCallsFromText(assistantText, out _, out string cleaned) ? cleaned : assistantText;
        }

        /// <summary>Removes fenced code blocks (```...```) from text to prevent false positive tool call detection.</summary>
        internal static string StripCodeBlocks(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            // Replace ```...``` blocks with whitespace of the same length to preserve positions
            return Regex.Replace(text, @"```[\s\S]*?```", m => new string(' ', m.Length));
        }

        /// <summary>Checks if a JSON string looks like a tool call (has "name" and "arguments" or "arguments_json").</summary>
        internal static bool IsValidToolCallJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            // Quick heuristic before parsing: must contain both key patterns
            return json.Contains("\"name\"") &&
                   (json.Contains("\"arguments\"") || json.Contains("\"arguments_json\""));
        }

        /// <summary>
        /// Converts any <see cref="JObject"/>/<see cref="JArray"/> values in the dictionary to
        /// their JSON string representation. MEAI's <c>AIFunctionFactory</c> cannot convert
        /// nested Newtonsoft tokens directly.
        /// </summary>
        private static void NormalizeJTokenValues<T>(Dictionary<string, T> arguments)
        {
            if (arguments == null)
            {
                return;
            }

            List<string> keys = new(arguments.Keys);
            foreach (string key in keys)
            {
                object val = arguments[key];
                if (val is JObject jo)
                {
                    arguments[key] = (T)(object)jo.ToString(Formatting.None);
                }
                else if (val is JArray ja)
                {
                    arguments[key] = (T)(object)ja.ToString(Formatting.None);
                }
            }
        }

        /// <summary>
        /// Find balanced JSON object spans in text that look like tool calls.
        /// Uses brace-counting to find balanced {} regions, then validates structure.
        /// </summary>
        internal static List<JsonSpan> FindToolCallJsonSpans(string text)
        {
            List<JsonSpan> spans = new();
            if (string.IsNullOrEmpty(text))
            {
                return spans;
            }

            int i = 0;
            while (i < text.Length)
            {
                int braceStart = text.IndexOf('{', i);
                if (braceStart < 0)
                {
                    break;
                }

                // Try to find matching closing brace
                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int j = braceStart;

                for (; j < text.Length; j++)
                {
                    char c = text[j];

                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (c == '\\' && inString)
                    {
                        escaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }

                    if (inString)
                    {
                        continue;
                    }

                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            string candidate = text.Substring(braceStart, j - braceStart + 1);
                            if (IsValidToolCallJson(candidate))
                            {
                                spans.Add(new JsonSpan { Start = braceStart, Length = j - braceStart + 1 });
                            }

                            break;
                        }
                    }
                }

                i = depth == 0 && j < text.Length ? j + 1 : braceStart + 1;
            }

            return spans;
        }

        private static List<JsonSpan> FindBalancedObjectSpans(string text)
        {
            List<JsonSpan> spans = new();
            if (string.IsNullOrEmpty(text))
            {
                return spans;
            }

            int i = 0;
            while (i < text.Length)
            {
                int braceStart = text.IndexOf('{', i);
                if (braceStart < 0)
                {
                    break;
                }

                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int j = braceStart;

                for (; j < text.Length; j++)
                {
                    char c = text[j];
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (c == '\\' && inString)
                    {
                        escaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }

                    if (inString)
                    {
                        continue;
                    }

                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            spans.Add(new JsonSpan { Start = braceStart, Length = j - braceStart + 1 });
                            break;
                        }
                    }
                }

                i = depth == 0 && j < text.Length ? j + 1 : braceStart + 1;
            }

            return spans;
        }

        private static bool LooksLikeJsonObjectPrefix(string candidate)
        {
            string trimmed = candidate?.TrimStart() ?? string.Empty;
            return trimmed.StartsWith("{", StringComparison.Ordinal) &&
                   (trimmed.Contains("\"name\"", StringComparison.Ordinal) ||
                    trimmed.Contains("\"arguments\"", StringComparison.Ordinal) ||
                    trimmed.Contains("\"arguments_json\"", StringComparison.Ordinal) ||
                    Regex.IsMatch(trimmed, @"^\{\s*""[^""]+""\s*:"));
        }

        private static bool LooksLikeToolCallObject(string candidate)
        {
            return !string.IsNullOrWhiteSpace(candidate) &&
                   candidate.Contains("\"name\"", StringComparison.Ordinal) &&
                   (candidate.Contains("\"arguments\"", StringComparison.Ordinal) ||
                    candidate.Contains("\"arguments_json\"", StringComparison.Ordinal));
        }

        private static bool TryParseToolCallJson(string candidate)
        {
            try
            {
                JObject json = JObject.Parse(candidate);
                string functionName = json["name"]?.ToString()?.Trim();
                JToken argsToken = json["arguments"] ?? json["arguments_json"];
                if (string.IsNullOrWhiteSpace(functionName) || argsToken == null)
                {
                    return false;
                }

                string argsStr = argsToken.Type == JTokenType.String
                    ? argsToken.ToString()
                    : argsToken.ToString(Formatting.None);

                return JsonConvert.DeserializeObject<Dictionary<string, object?>>(argsStr) != null;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveMalformedToolName(
            string candidate,
            IReadOnlyList<ILlmTool> requestedTools,
            IReadOnlyList<MEAI.AIFunction> aiTools)
        {
            Match match = Regex.Match(candidate ?? string.Empty, @"""name""\s*:\s*""([^""]+)""");
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                return match.Groups[1].Value.Trim();
            }

            string boundName = aiTools?.FirstOrDefault()?.Name;
            if (!string.IsNullOrWhiteSpace(boundName))
            {
                return boundName;
            }

            string requestedName = requestedTools?.FirstOrDefault()?.Name;
            return string.IsNullOrWhiteSpace(requestedName)
                ? "tool_call_parse_error"
                : requestedName;
        }

        private static MEAI.FunctionCallContent CreateMalformedToolCall(string name, string raw)
        {
            Dictionary<string, object?> arguments = new()
            {
                { ToolCallArgumentMarkers.RawArgumentsKey, raw ?? string.Empty },
                { ToolCallArgumentMarkers.ParseErrorKey, true }
            };
            return new MEAI.FunctionCallContent($"stream_parse_error_{Guid.NewGuid():N}", name, arguments);
        }

        /// <summary>
        /// For hybrid tool-json streaming (bound or unbound): after <see cref="TryExtractToolCallsFromText"/> yields
        /// <paramref name="cleanedText"/>, emit only the suffix not already streamed as the safe raw prefix
        /// (<paramref name="hybridRawExclusiveEndEmitted"/> bytes of <paramref name="visibleText"/>).
        /// </summary>
        internal static string? GetCleanedTextSuffixAfterHybridPrefix(
            string cleanedText,
            string visibleText,
            int hybridRawExclusiveEndEmitted)
        {
            if (string.IsNullOrWhiteSpace(cleanedText) || hybridRawExclusiveEndEmitted <= 0)
            {
                return null;
            }

            string rawPrefix = visibleText.Substring(0,
                Math.Min(hybridRawExclusiveEndEmitted, visibleText.Length));
            string rawPrefixTrimEnd = rawPrefix.TrimEnd();
            int skipLen = 0;
            if (cleanedText.StartsWith(rawPrefix, StringComparison.Ordinal))
            {
                skipLen = rawPrefix.Length;
            }
            else if (rawPrefixTrimEnd.Length > 0 &&
                     cleanedText.StartsWith(rawPrefixTrimEnd, StringComparison.Ordinal))
            {
                skipLen = rawPrefixTrimEnd.Length;
            }

            if (skipLen > 0 && cleanedText.Length > skipLen)
            {
                return cleanedText.Substring(skipLen);
            }

            if (skipLen == 0)
            {
                return cleanedText;
            }

            return null;
        }

        /// <summary>
        /// On-the-fly hybrid hold: prose in <paramref name="visibleText"/> up to
        /// <paramref name="hybridRawExclusiveEndEmitted"/> has already been streamed live (with any tool-call
        /// JSON in that region hidden). After the turn ends and tool calls are extracted, the only prose that
        /// still needs to be emitted is the JSON-stripped remainder of the held tail
        /// (<c>visibleText[hybridRawExclusiveEndEmitted..]</c>). This returns that suffix, or <c>null</c> when
        /// the held tail was only tool-call JSON / whitespace.
        /// </summary>
        internal static string? GetHybridUnemittedSuffix(string visibleText, int hybridRawExclusiveEndEmitted)
        {
            if (string.IsNullOrEmpty(visibleText) || hybridRawExclusiveEndEmitted >= visibleText.Length)
            {
                return null;
            }

            int from = Math.Max(0, hybridRawExclusiveEndEmitted);
            string heldTail = visibleText.Substring(from);
            if (string.IsNullOrWhiteSpace(heldTail))
            {
                return null;
            }

            // Strip any tool-call JSON that lived in the held tail so only prose remains.
            string strippedTail = TryExtractToolCallsFromText(heldTail, out _, out string cleanedTail)
                ? cleanedTail
                : heldTail;

            return string.IsNullOrEmpty(strippedTail) ? null : strippedTail;
        }

        /// <summary>
        /// One contiguous range of <c>text</c> that is safe to stream live in hybrid tool-json hold:
        /// either prose (outside any tool-call JSON) or skipped tool-call JSON that must be hidden.
        /// </summary>
        internal readonly struct HybridProseSegment
        {
            public HybridProseSegment(int start, int length, bool isToolJson)
            {
                Start = start;
                Length = length;
                IsToolJson = isToolJson;
            }

            /// <summary>Inclusive start index into the source text.</summary>
            public int Start { get; }

            /// <summary>Length of the segment in characters.</summary>
            public int Length { get; }

            /// <summary>When <c>true</c> this span is a complete text-shaped tool-call JSON object to hide (never emit).</summary>
            public bool IsToolJson { get; }
        }

        /// <summary>
        /// Hybrid tool-json hold, Kilo/Cline-style: instead of holding everything from the first <c>{</c>
        /// to the end of the turn, this walks <paramref name="text"/> and returns the ranges that can be
        /// resolved <em>now</em> — prose ranges (safe to stream as visible tokens) and completed text-shaped
        /// tool-call JSON spans (hidden) — up to <paramref name="exclusiveSafeEnd"/>. Everything at or after
        /// <paramref name="exclusiveSafeEnd"/> is an <em>incomplete</em> object that may still become a tool
        /// call and must stay held until it closes or the turn ends.
        /// <para>
        /// This is what keeps prose streaming live <em>after</em> a tool call: once a text-shaped tool JSON
        /// closes, the prose that follows it is immediately a safe prose segment again.
        /// </para>
        /// Indices align with <paramref name="text"/> because <see cref="StripCodeBlocks"/> preserves length.
        /// </summary>
        internal static List<HybridProseSegment> GetHybridSafeSegments(string text, out int exclusiveSafeEnd)
        {
            List<HybridProseSegment> segments = new();
            exclusiveSafeEnd = 0;
            if (string.IsNullOrEmpty(text))
            {
                return segments;
            }

            string search = StripCodeBlocks(text);

            // Hold boundary = start of the first STILL-INCOMPLETE (unclosed) brace that could grow into a
            // tool call. Unlike GetExclusiveEndForSafeUnboundRawStreaming, completed tool-call JSON spans do
            // NOT lower this boundary - they are hidden in place so prose after their closing `}` keeps
            // streaming live.
            int holdBoundary = GetFirstIncompleteBraceStart(search);

            // Completed text-shaped tool-call JSON spans (to hide) that fall before the hold boundary.
            List<JsonSpan> toolSpans = FindToolCallJsonSpans(search);

            int cursor = 0;
            foreach (JsonSpan span in toolSpans.OrderBy(s => s.Start))
            {
                if (span.Start >= holdBoundary)
                {
                    break;
                }

                if (span.Start > cursor)
                {
                    segments.Add(new HybridProseSegment(cursor, span.Start - cursor, isToolJson: false));
                }

                segments.Add(new HybridProseSegment(span.Start, span.Length, isToolJson: true));
                cursor = span.Start + span.Length;
            }

            if (holdBoundary > cursor)
            {
                segments.Add(new HybridProseSegment(cursor, holdBoundary - cursor, isToolJson: false));
            }

            exclusiveSafeEnd = holdBoundary;
            return segments;
        }

        /// <summary>
        /// Returns the index of the first still-open (unbalanced) <c>{</c> in <paramref name="search"/> — the
        /// point from which output must be held because the object may still grow into a tool call. Returns
        /// <c>search.Length</c> when every brace is balanced (nothing pending). <paramref name="search"/> must
        /// already be code-block-stripped so indices align with the source text.
        /// </summary>
        private static int GetFirstIncompleteBraceStart(string search)
        {
            if (string.IsNullOrEmpty(search))
            {
                return 0;
            }

            int i = 0;
            while (i < search.Length)
            {
                int braceStart = search.IndexOf('{', i);
                if (braceStart < 0)
                {
                    break;
                }

                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int j = braceStart;

                for (; j < search.Length; j++)
                {
                    char c = search[j];

                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (c == '\\' && inString)
                    {
                        escaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }

                    if (inString)
                    {
                        continue;
                    }

                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                }

                if (depth != 0)
                {
                    // Unbalanced from braceStart to end of text: hold from here.
                    return braceStart;
                }

                i = j + 1;
            }

            return search.Length;
        }

        /// <summary>
        /// For hybrid tool-json streaming (bound or unbound): largest <paramref name="text"/> prefix that can be emitted as raw
        /// without splitting a text-shaped tool JSON (complete <see cref="FindToolCallJsonSpans"/> hits) or
        /// an incomplete JSON object that may become a tool call.
        /// Indices align with <paramref name="text"/> because <see cref="StripCodeBlocks"/> preserves length.
        /// </summary>
        internal static int GetExclusiveEndForSafeUnboundRawStreaming(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            string search = StripCodeBlocks(text);
            int minHold = search.Length;

            foreach (JsonSpan span in FindToolCallJsonSpans(search))
            {
                if (span.Start < minHold)
                {
                    minHold = span.Start;
                }
            }

            int i = 0;
            while (i < search.Length)
            {
                int braceStart = search.IndexOf('{', i);
                if (braceStart < 0)
                {
                    break;
                }

                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int j = braceStart;

                for (; j < search.Length; j++)
                {
                    char c = search[j];

                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (c == '\\' && inString)
                    {
                        escaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }

                    if (inString)
                    {
                        continue;
                    }

                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                }

                if (depth == 0 && j < search.Length)
                {
                    i = j + 1;
                }
                else if (depth != 0)
                {
                    minHold = Math.Min(minHold, braceStart);
                    i = braceStart + 1;
                }
                else
                {
                    i = braceStart + 1;
                }
            }

            return minHold;
        }

        /// <summary>Represents a span of JSON text within a larger string.</summary>
        internal struct JsonSpan
        {
            public int Start;
            public int Length;
        }

        /// <summary>
        /// Maps <see cref="LlmCompletionRequest.ForcedToolMode"/> onto
        /// <see cref="MEAI.ChatOptions.ToolMode"/>. Called only when the request actually
        /// contains tools and an explicit forced-tool setting.
        /// <para>
        /// Multi-round streaming: the caller is responsible for resetting the mode to
        /// <see cref="MEAI.ChatToolMode.Auto"/> after the first iteration via
        /// <see cref="CloneOptionsWithAutoToolMode"/>; otherwise the model would be forced
        /// to keep emitting tool calls forever (it's pinned to "RequireAny" each turn).
        /// </para>
        /// </summary>
        private void ApplyForcedToolMode(MEAI.ChatOptions options, LlmCompletionRequest request,
            IReadOnlyList<MEAI.AIFunction> aiTools)
        {
            switch (request.ForcedToolMode)
            {
                case LlmToolChoiceMode.Auto:
                    return;
                case LlmToolChoiceMode.None:
                    options.ToolMode = MEAI.ChatToolMode.None;
                    return;
                case LlmToolChoiceMode.RequireAny:
                    options.ToolMode = MEAI.ChatToolMode.RequireAny;
                    return;
                case LlmToolChoiceMode.RequireSpecific:
                    string targetName = request.RequiredToolName?.Trim();
                    if (string.IsNullOrEmpty(targetName))
                    {
                        _logger.LogWarning(GameLogFeature.Llm,
                            "MeaiLlmClient: ForcedToolMode=RequireSpecific but RequiredToolName is empty - falling back to RequireAny.");
                        options.ToolMode = MEAI.ChatToolMode.RequireAny;
                        return;
                    }

                    bool isAvailable = false;
                    for (int i = 0; i < aiTools.Count; i++)
                    {
                        if (string.Equals(aiTools[i].Name, targetName, StringComparison.Ordinal))
                        {
                            isAvailable = true;
                            break;
                        }
                    }

                    if (!isAvailable)
                    {
                        _logger.LogWarning(GameLogFeature.Llm,
                            $"MeaiLlmClient: ForcedToolMode=RequireSpecific('{targetName}') but tool is not registered for this role - falling back to RequireAny.");
                        options.ToolMode = MEAI.ChatToolMode.RequireAny;
                        return;
                    }

                    options.ToolMode = MEAI.ChatToolMode.RequireSpecific(targetName);
                    return;
            }
        }

        /// <summary>
        /// Resolves the effective <c>MaxOutputTokens</c> for a single MEAI <c>ChatOptions</c>:
        /// a positive per-request value wins; an EXPLICIT per-request <c>0</c> means "no limit"
        /// (no <c>max_tokens</c> is sent — reasoning models can think as long as they need without
        /// the thinking budget eating the answer budget); <c>null</c> falls back to
        /// <see cref="ICoreAISettings.MaxTokens"/> when it is positive; otherwise <c>null</c> so the
        /// provider uses its own default. HTTP backends honour <c>null</c> by omitting
        /// <c>max_tokens</c> from the request; the LLMUnity backend honours it by resetting
        /// <c>numPredict</c> to its unbounded <c>-1</c> sentinel on every request instead of
        /// retaining a previously applied cap.
        /// </summary>
        private int? ResolveMaxOutputTokens(int? perRequest)
        {
            if (perRequest.HasValue)
            {
                if (perRequest.Value > 0)
                {
                    return perRequest.Value;
                }

                if (perRequest.Value == 0)
                {
                    return null; // explicit "unlimited" — do not fall back to the settings cap
                }
            }

            int settingsValue = _settings?.MaxTokens ?? 0;
            return settingsValue > 0 ? settingsValue : (int?)null;
        }

        /// <summary>
        /// Returns a shallow copy of <paramref name="source"/> with <see cref="MEAI.ChatToolMode.Auto"/>.
        /// Used in the streaming loop after the first iteration so the model isn't forced
        /// to keep emitting tool calls after each tool result is fed back.
        /// </summary>
        private static MEAI.ChatOptions CloneOptionsWithAutoToolMode(MEAI.ChatOptions source)
        {
            MEAI.ChatOptions clone = new()
            {
                Temperature = source.Temperature,
                MaxOutputTokens = source.MaxOutputTokens,
                Tools = source.Tools,
                ToolMode = MEAI.ChatToolMode.Auto
            };
            return clone;
        }

        private List<MEAI.AIFunction> BuildAIFunctions(IReadOnlyList<ILlmTool>? tools, string roleId)
        {
            List<MEAI.AIFunction> result = new();
            if (tools == null)
            {
                return result;
            }

            foreach (ILlmTool tool in AiToolOrder.Canonical(tools))
            {
                try
                {
                    switch (tool)
                    {
                        case MemoryLlmTool:
                            if (_memoryStore != null)
                            {
                                MemoryTool mt = new(_memoryStore, roleId);
                                result.Add(mt.CreateAIFunction());
                            }
                            else
                            {
                                // Memory tool was requested for this role but the orchestrator
                                // could not bind a store. The model may still emit memory tool
                                // JSON; the streaming/non-streaming loops will strip it from the
                                // visible reply but cannot persist anything. Warn loudly so the
                                // operator knows the tool is silently a no-op.
                                _logger.LogWarning(GameLogFeature.Llm,
                                    $"MeaiLlmClient: 'memory' tool requested for role '{roleId}' but IAgentMemoryStore is null. " +
                                    "Tool calls to 'memory' will be stripped from output without execution. " +
                                    "Wire IAgentMemoryStore (CoreServicesInstaller) to enable persistence.");
                            }

                            break;
                        case DelegateLlmTool dt:
                            result.Add(dt.CreateAIFunction());
                            break;
                        case IAIFunctionLlmTool functionTool:
                            result.Add(functionTool.CreateAIFunction());
                            break;
                        case IAIFunctionsLlmTool functionTools:
                            result.AddRange(functionTools.CreateAIFunctions());
                            break;
                        default:
                            _logger.LogWarning(GameLogFeature.Llm,
                                $"MeaiLlmClient: Tool '{tool.Name}' does not implement a MEAI function binding interface and cannot be exposed to the model.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(GameLogFeature.Llm, $"MeaiLlmClient: Tool '{tool.Name}' failed: {ex.Message}");
                }
            }

            return result;
        }

        private sealed class HttpSettingsAdapter : IOpenAiHttpSettings
        {
            private readonly CoreAISettingsAsset _s;

            public HttpSettingsAdapter(CoreAISettingsAsset s)
            {
                _s = s;
            }

            public string ApiBaseUrl => _s.ApiBaseUrl;
            public string ApiKey => _s.ApiKey;
            public string AuthorizationHeader => "";
            public string Model => _s.ModelName;
            public float Temperature => _s.Temperature;
            public int RequestTimeoutSeconds => _s.EffectiveHttpRequestTimeoutSeconds;
            public int MaxTokens => _s.MaxTokens;
            public string ExtraBodyJson => "";
            public LlmReasoningMode ReasoningMode => _s.ReasoningMode;
            public int ThinkingBudgetTokens => _s.ThinkingBudgetTokens;
            public bool LogLlmInput => _s.LogLlmInput;
            public bool LogLlmOutput => _s.LogLlmOutput;
            public bool EnableHttpDebugLogging => _s.EnableHttpDebugLogging;

            public IRequestHeaderProvider? HeaderProvider => null;
        }

        /// <summary>
        /// One stable idempotency key per <paramref name="request"/> instance so HTTP retries
        /// (e.g. <see cref="RefreshOnUnauthorizedDecorator"/>) reuse <c>Idempotency-Key</c>.
        /// </summary>
        private static string EnsureIdempotencyKey(LlmCompletionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrEmpty(request.IdempotencyKey))
            {
                request.IdempotencyKey = Guid.NewGuid().ToString("N");
            }

            return request.IdempotencyKey;
        }
    }
}
#endif
