#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using LLMUnity;
using MEAI = Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// LLM клиент с Microsoft.Extensions.AI (MEAI) поддержкой tool calling.
    /// </summary>
    public sealed class MeaiLlmUnityClient : ILlmClient
    {
        private readonly LLMAgent _unityAgent;
        private readonly IGameLogger _logger;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly bool _useChatHistory;
        private string _currentRoleId;
        private readonly List<MEAI.AITool> _tools = new();

        public LLMAgent UnityAgent => _unityAgent;
        public LLM LLM => _unityAgent?.llm ?? _unityAgent?.GetComponent<LLM>();

        public MeaiLlmUnityClient(
            LLMAgent unityAgent,
            IGameLogger logger,
            IAgentMemoryStore memoryStore = null,
            AgentMemoryPolicy memoryPolicy = null,
            bool useChatHistory = false)
        {
            _unityAgent = unityAgent ?? throw new ArgumentNullException(nameof(unityAgent));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _memoryStore = memoryStore;
            _useChatHistory = useChatHistory;
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _tools.Clear();
            if (tools == null) return;
            
            foreach (var tool in tools)
            {
                if (tool is AgentMemory.MemoryLlmTool)
                {
                    // MemoryLlmTool is a placeholder - actual MemoryTool will be created in CompleteAsync
                }
            }
        }

        private async Task EnsureClientReady(CancellationToken cancellationToken)
        {
            if (_unityAgent == null)
                throw new InvalidOperationException("LLMAgent is null");

            LLM llm = _unityAgent.llm ?? _unityAgent.GetComponent<LLM>();
            if (llm == null)
                throw new InvalidOperationException("LLMAgent: не назначен компонент LLM");

            Task<bool> setupTask = LLM.WaitUntilModelSetup();
            while (!setupTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (!await setupTask)
                throw new InvalidOperationException("LLMUnity: не удалась подготовка моделей");

            Task readyTask = llm.WaitUntilReady();
            while (!readyTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            await readyTask;
            cancellationToken.ThrowIfCancellationRequested();

            if (llm.failed)
                throw new InvalidOperationException("LLMUnity: сервер LLM не поднялся");
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo(GameLogFeature.Llm, "MeaiLlmUnityClient.CompleteAsync: starting");
                
                await EnsureClientReady(cancellationToken);

                string prevSystem = _unityAgent.systemPrompt;
                try
                {
                    if (!string.IsNullOrEmpty(request.SystemPrompt))
                        _unityAgent.systemPrompt = request.SystemPrompt;

                    _currentRoleId = request.AgentRoleId ?? "Unknown";
                    
                    _logger.LogInfo(GameLogFeature.Llm, $"MeaiLlmUnityClient: tools={request.Tools?.Count ?? 0}, store={_memoryStore != null}");

                    // Create actual MemoryTool if we have tools in request and store is available
                    List<MEAI.AITool> tools = new();
                    if (request.Tools != null && _memoryStore != null)
                    {
                        foreach (var tool in request.Tools)
                        {
                            if (tool is AgentMemory.MemoryLlmTool)
                            {
                                try
                                {
                                    var memoryTool = new MemoryTool(_memoryStore, _currentRoleId);
                                    tools.Add(memoryTool.CreateAIFunction());
                                    _logger.LogInfo(GameLogFeature.Llm, "MeaiLlmUnityClient: Added MemoryTool");
                                }
                                catch (Exception toolEx)
                                {
                                    _logger.LogWarning(GameLogFeature.Llm, "MeaiLlmUnityClient: Tool creation failed: " + toolEx.Message);
                                }
                            }
                        }
                    }

                    var innerClient = new LlmUnityMeaiChatClient(_unityAgent, _logger);
                    var loggerFactory = NullLoggerFactory.Instance;
                    
                    _logger.LogInfo(GameLogFeature.Llm, "MeaiLlmUnityClient: Creating FunctionInvokingChatClient");
                    
                    MEAI.FunctionInvokingChatClient functionClient;
                    try
                    {
                        functionClient = new MEAI.FunctionInvokingChatClient(innerClient, loggerFactory);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(GameLogFeature.Llm, "MeaiLlmUnityClient: FunctionInvokingChatClient creation failed: " + ex.Message + "\n" + ex.StackTrace);
                        throw;
                    }

                    using (functionClient)
                    {
                        var messages = new List<MEAI.ChatMessage>
                        {
                            new(MEAI.ChatRole.User, request.UserPayload ?? "")
                        };

                        var options = new MEAI.ChatOptions
                        {
                            Tools = tools
                        };

                        _logger.LogInfo(GameLogFeature.Llm, "MeaiLlmUnityClient: Calling GetResponseAsync");
                        MEAI.ChatResponse response = await functionClient.GetResponseAsync(messages, options, cancellationToken);
                        _logger.LogInfo(GameLogFeature.Llm, "MeaiLlmUnityClient: GetResponseAsync completed");

                        string content = "";
                        foreach (var msg in response.Messages)
                    {
                        if (msg.Role == MEAI.ChatRole.Assistant)
                        {
                            foreach (var item in msg.Contents)
                            {
                                if (item is MEAI.TextContent tc)
                                    content += tc.Text;
                            }
                        }
                    }

                    if (_useChatHistory && _memoryStore != null && !string.IsNullOrEmpty(content))
                    {
                        _memoryStore.AppendChatMessage(_currentRoleId, "user", request.UserPayload ?? "");
                        _memoryStore.AppendChatMessage(_currentRoleId, "assistant", content);
                    }

                    return new LlmCompletionResult { Ok = true, Content = content ?? "" };
                    } // end using functionClient
                }
                finally
                {
                    _unityAgent.systemPrompt = prevSystem;
                }
            }
            catch (OperationCanceledException)
            {
                return new LlmCompletionResult { Ok = false, Error = "Cancelled" };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Llm, "MeaiLlmUnityClient: " + ex.Message);
                return new LlmCompletionResult { Ok = false, Error = ex.Message };
            }
        }

        private sealed class LlmUnityMeaiChatClient : MEAI.IChatClient
        {
            private readonly LLMAgent _unityAgent;
            private readonly IGameLogger _logger;

            public LlmUnityMeaiChatClient(LLMAgent agent, IGameLogger logger)
            {
                _unityAgent = agent;
                _logger = logger;
            }

            public async Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                var messagesList = new List<MEAI.ChatMessage>(messages);
                string userMessage = "";

                foreach (var msg in messagesList)
                {
                    if (msg.Role == MEAI.ChatRole.User)
                    {
                        foreach (var item in msg.Contents)
                        {
                            if (item is MEAI.TextContent tc)
                                userMessage += tc.Text + "\n";
                        }
                    }
                }

                string result = await _unityAgent.Chat(userMessage.Trim(), addToHistory: false);

                // Qwen3.5-2B не поддерживает структурные tool_calls.
                // Модель возвращает JSON tool call как обычный текст в content.
                // Нужно распознать JSON и создать FunctionCallContent для MEAI FunctionInvokingChatClient.
                var responseContents = new List<MEAI.AIContent>();

                if (TryParseToolCallFromText(result, options?.Tools?.ToList() ?? new List<MEAI.AITool>(), out var toolCallContents, out var cleanedText))
                {
                    // Модель вернула tool call - возвращаем FunctionCallContent
                    responseContents.AddRange(toolCallContents);
                    if (!string.IsNullOrEmpty(cleanedText))
                    {
                        responseContents.Add(new MEAI.TextContent(cleanedText));
                    }
                }
                else
                {
                    // Обычный текст без tool call
                    responseContents.Add(new MEAI.TextContent(result));
                }

                var responseMessage = new MEAI.ChatMessage(MEAI.ChatRole.Assistant, responseContents);
                return new MEAI.ChatResponse(responseMessage)
                {
                    ModelId = options?.ModelId,
                    FinishReason = MEAI.ChatFinishReason.Stop
                };
            }

            /// <summary>
            /// Пытается распознать JSON tool call в текстовом ответе модели.
            /// Qwen3.5-2B возвращает tool call как JSON текст, а не как структурный tool_call.
            /// Поддерживает единый формат: {"name": "tool_name", "arguments": {...}}
            /// </summary>
            private bool TryParseToolCallFromText(
                string text,
                IReadOnlyList<MEAI.AITool> availableTools,
                out List<MEAI.FunctionCallContent> toolCalls,
                out string cleanedText)
            {
                toolCalls = new List<MEAI.FunctionCallContent>();
                cleanedText = text;

                if (string.IsNullOrEmpty(text) || availableTools == null || availableTools.Count == 0)
                {
                    return false;
                }

                // Ищем JSON в формате: {"name": "...", "arguments": {...}}
                // Может быть в ```json блоке или просто JSON в тексте

                var jsonRegex = new Regex(
                    @"```json\s*(\{[^`]+\})\s*```|(\{[^{}]*""name""\s*:\s*""([^""]+)""[^{}]*""arguments""\s*:\s*\{[^{}]*\}[^{}]*\})",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                var match = jsonRegex.Match(text);
                if (!match.Success)
                {
                    return false;
                }

                try
                {
                    string jsonStr = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

                    var json = JObject.Parse(jsonStr);

                    if (json["name"] == null)
                    {
                        return false;
                    }

                    string functionName = json["name"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(functionName))
                    {
                        return false;
                    }

                    var argumentsDict = new Dictionary<string, object?>();
                    if (json["arguments"] != null && json["arguments"].Type != Newtonsoft.Json.Linq.JTokenType.Null)
                    {
                        var argsObj = json["arguments"] as JObject;
                        if (argsObj != null)
                        {
                            foreach (var prop in argsObj.Properties())
                            {
                                argumentsDict[prop.Name] = prop.Value?.Type == Newtonsoft.Json.Linq.JTokenType.String 
                                    ? prop.Value.ToString() 
                                    : prop.Value?.ToObject<object>();
                            }
                        }
                    }

                    var functionCall = new MEAI.FunctionCallContent($"call_{functionName}_1", functionName, argumentsDict);
                    toolCalls.Add(functionCall);

                    // Удаляем JSON блок из текста
                    cleanedText = text.Substring(0, match.Index) + text.Substring(match.Index + match.Length);
                    cleanedText = cleanedText.Trim();

                    _logger.LogInfo(GameLogFeature.Llm, $"MeaiLlmUnityClient: Parsed tool call: {functionName}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(GameLogFeature.Llm, $"MeaiLlmUnityClient: Failed to parse tool call: {ex.Message}");
                }

                return false;
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var response = await GetResponseAsync(messages, options, cancellationToken);
                foreach (var msg in response.Messages)
                {
                    yield return new MEAI.ChatResponseUpdate
                    {
                        Role = msg.Role,
                        Contents = msg.Contents
                    };
                }
            }

            public object? GetService(Type serviceType, object? key) => null;
            public void Dispose() { }
        }
    }

    internal sealed class NullLoggerFactory : ILoggerFactory
    {
        public static readonly NullLoggerFactory Instance = new();
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    internal sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
#endif