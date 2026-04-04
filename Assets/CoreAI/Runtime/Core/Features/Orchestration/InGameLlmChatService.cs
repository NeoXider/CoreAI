using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Игровой чат с LLM: склеивает историю реплик, системный промпт для <see cref="BuiltInAgentRoleIds.PlayerChat"/>, вызов <see cref="ILlmClient"/>.
    /// </summary>
    public sealed class InGameLlmChatService : IInGameLlmChatService
    {
        private readonly ILlmClient _llm;
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly List<(string Role, string Text)> _turns = new();
        private readonly int _maxMessages;
        private readonly object _lock = new();

        public InGameLlmChatService(
            ILlmClient llm,
            IAgentSystemPromptProvider systemPrompts,
            int maxMessages = 24)
        {
            _llm = llm;
            _systemPrompts = systemPrompts;
            _maxMessages = maxMessages;
        }

        /// <inheritdoc />
        public int HistoryPairCount
        {
            get
            {
                lock (_lock)
                {
                    return _turns.Count / 2;
                }
            }
        }

        /// <inheritdoc />
        public void ClearHistory()
        {
            lock (_lock)
            {
                _turns.Clear();
            }
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> SendPlayerMessageAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return new LlmCompletionResult { Ok = false, Error = "empty message" };
            }

            string system = _systemPrompts.TryGetSystemPrompt(BuiltInAgentRoleIds.PlayerChat, out string sys) &&
                            !string.IsNullOrWhiteSpace(sys)
                ? sys.Trim()
                : "You are a helpful in-game assistant.";

            string transcript;
            lock (_lock)
            {
                StringBuilder sb = new();
                foreach ((string role, string text) in _turns)
                {
                    sb.AppendLine($"{role}: {text}");
                }

                sb.AppendLine($"User: {message}");
                transcript = sb.ToString();
            }

            LlmCompletionResult result = await _llm.CompleteAsync(
                new LlmCompletionRequest
                {
                    AgentRoleId = BuiltInAgentRoleIds.PlayerChat,
                    SystemPrompt = system,
                    UserPayload = transcript,
                    TraceId = Guid.NewGuid().ToString("N")
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Ok && !string.IsNullOrEmpty(result.Content))
            {
                lock (_lock)
                {
                    _turns.Add(("User", message.Trim()));
                    _turns.Add(("Assistant", result.Content.Trim()));
                    while (_turns.Count > _maxMessages)
                    {
                        _turns.RemoveAt(0);
                        if (_turns.Count > 0)
                        {
                            _turns.RemoveAt(0);
                        }
                    }
                }
            }

            return result;
        }
    }
}