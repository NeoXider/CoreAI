#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using LLMUnity;
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
        private readonly IOpenAiHttpSettings? _settings;
        private string _currentRoleId = "";

        public MeaiLlmClient(MEAI.IChatClient innerClient, IGameLogger logger, IAgentMemoryStore? memoryStore = null)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _memoryStore = memoryStore;
        }

        private MeaiLlmClient(MEAI.IChatClient innerClient, IGameLogger logger, IOpenAiHttpSettings settings, IAgentMemoryStore? memoryStore = null)
            : this(innerClient, logger, memoryStore)
        {
            _settings = settings;
        }

        /// <summary>
        /// Создать HTTP клиент (OpenAI-compatible API).
        /// </summary>
        public static MeaiLlmClient CreateHttp(
            IOpenAiHttpSettings settings,
            IGameLogger logger,
            IAgentMemoryStore? memoryStore = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            var innerClient = new MeaiOpenAiChatClient(settings, logger);
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
            var adapter = new HttpSettingsAdapter(settings);
            return CreateHttp(adapter, logger, memoryStore);
        }

        /// <summary>
        /// Создать LLMUnity клиент (локальная GGUF модель).
        /// </summary>
        public static MeaiLlmClient CreateLlmUnity(
            LLMAgent unityAgent,
            IGameLogger logger,
            IAgentMemoryStore? memoryStore = null)
        {
            if (unityAgent == null) throw new ArgumentNullException(nameof(unityAgent));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            var innerClient = new LlmUnityMeaiChatClient(unityAgent, logger);
            return new MeaiLlmClient(innerClient, logger, memoryStore);
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools) { }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            _currentRoleId = request.AgentRoleId ?? "Unknown";
            var aiTools = BuildAIFunctions(request.Tools, _currentRoleId);
            var functionClient = new MEAI.FunctionInvokingChatClient(_innerClient, NullLoggerFactory.Instance)
            {
                // Ограничиваем число итераций tool calling:
                // - Модель может вызывать несколько РАЗНЫХ tools подряд (memory → inventory → etc)
                // - Но если модель зацикливает ОДИН И ТОТ ЖЕ tool - прерываем
                // - 3 последовательных ошибки = останавливаем
                MaximumIterationsPerRequest = 3
            };

            var chatMessages = new List<MEAI.ChatMessage>
            {
                new(MEAI.ChatRole.System, request.SystemPrompt ?? ""),
                new(MEAI.ChatRole.User, request.UserPayload ?? "")
            };

            var chatOptions = new MEAI.ChatOptions
            {
                Temperature = request.Temperature,
                MaxOutputTokens = request.MaxOutputTokens
            };
            if (aiTools.Count > 0) chatOptions.Tools = aiTools.Cast<MEAI.AITool>().ToList();

            MEAI.ChatResponse response;
            try
            {
                response = await functionClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(GameLogFeature.Llm, $"MeaiLlmClient: {ex.Message}");
                return new LlmCompletionResult { Ok = false, Error = ex.Message };
            }

            // Логируем результат tool calling если включено
            if (_settings?.LogLlmOutput == true)
            {
                _logger.LogInfo(GameLogFeature.Llm, $"MeaiLlmClient: Final response: {response.Text}");
                if (response.Usage != null)
                {
                    _logger.LogInfo(GameLogFeature.Llm, $"MeaiLlmClient: Tokens - Input: {response.Usage.InputTokenCount}, Output: {response.Usage.OutputTokenCount}, Total: {response.Usage.TotalTokenCount}");
                }
            }

            string text = response.Text;
            if (string.IsNullOrEmpty(text))
                return new LlmCompletionResult { Ok = false, Error = "Empty response from LLM" };

            var result = new LlmCompletionResult { Ok = true, Content = text };
            if (response.Usage != null)
            {
                result.PromptTokens = (int)(response.Usage.InputTokenCount ?? 0);
                result.CompletionTokens = (int)(response.Usage.OutputTokenCount ?? 0);
                result.TotalTokens = (int)(response.Usage.TotalTokenCount ?? 0);
            }
            return result;
        }

        private List<MEAI.AIFunction> BuildAIFunctions(IReadOnlyList<ILlmTool>? tools, string roleId)
        {
            var result = new List<MEAI.AIFunction>();
            if (tools == null) return result;

            foreach (var tool in tools)
            {
                try
                {
                    switch (tool)
                    {
                        case MemoryLlmTool:
                            if (_memoryStore != null)
                            {
                                var mt = new MemoryTool(_memoryStore, roleId);
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
                        default:
                            var m = tool.GetType().GetMethod("CreateAIFunction");
                            if (m != null)
                            {
                                var f = m.Invoke(tool, null) as MEAI.AIFunction;
                                if (f != null) result.Add(f);
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
            public HttpSettingsAdapter(CoreAISettingsAsset s) => _s = s;
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
