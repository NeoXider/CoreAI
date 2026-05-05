using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Игровой чат с LLM: склеивает историю реплик, системный промпт для <see cref="BuiltInAgentRoleIds.SmartChat"/>, вызов <see cref="ILlmClient"/>.
    /// Включает sliding-window rate limiter для защиты от спама.
    /// </summary>
    public sealed class InGameLlmChatService : IInGameLlmChatService
    {
        private readonly ILlmClient _llm;
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly List<(string Role, string Text)> _turns = new();
        private readonly int _maxMessages;
        private readonly object _historyLock = new();
        private readonly object _rateLock = new();

        // Rate limiter
        private readonly int _maxRequestsPerWindow;
        private readonly TimeSpan _rateLimitWindow;
        private readonly Queue<DateTime> _requestTimestamps = new();

        /// <summary>
        /// Создать чат-сервис с опциональным rate limiting.
        /// </summary>
        /// <param name="llm">LLM клиент.</param>
        /// <param name="systemPrompts">Провайдер промптов.</param>
        /// <param name="maxMessages">Максимум сообщений в истории.</param>
        /// <param name="maxRequestsPerWindow">Максимум запросов в окне (0 = без лимита).</param>
        /// <param name="rateLimitWindowSeconds">Размер окна в секундах.</param>
        public InGameLlmChatService(
            ILlmClient llm,
            IAgentSystemPromptProvider systemPrompts,
            int maxMessages = 24,
            int maxRequestsPerWindow = 10,
            int rateLimitWindowSeconds = 60)
        {
            _llm = llm;
            _systemPrompts = systemPrompts;
            _maxMessages = maxMessages;
            _maxRequestsPerWindow = maxRequestsPerWindow;
            _rateLimitWindow = TimeSpan.FromSeconds(rateLimitWindowSeconds);
        }

        /// <inheritdoc />
        public int HistoryPairCount
        {
            get
            {
                lock (_historyLock)
                {
                    return _turns.Count / 2;
                }
            }
        }

        /// <inheritdoc />
        public void ClearHistory()
        {
            lock (_historyLock)
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

            // Rate limit check
            if (!TryAcquireRateSlot())
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = "rate_limited: too many requests. Please wait before sending another message."
                };
            }

            string baseSystem = _systemPrompts.TryGetSystemPrompt(BuiltInAgentRoleIds.SmartChat, out string sys) &&
                                !string.IsNullOrWhiteSpace(sys)
                ? sys.Trim()
                : "You are a helpful in-game assistant.";

            // Match <see cref="AiPromptComposer"/> Layer 1 — this service does not use the composer.
            string prefix = CoreAISettings.UniversalSystemPromptPrefix;
            string system = string.IsNullOrWhiteSpace(prefix)
                ? baseSystem
                : prefix.TrimEnd() + "\n" + baseSystem;

            // BUG-4 fix: snapshot history under lock, release during LLM call,
            // then re-acquire to append the response atomically.
            List<Microsoft.Extensions.AI.ChatMessage> history;
            lock (_historyLock)
            {
                history = new List<Microsoft.Extensions.AI.ChatMessage>(_turns.Count + 1);
                foreach ((string role, string text) in _turns)
                {
                    ChatRole chatRole = role == "User"
                        ? ChatRole.User
                        : ChatRole.Assistant;
                    history.Add(new Microsoft.Extensions.AI.ChatMessage(chatRole, text));
                }

                history.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, message));
            }

            LlmCompletionResult result = await _llm.CompleteAsync(
                new LlmCompletionRequest
                {
                    AgentRoleId = BuiltInAgentRoleIds.SmartChat,
                    SystemPrompt = system,
                    ChatHistory = history,
                    TraceId = Guid.NewGuid().ToString("N")
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Ok && !string.IsNullOrEmpty(result.Content))
            {
                lock (_historyLock)
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

        /// <summary>
        /// Sliding-window rate limiter: отклоняет запрос если превышен лимит.
        /// </summary>
        private bool TryAcquireRateSlot()
        {
            if (_maxRequestsPerWindow <= 0) return true; // Лимит отключён

            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                DateTime cutoff = now - _rateLimitWindow;

                // Удаляем устаревшие timestamps
                while (_requestTimestamps.Count > 0 && _requestTimestamps.Peek() < cutoff)
                {
                    _requestTimestamps.Dequeue();
                }

                if (_requestTimestamps.Count >= _maxRequestsPerWindow)
                {
                    return false;
                }

                _requestTimestamps.Enqueue(now);
                return true;
            }
        }
    }
}