#if COREAI_LLM
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

        private const int HybridToolJsonHeldTailMaxChars = 64 * 1024;

        /// <param name="supportsNativeToolCalling">
        /// Whether this endpoint accepts the native tools channel. Callers should pass its probed
        /// or explicitly configured capability. There is no implicit channel default.
        /// </param>
        public MeaiLlmClient(MEAI.IChatClient innerClient, IGameLogger logger, ICoreAISettings settings,
            bool supportsNativeToolCalling, IAgentMemoryStore? memoryStore = null)
        {
            if (innerClient == null)
            {
                throw new ArgumentNullException(nameof(innerClient));
            }
            _innerClient = supportsNativeToolCalling ? innerClient : StripNativeToolChannel(innerClient);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _memoryStore = memoryStore;
            _supportsNativeToolCalling = supportsNativeToolCalling;
        }

        /// <summary>
        /// Hides the native tool channel from an endpoint that has none, while the local MEAI function
        /// bindings stay available to the invocation layer above. The native
        /// <see cref="MEAI.ConfigureOptionsChatClient"/> does the wrapping: it hands the callback a
        /// CLONE of the caller's options (or a fresh instance when the caller passed none), so the
        /// request the endpoint sees loses the tool declarations and forced tool choice without the
        /// caller's own options object ever being touched.
        /// </summary>
        internal static MEAI.IChatClient StripNativeToolChannel(MEAI.IChatClient innerClient)
        {
            return new MEAI.ConfigureOptionsChatClient(innerClient, options =>
            {
                options.Tools = null;
                options.ToolMode = null;
                options.AllowMultipleToolCalls = null;
                // Raw passthrough copies of the same three fields, for transports that read them from
                // AdditionalProperties instead of the typed options.
                options.AdditionalProperties?.Remove("tools");
                options.AdditionalProperties?.Remove("tool_choice");
                options.AdditionalProperties?.Remove("parallel_tool_calls");
            });
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _supportsNativeToolCalling;

        /// <summary>
        /// Creates an OpenAI-compatible HTTP-backed MEAI client.
        /// </summary>
        /// <param name="supportsNativeToolCalling">
        /// Whether this endpoint accepts native tool calls. This is an endpoint capability, including
        /// local llama.cpp/LLMUnity servers, and must follow probing or explicit configuration.
        /// </param>
        public static MeaiLlmClient CreateHttp(
            IOpenAiHttpSettings openAiSettings,
            ICoreAISettings settings,
            IGameLogger logger,
            bool supportsNativeToolCalling,
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
            MeaiOpenAiChatClient innerClient = new(openAiSettings, transport, supportsNativeToolCalling);
            return new MeaiLlmClient(innerClient, logger, settings, supportsNativeToolCalling: supportsNativeToolCalling, memoryStore: memoryStore);
        }

        /// <summary>
        /// Creates an OpenAI-compatible HTTP-backed MEAI client from a Unity settings asset.
        /// </summary>
        public static MeaiLlmClient CreateHttp(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            bool supportsNativeToolCalling,
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
            return CreateHttp(adapter, settings, logger, supportsNativeToolCalling, memoryStore);
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string roleId = request.AgentRoleId ?? "Unknown";
            using LlmRequestContext.Scope ctxScope = LlmRequestContext.Begin(
                roleId,
                request.TraceId,
                EnsureIdempotencyKey(request),
                request.ActorId);
            List<MEAI.AIFunction> aiTools = request.ForcedToolMode == LlmToolChoiceMode.None
                ? new List<MEAI.AIFunction>() : BuildAIFunctions(request.Tools, roleId);
            string bindingError = RequiredToolBindingError(request, aiTools);
            if (bindingError != null)
            {
                return new LlmCompletionResult { Ok = false, ErrorCode = LlmErrorCode.InvalidRequest, Error = bindingError };
            }


            if (_settings.LogMeaiToolCallingSteps)
            {
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: SmartToolCallingChatClient created with {aiTools.Count} tools, max consecutive errors={_settings.MaxToolCallRetries}");
            }

            bool allowDuplicates = request.AllowDuplicateToolCalls ?? _settings.AllowDuplicateToolCalls;
            bool allowTextShapedToolCalls = InterpretsProseAsToolCalls(request);
            SmartToolCallingChatClient functionClient = new(_innerClient, Log.Instance, _settings, allowDuplicates,
                request.Tools, roleId, _settings.MaxToolCallRetries, request.TraceId,
                MessagePipeToolCallEventPublisher.Instance, CoreAiToolExecutionNotifier.Instance,
                request.MaxToolCallRoundtrips, request.ActorId, allowTextShapedToolCalls);

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
            }
            ApplyForcedToolMode(chatOptions, request, aiTools);

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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Llm, $"MeaiLlmClient: {ex.Message}");
                LlmCompletionResult failure = FromException(ex, functionClient.LastExecutedToolCalls);
                ApplyLastRoundtripPromptTokens(failure, functionClient.LastRoundtripUsage);
                return failure;
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

            string text = GetFinalAssistantText(response);

            text = SanitizeAssistantVisibleText(text, request);
            string reasoningText = ConcatenateAssistantReasoningText(response);

            if (string.IsNullOrEmpty(text) && !functionClient.LastTurnEndedByTool)
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
                    ReasoningContent = reasoningText,
                    Model = ResolveModelName(response.ModelId),
                    ExecutedToolCalls = functionClient.LastExecutedToolCalls,
                    LastRoundtripPromptTokens = ToLastRoundtripPromptTokens(functionClient.LastRoundtripUsage)
                };
            }

            LlmCompletionResult result = new()
            {
                Ok = true,
                Content = text,
                ReasoningContent = reasoningText,
                Model = ResolveModelName(response.ModelId)
            };
            if (response.Usage != null)
            {
                result.PromptTokens = (int)(response.Usage.InputTokenCount ?? 0);
                result.CompletionTokens = (int)(response.Usage.OutputTokenCount ?? 0);
                result.TotalTokens = (int)(response.Usage.TotalTokenCount ?? 0);
                (result.CacheReadTokens, result.CacheWriteTokens) =
                    ExtractCacheTokenCounts(response.Usage.AdditionalCounts);
                // WHY: PromptTokens stays the whole-turn CUMULATIVE sum (Prompt + Completion == Total
                // for cost telemetry); the prompt-size calibration reads the dedicated last-roundtrip
                // field instead. Zero counts are ignored (zero-emitting providers must not pollute it).
            }

            ApplyLastRoundtripPromptTokens(result, functionClient.LastRoundtripUsage);

            // Carry the tool-call diagnostic out of the smart-tool client so the logging
            // decorator can render the same `tools=[...]` line for stream and non-stream paths.
            result.ExecutedToolCalls = functionClient.LastExecutedToolCalls;
            return result;
        }

        /// <summary>
        /// Only the final assistant message is user-visible after the native tool loop.
        /// <see cref="MEAI.ChatResponse.Text"/> is deliberately NOT used here: it concatenates EVERY
        /// message of the response, so the tool transcript would be pasted into the teacher's reply.
        /// Per-message <see cref="MEAI.ChatMessage.Text"/> is the native concatenation of that one
        /// message's text contents.
        /// </summary>
        private static string GetFinalAssistantText(MEAI.ChatResponse response)
        {
            for (int index = response.Messages.Count - 1; index >= 0; index--)
            {
                MEAI.ChatMessage message = response.Messages[index];
                if (message.Role == MEAI.ChatRole.Assistant)
                {
                    return message.Text;
                }
            }

            return string.Empty;
        }

        private LlmCompletionResult FromException(
            Exception ex,
            IReadOnlyList<LlmToolCallTrace> executedToolCalls = null)
        {
            // WHY: A TimeoutException is an internal transport/provider timeout, never a user stop:
            // it must map to Timeout (retry/fallback eligible), not Cancelled or ProviderError.
            if (ex is TimeoutException)
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = ex.Message,
                    ErrorCode = LlmErrorCode.Timeout,
                    Model = ResolveModelName(),
                    ExecutedToolCalls = executedToolCalls ?? Array.Empty<LlmToolCallTrace>()
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
                    Model = ResolveModelName(),
                    ExecutedToolCalls = executedToolCalls ?? Array.Empty<LlmToolCallTrace>()
                };
            }

            return new LlmCompletionResult
            {
                Ok = false,
                Error = ex.Message,
                ErrorCode = LlmErrorCode.ProviderError,
                Model = ResolveModelName(),
                ExecutedToolCalls = executedToolCalls ?? Array.Empty<LlmToolCallTrace>()
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
                    chatMessages.Add(AiUserMessageBuilder.BuildUserMessage(request.UserPayload, request.Attachments));
                }
            }
            else
            {
                chatMessages.Add(AiUserMessageBuilder.BuildUserMessage(request.UserPayload ?? "", request.Attachments));
            }

            return chatMessages;
        }

        private static MEAI.ChatMessage NormalizeChatHistoryMessageForProvider(MEAI.ChatMessage message)
        {
            if (message.Role != MEAI.ChatRole.System)
            {
                return message;
            }

            // ChatMessage.Text is MEAI's own join of the message's TextContent parts and never returns
            // null, so no null guard is needed on it. Only a system message carrying no text at all
            // (attachments, tool traces) falls through to stringifying its parts.
            string text = message.Text;
            if (text.Length == 0)
            {
                text = string.Join(
                    "\n",
                    message.Contents
                        .Select(content => content?.ToString())
                        .Where(content => !string.IsNullOrWhiteSpace(content)));
            }

            return new MEAI.ChatMessage(MEAI.ChatRole.User, "System context update:\n" + text);
        }

        /// <summary>
        /// Model name reported on the completion result.
        /// <para>
        /// The provider's own answer wins: a proxy or router may serve a different model than the client
        /// asked for, and under <c>ServerManagedApi</c> the client asks for nothing at all — the backend
        /// decides. The configured name is only a fallback for providers that do not echo one (and for
        /// error paths, where there is no response to read).
        /// </para>
        /// </summary>
        /// <param name="providerReportedModel">Model id taken from the provider response, if any.</param>
        private string ResolveModelName(string providerReportedModel = null)
        {
            if (!string.IsNullOrWhiteSpace(providerReportedModel))
            {
                return providerReportedModel.Trim();
            }

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
        /// WHY this loop is not <c>FunctionInvokingChatClient</c>, unlike the non-streaming one in
        /// <see cref="SmartToolCallingChatClient"/>: MEAI's loop cannot invoke a tool until it has
        /// materialised the iteration's messages, i.e. until the iteration's stream has ended. Handing
        /// streaming to it stops the visible reply at the first tool call and resumes it only after the
        /// tool has run — the teacher freezes mid-sentence. This loop instead executes a call the moment
        /// its argument JSON is complete, while the model is still producing the rest of the turn.
        /// Do not "unify" the two loops onto MEAI without measuring that on a live model first.
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
            string roleId = request.AgentRoleId ?? "Unknown";
            using LlmRequestContext.Scope ctxScope = LlmRequestContext.Begin(
                roleId,
                request.TraceId,
                EnsureIdempotencyKey(request),
                request.ActorId);

            List<MEAI.ChatMessage> chatMessages = BuildMeaiChatMessages(request);

            List<MEAI.AIFunction> aiTools = request.ForcedToolMode == LlmToolChoiceMode.None
                ? new List<MEAI.AIFunction>() : BuildAIFunctions(request.Tools, roleId);
            string bindingError = RequiredToolBindingError(request, aiTools);
            if (bindingError != null)
            {
                yield return new LlmStreamChunk { IsDone = true, ErrorCode = LlmErrorCode.InvalidRequest, Error = bindingError };
                yield break;
            }

            string[] declaredToolNames = request.Tools?.Select(tool => tool.Name).ToArray() ?? Array.Empty<string>();
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
            }
            ApplyForcedToolMode(chatOptions, request, aiTools);

            _logger.LogInfo(GameLogFeature.Llm,
                $"MeaiLlmClient: Starting streaming with {chatMessages.Count} messages");

            int maxToolIterations = ResolveStreamingMaxToolRoundtrips(request.MaxToolCallRoundtrips, _settings);
            int toolIteration = 0;
            bool emittedAnyVisibleText = false;
            // One-shot guard for the reasoning-runaway rescue at the terminal path.
            bool emptyResponseNudgeSent = false;
            // Граница между репликами внутри ОДНОГО потока. Цикл ниже — это несколько ходов модели
            // (после каждого раунда инструментов она говорит заново), а наружу они уезжают одним
            // непрерывным потоком чанков. Флаг взводится на входе в следующую итерацию и гаснет на
            // первом же видимом чанке: без него потребитель склеивал конец одной реплики с началом
            // другой встык, и ученик читал «Проверь себя:**Ход завершён…**».
            bool visibleMessageBoundaryPending = false;
            string lastVisibleMessageId = null;

            // Единая точка выдачи видимого текста: помечает начало новой реплики РОВНО один раз и
            // ведёт учёт «была ли вообще видимая речь». Раньше эти два факта расползались по
            // четырнадцати местам, и любое новое место выдачи молча теряло границу.
            LlmStreamChunk MarkVisibleChunk(LlmStreamChunk visibleChunk)
            {
                emittedAnyVisibleText = true;
                if (visibleMessageBoundaryPending)
                {
                    visibleMessageBoundaryPending = false;
                    visibleChunk.StartsNewMessage = true;
                }

                return visibleChunk;
            }

            if (aiTools.Count == 0 && (request.Tools?.Count ?? 0) > 0)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"MeaiLlmClient: Streaming role='{roleId}' requested {request.Tools?.Count ?? 0} tool(s) but 0 AIFunction(s) were bound. " +
                    "Tool calls will be stripped from output without execution. Verify tool registration.");
            }

            bool allowDuplicates = request.AllowDuplicateToolCalls ?? _settings.AllowDuplicateToolCalls;
            ToolExecutionPolicy policy = new(Log.Instance, _settings, request.Tools, allowDuplicates,
                roleId, _settings.MaxToolCallRetries, request.TraceId,
                MessagePipeToolCallEventPublisher.Instance, CoreAiToolExecutionNotifier.Instance,
                request.ActorId);
            string? pendingFailedToolRetryInstruction = null;
            int emptyResponsesAfterToolFailure = 0;
            int emptyResponsesAfterToolSuccess = 0;
            // True only after a batch with at least one GENUINE success (!batch.AllFailed) - an
            // all-failed batch must fall through to the emptyResponsesAfterToolFailure path above instead.
            bool anyToolCallSucceededInStream = false;
            int streamedExecutedCallCount = 0;
            MEAI.UsageDetails turnUsage = null;
            // WHY: PromptTokens remains cumulative across all tool roundtrips for usage and cost metrics;
            // LastRoundtripPromptTokens carries the final prompt size used by calibration.
            MEAI.UsageDetails lastRoundtripUsage = null;

            // Set the moment the final tools-disabled summarization roundtrip starts, so that extra
            // turn can never run twice (recursion/loop guard for RunFinalNoToolsSummaryTurnAsync).
            bool finalSummaryTurnRan = false;

            // When the roundtrip cap or the max-consecutive-errors guard ends the streamed turn, run
            // EXACTLY ONE more model roundtrip with tools disabled so the consumer gets real prose
            // about what was accomplished instead of a canned/empty terminal chunk (Claude/Cursor
            // parity). The extra turn's visible text is buffered, stripped of any text-shaped tool
            // JSON (nothing may execute here) and only then streamed out; when the roundtrip fails
            // or yields no prose, the supplied fallback terminal chunk is emitted instead, so legacy
            // terminal semantics are fully preserved on failure.
            async IAsyncEnumerable<LlmStreamChunk> RunFinalNoToolsSummaryTurnAsync(
                string stopReason,
                LlmStreamChunk fallbackTerminal,
                MEAI.UsageDetails usageSoFar,
                string model)
            {
                if (finalSummaryTurnRan)
                {
                    ApplyStreamingUsageFields(fallbackTerminal, usageSoFar, model);
                    OverrideTerminalPromptTokensWithLastRoundtrip(fallbackTerminal, lastRoundtripUsage);
                    yield return fallbackTerminal;
                    yield break;
                }

                finalSummaryTurnRan = true;
                _logger.LogWarning(GameLogFeature.Llm,
                    $"MeaiLlmClient: {stopReason}; running one final tools-disabled roundtrip so the model can summarize.");

                List<MEAI.ChatMessage> summaryMessages = new(chatMessages)
                {
                    new MEAI.ChatMessage(MEAI.ChatRole.User,
                        "Tool budget exhausted. Do not call any more tools. Summarize in plain text " +
                        "what you accomplished and what remains to be done.")
                };

                // Tools deliberately omitted (not just ToolMode=None): the model physically cannot
                // emit another native tool call in this extra roundtrip.
                MEAI.ChatOptions summaryOptions = new()
                {
                    Temperature = chatOptions.Temperature,
                    MaxOutputTokens = chatOptions.MaxOutputTokens,
                    ToolMode = MEAI.ChatToolMode.None
                };

                ThinkBlockStreamFilter summaryFilter = new();
                System.Text.StringBuilder summaryVisible = new();
                MEAI.UsageDetails summaryUsage = usageSoFar;
                bool summaryFailed = false;

                IAsyncEnumerable<MEAI.ChatResponseUpdate> summaryStream =
                    _innerClient.GetStreamingResponseAsync(summaryMessages, summaryOptions, cancellationToken);
                await using IAsyncEnumerator<MEAI.ChatResponseUpdate> summaryEnumerator =
                    summaryStream.GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    MEAI.ChatResponseUpdate summaryUpdate;
                    try
                    {
                        if (!await summaryEnumerator.MoveNextAsync())
                        {
                            break;
                        }

                        summaryUpdate = summaryEnumerator.Current;
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException)
                    {
                        // Consumer is gone; propagate exactly like the main streaming loop.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        summaryFailed = true;
                        _logger.LogWarning(GameLogFeature.Llm,
                            "MeaiLlmClient: Final tools-disabled summary roundtrip failed " +
                            $"({ex.GetType().Name}: {ex.Message}); falling back to the terminal chunk.");
                        break;
                    }

                    if (summaryUpdate.Contents != null)
                    {
                        foreach (MEAI.AIContent summaryContent in summaryUpdate.Contents)
                        {
                            if (summaryContent is MEAI.UsageContent summaryUsageContent &&
                                summaryUsageContent.Details != null)
                            {
                                summaryUsage = AccumulateTurnUsage(summaryUsage, summaryUsageContent.Details);
                                lastRoundtripUsage = summaryUsageContent.Details;
                            }
                        }
                    }

                    string summaryRaw = GetStreamingUpdateText(summaryUpdate);
                    if (!string.IsNullOrEmpty(summaryRaw))
                    {
                        string summaryChunk = summaryFilter.ProcessChunk(summaryRaw);
                        if (!string.IsNullOrEmpty(summaryChunk))
                        {
                            summaryVisible.Append(summaryChunk);
                        }
                    }
                }

                summaryVisible.Append(summaryFilter.Flush());

                // WHY: this turn runs with tools disabled, so nothing here can execute — the strip is purely
                // cosmetic, and cosmetics must not delete a teacher's JSON example. It therefore follows
                // the same channel rule as everything else: only where a call can be written as prose.
                string summaryProse = summaryVisible.ToString();
                if (InterpretsProseAsToolCalls(request))
                {
                    summaryProse = StripEmbeddedToolCallJsonForDisplay(summaryProse, declaredToolNames);
                }

                string summaryText = summaryFailed
                    ? string.Empty
                    : SanitizeAssistantVisibleText(summaryProse, request);
                if (string.IsNullOrWhiteSpace(summaryText))
                {
                    ApplyStreamingUsageFields(fallbackTerminal, summaryUsage, model);
                    OverrideTerminalPromptTokensWithLastRoundtrip(fallbackTerminal, lastRoundtripUsage);
                    yield return fallbackTerminal;
                    yield break;
                }

                // Итоговый ход без инструментов — это отдельная реплика: перед ней уже прошли и речь,
                // и раунды инструментов.
                visibleMessageBoundaryPending |= emittedAnyVisibleText;
                cancellationToken.ThrowIfCancellationRequested();
                yield return MarkVisibleChunk(new LlmStreamChunk { Text = summaryText });

                // The model got to summarize, so the turn ends as a graceful stop: traces still
                // carry every failure for telemetry, but the consumer sees prose, not a raw error.
                LlmStreamChunk summaryTerminal = new()
                {
                    IsDone = true,
                    Text = string.Empty,
                    ExecutedToolCalls = policy.ExecutedTraces.ToList()
                };
                ApplyStreamingUsageFields(summaryTerminal, summaryUsage, model);
                OverrideTerminalPromptTokensWithLastRoundtrip(summaryTerminal, lastRoundtripUsage);
                yield return summaryTerminal;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                toolIteration++;
                // Новая итерация — новая реплика модели, но только если предыдущая уже что-то
                // сказала: иначе «границей» окажется самое начало ответа и потребитель поставит
                // разделитель перед первым же словом.
                visibleMessageBoundaryPending |= emittedAnyVisibleText;
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
                        // Cap reached with real progress: give the model one tools-disabled turn to
                        // summarize; on any failure the legacy clean-but-empty terminal chunk goes out.
                        LlmStreamChunk capFallback = new()
                        {
                            IsDone = true,
                            Text = string.Empty,
                            ExecutedToolCalls = executedToolCalls
                        };
                        await foreach (LlmStreamChunk summaryChunk in RunFinalNoToolsSummaryTurnAsync(
                                           "Streaming tool loop reached the roundtrip cap after successful tool calls",
                                           capFallback, turnUsage, streamModel))
                        {
                            yield return summaryChunk;
                        }

                        yield break;
                    }

                    LlmStreamChunk capErrorFallback = new()
                    {
                        IsDone = true,
                        Error = "tool loop exceeded max iterations",
                        ErrorCode = LlmErrorCode.ProviderError,
                        ExecutedToolCalls = executedToolCalls
                    };
                    await foreach (LlmStreamChunk summaryChunk in RunFinalNoToolsSummaryTurnAsync(
                                       "Streaming tool loop exceeded the roundtrip cap without a successful tool call",
                                       capErrorFallback, turnUsage, streamModel))
                    {
                        yield return summaryChunk;
                    }

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
                    iterationOptions = CloneOptionsWithAutoToolMode(chatOptions, aiTools);
                }

                ThinkBlockStreamFilter thinkFilter = new();
                // WHY: The filter suppresses inline <think> spans synchronously inside ProcessChunk;
                // buffering them here lets the iterator re-emit each span as a ReasoningText chunk
                // right after the call (an iterator cannot yield from inside a callback).
                List<string> pendingInlineReasoning = new();
                thinkFilter.ReasoningSink = pendingInlineReasoning.Add;
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
                // Local mirror of how many calls were handed to the policy this turn: executed
                // inline in sequential mode, or scheduled for parallel execution (the policy
                // returns null for those and their results surface at turn completion). The
                // streamed turn's slot count is internal to CoreAI.Core (not visible from this
                // assembly), and the mid-stream failure handler below must know whether any call
                // may already have mutated the world.
                int chunkCount = 0;
                bool toolsDeclared = (request.Tools?.Count ?? 0) > 0;
                bool unboundToolsRequested = toolsDeclared && aiTools.Count == 0;
                // WHY: failed local binding does not change the endpoint protocol or authorize parsing prose.
                bool interpretProseAsToolCalls = toolsDeclared && InterpretsProseAsToolCalls(request);
                // Удерживаем прозу ровно тогда, когда разбираем её как вызовы, — другого повода нет.
                bool hybridToolJsonHold = interpretProseAsToolCalls;
                bool streamLiveVisibleText = !hybridToolJsonHold;
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
                    List<HybridProseSegment> segments = GetHybridSafeSegments(full, out int safeEnd, declaredToolNames);
                    int heldLength = full.Length - safeEnd;
                    if (heldLength > HybridToolJsonHeldTailMaxChars)
                    {
                        hybridToolJsonCandidateOverflow = true;
                        _logger.LogWarning(GameLogFeature.Llm,
                            $"MeaiLlmClient: Held text-shaped tool-call candidate exceeded {HybridToolJsonHeldTailMaxChars} chars; failing closed instead of streaming the raw JSON tail.");
                    }

                    foreach (HybridProseSegment segment in segments)
                    {
                        int absoluteStart = segment.Start;
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
                            streamedVisibleToConsumer = true;
                            yield return MarkVisibleChunk(new LlmStreamChunk { Text = prose });
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
                        if (streamedExecutedCallCount == 0)
                        {
                            // No tool has mutated the world yet: preserve legacy behavior and let
                            // the exception escape (FallbackLlmClientDecorator relies on
                            // pre-first-chunk failures propagating so it can fall back).
                            throw;
                        }

                        // Tool calls were already handed to the policy mid-stream (with parallel
                        // execution some may still be IN FLIGHT): close the turn so in-flight
                        // calls are drained, the consecutive-error counter records once and the
                        // turn signature is registered (echo guard, batch parity) before the
                        // failure surfaces. CancellationToken.None on purpose: finalization of
                        // already-started calls must not itself be cancelled - the per-call tasks
                        // already carry the request token, and any slot left unfinished collates
                        // as an explicit failed result inside the policy. CompleteStreamedTurnAsync
                        // self-bounds the drain by the per-call tool timeout, so passing None here
                        // still cannot hang finalization on a cancellation-ignoring tool.
                        if (streamedTurn != null)
                        {
                            await policy.CompleteStreamedTurnAsync(streamedTurn, CancellationToken.None);
                        }

                        _logger.LogWarning(GameLogFeature.Llm,
                            $"MeaiLlmClient: Streaming transport failed after {streamedExecutedCallCount} tool call(s) " +
                            "started mid-stream; finalizing the partial turn instead of abandoning it " +
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

                    // WHY: OpenAI-compatible providers repeat the SERVED model on every chunk. Adopting it
                    // means usage/cost telemetry reports what actually answered - a router or a
                    // ServerManagedApi backend routinely picks a model the client never named.
                    if (!string.IsNullOrWhiteSpace(update.ModelId))
                    {
                        streamModel = ResolveModelName(update.ModelId);
                    }

                    int nativeToolCountBeforeUpdate = nativeToolCalls.Count;
                    if (update.Contents != null)
                    {
                        foreach (MEAI.AIContent content in update.Contents)
                        {
                            if (content is MEAI.FunctionCallContent fcc)
                            {
                                // WHY: An unsolicited provider call cannot override the caller's execution prohibition.
                                if (request.ForcedToolMode == LlmToolChoiceMode.None) continue;
                                nativeToolCalls.Add(fcc);
                                if (aiTools.Count > 0)
                                {
                                    streamedTurn ??= policy.BeginStreamedTurn();
                                    await policy.ExecuteStreamedAsync(
                                        streamedTurn, fcc, chatOptions, cancellationToken);
                                    streamedExecutedCallCount++;
                                }
                                else
                                {
                                    // No AIFunction is bound for this role: the call cannot execute.
                                    // Trace it per-call (the session-start warning alone leaves these
                                    // invisible in ExecutedTraces / diagnostics dashboards).
                                    policy.RecordSyntheticTrace(fcc.Name ?? "", false, 0d, "unbound-native",
                                        "Native tool call arrived but no MEAI AIFunction is bound for this role; call not executed.");
                                }
                            }
                            else if (content is MEAI.TextReasoningContent reasoningContent &&
                                     !string.IsNullOrEmpty(reasoningContent.Text))
                            {
                                // WHY: Provider-side reasoning (delta.reasoning_content) surfaces on a
                                // dedicated chunk field so the UI can render a collapsible thinking
                                // section; it never joins Text, keeping the visible answer clean.
                                yield return new LlmStreamChunk { ReasoningText = reasoningContent.Text };
                            }
                            else if (content is MEAI.UsageContent usageContent && usageContent.Details != null)
                            {
                                // Providers report usage once per roundtrip (final SSE chunk with
                                // stream_options.include_usage); sum across roundtrips so a multi-tool
                                // turn reports the whole turn, not just the last roundtrip.
                                turnUsage = AccumulateTurnUsage(turnUsage, usageContent.Details);
                                lastRoundtripUsage = usageContent.Details;
                                // Surface cumulative usage immediately: RoutingLlmClient keeps the last
                                // usage-bearing chunk and publishes LlmUsageReported even when the turn
                                // is later cancelled or times out mid-roundtrip, so token diagnostics
                                // never show zero for a turn that already burned tokens.
                                LlmStreamChunk usageProgress = new() { Text = string.Empty };
                                ApplyStreamingUsageFields(usageProgress, turnUsage, streamModel);
                                yield return usageProgress;
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

                    if (!string.IsNullOrEmpty(update.MessageId))
                    {
                        if (lastVisibleMessageId != null &&
                            !string.Equals(lastVisibleMessageId, update.MessageId, StringComparison.Ordinal))
                        {
                            visibleMessageBoundaryPending |= emittedAnyVisibleText;
                        }

                        lastVisibleMessageId = update.MessageId;
                    }

                    rawIterationText.Append(raw);
                    string visible = thinkFilter.ProcessChunk(raw);
                    if (pendingInlineReasoning.Count > 0)
                    {
                        foreach (string inlineReasoning in pendingInlineReasoning)
                        {
                            yield return new LlmStreamChunk { ReasoningText = inlineReasoning };
                        }

                        pendingInlineReasoning.Clear();
                    }

                    if (string.IsNullOrEmpty(visible))
                    {
                        continue;
                    }

                    chunkCount++;
                    iterationVisible.Append(visible);
                    visibleChunks.Add(visible);
                    if (streamLiveVisibleText)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return MarkVisibleChunk(new LlmStreamChunk { Text = visible });
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
                    ApplyStreamingUsageFields(streamFailChunk, turnUsage, streamModel);
                    OverrideTerminalPromptTokensWithLastRoundtrip(streamFailChunk, lastRoundtripUsage);
                    yield return streamFailChunk;
                    yield break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                string tail = thinkFilter.Flush();
                if (pendingInlineReasoning.Count > 0)
                {
                    foreach (string inlineReasoning in pendingInlineReasoning)
                    {
                        yield return new LlmStreamChunk { ReasoningText = inlineReasoning };
                    }

                    pendingInlineReasoning.Clear();
                }

                if (!string.IsNullOrEmpty(tail))
                {
                    iterationVisible.Append(tail);
                    visibleChunks.Add(tail);
                    if (streamLiveVisibleText)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return MarkVisibleChunk(new LlmStreamChunk { Text = tail });
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
                // WHY: Same channel gate. This only produces a diagnostic, but on a native endpoint the
                // diagnostic would be a lie: JSON inside <think> there is the model reasoning about an
                // example, not a call we dropped.
                bool hiddenThinkToolCall = interpretProseAsToolCalls &&
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
                        string visibleProse = SanitizeAssistantVisibleText(
                            StripEmbeddedToolCallJsonForDisplay(visibleText, declaredToolNames), request);
                        if (!string.IsNullOrWhiteSpace(visibleProse))
                        {
                            yield return MarkVisibleChunk(new LlmStreamChunk { Text = visibleProse });
                        }
                    }
                    else if (streamedVisibleToConsumer && hybridToolJsonHold &&
                             hybridRawExclusiveEndEmitted < visibleText.Length)
                    {
                        // The hybrid hold streamed only the safe prefix; whatever prose it was still
                        // holding when the native tool calls arrived must be flushed now or it is
                        // silently dropped for this roundtrip (mirrors the text-extraction path).
                        string heldSuffix = GetHybridUnemittedSuffix(visibleText, hybridRawExclusiveEndEmitted, declaredToolNames);
                        if (!string.IsNullOrEmpty(heldSuffix))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return MarkVisibleChunk(new LlmStreamChunk { Text = heldSuffix });
                        }
                    }

                    List<MEAI.AIContent> assistantContents = nativeToolCalls.Cast<MEAI.AIContent>().ToList();
                    if (!string.IsNullOrWhiteSpace(visibleText))
                    {
                        assistantContents.Add(new MEAI.TextContent(visibleText));
                    }

                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, assistantContents));

                    // Streamed turn: every call was executed or scheduled the moment it arrived —
                    // close the turn (drain any in-flight parallel calls, one consecutive-error
                    // record + echo signature, batch parity) and reuse its arrival-ordered
                    // results. Otherwise fall back to the classic end-of-stream batch.
                    ToolExecutionPolicy.BatchToolCallResult batch = streamedTurn != null
                        ? await policy.CompleteStreamedTurnAsync(streamedTurn, cancellationToken)
                        : await policy.ExecuteBatchAsync(nativeToolCalls, chatOptions, cancellationToken);
                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            $"MeaiLlmClient: native tool batch complete (streamed={streamedTurn != null}, anyFailed={batch.AnyFailed}, allFailed={batch.AllFailed}); continuing the turn");
                    }
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, batch.Results));
                    TrimStreamingToolCallHistory(chatMessages);
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
                        await foreach (LlmStreamChunk summaryChunk in RunFinalNoToolsSummaryTurnAsync(
                                           "Streaming tool loop hit max consecutive tool errors (native path)",
                                           errChunk, turnUsage, streamModel))
                        {
                            yield return summaryChunk;
                        }

                        yield break;
                    }

                    if (policy.TurnEndingToolSucceeded)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            "MeaiLlmClient: Streaming turn closed by a turn-ending tool (native path); " +
                            "the tool result is NOT sent back for another roundtrip.");
                        yield return BuildTurnEndedByToolChunk(
                            policy, turnUsage, streamModel, lastRoundtripUsage);
                        yield break;
                    }

                    continue;
                }

                // === Path 2: Text-based tool call extraction (primary for local models) ===
                // Run extraction whenever the *request* declared tools, not just when the
                // backend successfully bound AIFunctions. This way, a tool that was requested
                // but couldn't be bound (e.g., MemoryLlmTool with a null store) still has its
                // Resolve and cache required local values.
                if (interpretProseAsToolCalls && TryExtractToolCallsFromText(visibleText,
                        out List<MEAI.FunctionCallContent> toolCalls, out string cleanedText,
                        declaredToolNames))
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
                                yield return MarkVisibleChunk(new LlmStreamChunk { Text = cleanedText });
                            }
                            else if (hybridToolJsonHold && hybridRawExclusiveEndEmitted > 0)
                            {
                                string suffix = GetHybridUnemittedSuffix(
                                    visibleText, hybridRawExclusiveEndEmitted, declaredToolNames);
                                if (!string.IsNullOrEmpty(suffix))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    yield return MarkVisibleChunk(new LlmStreamChunk { Text = suffix });
                                }
                            }
                        }

                        LlmStreamChunk doneStrip = new()
                        {
                            IsDone = true,
                            Text = string.Empty,
                            ExecutedToolCalls = policy.ExecutedTraces.ToList()
                        };
                        ApplyStreamingUsageFields(doneStrip, turnUsage, streamModel);
                        OverrideTerminalPromptTokensWithLastRoundtrip(doneStrip, lastRoundtripUsage);
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
                        yield return MarkVisibleChunk(new LlmStreamChunk { Text = cleanedText });
                    }
                    else if (streamedVisibleToConsumer && hybridToolJsonHold && hybridRawExclusiveEndEmitted > 0 &&
                             !string.IsNullOrWhiteSpace(cleanedText))
                    {
                        string suffix = GetHybridUnemittedSuffix(
                            visibleText, hybridRawExclusiveEndEmitted, declaredToolNames);
                        if (!string.IsNullOrEmpty(suffix))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return MarkVisibleChunk(new LlmStreamChunk { Text = suffix });
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
                    TrimStreamingToolCallHistory(chatMessages);

                    // WHY: mirror the native path's streamedExecutedCallCount bump for text-extracted tool
                    // calls too — these ran through ExecuteBatchAsync and may have mutated the world, so a
                    // later-roundtrip transport failure (line ~821) must finalize-and-report (graded
                    // failure WITH traces) instead of a bare throw that lets the fallback/retry decorators
                    // replay the original request and double-execute a side-effecting tool.
                    streamedExecutedCallCount += toolCalls.Count;
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
                        await foreach (LlmStreamChunk summaryChunk in RunFinalNoToolsSummaryTurnAsync(
                                           "Streaming tool loop hit max consecutive tool errors (text-extraction path)",
                                           errChunk2, turnUsage, streamModel))
                        {
                            yield return summaryChunk;
                        }

                        yield break;
                    }

                    if (policy.TurnEndingToolSucceeded)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            "MeaiLlmClient: Streaming turn closed by a turn-ending tool (text-extraction path); " +
                            "the tool result is NOT sent back for another roundtrip.");
                        yield return BuildTurnEndedByToolChunk(
                            policy, turnUsage, streamModel, lastRoundtripUsage);
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

                // WHY: Same gate, and this one EXECUTES: it repairs a truncated JSON object out of the prose
                // and runs it as a tool call. On a native endpoint that is an example the tutor did not
                // finish typing, not a call - the most expensive possible way to be wrong about prose.
                if (interpretProseAsToolCalls && TryBuildMalformedTextToolCall(
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
                            yield return MarkVisibleChunk(new LlmStreamChunk { Text = sanitizedMalformedCleaned });
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
                        ApplyStreamingUsageFields(doneParseStrip, turnUsage, streamModel);
                        OverrideTerminalPromptTokensWithLastRoundtrip(doneParseStrip, lastRoundtripUsage);
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
                    TrimStreamingToolCallHistory(chatMessages);
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
                        await foreach (LlmStreamChunk summaryChunk in RunFinalNoToolsSummaryTurnAsync(
                                           "Streaming tool loop hit max consecutive tool errors (malformed-tool-json path)",
                                           errChunk3, turnUsage, streamModel))
                        {
                            yield return summaryChunk;
                        }

                        yield break;
                    }

                    if (policy.TurnEndingToolSucceeded)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            "MeaiLlmClient: Streaming turn closed by a turn-ending tool (malformed-tool-json path); " +
                            "the tool result is NOT sent back for another roundtrip.");
                        yield return BuildTurnEndedByToolChunk(
                            policy, turnUsage, streamModel, lastRoundtripUsage);
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
                            yield return MarkVisibleChunk(new LlmStreamChunk { Text = sanitizedFull });
                        }
                    }
                    else
                    {
                        foreach (string chunk in visibleChunks)
                        {
                            yield return MarkVisibleChunk(new LlmStreamChunk { Text = chunk });
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
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return MarkVisibleChunk(new LlmStreamChunk { Text = restSan });
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Reasoning-runaway rescue: a reasoning model can burn the ENTIRE output budget on
                // hidden thinking and end the roundtrip with zero visible text, zero tool calls and
                // finish_reason=length - the user would see "Could not get a response". Retry ONCE
                // with an explicit act-now nudge before surfacing the empty turn.
                if (request.ForcedToolMode != LlmToolChoiceMode.None && !emittedAnyVisibleText &&
                    nativeToolCalls.Count == 0 &&
                    string.IsNullOrWhiteSpace(SanitizeAssistantVisibleText(visibleText, request)) &&
                    !emptyResponseNudgeSent)
                {
                    emptyResponseNudgeSent = true;
                    _logger.LogWarning(GameLogFeature.Llm,
                        "MeaiLlmClient: roundtrip produced no visible text and no tool calls (likely reasoning runaway consumed the output budget) - retrying once with an act-now nudge.");
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User,
                        "Your previous response was empty - you likely spent the whole token budget on hidden reasoning. " +
                        "Act NOW: reply with the direct tool call or a short plain-text answer. Keep reasoning minimal."));
                    continue;
                }

                LlmStreamChunk terminal = new()
                {
                    IsDone = true,
                    Text = string.Empty,
                    ExecutedToolCalls = policy.ExecutedTraces.ToList()
                };
                ApplyStreamingUsageFields(terminal, turnUsage, streamModel);
                OverrideTerminalPromptTokensWithLastRoundtrip(terminal, lastRoundtripUsage);
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

        /// <summary>
        /// Accumulates per-roundtrip provider usage into a whole-turn total so multi-roundtrip tool
        /// turns report the sum of every roundtrip instead of only the last one. Delegates to the
        /// shared <see cref="LlmUsageAccumulator"/> so the non-streaming loop
        /// (<see cref="SmartToolCallingChatClient"/>) sums with identical semantics.
        /// </summary>
        private static MEAI.UsageDetails AccumulateTurnUsage(MEAI.UsageDetails total, MEAI.UsageDetails add)
        {
            return LlmUsageAccumulator.Accumulate(total, add);
        }

        /// <summary>
        /// Streaming-loop counterpart of the non-streaming history trim: after every tool roundtrip
        /// appends its assistant tool_calls + tool results to the message list, drop the oldest
        /// resolved tool exchanges beyond <see cref="ICoreAISettings.MaxToolCallHistoryMessages"/>
        /// via the shared <see cref="ToolCallHistoryTrimmer"/> (system + original user messages are
        /// kept; units are removed whole so no tool message is ever orphaned). 0 = unlimited.
        /// </summary>
        private void TrimStreamingToolCallHistory(List<MEAI.ChatMessage> chatMessages)
        {
            int maxHistoryMessages = _settings.MaxToolCallHistoryMessages;
            if (maxHistoryMessages <= 0)
            {
                return;
            }

            int removed = ToolCallHistoryTrimmer.Trim(chatMessages, maxHistoryMessages);
            if (removed > 0 && _settings.LogMeaiToolCallingSteps)
            {
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: Trimmed {removed} old tool call message(s) from the streaming loop, keeping {chatMessages.Count} total.");
            }
        }

        /// <summary>
        /// Non-streaming counterpart of <see cref="OverrideTerminalPromptTokensWithLastRoundtrip"/>:
        /// stamps every terminal <see cref="LlmCompletionResult"/> with the last roundtrip's prompt
        /// size, including paths whose response carries no usage object.
        /// </summary>
        private static void ApplyLastRoundtripPromptTokens(
            LlmCompletionResult result,
            MEAI.UsageDetails lastRoundtripUsage)
        {
            if (result == null)
            {
                return;
            }

            result.LastRoundtripPromptTokens = ToLastRoundtripPromptTokens(lastRoundtripUsage);
        }

        /// <summary>
        /// Reads the calibration prompt size from the last roundtrip's usage, if reported.
        /// </summary>
        private static int? ToLastRoundtripPromptTokens(MEAI.UsageDetails lastRoundtripUsage)
        {
            if (!(lastRoundtripUsage?.InputTokenCount > 0))
            {
                return null;
            }

            return (int)lastRoundtripUsage.InputTokenCount.Value;
        }

        /// <summary>
        /// Stamps the terminal chunk's <see cref="LlmStreamChunk.LastRoundtripPromptTokens"/> from the
        /// final roundtrip's usage. PromptTokens stays the whole-turn CUMULATIVE sum so
        /// Prompt + Completion == Total holds for every LlmUsageReported consumer; the prompt-size
        /// calibration (AiOrchestrator) reads this dedicated field instead. Zero/absent input counts
        /// are ignored so a zero-emitting provider cannot pollute the calibration channel.
        /// </summary>
        private static void OverrideTerminalPromptTokensWithLastRoundtrip(
            LlmStreamChunk chunk, MEAI.UsageDetails lastRoundtripUsage)
        {
            if (chunk == null || !(lastRoundtripUsage?.InputTokenCount > 0))
            {
                return;
            }

            chunk.LastRoundtripPromptTokens = ClampTokenCount(lastRoundtripUsage.InputTokenCount.Value);
        }

        /// <summary>
        /// Terminal chunk for a turn a tool declaring <see cref="ILlmTool.EndsTurn"/> closed after a
        /// SUCCESSFUL call. Carries the executed-call traces and the usage fields exactly like every other
        /// clean end of the streaming loop: a terminal chunk without them makes the turn read as
        /// zero-token and tool-less to the orchestrator's accounting, and a turn that produced no visible
        /// prose loses the very traces the tool-only completion line is synthesized from.
        /// </summary>
        private static LlmStreamChunk BuildTurnEndedByToolChunk(
            ToolExecutionPolicy policy,
            MEAI.UsageDetails turnUsage,
            string streamModel,
            MEAI.UsageDetails lastRoundtripUsage)
        {
            LlmStreamChunk chunk = new()
            {
                IsDone = true,
                Text = string.Empty,
                ExecutedToolCalls = policy.ExecutedTraces.ToList()
            };
            ApplyStreamingUsageFields(chunk, turnUsage, streamModel);
            OverrideTerminalPromptTokensWithLastRoundtrip(chunk, lastRoundtripUsage);
            return chunk;
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

        /// <summary>
        /// Prompt-cache reads and writes, from the one place they travel: <c>AdditionalCounts</c>.
        /// </summary>
        /// <remarks>
        /// WHY not <c>UsageDetails.CachedInputTokenCount</c>: that property arrives only in
        /// Microsoft.Extensions.AI 10.x, and the framework must compile against the consumer's floor of
        /// 9.10.2 (Unity 6 forces its own System.Text.Json 8.0.0.0, so the consumer cannot move up).
        /// Cache WRITES have no typed field in any MEAI version anyway — Anthropic-style
        /// <c>cache_creation_*</c> counters exist only as vendor keys — so a typed read would have
        /// covered half the question and left this reader in place regardless. One reader, one carrier.
        /// <para>
        /// Every matching key is SUMMED. Providers name cache counters in one dialect at a time
        /// (OpenAI/OpenRouter <c>prompt_tokens_details.cached_tokens</c>, DeepSeek
        /// <c>prompt_cache_hit_tokens</c>, Anthropic-style <c>cache_read_input_tokens</c>), so summing
        /// is right for a single response. A provider that reported the SAME number under two names
        /// would be counted twice — if one ever shows up, fix it by naming that provider's dialect, not
        /// by adding a second precedence path here.
        /// </para>
        /// </remarks>
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
        /// Concatenates every <see cref="MEAI.TextReasoningContent"/> the provider surfaced on the
        /// response (DeepSeek/Qwen <c>reasoning_content</c> and stripped inline <c>&lt;think&gt;</c>
        /// blocks) so <see cref="LlmCompletionResult.ReasoningContent"/> can feed a UI thinking section.
        /// <para>
        /// WHY hand-written: MEAI's own joins sit on the wrong side of this split. <c>ChatMessage.Text</c>
        /// and <c>ChatResponse.Text</c> deliberately EXCLUDE reasoning (<c>TextReasoningContent</c> does
        /// not derive from <c>TextContent</c>) — which is exactly what keeps the visible answer clean —
        /// and <c>AIContentExtensions.ConcatText</c>, the only join taking arbitrary content, is internal
        /// to the assembly in 9.10.2. No public MEAI call returns the reasoning parts on their own.
        /// </para>
        /// </summary>
        private static string ConcatenateAssistantReasoningText(MEAI.ChatResponse response)
        {
            if (response?.Messages == null || response.Messages.Count == 0)
            {
                return string.Empty;
            }

            System.Text.StringBuilder sb = new();
            foreach (MEAI.ChatMessage message in response.Messages)
            {
                if (message?.Contents == null)
                {
                    continue;
                }

                foreach (MEAI.AIContent content in message.Contents)
                {
                    if (content is MEAI.TextReasoningContent reasoning &&
                        !string.IsNullOrEmpty(reasoning.Text))
                    {
                        sb.Append(reasoning.Text);
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Visible prose of one streaming update. The concatenation itself is
        /// <see cref="MEAI.ChatResponseUpdate.Text"/> — it already joins every
        /// <see cref="MEAI.TextContent"/> of the update and excludes
        /// <see cref="MEAI.TextReasoningContent"/>, which does not derive from it. All this adds is the
        /// role gate: a tool or usage update carries no text the learner may see.
        /// </summary>
        private static string GetStreamingUpdateText(MEAI.ChatResponseUpdate update)
        {
            if (update.Role.HasValue && update.Role.Value != MEAI.ChatRole.Assistant)
            {
                return string.Empty;
            }

            return update.Text;
        }

        /// <summary>
        /// Whether this endpoint's answers may be read as tool calls at all: only where a call CAN be
        /// written as prose — no native tool channel — or where the host explicitly opted in for an
        /// endpoint that advertises one and then answers with JSON in the text.
        /// </summary>
        private bool InterpretsProseAsToolCalls(LlmCompletionRequest request)
        {
            return LlmToolChannelPolicy.InterpretsProse(
                _supportsNativeToolCalling, request?.AllowTextShapedToolCallsOnNativeEndpoint,
                request?.ForcedToolMode ?? LlmToolChoiceMode.Auto);
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
        /// Pattern-aware tool call extraction from text. Delegates to the portable
        /// <see cref="LlmToolCallTextExtractor"/> so both layers share one parser: exact call shape
        /// (top-level <c>"name"</c> string plus <c>"arguments"</c> object / <c>"arguments_json"</c>
        /// string), backtick/quote-cited spans and fenced ``` blocks skipped, and pseudo-syntax
        /// memory writes (e.g. Qwen: <c>Action=write content="..."</c>) picked up as fallback.
        /// <para>
        /// WHY MEAI does not replace this: MEAI reads calls off the native tool channel only. An
        /// endpoint without that channel (llama.cpp behind LLMUnity, some local proxies) can express a
        /// call ONLY by writing JSON into the reply text, so this is its single path to a tool. Deleting
        /// it does not raise an error anywhere — every tool on those endpoints simply stops running,
        /// silently. The gate that keeps it off native endpoints is
        /// <see cref="InterpretsProseAsToolCalls"/>, not the absence of this parser.
        /// </para>
        /// </summary>
        internal static bool TryExtractToolCallsFromText(
            string text,
            out List<MEAI.FunctionCallContent> toolCalls,
            out string cleanedText,
            IReadOnlyCollection<string> knownToolNames = null)
        {
            // WHY: delegates to the hardened portable extractor instead of a local parser so
            // cited schema examples never execute and the guards aren't duplicated.
            return TryPortableToolExtract(text, out toolCalls, out cleanedText, knownToolNames);
        }

        /// <summary>
        /// Delegates to <see cref="LlmToolCallTextExtractor"/> for parity with <see cref="SmartToolCallingChatClient"/>
        /// and picks up pseudo-syntax memory writes (Qwen / llama.cpp) when brace-count JSON extraction finds nothing.
        /// </summary>
        private static bool TryPortableToolExtract(
            string text,
            out List<MEAI.FunctionCallContent> toolCalls,
            out string cleanedText,
            IReadOnlyCollection<string> knownToolNames)
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
                    Dictionary<string, object?> arguments =
                        JsonConvert.DeserializeObject<Dictionary<string, object?>>(m.ArgumentsJson)
                        ?? new Dictionary<string, object?>();

                    // Normalize JObject/JArray values to strings for MEAI compatibility.
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
        /// Strips embedded tool-call JSON (<c>name</c> + <c>arguments</c>) from assistant text for chat UI.
        /// Used when the orchestrator could not separate tool JSON from prose in the streaming path
        /// (same rules as <see cref="TryExtractToolCallsFromText"/>; does not execute tools).
        /// </summary>
        public static string StripEmbeddedToolCallJsonForDisplay(string assistantText,
            IReadOnlyCollection<string> knownToolNames = null)
        {
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return assistantText ?? string.Empty;
            }

            return TryExtractToolCallsFromText(assistantText, out _, out string cleaned, knownToolNames) ? cleaned : assistantText;
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

        /// <summary>Checks if a JSON string has the exact tool-call shape (top-level "name" string plus "arguments" object or "arguments_json" string).</summary>
        internal static bool IsValidToolCallJson(string json)
        {
            // WHY: Shared with the portable extractor so the hybrid-hold span detection and the
            // execution path agree on what counts as a tool call (substring hits alone treated
            // quoted schema examples as commands).
            return LlmToolCallTextExtractor.LooksLikeToolCallJson(json);
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
        /// On-the-fly hybrid hold: prose in <paramref name="visibleText"/> up to
        /// <paramref name="hybridRawExclusiveEndEmitted"/> has already been streamed live (with any tool-call
        /// JSON in that region hidden). After the turn ends and tool calls are extracted, the only prose that
        /// still needs to be emitted is the JSON-stripped remainder of the held tail
        /// (<c>visibleText[hybridRawExclusiveEndEmitted..]</c>). This returns that suffix, or <c>null</c> when
        /// the held tail was only tool-call JSON / whitespace.
        /// </summary>
        internal static string? GetHybridUnemittedSuffix(string visibleText, int hybridRawExclusiveEndEmitted,
            IReadOnlyCollection<string> knownToolNames = null)
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
            string strippedTail = TryExtractToolCallsFromText(heldTail, out _, out string cleanedTail, knownToolNames)
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
        internal static List<HybridProseSegment> GetHybridSafeSegments(string text, out int exclusiveSafeEnd,
            IReadOnlyCollection<string> knownToolNames = null)
        {
            List<HybridProseSegment> segments = new();
            exclusiveSafeEnd = 0;
            if (string.IsNullOrEmpty(text))
            {
                return segments;
            }

            string search = StripCodeBlocks(text);

            // Hold boundary = start of the first STILL-INCOMPLETE (unclosed) brace that could grow into a
            // tool call. Completed tool-call JSON spans do NOT lower this boundary - they are hidden in
            // place so prose after their closing `}` keeps streaming live.
            int holdBoundary = GetFirstIncompleteBraceStart(search);

            // Completed text-shaped tool-call JSON spans (to hide) that fall before the hold boundary.
            LlmToolCallTextExtractor.TryExtract(text, knownToolNames,
                out List<LlmToolCallTextExtractor.Match> toolSpans, out _);

            // WHY in place: this method runs once per streamed chunk whenever prose is being parsed for
            // tool calls, and OrderBy allocated its iterator, key array and comparer every time just to
            // order a handful of spans. The list is freshly built by TryExtract, so nobody else can
            // observe the reordering, and (Start, Length) is a total order over the non-overlapping
            // spans it produces, so the sequence is the one the stable LINQ sort produced.
            toolSpans.Sort(ByStartThenLength);

            int cursor = 0;
            foreach (LlmToolCallTextExtractor.Match span in toolSpans)
            {
                if (span.Start >= holdBoundary)
                {
                    break;
                }

                if (span.Start > cursor)
                {
                    segments.Add(new HybridProseSegment(cursor, span.Start - cursor, false));
                }

                segments.Add(new HybridProseSegment(span.Start, span.Length, true));
                cursor = span.Start + span.Length;
            }

            if (holdBoundary > cursor)
            {
                segments.Add(new HybridProseSegment(cursor, holdBoundary - cursor, false));
            }

            exclusiveSafeEnd = holdBoundary;
            return segments;
        }

        /// <summary>
        /// Ascending span order for <see cref="GetHybridSafeSegments"/>. One cached delegate: the sort
        /// happens on a per-chunk path, so it must not allocate a comparer on every call.
        /// </summary>
        private static readonly Comparison<LlmToolCallTextExtractor.Match> ByStartThenLength =
            (left, right) => left.Start != right.Start
                ? left.Start.CompareTo(right.Start)
                : left.Length.CompareTo(right.Length);

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

        /// <summary>Represents a span of JSON text within a larger string.</summary>
        internal struct JsonSpan
        {
            public int Start;
            public int Length;
        }

        private static string RequiredToolBindingError(LlmCompletionRequest request, IReadOnlyList<MEAI.AIFunction> tools)
        {
            if (request.ForcedToolMode == LlmToolChoiceMode.RequireAny && !tools.Any(tool => tool != null))
                return "A tool call is required, but no callable tool is bound for this request. Check tool registration and memory-store wiring.";
            if (request.ForcedToolMode != LlmToolChoiceMode.RequireSpecific) return null;
            string requiredName = request.RequiredToolName?.Trim();
            if (string.IsNullOrEmpty(requiredName))
                return "RequireSpecific needs a non-empty RequiredToolName.";
            return tools.Any(tool => tool != null && string.Equals(tool.Name, requiredName, StringComparison.Ordinal))
                ? null
                : $"Required tool '{requiredName}' has no callable binding for this request. Check tool registration and memory-store wiring.";
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
                    MEAI.AIFunction targetTool = null;
                    for (int i = 0; i < aiTools.Count; i++)
                    {
                        if (string.Equals(aiTools[i].Name, targetName, StringComparison.Ordinal))
                        {
                            targetTool = aiTools[i];
                            break;
                        }
                    }

                    // Force the specific tool WITHOUT an OpenAI specific-function tool_choice: local
                    // llama.cpp / LM Studio servers reject {"type":"function","function":{"name":X}} with
                    // HTTP 400. "required" (RequireAny) + a tools list narrowed to just the target forces
                    // exactly that tool and is accepted by every OpenAI-compatible backend (cloud included).
                    // The narrowing is undone for later tool-loop iterations (see CloneOptionsWithAutoToolMode,
                    // which restores the full tool set), so only the first forced turn sees the single tool.
                    options.ToolMode = MEAI.ChatToolMode.RequireAny;
                    options.Tools = new List<MEAI.AITool> { targetTool };
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
        /// Returns a copy of <paramref name="source"/> with <see cref="MEAI.ChatToolMode.Auto"/>.
        /// Used in the streaming loop after the first iteration so the model isn't forced
        /// to keep emitting tool calls after each tool result is fed back. The full tool set is
        /// restored from <paramref name="fullTools"/> because a first-iteration
        /// <see cref="LlmToolChoiceMode.RequireSpecific"/> narrows <c>source.Tools</c> to the single
        /// forced tool — later iterations must see every tool again.
        /// <para>
        /// The copy itself is the native <see cref="MEAI.ChatOptions.Clone"/>: it carries EVERY option
        /// field over and hands back fresh <c>Tools</c>/<c>StopSequences</c>/<c>AdditionalProperties</c>
        /// collections, so reassigning them here cannot reach the first iteration's object. A copy that
        /// listed fields by hand would silently drop whatever a later request starts setting — and the
        /// drop would show up as a model answering iteration two under different options than one.
        /// </para>
        /// </summary>
        private static MEAI.ChatOptions CloneOptionsWithAutoToolMode(
            MEAI.ChatOptions source, IReadOnlyList<MEAI.AIFunction> fullTools)
        {
            MEAI.ChatOptions clone = source.Clone();
            clone.ToolMode = MEAI.ChatToolMode.Auto;
            if (fullTools != null && fullTools.Count > 0)
            {
                clone.Tools = fullTools.Cast<MEAI.AITool>().ToList();
            }

            return clone;
        }

        internal List<MEAI.AIFunction> BuildAIFunctions(IReadOnlyList<ILlmTool>? tools, string roleId)
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
                                // WHY: a missing store is a binding problem, not permission to interpret prose.
                                // Required calls fail validation before the provider; optional calls stay unavailable.
                                _logger.LogWarning(GameLogFeature.Llm,
                                    $"MeaiLlmClient: 'memory' tool requested for role '{roleId}' but IAgentMemoryStore is null. " +
                                    "The memory tool is unavailable and cannot persist changes. " +
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
            public LlmExecutionMode ExecutionMode => _s.ExecutionMode;
            public float Temperature => _s.Temperature;
            public int RequestTimeoutSeconds => _s.EffectiveHttpRequestTimeoutSeconds;
            public int MaxTokens => _s.MaxTokens;
            public string ExtraBodyJson => _s.ExtraBodyJson;
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
