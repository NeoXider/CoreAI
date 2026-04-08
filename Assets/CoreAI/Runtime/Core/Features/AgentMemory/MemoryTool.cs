using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// AIFunction-обёртка для работы с памятью агента.
    /// Используется в MEAI function calling pipeline (FunctionInvokingChatClient).
    /// Создаёт AIFunction, который вызывает ExecuteAsync при tool call от модели.
    /// </summary>
    public sealed class MemoryTool
    {
        private readonly IAgentMemoryStore _store;
        private readonly string _roleId;

        public MemoryTool(IAgentMemoryStore store, string roleId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _roleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
        }


        public AIFunction CreateAIFunction()
        {
            // Возвращаем строку (JSON) - MEAI правильно отправит её модели как tool result
            Func<string, string?, CancellationToken, Task<string>> func = ExecuteAsync;
            return AIFunctionFactory.Create(
                func,
                "memory",
                "Store, append, or clear persistent memory for agent recall across sessions.");
        }


        public async Task<string> ExecuteAsync(
            string action,
            string? content = null,
            CancellationToken cancellationToken = default)
        {
            if (CoreAISettings.LogToolCalls)
            {
                Logging.Log.Instance.Info($"[Tool Call] memory: action={action}", LogTag.Memory);
            }
            if (CoreAISettings.LogToolCallArguments && content != null)
            {
                var preview = content.Length > 200 ? content.Substring(0, 200) : content;
                Logging.Log.Instance.Info($"  content: {preview}", LogTag.Memory);
            }
            if (string.IsNullOrEmpty(action))
            {
                return SerializeResult(new MemoryResult { Success = false, Error = "Action is required" });
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                switch (action)
                {
                    case "write":
                        if (string.IsNullOrEmpty(content))
                        {
                            return SerializeResult(new MemoryResult { Success = false, Error = "Content is required for write action" });
                        }

                        // Записываем память (полная замена)
                        _store.Save(_roleId, new AgentMemoryState { Memory = content });
                        
                        if (CoreAISettings.LogToolCallResults)
                        {
                            Logging.Log.Instance.Info($"[Tool Call] memory: SUCCESS - Memory written for {_roleId}", LogTag.Memory);
                        }
                        
                        return SerializeResult(new MemoryResult
                        {
                            Success = true,
                            Message = $"DONE: Memory saved for {_roleId}. Action complete, no further calls needed."
                        });

                    case "append":
                        if (string.IsNullOrEmpty(content))
                        {
                            return SerializeResult(new MemoryResult
                                { Success = false, Error = "Content is required for append action" });
                        }

                        string currentState = _store.TryLoad(_roleId, out AgentMemoryState existing)
                            ? existing?.Memory ?? ""
                            : "";

                        // Идемпотентность: если контент уже существует, не добавляем повторно
                        // Это защищает от зацикливания tool call в FunctionInvokingChatClient
                        if (currentState.Contains(content, StringComparison.OrdinalIgnoreCase))
                        {
                            return SerializeResult(new MemoryResult
                            {
                                Success = true,
                                Message = $"Content already exists in memory for role: {_roleId}"
                            });
                        }

                        string newMemory = string.IsNullOrEmpty(currentState)
                            ? content
                            : currentState + "\n" + content;

                        _store.Save(_roleId, new AgentMemoryState { Memory = newMemory });
                        
                        if (CoreAISettings.LogToolCallResults)
                        {
                            Logging.Log.Instance.Info($"[Tool Call] memory: SUCCESS - Content appended for {_roleId}", LogTag.Memory);
                        }
                        
                        return SerializeResult(new MemoryResult
                        {
                            Success = true,
                            Message = $"DONE: Content appended to memory for {_roleId}. Action complete, no further calls needed."
                        });

                    case "clear":
                        _store.Clear(_roleId);
                        
                        if (CoreAISettings.LogToolCallResults)
                        {
                            Logging.Log.Instance.Info($"[Tool Call] memory: SUCCESS - Memory cleared for {_roleId}", LogTag.Memory);
                        }
                        
                        return SerializeResult(new MemoryResult
                        {
                            Success = true,
                            Message = $"DONE: Memory cleared for {_roleId}. Action complete, no further calls needed."
                        });

                    default:
                        return SerializeResult(new MemoryResult
                        {
                            Success = false,
                            Error = $"Unknown action: '{action}'. Valid actions: write, append, clear"
                        });
                }
            }
            catch (Exception ex)
            {
                if (CoreAISettings.LogToolCallResults)
                {
                    Logging.Log.Instance.Error($"[Tool Call] memory: FAILED - {ex.Message}", LogTag.Memory);
                }
                return SerializeResult(new MemoryResult
                {
                    Success = false,
                    Error = $"Memory operation failed: {ex.Message}"
                });
            }
        }

        private static string SerializeResult(MemoryResult result)
        {
            // Сериализуем в JSON строку - MEAI отправит это модели как tool result
            return JsonSerializer.Serialize(result);
        }


        public sealed class MemoryResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Error { get; set; }
        }
    }
}