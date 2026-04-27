#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Logging;
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
    /// Единый MEAI-клиент для любого бэкенда.
    /// Принимает <see cref="MEAI.IChatClient"/> и оборачивает в
    /// <see cref="MEAI.FunctionInvokingChatClient"/> для автоматического tool calling.
    ///
    /// Создание:
    ///   MeaiLlmClient.CreateHttp(settings, logger, memoryStore)
    ///   MeaiLlmClient.CreateLlmUnity(unityAgent, logger, memoryStore)
    /// </summary>
    public sealed class MeaiLlmClient : ILlmClient
    {
        private readonly MEAI.IChatClient _innerClient;
        private readonly IGameLogger _logger;
        private readonly IAgentMemoryStore? _memoryStore;
        private readonly ICoreAISettings _settings;
        private string _currentRoleId = "";

        public MeaiLlmClient(MEAI.IChatClient innerClient, IGameLogger logger, ICoreAISettings settings, IAgentMemoryStore? memoryStore = null)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _memoryStore = memoryStore;
        }

        /// <summary>
        /// Создать HTTP клиент (OpenAI-compatible API).
        /// </summary>
        public static MeaiLlmClient CreateHttp(
            IOpenAiHttpSettings openAiSettings,
            ICoreAISettings settings,
            IGameLogger logger,
            IAgentMemoryStore? memoryStore = null)
        {
            if (openAiSettings == null) throw new ArgumentNullException(nameof(openAiSettings));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            MeaiOpenAiChatClient innerClient = new(openAiSettings, logger);
            return new MeaiLlmClient(innerClient, logger, settings, memoryStore);
        }

        /// <summary>
        /// Создать HTTP клиент из единых настроек.
        /// </summary>
        public static MeaiLlmClient CreateHttp(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore? memoryStore = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            HttpSettingsAdapter adapter = new(settings);
            return CreateHttp(adapter, settings, logger, memoryStore);
        }

        /// <summary>
        /// Создать LLMUnity клиент (локальная GGUF модель).
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
            if (unityAgent == null) throw new ArgumentNullException(nameof(unityAgent));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            LlmUnityMeaiChatClient innerClient = new(unityAgent, logger);
            return new MeaiLlmClient(innerClient, logger, settings, memoryStore);
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
            List<MEAI.AIFunction> aiTools = BuildAIFunctions(request.Tools, _currentRoleId);

            if (_settings.LogMeaiToolCallingSteps)
            {
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: SmartToolCallingChatClient created with {aiTools.Count} tools, max consecutive errors={_settings.MaxToolCallRetries}");
            }

            bool allowDuplicates = request.AllowDuplicateToolCalls ?? _settings.AllowDuplicateToolCalls;
            SmartToolCallingChatClient functionClient = new(_innerClient, _logger, _settings, allowDuplicates, request.Tools, _currentRoleId, _settings.MaxToolCallRetries);

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

            // Логируем начальный промпт для отладки tool calling
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
                Temperature = request.Temperature,
                MaxOutputTokens = ResolveMaxOutputTokens(request.MaxOutputTokens)
            };
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
                response = await functionClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: GetResponseAsync completed, has {response.Messages?.Count ?? 0} messages in response");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Llm, $"MeaiLlmClient: {ex.Message}");
                return new LlmCompletionResult { Ok = false, Error = ex.Message };
            }

            // Логируем все сообщения в ответе для отладки tool calling
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

            // Логируем результат tool calling если включено
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
                return new LlmCompletionResult { Ok = false, Error = "Empty response from LLM" };
            }

            LlmCompletionResult result = new() { Ok = true, Content = text };
            if (response.Usage != null)
            {
                result.PromptTokens = (int)(response.Usage.InputTokenCount ?? 0);
                result.CompletionTokens = (int)(response.Usage.OutputTokenCount ?? 0);
                result.TotalTokens = (int)(response.Usage.TotalTokenCount ?? 0);
            }

            return result;
        }

        /// <summary>
        /// Стриминг ответа модели: возвращает чанки текста по мере генерации.
        /// <para>
        /// Поддерживает tool calling двумя способами:
        /// <list type="number">
        /// <item>Native: если <c>ChatResponseUpdate</c> содержит <c>FunctionCallContent</c> (облачные провайдеры).</item>
        /// <item>Text fallback: извлечение tool-call JSON из текста (Ollama, llama.cpp, LM Studio, etc.).</item>
        /// </list>
        /// </para>
        /// <para>
        /// Автоматически фильтрует <c>&lt;think&gt;...&lt;/think&gt;</c> блоки stateful-фильтром —
        /// корректно работает, даже если тег разбит между чанками.
        /// </para>
        /// <para>
        /// ВАЖНО: метод должен вызываться на главном потоке Unity (из coroutine, async void,
        /// или UniTask). Оборачивание в <c>Task.Run</c> приведёт к исключению
        /// "Create can only be called from the main thread" из-за создания
        /// <c>UnityWebRequest</c>.
        /// </para>
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            _currentRoleId = request.AgentRoleId ?? "Unknown";

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
                Temperature = request.Temperature,
                MaxOutputTokens = ResolveMaxOutputTokens(request.MaxOutputTokens)
            };
            if (aiTools.Count > 0)
            {
                chatOptions.Tools = aiTools.Cast<MEAI.AITool>().ToList();
                ApplyForcedToolMode(chatOptions, request, aiTools);
            }

            _logger.LogInfo(GameLogFeature.Llm,
                $"MeaiLlmClient: Starting streaming with {chatMessages.Count} messages");

            int maxToolIterations = Math.Max(1, _settings.MaxToolCallRetries + 1);
            int toolIteration = 0;

            // Shared policy for the entire streaming session
            bool allowDuplicates = request.AllowDuplicateToolCalls ?? _settings.AllowDuplicateToolCalls;
            ToolExecutionPolicy policy = new(_logger, _settings, request.Tools, allowDuplicates,
                _currentRoleId, _settings.MaxToolCallRetries);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                toolIteration++;
                if (toolIteration > maxToolIterations + 1)
                {
                    yield return new LlmStreamChunk { IsDone = true, Error = "tool loop exceeded max iterations" };
                    yield break;
                }

                // ForcedToolMode applies ONLY to the first iteration.
                // After we feed tool results back to the model, it must decide naturally
                // whether to call more tools or finalise with text — otherwise the
                // tool-choice constraint would loop forever (model is forced to re-call a tool,
                // we feed its result, model is forced again, ...).
                MEAI.ChatOptions iterationOptions = chatOptions;
                if (toolIteration > 1 && chatOptions.ToolMode != null && chatOptions.ToolMode is not MEAI.AutoChatToolMode)
                {
                    iterationOptions = CloneOptionsWithAutoToolMode(chatOptions);
                }

                ThinkBlockStreamFilter thinkFilter = new();
                List<string> visibleChunks = new();
                System.Text.StringBuilder iterationVisible = new();
                List<MEAI.FunctionCallContent> nativeToolCalls = new();
                int chunkCount = 0;

                await foreach (MEAI.ChatResponseUpdate update in _innerClient
                                   .GetStreamingResponseAsync(chatMessages, iterationOptions, cancellationToken))
                {
                    // Check for native tool calls in the update (from providers that support delta.tool_calls)
                    if (update.Contents != null)
                    {
                        foreach (MEAI.AIContent content in update.Contents)
                        {
                            if (content is MEAI.FunctionCallContent fcc)
                            {
                                nativeToolCalls.Add(fcc);
                            }
                        }
                    }

                    string raw = update.Text;
                    if (string.IsNullOrEmpty(raw)) continue;

                    string visible = thinkFilter.ProcessChunk(raw);
                    if (string.IsNullOrEmpty(visible)) continue;

                    chunkCount++;
                    iterationVisible.Append(visible);
                    visibleChunks.Add(visible);
                }

                string tail = thinkFilter.Flush();
                if (!string.IsNullOrEmpty(tail))
                {
                    iterationVisible.Append(tail);
                    visibleChunks.Add(tail);
                }

                string visibleText = iterationVisible.ToString();

                // === Path 1: Native tool calls from SSE delta.tool_calls ===
                if (nativeToolCalls.Count > 0 && aiTools.Count > 0)
                {
                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            $"MeaiLlmClient: Streaming detected {nativeToolCalls.Count} NATIVE tool call(s), executing...");
                    }

                    // Emit any visible text that preceded the tool calls
                    if (!string.IsNullOrWhiteSpace(visibleText))
                    {
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

                    if (policy.IsMaxErrorsReached)
                    {
                        yield return new LlmStreamChunk { IsDone = true, Error = "max consecutive tool errors reached" };
                        yield break;
                    }

                    continue;
                }

                // === Path 2: Text-based tool call extraction (primary for local models) ===
                if (aiTools.Count > 0 && TryExtractToolCallsFromText(visibleText, out List<MEAI.FunctionCallContent> toolCalls, out string cleanedText))
                {
                    if (_settings.LogMeaiToolCallingSteps)
                    {
                        _logger.LogInfo(GameLogFeature.Llm,
                            $"MeaiLlmClient: Streaming detected {toolCalls.Count} text-extracted tool call(s), executing...");
                    }

                    // Важно: текст до JSON tool-call должен быть виден пользователю.
                    // Иначе "префикс" ответа (например, "Working...") теряется в UI.
                    if (!string.IsNullOrWhiteSpace(cleanedText))
                    {
                        yield return new LlmStreamChunk { Text = cleanedText };
                    }

                    List<MEAI.AIContent> assistantContents = toolCalls.Cast<MEAI.AIContent>().ToList();
                    if (!string.IsNullOrWhiteSpace(cleanedText))
                    {
                        assistantContents.Add(new MEAI.TextContent(cleanedText));
                    }
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, assistantContents));

                    // Execute through shared policy (same as non-streaming)
                    ToolExecutionPolicy.BatchToolCallResult batch =
                        await policy.ExecuteBatchAsync(toolCalls, chatOptions, cancellationToken);
                    chatMessages.Add(new MEAI.ChatMessage(MEAI.ChatRole.Tool, batch.Results));

                    if (policy.IsMaxErrorsReached)
                    {
                        yield return new LlmStreamChunk { IsDone = true, Error = "max consecutive tool errors reached" };
                        yield break;
                    }

                    continue;
                }

                // === No tool calls — emit text chunks to consumer ===
                foreach (string chunk in visibleChunks)
                {
                    yield return new LlmStreamChunk { Text = chunk };
                }

                yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
                _logger.LogInfo(GameLogFeature.Llm,
                    $"MeaiLlmClient: Streaming completed ({chunkCount} chunks, total length={visibleText.Length})");
                yield break;
            }
        }

        /// <summary>
        /// Pattern-aware tool call extraction from text.
        /// Only matches JSON objects that contain both "name" and "arguments" keys.
        /// Supports multiple tool calls in a single text. Ignores JSON inside
        /// fenced code blocks (```...```) to avoid false positives.
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

            // Strip fenced code blocks to avoid matching JSON inside them
            string textForSearch = StripCodeBlocks(text);

            // Find all balanced JSON objects that look like tool calls
            List<JsonSpan> candidates = FindToolCallJsonSpans(textForSearch);
            if (candidates.Count == 0)
            {
                return false;
            }

            // Build cleaned text by removing all found tool-call JSON spans (from original text)
            // We need to map positions from stripped text back — since code blocks are only
            // *hidden* from search, the positions still correspond to the original text.
            System.Text.StringBuilder cleanBuilder = new(text.Length);
            int lastEnd = 0;
            foreach (JsonSpan span in candidates)
            {
                // Verify span is valid in original text too
                if (span.Start >= text.Length || span.Start + span.Length > text.Length) continue;
                string originalFragment = text.Substring(span.Start, span.Length);

                // Re-validate the fragment in the original text
                if (!IsValidToolCallJson(originalFragment)) continue;

                try
                {
                    JObject json = JObject.Parse(originalFragment);
                    string functionName = json["name"]?.ToString()?.Trim();
                    JToken argsToken = json["arguments"];
                    if (string.IsNullOrWhiteSpace(functionName) || argsToken == null) continue;

                    Dictionary<string, object?> arguments =
                        JsonConvert.DeserializeObject<Dictionary<string, object?>>(argsToken.ToString())
                        ?? new Dictionary<string, object?>();

                    string callId = $"stream_call_{functionName}_{Guid.NewGuid():N}";
                    toolCalls.Add(new MEAI.FunctionCallContent(callId, functionName, arguments));

                    cleanBuilder.Append(text, lastEnd, span.Start - lastEnd);
                    lastEnd = span.Start + span.Length;
                }
                catch
                {
                    // Malformed JSON — skip this candidate
                }
            }

            if (toolCalls.Count == 0)
            {
                return false;
            }

            // Append remaining text after last tool call
            if (lastEnd < text.Length)
            {
                cleanBuilder.Append(text, lastEnd, text.Length - lastEnd);
            }

            cleanedText = cleanBuilder.ToString().Trim();
            return true;
        }

        /// <summary>Removes fenced code blocks (```...```) from text to prevent false positive tool call detection.</summary>
        internal static string StripCodeBlocks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // Replace ```...``` blocks with whitespace of the same length to preserve positions
            return Regex.Replace(text, @"```[\s\S]*?```", m => new string(' ', m.Length));
        }

        /// <summary>Checks if a JSON string looks like a tool call (has "name" and "arguments").</summary>
        internal static bool IsValidToolCallJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            // Quick heuristic before parsing: must contain both key patterns
            return json.Contains("\"name\"") && json.Contains("\"arguments\"");
        }

        /// <summary>
        /// Find balanced JSON object spans in text that look like tool calls.
        /// Uses brace-counting to find balanced {} regions, then validates structure.
        /// </summary>
        internal static List<JsonSpan> FindToolCallJsonSpans(string text)
        {
            List<JsonSpan> spans = new();
            if (string.IsNullOrEmpty(text)) return spans;

            int i = 0;
            while (i < text.Length)
            {
                int braceStart = text.IndexOf('{', i);
                if (braceStart < 0) break;

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

                    if (inString) continue;

                    if (c == '{') depth++;
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

                i = (depth == 0 && j < text.Length) ? j + 1 : braceStart + 1;
            }

            return spans;
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
        /// has tools attached — forcing a tool with an empty tool list would error out.
        /// <para>
        /// Multi-round streaming: the caller is responsible for resetting the mode to
        /// <see cref="MEAI.ChatToolMode.Auto"/> after the first iteration via
        /// <see cref="CloneOptionsWithAutoToolMode"/>; otherwise the model would be forced
        /// to keep emitting tool calls forever (it's pinned to "RequireAny" each turn).
        /// </para>
        /// </summary>
        private void ApplyForcedToolMode(MEAI.ChatOptions options, LlmCompletionRequest request, IReadOnlyList<MEAI.AIFunction> aiTools)
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
                            "MeaiLlmClient: ForcedToolMode=RequireSpecific but RequiredToolName is empty — falling back to RequireAny.");
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
                            $"MeaiLlmClient: ForcedToolMode=RequireSpecific('{targetName}') but tool is not registered for this role — falling back to RequireAny.");
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

            foreach (ILlmTool tool in tools)
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

                            break;
                        case LuaLlmTool lt:
                            result.Add(lt.CreateAIFunction());
                            break;
                        case InventoryLlmTool it:
                            result.Add(it.CreateAIFunction());
                            break;
                        case GameConfigLlmTool gt:
                            result.Add(gt.CreateAIFunction());
                            break;
                        case WorldLlmTool wt:
                            result.Add(wt.CreateAIFunction());
                            break;
                        case SceneLlmTool slt:
                            result.AddRange(slt.CreateAIFunctions());
                            break;
                        case CameraLlmTool camt:
                            result.AddRange(camt.CreateAIFunctions());
                            break;
                        case DelegateLlmTool dt:
                            result.Add(MEAI.AIFunctionFactory.Create(dt.ActionDelegate, dt.Name, dt.Description));
                            break;
                        default:
                            MethodInfo m = tool.GetType().GetMethod("CreateAIFunction");
                            if (m != null)
                            {
                                MEAI.AIFunction f = m.Invoke(tool, null) as MEAI.AIFunction;
                                if (f != null)
                                {
                                    result.Add(f);
                                }
                            }

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
            public string Model => _s.ModelName;
            public float Temperature => _s.Temperature;
            public int RequestTimeoutSeconds => _s.RequestTimeoutSeconds;
            public int MaxTokens => _s.MaxTokens;
            public bool LogLlmInput => _s.LogLlmInput;
            public bool LogLlmOutput => _s.LogLlmOutput;
            public bool EnableHttpDebugLogging => _s.EnableHttpDebugLogging;
        }
    }
}
#endif
