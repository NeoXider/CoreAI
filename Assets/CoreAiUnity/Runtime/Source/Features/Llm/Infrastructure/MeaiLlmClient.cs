#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
            SmartToolCallingChatClient functionClient = new(_innerClient, _logger, _settings, allowDuplicates, request.Tools, _settings.MaxToolCallRetries);

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
                MaxOutputTokens = request.MaxOutputTokens
            };
            if (aiTools.Count > 0)
            {
                chatOptions.Tools = aiTools.Cast<MEAI.AITool>().ToList();
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
        /// Не поддерживает tool calling (стриминг несовместим с multi-step MEAI tools).
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

            MEAI.ChatOptions chatOptions = new()
            {
                Temperature = request.Temperature,
                MaxOutputTokens = request.MaxOutputTokens
            };

            _logger.LogInfo(GameLogFeature.Llm,
                $"MeaiLlmClient: Starting streaming with {chatMessages.Count} messages");

            ThinkBlockStreamFilter thinkFilter = new();
            System.Text.StringBuilder fullResponse = new();
            int chunkCount = 0;

            await foreach (MEAI.ChatResponseUpdate update in _innerClient
                               .GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken))
            {
                string raw = update.Text;
                if (string.IsNullOrEmpty(raw)) continue;

                string visible = thinkFilter.ProcessChunk(raw);
                if (string.IsNullOrEmpty(visible)) continue;

                chunkCount++;
                fullResponse.Append(visible);
                yield return new LlmStreamChunk { Text = visible };
            }

            // Финальный буфер, если модель оборвала ответ с неполным тегом.
            string tail = thinkFilter.Flush();
            if (!string.IsNullOrEmpty(tail))
            {
                fullResponse.Append(tail);
                yield return new LlmStreamChunk { Text = tail };
            }

            // Финальный чанк-маркер конца стрима.
            yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };

            _logger.LogInfo(GameLogFeature.Llm,
                $"MeaiLlmClient: Streaming completed ({chunkCount} chunks, total length={fullResponse.Length})");
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
