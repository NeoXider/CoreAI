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
            transport = nativeStreaming
                ? (IOpenAiHttpTransport)new FetchSseOpenAiTransport(sameOrigin)
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
                MessagePipeToolCallEventPublisher.Instance, CoreAiToolExecutionNotifier.Instance);

            List<MEAI.ChatMessage> chatMessages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.System, request.SystemPrompt ?? "")
            };

            if (request.ChatHistory != null && request.ChatHistory.Count > 0)
            {
                chatMessages.AddRange(request.ChatHistory);
                if (!string.IsNullOrWhiteSpace(request.UserPayload))
                {
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, request.UserPayload));
                }
            }
            else
            {
                chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, request.UserPayload ?? ""));
            }

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
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = "Empty response from LLM",
                    ErrorCode = LlmErrorCode.EmptyResponse,
                    Model = ResolveModelName()
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

            List<MEAI.ChatMessage> chatMessages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.System, request.SystemPrompt ?? "")
            };

            if (request.ChatHistory != null && request.ChatHistory.Count > 0)
            {
                chatMessages.AddRange(request.ChatHistory);
                if (!string.IsNullOrWhiteSpace(request.UserPayload))
                {
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, request.UserPayload));
                }
            }
            else
            {
                chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, request.UserPayload ?? ""));
            }

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

            int maxToolIterations = Math.Max(1, _settings.MaxToolCallRetries + 1);
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

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                toolIteration++;
                string streamModel = ResolveModelName();
                if (toolIteration > maxToolIterations + 1)
                {
                    IReadOnlyList<LlmToolCallTrace> executedToolCalls = policy.ExecutedTraces.ToList();
                    if (emittedAnyVisibleText && executedToolCalls.Any(t => t.Success))
                    {
                        _logger.LogWarning(GameLogFeature.Llm,
                            "MeaiLlmClient: Streaming tool loop reached the iteration guard after successful tool calls and visible text; completing without surfacing an internal guard error.");
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
                List<MEAI.FunctionCallContent> nativeToolCalls = new();
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

                await foreach (MEAI.ChatResponseUpdate update in _innerClient
                                   .GetStreamingResponseAsync(chatMessages, iterationOptions, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int nativeToolCountBeforeUpdate = nativeToolCalls.Count;
                    if (update.Contents != null)
                    {
                        foreach (MEAI.AIContent content in update.Contents)
                        {
                            if (content is MEAI.FunctionCallContent fcc)
                            {
                                nativeToolCalls.Add(fcc);
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
                        string full = iterationVisible.ToString();
                        int safeEnd = GetExclusiveEndForSafeUnboundRawStreaming(full);
                        if (safeEnd > hybridRawExclusiveEndEmitted)
                        {
                            string delta = full.Substring(hybridRawExclusiveEndEmitted,
                                safeEnd - hybridRawExclusiveEndEmitted);
                            hybridRawExclusiveEndEmitted = safeEnd;
                            foreach (string part in SplitForLiveUiStreaming(delta, LiveUiStreamMaxCharsPerChunk))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                emittedAnyVisibleText = true;
                                yield return new LlmStreamChunk { Text = part };
                            }

                            streamedVisibleToConsumer = true;
                        }

                        if (safeEnd < full.Length && !emittedHybridHoldTypingHint)
                        {
                            emittedHybridHoldTypingHint = true;
                            _logger.LogInfo(GameLogFeature.Llm,
                                "MeaiLlmClient: Tool-json hold started - emitting only the safe prefix; trailing `{...}` is buffered until the object closes or the stream ends.");
                            yield return new LlmStreamChunk
                            {
                                BufferedStreamingNoToolBinding = true,
                                BufferedStreamingUseToolProgressHint = true
                            };
                        }
                    }
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
                        string full = iterationVisible.ToString();
                        int safeEnd = GetExclusiveEndForSafeUnboundRawStreaming(full);
                        if (safeEnd > hybridRawExclusiveEndEmitted)
                        {
                            string delta = full.Substring(hybridRawExclusiveEndEmitted,
                                safeEnd - hybridRawExclusiveEndEmitted);
                            hybridRawExclusiveEndEmitted = safeEnd;
                            foreach (string part in SplitForLiveUiStreaming(delta, LiveUiStreamMaxCharsPerChunk))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                emittedAnyVisibleText = true;
                                yield return new LlmStreamChunk { Text = part };
                            }

                            streamedVisibleToConsumer = true;
                        }

                        if (safeEnd < full.Length && !emittedHybridHoldTypingHint)
                        {
                            emittedHybridHoldTypingHint = true;
                            _logger.LogInfo(GameLogFeature.Llm,
                                "MeaiLlmClient: Tool-json hold started - emitting only the safe prefix; trailing `{...}` is buffered until the object closes or the stream ends.");
                            yield return new LlmStreamChunk
                            {
                                BufferedStreamingNoToolBinding = true,
                                BufferedStreamingUseToolProgressHint = true
                            };
                        }
                    }
                }

                string visibleText = iterationVisible.ToString();
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

                    // Execute through shared policy
                    ToolExecutionPolicy.BatchToolCallResult batch =
                        await policy.ExecuteBatchAsync(nativeToolCalls, chatOptions, cancellationToken);
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, batch.Results));
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
                bool requestHadTools = (request.Tools?.Count ?? 0) > 0;
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
                                string suffix = GetCleanedTextSuffixAfterHybridPrefix(
                                    cleanedText, visibleText, hybridRawExclusiveEndEmitted);
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
                        string suffix = GetCleanedTextSuffixAfterHybridPrefix(
                            cleanedText, visibleText, hybridRawExclusiveEndEmitted);
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
        /// per-request value wins; otherwise fall back to <see cref="ICoreAISettings.MaxTokens"/>
        /// when it is positive; otherwise leave <c>null</c> so the provider uses its own default.
        /// Both HTTP and LLMUnity backends honour the resulting value uniformly.
        /// </summary>
        private int? ResolveMaxOutputTokens(int? perRequest)
        {
            if (perRequest.HasValue && perRequest.Value > 0)
            {
                return perRequest.Value;
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
                            result.Add(MEAI.AIFunctionFactory.Create(dt.ActionDelegate, dt.Name, dt.Description));
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
