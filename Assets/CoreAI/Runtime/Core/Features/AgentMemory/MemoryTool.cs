using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
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
            Func<string, string?, CancellationToken, Task<MemoryResult>> func = ExecuteAsync;
            return AIFunctionFactory.Create(
                func,
                "memory",
                "Store, append, or clear persistent memory for agent recall across sessions.");
        }


        public async Task<MemoryResult> ExecuteAsync(
            string action,
            string? content = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(action))
            {
                return new MemoryResult { Success = false, Error = "Action is required" };
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                switch (action)
                {
                    case "write":
                        if (string.IsNullOrEmpty(content))
                        {
                            return new MemoryResult { Success = false, Error = "Content is required for write action" };
                        }

                        _store.Save(_roleId, new AgentMemoryState { Memory = content });
                        return new MemoryResult
                        {
                            Success = true,
                            Message = $"Memory written successfully for role: {_roleId}"
                        };

                    case "append":
                        if (string.IsNullOrEmpty(content))
                        {
                            return new MemoryResult
                                { Success = false, Error = "Content is required for append action" };
                        }

                        string currentState = _store.TryLoad(_roleId, out AgentMemoryState existing)
                            ? existing?.Memory ?? ""
                            : "";

                        string newMemory = string.IsNullOrEmpty(currentState)
                            ? content
                            : currentState + "\n" + content;

                        _store.Save(_roleId, new AgentMemoryState { Memory = newMemory });
                        return new MemoryResult
                        {
                            Success = true,
                            Message = $"Content appended to memory for role: {_roleId}"
                        };

                    case "clear":
                        _store.Clear(_roleId);
                        return new MemoryResult
                        {
                            Success = true,
                            Message = $"Memory cleared for role: {_roleId}"
                        };

                    default:
                        return new MemoryResult
                        {
                            Success = false,
                            Error = $"Unknown action: '{action}'. Valid actions: write, append, clear"
                        };
                }
            }
            catch (Exception ex)
            {
                return new MemoryResult
                {
                    Success = false,
                    Error = $"Memory operation failed: {ex.Message}"
                };
            }
        }


        public sealed class MemoryResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Error { get; set; }
        }
    }
}